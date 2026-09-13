namespace DLSS_5_MANAGER.ViewModels

open System
open System.Collections
open System.Collections.ObjectModel
open System.IO
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Avalonia.Media
open Avalonia.Media.Imaging
open Avalonia.Threading
open DLSS_5_MANAGER.Models
open DLSS_5_MANAGER.Services

/// The community section: what other people got working, on which route, and
/// on what hardware.
///
/// Everything here is read-only until the user claims a display name, and the
/// name is the only thing they have to give - there is no account, no email and
/// no password. Identity is a one-way hash of the machine GUID, which is also
/// what stops one device posting about the same game twice.
module CommunityShared =

    /// Cover art downloaded from the server, kept beside the rest of the
    /// artwork cache so clearing the cache clears these too.
    let coverCacheDir =
        lazy
            (let dir =
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DLSS5Manager",
                    "Cache",
                    "Community"
                )

             try
                 Directory.CreateDirectory(dir) |> ignore
             with _ ->
                 ()

             dir)

    let private http =
        lazy
            (let c = new HttpClient()
             c.Timeout <- TimeSpan.FromSeconds(15.0)
             c)

    let private hashOf (text: string) =
        use sha = SHA256.Create()
        sha.ComputeHash(Encoding.UTF8.GetBytes(text))
        |> Array.take 10
        |> Array.map (fun b -> b.ToString("x2"))
        |> String.concat ""

    /// Decoded artwork, kept for the life of the process and keyed by URL.
    ///
    /// Leaving the section and coming back rebuilds every card view-model, and
    /// without this each one would go to disk and decode a JPEG again. The
    /// bitmaps are small and there are at most a hundred of them.
    let private decoded = Concurrent.ConcurrentDictionary<string, Bitmap>()

    /// Four downloads at a time. The grid asks for every cover at once, and a
    /// hundred simultaneous requests punish the connection without arriving any
    /// sooner.
    let private downloadSlots = new SemaphoreSlim(4)

    /// What is already decoded, with no work at all. Used to paint a card that
    /// has been on screen before without waiting for a thread.
    let cachedCover (url: string) : Bitmap option =
        if String.IsNullOrWhiteSpace(url) then None
        else
            match decoded.TryGetValue(url) with
            | true, b -> Some b
            | _ -> None

    /// Downloads once, then reads from disk forever, then from memory. Returns
    /// None rather than throwing - a missing cover is a placeholder, not an
    /// error. Call this off the UI thread: it blocks.
    let loadCover (url: string) : Bitmap option =
        if String.IsNullOrWhiteSpace(url) then None
        else
            match decoded.TryGetValue(url) with
            | true, b -> Some b
            | _ ->
                try
                    let file = Path.Combine(coverCacheDir.Value, hashOf url + ".jpg")

                    if not (File.Exists(file)) then
                        downloadSlots.Wait()

                        try
                            // Another card may have fetched it while this one
                            // waited for a slot.
                            if not (File.Exists(file)) then
                                let bytes = http.Value.GetByteArrayAsync(url).GetAwaiter().GetResult()
                                if bytes.Length < 512 then failwith "empty"
                                File.WriteAllBytes(file, bytes)
                        finally
                            downloadSlots.Release() |> ignore

                    use stream = File.OpenRead(file)
                    let bitmap = Bitmap.DecodeToWidth(stream, 420)
                    Some(decoded.GetOrAdd(url, bitmap))
                with _ ->
                    None

    /// "4 minutes ago" - short enough for a chip, precise enough to be useful.
    let ago (unixSeconds: int64) =
        try
            let span = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(unixSeconds)
            let m = int span.TotalMinutes

            if m < 1 then "just now"
            elif m < 60 then sprintf "%d minute%s ago" m (if m = 1 then "" else "s")
            elif m < 1440 then
                let h = m / 60
                sprintf "%d hour%s ago" h (if h = 1 then "" else "s")
            else
                let d = m / 1440
                sprintf "%d day%s ago" d (if d = 1 then "" else "s")
        with _ ->
            ""

    /// The install route, spelled the way the Manage sheet spells it.
    let routeLabel (route: string) (api: string) (arch: string) =
        let baseName =
            match route with
            | "optiscaler" ->
                match api with
                | "vulkan" -> "OptiScaler · Vulkan"
                | "neural" -> "OptiScaler · Neural"
                | _ -> "OptiScaler · DX12"
            | "dx12" -> "ReShade · DX12"
            | "dx11" -> "ReShade · DX11"
            | "dx9" -> "ReShade · DX9"
            | "emulator" -> "Emulator"
            | "amd" -> "AMD RDNA 4"
            | _ -> "Unknown"

        if arch = "32" then baseName + " · 32-bit" else baseName

    /// Bindings want a brush, not a hex string - Avalonia will not convert one
    /// for you, and a string bound to Foreground silently paints nothing.
    let private brush (hex: string) : IBrush =
        SolidColorBrush(Color.Parse(hex)) :> IBrush

    /// The same colour at low opacity, for the pill behind the text.
    let private tint (hex: string) (alpha: byte) : IBrush =
        let c = Color.Parse(hex)
        SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B)) :> IBrush

    let private routeHex (route: string) =
        match route with
        | "optiscaler" -> "#38BDF8"
        | "dx12" | "dx11" | "dx9" -> "#A78BFA"
        | "emulator" -> "#34D399"
        | "amd" -> "#F59E0B"
        | _ -> "#94A3B8"

    let routeAccent (route: string) = brush (routeHex route)
    let routeTint (route: string) = tint (routeHex route) 34uy
    let routeEdge (route: string) = tint (routeHex route) 90uy

    let statusText (status: string) =
        match status with
        | "working" -> "WORKING"
        | "broken" -> "NOT WORKING"
        | "mixed" -> "MIXED"
        | _ -> "UNKNOWN"

    let private statusHex (status: string) =
        match status with
        | "working" -> "#22C55E"
        | "broken" -> "#EF4444"
        | "mixed" -> "#F59E0B"
        | _ -> "#64748B"

    let statusAccent (status: string) = brush (statusHex status)
    let statusTint (status: string) = tint (statusHex status) 40uy
    let statusEdge (status: string) = tint (statusHex status) 110uy

    /// Marshals back to the UI thread. Every network reply lands here before it
    /// touches a collection the window is bound to.
    let ui (f: unit -> unit) =
        if Dispatcher.UIThread.CheckAccess() then f () else Dispatcher.UIThread.Post(f)

    /// The developer's own handle. The server refuses every name containing
    /// "nodix" to anyone else, so a post carrying this name really is his - the
    /// badge here only shows what the server already guaranteed.
    [<Literal>]
    let VerifiedName = "NODIX TECH"

    let isVerified (name: string) =
        not (String.IsNullOrWhiteSpace(name))
        && name.Trim().Equals(VerifiedName, StringComparison.OrdinalIgnoreCase)

    // -----------------------------------------------------------------------
    // In-app cache
    //
    // The Worker already caches at the edge, but a round trip is still a round
    // trip: leaving the section and coming back, or flipping a filter back to
    // one just used, should cost nothing at all. Answers are held here for a
    // few minutes and thrown away the moment this app writes something, so a
    // post the user just made never shows up missing from their own list.
    // -----------------------------------------------------------------------
    let private cacheLife = TimeSpan.FromMinutes(5.0)

    let private listCache =
        Concurrent.ConcurrentDictionary<string, DateTimeOffset * CommunityApi.GameDto[] * int>()

    let private feedCache =
        Concurrent.ConcurrentDictionary<string, DateTimeOffset * CommunityApi.ReportDto[] * CommunityApi.RouteStatDto[]>()

    let private fresh (stamp: DateTimeOffset) = DateTimeOffset.UtcNow - stamp < cacheLife

    let cachedList (key: string) =
        match listCache.TryGetValue(key) with
        | true, (stamp, games, total) when fresh stamp -> Some(games, total)
        | _ -> None

    let rememberList (key: string) (games: CommunityApi.GameDto[]) (total: int) =
        listCache.[key] <- (DateTimeOffset.UtcNow, games, total)

    let cachedFeed (key: string) =
        match feedCache.TryGetValue(key) with
        | true, (stamp, reports, routes) when fresh stamp -> Some(reports, routes)
        | _ -> None

    let rememberFeed (key: string) (reports: CommunityApi.ReportDto[]) (routes: CommunityApi.RouteStatDto[]) =
        feedCache.[key] <- (DateTimeOffset.UtcNow, reports, routes)

    /// Called after this app posts, comments or reacts, and by the Refresh
    /// button. Anything held could now be out of date.
    let dropCaches () =
        listCache.Clear()
        feedCache.Clear()


