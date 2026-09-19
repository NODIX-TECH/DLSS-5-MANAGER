namespace DLSS_5_MANAGER.ViewModels

open System
open System.Collections.ObjectModel
open System.IO
open System.Threading.Tasks
open Avalonia.Media.Imaging
open DLSS_5_MANAGER.Services

/// The private chat: one thread per person, the developer at the far end of
/// every one of them.
///
/// A message written here is visible to whoever wrote it and to the developer,
/// and to nobody else - which is what makes it the right place for the things
/// the public chat is wrong for: a screenshot with somebody's desktop in it, a
/// key, an account name.
///
/// A player sees one conversation and never has to choose. The developer sees
/// everyone who has written, most recent first, and picks.

/// One message in a private thread.
type DmMessageViewModel(dto: CommunityApi.DmMessageDto) =
    inherit ViewModelBase()

    let mutable image: Bitmap option = None
    let mutable started = false
    let mutable failed = false

    member _.Id = dto.Id
    member _.Author = if String.IsNullOrWhiteSpace(dto.Author) then "someone" else dto.Author
    member _.Body = if isNull dto.Body then "" else dto.Body
    member _.HasBody = not (String.IsNullOrWhiteSpace(dto.Body))
    member _.Ago = CommunityShared.ago dto.Created

    /// Which side of the conversation this is drawn on.
    member _.FromDev = dto.Dev

    member _.HasRole = CommunityShared.hasRole dto.Role
    member _.RoleText = CommunityShared.roleText dto.Role
    member _.RoleAccent = CommunityShared.roleAccent dto.Role
    member _.RoleTint = CommunityShared.roleTint dto.Role
    member _.RoleEdge = CommunityShared.roleEdge dto.Role

    member _.HasImage = not (String.IsNullOrWhiteSpace(dto.Image))
    member _.ImageKey = if isNull dto.Image then "" else dto.Image

    member _.Image =
        match image with
        | Some b -> b
        | None -> null

    /// The whole picture, for the viewer. None until it has been fetched once.
    member _.FullBytes() : byte[] option =
        if String.IsNullOrWhiteSpace(dto.Image) then None else ChatImages.tryReadCached dto.Image

    member _.ImageStarted = started
    member _.ImageFailed = failed
    member this.ImageLoading = this.HasImage && image.IsNone && not failed

    /// Pictures share the public chat's bucket and its disk cache, so one that
    /// has been seen once is never fetched again.
    member this.EnsureImage() =
        if not started && this.HasImage then
            started <- true
            let key = dto.Image

            Task.Run(fun () ->
                let bytes =
                    match ChatImages.tryReadCached key with
                    | Some b -> Some b
                    | None ->
                        match CommunityApi.getChatImage key with
                        | Ok b when b.Length > 0 ->
                            ChatImages.writeCached key b
                            Some b
                        | _ -> None

                let decoded =
                    match bytes with
                    | Some b ->
                        try
                            use ms = new MemoryStream(b)
                            Some(Bitmap.DecodeToWidth(ms, 420))
                        with _ ->
                            None
                    | None -> None

                CommunityShared.ui (fun () ->
                    image <- decoded
                    failed <- decoded.IsNone

                    for name in [ "Image"; "ImageLoading"; "ImageFailed" ] do
                        this.RaisePropertyChanged(name)))
            |> ignore


/// One person's thread, in the developer's list.
type DmThreadViewModel(dto: CommunityApi.DmThreadDto) =
    inherit ViewModelBase()

    member _.Thread = dto.Thread
    member _.Person = if String.IsNullOrWhiteSpace(dto.Person) then "someone" else dto.Person
    member _.Last = if isNull dto.Last then "" else dto.Last
    member _.Ago = CommunityShared.ago dto.Created
    member _.Total = dto.Total

    /// True when the developer wrote last, so a thread still waiting on him
    /// stands out from one already answered.
    member _.Answered = dto.Mine
    member _.NeedsReply = not dto.Mine

    member _.HasRole = CommunityShared.hasRole dto.Role
    member _.RoleText = CommunityShared.roleText dto.Role
    member _.RoleAccent = CommunityShared.roleAccent dto.Role
    member _.RoleTint = CommunityShared.roleTint dto.Role
    member _.RoleEdge = CommunityShared.roleEdge dto.Role


