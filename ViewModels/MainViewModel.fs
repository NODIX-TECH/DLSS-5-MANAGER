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
    member _.IsOptiNeuralMode = has "optiscaler-neural"
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
                    | "optiscaler-neural" -> "OptiScaler neural"
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
            [ "IsAllModes"; "IsOptiDx12Mode"; "IsOptiVulkanMode"; "IsOptiNeuralMode"; "IsDx12Mode"
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
    let mutable isSelected = false
    let mutable thumbnail: Imaging.Bitmap = null
    let mutable thumbnailTried = false

    member _.Key = key

    member this.Display
        with get () = display
        and set value = this.SetProperty(&display, value) |> ignore

    /// The tile that is the window's background right now.
    member this.IsSelected
        with get () = isSelected
        and set value = this.SetProperty(&isSelected, value) |> ignore

    /// The last tile of the background picker: the user's own picture rather
    /// than an atmosphere. Its key is never saved as an atmosphere.
    member _.IsCustom = key = AtmosphereOption.CustomKey

    /// Decoded the first time a tile asks for it, which is the first time the
    /// Visual Atmosphere section is unfolded - never at startup. The custom
    /// tile has no file of its own; the view model hands it one.
    member _.Thumbnail: Imaging.Bitmap =
        if not thumbnailTried && key <> AtmosphereOption.CustomKey then
            thumbnailTried <- true

            thumbnail <-
                try
                    let path = BackgroundStore.themeThumbnail key
                    if IO.File.Exists(path) then new Imaging.Bitmap(path) else null
                with _ ->
                    null

        thumbnail

    member this.HasThumbnail = not (isNull this.Thumbnail)

    /// The small x on the custom tile, once it holds a picture.
    member this.CanRemove = key = AtmosphereOption.CustomKey && this.HasThumbnail

    member this.SetThumbnail(bitmap: Imaging.Bitmap) =
        thumbnailTried <- true
        thumbnail <- bitmap
        this.RaisePropertyChanged("Thumbnail")
        this.RaisePropertyChanged("HasThumbnail")
        this.RaisePropertyChanged("CanRemove")

    static member CustomKey = "Custom"

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

    /// The six-second grace period before an automatic scan starts.
    let autoScanTimer = DispatcherTimer(Interval = TimeSpan.FromSeconds(1.0))
    let mutable autoScanSecondsLeft = 6
    let mutable isAutoScanPending = false
    let mutable isDraggingCard = false
    let mutable draggedCard: GameCardViewModel option = None
    let mutable isSidebarLayout = false
    let mutable isSettingsOpen = false
    let mutable totalGamesCount = 0

    /// The community section. Built with the window so the tab can switch to it
    /// instantly; it does not touch the network until the tab is opened.
    let community = CommunityViewModel()
    let screen = ScreenViewModel()

    /// PULSE, the chat inside the community section. Owned here rather than by
    /// the community view-model because it has to know about window focus -
    /// a minimised app must not poll - and about which section is on screen.
    let pulse = PulseViewModel()

    /// The private chat, the fourth tab. One thread per person with the
    /// developer; a player sees only their own, which is the whole point.
    let dm = DmViewModel()

    /// The gallery, the third tab of the community section. Like the chat it
    /// is built with the window and touches the network only once opened.
    let gallery = GalleryViewModel()

    /// The Downloads sheet (`CloudAssets`). It opens by itself once, on the
    /// first launch that finds something missing; after it has been put away
    /// the download button at the top is the way back to it.
    let assets = SetupViewModel()
    let mutable isAssetsOpen = assets.HasMissing && not (CloudAssets.wasPostponed ())
    let mutable downloadsHint = false

    /// Which of the three tabs of the community section is showing:
    /// "games", "chat" or "gallery".
    ///
    /// This was a bool while there were only two. The members below keep their
    /// old names and meanings so every binding in the window still reads the
    /// same, and no combination of tabs can be half-set.
    let mutable communityTab = "games"

    // ---- Manage sheet state ---------------------------------------------
    let mutable isManageOpen = false
    let mutable manageCard: GameCardViewModel option = None

    // ---- What the community found, for the game whose sheet is open --------
    //
    // Reading is public: the counts come from the same endpoint the grid uses
    // and need no name. Posting and replying are where the community asks who
    // you are, which is what the button into it leads to.
    let mutable glanceOpen = false
    let mutable glanceBusy = false
    let mutable glanceFor = ""                              // the title it was fetched for
    let mutable glanceGame: CommunityApi.GameDto option = None
    let mutable glanceMessage = ""

    /// What the community said about the last thirty games, by title.
    ///
    /// In memory only - nothing is written to disk for it: the answers are
    /// small, they go stale, and a person who opens six games in a row should
    /// not wait for six round trips. The order of arrival is kept beside it so
    /// the oldest can go when the thirty-first arrives. `None` is remembered
    /// too: "the community does not have this game" is an answer, and asking
    /// again every time the panel opens would be the same waste.
    let glanceCache = System.Collections.Generic.Dictionary<string, CommunityApi.GameDto option>(StringComparer.OrdinalIgnoreCase)
    let glanceOrder = System.Collections.Generic.Queue<string>()
    let mutable manageAnalysis: AnalysisStore.GameAnalysis option = None
    let mutable manageTitle = ""
    /// The anti-cheat the open game carries (AntiCheat.detect): None when
    /// none was found, Some "" when something is only named like one.
    let mutable manageAntiCheat: string option = None
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

    /// OptiScaler only: which API the game renders with, or the neural
    /// upstream build of OptiScaler itself.
    let mutable optiApi = ModInstaller.OptiDx12

    /// Neural upstream on the DX12 / DX11 / DX9 and AMD routes: one extra
    /// add-on next to the game. Off unless the user asks for it.
    let mutable useNeuralAddon = false

    /// The two RenoDX options on the 64-bit DX12 / DX11 / DX9 routes: the MFG
    /// unlock add-on beside RenoDX, and the Multipass build of RenoDX in place
    /// of the ordinary one. Both off unless the user asks.
    let mutable useMfgUnlock = false
    let mutable useMultipass = false

    /// Deep-fried chicken in RenoDX's place, on the same routes. Turning it on
    /// turns Multipass off - that option only picks a RenoDX build.
    let mutable useDeepFried = false

    /// The route and build recorded in the manifest for the open game, "" when
    /// this app did not install it. Drives the Install / Switch / Remove button.
    let mutable installedRoute = ""
    let mutable installedArch = ""
    let mutable installedApi = ""
    let mutable installedNeural = false
    let mutable installedMfgUnlock = false
    let mutable installedMultipass = false
    let mutable installedDeepFried = false

    /// The deployment log is folded away by default. It is the only thing in
    /// the sheet long enough to stretch it past the screen, and it is reference
    /// material - the headline above it already says how the run went.
    let mutable isInstallResultExpanded = false

    /// True once the user has picked a route in the open sheet. Detection then
    /// stops overriding it - see SetInstallMode.
    let mutable routeChosenByUser = false

    /// True when the sheet opened on a game that already had an install.
    ///
    /// Automatic routing is for a game nobody has modded yet: opening one of
    /// those on the option that suits it is the whole point. A game that has
    /// been installed already has an answer, and removing that install must not
    /// turn it back into a question - so this stays true for the life of the
    /// sheet even after the manifest is gone.
    let mutable sheetOpenedInstalled = false

    /// The detected-target readout starts folded away: it is reference
    /// information, and the sheet is about choosing and installing.
    let mutable isTargetDetailsOpen = false

    // ---- What the game itself turned out to be ---------------------------
    /// Every renderer the title can use, newest DirectX first. A game that
    /// offers two - DirectX 12 *and* Vulkan - carries both here.
    let mutable detectedApis: string[] = [||]
    /// The first of those: the one the routing decides on.
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
              ModInstaller.neuralAddonName
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

    let mutable selectedColorAtmosphere = "Emerald Horizon"

    /// No longer drawn - the pictures replaced the shapes - but still saved,
    /// so a settings file keeps what it held.
    let mutable selectedGeometricMotif = "Orbital Spheres"

    /// The picture behind the window, the user's own picture when there is one,
    /// and how bright it is allowed to be. See `BackgroundStore`.
    let mutable background = BackgroundStore.load ()
    let mutable backgroundImage: Imaging.Bitmap = null

    /// The mean luminance of the picture on screen, which the dim layer
    /// compensates for - see `BackgroundStore.exposure`.
    let mutable backgroundLuminance = BackgroundStore.ReferenceLuminance
    /// The mean luminance of the picture's brightest quarter - what the glare is judged on.
    let mutable backgroundBright = BackgroundStore.ReferenceLuminance

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

    /// The background picker in Settings: every atmosphere, then the user's
    /// own picture as the last tile.
    let customTile = AtmosphereOption(AtmosphereOption.CustomKey)

    let backgroundTiles =
        ObservableCollection<AtmosphereOption>(Seq.append atmosphereOptions [ customTile ])

    /// Rewrites the labels in place for the language now in use.
    let relabelAtmospheres (strings: Localization.Strings) =
        let names = strings.AtmosphereNames

        atmosphereOptions
        |> Seq.iteri (fun i option -> if i < names.Length then option.Display <- names.[i])

        customTile.Display <- strings.BackgroundCustom

    /// Only one tile is lit: the atmosphere on screen, unless the user's own
    /// picture has taken its place.
    let markSelectedTile () =
        for option in atmosphereOptions do
            option.IsSelected <- not background.UseCustom && option.Key = selectedColorAtmosphere

        customTile.IsSelected <- background.UseCustom

    /// A picture from disk, or null when it cannot be read. Anything wider
    /// than `maxWidth` is decoded straight down to it, so a 6000 px photo does
    /// not sit in memory at full size behind the window.
    let decodeImage (path: string) (maxWidth: int) : Imaging.Bitmap =
        try
            if String.IsNullOrWhiteSpace(path) || not (IO.File.Exists(path)) then
                null
            else
                let full =
                    use stream = IO.File.OpenRead(path)
                    new Imaging.Bitmap(stream)

                if full.PixelSize.Width <= maxWidth then
                    full
                else
                    full.Dispose()
                    use stream = IO.File.OpenRead(path)
                    Imaging.Bitmap.DecodeToWidth(stream, maxWidth)
        with _ ->
            null

    /// The picture for whatever is chosen now. The user's own wins while it
    /// can be read; one that cannot falls back to the atmosphere's.
    let loadBackground () =
        let custom =
            if background.UseCustom then decodeImage background.CustomFile 2560 else null

        if isNull custom then
            backgroundImage <- decodeImage (BackgroundStore.themeImage selectedColorAtmosphere) 2560

            // The small copy measures the same and reads in a few milliseconds.
            let mean, bright = BackgroundStore.measureLights (BackgroundStore.themeThumbnail selectedColorAtmosphere)
            backgroundLuminance <- mean
            backgroundBright <- bright
        else
            backgroundImage <- custom

            // Measured once and kept; a settings file from before the
            // measurement existed gets it on the first launch that needs it.
            if background.CustomLuminance <= 0.0 || background.CustomBright <= 0.0 then
                let mean, bright = BackgroundStore.measureLights background.CustomFile
                background <- { background with CustomLuminance = mean; CustomBright = bright }

                BackgroundStore.save background

            backgroundLuminance <- background.CustomLuminance
            backgroundBright <- background.CustomBright

    /// The glare last applied, so an unchanged picture does not rebuild the
    /// brushes on every brightness step that leaves it where it was.
    let mutable appliedGlare = -1.0

    /// Glare - see `BackgroundStore.glare`. The brushes are declared in
    /// App.axaml and used through DynamicResource; this replaces them with ones
    /// matched to the picture on screen. Over a dark picture they come out as
    /// the surfaces exactly as the XAML always had them.
    let applyGlare () =
        match Application.Current with
        | null -> ()
        | app ->
            let g = BackgroundStore.glare background.Brightness backgroundLuminance backgroundBright

            if abs (g - appliedGlare) > 0.002 then
                appliedGlare <- g

                // A surface's own glass laid over the app's dark glass
                // (#0B1014), that tint growing with the glare.
                let over (baseColor: string) (tint: float) =
                    let c = Color.Parse(baseColor)
                    let aw = float c.A / 255.0
                    let a = aw + (1.0 - aw) * tint

                    if a <= 0.0 then
                        Colors.Transparent
                    else
                        let mix (w: byte) (d: float) =
                            byte (Math.Round((aw * float w + (1.0 - aw) * tint * d) / a))

                        Color.FromArgb(byte (Math.Round(a * 255.0)), mix c.R 11.0, mix c.G 16.0, mix c.B 20.0)

                let res = app.Resources
                res.["GlareVeilBrush"] <- SolidColorBrush(Color.FromArgb(byte (Math.Round(0.26 * g * 255.0)), 0uy, 0uy, 0uy))
                res.["GlareCardBrush"] <- SolidColorBrush(over "#08FFFFFF" (0.62 * g))
                res.["GlareHeaderBrush"] <- SolidColorBrush(over "#10FFFFFF" (0.62 * g))
                res.["GlareScreenCardBrush"] <- SolidColorBrush(over "#10000000" (0.62 * g))
                res.["GlareBackingBrush"] <- SolidColorBrush(over "#00000000" (0.55 * g))

                let capsule =
                    LinearGradientBrush(
                        StartPoint = RelativePoint(0.0, 0.0, RelativeUnit.Relative),
                        EndPoint = RelativePoint(1.0, 1.0, RelativeUnit.Relative)
                    )

                capsule.GradientStops.Add(GradientStop(over "#30FFFFFF" (0.55 * g), 0.0))
                capsule.GradientStops.Add(GradientStop(over "#12FFFFFF" (0.55 * g), 0.5))
                capsule.GradientStops.Add(GradientStop(over "#20FFFFFF" (0.55 * g), 1.0))
                res.["GlareCapsuleBrush"] <- capsule

    let mutable bgBaseBrush: IBrush = SolidColorBrush(Color.Parse("#080D0A"))
    let mutable bgVignetteBrush: IBrush = null
    let mutable motifAccentBrush: IBrush = SolidColorBrush(Color.Parse("#35D22B"))

    /// Background animations run only while the window is in front.
    let mutable isWindowActive = true

    /// Performance mode: no moving background, no card hover animation.
    let mutable isPerformanceMode = false

    /// The in-game overlay, and the look it wears. Deployed with an install on
    /// the routes that can host it - see `ModInstaller.overlaySupported`.
    /// On or off is chosen per game, in its Manage sheet: `isOverlayEnabled`
    /// is the open sheet's choice, `overlayDefault` the last choice made in
    /// any sheet - what a game this app has not installed yet opens on, and
    /// what `OverlayDisabled` saves (inverted).
    let mutable isOverlayEnabled = true
    let mutable overlayDefault = true
    let mutable overlayTheme = ModInstaller.overlayThemes.[0]
    let mutable overlayHotkey = ModInstaller.overlayHotkeys.[0]

    /// The key that opens ReShade in game, and the line under the key chips.
    let mutable reshadeKey = ModInstaller.savedReShadeKey ()
    let mutable keysStatus = ""

    /// Manage sheet -> DLL NAME: folded away until asked for. The proxy names
    /// are what the open game's install uses right now ("" = none of that kind).
    let mutable dllNamesOpen = false
    let mutable optiProxyName = ""
    let mutable reshadeProxyName = ""
    let mutable optiProxyPick = ModInstaller.optiScalerProxyNames.[0]
    let mutable reshadeProxyPick = ModInstaller.reShadeProxyNames.[0]
    let mutable optiCustomName = ""
    let mutable reshadeCustomName = ""
    let mutable dllNameStatus = ""
    let mutable dllNameIsError = false
    /// The names come from ModInstaller's cache, or from a read of the disk in
    /// the background - never on the UI thread. Until the first answer for the
    /// open game arrives the card stays down.
    let mutable dllNamesKnown = false
    let mutable dllFolderNames: Set<string> = Set.empty
    let mutable optiChoices: string[] = ModInstaller.optiScalerProxyNames
    let mutable reshadeChoices: string[] = ModInstaller.reShadeProxyNames
    let mutable dllScanGeneration = 0
    let mutable dllBusy = false

    /// The key that opens OptiScaler's own menu. Kept like ReShade's - one
    /// choice for every install, not one per game.
    let mutable optiMenuKey = ModInstaller.savedOptiMenuKey ()
    let mutable optiKeyStatus = ""

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
            bgVignetteBrush <- createVignetteBrush("#5004030A")
            motifAccentBrush <- SolidColorBrush(Color.Parse("#38BDF8"))
        | "Supernova Flare" ->
            bgBaseBrush <- SolidColorBrush(Color.Parse("#140A0C"))
            bgVignetteBrush <- createVignetteBrush("#550E0305")
            motifAccentBrush <- SolidColorBrush(Color.Parse("#F43F5E"))
        | "Cyber Nebula" ->
            bgBaseBrush <- SolidColorBrush(Color.Parse("#0D0B16"))
            bgVignetteBrush <- createVignetteBrush("#5007030F")
            motifAccentBrush <- SolidColorBrush(Color.Parse("#C084FC"))
        | "Emerald Horizon" ->
            bgBaseBrush <- SolidColorBrush(Color.Parse("#081010"))
            bgVignetteBrush <- createVignetteBrush("#50020808")
            motifAccentBrush <- SolidColorBrush(Color.Parse("#34D399"))
        | "Midnight Titanium" ->
            bgBaseBrush <- SolidColorBrush(Color.Parse("#0A0A0C"))
            bgVignetteBrush <- createVignetteBrush("#50000000")
            motifAccentBrush <- SolidColorBrush(Color.Parse("#E2E8F0"))
        | "Frost Glacier" ->
            bgBaseBrush <- SolidColorBrush(Color.Parse("#091017"))
            bgVignetteBrush <- createVignetteBrush("#50030A12")
            motifAccentBrush <- SolidColorBrush(Color.Parse("#38BDF8"))
        | "Eclipse Crimson" ->
            bgBaseBrush <- SolidColorBrush(Color.Parse("#12070A"))
            bgVignetteBrush <- createVignetteBrush("#550F0206")
            motifAccentBrush <- SolidColorBrush(Color.Parse("#E11D48"))
        | "Deep Astral" ->
            bgBaseBrush <- SolidColorBrush(Color.Parse("#070B14"))
            bgVignetteBrush <- createVignetteBrush("#5002060D")
            motifAccentBrush <- SolidColorBrush(Color.Parse("#60A5FA"))
        | _ ->
            // Neon Emerald: signature DLSS 5 MANAGER atmosphere (matches the logo mark)
            selectedColorAtmosphere <- "Neon Emerald"
            bgBaseBrush <- SolidColorBrush(Color.Parse("#080D0A"))
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
        loadBackground ()
        applyGlare ()
        markSelectedTile ()

        if not (String.IsNullOrWhiteSpace(background.CustomFile)) then
            customTile.SetThumbnail(decodeImage background.CustomFile 320)

        languageCode <- if Localization.isKnownLanguage settings.Language then settings.Language else "en"
        loc <- Localization.Strings(languageCode)
        Localization.current <- loc
        relabelAtmospheres loc

        // A download finishing changes what the title bar and the Manage
        // sheet may offer: the button at the top goes once nothing is
        // missing, and the overlay option unlocks the moment its add-on lands.
        assets.PropertyChanged.Add(fun args ->
            match args.PropertyName with
            | "HasMissing" -> this.RaisePropertyChanged("HasMissingAssets")
            | "IsAnyBusy" -> this.RaisePropertyChanged("IsAssetsBusy")
            | "IsOverlayReady" ->
                this.RaisePropertyChanged("IsOverlayDownloaded")
                this.RaisePropertyChanged("IsOverlayLocked")
                this.RaisePropertyChanged("IsOverlayKeyVisible")
            | _ -> ())

        isAmdMode <- settings.AmdMode
        screen.SetAmdBlocked(isAmdMode)
        isPerformanceMode <- settings.PerformanceMode
        overlayDefault <- not settings.OverlayDisabled
        isOverlayEnabled <- overlayDefault

        overlayTheme <-
            if ModInstaller.overlayThemes |> Array.exists (fun t -> t = settings.OverlayTheme) then
                settings.OverlayTheme
            else
                ModInstaller.overlayThemes.[0]

        overlayHotkey <-
            if ModInstaller.overlayHotkeys |> Array.exists (fun k -> k = settings.OverlayHotkey) then
                settings.OverlayHotkey
            else
                ModInstaller.overlayHotkeys.[0]

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
                      PerformanceMode = isPerformanceMode
                      OverlayDisabled = not overlayDefault
                      OverlayTheme = overlayTheme
                      OverlayHotkey = overlayHotkey })

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
            //    window is never blank, and the real scan replaces them - but
            //    only after the grace period below, so a huge library can still
            //    be told not to bother.
            GameScanner.writeCacheVersion UpdateChecker.CurrentVersion
            this.ArmAutoScan()
        elif cached.Length > 0 then
            // Top up the deep-scan cache for anything added before this feature
            // existed. Does nothing (and shows nothing) when everything is cached.
            this.AnalyzePendingGames(cached)
        else
            // First run / empty cache: full library scan, after the same grace
            // period - an empty cache is exactly when someone with a very large
            // library is most likely to want to stop it.
            this.ArmAutoScan()

        // 3. Look for a newer build every launch. It runs off the UI thread and
        //    stays silent unless there really is one, so startup is unaffected.
        this.CheckForUpdatesOnStartup()

    // ---------------------------------------------------------------------
    // THEME / ATMOSPHERE
    // ---------------------------------------------------------------------
    member this.BgBaseBrush = bgBaseBrush
    member this.BgVignetteBrush = bgVignetteBrush
    member this.MotifAccentBrush = motifAccentBrush

    /// The picture behind everything. Null only when the file cannot be read,
    /// and then the atmosphere's base colour is what shows.
    member this.BackgroundImage = backgroundImage

    /// 15 to 100, as the Settings slider shows it.
    member this.BackgroundBrightness
        with get () = float background.Brightness
        and set (value: float) =
            let rounded =
                max BackgroundStore.MinBrightness (min BackgroundStore.MaxBrightness (int (Math.Round value)))

            if rounded <> background.Brightness then
                background <- { background with Brightness = rounded }
                BackgroundStore.save background
                applyGlare ()
                this.RaisePropertyChanged("BackgroundBrightness")
                this.RaisePropertyChanged("BackgroundDimOpacity")
                this.RaisePropertyChanged("BackgroundBrightnessText")

    /// A black layer over the picture: the lower the brightness, and the
    /// brighter the picture itself, the more of it there is.
    member this.BackgroundDimOpacity =
        1.0 - BackgroundStore.exposure background.Brightness backgroundLuminance

    member this.BackgroundBrightnessText = sprintf "%d%%" background.Brightness

    /// Every tile of the picker, the user's own picture last.
    member this.BackgroundTiles = backgroundTiles

    member this.HasCustomBackground = customTile.HasThumbnail
    member this.IsCustomBackgroundActive = background.UseCustom

    member private this.RaiseBackground() =
        markSelectedTile ()
        applyGlare ()
        this.RaisePropertyChanged("BackgroundImage")
        this.RaisePropertyChanged("BackgroundDimOpacity")
        this.RaisePropertyChanged("HasCustomBackground")
        this.RaisePropertyChanged("IsCustomBackgroundActive")

    /// The user's own copy is decoded small for its tile, once, and again
    /// only when they pick a different picture.
    member private this.RefreshCustomThumbnail() =
        customTile.SetThumbnail(
            if String.IsNullOrWhiteSpace(background.CustomFile) then null
            else decodeImage background.CustomFile 320
        )

    /// A tile in Settings: that atmosphere, with its picture. The custom
    /// tile goes through `UseCustomBackground` / `SetCustomBackground` instead.
    member this.ChooseAtmosphere(option: AtmosphereOption) =
        if not (isNull (box option)) && not option.IsCustom then
            let index = atmosphereKeys |> Array.tryFindIndex ((=) option.Key)

            if background.UseCustom then
                background <- { background with UseCustom = false }
                BackgroundStore.save background

                // Same atmosphere as before: the index setter below will see
                // nothing to change, so the picture is swapped back here.
                if option.Key = selectedColorAtmosphere then
                    loadBackground ()
                    this.RaiseBackground()

            match index with
            | Some i -> this.SelectedAtmosphereIndex <- i
            | None -> ()

    /// A picture the user picked. It is copied into our own folder first, so
    /// it survives the original being moved or deleted.
    member this.SetCustomBackground(path: string) =
        match BackgroundStore.importCustom path with
        | Some copy ->
            background <- { background with UseCustom = true; CustomFile = copy; CustomLuminance = 0.0; CustomBright = 0.0 }
            BackgroundStore.save background
            this.RefreshCustomThumbnail()
            loadBackground ()
            this.RaiseBackground()
        | None -> ()

    /// Back to the user's own picture, already imported earlier.
    member this.UseCustomBackground() =
        if this.HasCustomBackground && not background.UseCustom then
            background <- { background with UseCustom = true }
            BackgroundStore.save background
            loadBackground ()
            this.RaiseBackground()

    member this.RemoveCustomBackground() =
        BackgroundStore.removeCustom ()
        background <- { background with UseCustom = false; CustomFile = ""; CustomLuminance = 0.0; CustomBright = 0.0 }
        BackgroundStore.save background
        customTile.SetThumbnail(null)
        loadBackground ()
        this.RaiseBackground()

    member this.IsBackgroundMotionOn = false

    member this.IsWindowActive
        with get () = isWindowActive
        and set value =
            if isWindowActive <> value then
                isWindowActive <- value
                this.RaisePropertyChanged("IsBackgroundMotionOn")
                // PULSE only polls while someone can actually see it.
                pulse.SetWindowActive(value)

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
                      PerformanceMode = isPerformanceMode
                      OverlayDisabled = not overlayDefault
                      OverlayTheme = overlayTheme
                      OverlayHotkey = overlayHotkey }

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
                    loadBackground ()
                    this.RaiseBackground()
                    this.RaisePropertyChanged("BgBaseBrush")
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
                          PerformanceMode = isPerformanceMode
                          OverlayDisabled = not overlayDefault
                          OverlayTheme = overlayTheme
                          OverlayHotkey = overlayHotkey }

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
                loadBackground ()
                this.RaiseBackground()
                this.RaisePropertyChanged("BgBaseBrush")
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
                      PerformanceMode = isPerformanceMode
                      OverlayDisabled = not overlayDefault
                      OverlayTheme = overlayTheme
                      OverlayHotkey = overlayHotkey }

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
                Localization.current <- loc
                screen.RaiseLoc()

                this.RaisePropertyChanged("Loc")
                this.RaisePropertyChanged("SelectedLanguage")
                // Labels change, the list does not - so the picker keeps its
                // selection and simply redraws it in the new language.
                relabelAtmospheres loc
                // These pickers were refilled with the new words; tell them
                // again which entry is selected.
                this.RaisePropertyChanged("SelectedOverlayThemeIndex")
                community.RelabelFilters()
                assets.Relabel()
                this.RaisePropertyChanged("TotalGamesText")
                this.RaisePropertyChanged("InstallModeHintText")
                this.RaisePropertyChanged("DeepFriedHint")
                this.RaiseDlss5State()

                GameScanner.saveSettings
                    { IsSidebarLayout = isSidebarLayout
                      ColorAtmosphere = selectedColorAtmosphere
                      GeometricMotif = selectedGeometricMotif
                      Language = code
                      SupportPromptVersion = supportPromptVersion
                      AmdMode = isAmdMode
                      PerformanceMode = isPerformanceMode
                      OverlayDisabled = not overlayDefault
                      OverlayTheme = overlayTheme
                      OverlayHotkey = overlayHotkey }

    /// "TOTAL GAMES: 42" in the current language.
    member this.TotalGamesText = loc.TotalGames(totalGamesCount)

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
                      PerformanceMode = isPerformanceMode
                      OverlayDisabled = not overlayDefault
                      OverlayTheme = overlayTheme
                      OverlayHotkey = overlayHotkey }

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
                this.RaisePropertyChanged("IsCommunityViewVisible")
                this.RaisePropertyChanged("IsCommunityTabActive")
                this.RaisePropertyChanged("IsScreenTabActive")
                if value = "screen" then
                    screen.ApplyQuery(searchText)
                    screen.Start()
                this.RaisePropertyChanged("SearchPlaceholder")
                // The search control moves between the title bar and the
                // community filter row, so it has to hear about this.
                this.RaisePropertyChanged("IsCommunityGridActive")

                // The community grid is server-side, so it is fetched the first
                // time the section is opened and never before - a user who
                // never goes there makes no network call at all.
                if value = "community" then
                    community.ApplyQuery(searchText)
                    community.EnsureLoaded()

                    if communityTab = "chat" then
                        pulse.Activate(CommunityShared.isVerified community.DisplayName, community.DisplayName)
                    elif communityTab = "gallery" then
                        gallery.Activate(CommunityShared.isVerified community.DisplayName)
                    elif communityTab = "dm" then
                        dm.Activate(CommunityShared.isVerified community.DisplayName)
                else
                    // Leaving the section stops PULSE polling straight away.
                    pulse.Deactivate()

    member this.IsSettingsOpen
        with get () = isSettingsOpen
        and set value = this.ActiveSection <- (if value then "settings" else "games")

    /// One box, four jobs - it says which one it is doing right now.
    member this.SearchPlaceholder =
        match activeSection with
        | "settings" -> "Search settings..."
        | "emulators" -> "Search emulators..."
        | "community" -> "Search community..."
        | "screen" -> "Search Screen options..."
        | _ -> "Search games..."

    member this.IsGamesViewVisible = activeSection = "games"
    member this.IsEmulatorsViewVisible = activeSection = "emulators"
    member this.IsCommunityViewVisible = activeSection = "community"
    member this.IsGamesTabActive = activeSection = "games"
    member this.IsEmulatorsTabActive = activeSection = "emulators"
    member this.IsCommunityTabActive = activeSection = "community"
    member this.IsScreenTabActive = activeSection = "screen"
    member _.Screen = screen

    /// The community section's own state. Exposed so the window can bind to it
    /// as `Community.X` rather than mirroring three dozen properties here.
    member _.Community = community

    /// PULSE, the chat inside the community section.
    member _.Pulse = pulse

    /// The gallery, the third tab.
    member _.Gallery = gallery

    /// The private chat, the fourth tab.
    member _.Dm = dm

    /// The reply toast was clicked: straight to the chat.
    member this.OpenChatFromReply() =
        pulse.DismissReplyToast()
        this.ActiveSection <- "community"
        this.ShowPulse()

    member _.IsPulseOpen = communityTab = "chat"
    member _.IsCommunityGamesOpen = communityTab = "games"
    member _.IsGalleryOpen = communityTab = "gallery"
    member _.IsDmOpen = communityTab = "dm"

    /// The community **grid** is what is on screen right now.
    ///
    /// `IsCommunityGamesOpen` above only says the games tab is the one showing
    /// inside the community section - it is true in Games and Emulators as
    /// well, because those sections have no tabs at all. Using it to hide the
    /// title-bar search therefore hid it everywhere except chat. This asks the
    /// real question, and it is what the search moves against.
    member _.IsCommunityGridActive = activeSection = "community" && communityTab = "games"

    /// Only decides whether a Delete link is drawn on other people's messages.
    /// The server checks the developer flag itself before removing anything.
    member private _.ViewerIsDev = CommunityShared.isVerified community.DisplayName

    /// One place that moves between the three tabs, so a switch can never
    /// leave PULSE polling behind a tab that is no longer on screen.
    member private this.SetCommunityTab(tab: string) =
        if communityTab <> tab then
            communityTab <- tab

            // PULSE stops polling the moment it is off screen.
            if tab <> "chat" then pulse.Deactivate()

            this.RaisePropertyChanged("IsPulseOpen")
            this.RaisePropertyChanged("IsCommunityGamesOpen")
            this.RaisePropertyChanged("IsGalleryOpen")
            this.RaisePropertyChanged("IsDmOpen")
            this.RaisePropertyChanged("IsCommunityGridActive")

    /// Activating unconditionally is deliberate: clicking the reply toast while
    /// the chat is already open has to start it polling again.
    member this.ShowPulse() =
        this.SetCommunityTab("chat")
        pulse.Activate(this.ViewerIsDev, community.DisplayName)

    /// Back to the games.
    member this.ShowCommunityGames() = this.SetCommunityTab("games")

    member this.ShowGallery() =
        this.SetCommunityTab("gallery")
        gallery.Activate(this.ViewerIsDev)

    member this.ShowDm() =
        this.SetCommunityTab("dm")
        dm.Activate(this.ViewerIsDev)

    member this.OpenSettings() = this.ActiveSection <- "settings"
    member this.CloseSettings() = this.ActiveSection <- "games"
    member this.ShowGames() = this.ActiveSection <- "games"
    member this.ShowEmulators() = this.ActiveSection <- "emulators"
    member this.ShowCommunity() = this.ActiveSection <- "community"

    /// "Share result" in the Manage sheet. The post is built from the install
    /// this app made, so the route, API, bit-width and add-ons are already
    /// filled in and the user only picks the verdict.
    member this.ShareToCommunity() =
        match manageCard with
        | Some card ->
            this.IsManageOpen <- false

            community.OpenComposer(
                card.Game,
                isOverlayEnabled,
                ModInstaller.modeKey installMode,
                ModInstaller.optiApiKey optiApi,
                ModInstaller.archKey installArch,
                useNeuralAddon
            )

            this.ActiveSection <- "community"
        | None -> ()

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
                // One box, but only the page actually on screen does any work.
                //
                // Every keystroke used to re-filter the whole games library, the
                // emulator list and all nine settings cards - even while the
                // community grid was showing, which is filtered by the server and
                // cares about none of them. That cost nothing while the box was
                // hidden in this section; moving it here made it felt on every
                // letter typed.
                if activeSection = "community" then
                    community.ApplyQuery(value)
                elif activeSection = "screen" then
                    screen.ApplyQuery(value)
                else
                    filterGamesList ()
                    filterEmulatorsList ()
                    this.RaisePropertyChanged("HasGames")
                    this.RaisePropertyChanged("HasEmulators")
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
              yield loc.BackgroundBrightness
              yield loc.BackgroundCustom
              yield "visual"; yield "theme"; yield "color"; yield "background"; yield "wallpaper"
              yield "brightness"; yield "image"; yield "picture"
              yield! loc.AtmosphereNames ]

    member this.ShowLibraryCard =
        matchesCard searchText [ loc.LibraryCache; loc.FastCacheGameIndex; loc.RescanLibrary; loc.ClearCache
                                 "library"; "cache"; "scan"; "rescan"; "games"; "index"
                                 "dynamic"; "dynamic library" ]

    member this.ShowPayloadCard =
        matchesCard searchText [ loc.ModPayloadFiles; loc.ModPayloadDesc; loc.Replace; loc.Restore
                                 "mod"; "payload"; "files"; "dlss5-feed.addon64"; "renodx-dlss.addon64"; "renodx-dlss5.addon64"
                                 "nvngx_dlssnr.dll"; "replace"; "restore" ]

    member this.ShowPerformanceCard =
        matchesCard searchText [ "performance"; "animation"; "animations"; "gpu"; "motion"; "background"; "fps" ]

    member this.ShowAmdCard =
        matchesCard searchText [ "amd"; "rdna"; "radeon"; "amd mode"; "beta"; "gpu" ]

    member this.ShowOverlayCard =
        matchesCard searchText [ loc.OverlaySection; loc.OverlayTitle; loc.OverlayTagline; loc.OverlayStyle
                                 "overlay"; "dynamic overlay"; "hud"; "fps"; "vram"; "telemetry"; "theme"; "in-game" ]

    /// The three feature switches share one card, so the card is on screen when
    /// any of its rows is - each row still hides itself on its own property.
    member this.ShowFeaturesCard =
        this.ShowOverlayCard || this.ShowAmdCard || this.ShowPerformanceCard

    member this.ShowSupportCard =
        matchesCard searchText [ "support"; "donate"; "ko-fi"; "kofi"; "tutorial"; "tutorials"; "guide"
                                 "youtube"; "video"; "help"; "channel"; "nodix" ]

    member this.ShowAboutCard =
        matchesCard searchText [ "about"; "update"; "updates"; "version"; "credits"; "copyright"
                                 "dlss 5 manager"; "nodix"; "numidia"
                                 "gpu"; "graphics"; "card"; "hardware"; "nvidia"; "rtx" ]

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
            || this.ShowOverlayCard
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
        this.RaisePropertyChanged("ShowOverlayCard")
        this.RaisePropertyChanged("ShowFeaturesCard")
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
    // ---------------------------------------------------------------------
    // THE GRACE PERIOD BEFORE AN AUTOMATIC SCAN
    // ---------------------------------------------------------------------
    //
    // Walking a very large library takes minutes, and someone whose library
    // never changes should not pay that on every launch. So an automatic scan
    // now waits six seconds and says so, with a button to stop it - once, or
    // for good. Pressing Re-scan in Settings is unaffected: that is a direct
    // instruction and starts immediately.

    member this.IsAutoScanPending = isAutoScanPending

    member this.AutoScanCountdownText =
        sprintf "Scanning your library in %d s" autoScanSecondsLeft

    /// Arms the countdown, unless the scan was turned down in this version -
    /// in which case nothing happens at all until the user asks. After an
    /// update it asks again.
    member this.ArmAutoScan() =
        if GameScanner.isAutoScanDisabled UpdateChecker.CurrentVersion then
            this.ScanStatusText <- "Automatic scan is off"
        else
            autoScanSecondsLeft <- 6
            isAutoScanPending <- true
            this.RaisePropertyChanged("IsAutoScanPending")
            this.RaisePropertyChanged("AutoScanCountdownText")

            autoScanTimer.Tick.Add(fun _ ->
                autoScanSecondsLeft <- autoScanSecondsLeft - 1

                if autoScanSecondsLeft <= 0 then
                    autoScanTimer.Stop()
                    isAutoScanPending <- false
                    this.RaisePropertyChanged("IsAutoScanPending")
                    this.StartScanAsync()
                else
                    this.RaisePropertyChanged("AutoScanCountdownText"))

            autoScanTimer.Start()

    /// Stops the countdown without deciding anything - a scan asked for by
    /// hand does this before it starts.
    member this.CancelAutoScan() =
        if isAutoScanPending then
            autoScanTimer.Stop()
            isAutoScanPending <- false
            this.RaisePropertyChanged("IsAutoScanPending")
            this.ScanStatusText <- sprintf "%d Games Ready" totalGamesCount

    /// "Don't scan" - remembered: no restart starts it again. Only the next
    /// update asks once more. A scan by hand is never affected.
    member this.DisableAutoScan() =
        GameScanner.setAutoScanDisabled true UpdateChecker.CurrentVersion
        this.CancelAutoScan()
        this.ScanStatusText <- "Automatic scan is off"
        this.RaisePropertyChanged("IsAutoScanEnabled")

    /// The Settings switch, so turning it off is not a one-way door.
    member this.IsAutoScanEnabled
        with get () = not (GameScanner.isAutoScanDisabled UpdateChecker.CurrentVersion)
        and set value =
            GameScanner.setAutoScanDisabled (not value) UpdateChecker.CurrentVersion
            this.RaisePropertyChanged("IsAutoScanEnabled")

    member this.StartScanAsync() =
        // A scan asked for by hand cancels any countdown still running, rather
        // than letting a second one start behind it.
        this.CancelAutoScan()
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

                    // Anything the user removed and asked never to see again is
                    // dropped here, at the one place a scan produces titles.
                    // Adding a game by hand does not come through here, so that
                    // still works afterwards.
                    let excluded = GameScanner.loadExcluded ()

                    let combined =
                        GameScanner.deduplicateGames (scannedGames @ cachedCustomGames)
                        |> List.filter (fun g -> not (GameScanner.isExcluded excluded g))
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
                        this.ScanStatusText <- sprintf "%d Games Ready" combined.Length)

                    // The emulators are their own search, and it follows on in
                    // the same task with no gap - one press of Re-scan settles
                    // the whole library, games and emulators together.
                    let emulatorsAdded = this.RunEmulatorDetection()

                    Dispatcher.UIThread.Post(fun () ->
                        this.IsScanning <- false

                        this.ScanStatusText <-
                            if emulatorsAdded > 0 then
                                sprintf "%d Games Ready · %d emulator(s) added" combined.Length emulatorsAdded
                            else
                                sprintf "%d Games Ready" combined.Length)
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
    // DOWNLOADS SHEET
    // ---------------------------------------------------------------------
    member this.Assets = assets

    member this.IsAssetsOpen
        with get () = isAssetsOpen
        and set value = this.SetProperty(&isAssetsOpen, value) |> ignore

    /// The download button at the top shows while any of it is missing.
    member this.HasMissingAssets = assets.HasMissing
    member this.IsAssetsBusy = assets.IsAnyBusy

    member this.OpenAssets() = this.IsAssetsOpen <- true

    /// A reminder at the bottom, ten seconds after the app opens: the add-ons
    /// are downloaded from the button at the top. Only while something is
    /// missing - that button is not there otherwise - and not over the sheet
    /// itself. It goes away by itself after another ten seconds.
    member _.IsDownloadsHintShown = downloadsHint

    member this.StartDownloadsHint() =
        let once (seconds: float) (action: unit -> unit) =
            let t = DispatcherTimer(Interval = TimeSpan.FromSeconds(seconds))

            t.Tick.Add(fun _ ->
                t.Stop()
                action ())

            t.Start()

        once 10.0 (fun () ->
            if assets.HasMissing && not isAssetsOpen then
                downloadsHint <- true
                this.RaisePropertyChanged("IsDownloadsHintShown")
                once 10.0 (fun () -> this.DismissDownloadsHint()))

    member this.DismissDownloadsHint() =
        if downloadsHint then
            downloadsHint <- false
            this.RaisePropertyChanged("IsDownloadsHintShown")

    /// The reminder was clicked: straight to the Downloads sheet.
    member this.OpenAssetsFromHint() =
        this.DismissDownloadsHint()
        this.OpenAssets()

    /// Put away, whichever way: from now on the sheet waits to be asked for.
    member this.CloseAssets() =
        this.IsAssetsOpen <- false

        if assets.HasMissing then
            CloudAssets.postpone ()

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
                for name in [ "CanApplyDllName"; "CanRenameOptiCustom"; "CanRenameReShadeCustom" ] do
                    this.RaisePropertyChanged(name)

    member this.IsInstalling
        with get () = isInstalling
        and set value =
            if this.SetProperty(&isInstalling, value) then
                this.RaisePropertyChanged("IsManageReady")
                for name in [ "CanApplyDllName"; "CanRenameOptiCustom"; "CanRenameReShadeCustom" ] do
                    this.RaisePropertyChanged(name)
                this.RaiseDlss5State()

    /// Buttons are only live when nothing is running.
    member this.IsManageReady = not isInstalling && not isAnalyzing

    /// The three install routes. They are mutually exclusive - OptiScaler and
    /// ReShade cannot hook the same game at the same time.
    member this.IsOptiScalerMode = (installMode = ModInstaller.OptiScalerMode)
    member this.IsDx12Mode = (installMode = ModInstaller.Dx12Auto)
    member this.IsDx11Mode = (installMode = ModInstaller.Dx11)
    member this.IsDx9Mode = (installMode = ModInstaller.Dx9)
    member this.IsVulkanMode = (installMode = ModInstaller.VulkanMode)

    /// The user picked this route themselves, so detection must stop having an
    /// opinion. Without this, removing an install cleared `installedRoute` and
    /// the next analysis pass moved the sheet to whatever the game looked like
    /// - jumping the user somewhere they had not asked to go.
    member this.SetInstallMode(mode: ModInstaller.InstallMode) =
        routeChosenByUser <- true
        this.ApplyInstallMode(mode)
        this.ApplyDeepFriedDefault()
        this.RaiseInstallModeState()

    /// The same change made on the app's own initiative, which leaves the
    /// user's claim on the route alone.
    member private this.ApplyInstallMode(mode: ModInstaller.InstallMode) =
        if installMode <> mode then
            installMode <- mode

            // Each route opens on the build its era of games actually shipped:
            // DX11 is 64-bit, DX9 is 32-bit. Either can still be overridden.
            installArch <-
                match mode with
                | ModInstaller.Dx9 -> ModInstaller.Bit32
                | _ -> ModInstaller.Bit64

            this.ApplyDeepFriedDefault()
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
                this.RaisePropertyChanged("IsDllNameCardVisible")
                this.RaisePropertyChanged("IsAmdRouteActive")
                // LIVE FLOW runs on NVIDIA's network and optical flow only.
                screen.SetAmdBlocked(value)

                GameScanner.saveSettings
                    { IsSidebarLayout = isSidebarLayout
                      ColorAtmosphere = selectedColorAtmosphere
                      GeometricMotif = selectedGeometricMotif
                      Language = languageCode
                      SupportPromptVersion = supportPromptVersion
                      AmdMode = isAmdMode
                      PerformanceMode = isPerformanceMode
                      OverlayDisabled = not overlayDefault
                      OverlayTheme = overlayTheme
                      OverlayHotkey = overlayHotkey }

    /// True while the open sheet will install through the AMD payload.
    member this.IsAmdRouteActive = isAmdMode && not isEmulatorTarget

    // ---------------------------------------------------------------------
    // IN-GAME OVERLAY
    // ---------------------------------------------------------------------
    /// The Manage sheet's overlay switch: off means this install leaves the
    /// overlay out. It does not touch a game that already has one until that
    /// game is installed again - the overlay comes off with the mod, like
    /// everything else. The choice is also remembered as the default for the
    /// next game not installed yet.
    member this.IsOverlayEnabled
        with get () = isOverlayEnabled
        and set value =
            if isOverlayEnabled <> value then
                isOverlayEnabled <- value
                this.RaisePropertyChanged("IsOverlayEnabled")
                this.RaisePropertyChanged("IsOverlayKeyVisible")

            if overlayDefault <> value then
                overlayDefault <- value
                this.SaveOverlaySettings()

    /// The add-on itself is fetched, not installed with the program - see
    /// `CloudAssets`. Until it is here the Manage sheet shows its option locked
    /// with a way to get it, and an install simply leaves it out
    /// (`deployOverlay` skips a file that is not there).
    member this.IsOverlayDownloaded = assets.IsOverlayReady
    member this.IsOverlayLocked = not assets.IsOverlayReady

    /// The overlay's row in the CHANGE KEY card: only while there is an
    /// overlay to open, on a route that can host it.
    member this.IsOverlayKeyVisible =
        isOverlayEnabled && assets.IsOverlayReady && this.IsOverlayRouteSupported

    /// The theme names, shown as-is: they are identities the add-on resolves,
    /// not labels, so they stay English in every language exactly like the
    /// colour atmospheres do.
    member this.OverlayThemes = ModInstaller.overlayThemes

    /// The style picker binds to the index, like the atmosphere picker: it
    /// shows translated names (`Loc.OverlayThemeNames`), while the English
    /// name is what gets saved and what the add-on reads. The -1 a language
    /// switch reports for a moment is ignored.
    member this.SelectedOverlayThemeIndex
        with get () =
            ModInstaller.overlayThemes
            |> Array.tryFindIndex ((=) overlayTheme)
            |> Option.defaultValue 0
        and set (index: int) =
            if index >= 0 && index < ModInstaller.overlayThemes.Length then
                this.SelectedOverlayTheme <- ModInstaller.overlayThemes.[index]
                this.RaisePropertyChanged("SelectedOverlayThemeIndex")

    member this.SelectedOverlayTheme
        with get () = overlayTheme
        and set (value: string) =
            if not (String.IsNullOrWhiteSpace(value)) && overlayTheme <> value then
                overlayTheme <- value
                this.RaisePropertyChanged("SelectedOverlayTheme")
                this.SaveOverlaySettings()

    /// Which key opens the overlay in game. The overlay's own Settings tab can
    /// change it too; whichever was set last is what the next install writes.
    member this.OverlayHotkeys = ModInstaller.overlayHotkeys

    // ---- ReShade's key, the overlay's key, one click DLSS 5 -----------------
    member _.ReShadeKeys = ModInstaller.reshadeKeys
    member _.SelectedReShadeKey = reshadeKey
    member _.KeysStatus = keysStatus
    member _.HasKeysStatus = not (String.IsNullOrWhiteSpace(keysStatus))

    member private this.SetKeysStatus(text: string) =
        keysStatus <- (if isNull text then "" else text)
        this.RaisePropertyChanged("KeysStatus")
        this.RaisePropertyChanged("HasKeysStatus")

    // ---- DLL NAME: rename the OptiScaler / ReShade proxy after an install ----
    // The install still chooses the name itself, exactly as before; this only
    // moves the file afterwards, and only when the user asks.
    member _.IsDllNamesOpen = dllNamesOpen

    member _.DllNamesIcon =
        if dllNamesOpen then "M 7,14 L 12,9 L 17,14 Z" else "M 7,10 L 12,15 L 17,10 Z"

    member this.ToggleDllNames() =
        dllNamesOpen <- not dllNamesOpen
        if dllNamesOpen then this.RefreshDllNames()
        this.RaisePropertyChanged("IsDllNamesOpen")
        this.RaisePropertyChanged("DllNamesIcon")

    /// Is this name already a file in the proxy's folder - and not the proxy
    /// itself? Such a name belongs to the game and is never offered.
    member private _.IsNameTaken(name: string, current: string) =
        let n = if isNull name then "" else name.ToLowerInvariant()
        n <> "" && dllFolderNames.Contains n && not (n = current.ToLowerInvariant())

    member private this.ApplyDllNames(info: ModInstaller.ProxyNamesInfo) =
        optiProxyName <- (if isNull info.OptiName then "" else info.OptiName)
        reshadeProxyName <- (if isNull info.ReShadeName then "" else info.ReShadeName)

        dllFolderNames <-
            (if isNull info.FolderDlls then Set.empty
             else info.FolderDlls |> Array.map (fun n -> n.ToLowerInvariant()) |> Set.ofArray)

        dllNamesKnown <- true

        // Only the names that are free - plus the one in use, so the list
        // shows where it stands.
        let free (names: string[]) (current: string) =
            names |> Array.filter (fun n -> not (this.IsNameTaken(n, current)))

        optiChoices <- free ModInstaller.optiScalerProxyNames optiProxyName
        reshadeChoices <- free ModInstaller.reShadeProxyNames reshadeProxyName

        let pick (choices: string[]) (current: string) (previous: string) =
            match choices |> Array.tryFind (fun x -> x.Equals(current, StringComparison.OrdinalIgnoreCase)) with
            | Some n -> n
            | None ->
                if choices |> Array.contains previous then previous
                elif choices.Length > 0 then choices.[0]
                else previous

        optiProxyPick <- pick optiChoices optiProxyName optiProxyPick
        reshadeProxyPick <- pick reshadeChoices reshadeProxyName reshadeProxyPick

        for name in
            [ "OptiProxyNames"; "ReShadeProxyNames"; "HasOptiProxy"; "OptiProxyName"; "HasReShadeProxy"
              "ReShadeProxyName"; "HasNoProxy"; "SelectedOptiProxyName"; "SelectedReShadeProxyName"
              "IsDllNameCardVisible"; "IsEmulatorDllNameVisible"; "IsEmulatorVulkanLayer"
              "CanRenameOptiCustom"; "CanRenameReShadeCustom"; "IsOptiCustomTaken"; "IsReShadeCustomTaken"
              "CanApplyDllName" ] do
            this.RaisePropertyChanged(name)

    /// The names for the open game: from the cache at once when it still
    /// matches the install record, otherwise read in the background (after an
    /// install, a switch, a rename, or the sheet's refresh button).
    member this.RefreshDllNames() =
        dllScanGeneration <- dllScanGeneration + 1
        let generation = dllScanGeneration

        match manageCard with
        | None ->
            dllNamesKnown <- false
            this.RaisePropertyChanged("IsDllNameCardVisible")
        | Some card ->
            let game = card.Game

            match ModInstaller.tryCachedProxyNames game with
            | Some info -> this.ApplyDllNames(info)
            | None ->
                dllNamesKnown <- false
                this.RaisePropertyChanged("IsDllNameCardVisible")
                this.RaisePropertyChanged("IsEmulatorVulkanLayer")

                System.Threading.Tasks.Task.Run(fun () ->
                    let info = ModInstaller.proxyNames game

                    Dispatcher.UIThread.Post(fun () ->
                        // Only if nothing newer was asked for meanwhile, and the
                        // sheet still shows that game.
                        let sameGame =
                            match manageCard with
                            | Some c -> obj.ReferenceEquals(c, card)
                            | None -> false

                        if generation = dllScanGeneration && sameGame then this.ApplyDllNames(info)))
                |> ignore

    member _.OptiProxyNames = optiChoices
    member _.ReShadeProxyNames = reshadeChoices
    member _.HasOptiProxy = optiProxyName <> ""
    member _.OptiProxyName = optiProxyName
    member _.HasReShadeProxy = reshadeProxyName <> ""
    member _.ReShadeProxyName = reshadeProxyName
    member _.HasNoProxy = optiProxyName = "" && reshadeProxyName = ""

    /// Not offered at all before the mod is on the game: there is nothing to
    /// rename until an install has put a proxy there.
    member this.IsDllNameCardVisible =
        // Not while the sheet offers a switch: the names shown would belong to
        // the install that is about to be removed, not to the one being picked.
        not this.IsSwitchingRoute
        && dllNamesKnown
        && ((this.IsGameTarget && not this.HasNoProxy) || this.IsEmulatorDllNameVisible)

    /// Emulators: ReShade is renamed like on a game when it went in on the
    /// DirectX slot (a DLL beside the emulator). On Vulkan it is a layer and
    /// there is no DLL - the card still shows, and says so.
    member _.IsEmulatorDllNameVisible = isEmulatorTarget && installedRoute = "emulator"

    member this.IsEmulatorVulkanLayer = dllNamesKnown && this.IsEmulatorDllNameVisible && reshadeProxyName = ""

    member this.SelectedOptiProxyName
        with get () = optiProxyPick
        and set (value: string) = if not (isNull value) then this.SetProperty(&optiProxyPick, value) |> ignore

    member this.SelectedReShadeProxyName
        with get () = reshadeProxyPick
        and set (value: string) = if not (isNull value) then this.SetProperty(&reshadeProxyPick, value) |> ignore

    member this.OptiCustomName
        with get () = optiCustomName
        and set (value: string) =
            if this.SetProperty(&optiCustomName, (if isNull value then "" else value)) then
                this.RaisePropertyChanged("CanRenameOptiCustom")
                this.RaisePropertyChanged("IsOptiCustomTaken")

    member this.ReShadeCustomName
        with get () = reshadeCustomName
        and set (value: string) =
            if this.SetProperty(&reshadeCustomName, (if isNull value then "" else value)) then
                this.RaisePropertyChanged("CanRenameReShadeCustom")
                this.RaisePropertyChanged("IsReShadeCustomTaken")

    /// The typed name is already a file in the game folder: said under the
    /// box, and Apply stays off. The installer refuses it too, on its own.
    member this.IsOptiCustomTaken = this.IsNameTaken(ModInstaller.normalizeProxyName optiCustomName, optiProxyName)
    member this.IsReShadeCustomTaken = this.IsNameTaken(ModInstaller.normalizeProxyName reshadeCustomName, reshadeProxyName)

    member this.CanApplyDllName = not isInstalling && not isAnalyzing && not dllBusy

    member this.CanRenameOptiCustom =
        this.CanApplyDllName && ModInstaller.normalizeProxyName optiCustomName <> "" && not this.IsOptiCustomTaken

    member this.CanRenameReShadeCustom =
        this.CanApplyDllName && ModInstaller.normalizeProxyName reshadeCustomName <> "" && not this.IsReShadeCustomTaken

    member _.DllNameStatus = dllNameStatus
    member _.HasDllNameStatus = not (String.IsNullOrWhiteSpace(dllNameStatus))

    member _.DllNameStatusBrush: IBrush =
        if dllNameIsError then SolidColorBrush(Color.Parse("#F87171")) :> IBrush
        else SolidColorBrush(Color.Parse("#86EFAC")) :> IBrush

    /// The move itself runs off the UI thread too; the names are read again
    /// afterwards (the install record changed, so the cache no longer matches).
    member private this.RenameProxy(kind: ModInstaller.ProxyKind, name: string) =
        match manageCard with
        | Some card when this.CanApplyDllName ->
            dllBusy <- true
            this.RaisePropertyChanged("CanApplyDllName")
            this.RaisePropertyChanged("CanRenameOptiCustom")
            this.RaisePropertyChanged("CanRenameReShadeCustom")
            let game = card.Game

            System.Threading.Tasks.Task.Run(fun () ->
                let outcome = ModInstaller.renameProxy game kind name

                Dispatcher.UIThread.Post(fun () ->
                    dllBusy <- false
                    dllNameStatus <- outcome.Message
                    dllNameIsError <- not outcome.Success
                    this.RefreshDllNames()
                    this.RaisePropertyChanged("DllNameStatus")
                    this.RaisePropertyChanged("HasDllNameStatus")
                    this.RaisePropertyChanged("DllNameStatusBrush")
                    this.RaisePropertyChanged("CanApplyDllName")
                    this.RaisePropertyChanged("CanRenameOptiCustom")
                    this.RaisePropertyChanged("CanRenameReShadeCustom")))
            |> ignore
        | _ -> ()

    member this.RenameOptiToPick() = this.RenameProxy(ModInstaller.OptiScalerProxyDll, optiProxyPick)
    member this.RenameReShadeToPick() = this.RenameProxy(ModInstaller.ReShadeProxyDll, reshadeProxyPick)
    member this.RenameOptiToCustom() = this.RenameProxy(ModInstaller.OptiScalerProxyDll, optiCustomName)
    member this.RenameReShadeToCustom() = this.RenameProxy(ModInstaller.ReShadeProxyDll, reshadeCustomName)

    /// The folder of the game whose sheet is open, when it has an install for
    /// the keys to be written into - "" otherwise.
    member private this.InstalledGameDir =
        match manageCard with
        | Some card when ModInstaller.installedMode card.Game <> "" ->
            try
                System.IO.Path.GetDirectoryName(card.ExecutablePath)
            with _ ->
                ""
        | _ -> ""

    /// Picked from the sheet: kept for every install from now on and, on a game
    /// that already has one, written in straight away - that install has long
    /// finished, so there is nothing for the change to race.
    member this.ChooseReShadeKey(name: string) =
        if ModInstaller.reshadeKeys |> Array.contains name then
            if name.Equals(overlayHotkey, StringComparison.OrdinalIgnoreCase) then
                this.SetKeysStatus("That key already opens the overlay - pick another one for ReShade.")
            else
                reshadeKey <- name
                ModInstaller.saveReShadeKey name
                this.RaisePropertyChanged("SelectedReShadeKey")

                match this.InstalledGameDir with
                | "" -> this.SetKeysStatus("")
                | dir ->
                    ModInstaller.applyReShadeKey dir name
                    this.SetKeysStatus("ReShade now opens with " + name + " in this game.")

    member this.ChooseOverlayKey(binding: string) =
        if binding.Equals(reshadeKey, StringComparison.OrdinalIgnoreCase) then
            this.SetKeysStatus("That key already opens ReShade - pick another one for the overlay.")
        else
            this.SelectedOverlayHotkey <- binding

            match this.InstalledGameDir with
            | "" -> this.SetKeysStatus("")
            | dir ->
                ModInstaller.applyOverlayKey dir binding
                this.SetKeysStatus("The overlay now opens with " + binding + " in this game.")

    /// Called once an install has fully finished: the chosen keys go in last,
    /// after ReShade's setup has written the files they live in.
    member private this.ApplyKeysToInstall() =
        match this.InstalledGameDir with
        | "" -> ()
        | dir ->
            ModInstaller.applyReShadeKey dir reshadeKey
            ModInstaller.applyOverlayKey dir overlayHotkey
            // The OptiScaler route's own menu key. It writes nothing unless an
            // OptiScaler.ini is there, so the other routes are untouched.
            ModInstaller.applyOptiScalerMenuKey dir optiMenuKey

    // ---- OptiScaler's own menu key -----------------------------------------
    member _.OptiMenuKeys = ModInstaller.optiMenuKeys
    member _.SelectedOptiMenuKey = optiMenuKey
    member _.OptiKeyStatus = optiKeyStatus
    member _.HasOptiKeyStatus = not (String.IsNullOrWhiteSpace(optiKeyStatus))

    /// Picked from the OptiScaler route: kept for every install from now on
    /// and, on a game that already has one, written into its OptiScaler.ini
    /// straight away - that install has long finished, so nothing races it.
    member this.ChooseOptiMenuKey(name: string) =
        if ModInstaller.optiMenuKeys |> Array.contains name then
            optiMenuKey <- name
            ModInstaller.saveOptiMenuKey name
            this.RaisePropertyChanged("SelectedOptiMenuKey")

            let written =
                match this.InstalledGameDir with
                | "" -> false
                | dir ->
                    let ini = System.IO.Path.Combine(dir, "OptiScaler.ini")

                    if System.IO.File.Exists(ini) then
                        ModInstaller.applyOptiScalerMenuKey dir name
                        true
                    else
                        false

            optiKeyStatus <-
                if written then
                    "OptiScaler's menu now opens with "
                    + (if name = "Auto" then "Insert" else name)
                    + " in this game."
                else
                    ""

            this.RaisePropertyChanged("OptiKeyStatus")
            this.RaisePropertyChanged("HasOptiKeyStatus")

    member this.SelectedOverlayHotkey
        with get () = overlayHotkey
        and set (value: string) =
            if not (String.IsNullOrWhiteSpace(value)) && overlayHotkey <> value then
                overlayHotkey <- value
                this.RaisePropertyChanged("SelectedOverlayHotkey")
                this.SaveOverlaySettings()

    member private this.SaveOverlaySettings() =
        GameScanner.saveSettings
            { IsSidebarLayout = isSidebarLayout
              ColorAtmosphere = selectedColorAtmosphere
              GeometricMotif = selectedGeometricMotif
              Language = languageCode
              SupportPromptVersion = supportPromptVersion
              AmdMode = isAmdMode
              PerformanceMode = isPerformanceMode
              OverlayDisabled = not overlayDefault
              OverlayTheme = overlayTheme
              OverlayHotkey = overlayHotkey }

    /// What the current sheet would install, so the manage sheet can say
    /// whether the overlay is coming along.
    member this.IsOverlayRouteSupported =
        ModInstaller.overlaySupported installMode optiApi

    /// The CHANGE KEY card: OptiScaler's menu key on its own route, ReShade's
    /// and the overlay's on the routes that put a ReShade in the game - which
    /// are exactly the ones `overlaySupported` answers yes for.
    member this.IsKeysCardVisible =
        this.IsOptiScalerMode || this.IsOverlayRouteSupported

    member this.OverlayOptions: ModInstaller.OverlayOptions =
        { Enabled = isOverlayEnabled
          Theme = overlayTheme
          Hotkey = overlayHotkey }

    /// The 32-bit and DX9 routes only exist for DX11 and DX9.
    member this.IsArchChoiceVisible =
        not isEmulatorTarget && ModInstaller.archMatters installMode

    /// OptiScaler is the only route where the game's API changes anything.
    member this.IsOptiApiChoiceVisible =
        not isEmulatorTarget && installMode = ModInstaller.OptiScalerMode

    /// An emulator installs one way, but on either of two slots: ReShade hooks
    /// the Vulkan layer or the DirectX one. That is the only choice its sheet
    /// offers, and it rides in on the same `optiApi` field.
    member this.IsEmulatorApiChoiceVisible = isEmulatorTarget

    member this.IsOptiDx12 = (optiApi = ModInstaller.OptiDx12)
    member this.IsOptiVulkan = (optiApi = ModInstaller.OptiVulkan)
    member this.IsOptiNeural = (optiApi = ModInstaller.OptiNeural)

    member this.SetOptiApi(api: ModInstaller.OptiScalerApi) =
        if optiApi <> api then
            optiApi <- api
            this.RaiseInstallModeState()

    /// Marks the API an existing OptiScaler install actually used.
    member this.IsOptiDx12Installed =
        installedRoute = "optiscaler" && installedApi = "dx12"

    member this.IsOptiVulkanInstalled =
        installedRoute = "optiscaler" && installedApi = "vulkan"

    member this.IsOptiNeuralInstalled =
        installedRoute = "optiscaler" && installedApi = "neural"

    /// Neural upstream is an extra add-on on the ReShade and AMD routes;
    /// OptiScaler has its own build of it and offers that as an API instead.
    member this.IsNeuralAddonVisible =
        not isEmulatorTarget
        && (isAmdMode
            || installMode = ModInstaller.Dx12Auto
            || installMode = ModInstaller.Dx11
            || installMode = ModInstaller.Dx9
            || installMode = ModInstaller.VulkanMode)

    member this.IsNeuralAddonOn = useNeuralAddon
    member this.IsNeuralAddonOff = not useNeuralAddon

    member this.SetNeuralAddon(on: bool) =
        if useNeuralAddon <> on then
            useNeuralAddon <- on
            this.RaiseInstallModeState()

    /// Marks the add-on as live only while the route it was recorded against
    /// is the one on screen.
    member this.IsNeuralAddonInstalled =
        installedNeural
        && installedRoute <> ""
        && installedRoute = ModInstaller.modeKey installMode

    /// The two RenoDX options exist only where RenoDX is installed: the DX12,
    /// DX11 and DX9 routes, 64-bit. A 32-bit install carries no RenoDX, and
    /// neither do AMD mode or emulators.
    member this.IsRenoDxOptionsVisible =
        not isEmulatorTarget
        && not isAmdMode
        && (installMode = ModInstaller.Dx12Auto
            || installMode = ModInstaller.Dx11
            || installMode = ModInstaller.Dx9)
        && not (ModInstaller.archMatters installMode && installArch = ModInstaller.Bit32)

    /// MFG unlock is offered wherever it means something: the RenoDX routes,
    /// and the Vulkan route - which carries no RenoDX but still takes it.
    /// Multipass and deep-fried stay tied to `IsRenoDxOptionsVisible`, because
    /// both only decide what happens to RenoDX itself.
    member this.IsMfgUnlockVisible = this.IsRenoDxOptionsVisible || this.IsVulkanMode

    /// DX9 on a 32-bit game. The payload still goes in, but not where RenoDX
    /// would have been: a 32-bit game cannot load a single 64-bit module, so it
    /// travels into host64 beside the game with the rest of them.
    member this.IsDeepFriedHost64 =
        not isEmulatorTarget
        && not isAmdMode
        && (installMode = ModInstaller.Dx9 || installMode = ModInstaller.Dx11)
        && installArch = ModInstaller.Bit32

    /// Offered on the RenoDX routes, on 32-bit where it stands alone, and on
    /// Vulkan, where it is the effect itself.
    member this.IsDeepFriedVisible = this.IsRenoDxOptionsVisible || this.IsDeepFriedHost64 || this.IsVulkanMode

    /// The same switch does two different things, so it says two different
    /// things. On DX9 32-bit there is no RenoDX for it to replace - it goes in
    /// beside the 64-bit modules, and it is there to help the game run at all.
    member this.DeepFriedHint =
        if this.IsDeepFriedHost64 then loc.TipDeepFriedHost64
        elif this.IsVulkanMode then loc.TipDeepFriedVulkan
        else loc.TipDeepFried

    member this.IsMfgUnlockOn = useMfgUnlock
    member this.IsMfgUnlockOff = not useMfgUnlock

    member this.SetMfgUnlock(on: bool) =
        if useMfgUnlock <> on then
            useMfgUnlock <- on
            this.RaiseInstallModeState()

    member this.IsMultipassOn = useMultipass
    member this.IsMultipassOff = not useMultipass

    member this.SetMultipass(on: bool) =
        if useMultipass <> on then
            useMultipass <- on
            this.RaiseInstallModeState()

    /// Deep-fried chicken goes in where RenoDX would have, so Multipass - which
    /// only decides which RenoDX build to use - is switched off with it and
    /// greys out until it is switched back off.
    member this.IsDeepFriedOn = useDeepFried
    member this.IsDeepFriedOff = not useDeepFried
    member this.IsMultipassAvailable = not useDeepFried

    member this.SetDeepFried(on: bool) =
        if useDeepFried <> on then
            useDeepFried <- on
            if on then useMultipass <- false
            this.RaiseInstallModeState()

    /// On DX9 32-bit the payload is the point of the route, so it arrives
    /// switched on. Only when the user picks that combination themselves -
    /// reopening a sheet restores whatever was actually installed, and turning
    /// it off afterwards has to stick.
    member private this.ApplyDeepFriedDefault() =
        // Never over an install that is already there. The sheet has just
        // restored what that install actually used, and switching this on
        // underneath would offer to "change" something nobody touched.
        if installedRoute = "" then
            // On 32-bit the payload is the point of the route, so it arrives
            // on. Everywhere else it is an option, and an option that switches
            // itself on is not one - so changing route or build settles it
            // either way rather than carrying the last answer across.
            let wanted = this.IsDeepFriedHost64 || this.IsVulkanMode

            if wanted <> useDeepFried then
                useDeepFried <- wanted
                if wanted then useMultipass <- false

    member this.IsDeepFriedInstalled =
        installedDeepFried
        && installedRoute <> ""
        && installedRoute = ModInstaller.modeKey installMode

    /// Same rule as the neural dot: live only on the route it was recorded with.
    member this.IsMfgUnlockInstalled =
        installedMfgUnlock
        && installedRoute <> ""
        && installedRoute = ModInstaller.modeKey installMode

    member this.IsMultipassInstalled =
        installedMultipass
        && installedRoute <> ""
        && installedRoute = ModInstaller.modeKey installMode

    member this.IsBit64 = (installArch = ModInstaller.Bit64)
    member this.IsBit32 = (installArch = ModInstaller.Bit32)

    member this.SetInstallArch(arch: ModInstaller.InstallArch) =
        if installArch <> arch then
            installArch <- arch
            this.ApplyDeepFriedDefault()
            this.RaiseInstallModeState()

    member private this.RaiseInstallModeState() =
        this.RaisePropertyChanged("IsOptiScalerMode")
        this.RaisePropertyChanged("IsDx12Mode")
        this.RaisePropertyChanged("IsDx11Mode")
        this.RaisePropertyChanged("IsDx9Mode")
        this.RaisePropertyChanged("IsVulkanMode")
        this.RaisePropertyChanged("IsVulkanInstalled")
        this.RaisePropertyChanged("IsMfgUnlockVisible")
        this.RaisePropertyChanged("IsArchChoiceVisible")
        this.RaisePropertyChanged("IsOptiApiChoiceVisible")
        this.RaisePropertyChanged("IsOptiDx12")
        this.RaisePropertyChanged("IsOptiVulkan")
        this.RaisePropertyChanged("IsOptiNeural")
        this.RaisePropertyChanged("IsNeuralAddonVisible")
        this.RaisePropertyChanged("IsOverlayRouteSupported")
        this.RaisePropertyChanged("IsKeysCardVisible")
        this.RaisePropertyChanged("IsOverlayKeyVisible")
        this.RaisePropertyChanged("IsNeuralAddonOn")
        this.RaisePropertyChanged("IsNeuralAddonOff")
        this.RaisePropertyChanged("IsRenoDxOptionsVisible")
        this.RaisePropertyChanged("IsDeepFriedHost64")
        this.RaisePropertyChanged("IsDeepFriedVisible")
        this.RaisePropertyChanged("DeepFriedHint")
        this.RaisePropertyChanged("IsMfgUnlockOn")
        this.RaisePropertyChanged("IsMfgUnlockOff")
        this.RaisePropertyChanged("IsMultipassOn")
        this.RaisePropertyChanged("IsMultipassOff")
        this.RaisePropertyChanged("IsMultipassAvailable")
        this.RaisePropertyChanged("IsDeepFriedOn")
        this.RaisePropertyChanged("IsDeepFriedOff")
        this.RaisePropertyChanged("IsEmulatorApiChoiceVisible")
        this.RaisePropertyChanged("IsBit64")
        this.RaisePropertyChanged("IsBit32")
        this.RaisePropertyChanged("InstallModeHintText")
        // Picking a different route turns Install / Remove into Switch.
        this.RaiseDlss5State()

    // ---------------------------------------------------------------------
    // WHAT THE GAME IS
    // ---------------------------------------------------------------------
    /// What the game renders with. A title that offers two - Red Dead
    /// Redemption 2 is the obvious one - says both rather than picking a
    /// winner, because both are true and the choice is the player's.
    member this.DetectedApiText =
        let label api =
            match api with
            | "dx12" -> "DirectX 12"
            | "dx11" -> "DirectX 11"
            | "dx10" -> "DirectX 10"
            | "dx9" -> "DirectX 9"
            | "vulkan" -> "Vulkan"
            | "opengl" -> "OpenGL"
            | _ -> ""

        let names =
            detectedApis
            |> Array.map label
            |> Array.filter (fun s -> s <> "")

        if names.Length = 0 then "Unknown API" else String.Join(" + ", names)

    member this.DetectedArchText =
        match detectedArch with
        | "32" -> "32-bit"
        | "64" -> "64-bit"
        | _ -> ""


    // =======================================================================
    // COMMUNITY AT A GLANCE
    // =======================================================================

    /// Folded away until asked for: it is one request to the community, and a
    /// sheet that is opened to press Install should not make it.
    member this.IsCommunityGlanceOpen
        with get () = glanceOpen
        and set value =
            if this.SetProperty(&glanceOpen, value) then
                this.RaisePropertyChanged("CommunityGlanceIcon")
                if value then this.LoadCommunityGlance()

    member this.ToggleCommunityGlance() =
        this.IsCommunityGlanceOpen <- not glanceOpen

    /// Closed and blank, for the next game.
    member private this.ResetCommunityGlance() =
        glanceOpen <- false
        glanceBusy <- false
        glanceFor <- ""
        glanceGame <- None
        glanceMessage <- ""

        for name in
            [ "IsCommunityGlanceOpen"; "CommunityGlanceIcon"; "IsCommunityGlanceBusy"; "HasCommunityGlance"
              "CommunityGlanceMessage"; "HasCommunityGlanceMessage"; "CommunityGlanceWorking"
              "CommunityGlanceMixed"; "CommunityGlanceBroken"; "CommunityGlanceReports"
              "CommunityGlanceTitle" ] do
            this.RaisePropertyChanged(name)

    /// Ask again for this game, past whatever was remembered - for when the
    /// answer has moved on since.
    member this.RefreshCommunityGlance() =
        match manageCard with
        | Some card ->
            glanceCache.Remove(card.Title) |> ignore
            glanceFor <- ""
            this.LoadCommunityGlance()
        | None -> ()

    member this.CommunityGlanceIcon =
        if glanceOpen then "M 7,14 L 12,9 L 17,14 Z" else "M 7,10 L 12,15 L 17,10 Z"

    member _.IsCommunityGlanceBusy = glanceBusy
    member _.HasCommunityGlance = glanceGame.IsSome
    member _.CommunityGlanceMessage = glanceMessage
    member _.HasCommunityGlanceMessage = not (String.IsNullOrWhiteSpace(glanceMessage))

    /// How many people it worked for, and how many it did not. "Mixed" is
    /// counted with the ones it worked for nowhere: it is its own answer.
    member _.CommunityGlanceWorking =
        match glanceGame with
        | Some g -> string g.Working
        | None -> "0"

    member _.CommunityGlanceMixed =
        match glanceGame with
        | Some g -> string g.Mixed
        | None -> "0"

    member _.CommunityGlanceBroken =
        match glanceGame with
        | Some g -> string g.Broken
        | None -> "0"

    member _.CommunityGlanceReports =
        match glanceGame with
        | Some g -> sprintf "%d" g.Reports
        | None -> "0"

    member _.CommunityGlanceTitle =
        match glanceGame with
        | Some g -> g.Title
        | None -> ""

    /// The one request. The community keys games by its own id, so the local
    /// title is searched for and the best match taken - the same scorer the
    /// cover matcher uses, so "Hitman: Blood Money" and "HITMAN BLOOD MONEY"
    /// are one game and "Hitman 3" is not.
    member private this.LoadCommunityGlance() =
        let title =
            match manageCard with
            | Some card -> card.Title
            | None -> ""

        let raiseGlance () =
            for name in
                [ "IsCommunityGlanceBusy"; "HasCommunityGlance"; "CommunityGlanceMessage"
                  "HasCommunityGlanceMessage"; "CommunityGlanceWorking"; "CommunityGlanceMixed"
                  "CommunityGlanceBroken"; "CommunityGlanceReports"; "CommunityGlanceTitle" ] do
                this.RaisePropertyChanged(name)

        let remember (key: string) (value: CommunityApi.GameDto option) =
            if not (glanceCache.ContainsKey(key)) then
                glanceOrder.Enqueue(key)

                while glanceOrder.Count > 30 do
                    let oldest = glanceOrder.Dequeue()
                    glanceCache.Remove(oldest) |> ignore

            glanceCache[key] <- value

        if String.IsNullOrWhiteSpace(title) then
            glanceMessage <- ""
        elif glanceFor = title && (glanceGame.IsSome || glanceBusy) then
            ()   // already have it, or it is on its way
        elif glanceCache.ContainsKey(title) then
            // Asked before, this session: nothing goes over the wire.
            glanceFor <- title
            glanceBusy <- false
            glanceGame <- glanceCache[title]
            glanceMessage <- (if glanceGame.IsSome then "" else Localization.current.CommunityGlanceNone)
            raiseGlance ()
        else
            glanceFor <- title
            glanceGame <- None
            glanceBusy <- true
            glanceMessage <- ""
            this.RaisePropertyChanged("IsCommunityGlanceBusy")
            this.RaisePropertyChanged("HasCommunityGlance")
            this.RaisePropertyChanged("CommunityGlanceMessage")
            this.RaisePropertyChanged("HasCommunityGlanceMessage")

            System.Threading.Tasks.Task.Run(fun () ->
                let outcome =
                    try CommunityApi.listGames title "" "" 0 8
                    with ex -> Error ex.Message

                Dispatcher.UIThread.Post(fun () ->
                    // The sheet may have moved on to another game while this
                    // was in flight; the answer for the old one is dropped.
                    if glanceFor = title then
                        glanceBusy <- false

                        match outcome with
                        | Ok(list, _) ->
                            let best =
                                list
                                |> Array.filter (fun g -> not (isNull g.Title))
                                |> Array.map (fun g -> g, SteamCovers.score title g.Title)
                                |> Array.sortByDescending snd
                                |> Array.tryHead

                            match best with
                            | Some(g, points) when points >= 60 ->
                                glanceGame <- Some g
                                glanceMessage <- ""
                                remember title (Some g)
                            | _ ->
                                glanceGame <- None
                                glanceMessage <- Localization.current.CommunityGlanceNone
                                remember title None
                        | Error text ->
                            // A failure is not remembered: the next look should
                            // try again rather than repeat the error.
                            glanceGame <- None
                            glanceMessage <- text

                        raiseGlance ()))
            |> ignore

    /// Into the community, at this game, with its reports open.
    member this.OpenGlanceInCommunity() =
        match glanceGame with
        | Some dto ->
            this.IsManageOpen <- false
            this.ActiveSection <- "community"
            this.SetCommunityTab("games")
            community.OpenGame(CommunityGameViewModel(dto))
        | None -> ()

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
            // One read of the executable answers both questions: the whole list
            // is what the sheet shows, and its first entry - the newest DirectX
            // generation when there is one - is what the routing decides on.
            detectedApis <- GameAnalyzer.detectGraphicsApis manageExePath
            detectedApi <- (if detectedApis.Length > 0 then detectedApis.[0] else "")
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

        if not (respectExisting && (installedRoute <> "" || routeChosenByUser || sheetOpenedInstalled)) then
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

            this.ApplyInstallMode(mode)
            this.SetInstallArch(if detectedArch = "32" then ModInstaller.Bit32 else ModInstaller.Bit64)

    member this.InstallModeHintText =
        match installMode with
        | ModInstaller.OptiScalerMode -> loc.HintRouteOptiScaler
        | ModInstaller.Dx12Auto -> loc.HintRouteRenoDx
        | ModInstaller.VulkanMode -> loc.HintRouteVulkan
        | ModInstaller.Dx11 -> loc.HintRouteRenoDx
        | ModInstaller.Dx9 -> loc.HintRouteDx9
        | ModInstaller.Emulator -> loc.HintRouteEmulator
        | ModInstaller.AmdMode -> loc.HintRouteAmd

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
                && installedApi <> ModInstaller.optiApiKey optiApi)
            // Turning the neural upstream add-on on or off changes what is on
            // the game, so it is a switch like any other - and so do the two
            // RenoDX options.
            || (this.IsNeuralAddonVisible && installedNeural <> useNeuralAddon)
            || (this.IsRenoDxOptionsVisible
                && (installedMfgUnlock <> useMfgUnlock
                    || installedMultipass <> useMultipass
                    || installedDeepFried <> useDeepFried)))

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
    member this.IsVulkanInstalled = installedRoute = "vulkan"

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
            | ModInstaller.OptiScalerMode ->
                "OptiScaler"
            | ModInstaller.Dx12Auto -> "DX12"
            | ModInstaller.Dx11 -> "DX11"
            | ModInstaller.Dx9 -> "DX9"
            | ModInstaller.VulkanMode -> "Vulkan"

            | ModInstaller.Emulator -> "Emulator"


            | ModInstaller.AmdMode -> "AMD mode"

        // The add-on is part of what gets installed, so the button has to say
        // which way the switch is going.
        // One option is named; several are counted, so the text always fits
        // the button instead of running out past the edge of the sheet.
        // No option list on the button. It is a narrow control in a fixed-width
        // sheet, and any suffix - even a short one - ran out past its edge. The
        // options are on screen directly above it anyway.
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
        this.RaisePropertyChanged("IsDllNameCardVisible")
        this.RaisePropertyChanged("IsOptiScalerInstalled")
        this.RaisePropertyChanged("IsDx12Installed")
        this.RaisePropertyChanged("IsDx11Installed")
        this.RaisePropertyChanged("IsDx9Installed")
        this.RaisePropertyChanged("IsBit64Installed")
        this.RaisePropertyChanged("IsBit32Installed")
        this.RaisePropertyChanged("IsOptiDx12Installed")
        this.RaisePropertyChanged("IsOptiVulkanInstalled")
        this.RaisePropertyChanged("IsOptiNeuralInstalled")
        this.RaisePropertyChanged("IsNeuralAddonInstalled")
        this.RaisePropertyChanged("IsMfgUnlockInstalled")
        this.RaisePropertyChanged("IsMultipassInstalled")
        this.RaisePropertyChanged("IsDeepFriedInstalled")
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
                // Every new result arrives folded, whatever the last one was.
                isInstallResultExpanded <- false

                for name in
                    [ "HasInstallResult"; "InstallResultSummary"; "HasInstallResultDetails"
                      "IsInstallResultExpanded"; "InstallResultToggleText" ] do
                    this.RaisePropertyChanged(name)

    member this.HasInstallResult = not (String.IsNullOrWhiteSpace(installResultText))

    /// The headline alone: everything before the first bullet. "Switched. DX12
    /// 64-bit - ReShade + DLSS 5 installed" rather than the whole deployment
    /// log, which is what used to widen the sheet to fit it.
    member this.InstallResultSummary =
        let text = if isNull installResultText then "" else installResultText
        let cut = text.IndexOf(" • ")
        if cut > 0 then text.Substring(0, cut) else text

    /// Whether anything is folded behind the headline. A one-line message - an
    /// error, usually - gets no button, because there is nothing to unfold.
    member this.HasInstallResultDetails =
        let text = if isNull installResultText then "" else installResultText
        text.Contains(" • ")

    member this.IsInstallResultExpanded = isInstallResultExpanded

    member this.InstallResultToggleText =
        if isInstallResultExpanded then "HIDE" else "DETAILS"

    member this.ToggleInstallResult() =
        isInstallResultExpanded <- not isInstallResultExpanded
        this.RaisePropertyChanged("IsInstallResultExpanded")
        this.RaisePropertyChanged("InstallResultToggleText")

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

        installedNeural <-
            match manageCard with
            | Some card -> ModInstaller.installedNeuralAddon card.Game
            | None -> false

        let (mfgUnlock, multipass) =
            match manageCard with
            | Some card -> ModInstaller.installedRenoDxOptions card.Game
            | None -> (false, false)

        installedMfgUnlock <- mfgUnlock
        installedMultipass <- multipass

        installedDeepFried <-
            match manageCard with
            | Some card -> ModInstaller.installedDeepFried card.Game
            | None -> false

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

        // Last, once the route is known: an emulator's card depends on it, and
        // read before it (as it was) an install left the card hidden until
        // the sheet was opened again.
        this.RefreshDllNames()

        // The deep scan may have corrected the executable, so re-read what the
        // game is from the file we now believe in. Emulators have one route.
        if not isEmulatorTarget && not isAmdMode then this.DetectTarget(true)

    /// Re-runs the deep scan for the open game and refreshes the stored record.
    member this.AnalyzeManageTarget() =
        match manageCard with
        | None -> ()
        | Some card ->
            // The DLL names are kept between openings; a rescan reads them
            // from the disk again (a game update may have added a DLL).
            ModInstaller.forgetProxyNames card.Game
            // The rescan button asks about the anti-cheat again as well: a
            // game can have gained one in an update since the sheet opened.
            this.CheckAntiCheat(card)
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
        // The community panel belongs to the game that is open, and to no
        // other: folded away, and empty until this game is asked about (user:
        // "I open A, press show, go to B and find it already open with A's
        // numbers").
        this.ResetCommunityGlance()
        manageCard <- Some card

        // DLL NAME starts folded, with nothing left over from another game.
        dllNamesOpen <- false
        dllNameStatus <- ""
        optiCustomName <- ""
        reshadeCustomName <- ""
        this.RefreshDllNames()

        for name in
            [ "IsDllNamesOpen"; "DllNamesIcon"; "DllNameStatus"; "HasDllNameStatus"; "OptiCustomName"
              "ReShadeCustomName"; "CanRenameOptiCustom"; "CanRenameReShadeCustom" ] do
            this.RaisePropertyChanged(name)
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
        this.SetKeysStatus("")
        this.CheckAntiCheat(card)

        // Preselect whichever route a managed install already used, so the
        // sheet opens on "Remove" rather than offering to switch to itself.
        let (route, arch) = ModInstaller.installedRouteAndArch card.Game
        installedRoute <- route
        installedArch <- arch
        installedApi <- ModInstaller.installedOptiApi card.Game
        installedNeural <- ModInstaller.installedNeuralAddon card.Game

        let (mfgUnlock, multipass) = ModInstaller.installedRenoDxOptions card.Game
        installedMfgUnlock <- mfgUnlock
        installedMultipass <- multipass
        installedDeepFried <- ModInstaller.installedDeepFried card.Game

        // Open on whatever the recorded install actually used, so the sheet
        // offers Remove rather than a switch to itself.
        useNeuralAddon <- installedNeural
        useMfgUnlock <- installedMfgUnlock
        useMultipass <- installedMultipass
        useDeepFried <- installedDeepFried

        // The overlay is a choice per game: one this app installed opens on
        // what its install carried, any other on the last choice made.
        isOverlayEnabled <-
            match ModInstaller.installedOverlay card.Game with
            | Some carried -> carried
            | None -> overlayDefault

        this.RaisePropertyChanged("IsOverlayEnabled")
        this.RaisePropertyChanged("IsOverlayKeyVisible")

        // A fresh sheet: nothing has been picked in it yet, and whether this
        // game arrived with an install is what decides if detection may route
        // it at all.
        routeChosenByUser <- false
        sheetOpenedInstalled <- route <> ""

        if route = "optiscaler" then
            this.SetOptiApi(
                match installedApi with
                | "vulkan" -> ModInstaller.OptiVulkan
                // A manifest from before 1.2.1 can say "neural". That build is
                // now the only one, so it reads back as the DirectX 12 choice -
                // which is the slot it was hooked in anyway.
                | _ -> ModInstaller.OptiDx12
            )

        // An emulator has exactly one route, so none of the game-side choices
        // apply and the detection pass is skipped entirely.
        isEmulatorTarget <- card.Game.LauncherTypeName = "EMULATOR"
        this.RaisePropertyChanged("IsEmulatorTarget")
        this.RaisePropertyChanged("IsGameTarget")
        // The DLL NAME card depends on both the target kind and the route.
        this.RefreshDllNames()

        this.RaisePropertyChanged("IsAmdRouteActive")

        if isEmulatorTarget then
            // Which slot ReShade hooks. This has to sit *after* isEmulatorTarget
            // is set above, or it reads the previous sheet's value.
            //
            // Only a 1.2.7+ manifest records a real choice (`EmuApi`). Before
            // that every route stored a meaningless "dx12" in `Api`, so reading
            // that back would flip an emulator that was actually installed on
            // Vulkan. Empty means nobody chose, and the catalogue decides -
            // which is precisely what the older builds did.
            optiApi <-
                match ModInstaller.installedEmulatorApi card.Game with
                | "vulkan" -> ModInstaller.OptiVulkan
                | "dx12" -> ModInstaller.OptiDx12
                | _ ->
                    if EmulatorCatalog.reShadeApi card.ExecutablePath = EmulatorCatalog.vulkanApi then
                        ModInstaller.OptiVulkan
                    else
                        ModInstaller.OptiDx12

            this.ApplyInstallMode(ModInstaller.Emulator)
        elif isAmdMode then
            // AMD mode overrides detection entirely: one payload, no choices.
            this.ApplyInstallMode(ModInstaller.AmdMode)
        else
            match route with
            | "optiscaler" -> this.ApplyInstallMode(ModInstaller.OptiScalerMode)
            | "dx12" -> this.ApplyInstallMode(ModInstaller.Dx12Auto)
            | "dx11" -> this.ApplyInstallMode(ModInstaller.Dx11)
            | "dx9" -> this.ApplyInstallMode(ModInstaller.Dx9)
            | _ -> ()

            // SetInstallMode resets the build to that route's default, so the
            // recorded one is restored afterwards.
            if route <> "" then
                this.SetInstallArch(if arch = "32" then ModInstaller.Bit32 else ModInstaller.Bit64)

            // Nothing installed yet? Then what the game actually is decides.
            this.DetectTarget(true)

        // The setters above only notify when their value moved, and opening a
        // second game can leave them where they already were while the target
        // itself changed. One sweep settles the whole sheet.
        this.RaiseInstallModeState()

        // Everything was already measured during the scan, so the sheet opens
        // fully populated. Only a game we have never analyzed pays the cost.
        match AnalysisStore.tryGet card.Game with
        | Some cached ->
            this.ApplyAnalysis(cached)
            this.IsAnalyzing <- false
        | None ->
            this.RaiseManageInfo()
            this.AnalyzeManageTarget()

    // ---- Anti-cheat --------------------------------------------------------
    /// The yellow mark beside the game's name in the Manage sheet.
    member this.HasAntiCheat = manageAntiCheat.IsSome

    member this.AntiCheatText =
        match manageAntiCheat with
        | None -> ""
        | Some "" -> loc.AntiCheatWarning.Replace(" ({name})", "").Replace("({name})", "")
        | Some name -> loc.AntiCheatWarning.Replace("{name}", name)

    /// Off the UI thread: it walks the game's folder. The answer is kept only
    /// if the sheet still shows the same game when it arrives.
    member private this.CheckAntiCheat(card: GameCardViewModel) =
        manageAntiCheat <- None
        this.RaisePropertyChanged("HasAntiCheat")
        this.RaisePropertyChanged("AntiCheatText")
        let title = card.Title
        let folder = card.InstallDirectory
        let exe = card.ExecutablePath

        System.Threading.Tasks.Task.Run(fun () ->
            let found = try AntiCheat.detect title folder exe with _ -> None

            Dispatcher.UIThread.Post(fun () ->
                match manageCard with
                | Some current when obj.ReferenceEquals(current, card) ->
                    manageAntiCheat <- found
                    this.RaisePropertyChanged("HasAntiCheat")
                    this.RaisePropertyChanged("AntiCheatText")
                | _ -> ()))
        |> ignore

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
            let targetNeural = useNeuralAddon
            let targetMfgUnlock = useMfgUnlock
            let targetMultipass = useMultipass
            let targetDeepFried = useDeepFried
            let targetOverlay = this.OverlayOptions

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
                            let outcome =
                                ModInstaller.install game exePath plan target targetArch targetApi targetNeural targetMfgUnlock targetMultipass targetDeepFried targetOverlay (scaled 0.33 0.67)
                            (outcome.Success, "Switched. " + outcome.Message)
                    with ex ->
                        (false, ex.Message)

                Dispatcher.UIThread.Post(fun () ->
                    this.IsInstalling <- false

                    // A switch ends with the new route installed: the install chime.
                    if succeeded then
                        UiSounds.installDone ()
                        this.ApplyKeysToInstall()
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
                            if isInstallAction then
                                ModInstaller.install game exePath plan installMode installArch optiApi useNeuralAddon useMfgUnlock useMultipass useDeepFried this.OverlayOptions report
                            else
                                ModInstaller.uninstall game exePath plan report

                        (outcome.Success, outcome.Message)
                    with ex ->
                        (false, ex.Message)

                Dispatcher.UIThread.Post(fun () ->
                    this.IsInstalling <- false

                    // A calm chime for a finished install, the same notes falling
                    // for a removal. Nothing on a failure.
                    if succeeded then
                        if isInstallAction then
                            UiSounds.installDone ()
                            this.ApplyKeysToInstall()
                        else
                            UiSounds.uninstallDone ()
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
    member this.CreditsText = "Built by NODIX TECH"
    member this.CopyrightText = sprintf "© %d NODIX TECH. All rights reserved." DateTime.Now.Year

    // ---------------------------------------------------------------------
    // THIS MACHINE
    // ---------------------------------------------------------------------
    /// The card the app found, shown in the About card so the user can see for
    /// themselves what the install routes are reading. One cached registry
    /// read, so binding to it is free after the first.
    member this.DetectedGpuText =
        let name = SystemSpecs.gpuName ()
        if String.IsNullOrWhiteSpace(name) then "Not detected" else name

    member this.HasDetectedGpu = not (String.IsNullOrWhiteSpace(SystemSpecs.gpuName ()))

    /// An RTX 40 installs its own OptiScaler build, chosen by the card rather
    /// than by a switch - so the badge beside the name is the only place that
    /// tells the user it is happening.
    member this.IsRtx40Gpu = SystemSpecs.isRtx40 ()

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

    /// Finds the emulators the app knows by name, in the handful of folders
    /// they are actually installed in. It only ever adds: anything already in
    /// the list is left exactly as the user arranged it, so running this twice
    /// is harmless.
    /// The emulator half of a scan, without any of the busy-state bookkeeping:
    /// find what the catalogue knows, build a card for each new one and hand
    /// them to the UI thread. Returns how many were new.
    ///
    /// It reads the list it is comparing against from `emulators_cache.json`
    /// rather than the on-screen collection, because that file is written
    /// every time the collection changes and this runs off the UI thread.
    ///
    /// Both callers run it inside a background task; the caller owns
    /// `IsScanning` and the closing status line.
    member private this.RunEmulatorDetection() : int =
        let alreadyKnown =
            try
                GameScanner.loadCachedEmulators ()
                |> List.map (fun e ->
                    if isNull e.TargetExecutablePath then "" else e.TargetExecutablePath.ToLowerInvariant())
                |> Set.ofList
            with _ ->
                Set.empty

        let found =
            try
                EmulatorCatalog.scan ()
                |> List.filter (fun f -> not (alreadyKnown.Contains(f.ExePath.ToLowerInvariant())))
            with _ ->
                []

        let built = System.Collections.Generic.List<GameItem>()

        for entry in found do
            Dispatcher.UIThread.Post(fun () ->
                this.ScanStatusText <- sprintf "Reading %s (%s)..." entry.Display entry.System)

            try
                let folder = System.IO.Path.GetDirectoryName(entry.ExePath)

                let item =
                    { GameScanner.createCustomGameItem entry.Display folder entry.ExePath with
                        LauncherTypeName = "EMULATOR" }

                AnalysisStore.refreshExecutableOnly item |> ignore
                built.Add(item)
            with _ ->
                ()

        if built.Count > 0 then
            Dispatcher.UIThread.Post(fun () ->
                for item in built do
                    let alreadyThere =
                        allEmulators
                        |> Seq.exists (fun c ->
                            String.Equals(c.ExecutablePath, item.TargetExecutablePath, StringComparison.OrdinalIgnoreCase))

                    if not alreadyThere then allEmulators.Add(GameCardViewModel(item))

                this.RaisePropertyChanged("AllEmulatorsCount")
                filterEmulatorsList ()
                this.RaisePropertyChanged("HasEmulators")

                try
                    GameScanner.saveEmulatorsToCache [ for c in allEmulators -> c.Game ]
                with _ ->
                    ())

        built.Count

    /// The emulator scan on its own, from the Detect Emulators button.
    member this.DetectEmulators() =
        if not isScanning then
            this.IsScanning <- true
            this.ScanStatusText <- "Looking for emulators..."

            System.Threading.Tasks.Task.Run(fun () ->
                let added = this.RunEmulatorDetection()

                Dispatcher.UIThread.Post(fun () ->
                    this.IsScanning <- false

                    this.ScanStatusText <-
                        if added = 0 then
                            "No new emulators found"
                        else
                            sprintf "%d emulator(s) added" added))
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

                // The pick is the file, not a field in the game record: a scan
                // rebuilds every record from disk, and a pick stored there was
                // lost the next time the library was scanned.
                card.RefreshBanner()
                this.RaisePropertyChanged("CanRestoreCover")
            with _ ->
                ()

    /// Whether the game whose sheet is open is wearing a picture its owner
    /// chose - which is the only time there is anything to put back.
    member _.CanRestoreCover =
        match manageCard with
        | Some card -> card.HasCustomCover
        | None -> false

    /// Back to the artwork the scan found for this game. Does nothing when
    /// there was no pick, which is what the menu item does on a card nobody
    /// has changed.
    member this.RestoreGameCover(card: GameCardViewModel) =
        if not (isNull (box card)) && GameScanner.clearCustomCover card.Game then
            card.RefreshBanner()
            this.RaisePropertyChanged("CanRestoreCover")

    /// Right-click on a card: drop the title from the library without touching
    /// anything on disk. Its cached analysis goes with it.
    ///
    /// `alsoExclude` additionally records it, so the next scan does not simply
    /// find the same folder and put it back. Adding it by hand still works.
    member this.RemoveGame(card: GameCardViewModel, alsoExclude: bool) =
        if not (isNull (box card)) && alsoExclude then
            try
                GameScanner.addExcluded card.Game.TargetExecutablePath card.Game.Title
            with _ ->
                ()

        this.RemoveGame(card)

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

    /// The neural upstream build of OptiScaler, swapped the same way and just
    /// as unswitchable - the neural API has nothing to install without it.
    member this.OptiScalerNeuralState = ModInstaller.Payload.describeOptiScalerNeural ()

    /// The RTX 40 build, swapped and restored exactly like the one above.
    member this.OptiScalerRtx40State = ModInstaller.Payload.describeOptiScalerRtx40 ()

    member this.ReplaceOptiScalerRtx40(sourceDir: string) =
        let (ok, message) = ModInstaller.Payload.replaceOptiScalerRtx40 sourceDir
        this.PayloadStatusText <- message
        if ok then this.RaisePropertyChanged("OptiScalerRtx40State")

    member this.RestoreOptiScalerRtx40() =
        let (ok, message) = ModInstaller.Payload.restoreOptiScalerRtx40 ()
        this.PayloadStatusText <- message
        if ok then this.RaisePropertyChanged("OptiScalerRtx40State")

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

    member this.ReplaceOptiScalerNeural(sourceDir: string) =
        let (ok, message) = ModInstaller.Payload.replaceOptiScalerNeural sourceDir
        this.PayloadStatusText <- message
        if ok then this.RaisePropertyChanged("OptiScalerNeuralState")

    member this.RestoreOptiScalerNeural() =
        let (ok, message) = ModInstaller.Payload.restoreOptiScalerNeural ()
        this.PayloadStatusText <- message
        if ok then this.RaisePropertyChanged("OptiScalerNeuralState")

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
