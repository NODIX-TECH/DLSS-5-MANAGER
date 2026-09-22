namespace DLSS_5_MANAGER.Views

open Avalonia.Controls
open Avalonia.Markup.Xaml
open System
open Avalonia
open Avalonia.Controls.Primitives
open Avalonia.Input
open Avalonia.Interactivity
open Avalonia.Media
open Avalonia.Platform.Storage
open Avalonia.Threading
open Avalonia.VisualTree
open DLSS_5_MANAGER.Services
open DLSS_5_MANAGER.ViewModels

type ScreenView() as this =
    inherit UserControl()
    let mutable overlayInstance = false
    let mutable overlay: Window option = None
    let mutable hotkey: IDisposable option = None
    let mutable closing = false

    /// The bar being dragged, if any. A press that misses the handle still
    /// slides: the handle is invisible, and hunting for it would be the whole
    /// problem with the old bar all over again.
    let mutable sliding: Slider option = None

    /// The window's own view-model, which is where the translations live. The
    /// overlay is a separate window and would otherwise have none, leaving
    /// every label in it blank.
    let mutable ownerContext: obj = null

    /// The shortcut shown in the overlay's own header, kept current.
    let mutable overlayBadge: TextBlock option = None

    // The ids RegisterHotKey knows the two shortcuts by.
    let overlayHotkeyId = 0x5D51
    let dlssHotkeyId = 0x5D52

    /// Avalonia's key to the Windows virtual-key code RegisterHotKey wants.
    /// Letters, digits, the number pad and F1-F24 - everything a shortcut is
    /// reasonably made of. Anything else answers 0, which means "not usable".
    let virtualKeyOf (key: Key) : uint32 =
        let k = int key

        if key >= Key.A && key <= Key.Z then uint32 (0x41 + (k - int Key.A))
        elif key >= Key.D0 && key <= Key.D9 then uint32 (0x30 + (k - int Key.D0))
        elif key >= Key.NumPad0 && key <= Key.NumPad9 then uint32 (0x60 + (k - int Key.NumPad0))
        elif key >= Key.F1 && key <= Key.F24 then uint32 (0x70 + (k - int Key.F1))
        else 0u

    /// The handle takes no width at all - the fill itself is the handle, and
    /// it runs the whole groove. The track reserves nothing, so a press maps
    /// straight onto the bar with nothing to subtract.
    let sliderHandle = 2.0

    let vm () = this.DataContext :?> ScreenViewModel
    do
        AvaloniaXamlLoader.Load(this)
        this.AddHandler(Button.ClickEvent, EventHandler<RoutedEventArgs>(fun _ _ -> UiSounds.tick()), RoutingStrategies.Bubble)

        // The page never scrolls itself.
        //
        // Pressing a switch focuses it, focus asks to be brought into view, and
        // the scroll viewer obliges - so a click near the bottom of the page
        // jumped the whole section down under the hand that clicked it
        // (reported by several people: "it scrolls down on its own when I press
        // a button"). Nothing here needs to be scrolled to: everything a person
        // presses is already where they are looking, and the wheel and the bar
        // still work exactly as before.
        this.AddHandler(
            Control.RequestBringIntoViewEvent,
            EventHandler<RequestBringIntoViewEventArgs>(fun _ e -> e.Handled <- true),
            RoutingStrategies.Tunnel ||| RoutingStrategies.Bubble
        )

        // Press anywhere on a bar and slide, the way a phone does it.
        //
        // Emptying the track's two halves of their hit-testing was not enough on
        // its own: the slider still treats a press on its background as a step
        // rather than a jump, and a press that never touched the handle starts
        // no drag. One handler for the whole section does both properly.
        let moveTo (slider: Slider) (x: float) =
            // The handle is as wide as the bar is tall and the track centres it
            // on the value, so the usable run is that much shorter and begins
            // half a handle in.
            let span = slider.Bounds.Width - sliderHandle

            if span > 1.0 then
                let ratio = Math.Clamp((x - sliderHandle / 2.0) / span, 0.0, 1.0)
                let tick = slider.TickFrequency

                if slider.IsSnapToTickEnabled && tick > 0.0 then
                    // Equal zones. Rounding to the nearest step gives the ends
                    // half the width of the middle - a three-step bar reads as
                    // six uneven parts. Thirds of the bar are thirds.
                    let steps = int (Math.Round((slider.Maximum - slider.Minimum) / tick)) + 1

                    if steps > 1 then
                        let index = Math.Clamp(int (ratio * float steps), 0, steps - 1)
                        slider.Value <- slider.Minimum + float index * tick
                else
                    slider.Value <- slider.Minimum + ratio * (slider.Maximum - slider.Minimum)

        this.AddHandler(
            InputElement.PointerPressedEvent,
            EventHandler<PointerPressedEventArgs>(fun _ e ->
                match e.Source with
                | :? Visual as source ->
                    let slider = source.FindAncestorOfType<Slider>(true)

                    if not (isNull slider) then
                        let point = e.GetCurrentPoint(slider)

                        if point.Properties.IsLeftButtonPressed then
                            sliding <- Some slider
                            moveTo slider point.Position.X
                | _ -> ()),
            RoutingStrategies.Bubble,
            true
        )

        this.AddHandler(
            InputElement.PointerMovedEvent,
            EventHandler<PointerEventArgs>(fun _ e ->
                match sliding with
                | Some slider ->
                    let point = e.GetCurrentPoint(slider)

                    if point.Properties.IsLeftButtonPressed then moveTo slider point.Position.X
                    else sliding <- None
                | None -> ()),
            RoutingStrategies.Bubble,
            true
        )

        this.AddHandler(
            InputElement.PointerReleasedEvent,
            EventHandler<PointerReleasedEventArgs>(fun _ _ -> sliding <- None),
            RoutingStrategies.Bubble,
            true
        )
        this.AttachedToVisualTree.Add(fun _ ->
            if not overlayInstance && hotkey.IsNone then
                match TopLevel.GetTopLevel(this) with
                | :? Window as owner ->
                    ownerContext <- owner.DataContext
                    // Both shortcuts belong to this section. Pressed while the
                    // window sits on GAMES or COMMUNITY they used to act anyway;
                    // what is already open can always be closed, though.
                    let allowed () =
                        let isOpen =
                            match overlay with
                            | Some w -> w.IsVisible
                            | None -> false

                        match owner.DataContext with
                        | :? MainViewModel as main -> main.IsScreenTabActive || isOpen
                        | _ -> false

                    let register () =
                        if hotkey.IsNone && not closing then
                            let v = vm ()
                            let (overlayMods, overlayKey) = v.OverlayHotkey
                            let (dlssMods, dlssKey) = v.DlssHotkey

                            let overlayBinding: ScreenDesktop.HotkeyBinding =
                                { Id = overlayHotkeyId
                                  Modifiers = overlayMods
                                  Key = overlayKey
                                  Action = (fun () -> Dispatcher.UIThread.Post(fun () -> if allowed () then this.ToggleOverlay())) }

                            let dlssBinding: ScreenDesktop.HotkeyBinding =
                                { Id = dlssHotkeyId
                                  Modifiers = dlssMods
                                  Key = dlssKey
                                  Action = (fun () -> Dispatcher.UIThread.Post(fun () -> if allowed () then vm().ToggleNr())) }

                            // Windows refuses a combination another program has
                            // already claimed. Say which, and where to change it.
                            let refused (ids: int list) =
                                Dispatcher.UIThread.Post(fun () ->
                                    let v = vm ()

                                    let names =
                                        [ if List.contains overlayHotkeyId ids then yield v.OverlayKeyText
                                          if List.contains dlssHotkeyId ids then yield v.DlssKeyText ]

                                    v.ReportError(
                                        String.Join(", ", names)
                                        + " is already taken by another program. Pick a different one from its button."
                                    ))

                            hotkey <-
                                Some(new ScreenDesktop.HotkeyListener([ overlayBinding; dlssBinding ], refused) :> IDisposable)

                    // A changed shortcut takes effect at once: the old pair is
                    // released and the new pair registered in its place.
                    (vm ()).HotkeysChanged.Add(fun () ->
                        hotkey |> Option.iter (fun h -> h.Dispose())
                        hotkey <- None
                        register ()
                        overlayBadge |> Option.iter (fun badge -> badge.Text <- vm().OverlayKeyText))

                    owner.Opened.Add(fun _ -> register())
                    if owner.IsVisible then register()
                    owner.Closed.Add(fun _ ->
                        closing <- true
                        hotkey |> Option.iter(fun h -> h.Dispose())
                        hotkey <- None
                        overlay |> Option.iter(fun w -> w.Close()))
                | _ -> ())

    member _.ConfigureOverlay() =
        overlayInstance <- true
        for name in ["NoResults"; "ScreenHeader"; "SourceCard"; "ComparisonCard"; "PresetsCard"; "SettingsCard"] do
            this.FindControl<Control>(name).IsVisible <- false
        for name in ["ProcessingCard"; "EffectCard"; "CaptureCard"] do
            this.FindControl<Control>(name).IsVisible <- true

        // The main view keeps 90 pixels clear at the bottom for the window's
        // own navigation bar. Inside the overlay there is no such bar, so that
        // is just a band of empty panel under the last card.
        match this.FindControl<Border>("ScreenRoot") with
        | null -> ()
        | root -> root.Padding <- Thickness(18., 6., 18., 18.)

    member _.ToggleOverlay() =
        if not closing then
            vm().Start()
            match overlay with
            | Some window when window.IsVisible -> window.Hide()
            | Some window ->
                window.Show()
                window.Activate()
            | None ->
                let view = ScreenView(DataContext = this.DataContext)
                view.ConfigureOverlay()

                // The window itself is transparent and undecorated; the panel
                // people actually see is the Border below. That is the only way
                // to get rounded corners out of WindowDecorations.None - the
                // window is a bare rectangle whatever it is filled with.
                let window =
                    Window(
                        Title = "DLSS 5 MANAGER Screen overlay",
                        Width = 760.,
                        Height = 800.,
                        MinWidth = 700.,
                        MinHeight = 420.,
                        Topmost = true,
                        ShowInTaskbar = false,
                        WindowDecorations = WindowDecorations.None,
                        Background = Brushes.Transparent
                    )

                window.TransparencyLevelHint <- [ WindowTransparencyLevel.Transparent ]
                window.DataContext <- ownerContext

                let layout = Grid(RowDefinitions = RowDefinitions("Auto,*"))

                let header =
                    Grid(ColumnDefinitions = ColumnDefinitions("*,Auto"), Margin = Thickness(18., 12., 12., 6.))

                let titles =
                    StackPanel(
                        Orientation = Layout.Orientation.Horizontal,
                        Spacing = 9.,
                        VerticalAlignment = Layout.VerticalAlignment.Center
                    )

                titles.Children.Add(
                    TextBlock(
                        Text = "LIVE FLOW",
                        Foreground = Brushes.White,
                        FontWeight = FontWeight.Bold,
                        FontSize = 13.5,
                        LetterSpacing = 1.6,
                        VerticalAlignment = Layout.VerticalAlignment.Center
                    )
                )

                // The shortcut as it is set now - and kept current if it changes
                // while the overlay is open.
                let badge =
                    TextBlock(
                        Text = vm().OverlayKeyText,
                        FontSize = 10.5,
                        Foreground = SolidColorBrush(Color.Parse("#94A3B8"))
                    )

                overlayBadge <- Some badge

                titles.Children.Add(
                    Border(
                        Background = SolidColorBrush(Color.Parse("#14FFFFFF")),
                        CornerRadius = CornerRadius(8.),
                        Padding = Thickness(8., 2.),
                        VerticalAlignment = Layout.VerticalAlignment.Center,
                        Child = badge
                    )
                )

                header.Children.Add(titles)

                let close = Button(Content = "\u2715", Padding = Thickness(12., 6.))
                close.Classes.Add("AppleSecondaryBtn")
                Grid.SetColumn(close, 1)

                close.Click.Add(fun _ ->
                    UiSounds.tickOff ()
                    window.Hide())

                header.Children.Add(close)
                Grid.SetRow(view, 1)
                layout.Children.Add(header)
                layout.Children.Add(view)

                let shell =
                    Border(
                        CornerRadius = CornerRadius(18.),
                        ClipToBounds = true,
                        Background = SolidColorBrush(Color.Parse("#F50B1214")),
                        BorderBrush = SolidColorBrush(Color.Parse("#2EFFFFFF")),
                        BorderThickness = Thickness(1.),
                        Child = layout
                    )

                window.Content <- shell

                // Dragging used to work from the header alone - a strip about
                // thirty pixels tall. Any empty space moves the panel now:
                // buttons, sliders and text boxes handle their own press
                // first, so this only ever sees what nothing else wanted.
                // Dragging from anywhere empty - but only from somewhere empty.
                //
                // Plenty of controls act on the *release*, not the press, so a
                // BeginMoveDrag on every press ran first and ate the click: the
                // profile and model lists stopped opening at all. Anything that
                // belongs to a control is left alone now.
                let isInteractive (source: obj) =
                    match source with
                    | :? Visual as v ->
                        not (isNull (v.FindAncestorOfType<Button>(true)))
                        || not (isNull (v.FindAncestorOfType<ComboBox>(true)))
                        || not (isNull (v.FindAncestorOfType<Slider>(true)))
                        || not (isNull (v.FindAncestorOfType<TextBox>(true)))
                        || not (isNull (v.FindAncestorOfType<CheckBox>(true)))
                        || not (isNull (v.FindAncestorOfType<Thumb>(true)))
                    | _ -> false

                let startDrag (e: PointerPressedEventArgs) =
                    if e.GetCurrentPoint(shell).Properties.IsLeftButtonPressed
                       && not (isInteractive e.Source) then
                        window.BeginMoveDrag(e)

                header.PointerPressed.Add(startDrag)
                shell.PointerPressed.Add(startDrag)

                let onTop = DispatcherTimer(Interval = TimeSpan.FromSeconds(2.))

                onTop.Tick.Add(fun _ ->
                    if window.IsVisible then
                        let handle = window.TryGetPlatformHandle()
                        if not (isNull handle) then ScreenDesktop.keepOnTop handle.Handle)

                window.Closing.Add(fun e ->
                    if not closing then
                        e.Cancel <- true
                        window.Hide())

                window.Opened.Add(fun _ ->
                    let handle = window.TryGetPlatformHandle()

                    if not (isNull handle) then
                        // WDA_NONE. This used to be WDA_EXCLUDEFROMCAPTURE
                        // (0x11), which is a standing instruction to Windows to
                        // leave the window out of every capture - which is
                        // exactly why the overlay never appeared in a
                        // screenshot or a recording.
                        ScreenDesktop.SetWindowDisplayAffinity(handle.Handle, 0u) |> ignore
                        ScreenDesktop.keepOnTop handle.Handle

                    onTop.Start())

                overlay <- Some window
                window.Show()
                window.Activate()

    member this.OnOpenOverlay(_: obj, _: RoutedEventArgs) = this.ToggleOverlay()

    /// The key cap in a shortcut panel: click it, then press the combination.
    member _.OnCaptureHotkey(sender: obj, _: RoutedEventArgs) =
        match sender with
        | :? Control as c ->
            match c.Tag with
            | :? string as target ->
                vm().StartListening(target)
                c.Focus() |> ignore
            | _ -> ()
        | _ -> ()

    /// While a cap is listening, the next real key becomes the shortcut. The
    /// modifier keys on their own are ignored - they are waited past until the
    /// key they belong to arrives. Esc gives up without changing anything.
    member _.OnHotkeyPanelKeyDown(_: obj, e: KeyEventArgs) =
        let v = vm ()

        if v.Listening <> "" then
            if e.Key = Key.Escape then
                v.StopListening()
                e.Handled <- true
            else
                let vk = virtualKeyOf e.Key

                if vk <> 0u then
                    let mods =
                        (if e.KeyModifiers.HasFlag(KeyModifiers.Alt) then 1u else 0u)
                        ||| (if e.KeyModifiers.HasFlag(KeyModifiers.Control) then 2u else 0u)
                        ||| (if e.KeyModifiers.HasFlag(KeyModifiers.Shift) then 4u else 0u)
                        ||| (if e.KeyModifiers.HasFlag(KeyModifiers.Meta) then 8u else 0u)

                    let target = v.Listening
                    v.StopListening()
                    v.SetHotkey(target, mods, vk)
                    e.Handled <- true

    /// One of the ready-made combinations, carried as "target|modifiers|key".
    member _.OnHotkeyPreset(sender: obj, _: RoutedEventArgs) =
        match sender with
        | :? Control as c ->
            match c.Tag with
            | :? string as tag ->
                match tag.Split('|') with
                | [| target; mods; key |] ->
                    let v = vm ()
                    v.StopListening()
                    v.SetHotkey(target, uint32 mods, uint32 key)
                | _ -> ()
            | _ -> ()
        | _ -> ()
    member _.OnChooseOutput(_: obj, _: RoutedEventArgs) =
        task {
            try
                let owner = TopLevel.GetTopLevel(this)
                let! folders = owner.StorageProvider.OpenFolderPickerAsync(FolderPickerOpenOptions(Title="Screen capture folder", AllowMultiple=false))
                if folders.Count > 0 then
                    let path = folders[0].TryGetLocalPath()
                    if not (isNull path) then vm().OutputFolder <- path
            with ex -> vm().ReportError(ex.Message)
        } |> ignore