/// One route's tally inside the sheet header - the row of chips that says
/// which install method people actually had luck with on this game.
type RouteChipViewModel(stat: CommunityApi.RouteStatDto) =
    inherit ViewModelBase()

    member _.Route = stat.Route
    member _.Label = CommunityShared.routeLabel stat.Route "" ""
    member _.Accent = CommunityShared.routeAccent stat.Route
    member _.Tint = CommunityShared.routeTint stat.Route
    member _.Edge = CommunityShared.routeEdge stat.Route
    member _.Working = stat.Working
    member _.Mixed = stat.Mixed
    member _.Broken = stat.Broken
    member _.Total = stat.Total


/// One reply under a report.
type CommunityCommentViewModel(dto: CommunityApi.CommentDto) =
    inherit ViewModelBase()

    member _.Id = dto.Id
    member _.Author = if String.IsNullOrWhiteSpace(dto.Author) then "Anonymous" else dto.Author
    member _.Body = dto.Body
    member _.Ago = CommunityShared.ago dto.Created
    member _.IsVerified = CommunityShared.isVerified dto.Author


/// One post about one game.
type CommunityReportViewModel(dto: CommunityApi.ReportDto) =
    inherit ViewModelBase()

    let comments = ObservableCollection<CommunityCommentViewModel>()
    let mutable counts = if isNull (box dto.Reactions) then Array.zeroCreate 5 else dto.Reactions
    let mutable isCommentsOpen = false
    let mutable isLoadingComments = false
    let mutable replyText = ""
    let mutable commentCount = dto.Comments

    member _.Id = dto.Id
    member _.Author = if String.IsNullOrWhiteSpace(dto.Author) then "Anonymous" else dto.Author

    /// Stands in for an avatar - there are no uploads here.
    member _.Initial =
        if String.IsNullOrWhiteSpace(dto.Author) then "?" else dto.Author.Substring(0, 1).ToUpperInvariant()

    member _.Ago = CommunityShared.ago dto.Created
    member _.Body = dto.Body
    member _.HasBody = not (String.IsNullOrWhiteSpace(dto.Body))

    member _.StatusText = CommunityShared.statusText dto.Status
    member _.StatusAccent = CommunityShared.statusAccent dto.Status
    member _.StatusTint = CommunityShared.statusTint dto.Status
    member _.StatusEdge = CommunityShared.statusEdge dto.Status
    member _.RouteLabel = CommunityShared.routeLabel dto.Route dto.Api dto.Arch
    member _.RouteAccent = CommunityShared.routeAccent dto.Route
    member _.RouteTint = CommunityShared.routeTint dto.Route
    member _.RouteEdge = CommunityShared.routeEdge dto.Route
    member _.TargetLabel = if dto.Target = "title" then "TITLE" else "EXECUTABLE"

    member _.HasNeural = dto.Neural
    member _.HasOverlay = dto.Overlay

    /// The hardware chips only exist when the poster chose to attach them.
    member _.Gpu = dto.Gpu
    member _.Driver = dto.Driver
    member _.Cpu = dto.Cpu
    member _.Os = dto.Os
    member _.AppVersion = dto.Version
    member _.HasGpu = not (String.IsNullOrWhiteSpace(dto.Gpu))
    member _.HasDriver = not (String.IsNullOrWhiteSpace(dto.Driver))
    member _.HasCpu = not (String.IsNullOrWhiteSpace(dto.Cpu))
    member _.HasOs = not (String.IsNullOrWhiteSpace(dto.Os))
    member _.Ram = dto.Ram
    member _.HasRam = not (String.IsNullOrWhiteSpace(dto.Ram))
    member _.IsVerified = CommunityShared.isVerified dto.Author
    member _.HasVersion = not (String.IsNullOrWhiteSpace(dto.Version))

    member _.Emoji1 = CommunityApi.reactionEmoji.[0]
    member _.Emoji2 = CommunityApi.reactionEmoji.[1]
    member _.Emoji3 = CommunityApi.reactionEmoji.[2]
    member _.Emoji4 = CommunityApi.reactionEmoji.[3]
    member _.Emoji5 = CommunityApi.reactionEmoji.[4]

    member private _.Count(i: int) =
        if isNull (box counts) || counts.Length <= i then ""
        elif counts.[i] <= 0 then ""
        else string counts.[i]

    member this.Count1 = this.Count(0)
    member this.Count2 = this.Count(1)
    member this.Count3 = this.Count(2)
    member this.Count4 = this.Count(3)
    member this.Count5 = this.Count(4)

    /// Applied locally the moment the server confirms, so the number moves
    /// under the finger instead of after the next refresh.
    member this.ApplyReaction(slot: int, on: bool) =
        let i = slot - 1
        if not (isNull (box counts)) && i >= 0 && i < counts.Length then
            counts.[i] <- max 0 (counts.[i] + (if on then 1 else -1))
            this.RaisePropertyChanged("Count" + string slot)

    member _.Comments = comments

    /// Kept as a field, not read from the DTO: a reply has to move the number
    /// under the button straight away, not on the next fetch.
    member _.CommentCount = commentCount

    member _.CommentCountText =
        if commentCount = 1 then "1 comment" else sprintf "%d comments" commentCount

    member this.SetCommentCount(n: int) =
        if commentCount <> n then
            commentCount <- n
            this.RaisePropertyChanged("CommentCount")
            this.RaisePropertyChanged("CommentCountText")

    member this.IsCommentsOpen
        with get () = isCommentsOpen
        and set value =
            if this.SetProperty(&isCommentsOpen, value) then
                this.RaisePropertyChanged("IsCommentsOpen")

    member this.IsLoadingComments
        with get () = isLoadingComments
        and set value = this.SetProperty(&isLoadingComments, value) |> ignore

    member this.ReplyText
        with get () = replyText
        and set value =
            if this.SetProperty(&replyText, value) then
                this.RaisePropertyChanged("CanReply")

    member _.CanReply = replyText.Trim().Length >= 2


