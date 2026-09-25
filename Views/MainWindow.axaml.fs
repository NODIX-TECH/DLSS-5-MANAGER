namespace DLSS_5_MANAGER.Views

open System
open System.Diagnostics
open System.IO
open System.Threading.Tasks
open Avalonia
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Input.Platform
open Avalonia.Interactivity
open Avalonia.Markup.Xaml
open Avalonia.Platform.Storage
open Avalonia.Threading
open Avalonia.VisualTree
open DLSS_5_MANAGER.Services
open DLSS_5_MANAGER.ViewModels

/// Where and how big the window was when it closed, so it opens the same way:
/// a user who made it bigger by hand, or maximised it, got the small default
/// back on every launch. Its own `window.json`, like `background.json` - a
/// field in `AppSettings` would mean touching every `saveSettings` site.
module WindowPlacement =
    [<CLIMutable>]
    type Saved =
        { /// Top-left of the NORMAL (not maximised) window, screen pixels.
          X: int
          Y: int
          /// Its size, device-independent pixels (Width / Height).
          Width: float
          Height: float
          Maximized: bool }

    let private path () =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS5Manager", "window.json")

    let load () : Saved option =
        try
            let file = path ()
            if File.Exists(file) then
                let s = System.Text.Json.JsonSerializer.Deserialize<Saved>(File.ReadAllText(file))
                if s.Width >= 900.0 && s.Height >= 600.0 then Some s else None
            else None
        with _ -> None

    let save (s: Saved) =
        try
            let file = path ()
            Directory.CreateDirectory(Path.GetDirectoryName(file)) |> ignore
            File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(s))
        with _ -> ()

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
    let mutable screenShutdownComplete = false

    /// The last size and place the window had while neither maximised nor
    /// minimised - what un-maximising gives back, and what is saved.
    let mutable normalBounds: PixelPoint * float * float = (PixelPoint(0, 0), 1280.0, 820.0)
    let mutable wasMaximized = false

    do
        this.InitializeComponent()

        // The size and place it closed with (WindowPlacement). A place no
        // connected screen shows any more - a monitor unplugged since - is
        // dropped for the centre of the screen.
        match WindowPlacement.load () with
        | Some saved ->
            this.Width <- saved.Width
            this.Height <- saved.Height
            this.WindowStartupLocation <- WindowStartupLocation.Manual
            this.Position <- PixelPoint(saved.X, saved.Y)
            normalBounds <- (PixelPoint(saved.X, saved.Y), saved.Width, saved.Height)
            wasMaximized <- saved.Maximized

            this.Opened.Add(fun _ ->
                let screens = this.Screens

                let visible =
                    not (isNull screens)
                    && not (isNull (screens.ScreenFromPoint(PixelPoint(saved.X + 40, saved.Y + 20))))

                if not visible && not (isNull screens) && not (isNull screens.Primary) then
                    let area = screens.Primary.WorkingArea
                    let scale = screens.Primary.Scaling
                    let w = int (saved.Width * scale)
                    let h = int (saved.Height * scale)
                    this.Position <- PixelPoint(area.X + max 0 ((area.Width - w) / 2), area.Y + max 0 ((area.Height - h) / 2))

                if saved.Maximized then
                    this.WindowState <- WindowState.Maximized)
        | None -> ()

        let remember () =
            if this.WindowState = WindowState.Normal then
                // ClientSize: what a drag on the grips changes (Width/Height
                // keep the value they were given).
                normalBounds <- (this.Position, this.ClientSize.Width, this.ClientSize.Height)

            if this.WindowState <> WindowState.Minimized then
                wasMaximized <- (this.WindowState = WindowState.Maximized)

        // Read once things have settled: maximising reports the new size
        // before the new state, and read at once the maximised size was kept
        // as the normal one - un-maximising after a restart filled the screen.
        let rememberLater () = Dispatcher.UIThread.Post((fun () -> remember ()), DispatcherPriority.Background)

        this.PositionChanged.Add(fun _ -> rememberLater ())
        this.PropertyChanged.Add(fun args ->
            if args.Property = Window.WindowStateProperty
               || args.Property = Window.ClientSizeProperty
               || args.Property = Visual.BoundsProperty then
                rememberLater ())

        this.Closed.Add(fun _ ->
            let position, width, height = normalBounds
            WindowPlacement.save
                { X = position.X
                  Y = position.Y
                  Width = width
                  Height = height
                  Maximized = wasMaximized })
        this.Closing.Add(fun e ->
            match this.DataContext with
            | :? MainViewModel as vm when vm.Screen.IsRunning && not screenShutdownComplete ->
                e.Cancel <- true
                task {
                    do! vm.Screen.StopAsync()
                    screenShutdownComplete <- true
                    this.Close()
                } |> ignore
            | _ -> ())

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

        // The resize grips belong to a window that can be sized by hand.
        // Maximised, its edges are the screen's, and a resize cursor there
        // would only promise something that cannot happen.
        this.PropertyChanged.Add(fun args ->
            if args.Property = Window.WindowStateProperty then
                match this.FindControl<Panel>("ResizeGrips") with
                | null -> ()
                | grips -> grips.IsVisible <- (this.WindowState = WindowState.Normal))

        // The community grid asks for its next page when it is scrolled near its
        // end - but a grid shorter than the window (maximised, a first page of
        // twenty on a big screen) has nothing to scroll, and the next page never
        // came. So after every layout it is asked again whether it reaches a
        // screen past the window; LoadMore ignores the call while a page is on
        // its way or when there is nothing more.
        match this.FindControl<ScrollViewer>("CommunityScrollViewer") with
        | null -> ()
        | grid ->
            grid.LayoutUpdated.Add(fun _ ->
                if grid.IsEffectivelyVisible && grid.Viewport.Height > 0.0
                   && grid.Extent.Height - grid.Viewport.Height - grid.Offset.Y < grid.Viewport.Height then
                    match this.DataContext with
                    | :? MainViewModel as vm -> vm.Community.LoadMore()
                    | _ -> ())

        // The chat box: Ctrl+V may carry a picture, which a TextBox ignores.
        // Tunnel, so this sees the key before the box pastes text on its own.
        match this.FindControl<TextBox>("PulseDraftBox") with
        | null -> ()
        | chatBox ->
            chatBox.AddHandler(
                InputElement.KeyDownEvent,
                EventHandler<KeyEventArgs>(fun _ e ->
                    if e.Key = Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control) then
                        e.Handled <- true
                        this.PasteIntoChat(chatBox)
                    // Enter sends; Shift+Enter starts a new line. The box accepts
                    // returns, so plain Enter is taken here, before the box turns
                    // it into a line break of its own.
                    elif e.Key = Key.Enter && not (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) then
                        e.Handled <- true

                        match this.DataContext with
                        | :? MainViewModel as vm -> vm.Pulse.Send()
                        | _ -> ()),
                RoutingStrategies.Tunnel
            )

        // The private chat's box behaves exactly like the public one: Ctrl+V can
        // carry a picture, Enter sends, Shift+Enter starts a new line.
        match this.FindControl<TextBox>("DmDraftBox") with
        | null -> ()
        | dmBox ->
            dmBox.AddHandler(
                InputElement.KeyDownEvent,
                EventHandler<KeyEventArgs>(fun _ e ->
                    if e.Key = Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control) then
                        e.Handled <- true
                        this.PasteIntoDm(dmBox)
                    elif e.Key = Key.Enter && not (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) then
                        e.Handled <- true

                        match this.DataContext with
                        | :? MainViewModel as vm -> vm.Dm.Send()
                        | _ -> ()),
                RoutingStrategies.Tunnel
            )

        // The reply alert watches from the moment the window is up. It sends
        // nothing until this device has written in the chat at least once.
        this.Opened.Add(fun _ ->
            match this.DataContext with
            | :? MainViewModel as vm ->
                vm.Pulse.StartWatch()
                vm.StartDownloadsHint()
            | _ -> ())

        // Esc closes the chat's picture viewer.
        this.AddHandler(
            InputElement.KeyDownEvent,
            EventHandler<KeyEventArgs>(fun _ e ->
                match this.DataContext with
                | :? MainViewModel as vm when e.Key = Key.Escape && vm.Dm.IsViewerOpen ->
                    vm.Dm.CloseViewer()
                | :? MainViewModel as vm when e.Key = Key.Escape && vm.Pulse.IsViewerOpen ->
                    vm.Pulse.CloseViewer()
                    e.Handled <- true
                | :? MainViewModel as vm when e.Key = Key.Escape && vm.IsAssetsOpen ->
                    vm.CloseAssets()
                    e.Handled <- true
                | _ -> ()),
            RoutingStrategies.Tunnel
        )

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

    /// One of the grips around the frame: the OS takes the drag from here,
    /// so the resize is as smooth as any native window's.
    member this.OnResizeGripPressed(sender: obj, e: PointerPressedEventArgs) =
        match sender with
        | :? Control as grip when e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
                                  && this.WindowState = WindowState.Normal ->
            match grip.Tag with
            | :? string as tag ->
                match Enum.TryParse<WindowEdge>(tag) with
                | true, edge ->
                    e.Handled <- true
                    this.BeginResizeDrag(edge, e)
                | _ -> ()
            | _ -> ()
        | _ -> ()

    // =====================================================================
    // DOWNLOADS SHEET
    // =====================================================================
    member this.OnOpenAssetsClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.OpenAssets()
        | _ -> ()

    member this.OnAssetsCloseClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.CloseAssets()
        | _ -> ()

    member this.OnAssetsBackdropPressed(sender: obj, e: PointerPressedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.CloseAssets()
        | _ -> ()

    member this.OnAssetDownloadClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            match ctrl.DataContext with
            | :? SetupItemViewModel as item -> vm.Assets.Download(item)
            | _ -> ()
        | _ -> ()

    member this.OnAssetsDownloadAllClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.Assets.DownloadEverything()
        | _ -> ()

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

    member this.OnTabScreenClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.ActiveSection <- "screen"
        | _ -> ()

    member this.OnCommunityTutorialsClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> this.OpenExternal(vm.Community.TutorialsUrl)
        | _ -> ()

    /// Asks for the next page once the grid is within a screen of its end, so
    /// the rows are already there by the time the user reaches them. The
    /// view-model ignores the call when there is nothing more or a page is
    /// already on the way, so firing on every scroll event costs nothing.
    member this.OnCommunityScrolled(sender: obj, e: ScrollChangedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? ScrollViewer as sv) ->
            let remaining = sv.Extent.Height - sv.Viewport.Height - sv.Offset.Y

            if remaining < sv.Viewport.Height then
                vm.Community.LoadMore()
        | _ -> ()

    member this.OnCommunityRefreshClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.Community.Refresh()
        | _ -> ()

    /// The filter chips carry their value in Tag, so one handler serves the
    /// whole row and adding a route later is a line of XAML.
    // ---- the toolbar and the two halves of the section -------------------
    member this.OnCommunityGamesTabClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.ShowCommunityGames()
        | _ -> ()

    member this.OnPulseTabClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.ShowPulse()
        | _ -> ()

    // ---- the private chat -------------------------------------------------
    member this.OnDmTabClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.ShowDm()
        | _ -> ()

    member this.OnDmRefreshClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.Dm.Refresh()
        | _ -> ()

    /// Developer only: the list on the left is not drawn for anybody else.
    member this.OnDmThreadClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            match ctrl.DataContext with
            | :? DmThreadViewModel as item -> vm.Dm.OpenThread(item)
            | _ -> ()
        | _ -> ()

    member this.OnDmSendClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.Dm.Send()
        | _ -> ()

    member this.OnDmRemoveImageClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.Dm.RemoveImage()
        | _ -> ()

    member this.OnDmAttachClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            let options = FilePickerOpenOptions(Title = "Pick a picture", AllowMultiple = false)

            options.FileTypeFilter <-
                [| FilePickerFileType(
                       "Pictures",
                       Patterns = [| "*.png"; "*.jpg"; "*.jpeg"; "*.webp"; "*.gif"; "*.bmp" |]
                   ) |]

            async {
                let! files = this.StorageProvider.OpenFilePickerAsync(options) |> Async.AwaitTask

                if files <> null && files.Count > 0 then
                    vm.Dm.AttachImage(files.[0].Path.LocalPath)
            }
            |> Async.StartImmediate
        | _ -> ()

    // ---- the gallery ------------------------------------------------------
    member this.OnGalleryTabClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.ShowGallery()
        | _ -> ()

    member this.OnGalleryWallClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.Gallery.ShowWall()
        | _ -> ()

    member this.OnGalleryQueueClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.Gallery.ShowQueue()
        | _ -> ()

    member this.OnGalleryRefreshClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.Gallery.Refresh()
        | _ -> ()

    member this.OnGalleryUploadClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            let options = FilePickerOpenOptions(Title = "Pick a picture", AllowMultiple = false)

            options.FileTypeFilter <-
                [| FilePickerFileType(
                       "Pictures",
                       Patterns = [| "*.png"; "*.jpg"; "*.jpeg"; "*.webp"; "*.gif"; "*.bmp" |]
                   ) |]

            async {
                let! files = this.StorageProvider.OpenFilePickerAsync(options) |> Async.AwaitTask

                if files <> null && files.Count > 0 then
                    vm.Gallery.Upload(files.[0].Path.LocalPath)
            }
            |> Async.StartImmediate
        | _ -> ()

    member this.OnGalleryApproveClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            match ctrl.DataContext with
            | :? GalleryItemViewModel as item -> vm.Gallery.Approve(item)
            | _ -> ()
        | _ -> ()

    member this.OnGalleryRejectClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            match ctrl.DataContext with
            | :? GalleryItemViewModel as item -> vm.Gallery.Reject(item)
            | _ -> ()
        | _ -> ()

    /// The picture open in the chat viewer, copied onto the wall. Only drawn
    /// for the developer; the server checks again before it copies anything.
    member this.OnPromoteToGalleryClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            vm.Gallery.Promote(vm.Pulse.ViewerId, "")
            vm.Pulse.CloseViewer()
        | _ -> ()

    /// A wall picture loads when its card comes within reach of the visible
    /// part of the tab - a screen's height either side, so it is ready by the
    /// time it scrolls in - and not before. Opening the gallery therefore
    /// fetches the few pictures on screen, not the whole wall at once.
    member this.OnGalleryImageViewportChanged(sender: obj, e: Avalonia.Layout.EffectiveViewportChangedEventArgs) =
        match sender with
        | :? Control as ctrl ->
            match ctrl.DataContext with
            | :? GalleryItemViewModel as item when not item.ImageStarted ->
                let view = e.EffectiveViewport

                if view.Width > 0.0 && view.Height > 0.0 then
                    let reach = Rect(view.X, view.Y - view.Height, view.Width, view.Height * 3.0)

                    if reach.Intersects(Rect(ctrl.Bounds.Size)) then
                        item.EnsureImage()
            | _ -> ()
        | _ -> ()

    member this.OnGalleryImageClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            match ctrl.DataContext with
            | :? GalleryItemViewModel as item -> vm.Gallery.OpenViewer(item)
            | _ -> ()
        | _ -> ()

    member this.OnGalleryViewerCloseClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.Gallery.CloseViewer()
        | _ -> ()

    member this.OnGalleryViewerBackdropPressed(sender: obj, e: PointerPressedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.Gallery.CloseViewer()
        | _ -> ()

    /// Saves where the user chooses, exactly as the chat viewer does.
    member this.OnGalleryViewerSaveClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm when not (isNull vm.Gallery.ViewerBytes) ->
            let bytes = vm.Gallery.ViewerBytes
            let name = vm.Gallery.ViewerFileName

            task {
                try
                    let options =
                        FilePickerSaveOptions(
                            Title = "Save picture",
                            SuggestedFileName = name,
                            DefaultExtension = "png",
                            ShowOverwritePrompt = true
                        )

                    options.FileTypeChoices <-
                        [| FilePickerFileType("PNG image", Patterns = [| "*.png" |])
                           FilePickerFileType("WebP image", Patterns = [| "*.webp" |]) |]

                    let! file = this.StorageProvider.SaveFilePickerAsync(options)

                    if not (isNull file) then
                        let asWebp = file.Name.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
                        let! data = Task.Run(fun () -> if asWebp then bytes else ChatImages.toPng bytes)
                        use! stream = file.OpenWriteAsync()
                        do! stream.WriteAsync(data, 0, data.Length)
                with _ ->
                    ()
            }
            |> ignore
        | _ -> ()

    member this.OnCommunityClearFiltersClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            vm.Community.ClearFilters()
            vm.SearchText <- ""
        | _ -> ()

    /// The ✕ on the "Searching for" chip. The box belongs to the window, so
    /// clearing it there is what brings the whole grid back.
    member this.OnClearCommunitySearchClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.SearchText <- ""
        | _ -> ()

    // ---- CHAT (PULSE) -----------------------------------------------------
    /// Back to the message box, caret at the end - after Send, a reply, an emoji.
    member private this.FocusChatBox() =
        match this.FindControl<TextBox>("PulseDraftBox") with
        | null -> ()
        | box ->
            box.Focus() |> ignore
            box.CaretIndex <- (if isNull box.Text then 0 else box.Text.Length)

    member this.OnPulseSendClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            vm.Pulse.Send()
            this.FocusChatBox()
        | _ -> ()

    /// Enter sends; Esc drops the reply. The box does not accept returns, so it
    /// never swallows Enter first.
    member this.OnPulseDraftKeyDown(sender: obj, e: KeyEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm when e.Key = Key.Enter ->
            vm.Pulse.Send()
            e.Handled <- true
        | :? MainViewModel as vm when e.Key = Key.Escape && vm.Pulse.IsReplying ->
            vm.Pulse.CancelReply()
            e.Handled <- true
        | _ -> ()

    /// Ctrl+V in the message box. A picture file copied in Explorer, or a
    /// picture on the clipboard (a screenshot, "Copy image"), becomes the
    /// attachment; text is pasted as usual. Text wins when both are there, so
    /// copying cells or a paragraph never turns into a picture by surprise.
    member private this.PasteIntoChat(box: TextBox) =
        match this.DataContext, TopLevel.GetTopLevel(this) with
        | (:? MainViewModel as vm), top when not (isNull top) && not (isNull top.Clipboard) ->
            let clipboard = top.Clipboard

            let isPicture (path: string) =
                not (isNull path)
                && [ ".png"; ".jpg"; ".jpeg"; ".webp"; ".gif"; ".bmp" ]
                   |> List.contains (Path.GetExtension(path).ToLowerInvariant())

            // What the paste does once everything is read - outside the task,
            // so its state machine stays statically compilable (FS3511).
            let apply (picture: string option) (text: string) (bitmap: Avalonia.Media.Imaging.Bitmap) =
                match picture with
                | Some path -> vm.Pulse.AttachImage(path)
                | None when not (String.IsNullOrEmpty(text)) ->
                    let clean = text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ')
                    let current = if isNull box.Text then "" else box.Text
                    let a = max 0 (min current.Length (min box.SelectionStart box.SelectionEnd))
                    let b = max 0 (min current.Length (max box.SelectionStart box.SelectionEnd))
                    box.Text <- current.Remove(a, b - a).Insert(a, clean)
                    box.CaretIndex <- a + clean.Length
                | None when not (isNull bitmap) ->
                    use ms = new MemoryStream()
                    bitmap.Save(ms, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default)
                    vm.Pulse.AttachImageBytes(ms.ToArray())
                | None -> ()

            task {
                try
                    let! files = clipboard.TryGetFilesAsync()

                    let picture =
                        if isNull files then None
                        else files |> Seq.tryPick (fun f -> match f.TryGetLocalPath() with p when isPicture p -> Some p | _ -> None)

                    // Each await in a straight line, never inside a branch: only
                    // then can the compiler build the task statically (warning
                    // FS3511 otherwise). Same rules as before - a picture file,
                    // else text, else a picture on the clipboard.
                    let! text =
                        if picture.IsSome then Task.FromResult<string>(null)
                        else clipboard.TryGetTextAsync()

                    let! bitmap =
                        if picture.IsNone && String.IsNullOrEmpty(text) then clipboard.TryGetBitmapAsync()
                        else Task.FromResult<Avalonia.Media.Imaging.Bitmap>(null)

                    apply picture text bitmap
                with _ ->
                    ()
            }
            |> ignore
        | _ -> ()

    /// Ctrl+V in the private chat, by the same rules as the public one: a
    /// copied picture file or a picture on the clipboard becomes the
    /// attachment, and text is pasted as text - line breaks kept, since this
    /// box, unlike a single-line one, can hold them.
    member private this.PasteIntoDm(box: TextBox) =
        match this.DataContext, TopLevel.GetTopLevel(this) with
        | (:? MainViewModel as vm), top when not (isNull top) && not (isNull top.Clipboard) ->
            let clipboard = top.Clipboard

            let isPicture (path: string) =
                not (isNull path)
                && [ ".png"; ".jpg"; ".jpeg"; ".webp"; ".gif"; ".bmp" ]
                   |> List.contains (Path.GetExtension(path).ToLowerInvariant())

            // Outside the task, as in PasteIntoChat.
            let apply (picture: string option) (text: string) (bitmap: Avalonia.Media.Imaging.Bitmap) =
                match picture with
                | Some path -> vm.Dm.AttachImage(path)
                | None when not (String.IsNullOrEmpty(text)) ->
                    let current = if isNull box.Text then "" else box.Text
                    let a = max 0 (min current.Length (min box.SelectionStart box.SelectionEnd))
                    let b = max 0 (min current.Length (max box.SelectionStart box.SelectionEnd))
                    box.Text <- current.Remove(a, b - a).Insert(a, text)
                    box.CaretIndex <- a + text.Length
                | None when not (isNull bitmap) ->
                    use ms = new MemoryStream()
                    bitmap.Save(ms, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default)
                    vm.Dm.AttachImageBytes(ms.ToArray())
                | None -> ()

            task {
                try
                    let! files = clipboard.TryGetFilesAsync()

                    let picture =
                        if isNull files then None
                        else files |> Seq.tryPick (fun f -> match f.TryGetLocalPath() with p when isPicture p -> Some p | _ -> None)

                    // Awaits in a straight line, as in PasteIntoChat.
                    let! text =
                        if picture.IsSome then Task.FromResult<string>(null)
                        else clipboard.TryGetTextAsync()

                    let! bitmap =
                        if picture.IsNone && String.IsNullOrEmpty(text) then clipboard.TryGetBitmapAsync()
                        else Task.FromResult<Avalonia.Media.Imaging.Bitmap>(null)

                    apply picture text bitmap
                with _ ->
                    ()
            }
            |> ignore
        | _ -> ()

    member this.OnDmImageClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            match ctrl.DataContext with
            | :? DmMessageViewModel as item -> vm.Dm.OpenViewer(item)
            | _ -> ()
        | _ -> ()

    member this.OnDmViewerCloseClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.Dm.CloseViewer()
        | _ -> ()

    member this.OnDmViewerBackdropPressed(sender: obj, e: PointerPressedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.Dm.CloseViewer()
        | _ -> ()

    /// A key picked from one of the sheet's key menus. The menu closes behind
    /// the choice, so the chip shows the new key straight away.
    member private this.CloseMenuOf(ctrl: Control) =
        match Avalonia.LogicalTree.LogicalExtensions.FindLogicalAncestorOfType<Avalonia.Controls.Primitives.Popup>(ctrl, false) with
        | null -> ()
        | popup -> popup.IsOpen <- false

    member this.OnChooseOverlayKey(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            match ctrl.Tag with
            | :? string as key ->
                vm.ChooseOverlayKey(key)
                this.CloseMenuOf(ctrl)
            | _ -> ()
        | _ -> ()

    member this.OnChooseReShadeKey(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            match ctrl.Tag with
            | :? string as key ->
                vm.ChooseReShadeKey(key)
                this.CloseMenuOf(ctrl)
            | _ -> ()
        | _ -> ()

    member this.OnChooseOptiMenuKey(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            match ctrl.Tag with
            | :? string as key ->
                vm.ChooseOptiMenuKey(key)
                this.CloseMenuOf(ctrl)
            | _ -> ()
        | _ -> ()

    /// The small SPECS button on a report: the rest of the machine, folded.
    member this.OnToggleSpecsClicked(sender: obj, e: RoutedEventArgs) =
        match sender with
        | :? Control as ctrl ->
            match ctrl.DataContext with
            | :? CommunityReportViewModel as report -> report.ToggleSpecs()
            | _ -> ()
        | _ -> ()

    member this.OnChatEmojiClicked(sender: obj, e: RoutedEventArgs) =
        match sender, this.FindControl<TextBox>("PulseDraftBox") with
        | (:? Control as ctrl), box when not (isNull box) ->
            match ctrl.DataContext with
            | :? ChatReactionViewModel as choice ->
                let current = if isNull box.Text then "" else box.Text
                let at = max 0 (min current.Length box.CaretIndex)
                box.Text <- current.Insert(at, choice.Emoji)
                box.Focus() |> ignore
                box.CaretIndex <- at + choice.Emoji.Length
            | _ -> ()
        | _ -> ()

    /// Chips under a message and the six on its hover bar alike.
    member this.OnChatReactionClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            match ctrl.DataContext with
            | :? ChatReactionViewModel as r -> vm.Pulse.ToggleReaction(r.MessageId, r.Emoji)
            | _ -> ()
        | _ -> ()

    member this.OnChatReplyClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            match ctrl.DataContext with
            | :? PulseMessageViewModel as message ->
                vm.Pulse.StartReply(message)
                this.FocusChatBox()
            | _ -> ()
        | _ -> ()

    member this.OnChatCancelReplyClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.Pulse.CancelReply()
        | _ -> ()

    /// The quote above a reply takes you to the message it answers, when that
    /// message is still loaded.
    member this.OnChatReplyQuoteClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            match ctrl.DataContext with
            | :? PulseMessageViewModel as message ->
                match vm.Pulse.TryFind(message.ReplyToId), this.FindControl<ItemsControl>("PulseMessagesList") with
                | Some target, list when not (isNull list) ->
                    match list.ContainerFromItem(target) with
                    | null -> ()
                    | container -> container.BringIntoView()
                | _ -> ()
            | _ -> ()
        | _ -> ()

    // ---- the picture viewer ----------------------------------------------
    /// A picture loads when its box comes within reach of the visible part of
    /// the chat - a screen's height either side, so it is ready by the time it
    /// scrolls in - and not before. Opening the chat therefore fetches only
    /// the pictures near the bottom, not every one in the history.
    member this.OnChatImageViewportChanged(sender: obj, e: Avalonia.Layout.EffectiveViewportChangedEventArgs) =
        match sender with
        | :? Control as ctrl ->
            match ctrl.DataContext with
            | :? PulseMessageViewModel as message when not message.ImageStarted ->
                let view = e.EffectiveViewport

                if view.Width > 0.0 && view.Height > 0.0 then
                    let reach = Rect(view.X, view.Y - view.Height, view.Width, view.Height * 3.0)

                    if reach.Intersects(Rect(ctrl.Bounds.Size)) then
                        message.EnsureImage()
            | _ -> ()
        | _ -> ()

    // ---- the reply alert ---------------------------------------------------
    member this.OnDownloadsHintPressed(sender: obj, e: PointerPressedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.OpenAssetsFromHint()
        | _ -> ()

    member this.OnDownloadsHintCloseClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.DismissDownloadsHint()
        | _ -> ()

    member this.OnReplyToastPressed(sender: obj, e: PointerPressedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.OpenChatFromReply()
        | _ -> ()

    member this.OnReplyToastCloseClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.Pulse.DismissReplyToast()
        | _ -> ()

    member this.OnChatImageClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            match ctrl.DataContext with
            | :? PulseMessageViewModel as message -> vm.Pulse.OpenViewer(message)
            | _ -> ()
        | _ -> ()

    /// Puts one of your own messages back into the composer. The link is only
    /// drawn while the minute lasts, so this is never reached after it.
    member this.OnChatEditClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            match ctrl.DataContext with
            | :? PulseMessageViewModel as message -> vm.Pulse.BeginEdit(message)
            | _ -> ()
        | _ -> ()

    member this.OnChatCancelEditClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.Pulse.CancelEdit()
        | _ -> ()

    member this.OnChatViewerCloseClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.Pulse.CloseViewer()
        | _ -> ()

    member this.OnChatViewerBackdropPressed(sender: obj, e: PointerPressedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.Pulse.CloseViewer()
        | _ -> ()

    /// Saves where the user chooses. PNG by default - everything opens it -
    /// or the original WebP, byte for byte.
    member this.OnChatViewerSaveClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm when not (isNull vm.Pulse.ViewerBytes) ->
            let bytes = vm.Pulse.ViewerBytes
            let name = vm.Pulse.ViewerFileName

            task {
                try
                    let options =
                        FilePickerSaveOptions(
                            Title = "Save picture",
                            SuggestedFileName = name,
                            DefaultExtension = "png",
                            ShowOverwritePrompt = true
                        )

                    options.FileTypeChoices <-
                        [| FilePickerFileType("PNG image", Patterns = [| "*.png" |])
                           FilePickerFileType("WebP image", Patterns = [| "*.webp" |]) |]

                    let! file = this.StorageProvider.SaveFilePickerAsync(options)

                    if not (isNull file) then
                        let asWebp = file.Name.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
                        let! data = Task.Run(fun () -> if asWebp then bytes else ChatImages.toPng bytes)
                        use! stream = file.OpenWriteAsync()
                        do! stream.WriteAsync(data, 0, data.Length)
                with _ ->
                    ()
            }
            |> ignore
        | _ -> ()

    member this.OnPulseAttachClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            task {
                let options = FilePickerOpenOptions(Title = "Pick a picture", AllowMultiple = false)

                options.FileTypeFilter <-
                    [| FilePickerFileType(
                           "Pictures",
                           Patterns = [| "*.png"; "*.jpg"; "*.jpeg"; "*.webp"; "*.gif"; "*.bmp" |]
                       ) |]

                let! files = this.StorageProvider.OpenFilePickerAsync(options)

                if files.Count > 0 then
                    vm.Pulse.AttachImage(files.[0].Path.LocalPath)
            }
            |> ignore
        | _ -> ()

    member this.OnPulseRemoveImageClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.Pulse.RemoveImage()
        | _ -> ()

    member this.OnPulseDeleteClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            match ctrl.DataContext with
            | :? PulseMessageViewModel as message -> vm.Pulse.Delete(message)
            | _ -> ()
        | _ -> ()

    /// Keeps the conversation where the reader expects it.
    ///
    ///   - New messages below, and the reader was at the bottom: follow them.
    ///   - History loaded above, and the reader was at the top: stay on the
    ///     message they were looking at, instead of being thrown to the start.
    ///   - Near the top: ask for the history before it.
    member this.OnPulseScrolled(sender: obj, e: ScrollChangedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? ScrollViewer as sv) ->
            let grew = e.ExtentDelta.Y

            if grew > 0.0 then
                let extentBefore = sv.Extent.Height - grew
                let fromBottomBefore = extentBefore - sv.Viewport.Height - sv.Offset.Y

                if fromBottomBefore <= 120.0 then
                    sv.ScrollToEnd()
                elif sv.Offset.Y < 80.0 then
                    sv.Offset <- Vector(sv.Offset.X, sv.Offset.Y + grew)

            // Only when the reader scrolls up by hand. While content is being
            // added the offset is briefly 0 (before the jump to the newest
            // message), and reacting to that loaded page after page until the
            // whole history was in memory.
            if grew = 0.0 && e.OffsetDelta.Y < 0.0 && sv.Offset.Y < 60.0
               && sv.Extent.Height > sv.Viewport.Height then
                vm.Pulse.LoadOlder()
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
                // Three places the box can be now: the title bar, the sidebar
                // layout's own copy, and the community filter row it moves down
                // to while that grid is open. The community one is asked first
                // and by *effective* visibility - it lives inside a panel that
                // is collapsed everywhere else, and its own IsVisible would
                // still read true in there.
                let searchBox =
                    let sb1 = this.FindControl<TextBox>("SearchInputBox")
                    let sb2 = this.FindControl<TextBox>("SearchInputBoxSidebar")
                    let sb3 = this.FindControl<TextBox>("SearchInputBoxCommunity")

                    if sb3 <> null && sb3.IsEffectivelyVisible then sb3
                    elif sb1 <> null && sb1.IsVisible then sb1
                    elif sb2 <> null && sb2.IsVisible then sb2
                    elif sb1 <> null then sb1
                    else sb2

                if searchBox <> null then
                    searchBox.Focus() |> ignore
                    searchBox.SelectAll()
        | _ -> ()

    // ---- the developer's pinned note on a community game ----------------
    member this.OnPinNoteClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.Community.OpenNoteEditor()
        | _ -> ()

    member this.OnCancelNoteClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.Community.CloseNoteEditor()
        | _ -> ()

    member this.OnSaveNoteClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.Community.SaveSheetNote()
        | _ -> ()

    member this.OnDisableAutoScanClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            UiSounds.tickOff ()
            vm.DisableAutoScan()
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

    /// Settings -> Visual Atmosphere: a picture tile. The last tile is the
    /// user's own picture and the only way to pick one: empty, it asks for a
    /// picture; holding one, it switches to it; already showing, it asks for a
    /// different one.
    member this.OnBackgroundTileClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            match ctrl.DataContext with
            | :? AtmosphereOption as option when option.IsCustom ->
                if vm.HasCustomBackground && not vm.IsCustomBackgroundActive then vm.UseCustomBackground()
                else this.PickCustomBackground(vm)
            | :? AtmosphereOption as option -> vm.ChooseAtmosphere(option)
            | _ -> ()
        | _ -> ()

    /// The x on the custom tile. Handled here, or the click would carry on up
    /// to the tile it sits on and open the picker straight after.
    member this.OnRemoveCustomBackgroundClicked(sender: obj, e: RoutedEventArgs) =
        e.Handled <- true

        match this.DataContext with
        | :? MainViewModel as vm -> vm.RemoveCustomBackground()
        | _ -> ()

    member private this.PickCustomBackground(vm: MainViewModel) =
        let options = FilePickerOpenOptions()
        options.Title <- "Choose a background picture"
        options.AllowMultiple <- false

        let fileType = FilePickerFileType("Images")
        fileType.Patterns <- [| "*.png"; "*.jpg"; "*.jpeg"; "*.bmp"; "*.webp" |]
        options.FileTypeFilter <- [| fileType |]

        async {
            let! files = this.StorageProvider.OpenFilePickerAsync(options) |> Async.AwaitTask
            if files <> null && files.Count > 0 then
                vm.SetCustomBackground(files.[0].Path.LocalPath)
        }
        |> Async.StartImmediate

    member this.OnRestoreCoverClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            match ctrl.DataContext with
            | :? GameCardViewModel as card -> vm.RestoreGameCover(card)
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

    /// The same, but the title is also recorded so the next scan leaves it
    /// alone. Adding it by hand afterwards still works.
    member this.OnRemoveAndExcludeGameClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            match ctrl.DataContext with
            | :? GameCardViewModel as card -> vm.RemoveGame(card, true)
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
    // A soft tick when the route really changes - not for a click on the one
    // already picked.
    member this.OnSetOptiScalerMode(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            if not vm.IsOptiScalerMode then UiSounds.tick ()
            vm.SetInstallMode(ModInstaller.OptiScalerMode)
        | _ -> ()

    member this.OnSetDx12Mode(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            if not vm.IsDx12Mode then UiSounds.tick ()
            vm.SetInstallMode(ModInstaller.Dx12Auto)
        | _ -> ()

    member this.OnSetDx11Mode(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            if not vm.IsDx11Mode then UiSounds.tick ()
            vm.SetInstallMode(ModInstaller.Dx11)
        | _ -> ()

    member this.OnSetDx9Mode(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            if not vm.IsDx9Mode then UiSounds.tick ()
            vm.SetInstallMode(ModInstaller.Dx9)
        | _ -> ()

    // Every one of these answers the same way the route buttons do: a sound
    // only when the click actually changes something. Clicking the option that
    // is already chosen stays silent, or holding a toggle down would rattle.
    /// The developer correcting one report's verdict. The button carries the
    /// new status in its Tag; the card it sits on is the report.
    member this.OnCorrectStatusClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            match ctrl.DataContext, ctrl.Tag with
            | (:? CommunityReportViewModel as report), (:? string as status) ->
                vm.Community.CorrectStatus(report, status)
            | _ -> ()
        | _ -> ()

    /// The author's own verdict. Same three chips as the developer's row, but
    /// this one is drawn only on reports this machine filed.
    member this.OnEditOwnStatusClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            match ctrl.DataContext, ctrl.Tag with
            | (:? CommunityReportViewModel as report), (:? string as status) ->
                vm.Community.EditOwnStatus(report, status)
            | _ -> ()
        | _ -> ()

    member this.OnBeginEditBodyClicked(sender: obj, e: RoutedEventArgs) =
        match sender with
        | :? Control as ctrl ->
            match ctrl.DataContext with
            | :? CommunityReportViewModel as report -> report.BeginEditBody()
            | _ -> ()
        | _ -> ()

    member this.OnCancelEditBodyClicked(sender: obj, e: RoutedEventArgs) =
        match sender with
        | :? Control as ctrl ->
            match ctrl.DataContext with
            | :? CommunityReportViewModel as report -> report.CancelEditBody()
            | _ -> ()
        | _ -> ()

    member this.OnSaveOwnBodyClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            match ctrl.DataContext with
            | :? CommunityReportViewModel as report -> vm.Community.SaveOwnBody(report)
            | _ -> ()
        | _ -> ()

    member this.OnBeginEditCommentClicked(sender: obj, e: RoutedEventArgs) =
        match sender with
        | :? Control as ctrl ->
            match ctrl.DataContext with
            | :? CommunityCommentViewModel as comment -> comment.BeginEdit()
            | _ -> ()
        | _ -> ()

    member this.OnCancelEditCommentClicked(sender: obj, e: RoutedEventArgs) =
        match sender with
        | :? Control as ctrl ->
            match ctrl.DataContext with
            | :? CommunityCommentViewModel as comment -> comment.CancelEdit()
            | _ -> ()
        | _ -> ()

    member this.OnSaveOwnCommentClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            match ctrl.DataContext with
            | :? CommunityCommentViewModel as comment -> vm.Community.SaveOwnComment(comment)
            | _ -> ()
        | _ -> ()

    member this.OnMarkTestedByDevClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.Community.MarkGameTestedByDev()
        | _ -> ()

    member this.OnRemoveReportClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            match ctrl.DataContext with
            | :? CommunityReportViewModel as report -> vm.Community.RemoveReport(report)
            | _ -> ()
        | _ -> ()

    member this.OnToggleInstallResultClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.ToggleInstallResult()
        | _ -> ()

    member this.OnSetVulkanMode(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            if not vm.IsVulkanMode then UiSounds.tick ()
            vm.SetInstallMode(ModInstaller.VulkanMode)
        | _ -> ()

    member this.OnSetOptiDx12(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            if not vm.IsOptiDx12 then UiSounds.tick ()
            vm.SetOptiApi(ModInstaller.OptiDx12)
        | _ -> ()

    member this.OnSetOptiVulkan(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            if not vm.IsOptiVulkan then UiSounds.tick ()
            vm.SetOptiApi(ModInstaller.OptiVulkan)
        | _ -> ()

    member this.OnSetOptiNeural(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            if not vm.IsOptiNeural then UiSounds.tick ()
            vm.SetOptiApi(ModInstaller.OptiNeural)
        | _ -> ()

    member this.OnSetNeuralAddonOn(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            if not vm.IsNeuralAddonOn then UiSounds.toggle true
            vm.SetNeuralAddon(true)
        | _ -> ()

    member this.OnSetNeuralAddonOff(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            if vm.IsNeuralAddonOn then UiSounds.toggle false
            vm.SetNeuralAddon(false)
        | _ -> ()

    member this.OnSetMfgUnlockOn(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            if not vm.IsMfgUnlockOn then UiSounds.toggle true
            vm.SetMfgUnlock(true)
        | _ -> ()

    member this.OnSetMfgUnlockOff(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            if vm.IsMfgUnlockOn then UiSounds.toggle false
            vm.SetMfgUnlock(false)
        | _ -> ()

    member this.OnSetMultipassOn(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            if not vm.IsMultipassOn then UiSounds.toggle true
            vm.SetMultipass(true)
        | _ -> ()

    member this.OnSetDeepFriedOn(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            if not vm.IsDeepFriedOn then UiSounds.toggle true
            vm.SetDeepFried(true)
        | _ -> ()

    member this.OnSetDeepFriedOff(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            if vm.IsDeepFriedOn then UiSounds.toggle false
            vm.SetDeepFried(false)
        | _ -> ()

    member this.OnSetMultipassOff(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            if vm.IsMultipassOn then UiSounds.toggle false
            vm.SetMultipass(false)
        | _ -> ()

    member this.OnSetOverlayOn(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            if not vm.IsOverlayEnabled then UiSounds.toggle true
            vm.IsOverlayEnabled <- true
        | _ -> ()

    member this.OnSetOverlayOff(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            if vm.IsOverlayEnabled then UiSounds.toggle false
            vm.IsOverlayEnabled <- false
        | _ -> ()

    member this.OnSetBit64(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            if not vm.IsBit64 then UiSounds.tick ()
            vm.SetInstallArch(ModInstaller.Bit64)
        | _ -> ()

    member this.OnSetBit32(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            if not vm.IsBit32 then UiSounds.tick ()
            vm.SetInstallArch(ModInstaller.Bit32)
        | _ -> ()

    member this.OnToggleTargetDetailsClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.ToggleTargetDetails()
        | _ -> ()

    member this.OnToggleCommunityGlanceClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.ToggleCommunityGlance()
        | _ -> ()

    member this.OnToggleDllNamesClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            vm.ToggleDllNames()

            // Opened at the very bottom of the sheet: bring it up into view.
            if vm.IsDllNamesOpen then
                Dispatcher.UIThread.Post(
                    (fun () ->
                        match this.FindControl<Border>("DllNameCard") with
                        | null -> ()
                        | card -> card.BringIntoView()),
                    DispatcherPriority.Background)
        | _ -> ()

    member this.OnRenameOptiPickClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.RenameOptiToPick()
        | _ -> ()

    member this.OnRenameOptiCustomClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.RenameOptiToCustom()
        | _ -> ()

    member this.OnRenameReShadePickClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.RenameReShadeToPick()
        | _ -> ()

    member this.OnRenameReShadeCustomClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.RenameReShadeToCustom()
        | _ -> ()

    member this.OnRefreshGlanceClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.RefreshCommunityGlance()
        | _ -> ()

    member this.OnOpenGlanceInCommunityClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.OpenGlanceInCommunity()
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

    /// Runs the executable this sheet points at, so a change can be tried
    /// without going out to Explorer for it. Silent on failure by design - a
    /// game that refuses to start is not this app's error to raise.
    member this.OnLaunchTargetClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            try
                if not (String.IsNullOrWhiteSpace(vm.ManageExePath)) && File.Exists(vm.ManageExePath) then
                    let psi = ProcessStartInfo(vm.ManageExePath, UseShellExecute = true)
                    // Plenty of games only find their data when started from
                    // their own folder.
                    psi.WorkingDirectory <- Path.GetDirectoryName(vm.ManageExePath)
                    Process.Start(psi) |> ignore
            with _ ->
                ()
        | _ -> ()

    /// Re-reads this one game. The whole-library rescan is in Settings; this is
    /// for the far more common case of having just changed something in one
    /// game's folder.
    member this.OnRescanGameClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            UiSounds.tick ()
            vm.AnalyzeManageTarget()
        | _ -> ()

    member this.OnReplaceOptiScalerRtx40Clicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            let options = FolderPickerOpenOptions()
            options.Title <- "Select the OptiScaler RTX 40 folder (must contain OptiScaler.dll)"
            options.AllowMultiple <- false

            async {
                let! folders = this.StorageProvider.OpenFolderPickerAsync(options) |> Async.AwaitTask

                if folders <> null && folders.Count > 0 then
                    vm.ReplaceOptiScalerRtx40(folders.[0].Path.LocalPath)
            }
            |> Async.StartImmediate
        | _ -> ()

    member this.OnRestoreOptiScalerRtx40Clicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.RestoreOptiScalerRtx40()
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
