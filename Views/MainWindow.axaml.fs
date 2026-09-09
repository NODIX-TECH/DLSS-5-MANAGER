namespace DLSS_5_MANAGER.Views

open System
open System.Diagnostics
open System.IO
open Avalonia
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Interactivity
open Avalonia.Markup.Xaml
open Avalonia.Platform.Storage
open Avalonia.Threading
open Avalonia.VisualTree
open DLSS_5_MANAGER.Services
open DLSS_5_MANAGER.ViewModels

type MainWindow() as this =
    inherit Window()

    // --- Card drag & drop state -------------------------------------------
    let mutable dragStartPos: Nullable<Point> = Nullable()
    let mutable draggedCard: GameCardViewModel option = None
    let mutable currentTargetCard: GameCardViewModel option = None
    let mutable isDraggingCard = false

    // --- Kinetic smooth-scroll state --------------------------------------
    // The wheel sets a target offset; a 60 fps timer eases the real offset
    // towards it, so the grid glides instead of jumping line by line.
    let mutable scrollTargetY = 0.0
    let mutable isScrollAnimating = false
    /// Whichever page the wheel last touched - the games grid or the settings.
    let mutable activeScrollViewer: ScrollViewer = null
    let smoothScrollTimer = DispatcherTimer(Interval = TimeSpan.FromMilliseconds(8.0))

    do
        this.InitializeComponent()

        // Clear TextBox focus whenever the user clicks anywhere outside of it
        this.AddHandler(
            InputElement.PointerPressedEvent,
            EventHandler<PointerPressedEventArgs>(fun _ e ->
                try
                    let topLevel = TopLevel.GetTopLevel(this)
                    if topLevel <> null && topLevel.FocusManager <> null then
                        let focused = topLevel.FocusManager.GetFocusedElement()
                        if not (isNull (box focused)) && focused :? TextBox then
                            let isClickInsideTextBox =
                                match e.Source with
                                | :? Visual as v ->
                                    let rec isDescendantOfTextBox (cur: Visual) =
                                        if isNull (box cur) then false
                                        elif cur :? TextBox then true
                                        else isDescendantOfTextBox (cur.GetVisualParent())

                                    isDescendantOfTextBox v
                                | _ -> false

                            if not isClickInsideTextBox then
                                let sink = this.FindControl<Control>("DummyFocusSink")
                                if sink <> null then sink.Focus() |> ignore else this.Focus() |> ignore
                with _ -> ()),
            RoutingStrategies.Tunnel
        )

        // Park the background animations whenever the window is not the one in
        // front. They are the app's largest continuous GPU cost and nobody is
        // watching them from behind another window.
        let setMotion (on: bool) =
            match this.DataContext with
            | :? MainViewModel as vm -> vm.IsWindowActive <- on
            | _ -> ()

        this.Activated.Add(fun _ -> setMotion true)
        this.Deactivated.Add(fun _ -> setMotion false)

        smoothScrollTimer.Tick.Add(fun _ ->
            let sv = activeScrollViewer
            if isNull (box sv) then
                isScrollAnimating <- false
                smoothScrollTimer.Stop()
            else
                let current = sv.Offset.Y
                let diff = scrollTargetY - current

                if abs diff < 0.4 then
                    sv.Offset <- Vector(sv.Offset.X, scrollTargetY)
                    isScrollAnimating <- false
                    smoothScrollTimer.Stop()
                else
                    // Exponential ease-out: fast start, soft settle (no overshoot).
                    sv.Offset <- Vector(sv.Offset.X, current + diff * 0.16))

    member private this.InitializeComponent() = AvaloniaXamlLoader.Load(this)

    // =====================================================================
    // WINDOW CHROME
    // =====================================================================
    member this.OnHeaderPointerPressed(sender: obj, e: PointerPressedEventArgs) =
        if e.GetCurrentPoint(this).Properties.IsLeftButtonPressed then this.BeginMoveDrag(e)

    member this.OnMinimizeClicked(sender: obj, e: RoutedEventArgs) =
        this.WindowState <- WindowState.Minimized

    member this.OnMaximizeClicked(sender: obj, e: RoutedEventArgs) =
        if this.WindowState = WindowState.Maximized then
            this.WindowState <- WindowState.Normal
        else
            this.WindowState <- WindowState.Maximized

    member this.OnCloseClicked(sender: obj, e: RoutedEventArgs) = this.Close()

    // =====================================================================
    // KINETIC SMOOTH SCROLLING
    // =====================================================================
    member this.OnGamesWheelChanged(sender: obj, e: PointerWheelEventArgs) =
        this.SmoothScroll(this.FindControl<ScrollViewer>("GamesScrollViewer"), e)

    /// The settings page grew past a single screen, so it gets the same kinetic
    /// wheel handling as the grid instead of the default line-by-line stepping.
    member this.OnSettingsWheelChanged(sender: obj, e: PointerWheelEventArgs) =
        this.SmoothScroll(this.FindControl<ScrollViewer>("SettingsScrollViewer"), e)

    member private this.SmoothScroll(sv: ScrollViewer, e: PointerWheelEventArgs) =
        // Switching between the two pages restarts the easing on the new one.
        if not (Object.ReferenceEquals(sv, activeScrollViewer)) then
            activeScrollViewer <- sv
            isScrollAnimating <- false

        if not (isNull (box sv)) then
            let maxOffset = max 0.0 (sv.Extent.Height - sv.Viewport.Height)
            if maxOffset > 0.0 then
                // Re-sync the target whenever a new gesture starts.
                if not isScrollAnimating then scrollTargetY <- sv.Offset.Y

                let step = 125.0
                let proposed = scrollTargetY - (e.Delta.Y * step)
                scrollTargetY <- Math.Clamp(proposed, 0.0, maxOffset)

                if not isScrollAnimating then
                    isScrollAnimating <- true
                    smoothScrollTimer.Start()

                e.Handled <- true

    // =====================================================================
    // CARD DRAG & DROP REORDERING
    // =====================================================================
    member private this.PerformDropSwap(vm: MainViewModel) =
        // A press that never turned into a drag is a plain click: open the sheet.
        let clickedCard =
            if not isDraggingCard && dragStartPos.HasValue then draggedCard else None

        if isDraggingCard && draggedCard.IsSome then
            match currentTargetCard with
            | Some target when not (Object.ReferenceEquals(target, draggedCard.Value)) ->
                target.IsDragTarget <- false
                vm.SwapCards(draggedCard.Value, target)
            | _ -> ()

            draggedCard.Value.IsDragging <- false
        elif draggedCard.IsSome then
            draggedCard.Value.IsDragging <- false

        match clickedCard with
        | Some card when not vm.IsManageOpen -> vm.OpenManage(card)
        | _ -> ()

        match currentTargetCard with
        | Some t -> t.IsDragTarget <- false
        | None -> ()

        vm.IsDraggingCard <- false
        vm.DraggedCard <- None
        isDraggingCard <- false
        draggedCard <- None
        currentTargetCard <- None
        dragStartPos <- Nullable()

    member this.OnCardPointerPressed(sender: obj, e: PointerPressedEventArgs) =
        let point = e.GetCurrentPoint(this)
        if point.Properties.IsLeftButtonPressed then
            match sender with
            | :? Control as ctrl ->
                match ctrl.DataContext with
                | :? GameCardViewModel as card ->
                    dragStartPos <- Nullable(e.GetPosition(this))
                    draggedCard <- Some card
                    currentTargetCard <- None
                    isDraggingCard <- false
                | _ -> ()
            | _ -> ()

    member this.OnCardPointerMoved(sender: obj, e: PointerEventArgs) =
        if dragStartPos.HasValue && draggedCard.IsSome then
            let currentPos = e.GetPosition(this)
            let deltaX = currentPos.X - dragStartPos.Value.X
            let deltaY = currentPos.Y - dragStartPos.Value.Y
            let dist = Math.Sqrt(deltaX * deltaX + deltaY * deltaY)

            match this.DataContext with
            | :? MainViewModel as vm ->
                if dist > 8.0 && not isDraggingCard then
                    isDraggingCard <- true
                    draggedCard.Value.IsDragging <- true
                    vm.DraggedCard <- draggedCard
                    vm.IsDraggingCard <- true

                if isDraggingCard then
                    // 1. Floating ghost follows the cursor exactly
                    let ghost = this.FindControl<Border>("FloatingDragGhost")
                    if ghost <> null then
                        Canvas.SetLeft(ghost, currentPos.X - 108.0)
                        Canvas.SetTop(ghost, currentPos.Y - 143.0)

                    // 2. Direct bounding-box hit test against the visible cards
                    let mutable newTarget: GameCardViewModel option = None
                    let itemsControl = this.FindControl<ItemsControl>("GamesItemsControl")
                    if itemsControl <> null then
                        for descendant in itemsControl.GetVisualDescendants() do
                            if newTarget.IsNone then
                                match descendant with
                                | :? StackPanel as sp when sp.Classes.Contains("GameCardItem") && sp.IsVisible ->
                                    let cardPos = sp.TranslatePoint(Point(0.0, 0.0), this)
                                    if cardPos.HasValue then
                                        let w = if sp.Bounds.Width > 10.0 then sp.Bounds.Width else 216.0
                                        let h = if sp.Bounds.Height > 10.0 then sp.Bounds.Height else 320.0
                                        let rect = Rect(cardPos.Value.X, cardPos.Value.Y, w, h)
                                        if rect.Contains(currentPos) then
                                            match sp.DataContext with
                                            | :? GameCardViewModel as targetVm when
                                                not (Object.ReferenceEquals(targetVm, draggedCard.Value))
                                                ->
                                                newTarget <- Some targetVm
                                            | _ -> ()
                                | _ -> ()

                    // 3. Move the highlight when the hovered target changes
                    if currentTargetCard <> newTarget then
                        match currentTargetCard with
                        | Some prevTarget -> prevTarget.IsDragTarget <- false
                        | None -> ()

                        currentTargetCard <- newTarget

                        match currentTargetCard with
                        | Some target -> target.IsDragTarget <- true
                        | None -> ()
            | _ -> ()

    member this.OnCardPointerReleased(sender: obj, e: PointerReleasedEventArgs) =
        e.Pointer.Capture(null)
        match this.DataContext with
        | :? MainViewModel as vm ->
            this.PerformDropSwap(vm)
            e.Handled <- true
        | _ -> ()

    member this.OnCardPointerCaptureLost(sender: obj, e: PointerCaptureLostEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> this.PerformDropSwap(vm)
        | _ -> ()

    // =====================================================================
    // NAVIGATION & LIBRARY ACTIONS
    // =====================================================================
    member this.OnTabGamesClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.ShowGames()
        | _ -> ()

    member this.OnTabEmulatorsClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.ShowEmulators()
        | _ -> ()

    // =====================================================================
    // COMMUNITY
    // =====================================================================
    member this.OnTabCommunityClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.ShowCommunity()
        | _ -> ()

    member this.OnCommunityTutorialsClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> this.OpenExternal(vm.Community.TutorialsUrl)
        | _ -> ()

    member this.OnCommunityRefreshClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.Community.Refresh()
        | _ -> ()

    /// The filter chips carry their value in Tag, so one handler serves the
    /// whole row and adding a route later is a line of XAML.
    member this.OnCommunityRouteClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            vm.Community.SetRouteFilter(if isNull ctrl.Tag then "" else string ctrl.Tag)
        | _ -> ()

    member this.OnCommunityResultClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            vm.Community.SetResultFilter(if isNull ctrl.Tag then "" else string ctrl.Tag)
        | _ -> ()

    member this.OnCommunityClaimNameClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.Community.ClaimName()
        | _ -> ()

    member this.OnCommunityGameClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            match ctrl.DataContext with
            | :? CommunityGameViewModel as game -> vm.Community.OpenGame(game)
            | _ -> ()
        | _ -> ()

    member this.OnCommunitySheetCloseClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.Community.CloseSheet()
        | _ -> ()

    member this.OnCommunitySheetRouteClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            vm.Community.SetSheetRoute(if isNull ctrl.Tag then "" else string ctrl.Tag)
        | _ -> ()

    member this.OnCommunityCommentsClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            match ctrl.DataContext with
            | :? CommunityReportViewModel as report -> vm.Community.ToggleComments(report)
            | _ -> ()
        | _ -> ()

    member this.OnCommunityReplyClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            match ctrl.DataContext with
            | :? CommunityReportViewModel as report -> vm.Community.SendReply(report)
            | _ -> ()
        | _ -> ()

    /// The five emoji buttons share a handler; Tag holds the slot number the
    /// server stores the count in.
    member this.OnCommunityReactClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            match ctrl.DataContext, Int32.TryParse(string ctrl.Tag) with
            | (:? CommunityReportViewModel as report), (true, slot) -> vm.Community.React(report, slot)
            | _ -> ()
        | _ -> ()

    // ---- the composer ---------------------------------------------------
    member this.OnShareToCommunityClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.ShareToCommunity()
        | _ -> ()

    member this.OnComposerCloseClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.Community.CloseComposer()
        | _ -> ()

    member this.OnComposerStatusClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            vm.Community.SetComposeStatus(if isNull ctrl.Tag then "working" else string ctrl.Tag)
        | _ -> ()

    member this.OnComposerDetectSpecsClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.Community.DetectSpecs()
        | _ -> ()

    member this.OnComposerClearSpecsClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.Community.ClearSpecs()
        | _ -> ()

    member this.OnComposerPostClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.Community.SubmitReport()
        | _ -> ()

    /// Emulator cards do not drag or reorder, so a plain release opens them.
    member this.OnEmulatorCardPressed(sender: obj, e: PointerReleasedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) when e.InitialPressMouseButton = MouseButton.Left ->
            match ctrl.DataContext with
            | :? GameCardViewModel as card when not vm.IsManageOpen -> vm.OpenManage(card)
            | _ -> ()
        | _ -> ()

    member this.OnRemoveEmulatorClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            match ctrl.DataContext with
            | :? GameCardViewModel as card -> vm.RemoveEmulator(card)
            | _ -> ()
        | _ -> ()

    member this.OnAddEmulatorClicked(sender: obj, e: RoutedEventArgs) =
        let options = FilePickerOpenOptions()
        options.Title <- "Select the emulator executable (.exe)"
        options.AllowMultiple <- false
        let fileType = FilePickerFileType("Executable Files (*.exe)")
        fileType.Patterns <- [| "*.exe" |]
        options.FileTypeFilter <- [| fileType |]

        async {
            let! files = this.StorageProvider.OpenFilePickerAsync(options) |> Async.AwaitTask

            if files <> null && files.Count > 0 then
                match this.DataContext with
                | :? MainViewModel as vm ->
                    vm.ShowEmulators()
                    vm.AddEmulatorExecutable(files.[0].Path.LocalPath)
                | _ -> ()
        }
        |> Async.StartImmediate

    member this.OnDetectEmulatorsClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            vm.ShowEmulators()
            vm.DetectEmulators()
        | _ -> ()

    member this.OnEmulatorsWheelChanged(sender: obj, e: PointerWheelEventArgs) =
        this.SmoothScroll(this.FindControl<ScrollViewer>("EmulatorsScrollViewer"), e)

    member this.OnSettingsClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.ToggleSettings()
        | _ -> ()

    member this.OnBackToGamesClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.CloseSettings()
        | _ -> ()

    member this.OnSearchClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            vm.ToggleSearch()
            if vm.IsSearchOpen then
                let searchBox =
                    let sb1 = this.FindControl<TextBox>("SearchInputBox")
                    let sb2 = this.FindControl<TextBox>("SearchInputBoxSidebar")
                    if sb1 <> null && sb1.IsVisible then sb1
                    elif sb2 <> null && sb2.IsVisible then sb2
                    elif sb1 <> null then sb1
                    else sb2

                if searchBox <> null then
                    searchBox.Focus() |> ignore
                    searchBox.SelectAll()
        | _ -> ()

    member this.OnRescanClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.StartScanAsync()
        | _ -> ()

    member this.OnClearCacheClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.ClearCacheAndRescan()
        | _ -> ()

    member this.OnAddCustomFolderClicked(sender: obj, e: RoutedEventArgs) =
        let options = FolderPickerOpenOptions()
        options.Title <- "Select Game Folder or Library"
        options.AllowMultiple <- false

        async {
            let! folders = this.StorageProvider.OpenFolderPickerAsync(options) |> Async.AwaitTask
            if folders <> null && folders.Count > 0 then
                let path = folders.[0].Path.LocalPath
                match this.DataContext with
                | :? MainViewModel as vm -> vm.AddCustomFolder(path)
                | _ -> ()
        }
        |> Async.StartImmediate

    member this.OnAddSingleGameFileClicked(sender: obj, e: RoutedEventArgs) =
        let options = FilePickerOpenOptions()
        options.Title <- "Select Game Executable (.exe)"
        options.AllowMultiple <- false
        let fileType = FilePickerFileType("Executable Files (*.exe)")
        fileType.Patterns <- [| "*.exe" |]
        options.FileTypeFilter <- [| fileType |]

        async {
            let! files = this.StorageProvider.OpenFilePickerAsync(options) |> Async.AwaitTask
            if files <> null && files.Count > 0 then
                let exePath = files.[0].Path.LocalPath
                match this.DataContext with
                | :? MainViewModel as vm -> vm.AddSingleGameExecutable(exePath)
                | _ -> ()
        }
        |> Async.StartImmediate

    /// Right-click flyout on a card: swap its artwork for a picture of the
    /// user's own.
    member this.OnChangeCoverClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            match ctrl.DataContext with
            | :? GameCardViewModel as card ->
                let options = FilePickerOpenOptions()
                options.Title <- "Choose a cover image"
                options.AllowMultiple <- false

                let fileType = FilePickerFileType("Images")
                fileType.Patterns <- [| "*.png"; "*.jpg"; "*.jpeg"; "*.bmp"; "*.webp"; "*.gif" |]
                options.FileTypeFilter <- [| fileType |]

                async {
                    let! files = this.StorageProvider.OpenFilePickerAsync(options) |> Async.AwaitTask
                    if files <> null && files.Count > 0 then
                        vm.SetGameCover(card, files.[0].Path.LocalPath)
                }
                |> Async.StartImmediate
            | _ -> ()
        | _ -> ()

    /// Right-click flyout on a card: take the title out of the library.
    member this.OnRemoveGameClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            match ctrl.DataContext with
            | :? GameCardViewModel as card -> vm.RemoveGame(card)
            | _ -> ()
        | _ -> ()

    member this.OnToggleSectionClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) when not (isNull ctrl.Tag) ->
            vm.ToggleSection(string ctrl.Tag)
        | _ -> ()

    // =====================================================================
    // PAYLOAD ROWS, RESHADE SETUP, OPTISCALER, EXTRAS
    // =====================================================================
    member private this.PickFile(title: string, patterns: string[],typeLabel: string) =
        let options = FilePickerOpenOptions()
        options.Title <- title
        options.AllowMultiple <- false
        let fileType = FilePickerFileType(typeLabel)
        fileType.Patterns <- patterns
        options.FileTypeFilter <- [| fileType |]
        options

    member this.OnReplacePayloadRowClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            match ctrl.DataContext with
            | :? PayloadRowViewModel as row ->
                let extension = Path.GetExtension(row.Key)
                let options = this.PickFile("Select your own " + row.Key, [| "*" + extension |], row.Key)

                async {
                    let! files = this.StorageProvider.OpenFilePickerAsync(options) |> Async.AwaitTask
                    if files <> null && files.Count > 0 then
                        vm.ReplacePayloadFile(row.Key, files.[0].Path.LocalPath)
                }
                |> Async.StartImmediate
            | _ -> ()
        | _ -> ()

    member this.OnRestorePayloadRowClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            match ctrl.DataContext with
            | :? PayloadRowViewModel as row -> vm.RestorePayloadFile(row.Key)
            | _ -> ()
        | _ -> ()

    member this.OnReplaceReShadeClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            let options = this.PickFile("Select a ReShade setup executable", [| "*.exe" |], "ReShade setup (*.exe)")

            async {
                let! files = this.StorageProvider.OpenFilePickerAsync(options) |> Async.AwaitTask
                if files <> null && files.Count > 0 then
                    vm.ReplaceReShadeSetup(files.[0].Path.LocalPath)
            }
            |> Async.StartImmediate
        | _ -> ()

    member this.OnRestoreReShadeClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.RestoreReShadeSetup()
        | _ -> ()

    member this.OnReplaceOptiScalerClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            let options = FolderPickerOpenOptions()
            options.Title <- "Select the OptiScaler folder (must contain OptiScaler.dll)"
            options.AllowMultiple <- false

            async {
                let! folders = this.StorageProvider.OpenFolderPickerAsync(options) |> Async.AwaitTask
                if folders <> null && folders.Count > 0 then
                    vm.ReplaceOptiScaler(folders.[0].Path.LocalPath)
            }
            |> Async.StartImmediate
        | _ -> ()

    member this.OnReplaceOptiScalerNeuralClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            let options = FolderPickerOpenOptions()
            options.Title <- "Select the OptiScaler neural-upstream folder (must contain OptiScaler.dll)"
            options.AllowMultiple <- false

            async {
                let! folders = this.StorageProvider.OpenFolderPickerAsync(options) |> Async.AwaitTask
                if folders <> null && folders.Count > 0 then
                    vm.ReplaceOptiScalerNeural(folders.[0].Path.LocalPath)
            }
            |> Async.StartImmediate
        | _ -> ()

    member this.OnRestoreOptiScalerNeuralClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.RestoreOptiScalerNeural()
        | _ -> ()

    /// Several at once, so a whole updated payload can go in one pick; only
    /// the names that already exist in the folder are used.
    member this.OnReplaceAmdPayloadClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            let options = FilePickerOpenOptions()
            options.Title <- "Select replacement files for the AMD payload"
            options.AllowMultiple <- true

            async {
                let! files = this.StorageProvider.OpenFilePickerAsync(options) |> Async.AwaitTask

                if files <> null && files.Count > 0 then
                    vm.ReplaceAmdPayload([ for file in files -> file.Path.LocalPath ])
            }
            |> Async.StartImmediate
        | _ -> ()

    member this.OnRestoreAmdPayloadClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.RestoreAmdPayload()
        | _ -> ()

    member this.OnRestoreOptiScalerClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.RestoreOptiScaler()
        | _ -> ()

    member this.OnAddExtraFolderClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            let options = FolderPickerOpenOptions()
            options.Title <- "Select a folder to install with every mod"
            options.AllowMultiple <- false

            async {
                let! folders = this.StorageProvider.OpenFolderPickerAsync(options) |> Async.AwaitTask
                if folders <> null && folders.Count > 0 then
                    vm.AddExtras([ folders.[0].Path.LocalPath, true ])
            }
            |> Async.StartImmediate
        | _ -> ()

    /// Multiple files at once, so picking everything inside a folder puts those
    /// files beside the game rather than the folder itself.
    member this.OnAddExtraFilesClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            let options = FilePickerOpenOptions()
            options.Title <- "Select files to install with every mod"
            options.AllowMultiple <- true

            async {
                let! files = this.StorageProvider.OpenFilePickerAsync(options) |> Async.AwaitTask

                if files <> null && files.Count > 0 then
                    vm.AddExtras([ for file in files -> file.Path.LocalPath, false ])
            }
            |> Async.StartImmediate
        | _ -> ()

    member this.OnToggleExtraModeClicked(sender: obj, e: RoutedEventArgs) =
        match sender with
        | :? Control as ctrl when not (isNull ctrl.Tag) ->
            match ctrl.DataContext with
            | :? ExtraRowViewModel as row -> row.ToggleMode(string ctrl.Tag)
            | _ -> ()
        | _ -> ()

    member this.OnRemoveExtraClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            match ctrl.DataContext with
            | :? ExtraRowViewModel as row -> vm.RemoveExtra(row)
            | _ -> ()
        | _ -> ()


    // =====================================================================
    // MANAGE SHEET
    // =====================================================================
    member this.OnSetOptiScalerMode(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.SetInstallMode(ModInstaller.OptiScalerMode)
        | _ -> ()

    member this.OnSetDx12Mode(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.SetInstallMode(ModInstaller.Dx12Auto)
        | _ -> ()

    member this.OnSetDx11Mode(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.SetInstallMode(ModInstaller.Dx11)
        | _ -> ()

    member this.OnSetDx9Mode(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.SetInstallMode(ModInstaller.Dx9)
        | _ -> ()

    member this.OnSetOptiDx12(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.SetOptiApi(ModInstaller.OptiDx12)
        | _ -> ()

    member this.OnSetOptiVulkan(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.SetOptiApi(ModInstaller.OptiVulkan)
        | _ -> ()

    member this.OnSetOptiNeural(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.SetOptiApi(ModInstaller.OptiNeural)
        | _ -> ()

    member this.OnSetNeuralAddonOn(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.SetNeuralAddon(true)
        | _ -> ()

    member this.OnSetNeuralAddonOff(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.SetNeuralAddon(false)
        | _ -> ()

    member this.OnSetOverlayOn(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.IsOverlayEnabled <- true
        | _ -> ()

    member this.OnSetOverlayOff(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.IsOverlayEnabled <- false
        | _ -> ()

    member this.OnSetBit64(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.SetInstallArch(ModInstaller.Bit64)
        | _ -> ()

    member this.OnSetBit32(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.SetInstallArch(ModInstaller.Bit32)
        | _ -> ()

    member this.OnToggleTargetDetailsClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.ToggleTargetDetails()
        | _ -> ()

    member this.OnManageCloseClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.CloseManage()
        | _ -> ()

    member this.OnManageBackdropPressed(sender: obj, e: PointerPressedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            vm.CloseManage()
            e.Handled <- true
        | _ -> ()

    member this.OnOpenGameFolderClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            try
                if not (String.IsNullOrWhiteSpace(vm.ManageExePath)) && File.Exists(vm.ManageExePath) then
                    // Opens Explorer with the executable already selected.
                    Process.Start(ProcessStartInfo("explorer.exe", sprintf "/select,\"%s\"" vm.ManageExePath))
                    |> ignore
                elif not (String.IsNullOrWhiteSpace(vm.ManageFolder)) && Directory.Exists(vm.ManageFolder) then
                    Process.Start(ProcessStartInfo(vm.ManageFolder, UseShellExecute = true)) |> ignore
            with _ ->
                ()
        | _ -> ()

    member this.OnChangeExecutableClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            let options = FilePickerOpenOptions()
            options.Title <- "Select the real game executable"
            options.AllowMultiple <- false
            let fileType = FilePickerFileType("Executable Files (*.exe)")
            fileType.Patterns <- [| "*.exe" |]
            options.FileTypeFilter <- [| fileType |]

            async {
                let! files = this.StorageProvider.OpenFilePickerAsync(options) |> Async.AwaitTask
                if files <> null && files.Count > 0 then
                    vm.SetManageExecutable(files.[0].Path.LocalPath)
            }
            |> Async.StartImmediate
        | _ -> ()

    member this.OnInstallDlssClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.StartInstall()
        | _ -> ()

    member this.OnSwitchModeClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.StartSwitch()
        | _ -> ()

    member this.OnUninstallDlssClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.StartUninstall()
        | _ -> ()

    /// Opens a link in the user's own browser. Failures are silent by design:
    /// a machine with no browser association must not throw at the user.
    member private this.OpenExternal(url: string) =
        try
            if not (String.IsNullOrWhiteSpace(url)) then
                Process.Start(ProcessStartInfo(url, UseShellExecute = true)) |> ignore
        with _ ->
            ()

    member this.OnDismissSupportPromptClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.DismissSupportPrompt()
        | _ -> ()

    /// Opening Ko-fi answers the prompt, so it closes behind the click.
    member this.OnSupportPromptDonateClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            this.OpenExternal(vm.DonateUrl)
            vm.DismissSupportPrompt()
        | _ -> ()

    member this.OnDonateClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> this.OpenExternal(vm.DonateUrl)
        | _ -> ()

    member this.OnTutorialsClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> this.OpenExternal(vm.TutorialsUrl)
        | _ -> ()

    /// The header pill only exists while an update is waiting, so it always
    /// goes straight to the download page.
    member this.OnUpdateBadgeClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> this.OpenExternal(vm.DownloadPageUrl)
        | _ -> ()

    /// First press checks GitHub; once an update is known it opens the
    /// official download page instead.
    member this.OnCheckUpdatesClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            if vm.HasUpdateAvailable then
                try
                    Process.Start(ProcessStartInfo(vm.DownloadPageUrl, UseShellExecute = true)) |> ignore
                with _ ->
                    ()
            else
                vm.CheckForUpdates()
        | _ -> ()

    member this.OnSetTopBarLayout(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.SetLayoutMode(false)
        | _ -> ()

    member this.OnSetSidebarLayout(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.SetLayoutMode(true)
        | _ -> ()
