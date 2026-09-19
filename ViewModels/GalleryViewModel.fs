namespace DLSS_5_MANAGER.ViewModels

open System
open System.Collections.ObjectModel
open System.IO
open System.Threading.Tasks
open Avalonia.Media.Imaging
open DLSS_5_MANAGER.Services

/// The gallery: a moderated picture wall beside the chat.
///
/// The chat is fast and unfiltered; this is the opposite, and deliberately so.
/// Anyone may upload, but nothing is shown to anybody until the developer has
/// approved it - so a picture the user sends simply disappears from their view
/// until it is let through, which is why the tab says so in plain words.
///
/// Pictures share the chat's R2 key space and its disk cache, so one promoted
/// out of a conversation is already on this machine and is never fetched twice.

/// One picture: on the wall, or waiting for a decision.
type GalleryItemViewModel(dto: CommunityApi.GalleryItemDto) =
    inherit ViewModelBase()

    let mutable image: Bitmap option = None
    let mutable started = false
    let mutable failed = false

    member _.Id = dto.Id
    member _.Dto = dto
    member _.ImageKey = dto.Image

    member _.Author =
        if String.IsNullOrWhiteSpace(dto.Author) then "someone" else dto.Author

    member _.Caption = if isNull dto.Caption then "" else dto.Caption
    member _.HasCaption = not (String.IsNullOrWhiteSpace(dto.Caption))
    member _.Game = if isNull dto.Game then "" else dto.Game
    member _.HasGame = not (String.IsNullOrWhiteSpace(dto.Game))
    member _.Ago = CommunityShared.ago dto.Created

    /// Where it came from. Worth showing in the queue: a picture promoted out
    /// of the chat is already approved, so anything waiting came from an upload.
    member _.IsFromChat =
        not (isNull dto.Source) && dto.Source = "chat"

    // The badge, drawn exactly as the chat and the reports draw it.
    member _.HasRole = CommunityShared.hasRole dto.Role
    member _.RoleText = CommunityShared.roleText dto.Role
    member _.RoleAccent = CommunityShared.roleAccent dto.Role
    member _.RoleTint = CommunityShared.roleTint dto.Role
    member _.RoleEdge = CommunityShared.roleEdge dto.Role

    member _.Image =
        match image with
        | Some b -> b
        | None -> null

    /// What to draw in the box until the picture arrives.
    member _.ImageStarted = started
    member _.ImageFailed = failed
    member _.ImageLoading = image.IsNone && not failed

    /// Fetched off the UI thread, from the disk copy once it has been seen -
    /// a picture never changes under its key, so that copy is good forever.
    ///
    /// Started by the view when the card actually comes within reach of the
    /// visible part of the wall (see `OnGalleryImageViewportChanged`), and not
    /// when the list arrives. Opening the tab therefore fetches the handful of
    /// pictures on screen instead of every picture on the wall at once.
    member this.EnsureImage() =
        if not started then
            started <- true

            Task.Run(fun () ->
                let key = dto.Image

                let bytes =
                    match ChatImages.tryReadCached key with
                    | Some b -> Some b
                    | None ->
                        match CommunityApi.getChatImage key with
                        | Ok b ->
                            ChatImages.writeCached key b
                            Some b
                        | Error _ -> None

                let decoded =
                    match bytes with
                    | Some b ->
                        try
                            use ms = new MemoryStream(b)
                            Some(Bitmap.DecodeToWidth(ms, 560))
                        with _ ->
                            None
                    | None -> None

                CommunityShared.ui (fun () ->
                    image <- decoded
                    failed <- decoded.IsNone

                    for name in [ "Image"; "ImageFailed"; "ImageLoading" ] do
                        this.RaisePropertyChanged(name)))
            |> ignore

    /// The whole picture, for the viewer and for Save. `None` until it has been
    /// downloaded once - a card that has not loaded yet simply does not open.
    member _.FullBytes() : byte[] option =
        if String.IsNullOrWhiteSpace(dto.Image) then
            None
        else
            ChatImages.tryReadCached dto.Image


