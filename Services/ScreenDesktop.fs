namespace DLSS_5_MANAGER.Services

open System
open System.Runtime.InteropServices
open System.Threading

/// Native helpers used only by Screen; no hooks into other Manager sections.
module ScreenDesktop =
    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type Message =
        val mutable Hwnd: nativeint
        val mutable Id: uint32
        val mutable WParam: unativeint
        val mutable LParam: nativeint
        val mutable Time: uint32
        val mutable X: int
        val mutable Y: int
        val mutable Private: uint32
    [<DllImport("user32.dll")>]
    extern bool RegisterHotKey(nativeint hwnd, int id, uint32 modifiers, uint32 key)
    [<DllImport("user32.dll")>]
    extern bool UnregisterHotKey(nativeint hwnd, int id)
    [<DllImport("user32.dll")>]
    extern int GetMessage(Message& message, nativeint hwnd, uint32 first, uint32 last)
    [<DllImport("user32.dll")>]
    extern bool PostThreadMessage(uint32 thread, uint32 message, unativeint wp, nativeint lp)
    [<DllImport("kernel32.dll")>]
    extern uint32 GetCurrentThreadId()
    [<DllImport("user32.dll")>]
    extern bool IsWindow(nativeint hwnd)
    [<DllImport("user32.dll")>]
    extern bool ShowWindow(nativeint hwnd, int command)
    [<DllImport("user32.dll")>]
    extern bool IsIconic(nativeint hwnd)
    [<DllImport("user32.dll")>]
    extern bool SetForegroundWindow(nativeint hwnd)
    [<DllImport("user32.dll")>]
    extern bool SetWindowDisplayAffinity(nativeint hwnd, uint32 affinity)

    [<DllImport("user32.dll")>]
    extern bool SetWindowPos(nativeint hwnd, nativeint after, int x, int y, int cx, int cy, uint32 flags)

    [<DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")>]
    extern nativeint GetWindowLongPtr(nativeint hwnd, int index)

    [<DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")>]
    extern nativeint SetWindowLongPtr(nativeint hwnd, int index, nativeint value)

    /// A window that is seen but never touched or captured - the FPS counter.
    /// Clicks go through it to whatever is underneath, it never takes the
    /// focus, it is not in Alt+Tab, and screen capture leaves it out: LIVE
    /// FLOW captures the screen it sits on, and a counter it could see would
    /// be processed back into the picture, one frame late, under itself.
    let makeGhost (hwnd: nativeint) =
        if hwnd <> 0n && IsWindow(hwnd) then
            let style = GetWindowLongPtr(hwnd, -20)
            // WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
            SetWindowLongPtr(hwnd, -20, style ||| 0x20n ||| 0x80000n ||| 0x80n ||| 0x08000000n) |> ignore
            // WDA_EXCLUDEFROMCAPTURE (Windows 10 2004 and later). A Debug build
            // can leave it visible to capture so a test can photograph it.
#if DEBUG
            if Environment.GetEnvironmentVariable("DLSS5_TEST_SHOW_COUNTER") <> "1" then
                SetWindowDisplayAffinity(hwnd, 0x11u) |> ignore
#else
            SetWindowDisplayAffinity(hwnd, 0x11u) |> ignore
#endif

    /// Puts a window back on top without moving, resizing or focusing it.
    ///
    /// `Topmost` is a request Windows grants once. A game going exclusive
    /// fullscreen - or any other topmost window appearing - quietly pushes the
    /// overlay behind, and nothing tells the app it happened. So it is
    /// re-asserted on a timer rather than set once and trusted.
    let keepOnTop (hwnd: nativeint) =
        if hwnd <> 0n && IsWindow(hwnd) then
            // HWND_TOPMOST, with NOSIZE | NOMOVE | NOACTIVATE | SHOWWINDOW:
            // it must not steal focus from the game underneath it.
            SetWindowPos(hwnd, -1n, 0, 0, 0, 0, 0x0053u) |> ignore

    let focus (label: string) =
        let hwnd = nativeint (Int64.Parse(label.Split(':')[0], Globalization.NumberStyles.HexNumber))
        if not (IsWindow(hwnd)) then failwith "The selected window has closed. Refresh the source list."
        if IsIconic(hwnd) then ShowWindow(hwnd, 9) |> ignore
        if not (SetForegroundWindow(hwnd)) then failwith "Windows could not activate this app. Select it from the taskbar."

    /// One global shortcut. Modifiers use RegisterHotKey's own bits (Alt 1,
    /// Ctrl 2, Shift 4, Win 8); the key is a Windows virtual-key code.
    type HotkeyBinding =
        { Id: int
          Modifiers: uint32
          Key: uint32
          Action: unit -> unit }

    /// Every LIVE FLOW shortcut on one thread, registered together and released
    /// together - so changing one never leaves the old combination held behind
    /// it. `failed` hears which ones Windows refused, which almost always means
    /// another program already owns that combination.
    type HotkeyListener(bindings: HotkeyBinding list, failed: int list -> unit) =
        let mutable threadId = 0u
        let started = new ManualResetEventSlim(false)

        let thread =
            Thread(
                ThreadStart(fun () ->
                    threadId <- GetCurrentThreadId()

                    // 0x4000 is MOD_NOREPEAT: holding the keys down fires once.
                    let refused =
                        [ for b in bindings do
                              if not (RegisterHotKey(0n, b.Id, b.Modifiers ||| 0x4000u, b.Key)) then
                                  yield b.Id ]

                    started.Set()

                    if not refused.IsEmpty then failed refused

                    try
                        let mutable message = Unchecked.defaultof<Message>

                        while GetMessage(&message, 0n, 0u, 0u) > 0 do
                            // WM_HOTKEY carries the id it was registered with.
                            if message.Id = 0x312u then
                                let id = int message.WParam

                                match bindings |> List.tryFind (fun b -> b.Id = id) with
                                | Some b -> b.Action()
                                | None -> ()
                    finally
                        for b in bindings do
                            UnregisterHotKey(0n, b.Id) |> ignore)
            )

        do
            thread.IsBackground <- true
            thread.Name <- "LIVE FLOW shortcuts"
            thread.Start()

        interface IDisposable with
            member _.Dispose() =
                if started.Wait(1000) && thread.IsAlive then
                    PostThreadMessage(threadId, 0x12u, 0un, 0n) |> ignore
                    thread.Join(1000) |> ignore

                started.Dispose()

    type OverlayHotkey(toggle: unit -> unit, failed: unit -> unit) =
        let mutable threadId = 0u
        let started = new ManualResetEventSlim(false)
        let thread = Thread(ThreadStart(fun () ->
            threadId <- GetCurrentThreadId()
            let registered = RegisterHotKey(0n, 0x5D51, 0x4004u, 0x4Fu) // Shift+O, no repeats
            started.Set()
            if registered then
                try
                    let mutable message = Unchecked.defaultof<Message>
                    while GetMessage(&message, 0n, 0u, 0u) > 0 do
                        if message.Id = 0x312u then toggle()
                finally UnregisterHotKey(0n, 0x5D51) |> ignore
            else failed()))
        do thread.IsBackground <- true; thread.Name <- "Screen overlay shortcut"; thread.Start()
        interface IDisposable with
            member _.Dispose() =
                if started.Wait(1000) && thread.IsAlive then
                    PostThreadMessage(threadId, 0x12u, 0un, 0n) |> ignore
                    thread.Join(1000) |> ignore
                started.Dispose()
