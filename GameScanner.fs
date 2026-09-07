namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Diagnostics
open System.Collections.Generic
open Microsoft.Win32
open DLSS_5_MANAGER.Models

module GameScanner =

    let private httpClient = 
        let client = new HttpClient()
        client.Timeout <- TimeSpan.FromSeconds(2.5)
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DLSS5Manager-GameFinder/1.0 (Windows NT 10.0; Win64; x64)")
        client

    let private getPostersCacheDir () =
        let localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        let dir = Path.Combine(localAppData, "DLSS5Manager", "Cache", "Posters")
        if not (Directory.Exists(dir)) then Directory.CreateDirectory(dir) |> ignore
        dir

    let private tryGetJsonString (element: JsonElement) (propName: string) : string =
        match element.TryGetProperty(propName) with
        | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
        | _ -> ""

    let private downloadPoster (url: string) (cacheFilename: string) : string =
        try
            let cacheDir = getPostersCacheDir ()
            let localPath = Path.Combine(cacheDir, cacheFilename)
            if File.Exists(localPath) && (FileInfo(localPath).Length > 1000L) then
                localPath
            else
                let task = httpClient.GetByteArrayAsync(url)
                let bytes = task.GetAwaiter().GetResult()
                if bytes <> null && bytes.Length > 1000 then
                    File.WriteAllBytes(localPath, bytes)
                    localPath
                else
                    ""
        with _ -> ""

    let private isVerticalPoster (filePath: string) : bool =
        try
            if String.IsNullOrWhiteSpace(filePath) || not (File.Exists(filePath)) then false
            else
                use img = System.Drawing.Image.FromFile(filePath)
                img.Height > img.Width
        with _ -> false

    let private fetchSteamPoster (appId: string) : string =
        if String.IsNullOrWhiteSpace(appId) then ""
        else
            let urls = [|
                sprintf "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/%s/library_600x900.jpg" appId
                sprintf "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/%s/library_600x900_2x.jpg" appId
            |]

            let mutable result = ""
            for url in urls do
                if String.IsNullOrWhiteSpace(result) then
                    let downloaded = downloadPoster url (sprintf "steam_%s_600x900.jpg" appId)
                    if not (String.IsNullOrWhiteSpace(downloaded)) && isVerticalPoster downloaded then
                        result <- downloaded
            result

    let private generateSearchQueries (rawTitle: string) : string list =
        let cleaned = 
            Regex.Replace(rawTitle, @"(\[.*?\]|\(.*?\)|v\d+[\.\d]*|build\s*\d+|repack|dodi|fitgirl|elamigos|codex|skidrow|razor1911|flt|cpx|resynced|enhanced\s*edition|deluxe\s*edition|gold\s*edition|goty|game\s*of\s*the\s*year|definitive\s*edition|director'?s\s*cut|special\s*edition|anniversary\s*edition|ultimate\s*edition|complete\s*edition|remastered|remake|edition|update|hotfix)", " ", RegexOptions.IgnoreCase)
            |> fun s -> s.Replace('_', ' ').Replace('-', ' ').Replace('.', ' ')
            |> fun s -> Regex.Replace(s, @"\s+", " ").Trim()

        let list = List<string>()
        if not (String.IsNullOrWhiteSpace(cleaned)) then
            list.Add(cleaned)

            // Handle common franchise prefixes / apostrophes
            if cleaned.StartsWith("Assassins Creed", StringComparison.OrdinalIgnoreCase) then
                list.Add(cleaned.Replace("Assassins Creed", "Assassin's Creed", StringComparison.OrdinalIgnoreCase))
                list.Add(cleaned.Replace("Assassins Creed", "Assassin's Creed IV", StringComparison.OrdinalIgnoreCase))
                list.Add(cleaned.Replace("Assassins Creed", "Assassin's Creed IV Black Flag", StringComparison.OrdinalIgnoreCase))

            // Roman numerals <-> Numbers
            if cleaned.Contains(" 4") then list.Add(cleaned.Replace(" 4", " IV"))
            if cleaned.Contains(" IV", StringComparison.OrdinalIgnoreCase) then list.Add(cleaned.Replace(" IV", " 4", StringComparison.OrdinalIgnoreCase))
            if cleaned.Contains(" 3") then list.Add(cleaned.Replace(" 3", " III"))
            if cleaned.Contains(" III", StringComparison.OrdinalIgnoreCase) then list.Add(cleaned.Replace(" III", " 3", StringComparison.OrdinalIgnoreCase))
            if cleaned.Contains(" 5") then list.Add(cleaned.Replace(" 5", " V"))
            if cleaned.Contains(" V", StringComparison.OrdinalIgnoreCase) then list.Add(cleaned.Replace(" V", " 5", StringComparison.OrdinalIgnoreCase))
            if cleaned.Contains(" 2") then list.Add(cleaned.Replace(" 2", " II"))
            if cleaned.Contains(" II", StringComparison.OrdinalIgnoreCase) then list.Add(cleaned.Replace(" II", " 2", StringComparison.OrdinalIgnoreCase))

            // Shortened root title
            let words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            if words.Length >= 3 then
                list.Add(String.concat " " (words |> Array.take (min 4 words.Length)))
                list.Add(String.concat " " (words |> Array.take 3))
            if words.Length >= 2 then
                list.Add(String.concat " " (words |> Array.take 2))

        list |> Seq.distinct |> Seq.toList

    let private searchSteamPosterByTitle (gameTitle: string) : string =
        let queries = generateSearchQueries gameTitle
        let mutable foundPoster = ""

        let searchSingleQuery (query: string) =
            try
                if String.IsNullOrWhiteSpace(query) then ""
                else
                    let encoded = Uri.EscapeDataString(query)
                    let url = sprintf "https://store.steampowered.com/api/storesearch/?term=%s&l=english&cc=US" encoded
                    let task = httpClient.GetStringAsync(url)
                    let json = task.GetAwaiter().GetResult()
                    using (JsonDocument.Parse(json)) (fun doc ->
                        let root = doc.RootElement
                        let mutable candidatePoster = ""
                        match root.TryGetProperty("items") with
                        | true, items when items.ValueKind = JsonValueKind.Array ->
                            let count = items.GetArrayLength()
                            // Pass 1: Try base games only (filter out DLCs/Packs/Soundtracks)
                            for i in 0 .. (min 10 (count - 1)) do
                                if String.IsNullOrWhiteSpace(candidatePoster) then
                                    let item = items.[i]
                                    let itemName = tryGetJsonString item "name"
                                    let isDlc = itemName.Contains("Pack", StringComparison.OrdinalIgnoreCase) || 
                                                itemName.Contains("DLC", StringComparison.OrdinalIgnoreCase) || 
                                                itemName.Contains("Soundtrack", StringComparison.OrdinalIgnoreCase) ||
                                                itemName.Contains("Season Pass", StringComparison.OrdinalIgnoreCase)
                                    if not isDlc then
                                        match item.TryGetProperty("id") with
                                        | true, idProp when idProp.ValueKind = JsonValueKind.Number ->
                                            let id = string (idProp.GetInt32())
                                            let p = fetchSteamPoster id
                                            if not (String.IsNullOrWhiteSpace(p)) then
                                                candidatePoster <- p
                                        | _ -> ()
                            
                            // Pass 2: If no base game matched, try any item
                            if String.IsNullOrWhiteSpace(candidatePoster) then
                                for i in 0 .. (min 5 (count - 1)) do
                                    if String.IsNullOrWhiteSpace(candidatePoster) then
                                        let item = items.[i]
                                        match item.TryGetProperty("id") with
                                        | true, idProp when idProp.ValueKind = JsonValueKind.Number ->
                                            let id = string (idProp.GetInt32())
                                            let p = fetchSteamPoster id
                                            if not (String.IsNullOrWhiteSpace(p)) then
                                                candidatePoster <- p
                                        | _ -> ()
                        | _ -> ()
                        candidatePoster
                    )
            with _ -> ""

        for q in queries do
            if String.IsNullOrWhiteSpace(foundPoster) then
                let p = searchSingleQuery q
                if not (String.IsNullOrWhiteSpace(p)) then
                    foundPoster <- p

        foundPoster

    let private searchGogPosterByTitle (gameTitle: string) : string =
        let queries = generateSearchQueries gameTitle
        let mutable foundPoster = ""

        let searchSingleQuery (query: string) =
            try
                if String.IsNullOrWhiteSpace(query) then ""
                else
                    let encoded = Uri.EscapeDataString(query)
                    let url = sprintf "https://embed.gog.com/games/ajax/filtered?mediaType=game&search=%s" encoded
                    let task = httpClient.GetStringAsync(url)
                    let json = task.GetAwaiter().GetResult()
                    using (JsonDocument.Parse(json)) (fun doc ->
                        let root = doc.RootElement
                        let mutable candidatePoster = ""
                        match root.TryGetProperty("products") with
                        | true, products when products.ValueKind = JsonValueKind.Array && products.GetArrayLength() > 0 ->
                            let count = products.GetArrayLength()
                            for i in 0 .. (min 3 (count - 1)) do
                                if String.IsNullOrWhiteSpace(candidatePoster) then
                                    let prod = products.[i]
                                    let img = tryGetJsonString prod "image"
                                    if not (String.IsNullOrWhiteSpace(img)) then
                                        let imgUrl = 
                                            let cleanImg = if img.StartsWith("//") then "https:" + img else img
                                            sprintf "%s_vertical_480.jpg" cleanImg
                                        let p = downloadPoster imgUrl (sprintf "gog_%s.jpg" (Math.Abs(query.GetHashCode()).ToString()))
                                        if not (String.IsNullOrWhiteSpace(p)) && isVerticalPoster p then
                                            candidatePoster <- p
                        | _ -> ()
                        candidatePoster
                    )
            with _ -> ""

        for q in queries do
            if String.IsNullOrWhiteSpace(foundPoster) then
                let p = searchSingleQuery q
                if not (String.IsNullOrWhiteSpace(p)) then
                    foundPoster <- p

        foundPoster

    let private resolveArtwork (steamPath: string) (appIdOpt: string option) (gameTitle: string) (exePath: string) : string =
        let mutable found = ""

        // 1. Check local Steam appcache for STRICTLY 600x900 vertical posters
        match appIdOpt with
        | Some appId when not (String.IsNullOrWhiteSpace(steamPath)) && not (String.IsNullOrWhiteSpace(appId)) ->
            let appCacheDir = Path.Combine(steamPath, "appcache", "librarycache", appId)
            if Directory.Exists(appCacheDir) then
                let candidateFiles = Directory.GetFiles(appCacheDir, "*600x900*.jpg", SearchOption.AllDirectories)
                for f in candidateFiles do
                    if String.IsNullOrWhiteSpace(found) && isVerticalPoster f then
                        found <- f
        | _ -> ()

        // 2. Fetch official 600x900 vertical poster from Steam CDN if AppId is known
        if String.IsNullOrWhiteSpace(found) then
            match appIdOpt with
            | Some appId when not (String.IsNullOrWhiteSpace(appId)) ->
                found <- fetchSteamPoster appId
            | _ -> ()

        // 3. Search Steam Store API by Game Title and fetch official 600x900 vertical poster
        if String.IsNullOrWhiteSpace(found) && not (String.IsNullOrWhiteSpace(gameTitle)) then
            found <- searchSteamPosterByTitle gameTitle

        // 4. Search GOG API by Game Title
        if String.IsNullOrWhiteSpace(found) && not (String.IsNullOrWhiteSpace(gameTitle)) then
            found <- searchGogPosterByTitle gameTitle

        // 5. Fallback to icon extraction ONLY if no HD vertical poster was found anywhere
        if String.IsNullOrWhiteSpace(found) && not (String.IsNullOrWhiteSpace(exePath)) then
            found <- IconExtractor.extractIconToCache exePath

        found

    // =========================================================================
    // BLACKLIST CONFIGURATION
    // =========================================================================
    let private blacklistedFolderNames = 
        HashSet<string>(
            [|
                "_commonredist"; "directx"; "vcredist"; "dotnet"; "mono"; "openal"; "dependencies"; "runtime"
                "engine"; "thirdparty"; "extras"; "crashreportclient"; "support"; "documentation"; "tools"
                "easyanticheat"; "battleye"; "denuvo"; "nprotect"; "xigncode3"; "vanguard"
                "shadercache"; "d3dscache"; "savedata"; "mods"; "dlc"; "logs"; "screenshots"
            |],
            StringComparer.OrdinalIgnoreCase
        )

    let private blacklistedExeNames =
        HashSet<string>(
            [|
                "steam.exe"; "steamservice.exe"; "steamwebhelper.exe"; "epicgameslauncher.exe"
                "gog_galaxy.exe"; "galaxyclient.exe"; "origin.exe"; "eadesktop.exe"; "ubisoftconnect.exe"
                "battle.net.exe"; "uplay.exe"; "rockstargameslauncher.exe"; "dxsetup.exe"
                "oalinst.exe"; "crashreportclient.exe"; "crashreportclient-win64-shipping.exe"
                "unrealcecommon.exe"; "bugsplat.exe"; "sendrpt.exe"; "werfault.exe"; "feedback.exe"; "reporttool.exe"
                "easyanticheat_setup.exe"; "easyanticheat.exe"; "beservice.exe"; "bedaisy.exe"; "beservice_x64.exe"
                "steamnetworkingsockets.exe"; "vanguard.exe"; "launcher.exe"; "launch.exe"; "play.exe"; "start.exe"
                "gamelauncher.exe"; "config.exe"; "settings.exe"; "configuration.exe"; "autorun.exe"; "patch.exe"
                "updater.exe"; "uninstaller.exe"; "unins000.exe"; "unins001.exe"
            |],
            StringComparer.OrdinalIgnoreCase
        )

    let private isBlacklistedExe (fileName: string) =
        let lower = fileName.ToLowerInvariant()
        if blacklistedExeNames.Contains(lower) then
            true
        elif lower.StartsWith("vcredist") || lower.StartsWith("vc_redist") || lower.StartsWith("dotnet") || lower.StartsWith("unitycrashhandler") || lower.StartsWith("unins") then
            true
        else
            false

    let private isBlacklistedFolder (dirName: string) =
        blacklistedFolderNames.Contains(dirName)

    // =========================================================================
    // UPSCALING TECHNOLOGY DETECTION ENGINE
    // =========================================================================
    let private detectUpscalingFeatures (gameDir: string) (exePath: string) : string * string * string * string =
        try
            let searchDirs = 
                let list = List<string>()
                if Directory.Exists(gameDir) then list.Add(gameDir)
                if not (String.IsNullOrWhiteSpace(exePath)) then
                    let exeDir = Path.GetDirectoryName(exePath)
                    if Directory.Exists(exeDir) && not (list.Contains(exeDir)) then list.Add(exeDir)
                list

            let mutable hasDlss = false
            let mutable hasFsr = false
            let mutable hasXess = false
            let mutable hasOptiscaler = false

            for dir in searchDirs do
                let files = Directory.GetFiles(dir, "*.dll", SearchOption.TopDirectoryOnly)
                for f in files do
                    let name = Path.GetFileName(f).ToLowerInvariant()
                    if name.Contains("nvngx_dlss") || name.Contains("nvngx.dll") then hasDlss <- true
                    if name.Contains("ffx_fsr") || name.Contains("fidelityfx") then hasFsr <- true
                    if name.Contains("libxess") then hasXess <- true
                    if name.Contains("optiscaler") then hasOptiscaler <- true

                if File.Exists(Path.Combine(dir, "nvngx.ini")) then
                    hasOptiscaler <- true

            let status = 
                if hasOptiscaler then "OptiScaler Active"
                elif hasDlss && hasFsr then "DLSS + FSR"
                elif hasDlss then "DLSS Ready"
                elif hasFsr then "FSR Ready"
                elif hasXess then "XeSS Ready"
                else "Direct3D / Vulkan"

            let dlssVer = if hasDlss then "Supported" else ""
            let fsrVer = if hasFsr then "Supported" else ""
            let xessVer = if hasXess then "Supported" else ""

            (status, dlssVer, fsrVer, xessVer)
        with
        | _ -> ("Direct3D / Vulkan", "", "", "")

    // =========================================================================
    // REAL EXECUTABLE RESOLUTION (delegated to the scoring engine)
    // =========================================================================
    let resolvePrimaryExecutable (gameRoot: string) (gameTitle: string) : string * int64 =
        GameAnalyzer.resolveGameExecutable gameRoot gameTitle

    // =========================================================================
    // STEAM DETECTOR
    // =========================================================================
    let private scanSteamGames () : GameItem list =
        let results = List<GameItem>()
        try
            // 1. Locate Steam root
            let mutable steamPath = ""
            try
                use key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")
                if key <> null then
                    steamPath <- string (key.GetValue("SteamPath", ""))
            with _ -> ()

            if String.IsNullOrWhiteSpace(steamPath) then
                try
                    use key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")
                    if key <> null then
                        steamPath <- string (key.GetValue("InstallPath", ""))
                with _ -> ()

            if String.IsNullOrWhiteSpace(steamPath) && Directory.Exists(@"C:\Program Files (x86)\Steam") then
                steamPath <- @"C:\Program Files (x86)\Steam"

            if not (String.IsNullOrWhiteSpace(steamPath)) && Directory.Exists(steamPath) then
                let steamLibraries = List<string>()
                steamLibraries.Add(steamPath)

                // 2. Parse libraryfolders.vdf
                let vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf")
                if File.Exists(vdfPath) then
                    let vdfContent = File.ReadAllText(vdfPath)
                    let matches = Regex.Matches(vdfContent, @"""path""\s+""([^""]+)""")
                    for m in matches do
                        let p = m.Groups.[1].Value.Replace(@"\\", @"\")
                        if Directory.Exists(p) && not (steamLibraries.Contains(p)) then
                            steamLibraries.Add(p)

                // 3. Scan each Steam Library folder
                for lib in steamLibraries do
                    let steamAppsDir = Path.Combine(lib, "steamapps")
                    if Directory.Exists(steamAppsDir) then
                        let manifestFiles = Directory.GetFiles(steamAppsDir, "appmanifest_*.acf")
                        for acf in manifestFiles do
                            try
                                let content = File.ReadAllText(acf)
                                let appIdMatch = Regex.Match(content, @"""appid""\s+""(\d+)""")
                                let nameMatch = Regex.Match(content, @"""name""\s+""([^""]+)""")
                                let installDirMatch = Regex.Match(content, @"""installdir""\s+""([^""]+)""")

                                if appIdMatch.Success && nameMatch.Success && installDirMatch.Success then
                                    let appId = appIdMatch.Groups.[1].Value
                                    let title = nameMatch.Groups.[1].Value
                                    let installDirName = installDirMatch.Groups.[1].Value

                                    // Skip Steamworks redistributables / proton
                                    if appId <> "228980" && not (title.Contains("Steamworks")) && not (title.Contains("Proton")) && not (title.Contains("Shared")) then
                                        let fullInstallDir = Path.Combine(steamAppsDir, "common", installDirName)
                                        if Directory.Exists(fullInstallDir) then
                                            let (exePath, exeSize) = resolvePrimaryExecutable fullInstallDir title
                                            let bannerPath = resolveArtwork steamPath (Some appId) title exePath
                                            let (upscaleStatus, dlss, fsr, xess) = detectUpscalingFeatures fullInstallDir exePath

                                            let item = {
                                                AppId = sprintf "steam_%s" appId
                                                Title = title
                                                LauncherTypeName = "STEAM"
                                                InstallDirectory = fullInstallDir
                                                TargetExecutablePath = exePath
                                                TargetExecutableSize = exeSize
                                                LocalBannerPath = bannerPath
                                                LastManifestTimestamp = DateTime.UtcNow.Ticks
                                                UpscaleStatus = upscaleStatus
                                                DlssVersion = dlss
                                                FsrVersion = fsr
                                                XessVersion = xess
                                            }
                                            results.Add(item)
                            with _ -> ()
        with _ -> ()
        results |> Seq.toList

    // =========================================================================
    // EPIC GAMES DETECTOR
    // =========================================================================
    let private scanEpicGames () : GameItem list =
        let results = List<GameItem>()
        try
            let programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            let epicManifestsDir = Path.Combine(programData, "Epic", "EpicGamesLauncher", "Data", "Manifests")
            let epicImagesDir = Path.Combine(programData, "Epic", "EpicGamesLauncher", "Data", "Images")

            if Directory.Exists(epicManifestsDir) then
                let itemFiles = Directory.GetFiles(epicManifestsDir, "*.item")
                for itemPath in itemFiles do
                    try
                        let json = File.ReadAllText(itemPath)
                        using (JsonDocument.Parse(json)) (fun doc ->
                            let root = doc.RootElement
                            let displayName = tryGetJsonString root "DisplayName"
                            let installLocation = tryGetJsonString root "InstallLocation"
                            let launchExecutable = tryGetJsonString root "LaunchExecutable"
                            let appName = tryGetJsonString root "AppName"

                            if not (String.IsNullOrWhiteSpace(displayName)) && not (String.IsNullOrWhiteSpace(installLocation)) && Directory.Exists(installLocation) then
                                if not (displayName.StartsWith("Unreal Engine")) && not (appName.StartsWith("UE_")) then
                                    let mutable exePath = ""
                                    let mutable exeSize = 0L

                                    // Epic's LaunchExecutable is often a launcher stub
                                    // (PlayGTAV.exe), so only trust it when it is the real game.
                                    if not (String.IsNullOrWhiteSpace(launchExecutable)) then
                                        let candidate = Path.Combine(installLocation, launchExecutable)
                                        if File.Exists(candidate) && not (GameAnalyzer.isLikelyLauncherExe candidate) then
                                            exePath <- candidate
                                            exeSize <- FileInfo(candidate).Length

                                    if String.IsNullOrWhiteSpace(exePath) then
                                        let (resolvedPath, resolvedSize) = resolvePrimaryExecutable installLocation displayName
                                        exePath <- resolvedPath
                                        exeSize <- resolvedSize

                                    let mutable bannerPath = ""
                                    if Directory.Exists(epicImagesDir) then
                                        let candidateImages = Directory.GetFiles(epicImagesDir, sprintf "*%s*" appName)
                                        if candidateImages.Length > 0 then
                                            bannerPath <- candidateImages.[0]

                                    if String.IsNullOrWhiteSpace(bannerPath) then
                                        bannerPath <- resolveArtwork "" None displayName exePath

                                    let (upscaleStatus, dlss, fsr, xess) = detectUpscalingFeatures installLocation exePath

                                    let item = {
                                        AppId = sprintf "epic_%s" appName
                                        Title = displayName
                                        LauncherTypeName = "EPIC GAMES"
                                        InstallDirectory = installLocation
                                        TargetExecutablePath = exePath
                                        TargetExecutableSize = exeSize
                                        LocalBannerPath = bannerPath
                                        LastManifestTimestamp = DateTime.UtcNow.Ticks
                                        UpscaleStatus = upscaleStatus
                                        DlssVersion = dlss
                                        FsrVersion = fsr
                                        XessVersion = xess
                                    }
                                    results.Add(item)
                        )
                    with _ -> ()
        with _ -> ()
        results |> Seq.toList

    // =========================================================================
    // GOG GALAXY DETECTOR
    // =========================================================================
    let private scanGogGames () : GameItem list =
        let results = List<GameItem>()
        try
            let gogRegistryPaths = [| @"SOFTWARE\WOW6432Node\GOG.com\Games"; @"SOFTWARE\GOG.com\Games" |]
            for regPath in gogRegistryPaths do
                try
                    use baseKey = Registry.LocalMachine.OpenSubKey(regPath)
                    if baseKey <> null then
                        for subKeyName in baseKey.GetSubKeyNames() do
                            try
                                use gameKey = baseKey.OpenSubKey(subKeyName)
                                if gameKey <> null then
                                    let gameName = string (gameKey.GetValue("gameName", ""))
                                    let gamePath = string (gameKey.GetValue("path", ""))
                                    let exeName = string (gameKey.GetValue("exe", ""))
                                    let gameId = string (gameKey.GetValue("gameID", subKeyName))

                                    if not (String.IsNullOrWhiteSpace(gameName)) && not (String.IsNullOrWhiteSpace(gamePath)) && Directory.Exists(gamePath) then
                                        let mutable targetExe = ""
                                        let mutable targetSize = 0L

                                        // Same story for the GOG registry "exe" value.
                                        if not (String.IsNullOrWhiteSpace(exeName)) then
                                            let directExe = Path.Combine(gamePath, exeName)
                                            if File.Exists(directExe) && not (GameAnalyzer.isLikelyLauncherExe directExe) then
                                                targetExe <- directExe
                                                targetSize <- FileInfo(directExe).Length

                                        if String.IsNullOrWhiteSpace(targetExe) then
                                            let (resolvedPath, resolvedSize) = resolvePrimaryExecutable gamePath gameName
                                            targetExe <- resolvedPath
                                            targetSize <- resolvedSize

                                        let bannerPath = resolveArtwork "" None gameName targetExe
                                        let (upscaleStatus, dlss, fsr, xess) = detectUpscalingFeatures gamePath targetExe

                                        let item = {
                                            AppId = sprintf "gog_%s" gameId
                                            Title = gameName
                                            LauncherTypeName = "GOG GALAXY"
                                            InstallDirectory = gamePath
                                            TargetExecutablePath = targetExe
                                            TargetExecutableSize = targetSize
                                            LocalBannerPath = bannerPath
                                            LastManifestTimestamp = DateTime.UtcNow.Ticks
                                            UpscaleStatus = upscaleStatus
                                            DlssVersion = dlss
                                            FsrVersion = fsr
                                            XessVersion = xess
                                        }
                                        results.Add(item)
                            with _ -> ()
                with _ -> ()
        with _ -> ()
        results |> Seq.toList

    // =========================================================================
    // REPACK / CRACKED / STANDALONE DRIVES SCANNER
    // =========================================================================
    let private scanRepackAndStandaloneGames () : GameItem list =
        let results = List<GameItem>()
        try
            let allDrives = DriveInfo.GetDrives() |> Array.filter (fun d -> d.IsReady && d.DriveType = DriveType.Fixed)

            let candidateRootFolderPatterns = [|
                "games"; "games++"; "jeux"; "spiele"; "dodi-repacks"; "fitgirl repacks"
                "grand theft auto"; "pragmata"; "gog games"; "cracked"; "repack"
            |]

            for drive in allDrives do
                try
                    let rootDir = drive.RootDirectory
                    for topDir in rootDir.GetDirectories() do
                        try
                            let topNameLower = topDir.Name.ToLowerInvariant()
                            let isCandidateFolder = 
                                candidateRootFolderPatterns |> Array.exists (fun p -> topNameLower.Contains(p))

                            if isCandidateFolder && not (topNameLower.Contains("windows")) && not (topNameLower.Contains("recycle")) then
                                let subDirs = topDir.GetDirectories()
                                let isContainerFolder = 
                                    subDirs.Length > 0 && 
                                    (topNameLower = "games" || topNameLower = "games++" || topNameLower = "epic games" || topNameLower = "gog games" || topNameLower = "jeux" || topNameLower = "spiele" || topNameLower.EndsWith("repacks"))

                                if not isContainerFolder then
                                    let (directExe, directSize) = resolvePrimaryExecutable topDir.FullName topDir.Name
                                    if directSize > 25000000L then
                                        let banner = resolveArtwork "" None topDir.Name directExe
                                        let (upscaleStatus, dlss, fsr, xess) = detectUpscalingFeatures topDir.FullName directExe

                                        let item = {
                                            AppId = sprintf "repack_%s" (topDir.FullName.GetHashCode().ToString("X"))
                                            Title = topDir.Name
                                            LauncherTypeName = "REPACK"
                                            InstallDirectory = topDir.FullName
                                            TargetExecutablePath = directExe
                                            TargetExecutableSize = directSize
                                            LocalBannerPath = banner
                                            LastManifestTimestamp = DateTime.UtcNow.Ticks
                                            UpscaleStatus = upscaleStatus
                                            DlssVersion = dlss
                                            FsrVersion = fsr
                                            XessVersion = xess
                                        }
                                        results.Add(item)

                                for gameSubDir in subDirs do
                                    try
                                        let subNameLower = gameSubDir.Name.ToLowerInvariant()
                                        if not (isBlacklistedFolder gameSubDir.Name) && not (subNameLower.Contains("steamapps")) && not (subNameLower.Contains("riot")) then
                                            let (exePath, exeSize) = resolvePrimaryExecutable gameSubDir.FullName gameSubDir.Name
                                            if exeSize > 20000000L then
                                                let banner = resolveArtwork "" None gameSubDir.Name exePath
                                                let (upscaleStatus, dlss, fsr, xess) = detectUpscalingFeatures gameSubDir.FullName exePath

                                                let launcherType = 
                                                    if subNameLower.Contains("repack") || subNameLower.Contains("fitgirl") || subNameLower.Contains("dodi") || topNameLower.Contains("repack") then "REPACK"
                                                    else "STANDALONE"

                                                let item = {
                                                    AppId = sprintf "standalone_%s" (gameSubDir.FullName.GetHashCode().ToString("X"))
                                                    Title = gameSubDir.Name
                                                    LauncherTypeName = launcherType
                                                    InstallDirectory = gameSubDir.FullName
                                                    TargetExecutablePath = exePath
                                                    TargetExecutableSize = exeSize
                                                    LocalBannerPath = banner
                                                    LastManifestTimestamp = DateTime.UtcNow.Ticks
                                                    UpscaleStatus = upscaleStatus
                                                    DlssVersion = dlss
                                                    FsrVersion = fsr
                                                    XessVersion = xess
                                                }
                                                results.Add(item)
                                    with _ -> ()
                        with _ -> ()
                with _ -> ()
        with _ -> ()
        results |> Seq.toList

    let createCustomGameItem (title: string) (folderPath: string) (exePath: string) : GameItem =
        let exeSize = try FileInfo(exePath).Length with _ -> 0L
        let banner = resolveArtwork "" None title exePath
        let (upscaleStatus, dlss, fsr, xess) = detectUpscalingFeatures folderPath exePath
        {
            AppId = sprintf "custom_%s" (folderPath.GetHashCode().ToString("X"))
            Title = title
            LauncherTypeName = "CUSTOM"
            InstallDirectory = folderPath
            TargetExecutablePath = exePath
            TargetExecutableSize = exeSize
            LocalBannerPath = banner
            LastManifestTimestamp = DateTime.UtcNow.Ticks
            UpscaleStatus = upscaleStatus
            DlssVersion = dlss
            FsrVersion = fsr
            XessVersion = xess
        }

    let scanCustomFolder (folderPath: string) : GameItem list =
        let results = List<GameItem>()
        try
            if Directory.Exists(folderPath) then
                let dirInfo = DirectoryInfo(folderPath)
                let subDirs = dirInfo.GetDirectories()
                for sub in subDirs do
                    try
                        let (exePath, exeSize) = resolvePrimaryExecutable sub.FullName sub.Name
                        if exeSize > 20000000L && File.Exists(exePath) then
                            results.Add(createCustomGameItem sub.Name sub.FullName exePath)
                    with _ -> ()
                if results.Count = 0 then
                    let (exePath, exeSize) = resolvePrimaryExecutable folderPath dirInfo.Name
                    if File.Exists(exePath) then
                        results.Add(createCustomGameItem dirInfo.Name folderPath exePath)
        with _ -> ()
        results |> Seq.toList

    // =========================================================================
    // STRICT DE-DUPLICATION ENGINE
    // =========================================================================
    let private normalizeGameTitle (title: string) : string =
        let t = Regex.Replace(title.ToLowerInvariant(), @"(\[.*?\]|\(.*?\)|fitgirl|dodi|repack|edition|complete|the|version|\s+)", "")
        Regex.Replace(t, @"[^a-z0-9]", "")

    let deduplicateGames (games: GameItem list) : GameItem list =
        let seenTitles = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        let seenPaths = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        let seenAppIds = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        let unique = List<GameItem>()

        for g in games do
            let normTitle = normalizeGameTitle g.Title
            let normPath = g.InstallDirectory.ToLowerInvariant().TrimEnd('\\', '/')
            let appId = g.AppId.ToLowerInvariant()

            let isDuplicate =
                seenPaths.Contains(normPath) ||
                seenAppIds.Contains(appId) ||
                (not (String.IsNullOrWhiteSpace(normTitle)) && seenTitles.Contains(normTitle))

            if not isDuplicate then
                seenPaths.Add(normPath) |> ignore
                seenAppIds.Add(appId) |> ignore
                if not (String.IsNullOrWhiteSpace(normTitle)) then seenTitles.Add(normTitle) |> ignore
                unique.Add(g)

        unique |> Seq.toList

    // =========================================================================
    // CACHING SYSTEM (Atomic Delta Cache)
    // =========================================================================
    let private getCacheFilePath () =
        let localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        let dir = Path.Combine(localAppData, "DLSS5Manager")
        if not (Directory.Exists(dir)) then Directory.CreateDirectory(dir) |> ignore
        Path.Combine(dir, "library_cache_v3.json")

    let loadCachedGames () : GameItem list =
        try
            let cachePath = getCacheFilePath ()
            if File.Exists(cachePath) then
                let json = File.ReadAllText(cachePath)
                let options = JsonSerializerOptions()
                options.PropertyNameCaseInsensitive <- true
                JsonSerializer.Deserialize<GameItem list>(json, options)
            else []
        with _ -> []

    let saveGamesToCache (games: GameItem list) : unit =
        try
            let cachePath = getCacheFilePath ()
            let options = JsonSerializerOptions()
            options.WriteIndented <- true
            let json = JsonSerializer.Serialize(games, options)
            File.WriteAllText(cachePath, json)
        with _ -> ()

    let clearCache () : unit =
        try
            let cachePath = getCacheFilePath ()
            if File.Exists(cachePath) then File.Delete(cachePath)
        with _ -> ()

    // -------------------------------------------------------------------------
    // EMULATORS
    // -------------------------------------------------------------------------
    /// Emulators are never discovered by scanning - the user adds each one by
    /// hand - so they live in their own file and a library re-scan cannot wipe
    /// them out.
    let private getEmulatorCachePath () =
        let localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        let dir = Path.Combine(localAppData, "DLSS5Manager")
        if not (Directory.Exists(dir)) then Directory.CreateDirectory(dir) |> ignore
        Path.Combine(dir, "emulators_cache.json")

    let loadCachedEmulators () : GameItem list =
        try
            let path = getEmulatorCachePath ()

            if File.Exists(path) then
                let options = JsonSerializerOptions()
                options.PropertyNameCaseInsensitive <- true
                JsonSerializer.Deserialize<GameItem list>(File.ReadAllText(path), options)
            else
                []
        with _ ->
            []

    let saveEmulatorsToCache (emulators: GameItem list) : unit =
        try
            let options = JsonSerializerOptions()
            options.WriteIndented <- true
            File.WriteAllText(getEmulatorCachePath (), JsonSerializer.Serialize(emulators, options))
        with _ ->
            ()

    /// The build that last wrote the cache. A new release can detect things the
    /// old one could not, so the first launch after an update re-reads the
    /// library once instead of trusting what an older build recorded.
    /// One short line on disk - nothing to parse, nothing to grow.
    let private getCacheStampPath () =
        let localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        let dir = Path.Combine(localAppData, "DLSS5Manager")
        if not (Directory.Exists(dir)) then Directory.CreateDirectory(dir) |> ignore
        Path.Combine(dir, "cache_version.txt")

    let readCacheVersion () : string =
        try
            let p = getCacheStampPath ()
            if File.Exists(p) then File.ReadAllText(p).Trim() else ""
        with _ ->
            ""

    let writeCacheVersion (version: string) : unit =
        try File.WriteAllText(getCacheStampPath (), version)
        with _ -> ()

    // =========================================================================
    // USER PREFERENCES & SETTINGS SYSTEM
    // =========================================================================
    type AppSettings = {
        IsSidebarLayout: bool
        ColorAtmosphere: string
        GeometricMotif: string
        /// Two-letter UI language code. Empty on an upgrade from 1.0.x, which
        /// is the signal to fall back to whatever the OS is set to.
        Language: string
        /// The app version whose support prompt has already been shown. It
        /// appears once per machine, and once more after each update.
        SupportPromptVersion: string
        /// AMD RDNA 4 route. Off unless the user turns it on, and then every
        /// game installs through that one payload instead of the usual routes.
        AmdMode: bool
        /// Performance mode: stops the moving background and the card hover
        /// animations. Off by default - the app is meant to look alive.
        PerformanceMode: bool
    }

    let private getSettingsFilePath () =
        let localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        let dir = Path.Combine(localAppData, "DLSS5Manager")
        if not (Directory.Exists(dir)) then Directory.CreateDirectory(dir) |> ignore
        Path.Combine(dir, "settings.json")

    let private defaultSettings () =
        { IsSidebarLayout = false
          ColorAtmosphere = "Neon Emerald"
          GeometricMotif = "Orbital Spheres"
          Language = Localization.systemLanguage ()
          SupportPromptVersion = ""
          AmdMode = false
          PerformanceMode = false }

    let loadSettings () : AppSettings =
        try
            let p = getSettingsFilePath ()
            if File.Exists(p) then
                let json = File.ReadAllText(p)
                let options = JsonSerializerOptions()
                options.PropertyNameCaseInsensitive <- true
                let s = JsonSerializer.Deserialize<AppSettings>(json, options)
                let color = if String.IsNullOrWhiteSpace(s.ColorAtmosphere) then "Neon Emerald" else s.ColorAtmosphere
                let motif = if String.IsNullOrWhiteSpace(s.GeometricMotif) then "Orbital Spheres" else s.GeometricMotif
                let lang = if String.IsNullOrWhiteSpace(s.Language) then Localization.systemLanguage () else s.Language
                { s with
                    ColorAtmosphere = color
                    GeometricMotif = motif
                    Language = lang
                    SupportPromptVersion = (if isNull s.SupportPromptVersion then "" else s.SupportPromptVersion) }
            else defaultSettings ()
        with _ -> defaultSettings ()

    let saveSettings (settings: AppSettings) : unit =
        try
            let p = getSettingsFilePath ()
            let options = JsonSerializerOptions()
            options.WriteIndented <- true
            let json = JsonSerializer.Serialize(settings, options)
            File.WriteAllText(p, json)
        with _ -> ()

    // =========================================================================
    // MAIN UNIFIED ASYNCHRONOUS SCANNER
    // =========================================================================
    let scanAllGamesAsync (onProgress: string -> unit) : Async<GameItem list> =
        async {
            onProgress "Scanning Steam library..."
            do! Async.Sleep 350
            let steamGames = scanSteamGames ()

            onProgress "Scanning Epic Games..."
            do! Async.Sleep 300
            let epicGames = scanEpicGames ()

            onProgress "Scanning GOG Galaxy..."
            do! Async.Sleep 250
            let gogGames = scanGogGames ()

            onProgress "Scanning Repacks & Custom drives..."
            do! Async.Sleep 400
            let repackGames = scanRepackAndStandaloneGames ()

            onProgress "Optimizing and de-duplicating library..."
            let all = 
                let list = List<GameItem>()
                list.AddRange(steamGames)
                list.AddRange(epicGames)
                list.AddRange(gogGames)
                list.AddRange(repackGames)
                list |> Seq.toList

            let uniqueGames = 
                deduplicateGames all
                |> List.sortBy (fun g -> g.Title)

            saveGamesToCache uniqueGames
            onProgress (sprintf "%d Games Ready" uniqueGames.Length)
            return uniqueGames
        }
