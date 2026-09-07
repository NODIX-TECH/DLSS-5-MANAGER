namespace DLSS_5_MANAGER.ViewModels

open System
open System.Collections.ObjectModel
open Avalonia
open Avalonia.Media
open Avalonia.Threading
open DLSS_5_MANAGER.Models
open DLSS_5_MANAGER.Services

/// One row in the payload list: a bundled file the user can swap for their
/// own, and (except OptiScaler, which has no alternatives worth switching off)
/// leave out of an install entirely.
type PayloadRowViewModel(key: string, describe: unit -> string, canDisable: bool) =
    inherit ViewModelBase()

    let mutable state = describe ()
    let mutable enabled = not canDisable || ExtrasStore.isPayloadEnabled key

    member _.Key = key
    member _.Name = key
    member _.CanDisable = canDisable
    member _.State = state

    member this.Refresh() =
        state <- describe ()
        enabled <- not canDisable || ExtrasStore.isPayloadEnabled key
        this.RaisePropertyChanged("State")
        this.RaisePropertyChanged("IsEnabled")

    /// Off means the installer walks past it - not that anything is wrong.
    member this.IsEnabled
        with get () = enabled
        and set value =
            if canDisable && enabled <> value then
                enabled <- value
                ExtrasStore.setPayloadEnabled key value
                this.RaisePropertyChanged("IsEnabled")

/// One batch of user-added files or folders - everything picked in a single
/// go - copied next to the game on install.
///
/// The whole batch shares one switch and one route selection, so adding a
/// folder's worth of files produces one entry to reason about rather than
/// twenty identical ones.
type ExtraRowViewModel(key: string, items: ExtrasStore.ExtraItem list) =
    inherit ViewModelBase()

    let first = List.head items
    let fileNames = items |> List.map (fun i -> i.Name)

    let mutable enabled = first.Enabled

    let mutable modes =
        if isNull (box first.Modes) then [||] else first.Modes

    let has (k: string) =
        modes |> Array.exists (fun m -> String.Equals(m, k, StringComparison.OrdinalIgnoreCase))

    /// A route that already deploys one of these filenames cannot also take
    /// the extra - they would be fighting over the same slot.
    let allowed (routeKey: string) =
        not (ModInstaller.conflictsWithRoute fileNames routeKey)

    member _.Key = key

    member _.Title =
        match items with
        | [ single ] -> single.Name
        | _ -> sprintf "%d items" items.Length

    /// The folder they came from, which is what tells them apart at a glance.
    member _.SourceLabel =
        try
            let dir = System.IO.Path.GetDirectoryName(first.SourcePath.TrimEnd('\\', '/'))
            if String.IsNullOrWhiteSpace(dir) then first.SourcePath else dir
        with _ ->
            first.SourcePath

    member _.FilesLabel =
        match items with
        | [ single ] -> (if single.IsFolder then "Folder" else "File")
        | _ -> String.Join(", ", fileNames)

    member _.HasManyFiles = items.Length > 1

    member this.IsEnabled
        with get () = enabled
        and set value =
            if enabled <> value then
                enabled <- value
                ExtrasStore.setGroupEnabled key value
                this.RaisePropertyChanged("IsEnabled")

    // ---- Which routes may carry it ---------------------------------------
    member _.IsOptiDx12Allowed = allowed "optiscaler"
    member _.IsOptiVulkanAllowed = allowed "optiscaler"
    member _.IsDx12Allowed = allowed "dx12"
    member _.IsDx11Allowed = allowed "dx11"
    member _.IsDx9Allowed = allowed "dx9"
    member _.IsAmdAllowed = allowed "amd"
    member _.IsEmulatorAllowed = allowed "emulator"

    /// Named when at least one route had to be ruled out, so the greyed-out
    /// chips are never a mystery.
    member this.ConflictText =
        let blocked =
            [ "OptiScaler", allowed "optiscaler"
              "DX12", allowed "dx12"
              "DX11", allowed "dx11"
              "DX9", allowed "dx9"
              "AMD", allowed "amd"
              "Emulator", allowed "emulator" ]
            |> List.filter (snd >> not)
            |> List.map fst

        if blocked.IsEmpty then
            ""
        else
            "Not available for " + String.Join(", ", blocked) + " - those install a file of the same name."

    member this.HasConflict = this.ConflictText <> ""

    // ---- Route selection -------------------------------------------------
    // The routes that come in variants are listed as those variants, using the
    // same short names the cards wear, rather than hiding them behind a second
    // level of controls.
    member _.IsAllModes = modes.Length = 0
    member _.IsOptiDx12Mode = has "optiscaler-dx12"
    member _.IsOptiVulkanMode = has "optiscaler-vulkan"
    member _.IsDx12Mode = has "dx12"
    member _.IsDx1164Mode = has "dx11-64"
    member _.IsDx1132Mode = has "dx11-32"
    member _.IsDx964Mode = has "dx9-64"
    member _.IsDx932Mode = has "dx9-32"
    member _.IsAmdMode = has "amd"
    member _.IsEmulatorMode = has "emulator"

    /// A short line under the name saying where this ends up.
    member this.ModesText =
        if modes.Length = 0 then
            "Every install mode"
        else
            "Only: "
            + String.Join(
                ", ",
                modes
                |> Array.map (fun m ->
                    match m with
                    | "optiscaler"
                    | "optiscaler-dx12" -> "OptiScaler"
                    | "optiscaler-vulkan" -> "OptiScaler Vulkan"
                    | "dx12" -> "DX12"
                    | "dx11" -> "DX11"
                    | "dx11-64" -> "DX11 64-bit"
                    | "dx11-32" -> "DX11 32-bit"
                    | "dx9" -> "DX9"
                    | "dx9-64" -> "DX9 64-bit"
                    | "dx9-32" -> "DX9 32-bit"
                    | "amd" -> "AMD"
                    | "emulator" -> "Emulator"
                    | other -> other)
            )

    member this.ToggleMode(modeKey: string) =
        if modeKey = "all" then
            ExtrasStore.clearGroupModes key
        else
            ExtrasStore.toggleGroupMode key modeKey

        modes <-
            ExtrasStore.list ()
            |> List.tryFind (fun e -> String.Equals(ExtrasStore.groupKey e, key, StringComparison.OrdinalIgnoreCase))
            |> Option.map (fun e -> if isNull (box e.Modes) then [||] else e.Modes)
            |> Option.defaultValue [||]

        for name in
            [ "IsAllModes"; "IsOptiDx12Mode"; "IsOptiVulkanMode"; "IsDx12Mode"
              "IsDx1164Mode"; "IsDx1132Mode"; "IsDx964Mode"; "IsDx932Mode"
              "IsAmdMode"; "IsEmulatorMode"; "ModesText" ] do
            this.RaisePropertyChanged(name)

/// One entry in the colour atmosphere picker.
///
/// `Key` is the identity - the English name that gets saved and matched - and
/// `Display` is only what the user reads. Changing language rewrites `Display`
/// on the existing objects instead of handing the picker a new list, because
/// swapping the list drops its selection and leaves the box blank.
type AtmosphereOption(key: string) =
    inherit ViewModelBase()

    let mutable display = key

    member _.Key = key

    member this.Display
        with get () = display
        and set value = this.SetProperty(&display, value) |> ignore

    override _.ToString() = display

