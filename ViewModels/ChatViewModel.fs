namespace DLSS_5_MANAGER.ViewModels

open System
open System.Collections.Generic
open System.Collections.ObjectModel
open System.IO
open System.Runtime.InteropServices
open System.Text.Json
open System.Threading.Tasks
open Avalonia.Media.Imaging
open Avalonia.Threading
open DLSS_5_MANAGER.Services

/// The chat inside the community section (PULSE in the code, CHAT on screen).
///
/// Three rules keep it cheap, and none of them costs the user anything:
///
///   1. **Only new messages are ever fetched.** A poll asks for ids greater
///      than the newest one on screen, plus the reaction changes since the
///      last poll. History is never downloaded twice.
///   2. **The last messages are kept on disk.** Opening the chat after a
///      restart paints them immediately and asks only for what arrived since.
///   3. **Polling follows attention.** Every few seconds while the chat is on
///      screen and the window is in front; slower when nothing is happening;
///      a slow check for replies otherwise; nothing while the window is not
///      the one in front.
module PulseLimits =

    /// Long enough for a real question with a bit of context, short enough
    /// that nobody can paste an essay over everyone else.
    [<Literal>]
    let MaxChars = 500

    [<Literal>]
    let MaxWords = 100

    /// The server enforces its own limits; these only stop the user reaching
    /// for a button the server is about to refuse.
    [<Literal>]
    let MinGapSeconds = 2.0

    let wordCount (text: string) =
        if String.IsNullOrWhiteSpace(text) then 0
        else text.Split([| ' '; '\n'; '\r'; '\t' |], StringSplitOptions.RemoveEmptyEntries).Length

    /// What the duplicate check compares: case and spacing do not make a
    /// message different.
    let normalise (text: string) =
        if String.IsNullOrWhiteSpace(text) then ""
        else String.Join(" ", text.ToLowerInvariant().Split([| ' '; '\n'; '\r'; '\t' |], StringSplitOptions.RemoveEmptyEntries))

    /// "id|emoji" - how this device remembers which reactions are its own. The
    /// server cannot say (a cached answer is shared by everyone), so it is kept
    /// here.
    let reactionKey (id: int64) (emoji: string) = sprintf "%d|%s" id emoji

    /// What a reply shows of the message it answers.
    let snippet (body: string) (hasImage: bool) =
        if String.IsNullOrWhiteSpace(body) then (if hasImage then "📷 Photo" else "")
        elif body.Length > 140 then body.Substring(0, 140) + "…"
        else body

    /// A tally with one emoji moved up or down - what a tap shows at once,
    /// before the server has answered.
    let adjusted (rx: CommunityApi.ChatReactionDto[]) (emoji: string) (delta: int) : CommunityApi.ChatReactionDto[] =
        let list = ResizeArray<CommunityApi.ChatReactionDto>(if isNull (box rx) then [||] else rx)

        match Seq.tryFindIndex (fun (r: CommunityApi.ChatReactionDto) -> r.E = emoji) list with
        | Some i ->
            let n = list.[i].N + delta
            if n <= 0 then list.RemoveAt(i) else list.[i] <- { list.[i] with N = n }
        | None ->
            if delta > 0 then list.Add({ E = emoji; N = delta })

        list.ToArray()

    /// The composer's emoji palette.
    let palette =
        [| "😀"; "😁"; "😂"; "🤣"; "😊"; "🙂"; "😉"; "😍"; "🤩"; "😎"; "🤔"; "😮"; "😅"; "😬"; "😢"; "😭"
           "😡"; "🤯"; "🥳"; "😴"; "👍"; "👎"; "👏"; "🙌"; "🙏"; "💪"; "🤝"; "👀"; "🔥"; "❤️"; "💚"; "💯"
           "✅"; "❌"; "⚡"; "🚀"; "🎉"; "🎮"; "🕹️"; "💻"; "🖥️"; "🐛"; "⚠️"; "💀" |]


/// The soft two-note chime played when someone replies to one of your
/// messages. Generated in memory and played by Windows itself, so it needs no
/// sound file, no package, and never leaves the app.
module ReplyChime =

    [<DllImport("winmm.dll", EntryPoint = "PlaySoundW")>]
    extern bool private PlaySound(nativeint sound, nativeint hmod, uint32 flags)

    /// 16-bit mono PCM: a quiet high note falling into a higher one, each with
    /// a short fade so there is no click. Pinned for good, because an
    /// asynchronous PlaySound keeps reading the buffer after the call returns.
    let private wave =
        lazy
            (let rate = 22050

             let tone (freq: float) (millis: int) (amp: float) =
                 let n = rate * millis / 1000

                 Array.init n (fun i ->
                     let t = float i / float rate
                     let attack = min 1.0 (float i / (float rate * 0.006))
                     amp * attack * exp (-t * 16.0) * sin (2.0 * Math.PI * freq * t))

             let samples = Array.append (tone 880.0 110 0.20) (tone 1318.5 190 0.18)
             let dataBytes = samples.Length * 2

             use ms = new MemoryStream()
             use w = new BinaryWriter(ms)
             w.Write("RIFF"B)
             w.Write(36 + dataBytes)
             w.Write("WAVE"B)
             w.Write("fmt "B)
             w.Write(16)
             w.Write(1s)
             w.Write(1s)
             w.Write(rate)
             w.Write(rate * 2)
             w.Write(2s)
             w.Write(16s)
             w.Write("data"B)
             w.Write(dataBytes)

             for s in samples do
                 w.Write(int16 (Math.Clamp(s, -1.0, 1.0) * 32767.0))

             w.Flush()
             GCHandle.Alloc(ms.ToArray(), GCHandleType.Pinned))

    let play () =
        try
            // SND_ASYNC | SND_NODEFAULT | SND_MEMORY
            PlaySound(wave.Value.AddrOfPinnedObject(), 0n, 0x0001u ||| 0x0002u ||| 0x0004u) |> ignore
        with _ ->
            ()