/// One card in the community grid.
type CommunityGameViewModel(dto: CommunityApi.GameDto) =
    inherit ViewModelBase()

    let mutable cover: Bitmap option = CommunityShared.cachedCover dto.Cover
    let mutable coverStarted = cover.IsSome

    member _.Id = dto.Id
    member _.Title = dto.Title

    /// The row this card was built from, so a page already held can be put
    /// back in the cache without asking the server for it again.
    member _.Dto = dto

    /// Stands in for a cover Steam had nothing for.
    member _.Initial =
        if String.IsNullOrWhiteSpace(dto.Title) then "?" else dto.Title.Substring(0, 1).ToUpperInvariant()

    /// Fetched off the UI thread, always.
    ///
    /// This used to download and decode inside the getter, which the grid calls
    /// once per card while it is laying out - forty blocking HTTP requests on
    /// the UI thread, which is exactly what made the section freeze on the way
    /// in. Now the card renders immediately with its letter and swaps the
    /// artwork in when it arrives.
    member private this.Begin() =
        if not coverStarted then
            coverStarted <- true

            Task.Run(fun () ->
                let loaded = CommunityShared.loadCover dto.Cover

                if loaded.IsSome then
                    CommunityShared.ui (fun () ->
                        cover <- loaded
                        this.RaisePropertyChanged("Cover")
                        this.RaisePropertyChanged("HasCover")))
            |> ignore

    member this.Cover =
        this.Begin()

        match cover with
        | Some b -> b
        | None -> null

    member this.HasCover =
        this.Begin()
        cover.IsSome

    member _.VerdictText = CommunityShared.statusText dto.Verdict
    member _.VerdictAccent = CommunityShared.statusAccent dto.Verdict
    member _.VerdictTint = CommunityShared.statusTint dto.Verdict
    member _.VerdictEdge = CommunityShared.statusEdge dto.Verdict
    member _.Working = dto.Working
    member _.Mixed = dto.Mixed
    member _.Broken = dto.Broken
    member _.ReportsText = if dto.Reports = 1 then "1 report" else sprintf "%d reports" dto.Reports
    member _.CommentsText = if dto.Comments = 1 then "1 comment" else sprintf "%d comments" dto.Comments


