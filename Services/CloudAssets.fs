namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.Net.Http

/// The parts of the payload that are fetched on first launch instead of riding
/// along inside the installer.
///
/// They are ours and they change on their own schedule - the overlay is rebuilt
/// far more often than the program around it - so keeping them out of the setup
/// keeps the download small and lets a new overlay reach people without a new
/// release. Nothing here is optional: the program checks for all of them before
/// it opens, and asks for whatever is missing.
module CloudAssets =

    [<Literal>]
    let private Origin = "https://pub-fe8d3b5206c14d5d8bb6c6695c8e9578.r2.dev/"

    /// One file on the server, and every place it has to end up. A file with
    /// two targets is downloaded once and written to both.
    type Asset =
        { Name: string
          Targets: string list }

    /// What one button fetches.
    type Bundle =
        { Id: string
          Title: string
          Blurb: string
          Assets: Asset list }

    let private client =
        let c = new HttpClient()
        c.Timeout <- TimeSpan.FromMinutes(5.0)
        c.DefaultRequestHeaders.UserAgent.ParseAdd("DLSS5Manager/" + UpdateChecker.CurrentVersion)
        c

    /// The in-game overlay, both bitnesses. A 32-bit game loads a 32-bit
    /// ReShade, which can only load ".addon32" - that is most DX9 titles.
    let private overlayBundle () =
        let root = ModInstaller.modFilesRoot ()

        { Id = "overlay"
          Title = "Overlay"
          Blurb = "The in-game panel, 64-bit and 32-bit."
          Assets =
            [ { Name = "dlss5-overlay.addon64"
                Targets = [ Path.Combine(root, "dlss5-overlay.addon64") ] }
              { Name = "dlss5-overlay.addon32"
                Targets = [ Path.Combine(root, "dlss5-overlay.addon32") ] } ] }

    /// One file, into both OptiScaler payloads - the plugin folders already
    /// carry its XeFGUnlock.ini, and this is the half that was missing.
    let private xessBundle () =
        let root = ModInstaller.modFilesRoot ()

        let plugins (payload: string) =
            Path.Combine(root, payload, "OptiScaler", "plugins", "XeFGUnlock.asi")

        { Id = "xess-mfg"
          Title = "XESS MFG"
          Blurb = "Frame generation unlock for both OptiScaler payloads."
          Assets =
            [ { Name = "XeFGUnlock.asi"
                Targets =
                  [ plugins "if OptiScaler neural-upstream"
                    plugins "if Optiscaler RTX40" ] } ] }

    let bundles () = [ overlayBundle (); xessBundle () ]

    /// A bundle is there when every one of its files is in every one of its
    /// places. Half of it is not there.
    let isPresent (bundle: Bundle) =
        bundle.Assets
        |> List.forall (fun asset -> asset.Targets |> List.forall File.Exists)

    let allPresent () = bundles () |> List.forall isPresent

    /// How big the whole bundle is, asked before anything is downloaded so the
    /// bar can count against a real number instead of guessing. Zero when the
    /// server will not say, which the caller shows as a sweep.
    let private measure (bundle: Bundle) : Async<int64> =
        async {
            let mutable total = 0L

            for asset in bundle.Assets do
                try
                    use request = new HttpRequestMessage(HttpMethod.Head, Origin + asset.Name)
                    let! response = client.SendAsync(request) |> Async.AwaitTask

                    if response.IsSuccessStatusCode then
                        match response.Content.Headers.ContentLength |> Option.ofNullable with
                        | Some length -> total <- total + length
                        | None -> ()
                with _ ->
                    ()

            return total
        }

    /// Everything that arrives, into a file, counted as it goes. Plain and
    /// synchronous on purpose: this is already running off the UI thread, and
    /// a `use` inside an async block cannot be scoped the way this needs.
    let private drain (stream: Stream) (temp: string) (before: int64) (total: int64)
                      (report: int64 -> int64 -> unit) : int64 =
        use file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)
        let buffer = Array.zeroCreate<byte> (128 * 1024)
        let mutable written = 0L
        let mutable reading = true

        while reading do
            let read = stream.Read(buffer, 0, buffer.Length)

            if read <= 0 then
                reading <- false
            else
                file.Write(buffer, 0, read)
                written <- written + int64 read
                report (before + written) total

        file.Flush()
        written

    /// Downloads one bundle and puts it in place.
    ///
    /// Every file lands in a temporary file first and is only moved into "mod
    /// files" once it has arrived whole. A download that is cut off halfway
    /// therefore leaves nothing behind: the old file, if there was one, is
    /// still the old file, and the check above still says the bundle is
    /// missing rather than quietly passing a truncated one.
    let fetch (bundle: Bundle) (report: int64 -> int64 -> unit) : Async<Result<unit, string>> =
        async {
            let staging = Path.Combine(Path.GetTempPath(), "DLSS5Manager-assets")

            try
                Directory.CreateDirectory(staging) |> ignore
                let! total = measure bundle
                let mutable before = 0L
                report 0L total

                // Carried rather than returned: F# has no early exit out of a
                // loop, and the point is that the first thing to go wrong
                // stops the rest without anything having been replaced.
                let mutable failure = ""
                let landed = ResizeArray<string * string>()

                for asset in bundle.Assets do
                    if failure = "" then
                        let temp = Path.Combine(staging, asset.Name + ".part")

                        use! response =
                            client.GetAsync(Origin + asset.Name, HttpCompletionOption.ResponseHeadersRead)
                            |> Async.AwaitTask

                        if not response.IsSuccessStatusCode then
                            failure <- sprintf "%s: the server answered %d." asset.Name (int response.StatusCode)
                        else
                            use! stream = response.Content.ReadAsStreamAsync() |> Async.AwaitTask
                            before <- before + drain stream temp before total report

                            for target in asset.Targets do
                                landed.Add(temp, target)

                if failure <> "" then
                    return Result.Error failure
                else
                    // Nothing is replaced until every file in the bundle is here.
                    for (temp, target) in landed do
                        let folder = Path.GetDirectoryName(target: string)

                        if not (String.IsNullOrEmpty(folder)) then
                            Directory.CreateDirectory(folder) |> ignore

                        File.Copy(temp, target, true)

                    for (temp, _) in landed do
                        try
                            File.Delete(temp)
                        with _ ->
                            ()

                    report (max before 1L) (max before 1L)
                    return Ok()
            with ex ->
                return Result.Error(ex.Message)
        }