/// One emoji: a reaction chip under a message, a quick-react button on the
/// hover bar, or an entry in the composer's palette. Immutable - a changed
/// tally replaces the chips rather than editing them.
type ChatReactionViewModel(messageId: int64, emoji: string, count: int, mine: bool) =
    member _.MessageId = messageId
    member _.Emoji = emoji
    member _.Count = count
    member _.CountText = string count
    member _.IsMine = mine


/// One message in the conversation.
type PulseMessageViewModel
    (initial: CommunityApi.ChatMessageDto, mine: bool, viewerIsDev: bool, myReactions: HashSet<string>) =
    inherit ViewModelBase()

    let mutable dto = initial

    /// On screen, not yet confirmed by the server. See `PulseViewModel.Send`.
    let mutable isPending = false

    /// The picture this device just sent, held so it is drawn from memory
    /// rather than downloaded back.
    let mutable localBytes: byte[] option = None

    let mutable image: Bitmap option = None
    let mutable imageStarted = false
    let mutable imageFailed = false
    let reactions = ObservableCollection<ChatReactionViewModel>()

    let rebuildReactions () =
        reactions.Clear()
        let rx = if isNull (box dto.Rx) then [||] else dto.Rx

        for r in rx do
            if r.N > 0 && not (String.IsNullOrEmpty(r.E)) then
                reactions.Add(
                    ChatReactionViewModel(dto.Id, r.E, r.N, myReactions.Contains(PulseLimits.reactionKey dto.Id r.E))
                )

    do rebuildReactions ()

    member _.Id = dto.Id
    member _.Dto = dto
    member _.Author = if String.IsNullOrWhiteSpace(dto.Author) then "Anonymous" else dto.Author

    member _.Initial =
        if String.IsNullOrWhiteSpace(dto.Author) then "?" else dto.Author.Substring(0, 1).ToUpperInvariant()

    /// Set by the server from the profile's own flag, not guessed from the
    /// name - so it cannot be faked by a lookalike.
    member _.IsVerified = dto.Dev

    member _.Body = if isNull dto.Body then "" else dto.Body
    member _.HasBody = not (String.IsNullOrWhiteSpace(dto.Body))
    member _.Ago = CommunityShared.ago dto.Created
    member _.IsMine = mine
    member _.IsTheirs = not mine
    member _.IsPending = isPending
    member _.IsSent = not isPending
    member _.CanDelete = (mine || viewerIsDev) && not isPending
    member _.CanReact = not isPending

    // ---- the message it answers ---------------------------------------------
    member _.HasReply = not (isNull (box dto.Reply)) && dto.Reply.Id > 0L
    member this.ReplyToId = if this.HasReply then dto.Reply.Id else 0L
    member this.ReplyAuthor = if this.HasReply then dto.Reply.Author else ""

    member this.ReplyBody =
        if not this.HasReply then ""
        elif String.IsNullOrWhiteSpace(dto.Reply.Body) then "📷 Photo"
        else dto.Reply.Body

    // ---- reactions -----------------------------------------------------------
    member _.Reactions = reactions
    member _.HasReactions = reactions.Count > 0

    /// The six on the hover bar, carrying this message's id so one handler
    /// serves them all.
    member _.QuickReactions =
        CommunityApi.chatReactions |> Array.map (fun e -> ChatReactionViewModel(dto.Id, e, 0, false))

    member this.SetReactions(rx: CommunityApi.ChatReactionDto[]) =
        dto <- { dto with Rx = (if isNull (box rx) then [||] else rx) }
        rebuildReactions ()
        this.RaisePropertyChanged("HasReactions")

    // ---- the picture ---------------------------------------------------------
    member _.HasImage = not (String.IsNullOrWhiteSpace(dto.Image)) || localBytes.IsSome

    /// The box the picture is drawn into, known before the picture arrives so
    /// the conversation does not jump when it does.
    member _.ImageWidth =
        if dto.W <= 0 || dto.H <= 0 then 260.0
        else min 280.0 (float dto.W)

    member this.ImageHeight =
        if dto.W <= 0 || dto.H <= 0 then 180.0
        else min 360.0 (this.ImageWidth * float dto.H / float dto.W)

    member _.ImageFailed = imageFailed
    member this.ImageLoading = this.HasImage && image.IsNone && not imageFailed
    member _.ImageStarted = imageStarted

    /// Fetched off the UI thread, from memory or disk when it has been seen.
    ///
    /// Started by the view when the picture's box actually scrolls into sight
    /// (see `OnChatImageViewportChanged`), not when the message is created -
    /// so opening the chat downloads and decodes only the pictures on screen,
    /// not every picture in the history.
    member this.EnsureImage() =
        if not imageStarted && this.HasImage then
            imageStarted <- true
            let key = dto.Image
            let local = localBytes

            Task.Run(fun () ->
                let bytes =
                    match local with
                    | Some b -> Some b
                    | None ->
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
                            Some(Bitmap.DecodeToWidth(ms, 560))
                        with _ ->
                            None
                    | None -> None

                CommunityShared.ui (fun () ->
                    image <- decoded
                    imageFailed <- decoded.IsNone

                    for name in [ "Image"; "ImageFailed"; "ImageLoading" ] do
                        this.RaisePropertyChanged(name)))
            |> ignore

    member _.Image =
        match image with
        | Some b -> b
        | None -> null

    /// The full picture, for the viewer and for Save - from memory or the disk
    /// copy. None until it has been downloaded once.
    member _.FullBytes() : byte[] option =
        match localBytes with
        | Some b -> Some b
        | None when not (String.IsNullOrWhiteSpace(dto.Image)) -> ChatImages.tryReadCached dto.Image
        | None -> None

    // ---- sending -------------------------------------------------------------
    member _.MarkPending(picture: byte[] option) =
        isPending <- true
        localBytes <- picture

    /// The server took it: the bubble becomes the real message in place, so
    /// nothing on screen flickers or moves.
    member this.Confirm(real: CommunityApi.ChatMessageDto) =
        dto <- real
        isPending <- false

        for name in [ "Id"; "Dto"; "Ago"; "IsPending"; "IsSent"; "CanDelete"; "CanReact"; "QuickReactions" ] do
            this.RaisePropertyChanged(name)


