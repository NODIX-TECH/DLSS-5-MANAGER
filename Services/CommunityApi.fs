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
    let BaseUrl = "your-worker"

    /// Must match `wrangler secret put APP_SECRET` on the Worker.
    [<Literal>]
    let AppSecret = "lol i can not give this"

    [<Literal>]
    let TutorialsUrl = "https://dlss5manager.numidiastudios.com/tutorials"

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

    /// Returns the page and the server's own total, which is what the header
    /// counts - the page stops at 100 and would otherwise undercount.
    let listGames (query: string) (route: string) (result: string) : Result<GameDto[] * int, string> =
        let parts =
            [ if not (String.IsNullOrWhiteSpace(query)) then yield "q=" + Uri.EscapeDataString(query.Trim())
              if not (String.IsNullOrWhiteSpace(route)) then yield "route=" + Uri.EscapeDataString(route)
              if not (String.IsNullOrWhiteSpace(result)) then yield "result=" + Uri.EscapeDataString(result)
              yield "limit=100" ]

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