type DmViewModel() =
    inherit ViewModelBase()

    let messages = ObservableCollection<DmMessageViewModel>()
    let threads = ObservableCollection<DmThreadViewModel>()

    let mutable isDev = false
    let mutable loaded = false
    let mutable busy = false
    let mutable status = ""
    let mutable draft = ""

    /// "" is the caller's own thread, which is the only one a player has.
    let mutable activeThread = ""
    let mutable activeTitle = ""

    let mutable viewerOpen = false
    let mutable viewerImage: Bitmap option = None
    let mutable viewerBytes: byte[] = null

    let mutable pending: ChatImages.Encoded option = None
    let mutable pendingPreview: Bitmap option = None
    let mutable isAttaching = false

    // -----------------------------------------------------------------------
    // What the window binds to
    // -----------------------------------------------------------------------
    member _.Messages = messages
    member _.Threads = threads
    member _.IsDevViewer = isDev
    member _.IsBusy = busy
    member _.IsIdle = not busy
    member _.Status = status
    member _.HasStatus = not (String.IsNullOrWhiteSpace(status))
    member _.HasMessages = messages.Count > 0
    member _.IsEmpty = loaded && messages.Count = 0
    member _.HasThreads = threads.Count > 0
    member _.ThreadsEmpty = loaded && isDev && threads.Count = 0

    /// The list of people beside the conversation is the developer's alone: a
    /// player has exactly one thread and never has to choose.
    member _.ShowThreadList = isDev
    member _.ActiveTitle = activeTitle
    member _.IsAttaching = isAttaching
    member _.HasPendingImage = pending.IsSome

    member _.PendingPreview =
        match pendingPreview with
        | Some b -> b
        | None -> null

    member this.Draft
        with get () = draft
        and set (value: string) =
            draft <- (if isNull value then "" else value)
            this.RaisePropertyChanged("Draft")
            this.RaisePropertyChanged("CanSend")

    member _.CanSend =
        not busy && not isAttaching && (draft.Trim().Length > 0 || pending.IsSome)

    // -----------------------------------------------------------------------
    // Internals
    // -----------------------------------------------------------------------
    member private this.SetStatus(text: string) =
        status <- (if isNull text then "" else text)
        this.RaisePropertyChanged("Status")
        this.RaisePropertyChanged("HasStatus")

    member private this.SetBusy(value: bool) =
        busy <- value
        this.RaisePropertyChanged("IsBusy")
        this.RaisePropertyChanged("IsIdle")
        this.RaisePropertyChanged("CanSend")

    member private this.RaiseLists() =
        for name in [ "HasMessages"; "IsEmpty"; "HasThreads"; "ThreadsEmpty"; "ActiveTitle" ] do
            this.RaisePropertyChanged(name)

    member private this.RaiseComposer() =
        for name in [ "Draft"; "CanSend"; "IsAttaching"; "HasPendingImage"; "PendingPreview" ] do
            this.RaisePropertyChanged(name)

    // -----------------------------------------------------------------------
    // Coming and going
    // -----------------------------------------------------------------------
    member this.Activate(dev: bool) =
        if dev <> isDev then
            isDev <- dev
            this.RaisePropertyChanged("IsDevViewer")
            this.RaisePropertyChanged("ShowThreadList")
            loaded <- false

        if not loaded then this.Refresh()

    member this.Refresh() =
        if not busy then
            this.SetBusy(true)
            this.SetStatus("")

            let thread = activeThread
            let wantThreads = isDev

            Task.Run(fun () ->
                // The developer needs the list of everyone as well as whichever
                // conversation is open; a player only ever needs the one.
                let list = if wantThreads then CommunityApi.listDmThreads () else Ok [||]
                let page = CommunityApi.listDm thread 0L

                CommunityShared.ui (fun () ->
                    loaded <- true

                    match list with
                    | Ok rows ->
                        threads.Clear()
                        for r in rows do threads.Add(DmThreadViewModel(r))
                    | Error _ -> ()

                    match page with
                    | Ok(rows, serverSaysDev) ->
                        // The server is the authority on who the developer is;
                        // the flag passed in is only what the app believed.
                        if serverSaysDev <> isDev then
                            isDev <- serverSaysDev
                            this.RaisePropertyChanged("IsDevViewer")
                            this.RaisePropertyChanged("ShowThreadList")

                        messages.Clear()

                        for r in rows do
                            let vm = DmMessageViewModel(r)
                            vm.EnsureImage()
                            messages.Add(vm)
                    | Error e -> this.SetStatus(e)

                    this.RaiseLists()
                    this.SetBusy(false)))
            |> ignore

    /// The developer opening one person's conversation.
    member this.OpenThread(item: DmThreadViewModel) =
        if not (isNull (box item)) && activeThread <> item.Thread then
            activeThread <- item.Thread
            activeTitle <- item.Person
            messages.Clear()
            loaded <- false
            this.RaiseLists()
            this.Refresh()

    // -----------------------------------------------------------------------
    // Writing
    // -----------------------------------------------------------------------
    member this.AttachImage(path: string) =
        if not isAttaching then
            isAttaching <- true
            this.RaiseComposer()

            Task.Run(fun () ->
                let encoded = ChatImages.fromFile path

                let preview =
                    match encoded with
                    | Ok e ->
                        try
                            use ms = new MemoryStream(e.Bytes)
                            Some(Bitmap.DecodeToWidth(ms, 240))
                        with _ ->
                            None
                    | Error _ -> None

                CommunityShared.ui (fun () ->
                    isAttaching <- false

                    match encoded with
                    | Ok e ->
                        pending <- Some e
                        pendingPreview <- preview
                    | Error message -> this.SetStatus(message)

                    this.RaiseComposer()))
            |> ignore

    /// A picture that arrived as bytes - what Ctrl+V hands over.
    member this.AttachImageBytes(bytes: byte[]) =
        if not isAttaching then
            isAttaching <- true
            this.RaiseComposer()

            Task.Run(fun () ->
                let encoded = ChatImages.fromBytes bytes

                let preview =
                    match encoded with
                    | Ok e ->
                        try
                            use ms = new MemoryStream(e.Bytes)
                            Some(Bitmap.DecodeToWidth(ms, 240))
                        with _ ->
                            None
                    | Error _ -> None

                CommunityShared.ui (fun () ->
                    isAttaching <- false

                    match encoded with
                    | Ok e ->
                        pending <- Some e
                        pendingPreview <- preview
                    | Error message -> this.SetStatus(message)

                    this.RaiseComposer()))
            |> ignore

    // ---- the picture viewer, as the public chat has it -------------------
    member _.IsViewerOpen = viewerOpen

    member _.ViewerImage =
        match viewerImage with
        | Some b -> b
        | None -> null

    member _.ViewerBytes = viewerBytes

    member private this.RaiseViewer() =
        for name in [ "IsViewerOpen"; "ViewerImage" ] do
            this.RaisePropertyChanged(name)

    member this.OpenViewer(item: DmMessageViewModel) =
        match item.FullBytes() with
        | Some bytes ->
            viewerBytes <- bytes
            viewerImage <- None
            viewerOpen <- true
            this.RaiseViewer()

            Task.Run(fun () ->
                let decoded =
                    try
                        use ms = new MemoryStream(bytes)
                        Some(new Bitmap(ms))
                    with _ ->
                        None

                CommunityShared.ui (fun () ->
                    if viewerOpen && Object.ReferenceEquals(viewerBytes, bytes) then
                        viewerImage <- decoded
                        this.RaiseViewer()))
            |> ignore
        | None -> ()

    member this.CloseViewer() =
        if viewerOpen then
            viewerOpen <- false
            viewerImage <- None
            viewerBytes <- null
            this.RaiseViewer()

    member this.RemoveImage() =
        pending <- None
        pendingPreview <- None
        this.RaiseComposer()

    member this.Send() =
        let text = draft.Trim()

        if not busy && not isAttaching && (text.Length > 0 || pending.IsSome) then
            this.SetBusy(true)
            this.SetStatus("")

            let image = pending
            let thread = activeThread

            draft <- ""
            pending <- None
            pendingPreview <- None
            this.RaiseComposer()

            Task.Run(fun () ->
                // The picture goes up by the public chat's own route, so its
                // bytes are already counted against the storage cap.
                let uploaded =
                    match image with
                    | Some e ->
                        match CommunityApi.uploadChatImage e.Bytes with
                        | Ok k -> Ok(k, e.Width, e.Height)
                        | Error m -> Error m
                    | None -> Ok("", 0, 0)

                let result =
                    match uploaded with
                    | Error m -> Error m
                    | Ok(key, w, h) -> CommunityApi.postDm thread text key w h

                CommunityShared.ui (fun () ->
                    this.SetBusy(false)

                    match result with
                    | Ok() ->
                        loaded <- false
                        this.Refresh()
                    | Error e ->
                        // Put the words back rather than losing them.
                        draft <- text
                        this.RaiseComposer()
                        this.SetStatus(e)))
            |> ignore
