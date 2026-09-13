namespace DLSS_5_MANAGER.Services

open System
open System.Net.Http
open System.Security.Cryptography
open System.Threading.Tasks
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open Microsoft.Win32

/// Talks to the community Worker in front of Cloudflare D1.
///
/// The server accepts a request only when it carries an HMAC signature over the
/// method, the path, a timestamp, the device fingerprint and a hash of the
/// body. That keeps the database to this application: a browser cannot reach it
/// (the Worker returns no CORS headers) and a replayed request dies after five
/// minutes.
///
/// `AppSecret` obviously ships inside the binary, so it is a lock on the front
/// door rather than a vault. The real protection is on the server: one report
/// per device per game, a 24 hour cooldown, and per-device rate limits.
///
/// The fingerprint is a salted SHA-256 of the machine GUID. The raw GUID never
/// leaves the machine and the hash cannot be turned back into it, so a post is
/// tied to a device without identifying one.
module CommunityApi =

    [<Literal>]
    let BaseUrl = "https://dlss5manager-community-api.gtagatgta9.workers.dev"

    /// Must match `wrangler secret put APP_SECRET` on the Worker.
    [<Literal>]
    let AppSecret = "BRO-THIS-NOT-PUBLIC"

    [<Literal>]
    let TutorialsUrl = "https://dlss5manager.app/tutorials"

    /// The five reactions, in the order the server stores them (slot 1..5).
    let reactionEmoji = [| "❤️"; "\U0001F44D"; "\U0001F525"; "\U0001F389"; "\U0001F615" |]

    // -----------------------------------------------------------------------
    // WIRE TYPES
    // -----------------------------------------------------------------------
    [<CLIMutable>]
    type GameDto =
        { [<JsonPropertyName("id")>] Id: string
          [<JsonPropertyName("title")>] Title: string
          [<JsonPropertyName("cover")>] Cover: string
          [<JsonPropertyName("working")>] Working: int
          [<JsonPropertyName("mixed")>] Mixed: int
          [<JsonPropertyName("broken")>] Broken: int
          [<JsonPropertyName("reports")>] Reports: int
          [<JsonPropertyName("comments")>] Comments: int
          [<JsonPropertyName("verdict")>] Verdict: string
          [<JsonPropertyName("updated")>] Updated: int64 }

    [<CLIMutable>]
    type RouteStatDto =
        { [<JsonPropertyName("route")>] Route: string
          [<JsonPropertyName("working")>] Working: int
          [<JsonPropertyName("mixed")>] Mixed: int
          [<JsonPropertyName("broken")>] Broken: int
          [<JsonPropertyName("total")>] Total: int }

    [<CLIMutable>]
    type ReportDto =
        { [<JsonPropertyName("id")>] Id: string
          [<JsonPropertyName("author")>] Author: string
          [<JsonPropertyName("status")>] Status: string
          [<JsonPropertyName("route")>] Route: string
          [<JsonPropertyName("api")>] Api: string
          [<JsonPropertyName("arch")>] Arch: string
          [<JsonPropertyName("neural")>] Neural: bool
          [<JsonPropertyName("overlay")>] Overlay: bool
          [<JsonPropertyName("target")>] Target: string
          [<JsonPropertyName("body")>] Body: string
          [<JsonPropertyName("gpu")>] Gpu: string
          [<JsonPropertyName("driver")>] Driver: string
          [<JsonPropertyName("cpu")>] Cpu: string
          [<JsonPropertyName("os")>] Os: string
          [<JsonPropertyName("ram")>] Ram: string
          [<JsonPropertyName("version")>] Version: string
          [<JsonPropertyName("reactions")>] Reactions: int[]
          [<JsonPropertyName("comments")>] Comments: int
          [<JsonPropertyName("created")>] Created: int64 }

    [<CLIMutable>]
    type CommentDto =
        { [<JsonPropertyName("id")>] Id: string
          [<JsonPropertyName("author")>] Author: string
          [<JsonPropertyName("body")>] Body: string
          [<JsonPropertyName("created")>] Created: int64 }

    [<CLIMutable>]
    type GamesResponse =
        { [<JsonPropertyName("ok")>] Ok: bool
          [<JsonPropertyName("error")>] Error: string
          [<JsonPropertyName("total")>] Total: int
          [<JsonPropertyName("games")>] Games: GameDto[] }

    [<CLIMutable>]
    type GameResponse =
        { [<JsonPropertyName("ok")>] Ok: bool
          [<JsonPropertyName("error")>] Error: string
          [<JsonPropertyName("game")>] Game: GameDto
          [<JsonPropertyName("routes")>] Routes: RouteStatDto[] }

    [<CLIMutable>]
    type ReportsResponse =
        { [<JsonPropertyName("ok")>] Ok: bool
          [<JsonPropertyName("error")>] Error: string
          [<JsonPropertyName("reports")>] Reports: ReportDto[] }

    [<CLIMutable>]
    type CommentsResponse =
        { [<JsonPropertyName("ok")>] Ok: bool
          [<JsonPropertyName("error")>] Error: string
          [<JsonPropertyName("comments")>] Comments: CommentDto[] }

    [<CLIMutable>]
    type SimpleResponse =
        { [<JsonPropertyName("ok")>] Ok: bool
          [<JsonPropertyName("error")>] Error: string
          [<JsonPropertyName("name")>] Name: string
          [<JsonPropertyName("on")>] On: bool }

    /// What the composer sends. Named to match the Worker's field names.
    type ReportDraft =
        { Title: string
          SteamAppId: string
          Status: string
          Route: string
          Api: string
          Arch: string
          Neural: bool
          Overlay: bool
          Target: string
          Body: string
          Specs: SystemSpecs.Specs option }

    // -----------------------------------------------------------------------
    // IDENTITY
    // -----------------------------------------------------------------------
    let private sha256Hex (text: string) =
        use sha = SHA256.Create()
        sha.ComputeHash(Encoding.UTF8.GetBytes(text))
        |> Array.map (fun b -> b.ToString("x2"))
        |> String.concat ""

    let private machineGuid =
        lazy
            (try
                use key =
                    RegistryKey
                        .OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                        .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography")

                match (if isNull key then null else key.GetValue("MachineGuid")) with
                | null -> Environment.MachineName + "|" + Environment.UserName
                | v -> string v
             with _ ->
                 Environment.MachineName + "|" + Environment.UserName)

    /// Stable for the life of the machine, and one-way. The server only ever
    /// sees this hash.
    let fingerprint = lazy (sha256Hex ("dlss5:" + machineGuid.Value))

    // -----------------------------------------------------------------------
    // TRANSPORT
    // -----------------------------------------------------------------------
    let private client =
        lazy
            (let c = new HttpClient()
             c.Timeout <- TimeSpan.FromSeconds(20.0)
             c)

    let private jsonOptions =
        let o = JsonSerializerOptions()
        o.PropertyNameCaseInsensitive <- true
        o

    let private hmacHex (message: string) =
        use mac = new HMACSHA256(Encoding.UTF8.GetBytes(AppSecret))
        mac.ComputeHash(Encoding.UTF8.GetBytes(message))
        |> Array.map (fun b -> b.ToString("x2"))
        |> String.concat ""

    /// Signs and sends one request. Every failure - network, HTTP status, bad
    /// JSON - comes back as `Error message` so the UI has one thing to show.
    let private send<'T> (method: HttpMethod) (pathAndQuery: string) (body: string) : Result<'T, string> =
        try
            let ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            let fp = fingerprint.Value
            let payload =
                String.Join(
                    "\n",
                    [| method.Method
                       pathAndQuery
                       string ts
                       fp
                       sha256Hex body |]
                )

            use req = new HttpRequestMessage(method, BaseUrl + pathAndQuery)
            req.Headers.Add("X-DLSS5-App", "DLSS5MANAGER/" + UpdateChecker.CurrentVersion)
            req.Headers.Add("X-DLSS5-FP", fp)
            req.Headers.Add("X-DLSS5-TS", string ts)
            req.Headers.Add("X-DLSS5-Sig", hmacHex payload)

            if method = HttpMethod.Post then
                req.Content <- new StringContent(body, Encoding.UTF8, "application/json")

            use res = client.Value.Send(req)
            let text = res.Content.ReadAsStringAsync().GetAwaiter().GetResult()

            let parsed =
                try
                    Some(JsonSerializer.Deserialize<'T>(text, jsonOptions))
                with _ ->
                    None

            if res.IsSuccessStatusCode then
                match parsed with
                | Some v -> Ok v
                | None -> Error "Unexpected reply from the server."
            else
                // The Worker always answers with { ok, error }, so surface its
                // own words rather than a status code the user cannot act on.
                let message =
                    try
                        let doc = JsonDocument.Parse(text)
                        match doc.RootElement.TryGetProperty("error") with
                        | true, e -> e.GetString()
                        | _ -> sprintf "Server returned %d." (int res.StatusCode)
                    with _ ->
                        sprintf "Server returned %d." (int res.StatusCode)

                Error message
        with
        | :? TaskCanceledException -> Error "The server did not answer in time."
        | ex -> Error ex.Message

    let private escape (s: string) =
        JsonEncodedText.Encode(if isNull s then "" else s).ToString()

    // -----------------------------------------------------------------------
    // ENDPOINTS
    // -----------------------------------------------------------------------

    /// The display name this device already claimed, or "" for a first visit.
    let getMyName () : Result<string, string> =
        send<SimpleResponse> HttpMethod.Get "/v1/me" ""
        |> Result.map (fun r -> if isNull r.Name then "" else r.Name)

    let claimName (name: string) : Result<string, string> =
        let body = sprintf "{\"name\":\"%s\"}" (escape name)
        send<SimpleResponse> HttpMethod.Post "/v1/profile" body
        |> Result.map (fun r -> if isNull r.Name then name else r.Name)

    /// One page of the grid, plus the server's own total - which is what the
    /// header counts, since a page never holds all of them.
    let listGames
        (query: string)
        (route: string)
        (result: string)
        (offset: int)
        (limit: int)
        : Result<GameDto[] * int, string> =
        let parts =
            [ if not (String.IsNullOrWhiteSpace(query)) then yield "q=" + Uri.EscapeDataString(query.Trim())
              if not (String.IsNullOrWhiteSpace(route)) then yield "route=" + Uri.EscapeDataString(route)
              if not (String.IsNullOrWhiteSpace(result)) then yield "result=" + Uri.EscapeDataString(result)
              yield sprintf "offset=%d" (max 0 offset)
              yield sprintf "limit=%d" (max 1 limit) ]

        send<GamesResponse> HttpMethod.Get ("/v1/games?" + String.Join("&", parts)) ""
        |> Result.map (fun r ->
            let games = if isNull (box r.Games) then [||] else r.Games
            games, max r.Total games.Length)

    let getGame (id: string) : Result<GameDto * RouteStatDto[], string> =
        send<GameResponse> HttpMethod.Get ("/v1/games/" + Uri.EscapeDataString(id)) ""
        |> Result.map (fun r -> r.Game, (if isNull (box r.Routes) then [||] else r.Routes))

    let listReports (gameId: string) (route: string) : Result<ReportDto[], string> =
        let q =
            if String.IsNullOrWhiteSpace(route) then ""
            else "?route=" + Uri.EscapeDataString(route)

        send<ReportsResponse> HttpMethod.Get ("/v1/games/" + Uri.EscapeDataString(gameId) + "/reports" + q) ""
        |> Result.map (fun r -> if isNull (box r.Reports) then [||] else r.Reports)

    let listComments (reportId: string) : Result<CommentDto[], string> =
        send<CommentsResponse> HttpMethod.Get ("/v1/reports/" + Uri.EscapeDataString(reportId) + "/comments") ""
        |> Result.map (fun r -> if isNull (box r.Comments) then [||] else r.Comments)

    /// Hardware is attached only when the user asked for it - `Specs = None`
    /// posts nothing about the machine at all.
    let postReport (draft: ReportDraft) : Result<unit, string> =
        let specs = draft.Specs |> Option.defaultValue SystemSpecs.empty

        let body =
            String.Concat(
                [ "{"
                  sprintf "\"title\":\"%s\"," (escape draft.Title)
                  sprintf "\"steam_appid\":\"%s\"," (escape draft.SteamAppId)
                  sprintf "\"status\":\"%s\"," (escape draft.Status)
                  sprintf "\"route\":\"%s\"," (escape draft.Route)
                  sprintf "\"api\":\"%s\"," (escape draft.Api)
                  sprintf "\"arch\":\"%s\"," (escape draft.Arch)
                  sprintf "\"neural\":%b," draft.Neural
                  sprintf "\"overlay\":%b," draft.Overlay
                  sprintf "\"target\":\"%s\"," (escape draft.Target)
                  sprintf "\"body\":\"%s\"," (escape draft.Body)
                  sprintf "\"gpu\":\"%s\"," (escape specs.Gpu)
                  sprintf "\"driver\":\"%s\"," (escape specs.Driver)
                  sprintf "\"cpu\":\"%s\"," (escape specs.Cpu)
                  sprintf "\"os\":\"%s\"," (escape specs.Os)
                  sprintf "\"ram\":\"%s\"" (escape specs.Ram)
                  "}" ]
            )

        send<SimpleResponse> HttpMethod.Post "/v1/reports" body |> Result.map ignore

    let postComment (reportId: string) (text: string) : Result<unit, string> =
        let body = sprintf "{\"report_id\":\"%s\",\"body\":\"%s\"}" (escape reportId) (escape text)
        send<SimpleResponse> HttpMethod.Post "/v1/comments" body |> Result.map ignore

    /// Tapping the same emoji twice takes the reaction back; the reply says
    /// which way it went so the button can light up without a refetch.
    let toggleReaction (reportId: string) (slot: int) : Result<bool, string> =
        let body = sprintf "{\"report_id\":\"%s\",\"slot\":%d}" (escape reportId) slot
        send<SimpleResponse> HttpMethod.Post "/v1/reactions" body |> Result.map (fun r -> r.On)

    // =======================================================================
    // 1.2.3 - CHEAPER READS
    //
    // The functions above stay for apps still on 1.2.2. These are what 1.2.3
    // calls, and each one exists to take requests or D1 rows off the bill.
    // =======================================================================

    [<CLIMutable>]
    type GamesPageResponse =
        { [<JsonPropertyName("ok")>] Ok: bool
          [<JsonPropertyName("error")>] Error: string
          [<JsonPropertyName("total")>] Total: int
          [<JsonPropertyName("next")>] Next: string
          [<JsonPropertyName("games")>] Games: GameDto[] }

    /// One page of the grid, sought by cursor rather than skipped to by offset.
    ///
    /// `after` is the `next` the previous page handed back, or "" for the first
    /// page. With an offset the database reads every row it skips - page eight
    /// cost 160 rows to return twenty. A cursor seeks straight to the page.
    ///
    /// `sort` is "recent", "reports" or "title".
    let listGamesPage
        (query: string)
        (route: string)
        (result: string)
        (sort: string)
        (after: string)
        (limit: int)
        : Result<GameDto[] * int * string, string> =
        let parts =
            [ if not (String.IsNullOrWhiteSpace(query)) then yield "q=" + Uri.EscapeDataString(query.Trim())
              if not (String.IsNullOrWhiteSpace(route)) then yield "route=" + Uri.EscapeDataString(route)
              if not (String.IsNullOrWhiteSpace(result)) then yield "result=" + Uri.EscapeDataString(result)
              if not (String.IsNullOrWhiteSpace(sort)) then yield "sort=" + Uri.EscapeDataString(sort)
              if not (String.IsNullOrWhiteSpace(after)) then yield "after=" + Uri.EscapeDataString(after)
              yield sprintf "limit=%d" (max 1 limit) ]

        send<GamesPageResponse> HttpMethod.Get ("/v1/games?" + String.Join("&", parts)) ""
        |> Result.map (fun r ->
            let games = if isNull (box r.Games) then [||] else r.Games
            games, max r.Total games.Length, (if isNull r.Next then "" else r.Next))

    [<CLIMutable>]
    type FeedResponse =
        { [<JsonPropertyName("ok")>] Ok: bool
          [<JsonPropertyName("error")>] Error: string
          [<JsonPropertyName("reports")>] Reports: ReportDto[]
          [<JsonPropertyName("routes")>] Routes: RouteStatDto[] }

    /// A game's reports and its per-route tally in one request. Opening a game
    /// used to cost two - `getGame` for the chips, `listReports` for the posts.
    /// The tally only comes back when no route is picked, which is the only
    /// time the chips are rebuilt.
    let listFeed (gameId: string) (route: string) : Result<ReportDto[] * RouteStatDto[], string> =
        let q =
            if String.IsNullOrWhiteSpace(route) then ""
            else "?route=" + Uri.EscapeDataString(route)

        send<FeedResponse> HttpMethod.Get ("/v1/games/" + Uri.EscapeDataString(gameId) + "/reports" + q) ""
        |> Result.map (fun r ->
            (if isNull (box r.Reports) then [||] else r.Reports),
            (if isNull (box r.Routes) then [||] else r.Routes))

    /// Replies under a report. `known` is how many there are as far as this
    /// app knows, and it rides in the URL only so the edge cache sees a new
    /// address after a reply - otherwise the reply just posted would be served
    /// from a copy taken before it existed.
    let listCommentsFresh (reportId: string) (known: int) : Result<CommentDto[], string> =
        send<CommentsResponse> HttpMethod.Get (sprintf "/v1/reports/%s/comments?n=%d" (Uri.EscapeDataString(reportId)) known) ""
        |> Result.map (fun r -> if isNull (box r.Comments) then [||] else r.Comments)

    // =======================================================================
    // PULSE - the chat
    // =======================================================================

    /// One emoji under a message and how many people tapped it.
    [<CLIMutable>]
    type ChatReactionDto =
        { [<JsonPropertyName("e")>] E: string
          [<JsonPropertyName("n")>] N: int }

    /// What a reply answers - a copy made when the reply was sent, so drawing
    /// it never needs the original.
    [<CLIMutable>]
    type ChatReplyDto =
        { [<JsonPropertyName("id")>] Id: int64
          [<JsonPropertyName("author")>] Author: string
          [<JsonPropertyName("body")>] Body: string }

    [<CLIMutable>]
    type ChatMessageDto =
        { [<JsonPropertyName("id")>] Id: int64
          [<JsonPropertyName("author")>] Author: string
          /// A short one-way tag of the sender's device, so this app can tell
          /// its own messages apart without the server ever publishing a
          /// fingerprint. See `pulseTag`.
          [<JsonPropertyName("tag")>] Tag: string
          [<JsonPropertyName("dev")>] Dev: bool
          [<JsonPropertyName("body")>] Body: string
          [<JsonPropertyName("image")>] Image: string
          [<JsonPropertyName("w")>] W: int
          [<JsonPropertyName("h")>] H: int
          [<JsonPropertyName("created")>] Created: int64
          /// null when the message is not a reply.
          [<JsonPropertyName("reply")>] Reply: ChatReplyDto
          [<JsonPropertyName("rx")>] Rx: ChatReactionDto[] }

    /// A message already on screen whose reactions changed.
    [<CLIMutable>]
    type ChatUpdateDto =
        { [<JsonPropertyName("id")>] Id: int64
          [<JsonPropertyName("rx")>] Rx: ChatReactionDto[] }

    [<CLIMutable>]
    type ChatResponse =
        { [<JsonPropertyName("ok")>] Ok: bool
          [<JsonPropertyName("error")>] Error: string
          [<JsonPropertyName("messages")>] Messages: ChatMessageDto[]
          /// Ids removed recently. A poll only asks for what is new, so without
          /// this a deleted message would stay on every screen that had it.
          [<JsonPropertyName("removed")>] Removed: int64[]
          /// True when there is older history than the oldest message sent.
          [<JsonPropertyName("more")>] More: bool
          /// The reaction counter now; sent back on the next poll.
          [<JsonPropertyName("rev")>] Rev: int64
          /// Reaction changes since the rev the poll sent.
          [<JsonPropertyName("updates")>] Updates: ChatUpdateDto[] }

    [<CLIMutable>]
    type ChatReactResponse =
        { [<JsonPropertyName("ok")>] Ok: bool
          [<JsonPropertyName("error")>] Error: string
          /// Whether this device's reaction is now on or off.
          [<JsonPropertyName("on")>] On: bool
          [<JsonPropertyName("rx")>] Rx: ChatReactionDto[] }

    /// The six reactions, in the order they are offered. Must match the
    /// Worker's CHAT_REACTIONS exactly - the heart carries U+FE0F.
    let chatReactions =
        [| "\U0001F44D"; "❤️"; "\U0001F602"; "\U0001F62E"; "\U0001F622"; "\U0001F525" |]

    [<CLIMutable>]
    type ChatPostResponse =
        { [<JsonPropertyName("ok")>] Ok: bool
          [<JsonPropertyName("error")>] Error: string
          [<JsonPropertyName("message")>] Message: ChatMessageDto }

    [<CLIMutable>]
    type ImageUploadResponse =
        { [<JsonPropertyName("ok")>] Ok: bool
          [<JsonPropertyName("error")>] Error: string
          [<JsonPropertyName("key")>] Key: string }

    /// This device's tag, computed the same way the Worker computes it:
    /// SHA-256 of "pulse:" plus the fingerprint, first ten hex digits. It can
    /// be matched against a message but not turned back into the fingerprint.
    let pulseTag = lazy ((sha256Hex ("pulse:" + fingerprint.Value)).Substring(0, 10))

    let private sha256OfBytes (bytes: byte[]) =
        use sha = SHA256.Create()
        sha.ComputeHash(bytes) |> Array.map (fun b -> b.ToString("x2")) |> String.concat ""

    /// The same four signed headers `send` adds, for a body that is not text.
    let private signedRequest (method: HttpMethod) (pathAndQuery: string) (bodyHash: string) =
        let ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        let fp = fingerprint.Value
        let payload = String.Join("\n", [| method.Method; pathAndQuery; string ts; fp; bodyHash |])

        let req = new HttpRequestMessage(method, BaseUrl + pathAndQuery)
        req.Headers.Add("X-DLSS5-App", "DLSS5MANAGER/" + UpdateChecker.CurrentVersion)
        req.Headers.Add("X-DLSS5-FP", fp)
        req.Headers.Add("X-DLSS5-TS", string ts)
        req.Headers.Add("X-DLSS5-Sig", hmacHex payload)
        req

    let private errorOf (res: HttpResponseMessage) (text: string) =
        try
            let doc = JsonDocument.Parse(text)

            match doc.RootElement.TryGetProperty("error") with
            | true, e -> e.GetString()
            | _ -> sprintf "Server returned %d." (int res.StatusCode)
        with _ ->
            sprintf "Server returned %d." (int res.StatusCode)

    /// Messages newer than `afterId`, oldest first (0 asks for the latest
    /// page), plus the reaction changes since `rev`.
    let chatSince (afterId: int64) (rev: int64) : Result<ChatResponse, string> =
        send<ChatResponse> HttpMethod.Get (sprintf "/v1/chat?after=%d&rev=%d" (max 0L afterId) (max 0L rev)) ""

    /// History from before `beforeId`, for scrolling back.
    let chatBefore (beforeId: int64) : Result<ChatResponse, string> =
        send<ChatResponse> HttpMethod.Get (sprintf "/v1/chat?before=%d" (max 0L beforeId)) ""

    /// Taps one reaction on or off. Returns whether it is now on, and the tally.
    let reactChat (id: int64) (emoji: string) : Result<bool * ChatReactionDto[], string> =
        send<ChatReactResponse> HttpMethod.Post "/v1/chat/react" (sprintf "{\"id\":%d,\"emoji\":\"%s\"}" id (escape emoji))
        |> Result.map (fun r -> r.On, (if isNull (box r.Rx) then [||] else r.Rx))

    /// `replyTo` is the id being answered, or 0.
    let postChat (body: string) (imageKey: string) (width: int) (height: int) (replyTo: int64) : Result<ChatMessageDto, string> =
        let payload =
            sprintf
                "{\"body\":\"%s\",\"image\":\"%s\",\"w\":%d,\"h\":%d,\"reply\":%d}"
                (escape body)
                (escape imageKey)
                (max 0 width)
                (max 0 height)
                (max 0L replyTo)

        send<ChatPostResponse> HttpMethod.Post "/v1/chat" payload
        |> Result.bind (fun r ->
            if isNull (box r.Message) then Error "The server did not return the message."
            else Ok r.Message)

    let deleteChat (id: int64) : Result<unit, string> =
        send<SimpleResponse> HttpMethod.Post "/v1/chat/delete" (sprintf "{\"id\":%d}" id) |> Result.map ignore

    /// Sends an already-encoded WebP and gets back the key it is stored under.
    let uploadChatImage (webp: byte[]) : Result<string, string> =
        try
            use req = signedRequest HttpMethod.Post "/v1/chat/image" (sha256OfBytes webp)
            let content = new ByteArrayContent(webp)
            content.Headers.ContentType <- Headers.MediaTypeHeaderValue("image/webp")
            req.Content <- content

            use res = client.Value.Send(req)
            let text = res.Content.ReadAsStringAsync().GetAwaiter().GetResult()

            if res.IsSuccessStatusCode then
                let parsed = JsonSerializer.Deserialize<ImageUploadResponse>(text, jsonOptions)

                if isNull (box parsed) || String.IsNullOrWhiteSpace(parsed.Key) then
                    Error "The server did not return the picture."
                else
                    Ok parsed.Key
            else
                Error(errorOf res text)
        with
        | :? TaskCanceledException -> Error "The upload did not finish in time."
        | ex -> Error ex.Message

    /// The bytes of one chat image. Callers keep a disk copy - an image never
    /// changes under its key, so it is fetched from here once, ever.
    let getChatImage (key: string) : Result<byte[], string> =
        try
            use req = signedRequest HttpMethod.Get ("/v1/chat/image/" + Uri.EscapeDataString(key)) (sha256Hex "")
            use res = client.Value.Send(req)

            if res.IsSuccessStatusCode then
                Ok(res.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult())
            else
                Error(sprintf "Server returned %d." (int res.StatusCode))
        with ex ->
            Error ex.Message


/// What the community grid can be filtered and sorted by, and the words the
/// toolbar shows for each. Kept beside the API because the keys are exactly
/// what the Worker's query string takes.
module CommunityFilters =

    /// "reshade" is all three ReShade routes. The old toolbar sent "dx12" for
    /// its RESHADE button, which silently hid every DX11 and DX9 report.
    let routeKeys = [| ""; "optiscaler"; "reshade"; "emulator"; "amd" |]
    let routeLabels = [| "All routes"; "OptiScaler"; "ReShade"; "Emulator"; "AMD" |]

    let resultKeys = [| ""; "working"; "mixed"; "broken" |]
    let resultLabels = [| "Any result"; "Working"; "Mixed"; "Not working" |]

    let sortKeys = [| "recent"; "reports"; "title" |]
    let sortLabels = [| "Most recent"; "Most reports"; "A to Z" |]

    /// Index to key, or "" for anything out of range - a dropdown with nothing
    /// selected reports -1.
    let keyAt (keys: string[]) (index: int) =
        if index >= 0 && index < keys.Length then keys.[index] else ""
