namespace DLSS_5_MANAGER.Services

open System
open System.Net.Http
open System.Text.Json
open System.Text.RegularExpressions

/// Compares the running build against the published GitHub releases and points
/// the user at the official download page when a newer version exists.
module UpdateChecker =

    [<Literal>]
    let CurrentVersion = "1.1.9"

    [<Literal>]
    let ReleasesApiUrl = "https://api.github.com/repos/NODIX-TECH/DLSS-5-MANAGER/releases"

    [<Literal>]
    let ReleasesPageUrl = "https://github.com/NODIX-TECH/DLSS-5-MANAGER/releases"

    [<Literal>]
    let DownloadPageUrl = "https://numidiastudios.com/#dlss-5-manager"

    type UpdateResult =
        { HasUpdate: bool
          LatestVersion: string
          Message: string }

    let private client =
        let c = new HttpClient()
        c.Timeout <- TimeSpan.FromSeconds(12.0)
        c.DefaultRequestHeaders.UserAgent.ParseAdd("DLSS5Manager-Updater/1.0")
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json")
        c

    /// "v1.2.3" / "1.2.3-beta" -> [1; 2; 3]
    let private parseVersion (raw: string) : int list =
        if String.IsNullOrWhiteSpace(raw) then
            []
        else
            let cleaned = Regex.Match(raw.Trim().TrimStart('v', 'V'), @"^\d+(\.\d+)*")

            if not cleaned.Success then
                []
            else
                cleaned.Value.Split('.')
                |> Array.map (fun p ->
                    match Int32.TryParse(p) with
                    | true, v -> v
                    | _ -> 0)
                |> Array.toList

    /// Positive when `a` is newer than `b`.
    let private compareVersions (a: string) (b: string) : int =
        let va = parseVersion a
        let vb = parseVersion b
        let length = max va.Length vb.Length

        let pad (list: int list) =
            list @ List.replicate (length - list.Length) 0

        compare (pad va) (pad vb)

    let private readTag (element: JsonElement) : string * bool =
        let getString (name: string) : string =
            match element.TryGetProperty(name) with
            | true, (v: JsonElement) when v.ValueKind = JsonValueKind.String -> v.GetString()
            | _ -> ""

        let getBool (name: string) : bool =
            match element.TryGetProperty(name) with
            | true, (v: JsonElement) -> v.ValueKind = JsonValueKind.True
            | _ -> false

        let tag =
            let t = getString "tag_name"
            if String.IsNullOrWhiteSpace(t) then getString "name" else t

        (tag, getBool "draft" || getBool "prerelease")

    /// Newest published (non-draft, non-prerelease) tag, or "" when none exist.
    let private fetchLatestTag () : Async<string> =
        async {
            let! json = client.GetStringAsync(ReleasesApiUrl) |> Async.AwaitTask
            use document = JsonDocument.Parse(json)
            let root = document.RootElement

            if root.ValueKind <> JsonValueKind.Array then
                return ""
            else
                let mutable best = ""

                for item in root.EnumerateArray() do
                    let (tag, isPreview) = readTag item

                    if not isPreview && not (String.IsNullOrWhiteSpace(tag)) then
                        if best = "" || compareVersions tag best > 0 then best <- tag

                return best
        }

    let check () : Async<UpdateResult> =
        async {
            try
                let! latest = fetchLatestTag ()

                if String.IsNullOrWhiteSpace(latest) then
                    return
                        { HasUpdate = false
                          LatestVersion = CurrentVersion
                          Message = sprintf "You are on the latest version (v%s)." CurrentVersion }
                elif compareVersions latest CurrentVersion > 0 then
                    return
                        { HasUpdate = true
                          LatestVersion = latest.TrimStart('v', 'V')
                          Message = sprintf "Version %s is available." (latest.TrimStart('v', 'V')) }
                else
                    return
                        { HasUpdate = false
                          LatestVersion = CurrentVersion
                          Message = sprintf "You are on the latest version (v%s)." CurrentVersion }
            with ex ->
                return
                    { HasUpdate = false
                      LatestVersion = CurrentVersion
                      Message = "Could not reach the update server. " + ex.Message }
        }