/// The wall, the queue behind it, and the one upload box.
type GalleryViewModel() =
    inherit ViewModelBase()

    let items = ObservableCollection<GalleryItemViewModel>()
    let pending = ObservableCollection<GalleryItemViewModel>()

    let mutable isDev = false
    let mutable loaded = false
    let mutable busy = false
    let mutable status = ""
    let mutable queueOpen = false
    let mutable caption = ""

    // The picture viewer, built the same way the chat's is.
    let mutable viewerOpen = false
    let mutable viewerImage: Bitmap option = None
    let mutable viewerBytes: byte[] = null
    let mutable viewerName = "dlss5-gallery"

    // -----------------------------------------------------------------------
    // What the window binds to
    // -----------------------------------------------------------------------
    member _.Items = items
    member _.Pending = pending

    /// Only decides whether the queue is offered at all. The server checks the
    /// developer flag itself before showing or deciding anything.
    member _.IsDevViewer = isDev

    member _.IsBusy = busy

    /// Bound by the upload button, which must not be pressable twice.
    member _.IsIdle = not busy

    member _.Status = status
    member _.HasStatus = not (String.IsNullOrWhiteSpace(status))

    member _.IsQueueOpen = queueOpen
    member _.IsWallOpen = not queueOpen

    member _.HasItems = items.Count > 0

    /// Only after a load has actually happened - an empty wall and a wall that
    /// has not been fetched yet look the same otherwise.
    member _.IsEmpty = loaded && items.Count = 0

    member _.PendingCount = pending.Count
    member _.HasPending = pending.Count > 0
    member _.QueueIsEmpty = loaded && pending.Count = 0
    member _.PendingText = string pending.Count

    member this.Caption
        with get () = caption
        and set (value: string) =
            caption <- (if isNull value then "" else value)
            this.RaisePropertyChanged("Caption")

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

    member private this.RaiseWall() =
        this.RaisePropertyChanged("HasItems")
        this.RaisePropertyChanged("IsEmpty")

    member private this.RaiseQueue() =
        this.RaisePropertyChanged("PendingCount")
        this.RaisePropertyChanged("HasPending")
        this.RaisePropertyChanged("QueueIsEmpty")
        this.RaisePropertyChanged("PendingText")

    /// Note what this deliberately does **not** do: it never starts a download.
    /// Sixty cards arriving at once would otherwise mean sixty requests and
    /// sixty decodes in the same breath, which is exactly the stall the chat
    /// avoids. The view asks for each picture as its card comes into reach.
    member private _.Fill (target: ObservableCollection<GalleryItemViewModel>) (list: CommunityApi.GalleryItemDto[]) =
        target.Clear()

        for d in list do
            target.Add(GalleryItemViewModel(d))

    // -----------------------------------------------------------------------
    // Coming and going
    // -----------------------------------------------------------------------

    /// The tab came on screen. The wall is a curated page rather than a live
    /// feed, so it is fetched once and then only when asked for - opening the
    /// tab a second time costs nothing.
    member this.Activate(dev: bool) =
        if dev <> isDev then
            isDev <- dev
            this.RaisePropertyChanged("IsDevViewer")

            // Somebody who is not the developer has no queue to be looking at.
            if not dev && queueOpen then this.ShowWall()

        if not loaded then this.Refresh()

    member this.Refresh() =
        if not busy then
            this.SetBusy(true)
            this.SetStatus("")

            Task.Run(fun () ->
                let wall = CommunityApi.listGallery 60

                // Asked for only when this device believes it is the developer.
                // The server would refuse it anyway; this just avoids the call.
                let queue =
                    if isDev then CommunityApi.listGalleryPending () else Ok [||]

                CommunityShared.ui (fun () ->
                    loaded <- true

                    match wall with
                    | Ok list -> this.Fill items list
                    | Error e -> this.SetStatus(e)

                    // A queue that cannot be read is not an error worth showing:
                    // it means this device is not the developer after all.
                    match queue with
                    | Ok list -> this.Fill pending list
                    | Error _ -> pending.Clear()

                    this.RaiseWall()
                    this.RaiseQueue()
                    this.SetBusy(false)))
            |> ignore

    member this.ShowWall() =
        if queueOpen then
            queueOpen <- false
            this.RaisePropertyChanged("IsQueueOpen")
            this.RaisePropertyChanged("IsWallOpen")

    member this.ShowQueue() =
        if not queueOpen && isDev then
            queueOpen <- true
            this.RaisePropertyChanged("IsQueueOpen")
            this.RaisePropertyChanged("IsWallOpen")

    // -----------------------------------------------------------------------
    // Uploading, and the two decisions
    // -----------------------------------------------------------------------

    /// A picture picked from disk. It is turned into WebP here, the same way
    /// the chat does it, and goes straight into the queue.
    member this.Upload(path: string) =
        if not busy then
            this.SetBusy(true)
            this.SetStatus("")

            // Read before the work moves off this thread, so a caption typed
            // and then changed cannot follow the picture up.
            let text = caption

            Task.Run(fun () ->
                let result =
                    match ChatImages.fromFile path with
                    | Error e -> Error e
                    | Ok encoded ->
                        CommunityApi.uploadGalleryImage encoded.Bytes encoded.Width encoded.Height text ""

                CommunityShared.ui (fun () ->
                    this.SetBusy(false)

                    match result with
                    | Ok () ->
                        this.Caption <- ""
                        this.SetStatus("Sent. It appears once the developer has approved it.")

                        try
                            UiSounds.published ()
                        with _ ->
                            ()

                        // The developer sees their own upload arrive in the queue.
                        if isDev then this.Refresh()
                    | Error e -> this.SetStatus(e)))
            |> ignore

    member private this.Decide(item: GalleryItemViewModel, state: string) =
        if not busy && not (isNull (box item)) then
            this.SetBusy(true)
            this.SetStatus("")
            let id = item.Id

            Task.Run(fun () ->
                let result = CommunityApi.decideGallery id state

                CommunityShared.ui (fun () ->
                    this.SetBusy(false)

                    match result with
                    | Ok () ->
                        pending.Remove(item) |> ignore
                        this.RaiseQueue()

                        // An approved picture belongs on the wall immediately;
                        // a rejected one is gone from the server already.
                        if state = "approved" then
                            items.Insert(0, item)
                            this.RaiseWall()

                        try
                            UiSounds.tick ()
                        with _ ->
                            ()
                    | Error e -> this.SetStatus(e)))
            |> ignore

    member this.Approve(item: GalleryItemViewModel) = this.Decide(item, "approved")
    member this.Reject(item: GalleryItemViewModel) = this.Decide(item, "rejected")

    // -----------------------------------------------------------------------
    // The picture viewer - the same behaviour as the chat's, on its own state
    // so that opening a wall picture can never be mistaken for a chat message.
    // -----------------------------------------------------------------------
    member _.IsViewerOpen = viewerOpen

    member _.ViewerImage =
        match viewerImage with
        | Some b -> b
        | None -> null

    member _.ViewerBytes = viewerBytes
    member _.ViewerFileName = viewerName

    member private this.RaiseViewer() =
        for name in [ "IsViewerOpen"; "ViewerImage"; "ViewerFileName" ] do
            this.RaisePropertyChanged(name)

    /// Opens the picture at full size, decoded off the UI thread. A card whose
    /// picture has not finished downloading yet simply does not open.
    member this.OpenViewer(item: GalleryItemViewModel) =
        if not (isNull (box item)) then
            match item.FullBytes() with
            | Some bytes ->
                viewerBytes <- bytes
                viewerName <- "dlss5-gallery-" + item.Id
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
                        // Another picture may have been opened while this one
                        // was decoding; the bytes say which one won.
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

    /// Developer only: a picture from the chat, straight onto the wall. The
    /// server copies the stored object rather than pointing at it, so the
    /// chat's own clean-up can never delete a picture the wall is showing.
    member this.Promote(chatId: int64, text: string) =
        if not busy && chatId > 0L then
            this.SetBusy(true)
            this.SetStatus("")

            Task.Run(fun () ->
                let result = CommunityApi.promoteChatImage chatId text

                CommunityShared.ui (fun () ->
                    this.SetBusy(false)

                    match result with
                    | Ok () ->
                        this.SetStatus("Added to the gallery.")

                        try
                            UiSounds.published ()
                        with _ ->
                            ()

                        this.Refresh()
                    | Error e -> this.SetStatus(e)))
            |> ignore
