namespace DLSS_5_MANAGER.ViewModels

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json.Nodes
open System.Threading.Tasks
open Avalonia.Threading
open CommunityToolkit.Mvvm.Input
open DLSS_5_MANAGER.Services

/// Owns only the embedded Screen session. The games installer never uses this state.
type ScreenViewModel() as this =
    inherit ViewModelBase()

    let dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS5Manager", "Screen")
    let mutable child: Process option = None
    let mutable state = JsonObject()
    let mutable ready = false
    let mutable stopping = false
    let mutable preparing = false
    let mutable preparingSince = DateTime.MinValue
    let mutable status = "Preparing Screen…"
    let mutable error = ""
    let mutable updating = false
    let mutable presetName = ""
    let mutable selectedPreset = ""
    let mutable query = ""
    let noMicrophones = [| "None" |]
    let recordingRates = [| "60 FPS"; "30 FPS" |]
    let models = [| "Default"; "Natural"; "Cinematic" |]
    let multipliers = [| "×2"; "×3"; "×4" |]
    let flowMultipliers = [| "×2"; "×3"; "×4"; "×5"; "×6" |]
    let motionBackends = [| "cpu"; "nvofa" |]
    let passOptions = [| "1 · Standard"; "2 · Multi-pass"; "3 · Multi-pass"; "4 · Multi-pass" |]
    let mutable outputFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "DLSS 5 MANAGER")
    let mutable microphone = "None"
    let mutable systemAudio = true
    let mutable recordingFps = 60
    let captureConfig = Path.Combine(dataDir, "capture.json")
    let saveCapture () =
        try
            Directory.CreateDirectory(dataDir) |> ignore
            let cfg = JsonObject()
            cfg["folder"] <- JsonValue.Create(outputFolder)
            cfg["microphone"] <- JsonValue.Create(microphone)
            cfg["system_audio"] <- JsonValue.Create(systemAudio)
            cfg["fps"] <- JsonValue.Create(recordingFps)
            File.WriteAllText(captureConfig, cfg.ToJsonString())
        with _ -> ()
    // ---- shortcuts -------------------------------------------------------
    // Kept in their own file beside capture.json, so a bad value can never
    // take the capture settings down with it.
    let hotkeysConfig = Path.Combine(dataDir, "hotkeys.json")
    let mutable overlayMods = 4u    // Shift
    let mutable overlayKey = 0x4Fu  // O
    let mutable dlssMods = 0u
    let mutable dlssKey = 0x75u     // F6
    let mutable listening = ""
    let hotkeysChanged = Event<unit>()

    let saveHotkeys () =
        try
            Directory.CreateDirectory(dataDir) |> ignore
            let cfg = JsonObject()
            cfg["overlay_mods"] <- JsonValue.Create(overlayMods)
            cfg["overlay_key"] <- JsonValue.Create(overlayKey)
            cfg["dlss_mods"] <- JsonValue.Create(dlssMods)
            cfg["dlss_key"] <- JsonValue.Create(dlssKey)
            File.WriteAllText(hotkeysConfig, cfg.ToJsonString())
        with _ -> ()

    /// "Ctrl+Shift+O", "F6" - the way people write a shortcut down.
    let keyText (mods: uint32) (vk: uint32) =
        let k = int vk

        let name =
            if k >= 0x41 && k <= 0x5A then string (char k)
            elif k >= 0x30 && k <= 0x39 then string (char k)
            elif k >= 0x60 && k <= 0x69 then sprintf "Num %d" (k - 0x60)
            elif k >= 0x70 && k <= 0x87 then sprintf "F%d" (k - 0x6F)
            else sprintf "Key %02X" k

        String.Join(
            "+",
            [ if mods &&& 2u <> 0u then yield "Ctrl"
              if mods &&& 1u <> 0u then yield "Alt"
              if mods &&& 4u <> 0u then yield "Shift"
              if mods &&& 8u <> 0u then yield "Win"
              yield name ]
        )

    let mutable lastSound = DateTime.MinValue
    let sound () =
        if (DateTime.UtcNow-lastSound).TotalMilliseconds >= 90. then
            lastSound <- DateTime.UtcNow
            UiSounds.tick()
    let pendingNumbers = System.Collections.Generic.Dictionary<string, float>()
    let numberTimer = DispatcherTimer(Interval=TimeSpan.FromMilliseconds(220.))
    let matches (words: string) =
        let text = words.ToLowerInvariant()
        query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)
        |> Array.forall text.Contains
    let lists = System.Collections.Generic.Dictionary<string, string array>()

    // The window list with "None" in front, rebuilt only when the list behind
    // it changes - a new array on every read would make the box re-select.
    let mutable lastWindows: string array = [||]
    let mutable windowsWithNone: string array = [||]

    let str (key: string) fallback =
        match state[key] with
        | null -> fallback
        | value -> try value.GetValue<string>() with _ -> fallback
    let number (key: string) fallback =
        match state[key] with
        | null -> fallback
        | value -> try value.GetValue<float>() with _ -> fallback
    let flag (key: string) fallback =
        match state[key] with
        | null -> fallback
        | value -> try value.GetValue<bool>() with _ -> fallback
    let items (key: string) =
        let next =
            match state[key] with
            | :? JsonArray as values -> values |> Seq.map (fun v -> v.GetValue<string>()) |> Seq.toArray
            | _ -> [||]
        match lists.TryGetValue(key) with
        | true, previous when previous = next -> previous
        | _ -> lists[key] <- next; next
    let notify () = this.RaisePropertyChanged("")
    let setError text =
        error <- text
        this.RaisePropertyChanged("Error")
        this.RaisePropertyChanged("HasError")
    let send (message: JsonObject) =
        match child with
        | Some p when not p.HasExited ->
            try p.StandardInput.WriteLine(message.ToJsonString()); p.StandardInput.Flush()
            with ex -> setError ex.Message
        | _ -> ()
    let flushNumbers () =
        numberTimer.Stop()
        let values = pendingNumbers |> Seq.map (fun p -> p.Key,p.Value) |> Seq.toArray
        pendingNumbers.Clear()
        for key,value in values do
            let message = JsonObject()
            message["set"] <- JsonValue.Create(key)
            message["value"] <- JsonValue.Create(value)
            send message
    let action name =
        flushNumbers()
        let message = JsonObject()
        message["action"] <- JsonValue.Create(name: string)
        send message
    /// The three source buttons. Rebuilding the capture takes the engine a
    /// second or two, and nothing on screen used to say so - the button simply
    /// sat there looking ignored, so people pressed it again. The spinner the
    /// rest of the section already uses answers immediately instead.
    let sourceAction name =
        preparing <- true
        preparingSince <- DateTime.UtcNow
        this.RaisePropertyChanged("IsPreparing")
        action name

    /// The bars. A click on a switch deserves its tick; a drag along a bar
    /// produced a stream of them, and the timer that sends the final value
    /// ticked once more after the hand had already let go.
    let sliderKeys =
        Set.ofList [ "passes"; "work_scale"; "intensity"; "local_tone"; "local_structure"; "skin_structure"; "split"; "halo"
                     "sharpness"; "edge_smooth" ]

    let set key (value: JsonNode) =
        if ready && not updating then
            if key = "nr" || key = "nr_small" || key = "window" || key = "passes" || key = "work_scale" || key = "gpu" || key = "monitor" then
                preparing <- true
                preparingSince <- DateTime.UtcNow
                this.RaisePropertyChanged("IsPreparing")
            let message = JsonObject()
            message["set"] <- JsonValue.Create(key: string)
            message["value"] <- value
            send message
            if not (sliderKeys.Contains key) then sound ()
    let setBool (key: string) (value: bool) = if flag key false <> value then set key (JsonValue.Create(value))
    /// Which bound property each engine key actually feeds.
    ///
    /// Dragging a slider used to end in `notify ()`, which raises a change on
    /// *every* property at once. That re-evaluated every binding on the page -
    /// the seven search predicates included, and each of those splits and
    /// lower-cases strings on every pass. At one tick per 0.01 that is a
    /// hundred full-page invalidations per sweep, which is what made the
    /// comparison wipe crawl. Naming the property costs nothing instead.
    let numberProperties =
        dict [ "passes", [| "PassCount"; "PassIndex"; "PassCountLabel" |]
               "work_scale", [| "WorkScale"; "WorkSize" |]
               "intensity", [| "Intensity" |]
               "local_tone", [| "LocalTone" |]
               "local_structure", [| "LocalStructure" |]
               "skin_structure", [| "SkinStructure" |]
               "split", [| "Split" |]
               "halo", [| "HaloReduction"; "HaloLabel" |]
               "sharpness", [| "Sharpness"; "SharpnessLabel" |]
               "edge_smooth", [| "EdgeSmoothing"; "EdgeSmoothingLabel" |]
               "flow_multiplier", [| "FlowMultiplierIndex" |]
               "style", [| "ModelIndex" |]
               "frame_multiplier", [| "MultiplierIndex" |] ]

    /// Falls back to the blanket refresh for a key nobody has mapped, so a new
    /// slider still updates correctly before anyone remembers this table.
    let raiseFor (key: string) =
        match numberProperties.TryGetValue(key) with
        | true, names -> for name in names do this.RaisePropertyChanged(name)
        | _ -> notify ()

    let setNumber (key: string) (value: float) =
        if ready && not updating && (abs(number key Double.NaN - value) > 0.0001 || state[key] = null) then
            state[key] <- JsonValue.Create(value)
            pendingNumbers[key] <- value
            numberTimer.Stop()
            numberTimer.Start()
            if not (sliderKeys.Contains key) then sound ()
            raiseFor key
    let setString (key: string) (value: string) = if not (String.IsNullOrWhiteSpace(value)) && str key "" <> value then set key (JsonValue.Create(value))

    let findNative () =
        let rec search (dir: DirectoryInfo) =
            if isNull dir then failwith "Screen runtime folder was not found."
            let candidate = Path.Combine(dir.FullName, "mod files", "native")
            if File.Exists(Path.Combine(candidate, "screen", "controller.py")) then candidate
            else search dir.Parent
        search (DirectoryInfo(AppContext.BaseDirectory))

    let receive (line: string) =
        try
            let message = JsonNode.Parse(line)
            if message["state"] <> null then
                let next = message["state"].AsObject()
                // Flatten the upstream parameter group only for the bindings.
                match next["params"] with
                | :? JsonObject as parameters ->
                    for pair in parameters do next[pair.Key] <- pair.Value.DeepClone()
                | _ -> ()
                Dispatcher.UIThread.Post(fun () ->
                    // Frame counters are diagnostic data; they must not redraw
                    // every control while the user's settings remain unchanged.
                    next.Remove("frames") |> ignore
                    next.Remove("display_fps") |> ignore
                    for pair in pendingNumbers do next[pair.Key] <- JsonValue.Create(pair.Value)
                    let changed = not ready || not (JsonNode.DeepEquals(state, next))
                    updating <- true
                    try
                        state <- next
                        if preparing && (DateTime.UtcNow-preparingSince).TotalSeconds > 0.8 then
                            preparing <- false
                            this.RaisePropertyChanged("IsPreparing")
                        let selected = if flag "preset_active" false then str "profile" "" else ""
                        if selected <> selectedPreset then
                            selectedPreset <- selected
                            if selected <> "" then presetName <- selected
                        ready <- not stopping
                        status <-
                            if stopping then "Stopping Screen…"
                            elif flag "worker_failed" false then "Processing stopped — the worker failed."
                            elif state["gpu_ok"] <> null && not (flag "gpu_ok" true) then "The selected GPU could not start neural processing."
                            elif not (flag "nr" true) then "Ready"
                            elif flag "idle" false then "Ready — waiting for the screen to change"
                            else "Processing"
                        error <- str "error" ""
                        if changed then notify ()
                    finally updating <- false)
            elif message["error"] <> null then
                let text = message["error"].GetValue<string>()
                Dispatcher.UIThread.Post(fun () -> setError text)
        with _ -> () // Only our JSON status records belong to the control channel.

    do
        try
            if File.Exists(captureConfig) then
                let cfg = JsonNode.Parse(File.ReadAllText(captureConfig))
                outputFolder <- cfg["folder"].GetValue<string>()
                microphone <- cfg["microphone"].GetValue<string>()
                systemAudio <- cfg["system_audio"].GetValue<bool>()
                recordingFps <- cfg["fps"].GetValue<int>()
        with _ -> ()
        try
            if File.Exists(hotkeysConfig) then
                let cfg = JsonNode.Parse(File.ReadAllText(hotkeysConfig))
                overlayMods <- cfg["overlay_mods"].GetValue<uint32>()
                overlayKey <- cfg["overlay_key"].GetValue<uint32>()
                dlssMods <- cfg["dlss_mods"].GetValue<uint32>()
                dlssKey <- cfg["dlss_key"].GetValue<uint32>()
        with _ -> ()
        numberTimer.Tick.Add(fun _ ->
            numberTimer.Stop()
            let values = pendingNumbers |> Seq.map (fun p -> p.Key,p.Value) |> Seq.toArray
            pendingNumbers.Clear()
            for key,value in values do set key (JsonValue.Create(value)))

    /// The translations, read from the one set the whole app shares.
    ///
    /// This section has its own view-model, and the overlay puts it inside a
    /// window of its own - so neither can reach MainViewModel, which is where
    /// the language actually lives.
    member _.Loc = Localization.current

    /// The language changed underneath us. MainViewModel calls this because it
    /// is the only thing that knows.
    member this.RaiseLoc() = this.RaisePropertyChanged("Loc")

    // ---- shortcuts -------------------------------------------------------
    member _.OverlayHotkey = (overlayMods, overlayKey)
    member _.DlssHotkey = (dlssMods, dlssKey)
    member _.OverlayKeyText = keyText overlayMods overlayKey
    member _.DlssKeyText = keyText dlssMods dlssKey
    /// Whether there is a session to leave. The section starts one as soon as
    /// it is opened; until now the only way out was to leave the page, which
    /// is not something a person guesses (user: give me a way out of the
    /// ready state from the top).
    member _.CanStop = child.IsSome && not stopping
    member _.OverlayButtonText = "Overlay · " + keyText overlayMods overlayKey
    member _.DlssButtonText = "DLSS 5 · " + keyText dlssMods dlssKey

    /// Which shortcut's key cap is waiting for keys, "" when none is.
    member _.Listening = listening

    member _.OverlayCapText =
        if listening = "overlay" then Localization.current.LfShortcutListening
        else keyText overlayMods overlayKey

    member _.DlssCapText =
        if listening = "dlss" then Localization.current.LfShortcutListening
        else keyText dlssMods dlssKey

    [<CLIEvent>]
    member _.HotkeysChanged = hotkeysChanged.Publish

    member private this.RaiseShortcuts() =
        for name in [ "OverlayKeyText"; "DlssKeyText"; "OverlayButtonText"; "DlssButtonText"; "OverlayCapText"; "DlssCapText"; "Listening" ] do
            this.RaisePropertyChanged(name)

    /// A second click on the same cap puts it back rather than leaving it
    /// waiting for keys nobody means to press.
    member this.StartListening(target: string) =
        listening <- (if listening = target then "" else target)
        this.RaiseShortcuts()

    member this.StopListening() =
        if listening <> "" then
            listening <- ""
            this.RaiseShortcuts()

    member this.SetHotkey(target: string, mods: uint32, vk: uint32) =
        let clash =
            if target = "overlay" then mods = dlssMods && vk = dlssKey
            else mods = overlayMods && vk = overlayKey

        // A bare letter or digit registered system-wide would stop typing that
        // character in every other program. Function keys are fine alone.
        let isFunctionKey = vk >= 0x70u && vk <= 0x87u

        if clash then
            setError "That combination is already the other shortcut."
        elif mods = 0u && not isFunctionKey then
            setError "Add Ctrl, Alt or Shift - a single letter as a global shortcut would stop working in every other program."
        else
            if target = "overlay" then
                overlayMods <- mods
                overlayKey <- vk
            else
                dlssMods <- mods
                dlssKey <- vk

            setError ""
            saveHotkeys ()
            this.RaiseShortcuts()
            hotkeysChanged.Trigger()

    /// The DLSS 5 shortcut: on if it was off, off if it was on.
    member this.ToggleNr() = this.Nr <- not this.Nr

    member _.IsRunning = child.IsSome
    member _.IsPreparing = child.IsSome && not stopping && (preparing || not ready || (flag "nr" false && state["gpu_ok"] = null))
    member _.PassCount with get () = float(this.PassIndex + 1) and set (v: float) = setNumber "passes" (Math.Clamp(Math.Round(v), 1., 4.))
    /// Every slider in the card carries its own value in its own label, and
    /// nothing sits between two columns to be read as the wrong slider's
    /// number (user: "there is a 1x in front of halo reduction"). It also
    /// leaves all four rows the same shape - label, bar - so the two columns
    /// line up exactly, two by two.
    member this.PassCountLabel = sprintf "%s  %g×" Localization.current.LfMultiPass this.PassCount
    member this.HaloLabel = sprintf "%s  %.0f%%" Localization.current.LfHaloReduction (this.HaloReduction * 100.)
    member this.SharpnessLabel = sprintf "%s  %.0f%%" Localization.current.LfSharpness (this.Sharpness * 100.)
    member this.EdgeSmoothingLabel = sprintf "%s  %.0f%%" Localization.current.LfEdgeSmoothing (this.EdgeSmoothing * 100.)
    member _.IsRecording = flag "recording" false
    member _.CanConfigureCapture = not (flag "recording" false)
    member _.CaptureMessage = str "capture_message" ""
    member _.RecordingTime = sprintf "%02.0f:%02.0f" (floor(number "recording_seconds" 0. / 60.)) (number "recording_seconds" 0. % 60.)
    member _.Microphones = let values = items "microphones" in if values.Length = 0 then noMicrophones else values
    member _.Microphone
        with get () = microphone
        and set v =
            if not updating && not (isNull v) && v <> microphone then
                microphone <- v
                saveCapture()
                this.RaisePropertyChanged("Microphone")
    member _.SystemAudio
        with get () = systemAudio
        and set v =
            if not updating && v <> systemAudio then
                systemAudio <- v
                saveCapture()
                this.RaisePropertyChanged("SystemAudio")
    member _.RecordingRates = recordingRates
    member _.RecordingRateIndex
        with get () = if recordingFps = 60 then 0 else 1
        and set v =
            let fps = if v = 1 then 30 else 60
            if not updating && v >= 0 && v <= 1 && recordingFps <> fps then
                recordingFps <- fps
                saveCapture()
                this.RaisePropertyChanged("RecordingRateIndex")
    member _.OutputFolder
        with get () = outputFolder
        and set v =
            if not updating && not (String.IsNullOrWhiteSpace(v)) && outputFolder <> v then
                outputFolder <- v
                saveCapture()
                this.RaisePropertyChanged("OutputFolder")
    member _.ReportError(text: string) = setError text
    member _.Capture(name: string) =
        flushNumbers()
        let message = JsonObject()
        message["action"] <- JsonValue.Create(name)
        message["folder"] <- JsonValue.Create(outputFolder)
        message["microphone"] <- JsonValue.Create(microphone)
        message["system_audio"] <- JsonValue.Create(systemAudio)
        message["fps"] <- JsonValue.Create(recordingFps)
        send message
    member _.ScreenshotCommand = RelayCommand(fun () -> this.Capture("screenshot"))
    member _.RecordCommand = RelayCommand(fun () -> this.Capture(if this.IsRecording then "stop_recording" else "start_recording"))
    member _.RecordLabel = if this.IsRecording then "Stop recording" else "Record"
    member _.RefreshSourcesCommand = RelayCommand(fun () -> sourceAction "refresh_sources")
    member _.RetrySourceCommand = RelayCommand(fun () -> sourceAction "retry_source")
    member _.FocusSourceCommand = RelayCommand(fun () -> try ScreenDesktop.focus this.Window with ex -> setError ex.Message)
    member _.FastResolutionCommand = RelayCommand(fun () -> this.Boost <- true; this.WorkScale <- min this.ScaleCap 0.5)
    member _.BalancedResolutionCommand = RelayCommand(fun () -> this.Boost <- true; this.WorkScale <- min this.ScaleCap 0.65)
    member _.QualityResolutionCommand = RelayCommand(fun () -> this.Boost <- true; this.WorkScale <- min this.ScaleCap 0.85)
    member _.ApplyQuery(value: string) =
        query <- if isNull value then "" else value.Trim()
        notify ()
    member _.ShowProcessing = matches "processing dlss 5 nr dlss 4.5 fg frame generation multiplier boost network resolution fps multipass multi-pass passes halo outline flow mfg sharpness sharpen edge smoothing anti-aliasing معالجة دقة توليد اطارات تمريرات ميلتي باس هالة حدة تنعيم حواف مضاعفة"
    member _.ShowSource = matches "source window fullscreen clear selection application go app refresh reconnect نافذة شاشة مصدر إلغاء اختيار تحديث اتصال"
    member _.ShowEffect = matches "effect profile model default natural cinematic intensity local tone local structure skin structure تأثير بروفايل موديل تون بشرة قوة"
    member _.ShowComparison = matches "comparison before after wipe split مقارنة قبل بعد خط"
    member _.ShowSettings = matches "settings processing gpu display motion estimation cpu nvofa skip static frames hdr compatibility إعدادات كرت شاشة حركة"
    member _.ShowCapture = matches "capture screenshot printscreen windows win shift s obs recording spout2 plugin microphone audio output folder fps rate تصوير لقطة تسجيل صوت مايك مجلد حفظ"
    member _.ShowPresets = matches ("presets save new update rename delete apply saved name بريست حفظ اسم تعديل حذف " + String.Join(" ", items "saved_presets"))
    member _.NoSearchResults = not (this.ShowProcessing || this.ShowSource || this.ShowEffect || this.ShowComparison || this.ShowSettings || this.ShowCapture || this.ShowPresets)
    member _.IsReady = ready && not stopping
    member _.CanStart = child.IsNone
    member _.Status = status
    member _.Error = error
    member _.HasError = not (String.IsNullOrWhiteSpace(error))
    member _.HasFgInfo = state["fg_error"] <> null
    member _.GpuText = str "gpu_text" ""
    member _.WorkSize = str "work_size" "—"
    member _.ScreenSize = str "screen_size" "—"
    member _.ScaleCap = number "work_scale_cap" 1.0
    member _.IsWindowMode = flag "window_mode" false
    member _.CanDeletePreset = flag "preset_active" false
    member _.Profiles = items "builtin_profiles"
    member _.SavedPresets = items "saved_presets"
    member _.PresetName
        with get () = presetName
        and set value =
            if presetName <> value then
                presetName <- value
                this.RaisePropertyChanged("PresetName")
    member _.SelectedPreset
        with get () = if selectedPreset = "" then null else selectedPreset
        and set value = if not (String.IsNullOrWhiteSpace(value)) then setString "profile" value
    member _.Gpus = items "gpus"
    member _.Monitors = items "monitors"
    /// "None" first: follow no window, capture the whole screen. That used to
    /// be a Clear button beside the list, which read as "empty this list"
    /// rather than as a choice of what to capture.
    member _.Windows =
        let current = items "windows"

        if windowsWithNone.Length = 0 || not (obj.ReferenceEquals(current, lastWindows)) then
            lastWindows <- current
            windowsWithNone <- Array.append [| Localization.current.LfNone |] current

        windowsWithNone
    member _.Models = models
    member _.Multipliers = multipliers
    member _.MotionBackends = motionBackends
    member _.PassOptions = passOptions
    member _.PassIndex with get () = int (number "passes" 1.0) - 1 and set v = if v >= 0 && v <= 3 then setNumber "passes" (float (v + 1))
    member _.FrameInfo = sprintf "Frames: %.0f   •   Output: %s" (number "frames" 0.0) (str "screen_size" "—")
    member this.HasStepDown = this.StepDownInfo <> ""

    /// What is really being multiplied, when it is not what was picked.
    ///
    /// The runtime steps down when it refuses a multiplier - x4 asked for,
    /// x2 given - and the worker says so in its log. Showing the pick and
    /// nothing else describes something that is not happening; this line
    /// appears only when the two differ.
    member _.StepDownInfo =
        let running = int (number "running_multiplier" 0.0)
        let wanted =
            if flag "flow_mfg" false then int (number "flow_multiplier" 2.0)
            elif flag "frame_generation" false then int (number "frame_multiplier" 2.0)
            else 0
        if running > 0 && wanted > 0 && running <> wanted then
            sprintf "Running ×%d — ×%d was refused by this GPU." running wanted
        else ""

    member _.FgInfo =
        match state["fg_error"] with
        | null -> ""
        | v -> if v.GetValue<bool>() then "Frame generation active" else "Frame generation could not start on this GPU."

    member _.Nr with get () = ready && flag "nr" false and set v = setBool "nr" v
    member _.Boost with get () = flag "nr_small" true and set v = setBool "nr_small" v
    member _.FrameGeneration with get () = ready && flag "frame_generation" false and set v = setBool "frame_generation" v
    member _.Hdr with get () = flag "hdr" false and set v = setBool "hdr" v
    member _.SkipStatic with get () = flag "skip_static" false and set v = setBool "skip_static" v
    member _.ExternalOutput with get () = flag "spout" false and set v = setBool "spout" v
    member _.WorkScale with get () = number "work_scale" 0.65 and set v = setNumber "work_scale" v
    /// How much of the network's edge rim is taken out, 0..1 - the worker's
    /// halo_reduction.inl. Half unless the user moved it.
    member _.HaloReduction with get () = number "halo" 0.5 and set v = setNumber "halo" (Math.Clamp(v, 0.0, 1.0))
    /// Contrast-adaptive sharpening of the finished picture, 0..1 (0 = none).
    member _.Sharpness with get () = number "sharpness" 0.0 and set v = setNumber "sharpness" (Math.Clamp(v, 0.0, 1.0))
    /// Smoothing along edges - never across them - against stair-steps, 0..1.
    member _.EdgeSmoothing with get () = number "edge_smooth" 0.0 and set v = setNumber "edge_smooth" (Math.Clamp(v, 0.0, 1.0))
    /// FLOW MFG: LIVE FLOW's own frame multiplication. On turns DLSS 4.5 FG off
    /// (the engine does that, and the state that comes back shows it).
    member _.FlowMfg with get () = ready && flag "flow_mfg" false and set v = setBool "flow_mfg" v
    member _.FlowMultipliers = flowMultipliers
    member _.FlowMultiplierIndex
        with get () = int (number "flow_multiplier" 2.0) - 2
        and set v = if v >= 0 && v <= 4 then setNumber "flow_multiplier" (float (v + 2))
    member _.Intensity with get () = number "intensity" 1.0 and set v = setNumber "intensity" v
    member _.LocalTone with get () = number "local_tone" 0.5 and set v = setNumber "local_tone" v
    member _.LocalStructure with get () = number "local_structure" 1.0 and set v = setNumber "local_structure" v
    member _.SkinStructure with get () = number "skin_structure" -1.0 and set v = setNumber "skin_structure" v
    member _.Split with get () = number "split" 0.0 and set v = setNumber "split" v
    member _.ModelIndex with get () = int (number "style" 1.0) and set v = if v >= 0 then setNumber "style" (float v)
    member _.MultiplierIndex with get () = int (number "frame_multiplier" 2.0) - 2 and set v = if v >= 0 then setNumber "frame_multiplier" (float (v + 2))
    member _.Profile with get () = str "profile" "Natural" and set v = setString "profile" v
    member _.Gpu with get () = str "gpu" "" and set v = setString "gpu" v
    member _.Monitor with get () = str "monitor" "" and set v = setString "monitor" v
    member _.Window
        with get () = if flag "window_mode" false then str "window_current" "" else Localization.current.LfNone
        and set (v: string) =
            if not (String.IsNullOrWhiteSpace(v)) then
                if v = Localization.current.LfNone then
                    if flag "window_mode" false then sourceAction "window_mode"
                elif str "window_current" "" <> v then
                    set "window" (JsonValue.Create(v))
    member _.MotionBackend with get () = str "motion_backend" "cpu" and set v = setString "motion_backend" v
    member _.Start() =
        if child.IsNone then
            try
                let native = findNative ()
                let python = Path.Combine(native, "runtime", "python.exe")
                let model = Path.GetFullPath(Path.Combine(native, "..", "dlss 5", "nvngx_dlssnr.dll"))
                for path in [python; model; Path.Combine(native, "nvngx.dll"); Path.Combine(native, "nvngx_dlssg.dll")] do
                    if not (File.Exists(path)) then failwith ("Screen requires: " + path)
                Directory.CreateDirectory(dataDir) |> ignore
                let config = Path.Combine(dataDir, "config.json")
                if not (File.Exists(config)) then
                    File.WriteAllText(config, "{\"monitor\":0,\"width\":1920,\"height\":1080,\"fullscreen\":true,\"warmup\":120,\"work_scale\":0.65,\"profile\":\"Natural\",\"intensity\":1.0,\"local_tone\":0.5,\"local_structure\":1.0,\"skin_structure\":-1.0,\"nr_small\":true,\"worker_present\":true,\"motion_on_gpu\":true,\"capture_in_worker\":true,\"open_menu_on_start\":false,\"theme\":\"dark\",\"lang\":\"en\"}")
                let info = ProcessStartInfo(python, UseShellExecute=false, CreateNoWindow=true, WorkingDirectory=dataDir, RedirectStandardInput=true, RedirectStandardOutput=true, RedirectStandardError=true)
                info.StandardInputEncoding <- UTF8Encoding(false)
                info.StandardOutputEncoding <- Encoding.UTF8
                info.ArgumentList.Add(Path.Combine(native, "screen", "controller.py"))
                info.ArgumentList.Add("--config")
                info.ArgumentList.Add(config)
                info.Environment["DLSS_MANAGER_SCREEN_DATA"] <- dataDir
                info.Environment["DLSS_MANAGER_EXE"] <- Environment.ProcessPath
                info.Environment["DLSS_MANAGER_PID"] <- string Environment.ProcessId
                let p = new Process(StartInfo=info, EnableRaisingEvents=true)
                p.OutputDataReceived.Add(fun e -> if not (isNull e.Data) then receive e.Data)
                p.ErrorDataReceived.Add(fun e -> if not (String.IsNullOrWhiteSpace(e.Data)) then Dispatcher.UIThread.Post(fun () -> setError e.Data))
                p.Exited.Add(fun _ ->
                    Dispatcher.UIThread.Post(fun () ->
                        if child |> Option.exists (fun current -> obj.ReferenceEquals(current, p)) then
                            let code = p.ExitCode
                            child <- None
                            ready <- false
                            stopping <- false
                            status <- if code = 0 then "Screen stopped — resources unloaded." else "Screen could not continue. Reopen this section to retry."
                            notify ()))
                error <- ""
                status <- "Starting Screen…"
                p.Start() |> ignore
                child <- Some p
                p.BeginOutputReadLine()
                p.BeginErrorReadLine()
                notify ()
            with ex ->
                child <- None
                status <- "Screen could not start."
                setError ex.Message
                notify ()

    member _.StopAsync() : Task =
        numberTimer.Stop()
        pendingNumbers.Clear()
        task {
            match child with
            | None -> ()
            | Some p when stopping -> do! p.WaitForExitAsync()
            | Some p ->
                stopping <- true
                ready <- false
                status <- "Closing Screen…"
                notify ()
                action "quit"
                try
                    p.StandardInput.Close()
                    let! completed = Task.WhenAny(p.WaitForExitAsync(), Task.Delay(20000))
                    if not p.HasExited then
                        p.Kill(true)
                        do! p.WaitForExitAsync()
                        setError "Screen did not respond to shutdown; its process tree was terminated."
                with ex -> setError ex.Message
        }

    /// Leave the ready state: the same shutdown the page does when it closes,
    /// asked for deliberately. The section can be started again from here.
    member this.StopCommand = RelayCommand(fun () -> this.StopAsync() |> ignore)

    /// Back from the ready state. Without it the only way back in was to leave
    /// the section and come back, which nobody guesses (user: after Exit there
    /// is no button to turn it on again).
    member this.StartCommand = RelayCommand(fun () -> this.Start())
    member _.FullscreenCommand = RelayCommand(fun () -> sourceAction "window_mode")
    member _.OpenObsPluginCommand = RelayCommand(fun () ->
        try Process.Start(ProcessStartInfo("https://github.com/Off-World-Live/obs-spout2-plugin/releases", UseShellExecute=true)) |> ignore
        with ex -> setError ex.Message)
    member private _.SaveNamedPreset(update: bool) =
        flushNumbers()
        let message = JsonObject()
        message["action"] <- JsonValue.Create(if update then "update_preset" else "create_preset")
        message["name"] <- JsonValue.Create(presetName)
        message["previous"] <- JsonValue.Create(selectedPreset)
        send message
    member _.SavePresetCommand = RelayCommand(fun () -> this.SaveNamedPreset(false))
    member _.UpdatePresetCommand = RelayCommand(fun () -> this.SaveNamedPreset(true))
    member _.ApplyPresetCommand = RelayCommand(fun () -> if selectedPreset <> "" then set "profile" (JsonValue.Create(selectedPreset)))
    member _.DeletePresetCommand = RelayCommand(fun () -> action "delete_preset")

    member this.NrOffCommand = RelayCommand(fun () -> this.Nr <- false)
    member this.NrOnCommand = RelayCommand(fun () -> this.Nr <- true)

    member this.FrameGenerationOffCommand = RelayCommand(fun () -> this.FrameGeneration <- false)
    member this.FrameGenerationOnCommand = RelayCommand(fun () -> this.FrameGeneration <- true)
    member this.FlowMfgOffCommand = RelayCommand(fun () -> this.FlowMfg <- false)
    member this.FlowMfgOnCommand = RelayCommand(fun () -> this.FlowMfg <- true)

    member this.BoostOffCommand = RelayCommand(fun () -> this.Boost <- false)
    member this.BoostOnCommand = RelayCommand(fun () -> this.Boost <- true)

    member this.SkipStaticOffCommand = RelayCommand(fun () -> this.SkipStatic <- false)
    member this.SkipStaticOnCommand = RelayCommand(fun () -> this.SkipStatic <- true)

    member this.HdrOffCommand = RelayCommand(fun () -> this.Hdr <- false)
    member this.HdrOnCommand = RelayCommand(fun () -> this.Hdr <- true)

    member this.ExternalOutputOffCommand = RelayCommand(fun () -> this.ExternalOutput <- false)
    member this.ExternalOutputOnCommand = RelayCommand(fun () -> this.ExternalOutput <- true)