type MainViewModel() as this =
    inherit ViewModelBase()

    let allGames = ObservableCollection<GameCardViewModel>()
    let filteredGames = ObservableCollection<GameCardViewModel>()

    /// Emulators are a hand-curated list, kept apart from the scanned library.
    let allEmulators = ObservableCollection<GameCardViewModel>()
    let filteredEmulators = ObservableCollection<GameCardViewModel>()

    /// "games" | "emulators" | "settings" - the page on screen.
    let mutable activeSection = "games"

    /// True while the Manage sheet is showing an emulator, which installs by a
    /// single fixed route and needs none of the game-side choices.
    let mutable isEmulatorTarget = false

    let mutable searchText = ""
    let mutable isSearchOpen = false
    let mutable isScanning = false
    let mutable scanStatusText = "Ready"
    let mutable isDraggingCard = false
    let mutable draggedCard: GameCardViewModel option = None
    let mutable isSidebarLayout = false
    let mutable isSettingsOpen = false
    let mutable totalGamesCount = 0

    // ---- Manage sheet state ---------------------------------------------
    let mutable isManageOpen = false
    let mutable manageCard: GameCardViewModel option = None
    let mutable manageAnalysis: AnalysisStore.GameAnalysis option = None
    let mutable manageTitle = ""
    let mutable manageExePath = ""
    let mutable manageFolder = ""
    let mutable manageReShadeText = "Checking..."
    let mutable manageDlssText = "Checking..."
    let mutable manageStreamlineText = "Checking..."
    let mutable isAnalyzing = false
    let mutable isInstalling = false
    let mutable isModInstalled = false
    let mutable dlss5Present = false
    let mutable dlss5Complete = false
    let mutable dlss5Missing: string[] = [||]
    let mutable installProgress = 0.0
    let mutable installStatusText = ""
    let mutable installResultText = ""
    let mutable installResultIsError = false

    /// Install route. DX12 + OptiScaler is the recommended default; the other
    /// two are the ReShade routes and cannot coexist with it.
    let mutable installMode = ModInstaller.OptiScalerMode

    /// Which build of the mod to deploy. DX11 defaults to 64-bit and DX9 to
    /// 32-bit, matching what those two eras of games actually are.
    let mutable installArch = ModInstaller.Bit64

    /// OptiScaler only: which API the game renders with.
    let mutable optiApi = ModInstaller.OptiDx12

    /// The route and build recorded in the manifest for the open game, "" when
    /// this app did not install it. Drives the Install / Switch / Remove button.
    let mutable installedRoute = ""
    let mutable installedArch = ""
    let mutable installedApi = ""

    /// The detected-target readout starts folded away: it is reference
    /// information, and the sheet is about choosing and installing.
    let mutable isTargetDetailsOpen = false

    // ---- What the game itself turned out to be ---------------------------
    let mutable detectedApi = ""
    let mutable detectedArch = ""
    let mutable detectedDlss = false

    /// The executable the detection above describes. Opening a sheet asks twice
    /// - once immediately, once after the deep scan settles the path - and the
    /// second pass is skipped when nothing moved.
    let mutable detectedFor = ""

    // ---- Custom mod payload state ---------------------------------------
    let mutable payloadStatusText = ""

    /// The swappable payload files. OptiScaler is not here: it is a folder,
    /// and it has no switch because there is nothing to install without it.
    let payloadRows =
        ObservableCollection<PayloadRowViewModel>(
            [ ModInstaller.feedAddonName
              ModInstaller.feedAddon32Name
              ModInstaller.renodxAddonName
              GameAnalyzer.dlssnrFileName ]
            |> List.map (fun key ->
                PayloadRowViewModel(key, (fun () -> ModInstaller.Payload.describe key), true))
        )

    let extraRows = ObservableCollection<ExtraRowViewModel>()

    /// Which settings sections are unfolded. All start closed; a search that
    /// matches a section opens it so the result is actually readable.
    let openSections = System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)

    // ---- Update check state ---------------------------------------------
    let mutable isCheckingUpdates = false
    let mutable updateStatusText = ""
    let mutable hasUpdateAvailable = false
    let mutable latestVersionFound = ""

    let mutable selectedColorAtmosphere = "Neon Emerald"
    let mutable selectedGeometricMotif = "Orbital Spheres"

    /// AMD RDNA 4 route. Off until the user turns it on, and then every game
    /// installs through that one payload instead of the usual routes.
    let mutable isAmdMode = false

    // ---- Support prompt --------------------------------------------------
    /// The version whose prompt has already been seen on this machine.
    let mutable supportPromptVersion = ""
    let mutable isSupportPromptVisible = false

    /// Ten minutes in, once, and never again for this build.
    let supportPromptTimer =
        DispatcherTimer(Interval = TimeSpan.FromMinutes(10.0))

    // ---- Language --------------------------------------------------------
    let mutable languageCode = "en"
    let mutable loc = Localization.Strings("en")

    /// The atmosphere identities as stored and matched. Only the labels the
    /// user reads are translated, so a theme survives a language change.
    let atmosphereKeys =
        [| "Neon Emerald"; "Obsidian Onyx"; "Supernova Flare"; "Cyber Nebula"; "Emerald Horizon"
           "Midnight Titanium"; "Frost Glacier"; "Eclipse Crimson"; "Deep Astral" |]

    /// Built once and never rebuilt - see AtmosphereOption.
    let atmosphereOptions =
        ObservableCollection<AtmosphereOption>(atmosphereKeys |> Array.map AtmosphereOption)

    let motifKeys =
        [| "Orbital Spheres"; "Prism Auroras"; "Floating Crystals"
           "Stardust Particles"; "Cyber Flux"; "Minimal Clean" |]

    let motifOptions =
        ObservableCollection<AtmosphereOption>(motifKeys |> Array.map AtmosphereOption)

    /// Rewrites the labels in place for the language now in use.
    let relabelAtmospheres (strings: Localization.Strings) =
        let names = strings.AtmosphereNames

        atmosphereOptions
        |> Seq.iteri (fun i option -> if i < names.Length then option.Display <- names.[i])

        let motifNames = strings.MotifNames

        motifOptions
        |> Seq.iteri (fun i option -> if i < motifNames.Length then option.Display <- motifNames.[i])

    let mutable bgBaseBrush: IBrush = SolidColorBrush(Color.Parse("#080D0A"))
    let mutable bgOrb1Brush: IBrush = null
    let mutable bgOrb2Brush: IBrush = null
    let mutable bgVignetteBrush: IBrush = null
    let mutable motifAccentBrush: IBrush = SolidColorBrush(Color.Parse("#35D22B"))

    /// Background animations run only while the window is in front.
    let mutable isWindowActive = true

    /// Performance mode: no moving background, no card hover animation.
    let mutable isPerformanceMode = false

    let createRadialBrush (centerHex: string) =
        let brush = RadialGradientBrush()
        brush.Center <- RelativePoint(0.5, 0.5, RelativeUnit.Relative)
        brush.GradientOrigin <- RelativePoint(0.5, 0.5, RelativeUnit.Relative)
        brush.RadiusX <- RelativeScalar(0.5, RelativeUnit.Relative)
        brush.RadiusY <- RelativeScalar(0.5, RelativeUnit.Relative)
        brush.GradientStops.Add(GradientStop(Color.Parse(centerHex), 0.0))
        brush.GradientStops.Add(GradientStop(Color.FromArgb(0uy, 0uy, 0uy, 0uy), 1.0))
        brush

    let createVignetteBrush (edgeHex: string) =
        let brush = RadialGradientBrush()
        brush.Center <- RelativePoint(0.5, 0.5, RelativeUnit.Relative)
        brush.GradientOrigin <- RelativePoint(0.5, 0.5, RelativeUnit.Relative)
        brush.RadiusX <- RelativeScalar(0.8, RelativeUnit.Relative)
        brush.RadiusY <- RelativeScalar(0.8, RelativeUnit.Relative)
        brush.GradientStops.Add(GradientStop(Color.FromArgb(0uy, 0uy, 0uy, 0uy), 0.4))
        brush.GradientStops.Add(GradientStop(Color.Parse(edgeHex), 1.0))
        brush

    let applyAtmosphere (colorTheme: string) =
        selectedColorAtmosphere <- colorTheme
        match colorTheme with
        | "Obsidian Onyx" ->
            bgBaseBrush <- SolidColorBrush(Color.Parse("#0C0E14"))
            bgOrb1Brush <- createRadialBrush("#283552")
            bgOrb2Brush <- createRadialBrush("#1E2038")
            bgVignetteBrush <- createVignetteBrush("#5004030A")
            motifAccentBrush <- SolidColorBrush(Color.Parse("#38BDF8"))
        | "Supernova Flare" ->
            bgBaseBrush <- SolidColorBrush(Color.Parse("#140A0C"))
            bgOrb1Brush <- createRadialBrush("#5C1A22")
            bgOrb2Brush <- createRadialBrush("#3D101C")
            bgVignetteBrush <- createVignetteBrush("#550E0305")
            motifAccentBrush <- SolidColorBrush(Color.Parse("#F43F5E"))
        | "Cyber Nebula" ->
            bgBaseBrush <- SolidColorBrush(Color.Parse("#0D0B16"))
            bgOrb1Brush <- createRadialBrush("#3D1E5A")
            bgOrb2Brush <- createRadialBrush("#1C2454")
            bgVignetteBrush <- createVignetteBrush("#5007030F")
            motifAccentBrush <- SolidColorBrush(Color.Parse("#C084FC"))
        | "Emerald Horizon" ->
            bgBaseBrush <- SolidColorBrush(Color.Parse("#081010"))
            bgOrb1Brush <- createRadialBrush("#14423A")
            bgOrb2Brush <- createRadialBrush("#0E3140")
            bgVignetteBrush <- createVignetteBrush("#50020808")
            motifAccentBrush <- SolidColorBrush(Color.Parse("#34D399"))
        | "Midnight Titanium" ->
            bgBaseBrush <- SolidColorBrush(Color.Parse("#0A0A0C"))
            bgOrb1Brush <- createRadialBrush("#2D2E38")
            bgOrb2Brush <- createRadialBrush("#1C1D24")
            bgVignetteBrush <- createVignetteBrush("#50000000")
            motifAccentBrush <- SolidColorBrush(Color.Parse("#E2E8F0"))
        | "Frost Glacier" ->
            bgBaseBrush <- SolidColorBrush(Color.Parse("#091017"))
            bgOrb1Brush <- createRadialBrush("#18435C")
            bgOrb2Brush <- createRadialBrush("#162842")
            bgVignetteBrush <- createVignetteBrush("#50030A12")
            motifAccentBrush <- SolidColorBrush(Color.Parse("#38BDF8"))
        | "Eclipse Crimson" ->
            bgBaseBrush <- SolidColorBrush(Color.Parse("#12070A"))
            bgOrb1Brush <- createRadialBrush("#4C111F")
            bgOrb2Brush <- createRadialBrush("#2E0814")
            bgVignetteBrush <- createVignetteBrush("#550F0206")
            motifAccentBrush <- SolidColorBrush(Color.Parse("#E11D48"))
        | "Deep Astral" ->
            bgBaseBrush <- SolidColorBrush(Color.Parse("#070B14"))
            bgOrb1Brush <- createRadialBrush("#152C4E")
            bgOrb2Brush <- createRadialBrush("#0F1A30")
            bgVignetteBrush <- createVignetteBrush("#5002060D")
            motifAccentBrush <- SolidColorBrush(Color.Parse("#60A5FA"))
        | _ ->
            // Neon Emerald: signature DLSS 5 MANAGER atmosphere (matches the logo mark)
            selectedColorAtmosphere <- "Neon Emerald"
            bgBaseBrush <- SolidColorBrush(Color.Parse("#080D0A"))
            bgOrb1Brush <- createRadialBrush("#14401C")
            bgOrb2Brush <- createRadialBrush("#12291F")
            bgVignetteBrush <- createVignetteBrush("#55020604")
            motifAccentBrush <- SolidColorBrush(Color.Parse("#35D22B"))

    /// A settings card answers for itself whether the query is about it.
    /// Keywords carry both the English names and whatever the current language
    /// shows, so searching works without having to think in English.
    let matchesCard (query: string) (keywords: string seq) =
        if String.IsNullOrWhiteSpace(query) then
            true
        else
            let needle = query.Trim().ToLowerInvariant()

            keywords
            |> Seq.exists (fun (k: string) ->
                not (String.IsNullOrWhiteSpace(k)) && k.ToLowerInvariant().Contains(needle))

    let filterGamesList () =
        filteredGames.Clear()
        let query = searchText.Trim().ToLowerInvariant()
        for card in allGames do
            let matchesSearch =
                if String.IsNullOrWhiteSpace(query) then true
                else
                    card.Title.ToLowerInvariant().Contains(query)
                    || card.LauncherType.ToLowerInvariant().Contains(query)

            if matchesSearch then filteredGames.Add(card)

    let filterEmulatorsList () =
        filteredEmulators.Clear()
        let query = searchText.Trim().ToLowerInvariant()

        for card in allEmulators do
            if String.IsNullOrWhiteSpace(query) || card.Title.ToLowerInvariant().Contains(query) then
                filteredEmulators.Add(card)

    do
        // 0. Restore saved user preferences (layout + theme atmosphere + motif)
        let settings = GameScanner.loadSettings ()
        isSidebarLayout <- settings.IsSidebarLayout
        selectedGeometricMotif <- settings.GeometricMotif
        applyAtmosphere settings.ColorAtmosphere

        languageCode <- if Localization.isKnownLanguage settings.Language then settings.Language else "en"
        loc <- Localization.Strings(languageCode)
        relabelAtmospheres loc

        isAmdMode <- settings.AmdMode
        isPerformanceMode <- settings.PerformanceMode

        for (key, items) in ExtrasStore.groups () do
            extraRows.Add(ExtraRowViewModel(key, items))

        supportPromptVersion <-
            if isNull (box settings.SupportPromptVersion) then "" else settings.SupportPromptVersion

        // Only arm it when this build has not asked yet. A single tick, then
        // the timer stops for good.
        if supportPromptVersion <> UpdateChecker.CurrentVersion then
            supportPromptTimer.Tick.Add(fun _ ->
                supportPromptTimer.Stop()
                this.IsSupportPromptVisible <- true

                supportPromptVersion <- UpdateChecker.CurrentVersion

                GameScanner.saveSettings
                    { IsSidebarLayout = isSidebarLayout
                      ColorAtmosphere = selectedColorAtmosphere
                      GeometricMotif = selectedGeometricMotif
                      Language = languageCode
                      SupportPromptVersion = supportPromptVersion
                      AmdMode = isAmdMode
                      PerformanceMode = isPerformanceMode })

            supportPromptTimer.Start()

        // Emulators are hand-curated, so they simply come back as they were.
        for e in GameScanner.loadCachedEmulators () do
            let card = GameCardViewModel(e)
            allEmulators.Add(card)
            filteredEmulators.Add(card)

        // 1. Instant load from the local cache (0 ms perceived startup)
        let cached = GameScanner.loadCachedGames ()

        // A cache written by an older build may be missing whatever this one
        // learned to detect, so the first launch after an update re-reads the
        // library by itself. StartScanAsync keeps hand-added games.
        let firstRunOfThisBuild =
            GameScanner.readCacheVersion () <> UpdateChecker.CurrentVersion

        if cached.Length > 0 then
            for g in cached do
                let card = GameCardViewModel(g)
                allGames.Add(card)
                filteredGames.Add(card)
            totalGamesCount <- allGames.Count
            scanStatusText <- sprintf "%d Games Ready" cached.Length
            isScanning <- false

        if firstRunOfThisBuild then
            // 2. Just updated: the cards above are shown straight away so the
            //    window is never blank, and the real scan replaces them.
            GameScanner.writeCacheVersion UpdateChecker.CurrentVersion
            this.StartScanAsync()
        elif cached.Length > 0 then
            // Top up the deep-scan cache for anything added before this feature
            // existed. Does nothing (and shows nothing) when everything is cached.
            this.AnalyzePendingGames(cached)
        else
            // First run / empty cache: full library scan
            this.StartScanAsync()

        // 3. Look for a newer build every launch. It runs off the UI thread and
        //    stays silent unless there really is one, so startup is unaffected.
        this.CheckForUpdatesOnStartup()

    // ---------------------------------------------------------------------
    // THEME / ATMOSPHERE
    // ---------------------------------------------------------------------
    member this.BgBaseBrush = bgBaseBrush
    member this.BgOrb1Brush = bgOrb1Brush
    member this.BgOrb2Brush = bgOrb2Brush
    member this.BgVignetteBrush = bgVignetteBrush
    member this.MotifAccentBrush = motifAccentBrush

    /// Drives the background animations: only while the window is the active
    /// one, and never in performance mode.
    member this.IsBackgroundMotionOn = isWindowActive && not isPerformanceMode

    member this.IsWindowActive
        with get () = isWindowActive
        and set value =
            if isWindowActive <> value then
                isWindowActive <- value
                this.RaisePropertyChanged("IsBackgroundMotionOn")

    /// Turns off the moving background and the card hover animation. The look
    /// is unchanged at rest; only the motion goes.
    member this.IsPerformanceMode
        with get () = isPerformanceMode
        and set value =
            if isPerformanceMode <> value then
                isPerformanceMode <- value
                this.RaisePropertyChanged("IsPerformanceMode")
                this.RaisePropertyChanged("IsBackgroundMotionOn")

                GameScanner.saveSettings
                    { IsSidebarLayout = isSidebarLayout
                      ColorAtmosphere = selectedColorAtmosphere
                      GeometricMotif = selectedGeometricMotif
                      Language = languageCode
                      SupportPromptVersion = supportPromptVersion
                      AmdMode = isAmdMode
                      PerformanceMode = isPerformanceMode }

    /// Shown translated, stored in English: `AtmosphereOption.Key` is the
    /// identity. The collection instance is stable for the life of the window.
    member this.ColorAtmospheres = atmosphereOptions

    /// The picker binds to the index, not the text. Changing language replaces
    /// every item in the list, and a selection held by value would no longer
    /// match anything and come back blank; the index survives untouched.
    member this.SelectedAtmosphereIndex
        with get () =
            atmosphereKeys
            |> Array.tryFindIndex ((=) selectedColorAtmosphere)
            |> Option.defaultValue 0
        and set (index: int) =
            // A list swap momentarily reports -1; that is not a user choice.
            if index >= 0 && index < atmosphereKeys.Length then
                let value = atmosphereKeys.[index]

                if value <> selectedColorAtmosphere then
                    applyAtmosphere value
                    this.RaisePropertyChanged("BgBaseBrush")
                    this.RaisePropertyChanged("BgOrb1Brush")
                    this.RaisePropertyChanged("BgOrb2Brush")
                    this.RaisePropertyChanged("BgVignetteBrush")
                    this.RaisePropertyChanged("MotifAccentBrush")
                    this.RaisePropertyChanged("SelectedAtmosphereIndex")

                    GameScanner.saveSettings
                        { IsSidebarLayout = isSidebarLayout
                          ColorAtmosphere = value
                          GeometricMotif = selectedGeometricMotif
                          Language = languageCode
                          SupportPromptVersion = supportPromptVersion
                          AmdMode = isAmdMode
                          PerformanceMode = isPerformanceMode }

    member this.SelectedColorAtmosphere
        with get () =
            let index = atmosphereKeys |> Array.tryFindIndex ((=) selectedColorAtmosphere)
            loc.AtmosphereNames.[defaultArg index 0]
        and set (display: string) =
            let value =
                loc.AtmosphereNames
                |> Array.tryFindIndex ((=) display)
                |> Option.map (fun i -> atmosphereKeys.[i])
                |> Option.defaultValue display

            if not (String.IsNullOrWhiteSpace(value)) && value <> selectedColorAtmosphere then
                applyAtmosphere value
                this.RaisePropertyChanged("BgBaseBrush")
                this.RaisePropertyChanged("BgOrb1Brush")
                this.RaisePropertyChanged("BgOrb2Brush")
                this.RaisePropertyChanged("BgVignetteBrush")
                this.RaisePropertyChanged("MotifAccentBrush")
                this.RaisePropertyChanged("SelectedColorAtmosphere")
                GameScanner.saveSettings
                    { IsSidebarLayout = isSidebarLayout
                      ColorAtmosphere = value
                      GeometricMotif = selectedGeometricMotif
                      Language = languageCode
                      SupportPromptVersion = supportPromptVersion
                      AmdMode = isAmdMode
                      PerformanceMode = isPerformanceMode }

    // ---------------------------------------------------------------------
    // LANGUAGE
    // ---------------------------------------------------------------------
    /// Every visible string is read through this one object. Swapping it and
    /// announcing the change is what repaints the whole window in the new
    /// language - no per-label notifications to forget.
    member this.Loc = loc

    member this.Languages = Localization.availableLanguages |> Array.map snd

    member this.SelectedLanguage
        with get () = Localization.displayName languageCode
        and set (display: string) =
            let code =
                Localization.availableLanguages
                |> Array.tryFind (fun (_, name) -> name = display)
                |> Option.map fst
                |> Option.defaultValue languageCode

            if code <> languageCode then
                languageCode <- code
                loc <- Localization.Strings(code)

                this.RaisePropertyChanged("Loc")
                this.RaisePropertyChanged("SelectedLanguage")
                // Labels change, the list does not - so the picker keeps its
                // selection and simply redraws it in the new language.
                relabelAtmospheres loc
                this.RaisePropertyChanged("TotalGamesText")
                this.RaisePropertyChanged("InstallModeHintText")
                this.RaiseDlss5State()

                GameScanner.saveSettings
                    { IsSidebarLayout = isSidebarLayout
                      ColorAtmosphere = selectedColorAtmosphere
                      GeometricMotif = selectedGeometricMotif
                      Language = code
                      SupportPromptVersion = supportPromptVersion
                      AmdMode = isAmdMode
                      PerformanceMode = isPerformanceMode }

    /// "TOTAL GAMES: 42" in the current language.
    member this.TotalGamesText = loc.TotalGames(totalGamesCount)

    /// Same arrangement as the colour atmospheres: translated labels on stable
    /// objects, English keys underneath, so a language change cannot lose the
    /// selection or the saved value.
    member this.GeometricMotifs = motifOptions

    member this.SelectedMotifIndex
        with get () =
            motifKeys
            |> Array.tryFindIndex ((=) selectedGeometricMotif)
            |> Option.defaultValue 0
        and set (index: int) =
            // A list swap momentarily reports -1; that is not a user choice.
            if index >= 0 && index < motifKeys.Length then
                this.SelectedGeometricMotif <- motifKeys.[index]

    member this.SelectedGeometricMotif
        with get () = selectedGeometricMotif
        and set value =
            if not (String.IsNullOrWhiteSpace(value)) && value <> selectedGeometricMotif then
                selectedGeometricMotif <- value
                this.RaisePropertyChanged("SelectedGeometricMotif")
                this.RaisePropertyChanged("IsOrbitalSpheresVisible")
                this.RaisePropertyChanged("IsPrismAurorasVisible")
                this.RaisePropertyChanged("IsFloatingCrystalsVisible")
                this.RaisePropertyChanged("IsStardustParticlesVisible")
                this.RaisePropertyChanged("IsCyberFluxVisible")
                GameScanner.saveSettings
                    { IsSidebarLayout = isSidebarLayout
                      ColorAtmosphere = selectedColorAtmosphere
                      GeometricMotif = value
                      Language = languageCode
                      SupportPromptVersion = supportPromptVersion
                      AmdMode = isAmdMode
                      PerformanceMode = isPerformanceMode }

    member this.IsOrbitalSpheresVisible = selectedGeometricMotif = "Orbital Spheres"
    member this.IsPrismAurorasVisible = selectedGeometricMotif = "Prism Auroras"
    member this.IsFloatingCrystalsVisible = selectedGeometricMotif = "Floating Crystals"
    member this.IsStardustParticlesVisible = selectedGeometricMotif = "Stardust Particles"
    member this.IsCyberFluxVisible = selectedGeometricMotif = "Cyber Flux"

    // ---------------------------------------------------------------------
    // LAYOUT & NAVIGATION
    // ---------------------------------------------------------------------
    member this.IsSidebarLayout
        with get () = isSidebarLayout
        and set value =
            if this.SetProperty(&isSidebarLayout, value) then
                this.RaisePropertyChanged("IsTopBarLayout")
                GameScanner.saveSettings
                    { IsSidebarLayout = value
                      ColorAtmosphere = selectedColorAtmosphere
                      GeometricMotif = selectedGeometricMotif
                      Language = languageCode
                      SupportPromptVersion = supportPromptVersion
                      AmdMode = isAmdMode
                      PerformanceMode = isPerformanceMode }

    member this.IsTopBarLayout = not isSidebarLayout

    member this.SetLayoutMode(isSidebar: bool) = this.IsSidebarLayout <- isSidebar

    /// Three pages, one at a time. Everything else reads off this.
    member this.ActiveSection
        with get () = activeSection
        and set (value: string) =
            if activeSection <> value then
                activeSection <- value
                isSettingsOpen <- (value = "settings")

                this.RaisePropertyChanged("ActiveSection")
                this.RaisePropertyChanged("IsSettingsOpen")
                this.RaisePropertyChanged("IsGamesViewVisible")
                this.RaisePropertyChanged("IsEmulatorsViewVisible")
                this.RaisePropertyChanged("IsGamesTabActive")
                this.RaisePropertyChanged("IsEmulatorsTabActive")
                this.RaisePropertyChanged("SearchPlaceholder")

    member this.IsSettingsOpen
        with get () = isSettingsOpen
        and set value = this.ActiveSection <- (if value then "settings" else "games")

    /// One box, three jobs - it says which one it is doing right now.
    member this.SearchPlaceholder =
        match activeSection with
        | "settings" -> "Search settings..."
        | "emulators" -> "Search emulators..."
        | _ -> "Search games..."

    member this.IsGamesViewVisible = activeSection = "games"
    member this.IsEmulatorsViewVisible = activeSection = "emulators"
    member this.IsGamesTabActive = activeSection = "games"
    member this.IsEmulatorsTabActive = activeSection = "emulators"

    member this.OpenSettings() = this.ActiveSection <- "settings"
    member this.CloseSettings() = this.ActiveSection <- "games"
    member this.ShowGames() = this.ActiveSection <- "games"
    member this.ShowEmulators() = this.ActiveSection <- "emulators"

    member this.ToggleSettings() =
        this.ActiveSection <- (if activeSection = "settings" then "games" else "settings")

    // ---------------------------------------------------------------------
    // LIBRARY
    // ---------------------------------------------------------------------
    member this.Games = filteredGames
    member this.AllGamesCount = totalGamesCount
    member this.HasGames = filteredGames.Count > 0

    member this.SearchText
        with get () = searchText
        and set value =
            if this.SetProperty(&searchText, value) then
                filterGamesList ()
                filterEmulatorsList ()
                this.RaisePropertyChanged("HasGames")
                this.RaisePropertyChanged("HasEmulators")
                // The same box filters whichever page is open.
                this.RaiseSettingsFilter()

    // ---------------------------------------------------------------------
    // COLLAPSIBLE SETTINGS SECTIONS
    // ---------------------------------------------------------------------
    /// The long sections start folded so the page reads as a short list.
    /// Support, AMD mode and About stay open - they are one line each.
    member this.IsInterfaceSectionOpen = openSections.Contains("interface")
    member this.IsAtmosphereSectionOpen = openSections.Contains("atmosphere")
    member this.IsLibrarySectionOpen = openSections.Contains("library")
    member this.IsPayloadSectionOpen = openSections.Contains("payload")

    member private this.Chevron(key: string) =
        if openSections.Contains(key) then "M 7,14 L 12,9 L 17,14 Z" else "M 7,10 L 12,15 L 17,10 Z"

    member this.InterfaceChevron = this.Chevron("interface")
    member this.AtmosphereChevron = this.Chevron("atmosphere")
    member this.LibraryChevron = this.Chevron("library")
    member this.PayloadChevron = this.Chevron("payload")

    member this.ToggleSection(key: string) =
        if openSections.Contains(key) then openSections.Remove(key) |> ignore
        else openSections.Add(key) |> ignore

        this.RaiseSectionState()

    member private this.RaiseSectionState() =
        for name in [ "Interface"; "Atmosphere"; "Library"; "Payload" ] do
            this.RaisePropertyChanged("Is" + name + "SectionOpen")
            this.RaisePropertyChanged(name + "Chevron")

    // ---------------------------------------------------------------------
    // SETTINGS SEARCH
    // ---------------------------------------------------------------------
    member this.ShowInterfaceCard =
        matchesCard searchText [ loc.InterfaceNavigation; loc.NavbarPosition; loc.TopBar; loc.SideBar
                                 "interface"; "navigation"; "layout"; "language"; "sidebar" ]

    member this.ShowAtmosphereCard =
        matchesCard searchText
            [ yield loc.VisualAtmosphere
              yield loc.ColorAtmosphere
              yield loc.GeometricMotif
              yield "visual"; yield "theme"; yield "color"; yield "motif"; yield "background"
              yield! loc.AtmosphereNames ]

    member this.ShowLibraryCard =
        matchesCard searchText [ loc.LibraryCache; loc.FastCacheGameIndex; loc.RescanLibrary; loc.ClearCache
                                 "library"; "cache"; "scan"; "rescan"; "games"; "index" ]

    member this.ShowPayloadCard =
        matchesCard searchText [ loc.ModPayloadFiles; loc.ModPayloadDesc; loc.Replace; loc.Restore
                                 "mod"; "payload"; "files"; "dlss5-feed.addon64"; "renodx-dlss5.addon64"
                                 "nvngx_dlssnr.dll"; "replace"; "restore" ]

    member this.ShowPerformanceCard =
        matchesCard searchText [ "performance"; "animation"; "animations"; "gpu"; "motion"; "background"; "fps" ]

    member this.ShowAmdCard =
        matchesCard searchText [ "amd"; "rdna"; "radeon"; "amd mode"; "beta"; "gpu" ]

    member this.ShowSupportCard =
        matchesCard searchText [ "support"; "donate"; "ko-fi"; "kofi"; "tutorial"; "tutorials"; "guide"
                                 "youtube"; "video"; "help"; "channel"; "nodix" ]

    member this.ShowAboutCard =
        matchesCard searchText [ "about"; "update"; "updates"; "version"; "credits"; "copyright"
                                 "dlss 5 manager"; "nodix"; "numidia" ]

    /// Nothing on the settings page answers the query.
    member this.HasNoSettingsMatch =
        not (String.IsNullOrWhiteSpace(searchText))
        && not (
            this.ShowInterfaceCard
            || this.ShowAtmosphereCard
            || this.ShowLibraryCard
            || this.ShowPayloadCard
            || this.ShowPerformanceCard
            || this.ShowAmdCard
            || this.ShowSupportCard
            || this.ShowAboutCard
        )

    member private this.RaiseSettingsFilter() =
        // A hit the user cannot see is no hit at all, so a section that matches
        // the query unfolds itself and folds back when the box is cleared.
        if String.IsNullOrWhiteSpace(searchText) then
            openSections.Clear()
        else
            for (key, matched) in
                [ "interface", this.ShowInterfaceCard
                  "atmosphere", this.ShowAtmosphereCard
                  "library", this.ShowLibraryCard
                  "payload", this.ShowPayloadCard ] do
                if matched then openSections.Add(key) |> ignore else openSections.Remove(key) |> ignore

        this.RaiseSectionState()
        this.RaisePropertyChanged("ShowInterfaceCard")
        this.RaisePropertyChanged("ShowAtmosphereCard")
        this.RaisePropertyChanged("ShowLibraryCard")
        this.RaisePropertyChanged("ShowPayloadCard")
        this.RaisePropertyChanged("ShowPerformanceCard")
        this.RaisePropertyChanged("ShowAmdCard")
        this.RaisePropertyChanged("ShowSupportCard")
        this.RaisePropertyChanged("ShowAboutCard")
        this.RaisePropertyChanged("HasNoSettingsMatch")

    member this.IsSearchOpen
        with get () = isSearchOpen
        and set value =
            if this.SetProperty(&isSearchOpen, value) then
                this.RaisePropertyChanged("SearchBoxWidth")
                this.RaisePropertyChanged("SearchBoxOpacity")
                this.RaisePropertyChanged("SearchBoxMargin")

    member this.SearchBoxWidth = if isSearchOpen then 180.0 else 0.0
    member this.SearchBoxOpacity = if isSearchOpen then 1.0 else 0.0

    member this.SearchBoxMargin =
        if isSearchOpen then Avalonia.Thickness(4.0, 0.0, 4.0, 0.0) else Avalonia.Thickness(0.0)

    member this.ToggleSearch() = this.IsSearchOpen <- not this.IsSearchOpen

    member this.IsScanning
        with get () = isScanning
        and set value = this.SetProperty(&isScanning, value) |> ignore

    member this.ScanStatusText
        with get () = scanStatusText
        and set value = this.SetProperty(&scanStatusText, value) |> ignore

    member this.ClearCacheAndRescan() =
        GameScanner.clearCache ()
        AnalysisStore.clear ()
        allGames.Clear()
        filteredGames.Clear()
        totalGamesCount <- 0
        this.RaisePropertyChanged("AllGamesCount")
        this.RaisePropertyChanged("TotalGamesText")
        this.RaisePropertyChanged("HasGames")
        this.CloseSettings()
        this.StartScanAsync()

    // ---------------------------------------------------------------------
    // DRAG & DROP REORDERING
    // ---------------------------------------------------------------------
    member this.IsDraggingCard
        with get () = isDraggingCard
        and set value = this.SetProperty(&isDraggingCard, value) |> ignore

    member this.DraggedCard
        with get () = draggedCard
        and set (value: GameCardViewModel option) =
            draggedCard <- value
            this.RaisePropertyChanged("DraggedCardTitle")
            this.RaisePropertyChanged("DraggedCardImage")

    member this.DraggedCardTitle =
        match draggedCard with
        | Some c -> c.Title
        | None -> ""

    member this.DraggedCardImage =
        match draggedCard with
        | Some c -> c.BannerImage
        | None -> null

    member this.SwapCards(source: GameCardViewModel, target: GameCardViewModel) =
        if not (isNull (box source)) && not (isNull (box target)) && not (Object.ReferenceEquals(source, target)) then
            let srcAll = allGames.IndexOf(source)
            let tgtAll = allGames.IndexOf(target)
            if srcAll >= 0 && tgtAll >= 0 then
                let tempAll = allGames.[srcAll]
                allGames.[srcAll] <- allGames.[tgtAll]
                allGames.[tgtAll] <- tempAll

            let srcFilt = filteredGames.IndexOf(source)
            let tgtFilt = filteredGames.IndexOf(target)
            if srcFilt >= 0 && tgtFilt >= 0 then
                let tempFilt = filteredGames.[srcFilt]
                filteredGames.[srcFilt] <- filteredGames.[tgtFilt]
                filteredGames.[tgtFilt] <- tempFilt

            try
                GameScanner.saveGamesToCache (allGames |> Seq.map (fun c -> c.Game) |> Seq.toList)
            with _ -> ()

    member this.ReorderGames(source: GameCardViewModel, target: GameCardViewModel) =
        if not (isNull (box source)) && not (isNull (box target)) && not (Object.ReferenceEquals(source, target)) then
            let oldIdx = allGames.IndexOf(source)
            let newIdx = allGames.IndexOf(target)
            if oldIdx >= 0 && newIdx >= 0 then
                allGames.Move(oldIdx, newIdx)
                let oldFilt = filteredGames.IndexOf(source)
                let newFilt = filteredGames.IndexOf(target)
                if oldFilt >= 0 && newFilt >= 0 then filteredGames.Move(oldFilt, newFilt)
                try
                    GameScanner.saveGamesToCache (allGames |> Seq.map (fun c -> c.Game) |> Seq.toList)
                with _ -> ()

    // ---------------------------------------------------------------------
    // SCANNING & LIBRARY MUTATION
    // ---------------------------------------------------------------------
    member this.StartScanAsync() =
        this.CloseSettings()
        if not isScanning then
            this.IsScanning <- true
            this.ScanStatusText <- "Scanning games library..."

            System.Threading.Tasks.Task.Run(fun () ->
                try
                    let cachedCustomGames =
                        try
                            GameScanner.loadCachedGames () |> List.filter (fun g -> g.LauncherTypeName = "CUSTOM")
                        with _ -> []

                    let scannedGames =
                        GameScanner.scanAllGamesAsync (fun progress ->
                            Dispatcher.UIThread.Post(fun () -> this.ScanStatusText <- progress))
                        |> Async.RunSynchronously

                    let combined =
                        GameScanner.deduplicateGames (scannedGames @ cachedCustomGames)
                        |> List.sortBy (fun g -> g.Title)

                    GameScanner.saveGamesToCache combined

                    // Deep-scan every title once and store it, so opening a game
                    // and pressing Install are instant from now on.
                    AnalysisStore.refreshMany combined (fun index total title ->
                        Dispatcher.UIThread.Post(fun () ->
                            this.ScanStatusText <- sprintf "Analyzing %d/%d - %s" index total title))

                    Dispatcher.UIThread.Post(fun () ->
                        allGames.Clear()
                        for g in combined do
                            allGames.Add(GameCardViewModel(g))

                        totalGamesCount <- allGames.Count
                        this.RaisePropertyChanged("AllGamesCount")
                        this.RaisePropertyChanged("TotalGamesText")
                        filterGamesList ()
                        this.RaisePropertyChanged("HasGames")
                        this.IsScanning <- false
                        this.ScanStatusText <- sprintf "%d Games Ready" combined.Length)
                with ex ->
                    printfn "[DLSS5Manager Error] Scan failed: %s" (ex.ToString())
                    Dispatcher.UIThread.Post(fun () ->
                        this.IsScanning <- false
                        this.ScanStatusText <- "Ready"))
            |> ignore

    /// Deep-scans only the games that have never been analyzed. Used on startup
    /// (to top up an older library) and right after a game is added by hand, so
    /// the user never has to trigger a re-scan to make Manage / Install work.
    member this.AnalyzePendingGames(games: GameItem list) =
        let pending = games |> List.filter (fun g -> (AnalysisStore.tryGet g).IsNone)

        if not pending.IsEmpty then
            this.IsScanning <- true
            this.ScanStatusText <- "Analyzing games..."

            System.Threading.Tasks.Task.Run(fun () ->
                AnalysisStore.refreshMany pending (fun index total title ->
                    Dispatcher.UIThread.Post(fun () ->
                        this.ScanStatusText <- sprintf "Analyzing %d/%d - %s" index total title))

                Dispatcher.UIThread.Post(fun () ->
                    this.IsScanning <- false
                    this.ScanStatusText <- sprintf "%d Games Ready" totalGamesCount))
            |> ignore

    /// Adding a game by hand only ever inspects the file the user picked. The
    /// whole thing runs off the UI thread and is wrapped, so a locked folder or
    /// an unreadable executable shows a message instead of taking the app down.
    member this.AddSingleGameExecutable(exePath: string) =
        if not (String.IsNullOrWhiteSpace(exePath)) && System.IO.File.Exists(exePath) then
            let fileName = System.IO.Path.GetFileName(exePath)
            this.IsScanning <- true
            this.ScanStatusText <- sprintf "Inspecting %s..." fileName

            System.Threading.Tasks.Task.Run(fun () ->
                let built =
                    try
                        let folderPath = System.IO.Path.GetDirectoryName(exePath)
                        let rawName = System.IO.Path.GetFileNameWithoutExtension(exePath)
                        let folderName = System.IO.Path.GetFileName(folderPath)

                        let title =
                            if folderName.Length > 2 && not (folderName.ToLowerInvariant().Contains("bin")) then
                                folderName
                            else
                                rawName

                        let item = GameScanner.createCustomGameItem title folderPath exePath
                        AnalysisStore.refreshExecutableOnly item |> ignore
                        Some item
                    with ex ->
                        printfn "[DLSS5Manager Error] Manual add failed: %s" (ex.ToString())
                        None

                Dispatcher.UIThread.Post(fun () ->
                    this.IsScanning <- false

                    match built with
                    | Some item ->
                        allGames.Add(GameCardViewModel(item))
                        totalGamesCount <- allGames.Count
                        this.RaisePropertyChanged("AllGamesCount")
                        this.RaisePropertyChanged("TotalGamesText")
                        filterGamesList ()
                        this.RaisePropertyChanged("HasGames")

                        try
                            GameScanner.saveGamesToCache [ for c in allGames -> c.Game ]
                        with _ ->
                            ()

                        this.ScanStatusText <- sprintf "%d Games Ready" totalGamesCount
                    | None -> this.ScanStatusText <- sprintf "Could not read %s" fileName))
            |> ignore

    // ---------------------------------------------------------------------
    // MANAGE SHEET
    // ---------------------------------------------------------------------
    member this.IsManageOpen
        with get () = isManageOpen
        and set value = this.SetProperty(&isManageOpen, value) |> ignore

    member this.ManageCard = manageCard
    member this.ManageTitle = manageTitle
    member this.ManageExePath = manageExePath
    member this.ManageFolder = manageFolder
    member this.ManageReShadeText = manageReShadeText
    member this.ManageDlssText = manageDlssText
    member this.ManageStreamlineText = manageStreamlineText

    member this.HasExecutable = not (String.IsNullOrWhiteSpace(manageExePath))

    member this.IsAnalyzing
        with get () = isAnalyzing
        and set value =
            if this.SetProperty(&isAnalyzing, value) then
                this.RaisePropertyChanged("IsManageReady")

    member this.IsInstalling
        with get () = isInstalling
        and set value =
            if this.SetProperty(&isInstalling, value) then
                this.RaisePropertyChanged("IsManageReady")
                this.RaiseDlss5State()

    /// Buttons are only live when nothing is running.
    member this.IsManageReady = not isInstalling && not isAnalyzing

    /// The three install routes. They are mutually exclusive - OptiScaler and
    /// ReShade cannot hook the same game at the same time.
    member this.IsOptiScalerMode = (installMode = ModInstaller.OptiScalerMode)
    member this.IsDx12Mode = (installMode = ModInstaller.Dx12Auto)
    member this.IsDx11Mode = (installMode = ModInstaller.Dx11)
    member this.IsDx9Mode = (installMode = ModInstaller.Dx9)

    member this.SetInstallMode(mode: ModInstaller.InstallMode) =
        if installMode <> mode then
            installMode <- mode

            // Each route opens on the build its era of games actually shipped:
            // DX11 is 64-bit, DX9 is 32-bit. Either can still be overridden.
            installArch <-
                match mode with
                | ModInstaller.Dx9 -> ModInstaller.Bit32
                | _ -> ModInstaller.Bit64

            this.RaiseInstallModeState()

    /// An emulator installs one way only, so the sheet drops the route picker,
    /// the build picker and the detection chips for it. AMD mode is the same
    /// shape: one payload, no choices.
    member this.IsEmulatorTarget = isEmulatorTarget
    member this.IsGameTarget = not isEmulatorTarget && not isAmdMode

    /// AMD RDNA 4 route, set in Settings and applied to every game.
    member this.IsAmdModeEnabled
        with get () = isAmdMode
        and set value =
            if isAmdMode <> value then
                isAmdMode <- value
                this.RaisePropertyChanged("IsAmdModeEnabled")
                this.RaisePropertyChanged("IsGameTarget")
                this.RaisePropertyChanged("IsAmdRouteActive")

                GameScanner.saveSettings
                    { IsSidebarLayout = isSidebarLayout
                      ColorAtmosphere = selectedColorAtmosphere
                      GeometricMotif = selectedGeometricMotif
                      Language = languageCode
                      SupportPromptVersion = supportPromptVersion
                      AmdMode = isAmdMode
                      PerformanceMode = isPerformanceMode }

    /// True while the open sheet will install through the AMD payload.
    member this.IsAmdRouteActive = isAmdMode && not isEmulatorTarget

    /// The 32-bit and DX9 routes only exist for DX11 and DX9.
    member this.IsArchChoiceVisible =
        not isEmulatorTarget && ModInstaller.archMatters installMode

    /// OptiScaler is the only route where the game's API changes anything.
    member this.IsOptiApiChoiceVisible =
        not isEmulatorTarget && installMode = ModInstaller.OptiScalerMode

    member this.IsOptiDx12 = (optiApi = ModInstaller.OptiDx12)
    member this.IsOptiVulkan = (optiApi = ModInstaller.OptiVulkan)

    member this.SetOptiApi(api: ModInstaller.OptiScalerApi) =
        if optiApi <> api then
            optiApi <- api
            this.RaiseInstallModeState()

    /// Marks the API an existing OptiScaler install actually used.
    member this.IsOptiDx12Installed =
        installedRoute = "optiscaler" && installedApi = "dx12"

    member this.IsOptiVulkanInstalled =
        installedRoute = "optiscaler" && installedApi = "vulkan"

    member this.IsBit64 = (installArch = ModInstaller.Bit64)
    member this.IsBit32 = (installArch = ModInstaller.Bit32)

    member this.SetInstallArch(arch: ModInstaller.InstallArch) =
        if installArch <> arch then
            installArch <- arch
            this.RaiseInstallModeState()

    member private this.RaiseInstallModeState() =
        this.RaisePropertyChanged("IsOptiScalerMode")
        this.RaisePropertyChanged("IsDx12Mode")
        this.RaisePropertyChanged("IsDx11Mode")
        this.RaisePropertyChanged("IsDx9Mode")
        this.RaisePropertyChanged("IsArchChoiceVisible")
        this.RaisePropertyChanged("IsOptiApiChoiceVisible")
        this.RaisePropertyChanged("IsOptiDx12")
        this.RaisePropertyChanged("IsOptiVulkan")
        this.RaisePropertyChanged("IsBit64")
        this.RaisePropertyChanged("IsBit32")
        this.RaisePropertyChanged("InstallModeHintText")
        // Picking a different route turns Install / Remove into Switch.
        this.RaiseDlss5State()

    // ---------------------------------------------------------------------
    // WHAT THE GAME IS
    // ---------------------------------------------------------------------
    member this.DetectedApiText =
        match detectedApi with
        | "dx12" -> "DirectX 12"
        | "dx11" -> "DirectX 11"
        | "dx10" -> "DirectX 10"
        | "dx9" -> "DirectX 9"
        | _ -> "Unknown API"

    member this.DetectedArchText =
        match detectedArch with
        | "32" -> "32-bit"
        | "64" -> "64-bit"
        | _ -> ""

    /// Folds the executable / ReShade / DLSS / Streamline readout away.
    member this.IsTargetDetailsOpen
        with get () = isTargetDetailsOpen
        and set value =
            if this.SetProperty(&isTargetDetailsOpen, value) then
                this.RaisePropertyChanged("TargetDetailsToggleIcon")

    member this.ToggleTargetDetails() =
        this.IsTargetDetailsOpen <- not isTargetDetailsOpen

    /// Chevron: pointing down when closed, up when open.
    member this.TargetDetailsToggleIcon =
        if isTargetDetailsOpen then "M 7,14 L 12,9 L 17,14 Z" else "M 7,10 L 12,15 L 17,10 Z"

    member this.HasDetectedArch = detectedArch <> ""
    member this.HasDlssUpscaling = detectedDlss

    /// Reads the executable and its folder, then opens the sheet on the route
    /// that suits what was found. An install this app already made outranks it.
    member private this.DetectTarget(respectExisting: bool) =
        // Re-reading the same executable would only produce the same answer.
        let sameTarget = String.Equals(detectedFor, manageExePath, StringComparison.OrdinalIgnoreCase)

        if not sameTarget then
            detectedFor <- manageExePath
            detectedApi <- GameAnalyzer.detectGraphicsApi manageExePath
            detectedArch <- GameAnalyzer.detectArchitecture manageExePath

        let dlssFilesFound =
            match manageAnalysis with
            | Some a -> not (isNull (box a.DlssDirs)) && a.DlssDirs.Length > 0
            | None ->
                try
                    let dir = System.IO.Path.GetDirectoryName(manageExePath)

                    not (String.IsNullOrWhiteSpace(dir))
                    && GameAnalyzer.dlssFileNames
                       |> Array.exists (fun n -> System.IO.File.Exists(System.IO.Path.Combine(dir, n)))
                with _ ->
                    false

        // DLSS did not exist in the Direct3D 9 or 10 era, so a runtime sitting
        // in one of those folders is something we put there on an earlier run,
        // not the game shipping its own. Treating it as the game's would send
        // every modded DX9 title to OptiScaler.
        detectedDlss <-
            dlssFilesFound && detectedApi <> "dx9" && detectedApi <> "dx10"

        this.RaisePropertyChanged("DetectedApiText")
        this.RaisePropertyChanged("DetectedArchText")
        this.RaisePropertyChanged("HasDetectedArch")
        this.RaisePropertyChanged("HasDlssUpscaling")

        if not (respectExisting && installedRoute <> "") then
            // The Direct3D generation is the hard constraint, so it is read
            // first: a DX9 title can only take the DX9 route whatever else is
            // lying in its folder. Past that, a game already shipping DLSS
            // upscaling is what OptiScaler is for, and anything newer than
            // DX11 - or unreadable - takes the DX12 route.
            let mode =
                if detectedApi = "dx9" then ModInstaller.Dx9
                elif detectedApi = "dx10" then ModInstaller.Dx11
                elif detectedDlss then ModInstaller.OptiScalerMode
                elif detectedApi = "dx11" then ModInstaller.Dx11
                else ModInstaller.Dx12Auto

            this.SetInstallMode(mode)
            this.SetInstallArch(if detectedArch = "32" then ModInstaller.Bit32 else ModInstaller.Bit64)

    member this.InstallModeHintText =
        match installMode with
        | ModInstaller.OptiScalerMode -> "OptiScaler hooks the game directly. ReShade is not used."
        | ModInstaller.Dx12Auto -> "ReShade + RenoDX with the DLSS 5 effects."
        | ModInstaller.Dx11 -> "ReShade + RenoDX with the DLSS 5 effects."
        | ModInstaller.Dx9 -> "dgVoodoo translates Direct3D 9; ReShade moves to the dxgi slot."
        | ModInstaller.Emulator -> "ReShade on Vulkan with the DLSS 5 emulator payload."
        | ModInstaller.AmdMode -> "AMD RDNA 4: the AMD payload and the ray reconstruction model, nothing else."

    member this.IsModInstalled
        with get () = isModInstalled
        and set value = this.SetProperty(&isModInstalled, value) |> ignore

    /// Three mutually exclusive actions driven purely by what is on disk:
    ///   nothing installed          -> Install DLSS 5
    ///   installed but files missing -> Complete Installation
    ///   installed and healthy       -> Remove DLSS 5
    /// True when a route this app installed is on the game and the user has
    /// since picked a different one. The routes cannot coexist, so the primary
    /// action becomes "switch": remove the old one, then install the new one.
    member this.IsSwitchingRoute =
        dlss5Present
        && installedRoute <> ""
        && (installedRoute <> ModInstaller.modeKey installMode
            || (ModInstaller.archMatters installMode
                && installedArch <> ModInstaller.archKey installArch)
            || (installMode = ModInstaller.OptiScalerMode
                && installedApi <> ModInstaller.optiApiKey optiApi))

    member this.ShowInstallButton = not dlss5Present && not isInstalling
    member this.ShowSwitchButton = this.IsSwitchingRoute && not isInstalling

    member this.ShowCompleteButton =
        dlss5Present && not dlss5Complete && not this.IsSwitchingRoute && not isInstalling

    member this.ShowUninstallButton =
        dlss5Present && dlss5Complete && not this.IsSwitchingRoute && not isInstalling

    /// Marks the route that is actually on the game right now, so browsing the
    /// other two never loses track of which one is live.
    member this.IsOptiScalerInstalled = installedRoute = "optiscaler"
    member this.IsDx12Installed = installedRoute = "dx12"
    member this.IsDx11Installed = installedRoute = "dx11"
    member this.IsDx9Installed = installedRoute = "dx9"

    /// Marks the build in use, but only for the route that is actually on.
    member this.IsBit64Installed =
        installedArch = "64"
        && installedRoute = ModInstaller.modeKey installMode

    member this.IsBit32Installed =
        installedArch = "32"
        && installedRoute = ModInstaller.modeKey installMode

    member this.SwitchButtonText =
        let route =
            match installMode with
            | ModInstaller.OptiScalerMode -> "OptiScaler"
            | ModInstaller.Dx12Auto -> "DX12"
            | ModInstaller.Dx11 -> "DX11"
            | ModInstaller.Dx9 -> "DX9"

            | ModInstaller.Emulator -> "Emulator"


            | ModInstaller.AmdMode -> "AMD mode"

        if ModInstaller.archMatters installMode then
            sprintf "Switch to %s %s-bit" route (ModInstaller.archKey installArch)
        else
            "Switch to " + route

    member this.ManageDlss5Text =
        if not dlss5Present then loc.NotInstalled
        elif dlss5Complete then (if isModInstalled then loc.Installed else loc.Installed + " (external)")
        else
            let missing = if isNull (box dlss5Missing) then [||] else dlss5Missing
            if missing.Length = 0 then "Incomplete"
            else "Missing: " + String.Join(", ", missing)

    member this.ManageDlss5Brush: IBrush =
        if not dlss5Present then SolidColorBrush(Color.Parse("#CBD5E1")) :> IBrush
        elif dlss5Complete then SolidColorBrush(Color.Parse("#86EFAC")) :> IBrush
        else SolidColorBrush(Color.Parse("#FBBF24")) :> IBrush

    member private this.RaiseDlss5State() =
        this.RaisePropertyChanged("ShowInstallButton")
        this.RaisePropertyChanged("ShowSwitchButton")
        this.RaisePropertyChanged("ShowCompleteButton")
        this.RaisePropertyChanged("ShowUninstallButton")
        this.RaisePropertyChanged("SwitchButtonText")
        this.RaisePropertyChanged("IsSwitchingRoute")
        this.RaisePropertyChanged("IsOptiScalerInstalled")
        this.RaisePropertyChanged("IsDx12Installed")
        this.RaisePropertyChanged("IsDx11Installed")
        this.RaisePropertyChanged("IsDx9Installed")
        this.RaisePropertyChanged("IsBit64Installed")
        this.RaisePropertyChanged("IsBit32Installed")
        this.RaisePropertyChanged("IsOptiDx12Installed")
        this.RaisePropertyChanged("IsOptiVulkanInstalled")
        this.RaisePropertyChanged("ManageDlss5Text")
        this.RaisePropertyChanged("ManageDlss5Brush")

    member this.InstallProgress
        with get () = installProgress
        and set value = this.SetProperty(&installProgress, value) |> ignore

    member this.InstallStatusText
        with get () = installStatusText
        and set value = this.SetProperty(&installStatusText, value) |> ignore

    member this.InstallResultText
        with get () = installResultText
        and set value =
            if this.SetProperty(&installResultText, value) then
                this.RaisePropertyChanged("HasInstallResult")

    member this.HasInstallResult = not (String.IsNullOrWhiteSpace(installResultText))

    member this.InstallResultIsError
        with get () = installResultIsError
        and set value =
            if this.SetProperty(&installResultIsError, value) then
                this.RaisePropertyChanged("InstallResultBrush")

    member this.InstallResultBrush: IBrush =
        if installResultIsError then SolidColorBrush(Color.Parse("#F87171")) :> IBrush
        else SolidColorBrush(Color.Parse("#86EFAC")) :> IBrush

    member private this.RaiseManageInfo() =
        this.RaisePropertyChanged("ManageTitle")
        this.RaisePropertyChanged("ManageExePath")
        this.RaisePropertyChanged("ManageFolder")
        this.RaisePropertyChanged("ManageReShadeText")
        this.RaisePropertyChanged("ManageDlssText")
        this.RaisePropertyChanged("ManageStreamlineText")
        this.RaisePropertyChanged("HasExecutable")

    /// Paints the sheet from a cached deep-scan record - no disk walking.
    member private this.ApplyAnalysis(analysis: AnalysisStore.GameAnalysis) =
        manageAnalysis <- Some analysis

        let (route, arch) =
            match manageCard with
            | Some card -> ModInstaller.installedRouteAndArch card.Game
            | None -> ("", "")

        installedRoute <- route
        installedArch <- arch

        installedApi <-
            match manageCard with
            | Some card -> ModInstaller.installedOptiApi card.Game
            | None -> ""

        if not (String.IsNullOrWhiteSpace(analysis.ExecutablePath)) then
            manageExePath <- analysis.ExecutablePath
            manageFolder <- analysis.ExecutableFolder

        // On the emulator route ReShade hooks through the Vulkan layer, which
        // leaves no DLL beside the executable - the shared detector would
        // always report "Not installed" and read as a fault when it is not.
        manageReShadeText <-
            if analysis.ReShadeInstalled then loc.Installed
            elif isEmulatorTarget && installedRoute = "emulator" then loc.Installed + " (Vulkan layer)"
            else loc.NotInstalled
        manageDlssText <- analysis.DlssText
        manageStreamlineText <- analysis.StreamlineText
        dlss5Present <- analysis.Dlss5Present
        dlss5Complete <- analysis.Dlss5Complete
        dlss5Missing <- (if isNull (box analysis.Dlss5Missing) then [||] else analysis.Dlss5Missing)
        this.IsModInstalled <- analysis.ModInstalled
        this.RaiseManageInfo()
        this.RaiseDlss5State()

        // The deep scan may have corrected the executable, so re-read what the
        // game is from the file we now believe in. Emulators have one route.
        if not isEmulatorTarget && not isAmdMode then this.DetectTarget(true)

    /// Re-runs the deep scan for the open game and refreshes the stored record.
    member this.AnalyzeManageTarget() =
        match manageCard with
        | None -> ()
        | Some card ->
            this.IsAnalyzing <- true
            manageReShadeText <- "Checking..."
            manageDlssText <- "Checking..."
            manageStreamlineText <- "Checking..."
            this.RaiseManageInfo()

            let game = card.Game

            System.Threading.Tasks.Task.Run(fun () ->
                let analysis = AnalysisStore.refresh game

                Dispatcher.UIThread.Post(fun () ->
                    this.ApplyAnalysis(analysis)
                    this.IsAnalyzing <- false))
            |> ignore

    member this.OpenManage(card: GameCardViewModel) =
        manageCard <- Some card
        manageAnalysis <- None
        manageTitle <- card.Title
        manageExePath <- card.ExecutablePath

        manageFolder <-
            if String.IsNullOrWhiteSpace(card.ExecutablePath) then card.InstallDirectory
            else
                try System.IO.Path.GetDirectoryName(card.ExecutablePath)
                with _ -> card.InstallDirectory

        this.InstallResultText <- ""
        this.InstallProgress <- 0.0
        this.InstallStatusText <- ""
        this.IsManageOpen <- true

        // Preselect whichever route a managed install already used, so the
        // sheet opens on "Remove" rather than offering to switch to itself.
        let (route, arch) = ModInstaller.installedRouteAndArch card.Game
        installedRoute <- route
        installedArch <- arch
        installedApi <- ModInstaller.installedOptiApi card.Game

        if route = "optiscaler" then
            this.SetOptiApi(if installedApi = "vulkan" then ModInstaller.OptiVulkan else ModInstaller.OptiDx12)

        // An emulator has exactly one route, so none of the game-side choices
        // apply and the detection pass is skipped entirely.
        isEmulatorTarget <- card.Game.LauncherTypeName = "EMULATOR"
        this.RaisePropertyChanged("IsEmulatorTarget")
        this.RaisePropertyChanged("IsGameTarget")

        this.RaisePropertyChanged("IsAmdRouteActive")

        if isEmulatorTarget then
            this.SetInstallMode(ModInstaller.Emulator)
        elif isAmdMode then
            // AMD mode overrides detection entirely: one payload, no choices.
            this.SetInstallMode(ModInstaller.AmdMode)
        else
            match route with
            | "optiscaler" -> this.SetInstallMode(ModInstaller.OptiScalerMode)
            | "dx12" -> this.SetInstallMode(ModInstaller.Dx12Auto)
            | "dx11" -> this.SetInstallMode(ModInstaller.Dx11)
            | "dx9" -> this.SetInstallMode(ModInstaller.Dx9)
            | _ -> ()

            // SetInstallMode resets the build to that route's default, so the
            // recorded one is restored afterwards.
            if route <> "" then
                this.SetInstallArch(if arch = "32" then ModInstaller.Bit32 else ModInstaller.Bit64)

            // Nothing installed yet? Then what the game actually is decides.
            this.DetectTarget(true)

        // Everything was already measured during the scan, so the sheet opens
        // fully populated. Only a game we have never analyzed pays the cost.
        match AnalysisStore.tryGet card.Game with
        | Some cached ->
            this.ApplyAnalysis(cached)
            this.IsAnalyzing <- false
        | None ->
            this.RaiseManageInfo()
            this.AnalyzeManageTarget()

    member this.CloseManage() =
        if not isInstalling then
            this.IsManageOpen <- false
            manageCard <- None

    member this.SetManageExecutable(path: string) =
        match manageCard with
        | Some card when not (String.IsNullOrWhiteSpace(path)) ->
            card.SetExecutable(path)
            manageExePath <- path

            manageFolder <-
                try System.IO.Path.GetDirectoryName(path)
                with _ -> manageFolder

            this.RaiseManageInfo()
            GameScanner.saveGamesToCache [ for c in allGames -> c.Game ]
            this.AnalyzeManageTarget()
        | _ -> ()

    /// Switching routes is a removal followed by an install: OptiScaler and
    /// ReShade hook the same game in incompatible ways, so whatever the previous
    /// route put there - our ReShade included - comes out first.
    member this.StartSwitch() =
        match manageCard with
        | None -> ()
        | Some card ->
            let game = card.Game
            let exePath = manageExePath
            let target = installMode
            let targetArch = installArch
            let targetApi = optiApi

            this.InstallResultText <- ""
            this.InstallResultIsError <- false
            this.InstallProgress <- 0.0
            this.InstallStatusText <- "Removing the previous install..."
            this.IsInstalling <- true

            // The removal owns the first third of the bar, the install the rest.
            let report: ModInstaller.Progress =
                fun text progress ->
                    Dispatcher.UIThread.Post(fun () ->
                        this.InstallStatusText <- text
                        this.InstallProgress <- progress * 100.0)

            let scaled (offset: float) (span: float) : ModInstaller.Progress =
                fun text progress -> report text (offset + span * progress)

            let plan =
                manageAnalysis
                |> Option.map (fun a ->
                    { ModInstaller.InstallPlan.DlssDirs = a.DlssDirs
                      ModInstaller.InstallPlan.StreamlineDirs = a.StreamlineDirs })

            System.Threading.Tasks.Task.Run(fun () ->
                let (succeeded, message) =
                    try
                        let removal = ModInstaller.uninstall game exePath plan (scaled 0.0 0.33)

                        if not removal.Success then
                            (false, "Could not remove the previous install: " + removal.Message)
                        else
                            let outcome = ModInstaller.install game exePath plan target targetArch targetApi (scaled 0.33 0.67)
                            (outcome.Success, "Switched. " + outcome.Message)
                    with ex ->
                        (false, ex.Message)

                Dispatcher.UIThread.Post(fun () ->
                    this.IsInstalling <- false
                    this.InstallResultIsError <- not succeeded
                    this.InstallResultText <- message
                    this.InstallProgress <- (if succeeded then 100.0 else 0.0)
                    this.InstallStatusText <- ""

                    // The card badge is written from the manifest, which the
                    // run just changed.
                    match manageCard with
                    | Some c -> c.RefreshModBadge()
                    | None -> ()

                    this.AnalyzeManageTarget()))
            |> ignore

    member private this.RunModTask(isInstallAction: bool) =
        match manageCard with
        | None -> ()
        | Some card ->
            let game = card.Game
            let exePath = manageExePath

            this.InstallResultText <- ""
            this.InstallResultIsError <- false
            this.InstallProgress <- 0.0
            this.InstallStatusText <- if isInstallAction then "Starting..." else "Removing..."
            this.IsInstalling <- true

            let report: ModInstaller.Progress =
                fun text progress ->
                    Dispatcher.UIThread.Post(fun () ->
                        this.InstallStatusText <- text
                        this.InstallProgress <- progress * 100.0)

            // Folder locations come straight from the cached deep scan.
            let plan =
                manageAnalysis
                |> Option.map (fun a ->
                    { ModInstaller.InstallPlan.DlssDirs = a.DlssDirs
                      ModInstaller.InstallPlan.StreamlineDirs = a.StreamlineDirs })

            System.Threading.Tasks.Task.Run(fun () ->
                let (succeeded, message) =
                    try
                        let outcome =
                            if isInstallAction then ModInstaller.install game exePath plan installMode installArch optiApi report
                            else ModInstaller.uninstall game exePath plan report

                        (outcome.Success, outcome.Message)
                    with ex ->
                        (false, ex.Message)

                Dispatcher.UIThread.Post(fun () ->
                    this.IsInstalling <- false
                    this.InstallResultIsError <- not succeeded
                    this.InstallResultText <- message
                    this.InstallProgress <- (if succeeded then 100.0 else 0.0)
                    this.InstallStatusText <- ""

                    // The card badge is written from the manifest, which the
                    // run just changed.
                    match manageCard with
                    | Some c -> c.RefreshModBadge()
                    | None -> ()

                    this.AnalyzeManageTarget()))
            |> ignore

    member this.StartInstall() = this.RunModTask(true)
    member this.StartUninstall() = this.RunModTask(false)

    // ---------------------------------------------------------------------
    // UPDATES
    // ---------------------------------------------------------------------
    member this.AppVersionText = "v" + UpdateChecker.CurrentVersion
    member this.DownloadPageUrl = UpdateChecker.DownloadPageUrl

    /// Where the app points people who want to say thanks, and where the
    /// walkthroughs live. Both open in the system browser.
    // ---------------------------------------------------------------------
    // SUPPORT PROMPT
    // ---------------------------------------------------------------------
    member this.IsSupportPromptVisible
        with get () = isSupportPromptVisible
        and set value = this.SetProperty(&isSupportPromptVisible, value) |> ignore

    member this.DismissSupportPrompt() = this.IsSupportPromptVisible <- false

    member this.SupportPromptTitle = "Enjoying DLSS 5 MANAGER?"

    member this.SupportPromptText =
        "You have been using it for a while now. It is free and always will be - a tip on Ko-fi is what pays for the next release."

    member this.DonateUrl = "https://ko-fi.com/nodix"
    member this.TutorialsUrl = "https://www.youtube.com/@Nodix-Tech"

    // Authorship & rights, shown in the About card.
    member this.CreditsText = "Built by NODIX TECH · Published by Numidia Studios"
    member this.CopyrightText = sprintf "© %d Numidia Studios. All rights reserved." DateTime.Now.Year

    member this.IsCheckingUpdates
        with get () = isCheckingUpdates
        and set value =
            if this.SetProperty(&isCheckingUpdates, value) then
                this.RaisePropertyChanged("UpdateButtonText")
                this.RaisePropertyChanged("CanCheckUpdates")

    member this.CanCheckUpdates = not isCheckingUpdates

    member this.UpdateStatusText
        with get () = updateStatusText
        and set value =
            if this.SetProperty(&updateStatusText, value) then
                this.RaisePropertyChanged("HasUpdateStatus")

    member this.HasUpdateStatus = not (String.IsNullOrWhiteSpace(updateStatusText))

    member this.HasUpdateAvailable
        with get () = hasUpdateAvailable
        and set value =
            if this.SetProperty(&hasUpdateAvailable, value) then
                this.RaisePropertyChanged("UpdateButtonText")
                this.RaisePropertyChanged("UpdateStatusBrush")
                this.RaisePropertyChanged("UpdateBadgeText")

    /// The header pill, shown only once a newer build is confirmed.
    member this.UpdateBadgeText =
        if String.IsNullOrWhiteSpace(latestVersionFound) then "UPDATE AVAILABLE"
        else "UPDATE v" + latestVersionFound

    member this.UpdateButtonText =
        if isCheckingUpdates then "CHECKING..."
        elif hasUpdateAvailable then "DOWNLOAD UPDATE"
        else "CHECK FOR UPDATES"

    member this.UpdateStatusBrush: IBrush =
        if hasUpdateAvailable then SolidColorBrush(Color.Parse("#86EFAC")) :> IBrush
        else SolidColorBrush(Color.Parse("#94A3B8")) :> IBrush

    member this.CheckForUpdates() =
        if not isCheckingUpdates then
            this.IsCheckingUpdates <- true
            this.UpdateStatusText <- "Contacting update server..."

            async {
                let! result = UpdateChecker.check ()

                Dispatcher.UIThread.Post(fun () ->
                    this.IsCheckingUpdates <- false
                    latestVersionFound <- result.LatestVersion
                    this.HasUpdateAvailable <- result.HasUpdate
                    this.UpdateStatusText <- result.Message)
            }
            |> Async.Start

    /// Runs once on every launch, quietly. Only a confirmed newer build says
    /// anything - a failed check must never greet the user with an error.
    member this.CheckForUpdatesOnStartup() =
        async {
            let! result = UpdateChecker.check ()

            Dispatcher.UIThread.Post(fun () ->
                if result.HasUpdate then
                    latestVersionFound <- result.LatestVersion
                    this.HasUpdateAvailable <- true
                    this.UpdateStatusText <- result.Message)
        }
        |> Async.Start

    member this.AddCustomFolder(folderPath: string) =
        if not (String.IsNullOrWhiteSpace(folderPath)) && System.IO.Directory.Exists(folderPath) then
            this.IsScanning <- true
            this.ScanStatusText <- sprintf "Inspecting %s..." (System.IO.Path.GetFileName(folderPath.TrimEnd('\\', '/')))

            System.Threading.Tasks.Task.Run(fun () ->
                let scanned =
                    try
                        GameScanner.scanCustomFolder folderPath
                    with ex ->
                        printfn "[DLSS5Manager Error] Folder add failed: %s" (ex.ToString())
                        []

                Dispatcher.UIThread.Post(fun () ->
                    this.IsScanning <- false
                    let added = ResizeArray<GameItem>()

                    for g in scanned do
                        let isAlreadyAdded =
                            allGames
                            |> Seq.exists (fun c ->
                                String.Equals(
                                    c.Game.InstallDirectory,
                                    g.InstallDirectory,
                                    StringComparison.OrdinalIgnoreCase
                                ))

                        if not isAlreadyAdded then
                            allGames.Add(GameCardViewModel(g))
                            added.Add(g)

                    totalGamesCount <- allGames.Count
                    this.RaisePropertyChanged("AllGamesCount")
                    this.RaisePropertyChanged("TotalGamesText")
                    filterGamesList ()
                    this.RaisePropertyChanged("HasGames")
                    this.ScanStatusText <- sprintf "%d Games Ready" totalGamesCount

                    try
                        GameScanner.saveGamesToCache [ for c in allGames -> c.Game ]
                    with _ ->
                        ()

                    this.AnalyzePendingGames(List.ofSeq added)))
            |> ignore

    // ---------------------------------------------------------------------
    // EMULATORS
    // ---------------------------------------------------------------------
    member this.Emulators = filteredEmulators
    member this.HasEmulators = filteredEmulators.Count > 0
    member this.AllEmulatorsCount = allEmulators.Count

    /// Emulators are only ever added by hand, one executable at a time. Same
    /// inspection as a manual game add: the picked file and nothing else.
    member this.AddEmulatorExecutable(exePath: string) =
        if not (String.IsNullOrWhiteSpace(exePath)) && System.IO.File.Exists(exePath) then
            let fileName = System.IO.Path.GetFileName(exePath)
            this.IsScanning <- true
            this.ScanStatusText <- sprintf "Inspecting %s..." fileName

            System.Threading.Tasks.Task.Run(fun () ->
                let built =
                    try
                        let folderPath = System.IO.Path.GetDirectoryName(exePath)
                        let rawName = System.IO.Path.GetFileNameWithoutExtension(exePath)

                        let item =
                            { GameScanner.createCustomGameItem rawName folderPath exePath with
                                LauncherTypeName = "EMULATOR" }

                        AnalysisStore.refreshExecutableOnly item |> ignore
                        Some item
                    with ex ->
                        printfn "[DLSS5Manager Error] Emulator add failed: %s" (ex.ToString())
                        None

                Dispatcher.UIThread.Post(fun () ->
                    this.IsScanning <- false

                    match built with
                    | Some item ->
                        let alreadyThere =
                            allEmulators
                            |> Seq.exists (fun c ->
                                String.Equals(c.ExecutablePath, exePath, StringComparison.OrdinalIgnoreCase))

                        if not alreadyThere then allEmulators.Add(GameCardViewModel(item))

                        this.RaisePropertyChanged("AllEmulatorsCount")
                        filterEmulatorsList ()
                        this.RaisePropertyChanged("HasEmulators")

                        try
                            GameScanner.saveEmulatorsToCache [ for c in allEmulators -> c.Game ]
                        with _ ->
                            ()

                        this.ScanStatusText <- sprintf "%d Emulators Ready" allEmulators.Count
                    | None -> this.ScanStatusText <- sprintf "Could not read %s" fileName))
            |> ignore

    member this.RemoveEmulator(card: GameCardViewModel) =
        if not (isNull (box card)) then
            if isManageOpen && (match manageCard with Some c -> Object.ReferenceEquals(c, card) | None -> false) then
                this.CloseManage()

            allEmulators.Remove(card) |> ignore
            filteredEmulators.Remove(card) |> ignore
            this.RaisePropertyChanged("AllEmulatorsCount")
            this.RaisePropertyChanged("HasEmulators")

            try
                AnalysisStore.remove card.Game
                AnalysisStore.save ()
                GameScanner.saveEmulatorsToCache [ for c in allEmulators -> c.Game ]
            with _ ->
                ()

    /// Right-click on a card: put the user's own artwork on it. The picked file
    /// is copied into our poster cache first, so moving or deleting the
    /// original later cannot leave the card blank.
    member this.SetGameCover(card: GameCardViewModel, sourcePath: string) =
        if not (isNull (box card))
           && not (String.IsNullOrWhiteSpace(sourcePath))
           && System.IO.File.Exists(sourcePath) then
            try
                let dir =
                    System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "DLSS5Manager",
                        "Cache",
                        "Posters"
                    )

                System.IO.Directory.CreateDirectory(dir) |> ignore

                // Named after the game, so re-picking replaces the old file
                // instead of piling copies up in the cache.
                let key =
                    let raw =
                        if String.IsNullOrWhiteSpace(card.Game.AppId) then card.Game.Title else card.Game.AppId

                    Text.RegularExpressions.Regex.Replace(raw, @"[^A-Za-z0-9_\-]", "_")

                let extension =
                    let e = System.IO.Path.GetExtension(sourcePath)
                    if String.IsNullOrWhiteSpace(e) then ".png" else e

                let target = System.IO.Path.Combine(dir, "custom_" + key + extension)

                // Clear out a previous pick that used a different extension.
                for stale in System.IO.Directory.GetFiles(dir, "custom_" + key + ".*") do
                    if not (String.Equals(stale, target, StringComparison.OrdinalIgnoreCase)) then
                        try System.IO.File.Delete(stale) with _ -> ()

                System.IO.File.Copy(sourcePath, target, true)
                card.SetBanner(target)

                GameScanner.saveGamesToCache [ for c in allGames -> c.Game ]
            with _ ->
                ()

    /// Right-click on a card: drop the title from the library without touching
    /// anything on disk. Its cached analysis goes with it.
    member this.RemoveGame(card: GameCardViewModel) =
        if not (isNull (box card)) then
            if isManageOpen && (match manageCard with Some c -> Object.ReferenceEquals(c, card) | None -> false) then
                this.CloseManage()

            allGames.Remove(card) |> ignore
            filteredGames.Remove(card) |> ignore
            totalGamesCount <- allGames.Count
            this.RaisePropertyChanged("AllGamesCount")
            this.RaisePropertyChanged("TotalGamesText")
            this.RaisePropertyChanged("HasGames")

            try
                AnalysisStore.remove card.Game
                AnalysisStore.save ()
                GameScanner.saveGamesToCache [ for c in allGames -> c.Game ]
            with _ ->
                ()

    // ---------------------------------------------------------------------
    // CUSTOM MOD PAYLOAD FILES
    // ---------------------------------------------------------------------
    member this.FeedAddonName = ModInstaller.feedAddonName
    member this.FeedAddon32Name = ModInstaller.feedAddon32Name
    member this.FeedAddon32State = ModInstaller.Payload.describe ModInstaller.feedAddon32Name
    member this.RenodxAddonName = ModInstaller.renodxAddonName
    member this.DlssnrFileName = GameAnalyzer.dlssnrFileName

    member this.FeedAddonState = ModInstaller.Payload.describe ModInstaller.feedAddonName
    member this.RenodxAddonState = ModInstaller.Payload.describe ModInstaller.renodxAddonName
    member this.DlssnrState = ModInstaller.Payload.describe GameAnalyzer.dlssnrFileName

    member this.PayloadStatusText
        with get () = payloadStatusText
        and set value =
            if this.SetProperty(&payloadStatusText, value) then
                this.RaisePropertyChanged("HasPayloadStatus")

    member this.HasPayloadStatus = not (String.IsNullOrWhiteSpace(payloadStatusText))

    member private this.RaisePayloadState() =
        this.RaisePropertyChanged("FeedAddonState")
        this.RaisePropertyChanged("FeedAddon32State")
        this.RaisePropertyChanged("RenodxAddonState")
        this.RaisePropertyChanged("DlssnrState")
        for row in payloadRows do row.Refresh()

    member this.ReplacePayloadFile(fileName: string, sourcePath: string) =
        let (ok, message) = ModInstaller.Payload.replaceWith fileName sourcePath
        this.PayloadStatusText <- message
        if ok then this.RaisePayloadState()

    member this.RestorePayloadFile(fileName: string) =
        let (ok, message) = ModInstaller.Payload.restore fileName
        this.PayloadStatusText <- message
        if ok then this.RaisePayloadState()

    // ---------------------------------------------------------------------
    // PAYLOAD ROWS, RESHADE SETUP, OPTISCALER
    // ---------------------------------------------------------------------
    /// The swappable payload files, each with its own on/off switch.
    member this.PayloadRows = payloadRows

    member this.ReShadeSetupState = ModInstaller.Payload.describeReShade ()

    /// Off means no route runs the ReShade installer. A game that already has
    /// ReShade keeps it; one that does not simply goes without.
    member this.IsReShadeEnabled
        with get () = ExtrasStore.isPayloadEnabled ModInstaller.reShadeSetupKey
        and set value =
            if ExtrasStore.isPayloadEnabled ModInstaller.reShadeSetupKey <> value then
                ExtrasStore.setPayloadEnabled ModInstaller.reShadeSetupKey value
                this.RaisePropertyChanged("IsReShadeEnabled")
    member this.OptiScalerState = ModInstaller.Payload.describeOptiScaler ()

    member this.ReplaceReShadeSetup(sourcePath: string) =
        let (ok, message) = ModInstaller.Payload.replaceReShade sourcePath
        this.PayloadStatusText <- message
        if ok then this.RaisePropertyChanged("ReShadeSetupState")

    member this.RestoreReShadeSetup() =
        let (ok, message) = ModInstaller.Payload.restoreReShade ()
        this.PayloadStatusText <- message
        if ok then this.RaisePropertyChanged("ReShadeSetupState")

    /// The folder has to be the one OptiScaler was extracted into - the
    /// service checks for OptiScaler.dll and refuses anything else.
    member this.ReplaceOptiScaler(sourceDir: string) =
        let (ok, message) = ModInstaller.Payload.replaceOptiScaler sourceDir
        this.PayloadStatusText <- message
        if ok then this.RaisePropertyChanged("OptiScalerState")

    /// The AMD payload: replaceable, never switchable - the route cannot
    /// install without it.
    member this.AmdPayloadState = ModInstaller.Payload.describeAmd ()

    member this.ReplaceAmdPayload(sourcePaths: string list) =
        let (ok, message) = ModInstaller.Payload.replaceAmdFiles sourcePaths
        this.PayloadStatusText <- message
        if ok then this.RaisePropertyChanged("AmdPayloadState")

    member this.RestoreAmdPayload() =
        let (ok, message) = ModInstaller.Payload.restoreAmd ()
        this.PayloadStatusText <- message
        if ok then this.RaisePropertyChanged("AmdPayloadState")

    member this.RestoreOptiScaler() =
        let (ok, message) = ModInstaller.Payload.restoreOptiScaler ()
        this.PayloadStatusText <- message
        if ok then this.RaisePropertyChanged("OptiScalerState")

    // ---------------------------------------------------------------------
    // USER EXTRAS
    // ---------------------------------------------------------------------
    member this.Extras = extraRows
    member this.HasExtras = extraRows.Count > 0

    member private this.ReloadExtras() =
        extraRows.Clear()

        for (key, items) in ExtrasStore.groups () do
            extraRows.Add(ExtraRowViewModel(key, items))

        this.RaisePropertyChanged("HasExtras")

    /// Everything picked in one go becomes a single entry, so a folder's worth
    /// of files does not turn into a wall of identical rows.
    member this.AddExtras(picks: (string * bool) list) =
        let added = ExtrasStore.addBatch picks

        if added > 0 then
            this.ReloadExtras()

            this.PayloadStatusText <-
                if added = 1 then "1 item will be installed with every mod from now on."
                else sprintf "%d items will be installed together with every mod from now on." added
        else
            this.PayloadStatusText <- "Those are already in the list."

    member this.RemoveExtra(row: ExtraRowViewModel) =
        if not (isNull (box row)) then
            ExtrasStore.removeGroup row.Key
            this.ReloadExtras()
            this.PayloadStatusText <- row.Title + " removed from the extras."
