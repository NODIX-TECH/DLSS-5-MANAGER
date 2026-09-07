namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.Diagnostics
open System.Text.Json
open System.Text.RegularExpressions
open System.Security.Cryptography
open System.Text
open System.Collections.Generic
open DLSS_5_MANAGER.Models
open DLSS_5_MANAGER.Services.GameAnalyzer

/// Performs the full DLSS 5 installation:
///   1. ReShade (headless) next to the real game executable
///   2. renodx-dlss5.addon64 next to ReShade
///   3. NVIDIA Streamline refresh   (only when the game ships >= 2.4)
///   4. NVIDIA DLSS refresh         (only when the game ships an older build)
///   5. nvngx_dlssnr.dll placed next to ReShade, DLSS and Streamline
/// Every file it overwrites is backed up first so the whole thing is reversible.
module ModInstaller =

    /// Streamline builds older than this predate the SL2 interface and must not be swapped.
    let minimumStreamlineVersion =
        { Major = 2; Minor = 4; Build = 0; Revision = 0 }

    // =====================================================================
    // MANIFEST MODEL
    // =====================================================================
    [<CLIMutable>]
    type InstalledFile =
        { TargetPath: string
          BackupPath: string
          WasExisting: bool }

    [<CLIMutable>]
    type InstallManifest =
        { GameId: string
          GameTitle: string
          ExecutablePath: string
          InstalledAtUtc: string
          /// Which install route produced this manifest, so the uninstaller
          /// knows whether OptiScaler has to be unwound first.
          Mode: string
          /// "64" / "32", empty on manifests written before 1.1.0 - those were
          /// all 64-bit, which is what an empty value is read as.
          Arch: string
          /// OptiScaler only: "dx12" / "vulkan". Empty on older manifests.
          Api: string
          Files: InstalledFile[] }

    /// The mutually exclusive install routes offered in the Manage sheet.
    type InstallMode =
        /// DX12 + OptiScaler - the recommended default.
        | OptiScalerMode
        /// ReShade + RenoDX, DirectX 12 titles.
        | Dx12Auto
        /// ReShade + RenoDX plus the bundled DX11 effect payload.
        | Dx11
        /// DirectX 9 titles: ReShade is installed on the d3d9 slot and then
        /// moved aside so dgVoodoo can take it.
        | Dx9
        /// Emulators: ReShade on Vulkan plus the emulator payload. No RenoDX,
        /// no Streamline - the emulator is the renderer, not the game.
        | Emulator
        /// AMD RDNA 4: a self-contained payload and the ray reconstruction
        /// model, nothing else. Beta.
        | AmdMode

    /// Which build of the mod runs in the game's process. A 32-bit game cannot
    /// load the 64-bit add-ons, so those move into a side-by-side host.
    type InstallArch =
        | Bit64
        | Bit32

    /// Which API the game renders with, on the OptiScaler route. The files
    /// deployed are identical either way - it only decides which proxy library
    /// OptiScaler takes over, because a Vulkan title never loads dxgi.dll.
    type OptiScalerApi =
        | OptiDx12
        | OptiVulkan

    let optiApiKey (api: OptiScalerApi) =
        match api with
        | OptiDx12 -> "dx12"
        | OptiVulkan -> "vulkan"

    let modeKey (mode: InstallMode) =
        match mode with
        | OptiScalerMode -> "optiscaler"
        | Dx12Auto -> "dx12"
        | Dx11 -> "dx11"
        | Dx9 -> "dx9"
        | Emulator -> "emulator"
        | AmdMode -> "amd"

    let archKey (arch: InstallArch) =
        match arch with
        | Bit64 -> "64"
        | Bit32 -> "32"

    /// Only the DX11 and DX9 routes come in two builds; the other two are
    /// 64-bit by definition, so their manifests never disagree on it.
    let archMatters (mode: InstallMode) =
        match mode with
        | Dx11
        | Dx9 -> true
        | _ -> false

    type Progress = string -> float -> unit

    type InstallOutcome =
        { Success: bool
          Message: string }

    /// Folder locations discovered during the library scan. Passing them in
    /// skips the directory walk entirely, so pressing Install acts immediately.
    type InstallPlan =
        { DlssDirs: string[]
          StreamlineDirs: string[] }

    /// Rebuilds folder descriptors from cached paths, re-reading the versions
    /// straight off disk so a stale cache can never cause a wrong decision.
    let private foldersFromPaths (dirs: string[]) (fileNames: string[]) : GameAnalyzer.ModFolder list =
        if isNull (box dirs) then
            []
        else
            dirs
            |> Array.filter Directory.Exists
            |> Array.map (fun dir ->
                let files =
                    fileNames
                    |> Array.map (fun n -> Path.Combine(dir, n))
                    |> Array.filter File.Exists
                    |> Array.map (fun p ->
                        let fileName = Path.GetFileName(p)

                        { GameAnalyzer.ModFile.Name = fileName
                          GameAnalyzer.ModFile.Path = p
                          GameAnalyzer.ModFile.Version = readFileVersion p })
                    |> Array.toList

                { GameAnalyzer.ModFolder.Directory = dir
                  GameAnalyzer.ModFolder.Files = files })
            |> Array.filter (fun f -> not f.Files.IsEmpty)
            |> Array.toList

    // =====================================================================
    // PATHS
    // =====================================================================
    let private appDataRoot () =
        let p =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS5Manager")

        Directory.CreateDirectory(p) |> ignore
        p

    /// "mod files" normally sits next to the executable. During development the
    /// binary runs from bin\Debug\net8.0, so walk up a few levels as a fallback.
    let modFilesRoot () =
        let direct = Path.Combine(AppContext.BaseDirectory, "mod files")

        if Directory.Exists(direct) then
            direct
        else
            let mutable current = DirectoryInfo(AppContext.BaseDirectory)
            let mutable found = ""
            let mutable hops = 0

            while found = "" && not (isNull (box current)) && hops < 6 do
                let candidate = Path.Combine(current.FullName, "mod files")
                if Directory.Exists(candidate) then found <- candidate
                current <- current.Parent
                hops <- hops + 1

            found

    let private safeId (game: GameItem) =
        let raw =
            if String.IsNullOrWhiteSpace(game.AppId) then game.Title else game.AppId

        let cleaned = Regex.Replace(raw, @"[^A-Za-z0-9_\-]", "_")
        if cleaned.Length > 80 then cleaned.Substring(0, 80) else cleaned

    let private manifestPath (game: GameItem) =
        let dir = Path.Combine(appDataRoot (), "Installs")
        Directory.CreateDirectory(dir) |> ignore
        Path.Combine(dir, safeId game + ".json")

    let private backupRoot (game: GameItem) =
        let dir = Path.Combine(appDataRoot (), "Backups", safeId game)
        Directory.CreateDirectory(dir) |> ignore
        dir

    let isInstalled (game: GameItem) = File.Exists(manifestPath game)

    /// The app no longer demands elevation just to start, so a folder Windows
    /// protects - anything under Program Files - has to be found out here and
    /// reported plainly, rather than failing halfway through with an
    /// access-denied exception.
    let private canWriteTo (dir: string) =
        try
            if String.IsNullOrWhiteSpace(dir) || not (Directory.Exists(dir)) then
                false
            else
                let probe = Path.Combine(dir, ".dlss5manager-write-test")
                use _ = File.Create(probe, 1, FileOptions.DeleteOnClose)
                true
        with _ ->
            false

    let private elevationNeededMessage =
        "Windows will not let the app write to this game's folder. Close DLSS 5 MANAGER, right-click it and choose \"Run as administrator\", then try again."

    let private shortHash (text: string) =
        use sha = SHA256.Create()
        let bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToLowerInvariant()))
        BitConverter.ToString(bytes).Replace("-", "").Substring(0, 10).ToLowerInvariant()

    // =====================================================================
    // FILE OPERATIONS (always backed up)
    // =====================================================================
    /// Tracks every file the installer touches.
    ///
    /// `known` is seeded from a previous manifest, so repairing an install never
    /// mistakes our own already-deployed file for an original game file - which
    /// would otherwise poison the backup and break uninstall.
    type private Tracker(backupDir: string, prior: InstalledFile[]) =
        let entries = Dictionary<string, InstalledFile>(StringComparer.OrdinalIgnoreCase)
        let addedThisRun = List<InstalledFile>()

        do
            if not (isNull (box prior)) then
                for entry in prior do
                    if not (String.IsNullOrWhiteSpace(entry.TargetPath)) then
                        entries.[entry.TargetPath] <- entry

        member _.Entries = entries.Values |> Seq.toArray
        member _.AddedThisRun = addedThisRun

        /// Remembers a file created by an external tool (the ReShade setup).
        member _.Record(target: string, existedBefore: bool, backupPath: string) =
            if not (entries.ContainsKey(target)) then
                let entry =
                    { TargetPath = target
                      BackupPath = backupPath
                      WasExisting = existedBefore }

                entries.[target] <- entry
                addedThisRun.Add(entry)

        /// Copies one of our payload files over a target, backing up an original once.
        member this.Copy(source: string, target: string) =
            if entries.ContainsKey(target) then
                // Already ours from an earlier run - overwrite, keep the original backup.
                Directory.CreateDirectory(Path.GetDirectoryName(target)) |> ignore
                File.Copy(source, target, true)
            else
                let wasExisting = File.Exists(target)
                let mutable backupPath = ""

                if wasExisting then
                    // Flat backup names keep us clear of the 260 character path limit.
                    backupPath <- Path.Combine(backupDir, shortHash target + "_" + Path.GetFileName(target))

                    if not (File.Exists(backupPath)) then
                        File.Copy(target, backupPath, false)

                Directory.CreateDirectory(Path.GetDirectoryName(target)) |> ignore
                File.Copy(source, target, true)
                this.Record(target, wasExisting, backupPath)

    // =====================================================================
    // DLSS 5 PRESENCE & COMPLETENESS
    // =====================================================================
    let renodxAddonName = "renodx-dlss5.addon64"

    /// Ships next to the RenoDX add-on and must always travel with it.
    let feedAddonName = "dlss5-feed.addon64"

    /// The 32-bit build of the feed add-on. It is the only one a 32-bit game
    /// can load, so it replaces all three 64-bit files next to the executable.
    let feedAddon32Name = "dlss5-feed.addon32"

    /// Everything 64-bit a 32-bit game still needs lives in this folder and is
    /// driven out-of-process by dlss5-feed-host64.exe.
    let bit32PayloadDirName = "if 32 bit"
    let host64DirName = "host64"

    /// dgVoodoo: translates the game's Direct3D 9 calls to a modern API.
    let dx9PayloadDirName = "if dx9"

    /// Everything an emulator needs on top of the shared payload.
    let emulatorPayloadDirName = "if emulator"

    /// AMD RDNA 4: everything that route needs, and it needs nothing else.
    let amdPayloadDirName = "if amd"

    /// The proxy names the AMD payload can load from, in order of preference.
    let amdSlots = [| "version.dll"; "dxgi.dll"; "winmm.dll" |]

    /// Files the mod writes itself the first time the game runs. They exist
    /// only because we were there, so removal has to take them too - the
    /// manifest cannot know about them, it is written before the game starts.
    let runtimeLeftovers =
        [| "dlssnr_on_amd.log"; "dlss5-feed.cfg"; "OptiScaler.log"; "ReShade.log"; "dgVoodoo.log" |]

    /// Files that only ever exist because we put them there. Names the game
    /// could plausibly own - nvngx_dlss.dll, the proxy DLLs - are deliberately
    /// absent: those are restored from backups by the manifest instead.
    ///
    /// A manifest written by an older build can list different paths from the
    /// ones the current layout produces, so removal finishes with a sweep over
    /// this list. Anything already handled by the manifest is skipped.
    let exclusiveArtifacts =
        [| "dlssnr_on_amd.ini"; "dlssnr_on_amd.log"; "dlss5-feed.addon64"; "dlss5-feed.addon32"; "renodx-dlss5.addon64"
           "nvngx_dlssnr.dll"; "nvngx.dll_dlssnr.dll"
           "OptiScaler.ini"; "OptiScaler.log"; "Remove_OptiScaler.bat"; "setup_windows.bat"
           "dgVoodoo.conf"; "dgVoodooCpl.exe"
           "deep-fried-chicken-nvngx.dll"; "deep-fried-chicken.addon64"; "deep-fried-chicken.cfg" |]

    /// One complete effect set for every route - the standard package plus the
    /// DLSS 5 feed shaders - dropped into the game's own "reshade-shaders"
    /// folder so Shaders\ and Textures\ merge into place.
    let reshadeShadersDirName = "reshade-shaders"
    let standardEffectsDirName = "reshade-shaders"

    /// The ray reconstruction model lives in its own folder so the 165 MB file
    /// is not sitting loose among the small ones.
    let dlss5DirName = "dlss 5"

    let optiScalerDirName = "if OptiScaler"

    /// Exactly the menu OptiScaler's setup offers, in its own order. The first
    /// name the game does not already use is the one that cannot clash.
    let optiScalerSlots =
        [| "dxgi.dll"; "winmm.dll"; "version.dll"; "dbghelp.dll"
           "d3d12.dll"; "wininet.dll"; "winhttp.dll"; "OptiScaler.asi" |]

    /// A Vulkan title never loads dxgi.dll, so hooking it there does nothing.
    /// winmm.dll is the slot OptiScaler's own setup recommends for Vulkan, so
    /// it moves to the front and the rest of the order is unchanged.
    let optiScalerSlotsVulkan =
        Array.append [| "winmm.dll" |] (optiScalerSlots |> Array.filter (fun n -> n <> "winmm.dll"))

    // =====================================================================
    // USER-SUPPLIED PAYLOAD FILES
    // =====================================================================
    /// The three payload files a user may swap for a build of their own. The
    /// replacement overwrites the copy inside "mod files", so every later
    /// install deploys it; the factory copy is kept once so it can come back.
    /// The ReShade installer, whichever build is sitting in "mod files" - the
    /// user can drop a newer one in from Settings, so the name is never
    /// hard-coded. The newest by write time wins if several are present.
    let reShadeSetupPath () =
        let root = modFilesRoot ()

        if String.IsNullOrWhiteSpace(root) then
            ""
        else
            try
                Directory.GetFiles(root, "ReShade_Setup*.exe")
                |> Array.sortByDescending (fun p -> File.GetLastWriteTimeUtc(p))
                |> Array.tryHead
                |> Option.defaultValue ""
            with _ ->
                ""

    /// Stands in for the ReShade setup wherever a payload is named by string.
    let reShadeSetupKey = "ReShade Setup"

    /// Stands in for the whole "if OptiScaler" folder in the same way.
    let optiScalerKey = "OptiScaler"

    /// The filenames a route already puts next to the executable.
    ///
    /// An extra carrying one of these names would fight the payload for the
    /// same slot, so the settings page refuses to aim it at that route rather
    /// than letting the two quietly overwrite each other.
    let routeOwnedNames (routeKey: string) : Set<string> =
        let root = modFilesRoot ()

        let namesIn (relative: string list) =
            try
                let dir = List.fold (fun acc p -> Path.Combine(acc, p)) root relative

                if Directory.Exists(dir) then
                    Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                    |> Array.map Path.GetFileName
                    |> Set.ofArray
                else
                    Set.empty
            with _ ->
                Set.empty

        if String.IsNullOrWhiteSpace(root) then
            Set.empty
        else
            let model = Set.ofList [ dlssnrFileName ]
            let runtimes = namesIn [ "streamline_dlss" ]
            let effects = namesIn [ reshadeShadersDirName ]

            // Every route a ReShade install underlies shares these.
            let reshadeSide =
                Set.ofList
                    [ "dxgi.dll"; "d3d9.dll"; "d3d10.dll"; "d3d11.dll"; "d3d12.dll"; "opengl32.dll"
                      "vulkan-1.dll"; "ReShade.ini"; "ReShadePreset.ini"; "ReShade.log" ]

            let addons = Set.ofList [ renodxAddonName; feedAddonName; feedAddon32Name ]

            match routeKey with
            | "optiscaler" -> Set.unionMany [ model; runtimes; namesIn [ optiScalerDirName ] ]
            | "amd" -> Set.unionMany [ model; namesIn [ amdPayloadDirName ]; Set.ofArray amdSlots ]
            | "emulator" ->
                Set.unionMany [ model; runtimes; effects; reshadeSide; addons; namesIn [ emulatorPayloadDirName ] ]
            | "dx9" ->
                Set.unionMany
                    [ model; runtimes; effects; reshadeSide; addons; namesIn [ dx9PayloadDirName ]
                      namesIn [ bit32PayloadDirName; host64DirName ] ]
            | _ ->
                Set.unionMany
                    [ model; runtimes; effects; reshadeSide; addons; namesIn [ bit32PayloadDirName; host64DirName ] ]

    /// True when this extra would land on a name the route already owns.
    let conflictsWithRoute (fileNames: string seq) (routeKey: string) =
        let owned = routeOwnedNames routeKey

        fileNames
        |> Seq.exists (fun n -> owned |> Set.exists (fun o -> String.Equals(o, n, StringComparison.OrdinalIgnoreCase)))

    module Payload =

        let replaceableFiles =
            [| feedAddonName; feedAddon32Name; renodxAddonName; dlssnrFileName |]

        let private factoryDir () =
            let p = Path.Combine(appDataRoot (), "FactoryModFiles")
            Directory.CreateDirectory(p) |> ignore
            p

        /// Where each replaceable file actually sits inside "mod files".
        let livePath (fileName: string) =
            let root = modFilesRoot ()

            if String.IsNullOrWhiteSpace(root) then ""
            elif fileName.Equals(dlssnrFileName, StringComparison.OrdinalIgnoreCase) then
                Path.Combine(root, dlss5DirName, fileName)
            else
                Path.Combine(root, fileName)

        let factoryPath (fileName: string) = Path.Combine(factoryDir (), fileName)

        let isOverridden (fileName: string) = File.Exists(factoryPath fileName)

        /// Human readable state for the settings card.
        let describe (fileName: string) =
            let live = livePath fileName

            if String.IsNullOrWhiteSpace(live) || not (File.Exists(live)) then
                "Missing"
            else
                let ver = readFileVersion live
                let size = try (FileInfo(live)).Length / 1024L with _ -> 0L

                let tag =
                    if isZeroVer ver then sprintf "%d KB" size else sprintf "v%s" (verText ver)

                if isOverridden fileName then "Custom · " + tag else "Bundled · " + tag

        /// Copies the user's file over the bundled one, backing the original up once.
        let replaceWith (fileName: string) (sourcePath: string) : bool * string =
            try
                let live = livePath fileName

                if String.IsNullOrWhiteSpace(live) then
                    (false, "The \"mod files\" folder is missing next to the application.")
                elif String.IsNullOrWhiteSpace(sourcePath) || not (File.Exists(sourcePath)) then
                    (false, "The selected file no longer exists.")
                else
                    let factory = factoryPath fileName

                    if File.Exists(live) && not (File.Exists(factory)) then
                        File.Copy(live, factory, false)

                    Directory.CreateDirectory(Path.GetDirectoryName(live)) |> ignore
                    File.Copy(sourcePath, live, true)
                    (true, sprintf "%s replaced with your own copy." fileName)
            with ex ->
                (false, "Could not replace " + fileName + ": " + ex.Message)

        /// Puts the copy that shipped with the app back in place.
        let restore (fileName: string) : bool * string =
            try
                let live = livePath fileName
                let factory = factoryPath fileName

                if not (File.Exists(factory)) then
                    (false, fileName + " is already the bundled version.")
                else
                    File.Copy(factory, live, true)
                    File.Delete(factory)
                    (true, sprintf "%s restored to the bundled version." fileName)
            with ex ->
                (false, "Could not restore " + fileName + ": " + ex.Message)

        // -----------------------------------------------------------------
        // RESHADE SETUP
        // -----------------------------------------------------------------
        /// A newer ReShade build comes as a differently named executable, so
        /// the old one is retired rather than overwritten and the installer
        /// picks up whatever is there.
        let describeReShade () =
            let live = reShadeSetupPath ()

            if String.IsNullOrWhiteSpace(live) then
                "Missing"
            else
                let name = Path.GetFileNameWithoutExtension(live)
                if isOverridden reShadeSetupKey then "Custom · " + name else "Bundled · " + name

        let replaceReShade (sourcePath: string) : bool * string =
            try
                let root = modFilesRoot ()

                if String.IsNullOrWhiteSpace(root) then
                    (false, "The \"mod files\" folder is missing next to the application.")
                elif String.IsNullOrWhiteSpace(sourcePath) || not (File.Exists(sourcePath)) then
                    (false, "The selected file no longer exists.")
                else
                    let existing = reShadeSetupPath ()
                    let factory = factoryPath reShadeSetupKey

                    // Keep the shipped installer once, so Restore has something
                    // to put back after any number of updates.
                    if File.Exists(existing) && not (File.Exists(factory)) then
                        File.Copy(existing, factory, false)
                        File.WriteAllText(factory + ".name", Path.GetFileName(existing))

                    for old in Directory.GetFiles(root, "ReShade_Setup*.exe") do
                        try File.Delete(old) with _ -> ()

                    File.Copy(sourcePath, Path.Combine(root, Path.GetFileName(sourcePath)), true)
                    (true, "ReShade setup replaced with " + Path.GetFileName(sourcePath) + ".")
            with ex ->
                (false, "Could not replace the ReShade setup: " + ex.Message)

        let restoreReShade () : bool * string =
            try
                let root = modFilesRoot ()
                let factory = factoryPath reShadeSetupKey

                if not (File.Exists(factory)) then
                    (false, "The ReShade setup is already the bundled version.")
                else
                    let originalName =
                        try
                            let n = File.ReadAllText(factory + ".name").Trim()
                            if String.IsNullOrWhiteSpace(n) then "ReShade_Setup.exe" else n
                        with _ ->
                            "ReShade_Setup.exe"

                    for old in Directory.GetFiles(root, "ReShade_Setup*.exe") do
                        try File.Delete(old) with _ -> ()

                    File.Copy(factory, Path.Combine(root, originalName), true)
                    File.Delete(factory)
                    try File.Delete(factory + ".name") with _ -> ()
                    (true, "ReShade setup restored to the bundled version.")
            with ex ->
                (false, "Could not restore the ReShade setup: " + ex.Message)

        // -----------------------------------------------------------------
        // OPTISCALER FOLDER
        // -----------------------------------------------------------------
        /// OptiScaler ships as a folder, so updating it means swapping the
        /// whole payload. A folder without OptiScaler.dll is not OptiScaler.
        let describeOptiScaler () =
            let root = modFilesRoot ()

            if String.IsNullOrWhiteSpace(root) then
                "Missing"
            else
                let dll = Path.Combine(root, optiScalerDirName, "OptiScaler.dll")

                if not (File.Exists(dll)) then
                    "Missing"
                else
                    let ver = readFileVersion dll

                    let tag =
                        if isZeroVer ver then
                            sprintf "%d MB" ((try (FileInfo(dll)).Length with _ -> 0L) / 1048576L)
                        else
                            sprintf "v%s" (verText ver)

                    if isOverridden optiScalerKey then "Custom · " + tag else "Bundled · " + tag

        let private optiFactoryDir () =
            let p = Path.Combine(factoryDir (), "OptiScalerPayload")
            p

        let replaceOptiScaler (sourceDir: string) : bool * string =
            try
                let root = modFilesRoot ()

                if String.IsNullOrWhiteSpace(root) then
                    (false, "The \"mod files\" folder is missing next to the application.")
                elif String.IsNullOrWhiteSpace(sourceDir) || not (Directory.Exists(sourceDir)) then
                    (false, "That folder no longer exists.")
                elif not (File.Exists(Path.Combine(sourceDir, "OptiScaler.dll"))) then
                    (false, "That folder does not contain OptiScaler.dll - pick the folder OptiScaler was extracted into.")
                else
                    let live = Path.Combine(root, optiScalerDirName)
                    let factory = optiFactoryDir ()

                    // Set the shipped payload aside once.
                    if Directory.Exists(live) && not (Directory.Exists(factory)) then
                        Directory.CreateDirectory(factory) |> ignore

                        for src in Directory.GetFiles(live, "*", SearchOption.AllDirectories) do
                            let relative = src.Substring(live.Length).TrimStart('\\', '/')
                            let dst = Path.Combine(factory, relative)
                            Directory.CreateDirectory(Path.GetDirectoryName(dst)) |> ignore
                            File.Copy(src, dst, true)

                        File.WriteAllText(factoryPath optiScalerKey, "folder")

                    if Directory.Exists(live) then Directory.Delete(live, true)
                    Directory.CreateDirectory(live) |> ignore

                    let mutable count = 0

                    for src in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories) do
                        let relative = src.Substring(sourceDir.Length).TrimStart('\\', '/')
                        let dst = Path.Combine(live, relative)
                        Directory.CreateDirectory(Path.GetDirectoryName(dst)) |> ignore
                        File.Copy(src, dst, true)
                        count <- count + 1

                    (true, sprintf "OptiScaler updated (%d file(s))." count)
            with ex ->
                (false, "Could not update OptiScaler: " + ex.Message)

        // -----------------------------------------------------------------
        // AMD PAYLOAD
        // -----------------------------------------------------------------
        /// The AMD route has nothing to fall back on, so its files have no
        /// off switch - only the ability to be swapped for the user's own.
        /// Picking several at once replaces every one that matches by name.
        let amdKey = "AMD payload"

        let describeAmd () =
            let root = modFilesRoot ()

            if String.IsNullOrWhiteSpace(root) then
                "Missing"
            else
                let proxy = Path.Combine(root, amdPayloadDirName, "version.dll")

                if not (File.Exists(proxy)) then
                    "Missing"
                else
                    let ver = readFileVersion proxy

                    let tag =
                        if isZeroVer ver then
                            sprintf "%d KB" ((try (FileInfo(proxy)).Length with _ -> 0L) / 1024L)
                        else
                            sprintf "v%s" (verText ver)

                    if isOverridden amdKey then "Custom · " + tag else "Bundled · " + tag

        let private amdFactoryDir () = Path.Combine(factoryDir (), "AmdPayload")

        let replaceAmdFiles (sourcePaths: string list) : bool * string =
            try
                let root = modFilesRoot ()
                let live = Path.Combine(root, amdPayloadDirName)

                if String.IsNullOrWhiteSpace(root) || not (Directory.Exists(live)) then
                    (false, "The \"" + amdPayloadDirName + "\" payload is missing from \"mod files\".")
                else
                    let existing =
                        Directory.GetFiles(live, "*", SearchOption.AllDirectories)
                        |> Array.map (fun p -> Path.GetFileName(p), p)

                    // Set the shipped payload aside once, so Restore works
                    // however many times the user swaps a file.
                    let factory = amdFactoryDir ()

                    if not (Directory.Exists(factory)) then
                        Directory.CreateDirectory(factory) |> ignore

                        for (_, src) in existing do
                            File.Copy(src, Path.Combine(factory, Path.GetFileName(src)), true)

                        File.WriteAllText(factoryPath amdKey, "folder")

                    let mutable replaced = 0
                    let skipped = ResizeArray<string>()

                    for source in sourcePaths do
                        if File.Exists(source) then
                            let name = Path.GetFileName(source)

                            match
                                existing
                                |> Array.tryFind (fun (n, _) -> String.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                            with
                            | Some(_, target) ->
                                File.Copy(source, target, true)
                                replaced <- replaced + 1
                            | None -> skipped.Add(name)

                    if replaced = 0 then
                        (false, "None of those match a file in the AMD payload - the names have to be the same.")
                    elif skipped.Count > 0 then
                        (true, sprintf "%d AMD file(s) replaced. Ignored: %s" replaced (String.Join(", ", skipped)))
                    else
                        (true, sprintf "%d AMD file(s) replaced with your own." replaced)
            with ex ->
                (false, "Could not replace the AMD payload: " + ex.Message)

        let restoreAmd () : bool * string =
            try
                let root = modFilesRoot ()
                let factory = amdFactoryDir ()

                if not (Directory.Exists(factory)) then
                    (false, "The AMD payload is already the bundled version.")
                else
                    let live = Path.Combine(root, amdPayloadDirName)
                    Directory.CreateDirectory(live) |> ignore

                    for src in Directory.GetFiles(factory) do
                        File.Copy(src, Path.Combine(live, Path.GetFileName(src)), true)

                    Directory.Delete(factory, true)
                    try File.Delete(factoryPath amdKey) with _ -> ()
                    (true, "AMD payload restored to the bundled version.")
            with ex ->
                (false, "Could not restore the AMD payload: " + ex.Message)

        let restoreOptiScaler () : bool * string =
            try
                let root = modFilesRoot ()
                let factory = optiFactoryDir ()

                if not (Directory.Exists(factory)) then
                    (false, "OptiScaler is already the bundled version.")
                else
                    let live = Path.Combine(root, optiScalerDirName)
                    if Directory.Exists(live) then Directory.Delete(live, true)
                    Directory.CreateDirectory(live) |> ignore

                    for src in Directory.GetFiles(factory, "*", SearchOption.AllDirectories) do
                        let relative = src.Substring(factory.Length).TrimStart('\\', '/')
                        let dst = Path.Combine(live, relative)
                        Directory.CreateDirectory(Path.GetDirectoryName(dst)) |> ignore
                        File.Copy(src, dst, true)

                    Directory.Delete(factory, true)
                    try File.Delete(factoryPath optiScalerKey) with _ -> ()
                    (true, "OptiScaler restored to the bundled version.")
            with ex ->
                (false, "Could not restore OptiScaler: " + ex.Message)

    /// What the app can tell about a game just by looking at its files.
    /// `nvngx_dlssnr.dll` is the one file DLSS 5 cannot run without, so its
    /// presence alone means "DLSS 5 is already on this game".
    type Dlss5Status =
        { Present: bool
          Complete: bool
          Missing: string[]
          ManagedByApp: bool
          DlssnrLocations: string[] }

    let private ourVersionOf (relativePath: string) =
        let modRoot = modFilesRoot ()

        if String.IsNullOrWhiteSpace(modRoot) then zeroVer
        else readFileVersion (Path.Combine(modRoot, relativePath))

    /// Route and build a managed install used. Both are "" when this app did
    /// not install it; a manifest from before 1.1.0 reads back as 64-bit.
    let installedRouteAndArch (game: GameItem) : string * string =
        try
            let path = manifestPath game

            if File.Exists(path) then
                let options = JsonSerializerOptions()
                options.PropertyNameCaseInsensitive <- true
                let m = JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(path), options)
                let route = if isNull (box m.Mode) then "" else m.Mode
                let arch = if String.IsNullOrWhiteSpace(m.Arch) then "64" else m.Arch
                (route, (if route = "" then "" else arch))
            else
                ("", "")
        with _ ->
            ("", "")

    let installedMode (game: GameItem) : string = fst (installedRouteAndArch game)

    /// The API an OptiScaler install was set up for. "" when unknown, which an
    /// older manifest reads back as - those were all DirectX 12.
    let installedOptiApi (game: GameItem) : string =
        try
            let path = manifestPath game

            if File.Exists(path) then
                let options = JsonSerializerOptions()
                options.PropertyNameCaseInsensitive <- true
                let m = JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(path), options)
                if String.IsNullOrWhiteSpace(m.Api) then "dx12" else m.Api
            else
                ""
        with _ ->
            ""

    let inspect (game: GameItem) (exePath: string) (dlssDirs: string[]) (streamlineDirs: string[]) : Dlss5Status =
        let managed = isInstalled game

        if String.IsNullOrWhiteSpace(exePath) || not (File.Exists(exePath)) then
            { Present = false
              Complete = false
              Missing = [| "Game executable" |]
              ManagedByApp = managed
              DlssnrLocations = [||] }
        else
            let exeDir = Path.GetDirectoryName(exePath)
            let dlssDirs = if isNull (box dlssDirs) then [||] else dlssDirs
            let streamlineDirs = if isNull (box streamlineDirs) then [||] else streamlineDirs

            // Every folder that should end up holding the ray reconstruction model.
            let expectedDlssnrDirs =
                // host64 is where a 32-bit install keeps the model; on every
                // other route the folder simply does not exist.
                Array.concat [ [| exeDir; Path.Combine(exeDir, host64DirName) |]; dlssDirs; streamlineDirs ]
                |> Array.filter Directory.Exists
                |> Array.distinctBy (fun d -> d.TrimEnd('\\', '/').ToLowerInvariant())

            let dlssnrFound =
                expectedDlssnrDirs
                |> Array.filter (fun d -> File.Exists(Path.Combine(d, dlssnrFileName)))

            let present = dlssnrFound.Length > 0
            let missing = List<string>()

            let (installedRoute, installedArch) = installedRouteAndArch game

            // OptiScaler is its own route - ReShade and the RenoDX add-ons are
            // deliberately absent there, so they must not read as "missing".
            let isOptiScaler =
                installedRoute.Equals("optiscaler", StringComparison.OrdinalIgnoreCase)
                || File.Exists(Path.Combine(exeDir, "OptiScaler.ini"))

            // A 32-bit install lays out completely differently: one 32-bit
            // add-on beside the game, everything 64-bit inside host64.
            let is32BitInstall =
                not isOptiScaler
                && (installedArch = "32" || File.Exists(Path.Combine(exeDir, feedAddon32Name)))

            // Emulators hook ReShade through the Vulkan layer, which leaves no
            // DLL beside the executable for the shared detector to find.
            let isEmulator = installedRoute.Equals("emulator", StringComparison.OrdinalIgnoreCase)

            // AMD mode leaves only its own payload and the model behind.
            let isAmd = installedRoute.Equals("amd", StringComparison.OrdinalIgnoreCase)

            if isAmd then
                if not (amdSlots |> Array.exists (fun n -> File.Exists(Path.Combine(exeDir, n)))) then
                    missing.Add("AMD proxy library")
            elif isEmulator then
                if not (File.Exists(Path.Combine(exeDir, feedAddonName))) then
                    missing.Add("DLSS 5 feed add-on")
            elif isOptiScaler then
                let hooked =
                    optiScalerSlots
                    |> Array.exists (fun n -> File.Exists(Path.Combine(exeDir, n)))

                if not hooked then missing.Add("OptiScaler proxy library")
            elif is32BitInstall then
                if not (GameAnalyzer.isReShadeInstalled exePath) then missing.Add("ReShade")

                if not (File.Exists(Path.Combine(exeDir, feedAddon32Name))) then
                    missing.Add("32-bit DLSS 5 feed add-on")

                if not (File.Exists(Path.Combine(exeDir, host64DirName, "dlss5-feed-host64.exe"))) then
                    missing.Add("64-bit host")
            else
                if not (GameAnalyzer.isReShadeInstalled exePath) then missing.Add("ReShade")

                if not (File.Exists(Path.Combine(exeDir, renodxAddonName))) then
                    missing.Add("RenoDX DLSS 5 add-on")

                if not (File.Exists(Path.Combine(exeDir, feedAddonName))) then
                    missing.Add("DLSS 5 feed add-on")

            // OptiScaler only ever wants the model next to the executable, so a
            // "gap" in the other folders is expected there, not a fault.
            let dlssnrGaps =
                if isOptiScaler || isEmulator || isAmd then
                    if File.Exists(Path.Combine(exeDir, dlssnrFileName)) then 0 else 1
                elif is32BitInstall then
                    // The model belongs in host64 and nowhere else here.
                    if File.Exists(Path.Combine(exeDir, host64DirName, dlssnrFileName)) then 0 else 1
                else
                    expectedDlssnrDirs.Length - dlssnrFound.Length

            if dlssnrGaps > 0 then
                missing.Add(sprintf "Ray reconstruction model (%d location(s))" dlssnrGaps)

            // DLSS runtime must not be older than the one we ship.
            let dlssOutdated =
                not (isOptiScaler || isEmulator || isAmd)
                &&
                dlssDirs
                |> Array.filter Directory.Exists
                |> Array.exists (fun dir ->
                    GameAnalyzer.dlssFileNames
                    |> Array.exists (fun name ->
                        let target = Path.Combine(dir, name)
                        let ours = ourVersionOf (Path.Combine("streamline_dlss", "dlss", name))

                        File.Exists(target)
                        && not (isZeroVer ours)
                        && compareVer (readFileVersion target) ours < 0))

            if dlssOutdated then missing.Add("DLSS runtime update")

            // Streamline only counts when the game already ships a modern build.
            let ourStreamlineVer = ourVersionOf (Path.Combine("streamline_dlss", "streamline", "sl.interposer.dll"))

            let streamlineOutdated =
                not (isOptiScaler || isEmulator || isAmd)
                && streamlineDirs
                |> Array.filter Directory.Exists
                |> Array.exists (fun dir ->
                    let installed =
                        GameAnalyzer.streamlineFileNames
                        |> Array.map (fun n -> Path.Combine(dir, n))
                        |> Array.filter File.Exists
                        |> Array.map readFileVersion

                    if installed.Length = 0 then
                        false
                    else
                        let newest = installed |> Array.reduce (fun a b -> if compareVer a b >= 0 then a else b)

                        compareVer newest minimumStreamlineVersion >= 0
                        && compareVer ourStreamlineVer newest > 0)

            if streamlineOutdated then missing.Add("Streamline runtime update")

            { Present = present
              Complete = present && missing.Count = 0
              Missing = missing.ToArray()
              ManagedByApp = managed
              DlssnrLocations = dlssnrFound }

    // =====================================================================
    // OPTISCALER HOOK (native - no batch script)
    // =====================================================================
    /// True when this exact file is a ReShade build. Needed because ReShade can
    /// sit on a slot the shared detector does not look at - d3d9 on the DX9
    /// route, and dxgi once we have moved it there.
    let private isReShadeFile (path: string) =
        try
            File.Exists(path)
            && (let fvi = FileVersionInfo.GetVersionInfo(path)

                not (isNull fvi.ProductName) && fvi.ProductName.Contains("ReShade"))
        with _ ->
            false

    /// True when this file is an OptiScaler proxy left over from an earlier
    /// install: the rename keeps the original filename in the version resource.
    let private isOptiScalerProxy (path: string) =
        try
            File.Exists(path)
            && (let fvi = FileVersionInfo.GetVersionInfo(path)

                not (isNull fvi.OriginalFilename)
                && fvi.OriginalFilename.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase))
        with _ ->
            false

    /// Clears proxies a previous OptiScaler install left behind, so the new one
    /// cannot end up hooked twice under two different names.
    let private clearStaleOptiScalerHooks (dir: string) : int =
        let mutable cleared = 0

        for name in Array.append optiScalerSlots [| "OptiScaler.asi"; "Remove_OptiScaler.bat"; "Remove OptiScaler.bat" |] do
            let p = Path.Combine(dir, name)

            let removable =
                name.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) && File.Exists(p)
                || isOptiScalerProxy p

            if removable then
                try
                    File.Delete(p)
                    cleared <- cleared + 1
                with _ ->
                    ()

        cleared

    /// Picks the proxy name OptiScaler can take over without colliding with a
    /// file the game already ships - the same walk down the setup's own menu,
    /// starting from whichever slot suits the game's graphics API.
    let private pickOptiScalerSlot (dir: string) (useVulkan: bool) : string =
        let order = if useVulkan then optiScalerSlotsVulkan else optiScalerSlots

        order
        |> Array.tryFind (fun n -> not (File.Exists(Path.Combine(dir, n))))
        |> Option.defaultValue order.[0]

    // =====================================================================
    // STEP 1 - RESHADE
    // =====================================================================
    /// Drives the ReShade setup headless. It can take a while on a cold disk,
    /// so the elapsed time is reported the whole way through - a bar that never
    /// moves is indistinguishable from an app that has hung.
    let private runReShadeSetup (setupExe: string) (gameExe: string) (api: string) (report: Progress) : bool * string =
        try
            let psi = ProcessStartInfo()
            psi.FileName <- setupExe
            psi.Arguments <- sprintf "\"%s\" --api %s --headless" gameExe api
            psi.UseShellExecute <- false
            psi.CreateNoWindow <- true
            psi.WindowStyle <- ProcessWindowStyle.Hidden
            psi.WorkingDirectory <- Path.GetDirectoryName(setupExe)

            use proc = Process.Start(psi)
            let watch = Stopwatch.StartNew()
            let mutable finished = false

            while not finished && watch.Elapsed.TotalSeconds < 180.0 do
                finished <- proc.WaitForExit(350)

                if not finished then
                    // Creep from 10% to 18% over the first half minute so the
                    // bar keeps moving even while the setup is doing its work.
                    let fraction = min 1.0 (watch.Elapsed.TotalSeconds / 30.0)

                    report
                        (sprintf "Installing ReShade runtime... (%ds)" (int watch.Elapsed.TotalSeconds))
                        (0.10 + 0.08 * fraction)

            if not finished then
                try proc.Kill(true) with _ -> ()
                (false, "ReShade setup timed out")
            elif proc.ExitCode = 0 then
                (true, "")
            else
                (false, sprintf "ReShade setup returned exit code %d" proc.ExitCode)
        with ex ->
            (false, "ReShade setup failed: " + ex.Message)

    /// Copies a payload folder into a target folder keeping the tree shape, so
    /// "reshade-shaders\Shaders" merges straight into the game's own folder.
    let private copyTreeExcept (tracker: Tracker) (sourceDir: string) (targetDir: string) (skip: string[]) : int =
        let mutable count = 0

        if Directory.Exists(sourceDir) then
            let skipSet = HashSet<string>(skip, StringComparer.OrdinalIgnoreCase)

            for src in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories) do
                if not (skipSet.Contains(Path.GetFileName(src))) then
                    let relative = src.Substring(sourceDir.Length).TrimStart('\\', '/')
                    let dst = Path.Combine(targetDir, relative)
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)) |> ignore
                    tracker.Copy(src, dst)
                    count <- count + 1

        count

    let private copyTree (tracker: Tracker) (sourceDir: string) (targetDir: string) : int =
        copyTreeExcept tracker sourceDir targetDir [||]

    /// The ReShade setup exposes no command line switch for its effect
    /// packages, so ticking "Standard effects" by hand is not something the
    /// headless run can do. We deploy the very same package ourselves - the
    /// utility effects (DisplayDepth, UIMask, ...) bundled under
    /// "mod files\reshade-shaders" - and point ReShade.ini at it.
    let private ensureStandardEffectPaths (tracker: Tracker) (exeDir: string) : string * int =
        let shadersRoot = Path.Combine(exeDir, reshadeShadersDirName)
        Directory.CreateDirectory(Path.Combine(shadersRoot, "Shaders")) |> ignore
        Directory.CreateDirectory(Path.Combine(shadersRoot, "Textures")) |> ignore

        let standardEffects =
            let root = modFilesRoot ()

            if String.IsNullOrWhiteSpace(root) then 0
            else copyTree tracker (Path.Combine(root, standardEffectsDirName)) shadersRoot

        let ini = Path.Combine(exeDir, "ReShade.ini")

        let wanted =
            [ "EffectSearchPaths", ".\\" + reshadeShadersDirName + "\\Shaders\\**"
              "TextureSearchPaths", ".\\" + reshadeShadersDirName + "\\Textures\\**" ]

        try
            let startsWithKey (key: string) (line: string) =
                line.TrimStart().StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)

            let existing =
                if File.Exists(ini) then File.ReadAllLines(ini) |> List.ofArray else []

            let updated =
                wanted
                |> List.fold
                    (fun (lines: string list) (key, value) ->
                        if lines |> List.exists (startsWithKey key) then
                            lines
                            |> List.map (fun l -> if startsWithKey key l then key + "=" + value else l)
                        else
                            // Drop the key into [GENERAL], creating the section if needed.
                            let index =
                                lines
                                |> List.tryFindIndex (fun l -> l.Trim().Equals("[GENERAL]", StringComparison.OrdinalIgnoreCase))

                            match index with
                            | Some i -> List.truncate (i + 1) lines @ [ key + "=" + value ] @ List.skip (i + 1) lines
                            | None -> [ "[GENERAL]"; key + "=" + value ] @ lines)
                    existing

            File.WriteAllLines(ini, updated)
        with _ ->
            ()

        (shadersRoot, standardEffects)

    /// Copies a payload file only when the user has left it switched on.
    /// A file switched off is simply not deployed - it is not an error, and
    /// nothing else about the install changes.
    let private copyIfEnabled (tracker: Tracker) (payloadName: string) (source: string) (target: string) =
        if ExtrasStore.isPayloadEnabled payloadName && File.Exists(source) then
            tracker.Copy(source, target)
            true
        else
            false

    /// The user's own additions, deployed next to the executable at the end of
    /// every route. A folder keeps its name and goes in whole; a file lands
    /// beside the game. Anything switched off is skipped, and a source that
    /// has since been moved or deleted is passed over rather than failing the
    /// install that was otherwise fine.
    let private deployExtras
        (tracker: Tracker)
        (exeDir: string)
        (mode: InstallMode)
        (arch: InstallArch)
        (optiApi: OptiScalerApi)
        (report: Progress)
        (at: float)
        : int =

        let routeKey = modeKey mode

        // The exact combination, so an extra can be aimed at DX11 32-bit
        // without also landing on DX11 64-bit.
        let variantKey =
            match mode with
            | OptiScalerMode -> routeKey + "-" + optiApiKey optiApi
            | Dx11
            | Dx9 -> routeKey + "-" + archKey arch
            | _ -> routeKey

        let extras =
            ExtrasStore.list ()
            |> List.filter (fun e -> e.Enabled && ExtrasStore.appliesTo e [ routeKey; variantKey ])

        if extras.IsEmpty then
            0
        else
            report "Installing your extras..." at
            let mutable count = 0

            for extra in extras do
                try
                    if extra.IsFolder && Directory.Exists(extra.SourcePath) then
                        let targetDir = Path.Combine(exeDir, Path.GetFileName(extra.SourcePath.TrimEnd('\\', '/')))
                        count <- count + copyTree tracker extra.SourcePath targetDir
                    elif File.Exists(extra.SourcePath) then
                        tracker.Copy(extra.SourcePath, Path.Combine(exeDir, Path.GetFileName(extra.SourcePath)))
                        count <- count + 1
                with _ ->
                    ()

            count

    /// What a runtime deployment did, and where it found things.
    type private RuntimeResult =
        { DlssFolders: GameAnalyzer.ModFolder list
          StreamlineFolders: GameAnalyzer.ModFolder list
          Summary: string list }

    /// The NVIDIA runtimes, shared by every route since they all read from the
    /// one "streamline_dlss" folder.
    ///
    /// Streamline is only refreshed where the game already ships a build new
    /// enough to accept it - swapping it into an older title breaks that title.
    /// The DLSS runtime has no such constraint, so it is always deployed next
    /// to the executable, even when the game never shipped one.
    let private deployRuntimes
        (tracker: Tracker)
        (game: GameItem)
        (exePath: string)
        (exeDir: string)
        (modRoot: string)
        (plan: InstallPlan option)
        (report: Progress)
        (fromP: float)
        (span: float)
        : RuntimeResult =

        let at (fraction: float) = fromP + span * fraction
        let ourStreamlineDir = Path.Combine(modRoot, "streamline_dlss", "streamline")
        let ourDlssDir = Path.Combine(modRoot, "streamline_dlss", "dlss")

        // ---- Streamline ---------------------------------------------------
        report "Scanning for NVIDIA Streamline..." (at 0.0)

        let ourStreamlineFiles =
            if Directory.Exists(ourStreamlineDir) then Directory.GetFiles(ourStreamlineDir, "sl.*.dll")
            else [||]

        let ourStreamlineVer =
            if ourStreamlineFiles.Length > 0 then readFileVersion ourStreamlineFiles.[0] else zeroVer

        let streamlineFolders =
            match plan with
            | Some p -> foldersFromPaths p.StreamlineDirs streamlineFileNames
            | None -> findStreamlineFolders game.InstallDirectory exePath

        let mutable streamlineUpdated = 0
        let mutable streamlineSkipped = 0

        for folder in streamlineFolders do
            let gameVer = folderVersion folder

            let qualifies =
                compareVer gameVer minimumStreamlineVersion >= 0
                && compareVer ourStreamlineVer gameVer > 0

            if qualifies then
                report (sprintf "Updating Streamline %s -> %s..." (verText gameVer) (verText ourStreamlineVer)) (at 0.3)

                for src in ourStreamlineFiles do
                    tracker.Copy(src, Path.Combine(folder.Directory, Path.GetFileName(src)))

                streamlineUpdated <- streamlineUpdated + 1
            else
                streamlineSkipped <- streamlineSkipped + 1

        // ---- DLSS: upgrade in place, never downgrade ----------------------
        report "Scanning for NVIDIA DLSS runtime..." (at 0.5)

        let dlssFolders =
            match plan with
            | Some p -> foldersFromPaths p.DlssDirs dlssFileNames
            | None -> findDlssFolders game.InstallDirectory exePath

        let mutable dlssUpdated = 0
        let mutable dlssSkipped = 0

        for folder in dlssFolders do
            for modFile in folder.Files do
                let ourFile = Path.Combine(ourDlssDir, modFile.Name)

                if File.Exists(ourFile) then
                    let ourVer = readFileVersion ourFile

                    if compareVer modFile.Version ourVer < 0 then
                        report
                            (sprintf "Updating %s %s -> %s..." modFile.Name (verText modFile.Version) (verText ourVer))
                            (at 0.7)

                        tracker.Copy(ourFile, modFile.Path)
                        dlssUpdated <- dlssUpdated + 1
                    else
                        dlssSkipped <- dlssSkipped + 1

        // ---- DLSS next to the executable ----------------------------------
        report "Deploying DLSS runtime next to the game..." (at 0.85)
        let mutable dlssAdded = 0

        if Directory.Exists(ourDlssDir) then
            for src in Directory.GetFiles(ourDlssDir, "*.dll") do
                let target = Path.Combine(exeDir, Path.GetFileName(src))
                let ourVer = readFileVersion src

                let needed =
                    not (File.Exists(target)) || compareVer (readFileVersion target) ourVer < 0

                if needed then
                    tracker.Copy(src, target)
                    dlssAdded <- dlssAdded + 1

        { DlssFolders = dlssFolders
          StreamlineFolders = streamlineFolders
          Summary =
            [ (if streamlineUpdated > 0 then sprintf "Streamline updated in %d folder(s)" streamlineUpdated
               elif streamlineSkipped > 0 then "Streamline left untouched (incompatible or already newer)"
               else "No Streamline runtime found")
              (if dlssUpdated + dlssAdded > 0 then
                   sprintf "DLSS runtime deployed (%d file(s))" (dlssUpdated + dlssAdded)
               elif dlssSkipped > 0 then "DLSS runtime already up to date"
               else "No DLSS runtime found") ] }

    // =====================================================================
    // INSTALL
    // =====================================================================
    /// `mode` mirrors the switch in the Manage sheet. OptiScaler is its own
    /// self-contained route and never shares a folder with the ReShade ones;
    /// DX12 and DX11 differ only by the extra effect payload DX11 receives.
    let install
        (game: GameItem)
        (exePath: string)
        (plan: InstallPlan option)
        (mode: InstallMode)
        (arch: InstallArch)
        (optiApi: OptiScalerApi)
        (report: Progress)
        : InstallOutcome =
        // Carry forward what a previous run already recorded, so re-running the
        // installer to repair a broken install stays fully reversible.
        let priorEntries =
            try
                let path = manifestPath game

                if File.Exists(path) then
                    let options = JsonSerializerOptions()
                    options.PropertyNameCaseInsensitive <- true
                    let m = JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(path), options)
                    if isNull (box m.Files) then [||] else m.Files
                else
                    [||]
            with _ ->
                [||]

        let tracker = Tracker(backupRoot game, priorEntries)

        try
            report "Preparing installation..." 0.03

            if String.IsNullOrWhiteSpace(exePath) || not (File.Exists(exePath)) then
                { Success = false
                  Message = "Game executable was not found. Pick it manually and try again." }
            else

            let modRoot = modFilesRoot ()

            if String.IsNullOrWhiteSpace(modRoot) then
                { Success = false
                  Message = "The \"mod files\" folder is missing next to the application." }
            else

            let exeDir = Path.GetDirectoryName(exePath)
            let dlssnrFile = Path.Combine(modRoot, dlss5DirName, dlssnrFileName)

            let writeManifest () =
                let manifest =
                    { GameId = safeId game
                      GameTitle = game.Title
                      ExecutablePath = exePath
                      InstalledAtUtc = DateTime.UtcNow.ToString("o")
                      Mode = modeKey mode
                      Arch = archKey arch
                      Api = optiApiKey optiApi
                      Files = tracker.Entries }

                let options = JsonSerializerOptions()
                options.WriteIndented <- true
                File.WriteAllText(manifestPath game, JsonSerializer.Serialize(manifest, options))

            if not (canWriteTo exeDir) then
                { Success = false; Message = elevationNeededMessage }
            else

            // =============================================================
            // ROUTE A - DX12 + OPTISCALER (recommended, self-contained)
            // =============================================================
            if mode = OptiScalerMode then
                let optiRoot = Path.Combine(modRoot, optiScalerDirName)

                if not (Directory.Exists(optiRoot)) then
                    { Success = false
                      Message = "The \"" + optiScalerDirName + "\" payload is missing from \"mod files\"." }
                elif not (File.Exists(dlssnrFile)) then
                    { Success = false; Message = "Missing mod file: " + dlssnrFileName }
                else

                // Everything setup_windows.bat does on the NVIDIA path is done
                // here directly. Driving that script through stdin was both slow
                // and fragile - one unexpected prompt and its retry loops spin
                // forever - and it does nothing this cannot do instantly:
                // clear stale hooks, take a free proxy name, leave the .ini
                // untouched (NVIDIA needs no spoofing, no OptiPatcher).
                report "Clearing previous OptiScaler hooks..." 0.10
                clearStaleOptiScalerHooks exeDir |> ignore

                let slotName = pickOptiScalerSlot exeDir (optiApi = OptiVulkan)
                let optiDll = Path.Combine(optiRoot, "OptiScaler.dll")

                report "Deploying OptiScaler files..." 0.18

                // Skipped on purpose: the model is deployed once from "mod
                // files" rather than copied twice, OptiScaler.dll goes straight
                // to its proxy name, and the setup scripts are never needed in
                // the game folder because nothing runs them.
                let deployed =
                    copyTreeExcept
                        tracker
                        optiRoot
                        exeDir
                        [| dlssnrFileName; "OptiScaler.dll"; "setup_windows.bat"; "setup_linux.sh" |]

                report (sprintf "Hooking OptiScaler as %s..." slotName) 0.42
                tracker.Copy(optiDll, Path.Combine(exeDir, slotName))

                report "Deploying DLSS 5 ray reconstruction model (165 MB)..." 0.52
                copyIfEnabled tracker dlssnrFileName dlssnrFile (Path.Combine(exeDir, dlssnrFileName)) |> ignore

                // OptiScaler's payload no longer carries its own copies of the
                // NVIDIA runtimes - they live in "streamline_dlss" now, shared
                // with the ReShade routes, and are deployed on the same terms:
                // the DLSS runtime always, Streamline only where the game
                // already ships a build new enough to accept it.
                report "Deploying NVIDIA runtimes..." 0.68

                let optiRuntimes =
                    deployRuntimes tracker game exePath exeDir modRoot plan report 0.68 0.16

                // OptiScaler never installs ReShade. But if the user already
                // had it, the effects belong in its folder, so they go in then
                // and only then - an empty reshade-shaders next to a game with
                // no ReShade would just be litter.
                let optiEffects =
                    if GameAnalyzer.isReShadeInstalled exePath then
                        report "Adding DLSS 5 effects to the existing ReShade..." 0.86
                        let shaders = Path.Combine(exeDir, reshadeShadersDirName)
                        copyTree tracker (Path.Combine(modRoot, standardEffectsDirName)) shaders
                    else
                        0

                let extraFiles = deployExtras tracker exeDir mode arch optiApi report 0.88


                report "Writing restore point..." 0.92
                writeManifest ()
                report "DLSS 5 installed successfully." 1.0

                { Success = true
                  Message =
                    String.Join(
                        " • ",
                        [ yield "DX12 + OptiScaler (recommended) installed"
                          yield sprintf "%d OptiScaler file(s) deployed" (deployed + 1)
                          yield sprintf "Hooked as %s" slotName
                          yield! optiRuntimes.Summary
                          yield "Ray reconstruction model deployed next to the game"


                          if extraFiles > 0 then

                              yield sprintf "%d extra file(s) deployed" extraFiles


                          if optiEffects > 0 then
                              yield sprintf "DLSS 5 effects added to your ReShade (%d file(s))" optiEffects
                          else
                              yield "ReShade was not touched" ]
                    ) }
            // =============================================================
            // ROUTE D - AMD RDNA 4
            // =============================================================
            // Self-contained: the payload plus the ray reconstruction model.
            // No ReShade, no RenoDX, no Streamline, no OptiScaler.
            elif mode = AmdMode then
                let amdPayload = Path.Combine(modRoot, amdPayloadDirName)

                if not (Directory.Exists(amdPayload)) then
                    { Success = false
                      Message = "The \"" + amdPayloadDirName + "\" payload is missing from \"mod files\"." }
                elif not (File.Exists(dlssnrFile)) then
                    { Success = false; Message = "Missing mod file: " + dlssnrFileName }
                else

                // version.dll is the payload's preferred name, but plenty of
                // games ship one of their own; the first free name wins.
                let slotName =
                    amdSlots
                    |> Array.tryFind (fun n -> not (File.Exists(Path.Combine(exeDir, n))))
                    |> Option.defaultValue amdSlots.[0]

                report "Installing the AMD payload..." 0.20

                let deployed =
                    copyTreeExcept tracker amdPayload exeDir [| "version.dll" |]

                report (sprintf "Hooking as %s..." slotName) 0.45
                tracker.Copy(Path.Combine(amdPayload, "version.dll"), Path.Combine(exeDir, slotName))

                report "Deploying DLSS 5 ray reconstruction model (165 MB)..." 0.60
                copyIfEnabled tracker dlssnrFileName dlssnrFile (Path.Combine(exeDir, dlssnrFileName)) |> ignore

                let extraFiles = deployExtras tracker exeDir mode arch optiApi report 0.90


                report "Writing restore point..." 0.94
                writeManifest ()
                report "DLSS 5 installed successfully." 1.0

                { Success = true
                  Message =
                    String.Join(
                        " • ",
                        [ "AMD RDNA 4 mode installed"
                          sprintf "%d payload file(s) deployed" (deployed + 1)
                          sprintf "Hooked as %s" slotName
                          "Ray reconstruction model deployed next to the game"
                          (if extraFiles > 0 then sprintf "%d extra file(s) deployed" extraFiles else "") ]
                        |> List.filter (fun s -> s <> "")
                    ) }

            // =============================================================
            // ROUTE C - EMULATORS (ReShade on Vulkan)
            // =============================================================
            elif mode = Emulator then
                let setupExe = reShadeSetupPath ()
                let feedAddonFile = Path.Combine(modRoot, feedAddonName)
                let dlssFile = Path.Combine(modRoot, "streamline_dlss", "dlss", "nvngx_dlss.dll")
                let emulatorPayload = Path.Combine(modRoot, emulatorPayloadDirName)

                let missing =
                    [ setupExe; feedAddonFile; dlssnrFile; dlssFile ]
                    |> List.filter (fun p -> not (File.Exists(p)))

                if not missing.IsEmpty then
                    { Success = false
                      Message = "Missing mod file: " + Path.GetFileName(missing.Head) }
                elif not (Directory.Exists(emulatorPayload)) then
                    { Success = false
                      Message = "The \"" + emulatorPayloadDirName + "\" payload is missing from \"mod files\"." }
                else

                // Emulators render through Vulkan, so that is the API ReShade
                // hooks. Vulkan installs differ from the DLL-swap routes - the
                // setup can leave several files behind - so we note what was
                // there first and record whatever is new.
                report "Installing ReShade runtime (vulkan)..." 0.10

                let reshadeArtifacts =
                    [ "vulkan-1.dll"; "ReShade64.json"; "ReShade64.dll"
                      "ReShade.ini"; "ReShadePreset.ini"; "ReShade.log" ]

                let before =
                    reshadeArtifacts
                    |> List.filter (fun n -> File.Exists(Path.Combine(exeDir, n)))
                    |> Set.ofList

                let (setupOk, setupError) =
                    if ExtrasStore.isPayloadEnabled reShadeSetupKey then
                        runReShadeSetup setupExe exePath "vulkan" report
                    else
                        report "ReShade setup is switched off, skipping." 0.16
                        (true, "")

                if not setupOk && not (File.Exists(Path.Combine(exeDir, "ReShade.ini"))) then
                    { Success = false; Message = setupError }
                else

                for name in reshadeArtifacts do
                    let p = Path.Combine(exeDir, name)
                    if File.Exists(p) && not (before.Contains(name)) then tracker.Record(p, false, "")

                report "Installing standard effects (DisplayDepth, UIMask, ...)..." 0.30
                let (_, effectFiles) = ensureStandardEffectPaths tracker exeDir

                report "Installing the DLSS 5 add-on..." 0.50
                copyIfEnabled tracker feedAddonName feedAddonFile (Path.Combine(exeDir, feedAddonName)) |> ignore

                report "Deploying DLSS runtime..." 0.58
                tracker.Copy(dlssFile, Path.Combine(exeDir, "nvngx_dlss.dll"))

                report "Installing the emulator payload..." 0.66
                let payloadFiles = copyTree tracker emulatorPayload exeDir

                report "Deploying DLSS 5 ray reconstruction model (165 MB)..." 0.74
                copyIfEnabled tracker dlssnrFileName dlssnrFile (Path.Combine(exeDir, dlssnrFileName)) |> ignore

                let extraFiles = deployExtras tracker exeDir mode arch optiApi report 0.90


                report "Writing restore point..." 0.94
                writeManifest ()
                report "DLSS 5 installed successfully." 1.0

                { Success = true
                  Message =
                    String.Join(
                        " • ",
                        [ "Emulator - ReShade (Vulkan) + DLSS 5 installed"
                          sprintf "Effects deployed (%d file(s))" effectFiles
                          sprintf "Emulator payload deployed (%d file(s))" payloadFiles
                          "DLSS runtime and ray reconstruction model deployed"
                          (if extraFiles > 0 then sprintf "%d extra file(s) deployed" extraFiles else "") ]
                        |> List.filter (fun s -> s <> "")
                    ) }
            else

            // =============================================================
            // ROUTE B - RESHADE + RENODX (DX12 auto / DX11)
            // =============================================================
            let isDx9 = (mode = Dx9)
            let is32Bit = (arch = Bit32)
            let setupExe = reShadeSetupPath ()
            let addonFile = Path.Combine(modRoot, renodxAddonName)
            let feedAddonFile = Path.Combine(modRoot, feedAddonName)
            let feedAddon32File = Path.Combine(modRoot, feedAddon32Name)
            let host64Dir = Path.Combine(modRoot, bit32PayloadDirName, host64DirName)
            let dx9PayloadDir = Path.Combine(modRoot, dx9PayloadDirName)

            let missing =
                [ yield setupExe
                  if is32Bit then
                      yield feedAddon32File
                  else
                      yield addonFile
                      yield feedAddonFile
                      yield dlssnrFile ]
                |> List.filter (fun p -> not (File.Exists(p)))

            if not missing.IsEmpty then
                { Success = false
                  Message = "Missing mod file: " + Path.GetFileName(missing.Head) }
            elif is32Bit && not (Directory.Exists(host64Dir)) then
                { Success = false
                  Message = "The \"" + bit32PayloadDirName + "\" payload is missing from \"mod files\"." }
            elif isDx9 && not (Directory.Exists(dx9PayloadDir)) then
                { Success = false
                  Message = "The \"" + dx9PayloadDirName + "\" payload is missing from \"mod files\"." }
            else

            let backupDir = backupRoot game

            // -------------------------------------------------------------
            // 1. ReShade (headless, invisible)
            // -------------------------------------------------------------
            report "Detecting graphics API..." 0.06

            // DX9 titles get ReShade on the d3d9 slot, because that is the only
            // one the game itself loads. It does not stay there - see step 1a.
            let api = if isDx9 then "d3d9" else detectReShadeApi exePath
            report (sprintf "Installing ReShade runtime (%s)..." api) 0.10

            let reshadeDll = Path.Combine(exeDir, api + ".dll")

            // isReShadeInstalled never looks at d3d9.dll, so on the DX9 route we
            // check that slot ourselves as well as the one we move it to.
            // Switched off in Settings: no route installs ReShade. Whatever the
            // game already has is left exactly as it is.
            let reShadeEnabled = ExtrasStore.isPayloadEnabled reShadeSetupKey

            let alreadyHasReShade =
                GameAnalyzer.isReShadeInstalled exePath
                || (isDx9 && isReShadeFile reshadeDll)

            let reshadeExisted = File.Exists(reshadeDll)

            let reshadeBackup =
                if reshadeExisted then
                    let bp = Path.Combine(backupDir, shortHash reshadeDll + "_" + api + ".dll")
                    if not (File.Exists(bp)) then File.Copy(reshadeDll, bp, false)
                    bp
                else
                    ""

            // Re-running the setup over an existing ReShade returns a non-zero
            // exit code, so skip it when ReShade is already there.
            let reshadeError =
                if not reShadeEnabled then
                    report "ReShade setup is switched off, skipping." 0.16
                    ""
                elif alreadyHasReShade then
                    report "ReShade already present, skipping." 0.16
                    ""
                else
                    let (ok, err) = runReShadeSetup setupExe exePath api report
                    // The exit code is only a problem if the DLL really is absent.
                    if ok || File.Exists(reshadeDll) || GameAnalyzer.isReShadeInstalled exePath then "" else err

            if reshadeError <> "" then
                { Success = false; Message = reshadeError }
            else

            if reShadeEnabled && not alreadyHasReShade then
                tracker.Record(reshadeDll, reshadeExisted, reshadeBackup)

                // ReShade also drops its configuration files; track them for a clean removal.
                for extra in [ "ReShade.ini"; "ReShadePreset.ini"; "ReShade.log" ] do
                    let p = Path.Combine(exeDir, extra)
                    if File.Exists(p) then tracker.Record(p, false, "")

            // -------------------------------------------------------------
            // 1a. DX9 only - free the d3d9 slot for dgVoodoo
            // -------------------------------------------------------------
            // dgVoodoo has to be the d3d9.dll the game loads; it translates to a
            // modern API and ReShade then hooks that instead. So ReShade moves
            // to dxgi.dll, and it has to happen before anything else is copied
            // in - the DX9 payload brings its own D3D9.dll for that slot.
            let dx9RenameError =
                if not isDx9 then
                    ""
                else
                    try
                        let target = Path.Combine(exeDir, "dxgi.dll")

                        if isReShadeFile target then
                            // A previous run already moved it.
                            ""
                        elif not (File.Exists(reshadeDll)) then
                            // Nothing on the d3d9 slot to move: either ReShade
                            // is switched off, or it was never installed there.
                            ""
                        else
                            report "Moving ReShade to the dxgi slot..." 0.18

                            let targetExisted = File.Exists(target)

                            let targetBackup =
                                if targetExisted then
                                    let bp = Path.Combine(backupDir, shortHash target + "_dxgi.dll")
                                    if not (File.Exists(bp)) then File.Copy(target, bp, false)
                                    bp
                                else
                                    ""

                            File.Move(reshadeDll, target, true)
                            tracker.Record(target, targetExisted, targetBackup)
                            ""
                    with ex ->
                        "Could not move ReShade to dxgi.dll: " + ex.Message

            if dx9RenameError <> "" then
                { Success = false; Message = dx9RenameError }
            else

            // -------------------------------------------------------------
            // 1b. Standard effects layout + shared effect payload
            // -------------------------------------------------------------
            report "Installing standard effects (DisplayDepth, UIMask, ...)..." 0.20

            let (shadersRoot, standardEffectFiles) = ensureStandardEffectPaths tracker exeDir
            report (sprintf "Standard effects installed (%d file(s))." standardEffectFiles) 0.23

            // -------------------------------------------------------------
            // 1c. DX9 only - dgVoodoo wrapper next to the executable
            // -------------------------------------------------------------
            let dx9PayloadFiles =
                if isDx9 then
                    report "Installing the DirectX 9 wrapper..." 0.28
                    copyTree tracker dx9PayloadDir exeDir
                else
                    0

            // -------------------------------------------------------------
            // 2. Add-ons next to ReShade
            // -------------------------------------------------------------
            // A 32-bit game cannot load any of the 64-bit modules, so it gets
            // the 32-bit feed add-on on its own and everything else runs beside
            // it out of the host64 folder.
            let host64Files =
                if is32Bit then
                    report "Installing the 32-bit add-on..." 0.30
                    copyIfEnabled tracker feedAddon32Name feedAddon32File (Path.Combine(exeDir, feedAddon32Name)) |> ignore

                    report "Installing the 64-bit host..." 0.33
                    let host64Target = Path.Combine(exeDir, host64DirName)
                    let copied = copyTree tracker host64Dir host64Target

                    // The host needs the NVIDIA runtimes, but it no longer
                    // carries its own copies - they come from the one place
                    // every route reads them from.
                    let ourDlssDir = Path.Combine(modRoot, "streamline_dlss", "dlss")
                    let mutable extra = 0

                    for name in [ "nvngx_dlss.dll"; "nvngx_dlssg.dll" ] do
                        let src = Path.Combine(ourDlssDir, name)

                        if File.Exists(src) then
                            tracker.Copy(src, Path.Combine(host64Target, name))
                            extra <- extra + 1

                    copied + extra
                else
                    report "Installing RenoDX DLSS 5 add-on..." 0.30
                    copyIfEnabled tracker renodxAddonName addonFile (Path.Combine(exeDir, renodxAddonName)) |> ignore
                    copyIfEnabled tracker feedAddonName feedAddonFile (Path.Combine(exeDir, feedAddonName)) |> ignore
                    0

            // -------------------------------------------------------------
            // 3-4. NVIDIA Streamline + DLSS runtime
            // -------------------------------------------------------------
            let runtimes =
                deployRuntimes tracker game exePath exeDir modRoot plan report 0.40 0.36

            let dlssFolders = runtimes.DlssFolders
            let streamlineFolders = runtimes.StreamlineFolders

            // -------------------------------------------------------------
            // 5. Ray Reconstruction model - the core of DLSS 5
            // -------------------------------------------------------------
            report "Deploying DLSS 5 ray reconstruction model (165 MB)..." 0.78

            let dlssnrTargets =
                [ // 32-bit: the model is 64-bit, so it stays out of the game's
                  // own folder and goes to the host that can actually load it.
                  if is32Bit then yield Path.Combine(exeDir, host64DirName) else yield exeDir
                  for f in dlssFolders -> f.Directory
                  for f in streamlineFolders -> f.Directory ]
                |> List.distinctBy (fun d -> d.TrimEnd('\\', '/').ToLowerInvariant())
                |> List.filter Directory.Exists

            if File.Exists(dlssnrFile) then
                for target in dlssnrTargets do
                    copyIfEnabled tracker dlssnrFileName dlssnrFile (Path.Combine(target, dlssnrFileName)) |> ignore

            // -------------------------------------------------------------
            // 6. Manifest
            // -------------------------------------------------------------
            let extraFiles = deployExtras tracker exeDir mode arch optiApi report 0.90

            report "Writing restore point..." 0.94

            writeManifest ()

            report "DLSS 5 installed successfully." 1.0

            let summary =
                let routeName =
                    match mode with
                    | Dx9 -> "DX9"
                    | Dx11 -> "DX11"
                    | _ -> "DX12"

                let parts =
                    [ yield sprintf "%s %s-bit - ReShade + DLSS 5 installed" routeName (archKey arch)
                      yield sprintf "Effects deployed (%d file(s))" standardEffectFiles

                      if isDx9 then
                          yield
                              sprintf
                                  "DirectX 9 wrapper deployed (%d file(s)), ReShade moved to dxgi.dll"
                                  dx9PayloadFiles

                      if is32Bit then
                          yield sprintf "32-bit add-on + 64-bit host deployed (%d file(s))" host64Files

                      yield! runtimes.Summary
                      yield sprintf "Ray reconstruction model deployed to %d location(s)" dlssnrTargets.Length

                      if extraFiles > 0 then
                          yield sprintf "%d extra file(s) deployed" extraFiles ]
                    |> List.filter (fun s -> s <> "")

                String.Join(" • ", parts)

            { Success = true; Message = summary }

        with ex ->
            // Undo only what this run added, so a failed repair cannot wipe out
            // a previously working install.
            for entry in Seq.rev tracker.AddedThisRun do
                try
                    if entry.WasExisting && File.Exists(entry.BackupPath) then
                        File.Copy(entry.BackupPath, entry.TargetPath, true)
                    elif not entry.WasExisting && File.Exists(entry.TargetPath) then
                        File.Delete(entry.TargetPath)
                with _ ->
                    ()

            { Success = false
              Message = "Installation failed and was rolled back: " + ex.Message }

    // =====================================================================
    // UNINSTALL
    // =====================================================================
    /// The mod was put there by something other than this app, so there are no
    /// backups to restore. Removing the ray reconstruction model is enough to
    /// turn DLSS 5 off, and it is the only file we are certain we may delete.
    let private removeForeignInstall (game: GameItem) (exePath: string) (plan: InstallPlan option) (report: Progress) : InstallOutcome =
        try
            report "Locating DLSS 5 files..." 0.15

            let dlssDirs, streamlineDirs =
                match plan with
                | Some p -> p.DlssDirs, p.StreamlineDirs
                | None ->
                    (findDlssFolders game.InstallDirectory exePath |> List.map (fun f -> f.Directory) |> List.toArray),
                    (findStreamlineFolders game.InstallDirectory exePath
                     |> List.map (fun f -> f.Directory)
                     |> List.toArray)

            let status = inspect game exePath dlssDirs streamlineDirs

            if status.DlssnrLocations.Length = 0 then
                { Success = false; Message = "No DLSS 5 files were found for this game." }
            else
                let mutable removed = 0
                let total = status.DlssnrLocations.Length

                status.DlssnrLocations
                |> Array.iteri (fun index dir ->
                    report "Removing DLSS 5 files..." (0.2 + 0.7 * float (index + 1) / float total)

                    try
                        let target = Path.Combine(dir, dlssnrFileName)

                        if File.Exists(target) then
                            File.Delete(target)
                            removed <- removed + 1
                    with _ ->
                        ())

                report "DLSS 5 removed." 1.0

                { Success = true
                  Message =
                    sprintf
                        "This DLSS 5 install was not made by DLSS 5 MANAGER, so only %s was removed (%d location(s)). ReShade and the game's own runtime files were left untouched."
                        dlssnrFileName
                        removed }
        with ex ->
            { Success = false; Message = "Removal failed: " + ex.Message }

    let uninstall (game: GameItem) (exePath: string) (plan: InstallPlan option) (report: Progress) : InstallOutcome =
        try
            let path = manifestPath game

            let targetDir =
                try
                    if String.IsNullOrWhiteSpace(exePath) then "" else Path.GetDirectoryName(exePath)
                with _ ->
                    ""

            if not (canWriteTo targetDir) then
                // Removal restores and deletes files, so it needs the same
                // rights the install did.
                { Success = false; Message = elevationNeededMessage }
            elif not (File.Exists(path)) then
                removeForeignInstall game exePath plan report
            else
                report "Reading restore point..." 0.08

                let options = JsonSerializerOptions()
                options.PropertyNameCaseInsensitive <- true
                let manifest = JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(path), options)

                // An OptiScaler install unwinds itself first: the model goes,
                // then OptiScaler's own uninstaller runs out of sight. What it
                // leaves behind is picked up by the manifest pass below.
                let mutable optiNote = ""

                if not (isNull (box manifest.Mode))
                   && manifest.Mode.Equals("optiscaler", StringComparison.OrdinalIgnoreCase) then
                    let exeDir =
                        try
                            if String.IsNullOrWhiteSpace(exePath) then
                                Path.GetDirectoryName(manifest.ExecutablePath)
                            else
                                Path.GetDirectoryName(exePath)
                        with _ ->
                            ""

                    if not (String.IsNullOrWhiteSpace(exeDir)) && Directory.Exists(exeDir) then
                        report "Removing ray reconstruction model..." 0.15

                        try
                            let model = Path.Combine(exeDir, dlssnrFileName)
                            if File.Exists(model) then File.Delete(model)
                        with _ ->
                            ()

                        report "Removing OptiScaler hooks..." 0.3

                        // Anything still carrying OptiScaler's version resource
                        // is ours, whatever name it ended up under.
                        let cleared = clearStaleOptiScalerHooks exeDir

                        optiNote <- sprintf "OptiScaler removed (%d hook(s)). " cleared

                let files = if isNull (box manifest.Files) then [||] else manifest.Files
                let total = max 1 files.Length
                let mutable index = 0
                let mutable restored = 0
                let mutable removed = 0

                for entry in Array.rev files do
                    index <- index + 1
                    report "Restoring original game files..." (0.4 + 0.5 * float index / float total)

                    try
                        if entry.WasExisting && File.Exists(entry.BackupPath) then
                            File.Copy(entry.BackupPath, entry.TargetPath, true)
                            restored <- restored + 1
                        elif not entry.WasExisting && File.Exists(entry.TargetPath) then
                            File.Delete(entry.TargetPath)
                            removed <- removed + 1
                    with _ ->
                        ()

                try
                    let root = Path.GetDirectoryName(manifest.ExecutablePath)

                    if not (String.IsNullOrWhiteSpace(root)) then
                        // Config and log files the mod wrote on its first run.
                        for name in runtimeLeftovers do
                            let p = Path.Combine(root, name)

                            try
                                if File.Exists(p) then File.Delete(p)
                            with _ ->
                                ()

                        // Sweep for our own files the manifest did not account
                        // for - an install made by an older build recorded a
                        // different set of paths, and whatever it left behind
                        // is still unmistakably ours.
                        let handled =
                            files
                            |> Array.choose (fun e ->
                                if String.IsNullOrWhiteSpace(e.TargetPath) then None else Some e.TargetPath)
                            |> fun paths -> HashSet<string>(paths, StringComparer.OrdinalIgnoreCase)

                        for name in exclusiveArtifacts do
                            for dir in [ root; Path.Combine(root, host64DirName) ] do
                                let p = Path.Combine(dir, name)

                                try
                                    if File.Exists(p) && not (handled.Contains(p)) then
                                        File.Delete(p)
                                        removed <- removed + 1
                                with _ ->
                                    ()

                        // ReShade sits on a name the game could also use, so it
                        // is only removed when its own version resource says so
                        // and the manifest did not already deal with it.
                        for name in [ "dxgi.dll"; "d3d9.dll"; "d3d11.dll"; "d3d12.dll"; "opengl32.dll" ] do
                            let p = Path.Combine(root, name)

                            try
                                if not (handled.Contains(p)) && isReShadeFile p then
                                    File.Delete(p)
                                    removed <- removed + 1
                            with _ ->
                                ()

                        // Folders the payloads brought with them are left empty
                        // by the file pass above; drop them so the game folder
                        // comes back clean.
                        for name in [ "OptiScaler"; "Licenses"; reshadeShadersDirName; host64DirName ] do
                            let dir = Path.Combine(root, name)

                            // No files left anywhere inside means everything in
                            // there was ours; empty subfolders go with it.
                            if Directory.Exists(dir)
                               && Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length = 0 then
                                Directory.Delete(dir, true)
                with _ ->
                    ()

                File.Delete(path)
                report "DLSS 5 removed." 1.0

                { Success = true
                  Message =
                    optiNote
                    + sprintf "Restored %d original file(s) and removed %d added file(s)." restored removed }
        with ex ->
            { Success = false; Message = "Uninstall failed: " + ex.Message }
