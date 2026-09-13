namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.Diagnostics
open System.Text.RegularExpressions
open System.Collections.Generic

/// Resolves the *real* game executable inside an install folder and locates
/// the DLSS / NVIDIA Streamline runtime files that ship with the game.
module GameAnalyzer =

    // =====================================================================
    // FILE VERSION HELPERS
    // =====================================================================
    type FileVer =
        { Major: int
          Minor: int
          Build: int
          Revision: int }

    let zeroVer = { Major = 0; Minor = 0; Build = 0; Revision = 0 }

    let isZeroVer (v: FileVer) =
        v.Major = 0 && v.Minor = 0 && v.Build = 0 && v.Revision = 0

    /// Human readable form. DLSS uses "310.8", Streamline uses "2.13".
    let verText (v: FileVer) =
        if isZeroVer v then "unknown"
        elif v.Build = 0 && v.Revision = 0 then sprintf "%d.%d" v.Major v.Minor
        else sprintf "%d.%d.%d" v.Major v.Minor v.Build

    let compareVer (a: FileVer) (b: FileVer) =
        compare (a.Major, a.Minor, a.Build, a.Revision) (b.Major, b.Minor, b.Build, b.Revision)

    let readFileVersion (path: string) : FileVer =
        try
            if File.Exists(path) then
                let fvi = FileVersionInfo.GetVersionInfo(path)
                { Major = fvi.FileMajorPart
                  Minor = fvi.FileMinorPart
                  Build = fvi.FileBuildPart
                  Revision = fvi.FilePrivatePart }
            else
                zeroVer
        with _ ->
            zeroVer

    /// FileDescription (falls back to ProductName) - the single strongest
    /// signal for identifying which executable really is the game.
    let readDescription (path: string) : string =
        try
            let fvi = FileVersionInfo.GetVersionInfo(path)
            let d =
                if String.IsNullOrWhiteSpace(fvi.FileDescription) then fvi.ProductName
                else fvi.FileDescription

            if isNull d then "" else d.Trim()
        with _ ->
            ""

    // =====================================================================
    // TEXT SIMILARITY
    // =====================================================================
    let private stopWords =
        HashSet<string>(
            [| "the"; "of"; "a"; "an"; "and"; "edition"; "complete"; "deluxe"; "ultimate"; "definitive"
               "remastered"; "remake"; "goty"; "game"; "year"; "enhanced"; "directors"; "cut"; "anniversary"
               "special"; "gold"; "premium"; "standard"; "repack"; "win64"; "shipping"; "x64"; "win"; "exe" |],
            StringComparer.OrdinalIgnoreCase
        )

    let private normalize (s: string) =
        if String.IsNullOrWhiteSpace(s) then ""
        else Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]", "")

    let private tokenize (s: string) =
        if String.IsNullOrWhiteSpace(s) then
            [||]
        else
            Regex
                .Replace(s.ToLowerInvariant(), @"[^a-z0-9]+", " ")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            |> Array.filter (fun t -> not (stopWords.Contains(t)))

    /// 0.0 .. 1.0 - 1.0 means the two names are the same thing.
    let private similarity (a: string) (b: string) : float =
        let na = normalize a
        let nb = normalize b

        if na = "" || nb = "" then
            0.0
        elif na = nb then
            1.0
        else
            let ta = HashSet<string>(tokenize a, StringComparer.OrdinalIgnoreCase)
            let tb = HashSet<string>(tokenize b, StringComparer.OrdinalIgnoreCase)

            if ta.Count = 0 || tb.Count = 0 then
                if na.Contains(nb) || nb.Contains(na) then 0.5 else 0.0
            else
                let intersection = ta |> Seq.filter tb.Contains |> Seq.length
                let smaller = min ta.Count tb.Count
                let coverage = float intersection / float smaller

                if coverage >= 0.999 then 0.85
                elif coverage >= 0.6 then 0.6
                elif coverage > 0.0 then 0.3
                elif na.Contains(nb) || nb.Contains(na) then 0.45
                else 0.0

    // =====================================================================
    // EXCLUSION RULES
    // =====================================================================
    let private skippedDirNames =
        HashSet<string>(
            [| "_commonredist"; "commonredist"; "redist"; "redists"; "redistributable"; "redistributables"
               "directx"; "direct x"; "vcredist"; "vc_redist"; "vcred"; "dotnet"; "dotnetfx"; "mono"; "openal"
               "dependencies"; "prerequisites"; "prereq"; "installer"; "installers"; "__installer"
               "easyanticheat"; "easyanticheat_eos"; "battleye"; "denuvo"; "nprotect"; "xigncode3"; "vanguard"
               "anticheat"; "crashreporter"; "crashreportclient"; "shadercache"; "d3dscache"; "savedata"
               "saves"; "savegames"; "logs"; "screenshots"; "manual"; "manuals"; "soundtrack"; "artbook"
               "extras"; "bonus"; "documentation"; "docs"; "support"; "tools"; "sdk"; "mods"; "dlc"
               "backup"; "backups"; "_backup"; "dlss5_backup" |],
            StringComparer.OrdinalIgnoreCase
        )

    /// Unreal ships engine-side tools under Engine\Binaries - never the game.
    let private isSkippedDirPath (fullPath: string) =
        let p = fullPath.Replace('\\', '/').ToLowerInvariant()
        p.Contains("/engine/binaries/")
        || p.Contains("/engine/extras/")
        || p.Contains("/engine/plugins/")

    let private isSkippedDir (dirPath: string) =
        let name = Path.GetFileName(dirPath)
        skippedDirNames.Contains(name) || isSkippedDirPath dirPath

    let private excludedExeNames =
        HashSet<string>(
            [| "steam.exe"; "steamservice.exe"; "steamwebhelper.exe"; "steamerrorreporter.exe"
               "epicgameslauncher.exe"; "epicwebhelper.exe"; "gog_galaxy.exe"; "galaxyclient.exe"
               "origin.exe"; "eadesktop.exe"; "ubisoftconnect.exe"; "upc.exe"; "battle.net.exe"; "uplay.exe"
               "rockstargameslauncher.exe"; "rockstar-games-launcher.exe"; "rockstar-games-epic.exe"
               "social-club-setup.exe"; "playgtav.exe"; "redprelauncher.exe"; "redistributableuninstaller.exe"
               "dxsetup.exe"; "dxwebsetup.exe"; "oalinst.exe"; "createdump.exe"; "7za.exe"; "7z.exe"
               "crashreporter.exe"; "crashreportclient.exe"; "crashreportclient-win64-shipping.exe"
               "unrealcecommon.exe"; "bugsplat.exe"; "sendrpt.exe"; "werfault.exe"; "feedback.exe"
               "reporttool.exe"; "anticheatinstaller.exe"; "easyanticheat_setup.exe"; "easyanticheat.exe"
               "beservice.exe"; "beservice_x64.exe"; "bedaisy.exe"; "vanguard.exe"; "vconsole2.exe"
               "launcher.exe"; "launch.exe"; "play.exe"; "start.exe"; "gamelauncher.exe"; "config.exe"
               "settings.exe"; "configuration.exe"; "autorun.exe"; "patch.exe"; "updater.exe"
               "uninstaller.exe"; "uninstall.exe"; "unins000.exe"; "unins001.exe"; "notification_helper.exe"
               "quickstart.exe"; "activation.exe"; "cleanup.exe" |],
            StringComparer.OrdinalIgnoreCase
        )

    let private excludedPrefixes =
        [| "vcredist"; "vc_redist"; "dotnet"; "ndp4"; "unins"; "setup_"; "install_"; "unitycrashhandler"
           "ue4prereqsetup"; "ueprereqsetup"; "directx"; "dxsetup"; "oalinst"; "steamsetup" |]

    let private excludedSuffixes =
        [| "_be.exe"; "-be.exe"; "-win64-debuggame.exe"; "-win64-test.exe"; "-win32-debuggame.exe"
           "editor.exe"; "-editor.exe"; "server.exe"; "-server.exe"; "dedicatedserver.exe"; "_dev.exe"
           "-cmd.exe"; "setup.exe"; "installer.exe"; "_legacy_app.exe"; "crashhandler.exe"
           "crashreporter.exe"; "helper.exe"; "_uninstall.exe" |]

    let private isExcludedExe (fileName: string) =
        let lower = fileName.ToLowerInvariant()

        if excludedExeNames.Contains(lower) then true
        elif excludedPrefixes |> Array.exists lower.StartsWith then true
        elif excludedSuffixes |> Array.exists lower.EndsWith then true
        else false

    /// Words that mark a binary as a helper rather than the game itself.
    let private launcherWords =
        [| "launcher"; "prelauncher"; "redirector"; "setup"; "installer"; "uninstall"; "updater"; "patcher"
           "crash"; "report"; "service"; "helper"; "console"; "config"; "settings"; "editor"; "server"
           "benchmark"; "anticheat"; "redistributable"; "dump"; "diagnostic"; "overlay"; "activation" |]

    let private isLauncherish (text: string) =
        if String.IsNullOrWhiteSpace(text) then
            false
        else
            let lower = text.ToLowerInvariant()
            launcherWords |> Array.exists lower.Contains

    // =====================================================================
    // DIRECTORY WALKER
    // =====================================================================
    let private walkDirectories (root: string) (maxDepth: int) : string list =
        let acc = List<string>()

        if Directory.Exists(root) then
            let queue = Queue<string * int>()
            queue.Enqueue((root, 0))

            while queue.Count > 0 do
                let (dir, depth) = queue.Dequeue()
                acc.Add(dir)

                if depth < maxDepth then
                    let subs =
                        try Directory.GetDirectories(dir)
                        with _ -> [||]

                    for sub in subs do
                        if not (isSkippedDir sub) then queue.Enqueue((sub, depth + 1))

        List.ofSeq acc

    // =====================================================================
    // EXECUTABLE RESOLUTION
    // =====================================================================
    /// Per-folder signals (Steam API next to it, engine DLLs, ...) cached so
    /// that a folder with 20 executables is only inspected once.
    let private folderBonus (cache: Dictionary<string, float>) (dir: string) : float =
        match cache.TryGetValue(dir) with
        | true, v -> v
        | _ ->
            let mutable bonus = 0.0

            try
                let files = Directory.GetFiles(dir)

                let names =
                    HashSet<string>(files |> Array.map Path.GetFileName, StringComparer.OrdinalIgnoreCase)

                let dllCount =
                    files
                    |> Array.filter (fun f -> f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    |> Array.length

                if names.Contains("steam_api64.dll") || names.Contains("steam_api.dll") then
                    bonus <- bonus + 6000.0

                if names.Contains("EOSSDK-Win64-Shipping.dll") || names.Contains("GalaxyPeer64.dll") then
                    bonus <- bonus + 3000.0

                if names.Contains("nvngx_dlss.dll")
                   || names.Contains("sl.interposer.dll")
                   || names.Contains("nvngx_dlssg.dll") then
                    bonus <- bonus + 5000.0

                if dllCount >= 8 then bonus <- bonus + 2500.0
                elif dllCount >= 3 then bonus <- bonus + 1000.0
            with _ ->
                ()

            cache.[dir] <- bonus
            bonus

    let private scoreExecutable
        (gameTitle: string)
        (root: string)
        (file: FileInfo)
        (cache: Dictionary<string, float>)
        : float =

        let name = file.Name
        let lower = name.ToLowerInvariant()
        let nameNoExt = Path.GetFileNameWithoutExtension(name)
        let dir = file.DirectoryName

        let relative =
            let full = file.FullName
            if full.StartsWith(root, StringComparison.OrdinalIgnoreCase) then
                full.Substring(root.Length).TrimStart('\\', '/')
            else
                name

        let relLower = "/" + relative.Replace('\\', '/').ToLowerInvariant()
        let description = readDescription file.FullName

        let mutable score = 0.0

        // 1. Version-resource description vs. the library title (dominant signal)
        score <- score + (similarity description gameTitle) * 50000.0

        // 2. File name vs. the library title
        score <- score + (similarity nameNoExt gameTitle) * 20000.0

        // 3. Helper / launcher binaries are actively pushed down
        if isLauncherish description then score <- score - 25000.0
        if isLauncherish nameNoExt then score <- score - 15000.0

        // 4. Engine specific layouts
        if relLower.Contains("/binaries/win64/") then
            if lower.EndsWith("-win64-shipping.exe") then score <- score + 25000.0
            else score <- score + 12000.0
        elif relLower.Contains("/bin/x64_dx12/") then
            // RED Engine ships a DX11 and a DX12 build; DLSS lives in the DX12 one.
            score <- score + 14000.0
        elif relLower.Contains("/bin/x64/")
             || relLower.Contains("/bin/win64/")
             || relLower.Contains("/binaries/") then
            score <- score + 11500.0
        elif relLower.Contains("/x64/") || relLower.Contains("/win64/") then
            score <- score + 7000.0

        // 5. Depth: root executables are usually the game, deep ones usually are not
        let depth = relative.Replace('\\', '/').Split('/').Length - 1
        if depth = 0 then score <- score + 6000.0
        else score <- score - 800.0 * float (max 0 (depth - 1))

        // 6. Folder context
        score <- score + folderBonus cache dir

        // 7. Unity: <Name>.exe always sits next to <Name>_Data
        try
            if Directory.Exists(Path.Combine(dir, nameNoExt + "_Data")) then
                score <- score + 15000.0
        with _ ->
            ()

        // 8. Size, capped so a huge installer cannot outrank the real game
        score <- score + (min (float file.Length / 1048576.0) 400.0) * 20.0

        // 9. Repack crack folders hold a duplicate of the game exe
        if Regex.IsMatch(relLower, @"crack|razor1911|codex|plaza|skidrow|empress|goldberg|steamless") then
            score <- score - 9000.0

        score

    /// Returns (executablePath, sizeInBytes). Empty path when nothing sensible was found.
    let resolveGameExecutable (gameRoot: string) (gameTitle: string) : string * int64 =
        if String.IsNullOrWhiteSpace(gameRoot) || not (Directory.Exists(gameRoot)) then
            ("", 0L)
        else
            try
                let root = gameRoot.TrimEnd('\\', '/')
                let cache = Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
                let candidates = List<string * int64 * float>()

                for dir in walkDirectories root 5 do
                    let files =
                        try DirectoryInfo(dir).GetFiles("*.exe")
                        with _ -> [||]

                    for file in files do
                        // Tiny stubs are never the game.
                        if not (isExcludedExe file.Name) && file.Length > 40960L then
                            let score = scoreExecutable gameTitle root file cache
                            candidates.Add((file.FullName, file.Length, score))

                if candidates.Count = 0 then
                    ("", 0L)
                else
                    let (bestPath, bestSize, _) =
                        candidates |> Seq.maxBy (fun (_, _, score) -> score)

                    (bestPath, bestSize)
            with _ ->
                ("", 0L)

    /// True when an executable is a launcher / helper stub rather than the game.
    /// Store manifests (Epic, GOG) often point at one of these, and mods must
    /// never be installed next to them.
    let isLikelyLauncherExe (exePath: string) : bool =
        try
            if String.IsNullOrWhiteSpace(exePath) || not (File.Exists(exePath)) then
                true
            else
                let name = Path.GetFileName(exePath)

                isExcludedExe name
                || isLauncherish (Path.GetFileNameWithoutExtension(name))
                || isLauncherish (readDescription exePath)
        with _ ->
            true

    // =====================================================================
    // DLSS / STREAMLINE DISCOVERY
    // =====================================================================
    let dlssFileNames =
        [| "nvngx_dlss.dll"; "nvngx_dlssg.dll"; "nvngx_dlssd.dll" |]

    let streamlineFileNames =
        [| "sl.interposer.dll"; "sl.common.dll"; "sl.dlss.dll"; "sl.dlss_g.dll"; "sl.dlss_nr.dll"
           "sl.nis.dll"; "sl.pcl.dll"; "sl.reflex.dll" |]

    let dlssnrFileName = "nvngx_dlssnr.dll"

    type ModFile =
        { Name: string
          Path: string
          Version: FileVer }

    type ModFolder =
        { Directory: string
          Files: ModFile list }

    /// Highest version among the files of a folder - represents the folder's runtime version.
    let folderVersion (folder: ModFolder) : FileVer =
        folder.Files
        |> List.fold (fun acc f -> if compareVer f.Version acc > 0 then f.Version else acc) zeroVer

    let findModFoldersDepth (root: string) (targetNames: string[]) (maxDepth: int) : ModFolder list =
        if String.IsNullOrWhiteSpace(root) || not (Directory.Exists(root)) then
            []
        else
            let wanted = HashSet<string>(targetNames, StringComparer.OrdinalIgnoreCase)

            [ for dir in walkDirectories (root.TrimEnd('\\', '/')) maxDepth do
                let files =
                    try Directory.GetFiles(dir, "*.dll")
                    with _ -> [||]

                let hits =
                    files
                    |> Array.filter (fun f -> wanted.Contains(Path.GetFileName(f)))
                    |> Array.map (fun f ->
                        { Name = Path.GetFileName(f)
                          Path = f
                          Version = readFileVersion f })
                    |> Array.toList

                if not hits.IsEmpty then
                    yield { Directory = dir; Files = hits } ]

    let findModFolders (root: string) (targetNames: string[]) : ModFolder list =
        findModFoldersDepth root targetNames 6

    /// Search roots: the install directory plus the executable's own folder
    /// (repacks sometimes put the runtime next to the exe only).
    let searchRoots (installDir: string) (exePath: string) : string list =
        let roots = List<string>()

        if not (String.IsNullOrWhiteSpace(installDir)) && Directory.Exists(installDir) then
            roots.Add(installDir.TrimEnd('\\', '/'))

        if not (String.IsNullOrWhiteSpace(exePath)) then
            let exeDir =
                try Path.GetDirectoryName(exePath)
                with _ -> ""

            if not (String.IsNullOrWhiteSpace(exeDir)) && Directory.Exists(exeDir) then
                let normalized = exeDir.TrimEnd('\\', '/')

                let alreadyCovered =
                    roots
                    |> Seq.exists (fun r -> normalized.StartsWith(r, StringComparison.OrdinalIgnoreCase))

                if not alreadyCovered then roots.Add(normalized)

        List.ofSeq roots

    let findDlssFolders (installDir: string) (exePath: string) : ModFolder list =
        searchRoots installDir exePath
        |> List.collect (fun r -> findModFolders r dlssFileNames)
        |> List.distinctBy (fun f -> f.Directory.ToLowerInvariant())

    let findStreamlineFolders (installDir: string) (exePath: string) : ModFolder list =
        searchRoots installDir exePath
        |> List.collect (fun r -> findModFolders r streamlineFileNames)
        |> List.distinctBy (fun f -> f.Directory.ToLowerInvariant())

    // =====================================================================
    // GRAPHICS API & ARCHITECTURE
    // =====================================================================
    /// The runtime files each Direct3D generation ships with, newest first -
    /// a title that carries DX12's files is a DX12 title even if older
    /// libraries are lying around next to it.
    let private apiMarkers =
        [ "dx12",
          [| "d3d12.dll"; "D3D12Core.dll"; "dxcompiler.dll"; "dxil.dll"; "d3d12SDKLayers.dll" |]

          "dx11", [| "d3d11.dll"; "d3dx11_42.dll"; "d3dx11_43.dll" |]

          "dx10",
          Array.append
              [| "d3d10.dll"; "d3d10_1.dll"; "d3d10core.dll"; "d3dx10.dll" |]
              [| for v in 33..43 -> sprintf "d3dx10_%d.dll" v |]

          "dx9", Array.append [| "d3d9.dll" |] [| for v in 24..43 -> sprintf "d3dx9_%d.dll" v |] ]

    /// Files the mod itself puts next to a game. d3d9/d3d11/d3d12.dll are all
    /// names a wrapper takes over, so one of ours must never be read back as
    /// evidence of what the game was written against.
    let private isModWrapper (path: string) =
        try
            let fvi = FileVersionInfo.GetVersionInfo(path)
            let product = if isNull fvi.ProductName then "" else fvi.ProductName
            let original = if isNull fvi.OriginalFilename then "" else fvi.OriginalFilename

            product.Contains("ReShade")
            || product.Contains("dgVoodoo")
            || original.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase)
        with _ ->
            false

    /// Folders the installer creates. Their contents describe the mod, not the game.
    let private modOwnedDirs =
        HashSet<string>([ "host64"; "optiscaler"; "reshade-shaders" ], StringComparer.OrdinalIgnoreCase)

    /// Every filename that says anything about the API, flattened once.
    let private apiMarkerNames =
        HashSet<string>(apiMarkers |> List.collect (snd >> Array.toList), StringComparer.OrdinalIgnoreCase)

    /// The only names a wrapper can take over. Reading a version resource costs
    /// real time, so it is spent on these few and on nothing else - a game
    /// folder can hold hundreds of DLLs and none of the rest are ambiguous.
    let private wrapperProneNames =
        HashSet<string>(
            [ "d3d9.dll"; "d3d10.dll"; "d3d11.dll"; "d3d12.dll"
              "dxgi.dll"; "winmm.dll"; "version.dll"; "dbghelp.dll" ],
            StringComparer.OrdinalIgnoreCase
        )

    /// "dx12" / "dx11" / "dx10" / "dx9", or "" when nothing gives it away.
    /// Looks beside the executable and one level down, which is where the
    /// Agility SDK keeps D3D12Core.dll.
    let detectGraphicsApi (exePath: string) : string =
        try
            if String.IsNullOrWhiteSpace(exePath) then
                ""
            else
                let exeDir = Path.GetDirectoryName(exePath)

                if String.IsNullOrWhiteSpace(exeDir) || not (Directory.Exists(exeDir)) then
                    ""
                else
                    let dirs =
                        [ yield exeDir
                          yield!
                              (try
                                  Directory.GetDirectories(exeDir)
                                  |> Array.filter (fun d -> not (modOwnedDirs.Contains(Path.GetFileName(d))))
                                  |> Array.toList
                               with _ ->
                                   []) ]

                    let present = HashSet<string>(StringComparer.OrdinalIgnoreCase)

                    for dir in dirs do
                        try
                            for file in Directory.GetFiles(dir, "*.dll") do
                                let name = Path.GetFileName(file)

                                // Only marker files matter, and only the handful
                                // a wrapper could be squatting on need checking.
                                if apiMarkerNames.Contains(name)
                                   && (not (wrapperProneNames.Contains(name)) || not (isModWrapper file)) then
                                    present.Add(name) |> ignore
                        with _ ->
                            ()

                    apiMarkers
                    |> List.tryFind (fun (_, markers) -> markers |> Array.exists present.Contains)
                    |> Option.map fst
                    |> Option.defaultValue ""
        with _ ->
            ""

    /// "32" / "64" straight from the COFF header's machine type - four bytes
    /// read off the front of the file, no loading and no guessing.
    let detectArchitecture (exePath: string) : string =
        try
            use stream = File.OpenRead(exePath)
            use reader = new BinaryReader(stream)

            // "MZ", then the PE header offset lives at 0x3C.
            if reader.ReadUInt16() <> 0x5A4Dus then
                ""
            else
                stream.Position <- 0x3CL
                let peOffset = int64 (reader.ReadInt32())

                if peOffset <= 0L || peOffset + 6L > stream.Length then
                    ""
                else
                    stream.Position <- peOffset

                    if reader.ReadUInt32() <> 0x00004550u then // "PE\0\0"
                        ""
                    else
                        match reader.ReadUInt16() with
                        | 0x014Cus -> "32"
                        | 0x8664us
                        | 0xAA64us -> "64"
                        | _ -> ""
        with _ ->
            ""

    /// dxgi covers D3D10/11/12, which is what every DLSS title uses.
    let detectReShadeApi (exePath: string) : string =
        try
            let dir = Path.GetDirectoryName(exePath)

            if File.Exists(Path.Combine(dir, "vulkan-1.dll")) then "vulkan"
            elif File.Exists(Path.Combine(dir, "opengl32.dll")) then "opengl"
            else "dxgi"
        with _ ->
            "dxgi"

    let isReShadeInstalled (exePath: string) : bool =
        try
            let dir = Path.GetDirectoryName(exePath)

            [ "dxgi.dll"; "d3d11.dll"; "d3d12.dll"; "opengl32.dll" ]
            |> List.exists (fun n ->
                let p = Path.Combine(dir, n)

                File.Exists(p)
                && (let fvi = FileVersionInfo.GetVersionInfo(p)
                    not (isNull fvi.ProductName) && fvi.ProductName.Contains("ReShade")))
        with _ ->
            false