/// The section itself.
type CommunityViewModel() =
    inherit ViewModelBase()

    let games = ObservableCollection<CommunityGameViewModel>()
    let reports = ObservableCollection<CommunityReportViewModel>()

    let mutable displayName = ""
    let mutable nameDraft = ""
    let mutable statusMessage = ""
    let mutable isBusy = false
    let mutable isLoaded = false

    let mutable routeFilter = ""
    let mutable resultFilter = ""
    let mutable query = ""
    let mutable totalGames = 0

    /// Twenty at a time. The whole list arriving at once is what made entering
    /// the section expensive; the rest follows as the user scrolls.
    let pageSize = 20
    let mutable hasMore = false
    let mutable isLoadingMore = false

    /// Where the next page starts, exactly as the server handed it back. ""
    /// means the list is complete.
    let mutable nextCursor = ""

    /// The cursor that goes with each cached answer, so a grid restored from
    /// memory can still carry on scrolling instead of stopping at its end.
    let cursorByKey = Generic.Dictionary<string, string>()

    /// "recent", "reports" or "title".
    let mutable sortOrder = "recent"

    /// Every id on screen. A game can only be added once, whatever the server
    /// returns - two pages overlapping is the classic way a duplicate appears.
    let shownIds = Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)

    // ---- the sheet -------------------------------------------------------
    let mutable isSheetOpen = false
    let mutable sheetTitle = ""
    let mutable sheetGameId = ""
    let mutable sheetRouteFilter = ""

    // ---- the composer ----------------------------------------------------
    let mutable isComposerOpen = false
    let mutable composeTitle = ""
    let mutable composeSteamId = ""
    let mutable composeStatus = "working"
    let mutable composeRoute = "optiscaler"
    let mutable composeApi = "dx12"
    let mutable composeArch = "64"
    let mutable composeNeural = false
    let mutable composeOverlay = false
    let mutable composeTarget = "executable"
    let mutable composeBody = ""
    let mutable specs: SystemSpecs.Specs option = None
    let mutable isDetectingSpecs = false

    let routeChips = ObservableCollection<RouteChipViewModel>()

    let ui = CommunityShared.ui

    /// Every call goes the same way: flip the spinner, run off the UI thread,
    /// come back with either a message or the new state. A member rather than a
    /// `let` so nothing captures `this` before the object is built.
    member private this.Run(work: unit -> Result<unit, string>) =
        if not isBusy then
            isBusy <- true
            this.RaisePropertyChanged("IsBusy")
            this.RaisePropertyChanged("IsIdle")
            this.RaisePropertyChanged("IsFirstLoad")
            this.RaisePropertyChanged("ShowNothingFound")

            Task.Run(fun () ->
                let outcome =
                    try
                        work ()
                    with ex ->
                        Error ex.Message

                ui (fun () ->
                    isBusy <- false
                    this.RaisePropertyChanged("IsBusy")
                    this.RaisePropertyChanged("IsIdle")
                    this.RaisePropertyChanged("IsFirstLoad")
                    this.RaisePropertyChanged("ShowNothingFound")

                    match outcome with
                    | Ok() -> ()
                    | Error message ->
                        statusMessage <- message
                        this.RaisePropertyChanged("StatusMessage")
                        this.RaisePropertyChanged("HasStatusMessage")))
            |> ignore

    // ======================================================================
    // IDENTITY
    // ======================================================================
    member _.DisplayName = displayName
    member _.IsNamed = not (String.IsNullOrWhiteSpace(displayName))
    member _.IsAnonymous = String.IsNullOrWhiteSpace(displayName)

    member this.NameDraft
        with get () = nameDraft
        and set value =
            if this.SetProperty(&nameDraft, value) then
                this.RaisePropertyChanged("CanClaimName")

    member _.CanClaimName = nameDraft.Trim().Length >= 3

    /// Called once when the section opens. Asks the server which name this
    /// device already holds so a returning user never sees the gate again.
    member this.ClaimName() =
        let wanted = nameDraft.Trim()
        if wanted.Length >= 3 then
            this.Run(fun () ->
                match CommunityApi.claimName wanted with
                | Error e -> Error e
                | Ok confirmed ->
                    ui (fun () ->
                        displayName <- confirmed
                        statusMessage <- ""
                        this.RaisePropertyChanged("DisplayName")
                        this.RaisePropertyChanged("IsNamed")
                        this.RaisePropertyChanged("IsAnonymous")
                        this.RaisePropertyChanged("HasStatusMessage")
                        this.RaisePropertyChanged("StatusMessage"))

                    Ok())

    // ======================================================================
    // THE GRID
    // ======================================================================
    member _.Games = games
    member _.HasGames = games.Count > 0
    member _.IsEmpty = games.Count = 0
    member _.TotalText = if totalGames = 1 then "1 game" else sprintf "%d games" totalGames

    member _.IsBusy = isBusy
    member _.IsIdle = not isBusy

    /// An empty grid means two different things, and they must not look alike:
    /// still fetching, or nobody has posted about anything matching.
    member _.IsFirstLoad = isBusy && games.Count = 0
    member _.ShowNothingFound = not isBusy && games.Count = 0
    member _.StatusMessage = statusMessage
    member _.HasStatusMessage = not (String.IsNullOrWhiteSpace(statusMessage))

    member _.RouteFilter = routeFilter
    member _.ResultFilter = resultFilter

    // ---------------------------------------------------------------------
    // The toolbar. Three small dropdowns replace the two long rows of nine
    // buttons - same choices, a third of the width, plus a sort order.
    // ---------------------------------------------------------------------
    member _.RouteOptions = CommunityFilters.routeLabels
    member _.ResultOptions = CommunityFilters.resultLabels
    member _.SortOptions = CommunityFilters.sortLabels

    // The dropdowns show translated words (`Loc.CommunityRouteOptions` and
    // friends) and bind to these indices. A language switch refills them and
    // they report -1 for a moment - not a user choice, so it is ignored rather
    // than read as "All" and a reload.
    member this.SelectedRouteIndex
        with get () = max 0 (Array.IndexOf(CommunityFilters.routeKeys, routeFilter))
        and set (i: int) =
            if i >= 0 then
                let key = CommunityFilters.keyAt CommunityFilters.routeKeys i
                if routeFilter <> key then
                    routeFilter <- key
                    this.RaiseFilters()
                    this.LoadGames()

    member this.SelectedResultIndex
        with get () = max 0 (Array.IndexOf(CommunityFilters.resultKeys, resultFilter))
        and set (i: int) =
            if i >= 0 then
                let key = CommunityFilters.keyAt CommunityFilters.resultKeys i
                if resultFilter <> key then
                    resultFilter <- key
                    this.RaiseFilters()
                    this.LoadGames()

    member this.SelectedSortIndex
        with get () = max 0 (Array.IndexOf(CommunityFilters.sortKeys, sortOrder))
        and set (i: int) =
            if i >= 0 then
                let key = CommunityFilters.keyAt CommunityFilters.sortKeys i
                if sortOrder <> (if key = "" then "recent" else key) then
                    sortOrder <- (if key = "" then "recent" else key)
                    this.RaiseFilters()
                    this.LoadGames()

    /// After a language switch the dropdowns hold new words; this tells them
    /// again which entry is selected so none of them comes back blank.
    member this.RelabelFilters() =
        for name in [ "SelectedRouteIndex"; "SelectedResultIndex"; "SelectedSortIndex" ] do
            this.RaisePropertyChanged(name)

    /// What the search box is filtering by, shown as a chip with its own ✕ so
    /// it is never unclear why the grid looks short.
    member _.QueryText = query
    member _.HasQuery = not (String.IsNullOrWhiteSpace(query))

    member _.HasActiveFilters = routeFilter <> "" || resultFilter <> "" || sortOrder <> "recent"

    member private this.RaiseFilters() =
        for name in
            [ "SelectedRouteIndex"; "SelectedResultIndex"; "SelectedSortIndex"
              "HasActiveFilters"; "QueryText"; "HasQuery" ] do
            this.RaisePropertyChanged(name)

    /// Everything back to "all games, newest first". The search box is cleared
    /// by the window, which owns it.
    member this.ClearFilters() =
        if routeFilter <> "" || resultFilter <> "" || sortOrder <> "recent" then
            routeFilter <- ""
            resultFilter <- ""
            sortOrder <- "recent"
            this.RaiseFilters()
            this.LoadGames()

    /// The window's one search box drives this while the section is open.
    member this.ApplyQuery(text: string) =
        let trimmed = if isNull text then "" else text.Trim()
        if query <> trimmed then
            query <- trimmed
            this.RaiseFilters()
            if isLoaded then this.LoadGames()

    /// The filters and the sort together identify one answer, so they are the key.
    member private _.GridKey = String.Join("", [| query; routeFilter; resultFilter; sortOrder |])

    /// Appends a page. Ids already on screen are skipped, so a game can never
    /// be listed twice however the pages line up.
    member private this.AddPage(list: CommunityApi.GameDto[], total: int, next: string, reset: bool) =
        if reset then
            games.Clear()
            shownIds.Clear()

        for g in list do
            if not (isNull g.Id) && shownIds.Add(g.Id) then
                games.Add(CommunityGameViewModel(g))

        totalGames <- total

        // The server says whether there is more: a cursor for the next page,
        // or nothing. Counting rows here would be guessing.
        nextCursor <- next
        hasMore <- next <> ""
        statusMessage <- ""

        for name in
            [ "HasGames"; "IsEmpty"; "TotalText"; "StatusMessage"; "HasStatusMessage"
              "IsFirstLoad"; "ShowNothingFound"; "HasMore"; "IsLoadingMore" ] do
            this.RaisePropertyChanged(name)

    /// Fills the grid, from memory when the same filters were asked for in the
    /// last few minutes. Returns true when it answered without going out, which
    /// is what makes coming back to the section instant.
    member private this.ServeFromCache() =
        let key = this.GridKey

        match CommunityShared.cachedList key with
        | Some(list, total) ->
            let next =
                match cursorByKey.TryGetValue(key) with
                | true, c -> c
                | _ -> ""

            ui (fun () -> this.AddPage(list, total, next, true))
            true
        | None -> false

    member private this.Remember(key: string, list: CommunityApi.GameDto[], total: int, next: string) =
        CommunityShared.rememberList key list total
        cursorByKey.[key] <- next

    member private this.FetchGames() =
        let key = this.GridKey

        this.Run(fun () ->
            match CommunityApi.listGamesPage query routeFilter resultFilter sortOrder "" pageSize with
            | Error e -> Error e
            | Ok(list, total, next) ->
                this.Remember(key, list, total, next)
                ui (fun () -> this.AddPage(list, total, next, true))
                Ok())

    /// The Refresh button. Always goes out - that is the whole point of it.
    member this.Refresh() =
        CommunityShared.dropCaches ()
        cursorByKey.Clear()
        this.FetchGames()

    member this.LoadGames() =
        if not (this.ServeFromCache()) then this.FetchGames()

    member _.HasMore = hasMore
    member _.IsLoadingMore = isLoadingMore

    /// The next page, asked for when the grid is scrolled near its end.
    ///
    /// Deliberately not through `Run`: the spinner in the header means "the
    /// list is being replaced", and this only adds to the bottom of it.
    member this.LoadMore() =
        if hasMore && not isLoadingMore && not isBusy && nextCursor <> "" then
            isLoadingMore <- true
            this.RaisePropertyChanged("IsLoadingMore")

            let key = this.GridKey
            let cursor = nextCursor

            Task.Run(fun () ->
                let outcome =
                    try
                        CommunityApi.listGamesPage query routeFilter resultFilter sortOrder cursor pageSize
                    with ex ->
                        Error ex.Message

                ui (fun () ->
                    isLoadingMore <- false

                    match outcome with
                    | Ok(list, total, next) ->
                        this.AddPage(list, total, next, false)

                        // Everything held so far, so coming back to the section
                        // restores the whole scroll rather than the first page.
                        let held = games |> Seq.map (fun g -> g.Dto) |> Seq.toArray
                        this.Remember(key, held, total, next)
                    | Error _ ->
                        // A page that fails is not worth a banner: the grid
                        // stops growing and scrolling again retries it.
                        hasMore <- false
                        this.RaisePropertyChanged("HasMore")

                    this.RaisePropertyChanged("IsLoadingMore")))
            |> ignore

    /// First entry into the section: find out who this device is, then load.
    /// Coming back later re-enters through `LoadGames`, which normally has the
    /// answer already.
    member this.EnsureLoaded() =
        if not isLoaded then
            isLoaded <- true
            let key = this.GridKey

            this.Run(fun () ->
                let name = CommunityApi.getMyName () |> Result.defaultValue ""

                ui (fun () ->
                    displayName <- name
                    this.RaisePropertyChanged("DisplayName")
                    this.RaisePropertyChanged("IsNamed")
                    this.RaisePropertyChanged("IsAnonymous"))

                match CommunityApi.listGamesPage query routeFilter resultFilter sortOrder "" pageSize with
                | Error e -> Error e
                | Ok(list, total, next) ->
                    this.Remember(key, list, total, next)
                    ui (fun () -> this.AddPage(list, total, next, true))
                    Ok())
        else
            this.LoadGames()

    // ======================================================================
    // THE SHEET - one game's reports
    // ======================================================================
    member _.IsSheetOpen = isSheetOpen
    member _.SheetTitle = sheetTitle
    member _.Reports = reports
    member _.HasReports = reports.Count > 0
    member _.SheetRouteFilter = sheetRouteFilter

    member _.RouteChips = routeChips
    member _.HasRouteChips = routeChips.Count > 0

    member this.OpenGame(game: CommunityGameViewModel) =
        sheetGameId <- game.Id
        sheetTitle <- game.Title
        sheetRouteFilter <- ""
        isSheetOpen <- true
        reports.Clear()

        this.RaisePropertyChanged("IsSheetOpen")
        this.RaisePropertyChanged("SheetTitle")
        this.RaisePropertyChanged("HasReports")
        this.LoadReports()

    member this.CloseSheet() =
        isSheetOpen <- false
        reports.Clear()
        this.RaisePropertyChanged("IsSheetOpen")
        this.RaisePropertyChanged("HasReports")

    member this.SetSheetRoute(route: string) =
        if sheetRouteFilter <> route then
            sheetRouteFilter <- route
            this.RaisePropertyChanged("SheetRouteFilter")
            this.LoadReports()

    member private this.ShowFeed(list: CommunityApi.ReportDto[], stats: CommunityApi.RouteStatDto[]) =
        // The chips are rebuilt only when the whole game is reloaded, never
        // when the route filter narrows the feed - otherwise picking a route
        // would erase the other routes.
        if stats.Length > 0 || routeChips.Count = 0 then
            routeChips.Clear()

            for s in stats do
                routeChips.Add(RouteChipViewModel(s))

        reports.Clear()

        for r in list do
            reports.Add(CommunityReportViewModel(r))

        this.RaisePropertyChanged("HasRouteChips")
        this.RaisePropertyChanged("HasReports")

    /// Opening the same game twice, or flipping a route chip back, is answered
    /// from memory - the sheet appears filled instead of blank-then-populated.
    member this.LoadReports() =
        let id = sheetGameId
        let key = id + "" + sheetRouteFilter

        match CommunityShared.cachedFeed key with
        | Some(list, stats) -> this.ShowFeed(list, stats)
        | None ->
            this.Run(fun () ->
                // One request: the Worker puts the route chips in the same
                // answer as the reports whenever no route is picked. With a
                // route picked no chips come back and the ones showing stay.
                match CommunityApi.listFeed id sheetRouteFilter with
                | Error e -> Error e
                | Ok(list, stats) ->
                    CommunityShared.rememberFeed key list stats
                    ui (fun () -> this.ShowFeed(list, stats))
                    Ok())

    // ======================================================================
    // COMMENTS AND REACTIONS
    // ======================================================================
    member this.ToggleComments(report: CommunityReportViewModel) =
        if report.IsCommentsOpen then report.IsCommentsOpen <- false
        else
            report.IsCommentsOpen <- true

            if report.Comments.Count = 0 then
                report.IsLoadingComments <- true

                Task.Run(fun () ->
                    // Keyed on the reply count, so the edge copy is reused until
                    // someone actually replies - and never served stale after.
                    let loaded = CommunityApi.listCommentsFresh report.Id report.CommentCount

                    ui (fun () ->
                        report.IsLoadingComments <- false

                        match loaded with
                        | Ok list ->
                            report.Comments.Clear()
                            for c in list do
                                report.Comments.Add(CommunityCommentViewModel(c))
                        | Error _ -> ()))
                |> ignore

    member this.SendReply(report: CommunityReportViewModel) =
        let text = report.ReplyText.Trim()

        if text.Length >= 2 && this.IsNamed then
            this.Run(fun () ->
                match CommunityApi.postComment report.Id text with
                | Error e -> Error e
                | Ok() ->
                    // What is held in memory no longer matches the server.
                    CommunityShared.dropCaches ()
                    let refreshed = CommunityApi.listCommentsFresh report.Id (report.CommentCount + 1)

                    ui (fun () ->
                        report.ReplyText <- ""

                        match refreshed with
                        | Ok list ->
                            report.Comments.Clear()
                            for c in list do
                                report.Comments.Add(CommunityCommentViewModel(c))

                            // The count on the button comes from the reply list
                            // that just came back, so it is right immediately
                            // rather than one refresh behind.
                            report.SetCommentCount(list.Length)
                        | Error _ -> report.SetCommentCount(report.CommentCount + 1))

                    Ok())

    member this.React(report: CommunityReportViewModel, slot: int) =
        if this.IsNamed then
            Task.Run(fun () ->
                match CommunityApi.toggleReaction report.Id slot with
                | Ok on ->
                    CommunityShared.dropCaches ()
                    ui (fun () -> report.ApplyReaction(slot, on))
                | Error _ -> ())
            |> ignore

    // ======================================================================
    // THE COMPOSER
    // ======================================================================
    member _.IsComposerOpen = isComposerOpen
    member _.ComposeTitle = composeTitle

    member this.ComposeBody
        with get () = composeBody
        and set value = this.SetProperty(&composeBody, value) |> ignore

    member _.IsStatusWorking = composeStatus = "working"
    member _.IsStatusMixed = composeStatus = "mixed"
    member _.IsStatusBroken = composeStatus = "broken"
    member _.ComposeRouteLabel = CommunityShared.routeLabel composeRoute composeApi composeArch
    member _.ComposeTargetLabel = if composeTarget = "title" then "TITLE" else "EXECUTABLE"

    member _.SpecsGpu = specs |> Option.map (fun s -> s.Gpu) |> Option.defaultValue ""
    member _.SpecsDriver = specs |> Option.map (fun s -> s.Driver) |> Option.defaultValue ""
    member _.SpecsCpu = specs |> Option.map (fun s -> s.Cpu) |> Option.defaultValue ""
    member _.SpecsOs = specs |> Option.map (fun s -> s.Os) |> Option.defaultValue ""
    member _.SpecsRam = specs |> Option.map (fun s -> s.Ram) |> Option.defaultValue ""
    member _.HasSpecs = specs.IsSome
    member _.IsDetectingSpecs = isDetectingSpecs

    /// Nothing about the machine is read until this is pressed, and the result
    /// is shown before it is ever sent - attaching it stays the user's call.
    member this.DetectSpecs() =
        if not isDetectingSpecs then
            isDetectingSpecs <- true
            this.RaisePropertyChanged("IsDetectingSpecs")

            Task.Run(fun () ->
                let found = SystemSpecs.detect ()

                ui (fun () ->
                    specs <- Some found
                    isDetectingSpecs <- false

                    for name in [ "SpecsGpu"; "SpecsDriver"; "SpecsCpu"; "SpecsOs"; "SpecsRam"; "HasSpecs"; "IsDetectingSpecs" ] do
                        this.RaisePropertyChanged(name)))
            |> ignore

    member this.ClearSpecs() =
        specs <- None
        for name in [ "SpecsGpu"; "SpecsDriver"; "SpecsCpu"; "SpecsOs"; "SpecsRam"; "HasSpecs" ] do
            this.RaisePropertyChanged(name)

    member this.SetComposeStatus(value: string) =
        composeStatus <- value
        for name in [ "IsStatusWorking"; "IsStatusMixed"; "IsStatusBroken" ] do
            this.RaisePropertyChanged(name)

    /// Opened from a game the user owns. The route, API, bit-width and add-ons
    /// come straight off that game's install manifest, so the post says exactly
    /// how it was installed without the user retyping any of it.
    member this.OpenComposer
        (
            game: GameItem,
            overlayOn: bool,
            pickedRoute: string,
            pickedApi: string,
            pickedArch: string,
            pickedNeural: bool
        ) =
        composeTitle <- game.Title
        composeSteamId <-
            if not (isNull game.AppId) && game.AppId.StartsWith("steam_", StringComparison.OrdinalIgnoreCase) then
                game.AppId.Substring(6)
            else
                ""

        // A game this app installed reports what the manifest recorded. One it
        // has not touched reports what the sheet is showing, which is what the
        // user is about to install or already did by hand - never a guess.
        let installedRoute, installedArch = ModInstaller.installedRouteAndArch game
        let hasManifest = not (String.IsNullOrWhiteSpace(installedRoute))

        composeRoute <- (if hasManifest then installedRoute.ToLowerInvariant() else pickedRoute)
        composeArch <- (if hasManifest then installedArch else pickedArch)
        composeApi <- (if hasManifest then ModInstaller.installedOptiApi game else pickedApi)
        composeNeural <- (if hasManifest then ModInstaller.installedNeuralAddon game else pickedNeural)
        composeOverlay <- overlayOn
        composeTarget <- (if String.IsNullOrWhiteSpace(game.TargetExecutablePath) then "title" else "executable")
        composeStatus <- "working"
        composeBody <- ""
        specs <- None
        isComposerOpen <- true
        statusMessage <- ""

        for name in
            [ "IsComposerOpen"; "ComposeTitle"; "ComposeRouteLabel"; "ComposeTargetLabel"; "ComposeBody"
              "IsStatusWorking"; "IsStatusMixed"; "IsStatusBroken"; "HasSpecs"; "StatusMessage"; "HasStatusMessage" ] do
            this.RaisePropertyChanged(name)

    member this.CloseComposer() =
        isComposerOpen <- false
        this.RaisePropertyChanged("IsComposerOpen")

    member this.SubmitReport() =
        if this.IsNamed && not (String.IsNullOrWhiteSpace(composeTitle)) then
            let draft: CommunityApi.ReportDraft =
                { Title = composeTitle
                  SteamAppId = composeSteamId
                  Status = composeStatus
                  Route = composeRoute
                  Api = (if composeRoute = "optiscaler" then composeApi else "")
                  Arch = composeArch
                  Neural = composeNeural
                  Overlay = composeOverlay
                  Target = composeTarget
                  Body = composeBody
                  Specs = specs }

            this.Run(fun () ->
                match CommunityApi.postReport draft with
                | Error e -> Error e
                | Ok() ->
                    // The user's own post must never be missing from the list
                    // they land back on, so nothing held is trusted after this.
                    CommunityShared.dropCaches ()

                    ui (fun () ->
                        // A short bright sparkle: the result is out.
                        UiSounds.published ()
                        isComposerOpen <- false
                        this.RaisePropertyChanged("IsComposerOpen"))

                    // Back to the first page: the new post is the most recently
                    // updated game, so it is the one at the top.
                    cursorByKey.Clear()
                    let key = this.GridKey

                    match CommunityApi.listGamesPage query routeFilter resultFilter sortOrder "" pageSize with
                    | Error e -> Error e
                    | Ok(list, total, next) ->
                        this.Remember(key, list, total, next)
                        ui (fun () -> this.AddPage(list, total, next, true))
                        Ok())

    member _.TutorialsUrl = CommunityApi.TutorialsUrl