/// The conversation, the composer and the polling behind them.
type PulseViewModel() =
    inherit ViewModelBase()

    let messages = ObservableCollection<PulseMessageViewModel>()
    let byId = Dictionary<int64, PulseMessageViewModel>()
    let myReactions = HashSet<string>()

    /// Ids of messages this device sent - from its own tag on every message
    /// seen, and from each send. A new message replying to one of these is a
    /// reply to you. Kept on disk so it survives a restart.
    let myIds = HashSet<int64>()

    /// Replies already announced, so a repeated poll never chimes twice.
    let announced = HashSet<int64>()

    let mutable newestId = 0L
    let mutable oldestId = 0L
    let mutable hasOlder = true

    /// The reaction counter as of the last poll. See the Worker's `listChat`.
    let mutable lastRev = 0L

    let mutable draft = ""
    let mutable pending: ChatImages.Encoded option = None
    let mutable pendingPreview: Bitmap option = None
    let mutable replyingTo: PulseMessageViewModel option = None

    let mutable isAttaching = false
    let mutable isLoadingOlder = false
    let mutable isFirstLoad = false
    let mutable status = ""

    let mutable active = false
    let mutable windowActive = true
    let mutable polling = false
    let mutable quietPolls = 0
    let mutable timer: DispatcherTimer option = None
    let mutable trimmed = false

    /// The slow check for replies while the chat is not on screen. See
    /// `StartWatch`.
    let mutable watching = false

    // The reply alert.
    let mutable unreadReplies = 0
    let mutable replyToast = false
    let mutable replyToastText = ""
    let mutable toastTimer: DispatcherTimer option = None

    let mutable lastSentAt = DateTimeOffset.MinValue
    let mutable lastSentNorm = ""
    let mutable viewerIsDev = false
    let mutable myName = ""

    /// Bubbles not yet confirmed carry negative ids, so they can never collide
    /// with a real one.
    let mutable nextTempId = -1L

    // The picture viewer.
    let mutable viewerOpen = false
    let mutable viewerImage: Bitmap option = None
    let mutable viewerBytes: byte[] = null
    let mutable viewerId = 0L

    let palette =
        PulseLimits.palette |> Array.map (fun e -> ChatReactionViewModel(0L, e, 0, false))

    let appended = Event<unit>()

    /// How many messages survive a restart - also how many are drawn when the
    /// chat opens. Every message on screen is laid out, so this is kept small;
    /// the rest is one scroll up away.
    let keepOnDisk = 30

    /// How many messages are held in memory at most. Scrolling back past this
    /// loads older ones again; new arrivals push the oldest out.
    let keepInMemory = 150

    /// How often replies are looked for while the chat is not on screen. Only
    /// while the window is in front, and only once this device has sent
    /// something - nobody else can be replied to.
    let watchInterval = TimeSpan.FromSeconds(90.0)

    let fileIn (name: string) =
        lazy
            (Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DLSS5Manager",
                name
            ))

    let historyFile = fileIn "pulse_history.json"
    let reactionsFile = fileIn "pulse_reactions.json"
    let mineFile = fileIn "pulse_mine.json"

    let ui = CommunityShared.ui

    let isMine (dto: CommunityApi.ChatMessageDto) =
        not (isNull dto.Tag) && dto.Tag = CommunityApi.pulseTag.Value

    // ---------------------------------------------------------------------
    // The collection
    // ---------------------------------------------------------------------
    member private _.Wrap(dto: CommunityApi.ChatMessageDto) =
        PulseMessageViewModel(dto, isMine dto, viewerIsDev, myReactions)

    /// Newer messages go on the end. Anything already held is skipped, which is
    /// what makes a repeated or overlapping poll harmless.
    member private this.Append(list: CommunityApi.ChatMessageDto[]) =
        let mutable added = 0

        for dto in list |> Array.sortBy (fun d -> d.Id) do
            if not (byId.ContainsKey(dto.Id)) then
                let vm = this.Wrap(dto)
                byId.[dto.Id] <- vm
                messages.Add(vm)
                added <- added + 1

                if isMine dto then myIds.Add(dto.Id) |> ignore
                if dto.Id > newestId then newestId <- dto.Id
                if oldestId = 0L || dto.Id < oldestId then oldestId <- dto.Id

        // Only the latest messages are held. Past that the oldest are dropped
        // from memory - every message is drawn and every picture decoded, so a
        // long conversation kept whole made the app crawl. They are one scroll
        // away again through LoadOlder.
        if messages.Count > keepInMemory then
            while messages.Count > keepInMemory && not messages.[0].IsPending do
                let first = messages.[0]
                messages.RemoveAt(0)
                byId.Remove(first.Id) |> ignore

            oldestId <- messages.[0].Id
            hasOlder <- true

        added

    member private this.Prepend(list: CommunityApi.ChatMessageDto[]) =
        let older =
            list
            |> Array.filter (fun d -> not (byId.ContainsKey(d.Id)))
            |> Array.sortBy (fun d -> d.Id)

        for i in 0 .. older.Length - 1 do
            let vm = this.Wrap(older.[i])
            byId.[older.[i].Id] <- vm
            messages.Insert(i, vm)

            if isMine older.[i] then myIds.Add(older.[i].Id) |> ignore
            if oldestId = 0L || older.[i].Id < oldestId then oldestId <- older.[i].Id

        older.Length

    member private this.Remove(ids: int64[]) =
        if not (isNull (box ids)) then
            for id in ids do
                match byId.TryGetValue(id) with
                | true, vm ->
                    messages.Remove(vm) |> ignore
                    byId.Remove(id) |> ignore

                    match replyingTo with
                    | Some r when Object.ReferenceEquals(r, vm) ->
                        replyingTo <- None
                        this.RaiseReply()
                    | _ -> ()
                | _ -> ()

    /// Reaction changes from a poll, for messages already on screen.
    member private _.ApplyUpdates(updates: CommunityApi.ChatUpdateDto[]) =
        let mutable changed = 0

        if not (isNull (box updates)) then
            for u in updates do
                match byId.TryGetValue(u.Id) with
                | true, vm ->
                    vm.SetReactions(u.Rx)
                    changed <- changed + 1
                | _ -> ()

        changed

    member private this.RaiseList() =
        for name in [ "HasMessages"; "IsEmpty"; "HasOlder"; "IsFirstLoad" ] do
            this.RaisePropertyChanged(name)

    member this.TryFind(id: int64) =
        match byId.TryGetValue(id) with
        | true, vm -> Some vm
        | _ -> None

    // ---------------------------------------------------------------------
    // Disk
    // ---------------------------------------------------------------------
    member private this.SaveHistory() =
        try
            let latest =
                messages
                |> Seq.filter (fun m -> not m.IsPending)
                |> Seq.map (fun m -> m.Dto)
                |> Seq.toArray
                |> fun all -> if all.Length > keepOnDisk then all.[all.Length - keepOnDisk ..] else all

            File.WriteAllText(historyFile.Value, JsonSerializer.Serialize(latest))
        with _ ->
            ()

        this.SaveMine()

    member private this.LoadHistory() =
        try
            if File.Exists(historyFile.Value) then
                let saved =
                    JsonSerializer.Deserialize<CommunityApi.ChatMessageDto[]>(File.ReadAllText(historyFile.Value))

                // A file written by an older build may hold more; only the
                // latest are drawn, the rest come back by scrolling up.
                if not (isNull (box saved)) then
                    let latest =
                        if saved.Length > keepOnDisk then saved.[saved.Length - keepOnDisk ..] else saved

                    this.Append(latest) |> ignore
        with _ ->
            ()

    member private _.SaveReactions() =
        try
            File.WriteAllText(reactionsFile.Value, JsonSerializer.Serialize(Seq.toArray myReactions))
        with _ ->
            ()

    member private _.LoadReactions() =
        try
            if File.Exists(reactionsFile.Value) then
                let saved = JsonSerializer.Deserialize<string[]>(File.ReadAllText(reactionsFile.Value))

                if not (isNull saved) then
                    for key in saved do
                        myReactions.Add(key) |> ignore
        with _ ->
            ()

    /// The newest 500 of this device's own message ids. Older ones are very
    /// unlikely to be replied to now.
    member private _.SaveMine() =
        try
            let latest = myIds |> Seq.sortDescending |> Seq.truncate 500 |> Seq.toArray
            File.WriteAllText(mineFile.Value, JsonSerializer.Serialize(latest))
        with _ ->
            ()

    member private _.LoadMine() =
        try
            if File.Exists(mineFile.Value) then
                let saved = JsonSerializer.Deserialize<int64[]>(File.ReadAllText(mineFile.Value))

                if not (isNull saved) then
                    for id in saved do
                        myIds.Add(id) |> ignore
        with _ ->
            ()

    // ---------------------------------------------------------------------
    // The reply alert
    // ---------------------------------------------------------------------
    member _.HasReplyToast = replyToast
    member _.ReplyToastText = replyToastText
    member _.UnreadReplies = unreadReplies
    member _.HasUnreadReplies = unreadReplies > 0
    member _.UnreadRepliesText = if unreadReplies > 9 then "9+" else string unreadReplies

    member private this.RaiseReplyAlert() =
        for name in [ "HasReplyToast"; "ReplyToastText"; "UnreadReplies"; "HasUnreadReplies"; "UnreadRepliesText" ] do
            this.RaisePropertyChanged(name)

    member this.DismissReplyToast() =
        if replyToast then
            replyToast <- false
            this.RaiseReplyAlert()

    /// Someone replied to one of your messages. A soft chime always; and when
    /// the chat is not what you are looking at, a toast inside the app and a
    /// count on the CHAT tab. Nothing is ever shown outside the app.
    member private this.AnnounceReplies(replies: CommunityApi.ChatMessageDto[]) =
        ReplyChime.play ()

        if not (active && windowActive) then
            unreadReplies <- unreadReplies + replies.Length

            let last = replies.[replies.Length - 1]

            replyToastText <-
                if replies.Length = 1 then
                    let who = if String.IsNullOrWhiteSpace(last.Author) then "Someone" else last.Author
                    let what = PulseLimits.snippet last.Body (not (String.IsNullOrWhiteSpace(last.Image)))
                    if what = "" then sprintf "%s replied to you" who else sprintf "%s replied to you: %s" who what
                else
                    sprintf "%d new replies to your messages" replies.Length

            replyToast <- true
            this.RaiseReplyAlert()

            let t =
                match toastTimer with
                | Some t -> t
                | None ->
                    let t = DispatcherTimer(Interval = TimeSpan.FromSeconds(7.0))

                    t.Tick.Add(fun _ ->
                        t.Stop()
                        this.DismissReplyToast())

                    toastTimer <- Some t
                    t

            t.Stop()
            t.Start()

    // ---------------------------------------------------------------------
    // Polling
    // ---------------------------------------------------------------------
    /// Five seconds while people are talking, stretching to twenty when the
    /// room goes quiet, and back to five the moment anything arrives. A room
    /// nobody is typing in costs a request every twenty seconds, not twelve a
    /// minute - and the edge answers most of those without touching D1. Off
    /// screen it is only the reply check.
    member private _.NextInterval() =
        if not active then watchInterval
        elif quietPolls < 6 then TimeSpan.FromSeconds(5.0)
        elif quietPolls < 18 then TimeSpan.FromSeconds(10.0)
        else TimeSpan.FromSeconds(20.0)

    member private this.EnsureTimer() =
        match timer with
        | None ->
            let t = DispatcherTimer(Interval = this.NextInterval())
            t.Tick.Add(fun _ -> this.Poll())
            timer <- Some t
            t.Start()
        | Some t ->
            t.Interval <- this.NextInterval()
            t.Start()

    member private this.Poll() =
        let wanted = active || (watching && myIds.Count > 0)

        if wanted && windowActive && not polling then
            polling <- true
            let since = newestId
            let rev = lastRev

            Task.Run(fun () ->
                let outcome =
                    try
                        CommunityApi.chatSince since rev
                    with ex ->
                        Error ex.Message

                ui (fun () ->
                    polling <- false
                    isFirstLoad <- false

                    match outcome with
                    | Ok r ->
                        let incoming = if isNull (box r.Messages) then [||] else r.Messages
                        this.Remove(r.Removed)

                        // A first load with nothing on disk also learns whether
                        // there is history to scroll back to.
                        if since = 0L then hasOlder <- r.More

                        // Only what is genuinely new can be a new reply - never
                        // the page a first load fills the screen with.
                        let fresh =
                            if since > 0L then incoming |> Array.filter (fun d -> not (byId.ContainsKey(d.Id)))
                            else [||]

                        let added = this.Append(incoming)
                        let updated = this.ApplyUpdates(r.Updates)
                        if r.Rev > lastRev then lastRev <- r.Rev

                        let replies =
                            fresh
                            |> Array.filter (fun d ->
                                not (isNull (box d.Reply))
                                && d.Reply.Id > 0L
                                && myIds.Contains(d.Reply.Id)
                                && not (isMine d)
                                && announced.Add(d.Id))

                        if replies.Length > 0 then this.AnnounceReplies(replies)

                        let removedAny = not (isNull (box r.Removed)) && r.Removed.Length > 0

                        if added > 0 || updated > 0 || removedAny then
                            quietPolls <- 0
                            this.SaveHistory()
                            if added > 0 then appended.Trigger()
                        else
                            quietPolls <- quietPolls + 1

                        if status <> "" then this.SetStatus("")
                    | Error e ->
                        quietPolls <- quietPolls + 1

                        // Only worth saying when there is nothing else on screen.
                        if active && messages.Count = 0 then this.SetStatus(e)

                    this.RaiseList()

                    match timer with
                    | Some t -> t.Interval <- this.NextInterval()
                    | None -> ()))
            |> ignore

    /// Starts the slow reply check. Called once when the window opens. It
    /// makes no request at all until this device has sent a message, and none
    /// while the window is not the one in front.
    member this.StartWatch() =
        if not watching then
            watching <- true
            this.LoadMine()

            if messages.Count = 0 then
                this.LoadHistory()
                this.RaiseList()

            this.EnsureTimer()

    /// The chat came on screen.
    member this.Activate(isDev: bool, name: string) =
        viewerIsDev <- isDev
        myName <- (if isNull name then "" else name.Trim())
        active <- true

        if not trimmed then
            trimmed <- true
            this.LoadReactions()
            Task.Run(fun () -> ChatImages.trimCache (300L * 1024L * 1024L)) |> ignore

        if messages.Count = 0 then
            this.LoadHistory()
            isFirstLoad <- messages.Count = 0
            this.RaiseList()
            if messages.Count > 0 then appended.Trigger()

        // Looking at the chat is reading the replies.
        unreadReplies <- 0
        replyToast <- false
        this.RaiseReplyAlert()

        quietPolls <- 0
        this.EnsureTimer()
        this.Poll()

    /// The chat went off screen. Only the slow reply check carries on.
    member this.Deactivate() =
        active <- false

        match timer with
        | Some t when watching -> t.Interval <- this.NextInterval()
        | Some t -> t.Stop()
        | None -> ()

    /// The window lost or regained focus. A window in the background does not
    /// poll at all.
    member this.SetWindowActive(value: bool) =
        windowActive <- value
        if value && active then this.Poll()

    member this.LoadOlder() =
        if hasOlder && not isLoadingOlder && oldestId > 0L then
            isLoadingOlder <- true
            this.RaisePropertyChanged("IsLoadingOlder")
            let before = oldestId

            Task.Run(fun () ->
                let outcome =
                    try
                        CommunityApi.chatBefore before
                    with ex ->
                        Error ex.Message

                ui (fun () ->
                    isLoadingOlder <- false

                    match outcome with
                    | Ok r ->
                        this.Prepend(if isNull (box r.Messages) then [||] else r.Messages) |> ignore
                        hasOlder <- r.More
                    | Error _ -> hasOlder <- false

                    this.RaisePropertyChanged("IsLoadingOlder")
                    this.RaiseList()))
            |> ignore

    // ---------------------------------------------------------------------
    // State the view binds to
    // ---------------------------------------------------------------------
    member _.Messages = messages
    member _.HasMessages = messages.Count > 0
    member _.IsEmpty = messages.Count = 0 && not isFirstLoad
    member _.IsFirstLoad = isFirstLoad && messages.Count = 0
    member _.HasOlder = hasOlder
    member _.IsLoadingOlder = isLoadingOlder
    member _.Status = status
    member _.HasStatus = not (String.IsNullOrWhiteSpace(status))
    member _.EmojiPalette = palette

    [<CLIEvent>]
    member _.MessagesAppended = appended.Publish

    member this.Draft
        with get () = draft
        and set (value: string) =
            if this.SetProperty(&draft, (if isNull value then "" else value)) then
                for name in [ "CharCountText"; "IsOverLimit"; "ShowCharCount"; "CanSend" ] do
                    this.RaisePropertyChanged(name)

    member _.CharCountText = sprintf "%d / %d" draft.Length PulseLimits.MaxChars

    /// The counter only appears when it starts to matter.
    member _.ShowCharCount = draft.Length >= 400

    member _.IsOverLimit =
        draft.Length > PulseLimits.MaxChars || PulseLimits.wordCount draft > PulseLimits.MaxWords

    member this.CanSend =
        not isAttaching && not this.IsOverLimit && (draft.Trim().Length > 0 || pending.IsSome)

    member _.IsAttaching = isAttaching
    member _.HasPendingImage = pending.IsSome

    member _.PendingPreview =
        match pendingPreview with
        | Some b -> b
        | None -> null

    member _.IsReplying = replyingTo.IsSome

    member _.ReplyingToAuthor =
        match replyingTo with
        | Some m -> m.Author
        | None -> ""

    member _.ReplyingToSnippet =
        match replyingTo with
        | Some m -> PulseLimits.snippet m.Body m.HasImage
        | None -> ""

    member private this.SetStatus(text: string) =
        status <- text
        this.RaisePropertyChanged("Status")
        this.RaisePropertyChanged("HasStatus")

    member private this.RaiseComposer() =
        for name in
            [ "Draft"; "CharCountText"; "IsOverLimit"; "ShowCharCount"; "CanSend"; "IsAttaching"
              "HasPendingImage"; "PendingPreview" ] do
            this.RaisePropertyChanged(name)

    member private this.RaiseReply() =
        for name in [ "IsReplying"; "ReplyingToAuthor"; "ReplyingToSnippet" ] do
            this.RaisePropertyChanged(name)

    // ---------------------------------------------------------------------
    // Pictures in the composer
    // ---------------------------------------------------------------------
    /// Converts to WebP here, before anything is sent, so the user sees exactly
    /// what will go out.
    member private this.AttachWith(encode: unit -> Result<ChatImages.Encoded, string>) =
        if not isAttaching then
            isAttaching <- true
            this.RaiseComposer()

            Task.Run(fun () ->
                let encoded =
                    try
                        encode ()
                    with ex ->
                        Error ex.Message

                let preview =
                    match encoded with
                    | Ok e ->
                        try
                            use ms = new MemoryStream(e.Bytes)
                            Some(Bitmap.DecodeToWidth(ms, 240))
                        with _ ->
                            None
                    | Error _ -> None

                ui (fun () ->
                    isAttaching <- false

                    match encoded with
                    | Ok e ->
                        pending <- Some e
                        pendingPreview <- preview
                        this.SetStatus("")
                    | Error message -> this.SetStatus(message)

                    this.RaiseComposer()))
            |> ignore

    /// A picture picked from disk, or a picture file pasted from Explorer.
    member this.AttachImage(path: string) =
        if not (String.IsNullOrWhiteSpace(path)) then
            this.AttachWith(fun () -> ChatImages.fromFile path)

    /// A picture pasted from the clipboard - a screenshot, a copied image.
    member this.AttachImageBytes(bytes: byte[]) =
        if not (isNull bytes) && bytes.Length > 0 then
            this.AttachWith(fun () -> ChatImages.fromBytes bytes)

    member this.RemoveImage() =
        pending <- None
        pendingPreview <- None
        this.RaiseComposer()

    // ---------------------------------------------------------------------
    // Replies
    // ---------------------------------------------------------------------
    member this.StartReply(message: PulseMessageViewModel) =
        if not message.IsPending then
            replyingTo <- Some message
            this.RaiseReply()

    member this.CancelReply() =
        if replyingTo.IsSome then
            replyingTo <- None
            this.RaiseReply()

    // ---------------------------------------------------------------------
    // Sending
    // ---------------------------------------------------------------------
    /// The message appears the instant Send is pressed and the box is cleared
    /// for the next one; the server's answer then confirms the bubble in place.
    /// If the server refuses, the bubble goes and everything typed comes back -
    /// nothing is ever lost to a failed send.
    member this.Send() =
        let text = draft.Trim()
        let norm = PulseLimits.normalise text

        if isAttaching || (text.Length = 0 && pending.IsNone) then
            ()
        elif this.IsOverLimit then
            this.SetStatus(sprintf "Keep it under %d characters and %d words." PulseLimits.MaxChars PulseLimits.MaxWords)
        elif (DateTimeOffset.UtcNow - lastSentAt).TotalSeconds < PulseLimits.MinGapSeconds then
            this.SetStatus("A moment between messages, please.")
        elif norm <> "" && norm = lastSentNorm && pending.IsNone then
            this.SetStatus("You just sent that.")
        else
            let image = pending
            let preview = pendingPreview
            let reply = replyingTo
            let previousAt = lastSentAt
            let previousNorm = lastSentNorm

            lastSentAt <- DateTimeOffset.UtcNow
            lastSentNorm <- norm

            let tempId = nextTempId
            nextTempId <- nextTempId - 1L

            let local: CommunityApi.ChatMessageDto =
                { Id = tempId
                  Author = (if myName = "" then "You" else myName)
                  Tag = CommunityApi.pulseTag.Value
                  Dev = viewerIsDev
                  Body = text
                  Image = ""
                  W = (match image with Some e -> e.Width | None -> 0)
                  H = (match image with Some e -> e.Height | None -> 0)
                  Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                  Reply =
                    (match reply with
                     | Some m ->
                         { Id = m.Id
                           Author = m.Author
                           Body = PulseLimits.snippet m.Body m.HasImage }
                     | None -> Unchecked.defaultof<_>)
                  Rx = [||] }

            let bubble = this.Wrap(local)
            bubble.MarkPending(image |> Option.map (fun e -> e.Bytes))
            messages.Add(bubble)

            draft <- ""
            pending <- None
            pendingPreview <- None
            replyingTo <- None
            this.SetStatus("")
            this.RaiseComposer()
            this.RaiseReply()
            this.RaiseList()
            appended.Trigger()

            let replyId =
                match reply with
                | Some m -> m.Id
                | None -> 0L

            Task.Run(fun () ->
                let outcome =
                    try
                        let uploaded =
                            match image with
                            | Some e -> CommunityApi.uploadChatImage e.Bytes |> Result.map (fun key -> key, e.Width, e.Height)
                            | None -> Ok("", 0, 0)

                        uploaded
                        |> Result.bind (fun (key, w, h) ->
                            // The just-uploaded picture is kept locally under its
                            // key, so the sender never downloads their own image.
                            match image with
                            | Some e when key <> "" -> ChatImages.writeCached key e.Bytes
                            | _ -> ()

                            CommunityApi.postChat text key w h replyId)
                    with ex ->
                        Error ex.Message

                ui (fun () ->
                    match outcome with
                    | Ok sent ->
                        quietPolls <- 0
                        myIds.Add(sent.Id) |> ignore

                        // A poll that landed first may already hold it.
                        if byId.ContainsKey(sent.Id) then
                            messages.Remove(bubble) |> ignore
                        else
                            bubble.Confirm(sent)
                            byId.[sent.Id] <- bubble
                            if sent.Id > newestId then newestId <- sent.Id
                            if oldestId = 0L || sent.Id < oldestId then oldestId <- sent.Id

                        this.SaveHistory()
                    | Error message ->
                        messages.Remove(bubble) |> ignore
                        lastSentAt <- previousAt
                        lastSentNorm <- previousNorm

                        if draft = "" then draft <- text

                        if pending.IsNone && image.IsSome then
                            pending <- image
                            pendingPreview <- preview

                        if replyingTo.IsNone then replyingTo <- reply

                        this.SetStatus(message)
                        this.RaiseComposer()
                        this.RaiseReply()

                    this.RaiseList()))
            |> ignore

    member this.Delete(message: PulseMessageViewModel) =
        if message.CanDelete then
            Task.Run(fun () ->
                match CommunityApi.deleteChat message.Id with
                | Ok() ->
                    ui (fun () ->
                        this.Remove([| message.Id |])
                        this.SaveHistory()
                        this.RaiseList())
                | Error e -> ui (fun () -> this.SetStatus(e)))
            |> ignore

    // ---------------------------------------------------------------------
    // Reactions
    // ---------------------------------------------------------------------
    /// The chip changes on the tap; the server's tally then replaces it. On an
    /// error the tap is undone.
    member this.ToggleReaction(messageId: int64, emoji: string) =
        match byId.TryGetValue(messageId) with
        | true, message when not message.IsPending ->
            let key = PulseLimits.reactionKey messageId emoji
            let wasOn = myReactions.Contains(key)
            let before = message.Dto.Rx

            if wasOn then myReactions.Remove(key) |> ignore else myReactions.Add(key) |> ignore
            message.SetReactions(PulseLimits.adjusted before emoji (if wasOn then -1 else 1))

            Task.Run(fun () ->
                let outcome =
                    try
                        CommunityApi.reactChat messageId emoji
                    with ex ->
                        Error ex.Message

                ui (fun () ->
                    match outcome with
                    | Ok(on, rx) ->
                        if on then myReactions.Add(key) |> ignore else myReactions.Remove(key) |> ignore
                        message.SetReactions(rx)
                        this.SaveReactions()
                        this.SaveHistory()
                    | Error e ->
                        if wasOn then myReactions.Add(key) |> ignore else myReactions.Remove(key) |> ignore
                        message.SetReactions(before)
                        this.SetStatus(e)))
            |> ignore
        | _ -> ()

    // ---------------------------------------------------------------------
    // The picture viewer
    // ---------------------------------------------------------------------
    member _.IsViewerOpen = viewerOpen

    member _.ViewerImage =
        match viewerImage with
        | Some b -> b
        | None -> null

    member _.ViewerBytes = viewerBytes
    member _.ViewerFileName = sprintf "dlss5-chat-%d" viewerId

    member private this.RaiseViewer() =
        for name in [ "IsViewerOpen"; "ViewerImage"; "ViewerFileName" ] do
            this.RaisePropertyChanged(name)

    /// Opens the picture at full size. Decoded off the UI thread; a picture
    /// that has not finished downloading yet simply does not open.
    member this.OpenViewer(message: PulseMessageViewModel) =
        match message.FullBytes() with
        | Some bytes ->
            viewerBytes <- bytes
            viewerId <- max 0L message.Id
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

                ui (fun () ->
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
