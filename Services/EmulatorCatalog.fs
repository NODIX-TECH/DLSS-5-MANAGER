namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.Diagnostics
open System.Collections.Generic

/// The console emulators the app knows by name, and the bounded search that
/// finds them on disk.
///
/// A library scan may walk a whole drive because a game can be anywhere. An
/// emulator cannot: it is a small application that lands in one of a handful
/// of predictable places. So this looks only where those places are, to a
/// fixed depth, under a time and folder budget - the scan is meant to finish
/// while the user is still looking at the page.
module EmulatorCatalog =

    /// Which ReShade slot an emulator's renderer actually loads. Everything
    /// here draws through Vulkan except Ryujinx, whose DLSS 5 payload runs on
    /// DirectX 12 instead.
    let private vulkanApi = "vulkan"
    let private dx12Api = "dxgi"

    type Emulator =
        { /// Executable names, best first - the first one found in a folder is
          /// the one that gets added, so a GUI build wins over the headless
          /// sibling sitting next to it.
          ExeNames: string[]
          /// What the card is called once it is added.
          Display: string
          /// The machine it emulates. Shown while scanning, nowhere else.
          System: string
          /// A name this common needs its folder to agree before we believe
          /// it - "Fusion.exe" is not enough on its own. Empty means the
          /// executable name is distinctive by itself.
          FolderHints: string[]
          /// The API its ReShade install has to hook.
          Api: string }

    let private emu names display system api =
        { ExeNames = names
          Display = display
          System = system
          FolderHints = [||]
          Api = api }

    let private emuHinted names display system hints =
        { ExeNames = names
          Display = display
          System = system
          FolderHints = hints
          Api = vulkanApi }

    /// Every emulator the auto-detect recognises, in console generations.
    let known: Emulator[] =
        [|
          // ---- Sony -------------------------------------------------------
          emu
              [| "duckstation-qt-x64-ReleaseLTCG.exe"
                 "duckstation-qt.exe"
                 "duckstation-nogui-x64-ReleaseLTCG.exe"
                 "duckstation.exe" |]
              "DuckStation"
              "PlayStation 1"
              vulkanApi
          emu [| "ePSXe_x64.exe"; "ePSXe.exe" |] "ePSXe" "PlayStation 1" vulkanApi
          emu [| "pcsx2-qt.exe"; "pcsx2x64-avx2.exe"; "pcsx2x64.exe"; "pcsx2.exe" |] "PCSX2" "PlayStation 2" vulkanApi
          emu [| "rpcs3.exe" |] "RPCS3" "PlayStation 3" vulkanApi
          emu [| "shadPS4.exe"; "shadps4-qt.exe" |] "shadPS4" "PlayStation 4" vulkanApi
          emu [| "fpPS4.exe" |] "fpPS4" "PlayStation 4" vulkanApi
          emu [| "PPSSPPWindows64.exe"; "PPSSPPWindows.exe"; "PPSSPPQt.exe" |] "PPSSPP" "PSP" vulkanApi
          emu [| "Vita3K.exe" |] "Vita3K" "PS Vita" vulkanApi

          // ---- Nintendo ---------------------------------------------------
          // Ryujinx is the one emulator whose payload runs on DirectX 12.
          emu
              [| "Ryujinx.exe"; "Ryujinx.Ava.exe"; "Ryubing.exe"; "Ryujinx.Headless.SDL2.exe" |]
              "Ryujinx"
              "Nintendo Switch"
              dx12Api
          emu [| "suyu.exe" |] "Suyu" "Nintendo Switch" vulkanApi
          emu [| "sudachi.exe" |] "Sudachi" "Nintendo Switch" vulkanApi
          emu [| "yuzu.exe" |] "Yuzu" "Nintendo Switch" vulkanApi
          emu [| "Dolphin.exe"; "DolphinQt.exe"; "DolphinWx.exe" |] "Dolphin" "Wii / GameCube" vulkanApi
          emu [| "Cemu.exe" |] "Cemu" "Wii U" vulkanApi
          emu [| "lime3ds-gui.exe"; "lime3ds.exe" |] "Lime3DS" "Nintendo 3DS" vulkanApi
          emu [| "citra-qt.exe"; "citra.exe" |] "Citra" "Nintendo 3DS" vulkanApi
          emu [| "melonDS.exe" |] "melonDS" "Nintendo DS" vulkanApi
          emu [| "DeSmuME_x64.exe"; "DeSmuME.exe" |] "DeSmuME" "Nintendo DS" vulkanApi
          emu [| "Project64.exe" |] "Project64" "Nintendo 64" vulkanApi
          emu [| "mupen64plus-gui.exe"; "mupen64plus.exe" |] "Mupen64Plus" "Nintendo 64" vulkanApi
          emu [| "mGBA.exe" |] "mGBA" "Game Boy Advance" vulkanApi
          emu [| "visualboyadvance-m.exe"; "VisualBoyAdvance.exe" |] "VisualBoyAdvance-M" "Game Boy Advance" vulkanApi
          emu [| "snes9x-x64.exe"; "snes9x.exe" |] "Snes9x" "Super Nintendo" vulkanApi
          emu [| "Mesen.exe" |] "Mesen" "NES / SNES" vulkanApi

          // ---- Microsoft --------------------------------------------------
          emu [| "xenia_canary.exe"; "xenia.exe" |] "Xenia" "Xbox 360" vulkanApi
          emu [| "xemu.exe" |] "xemu" "Xbox" vulkanApi

          // ---- SEGA -------------------------------------------------------
          emu [| "flycast.exe" |] "Flycast" "Dreamcast" vulkanApi
          emu [| "redream.exe" |] "Redream" "Dreamcast" vulkanApi
          emu [| "mednafen.exe" |] "Mednafen" "Sega Saturn" vulkanApi
          emuHinted [| "SSF.exe" |] "SSF" "Sega Saturn" [| "ssf" |]
          emuHinted [| "Fusion.exe" |] "Kega Fusion" "Mega Drive" [| "fusion"; "kega" |]

          // ---- Arcade & multi-system --------------------------------------
          emu [| "retroarch.exe" |] "RetroArch" "Multi-system" vulkanApi
          emu [| "mame64.exe"; "mame.exe" |] "MAME" "Arcade" vulkanApi
          emu [| "dosbox-x.exe"; "dosbox.exe" |] "DOSBox" "MS-DOS" vulkanApi |]

    /// Executable name -> emulator, built once.
    let private byExeName =
        lazy
            (let table = Dictionary<string, Emulator>(StringComparer.OrdinalIgnoreCase)

             for entry in known do
                 for name in entry.ExeNames do
                     table.[name] <- entry

             table)

    /// True when the folder the executable sits in - or the one above it -
    /// backs up what its name claims.
    let private folderAgrees (exePath: string) (hints: string[]) =
        if hints.Length = 0 then
            true
        else
            try
                let dir = Path.GetDirectoryName(exePath)

                let names =
                    [ Path.GetFileName(dir)
                      (try Path.GetFileName(Path.GetDirectoryName(dir)) with _ -> "") ]
                    |> List.filter (fun n -> not (String.IsNullOrWhiteSpace(n)))
                    |> List.map (fun n -> n.ToLowerInvariant())

                hints |> Array.exists (fun h -> names |> List.exists (fun n -> n.Contains(h)))
            with _ ->
                false

    /// Which emulator this executable is, when it is one we know.
    let identify (exePath: string) : Emulator option =
        try
            if String.IsNullOrWhiteSpace(exePath) then
                None
            else
                match byExeName.Value.TryGetValue(Path.GetFileName(exePath)) with
                | true, entry when folderAgrees exePath entry.FolderHints -> Some entry
                | _ ->
                    // A renamed executable still gives itself away by the
                    // folder it was extracted into.
                    let folder =
                        try
                            Path.GetFileName(Path.GetDirectoryName(exePath))
                        with _ ->
                            ""

                    if String.IsNullOrWhiteSpace(folder) then
                        None
                    else
                        known
                        |> Array.tryFind (fun e ->
                            e.FolderHints.Length = 0
                            && folder.Equals(e.Display, StringComparison.OrdinalIgnoreCase))
        with _ ->
            None

    /// The API ReShade has to be installed on for this emulator. Vulkan is the
    /// answer for everything the catalogue does not name otherwise, which is
    /// what every emulator install did before the catalogue existed.
    let reShadeApi (exePath: string) : string =
        match identify exePath with
        | Some entry -> entry.Api
        | None -> vulkanApi

    /// True when this emulator wants ReShade on DirectX 12 rather than Vulkan.
    let usesDx12 (exePath: string) : bool = reShadeApi exePath = dx12Api

    // =====================================================================
    // THE SEARCH
    // =====================================================================
    /// Folders no emulator is ever installed into, and the ones that would
    /// cost the most to walk. Skipping them is most of what keeps this quick.
    let private skipFolders =
        HashSet<string>(
            [| "windows"; "winsxs"; "$recycle.bin"; "system volume information"; "perflogs"
               "recovery"; "boot"; "msocache"; "$windows.~ws"; "$windows.~bt"
               "temp"; "tmp"; "cache"; "caches"; "shadercache"; "d3dscache"; "downloading"
               "packages"; "windowsapps"; "assembly"; "installer"; "servicing"; "depotcache"
               "node_modules"; ".git"; ".vs"; "obj"; "workshop"
               "common files"; "internet explorer"; "windows defender"; "windows nt"
               "microsoft"; "microsoft office"; "microsoft visual studio"; "microsoft shared"
               "nvidia corporation"; "windowspowershell"; "dotnet"; "java" |],
            StringComparer.OrdinalIgnoreCase
        )

    /// Top level folder names on a drive worth going into. A drive's other top
    /// level folders are read but never opened.
    let private candidateRoots =
        [| "emu"; "emulation"; "roms"; "retro"; "console"; "arcade"
           "game"; "jeux"; "spiele"; "juegos"
           "program files"; "programs"; "apps"; "portable"; "tools"
           "nintendo"; "playstation"; "sony"; "sega"; "xbox"; "steam" |]

    /// How long the whole search may take, and how many folders it may open.
    /// Whichever runs out first ends it - a scan that has not found an
    /// emulator in twenty seconds was never going to.
    let private timeBudgetSeconds = 20.0
    let private folderBudget = 20000

    type private Budget() =
        let watch = Stopwatch.StartNew()
        let mutable visited = 0

        /// False once the search has spent what it was given.
        member _.Take() =
            visited <- visited + 1
            visited < folderBudget && watch.Elapsed.TotalSeconds < timeBudgetSeconds

    /// One emulator found on disk.
    type Found =
        { ExePath: string
          Display: string
          System: string }

    /// Walks one root to a fixed depth, collecting whatever it recognises.
    /// Best match per folder: a folder holding both the GUI and the headless
    /// build contributes the GUI one only.
    let private walk (root: string) (maxDepth: int) (budget: Budget) (hits: Dictionary<string, Found>) =
        let rec descend (dir: string) (depth: int) =
            if budget.Take() then
                try
                    let mutable best: (int * Emulator * string) option = None

                    for file in Directory.EnumerateFiles(dir, "*.exe") do
                        let name = Path.GetFileName(file)

                        match byExeName.Value.TryGetValue(name) with
                        | true, entry when folderAgrees file entry.FolderHints ->
                            let rank =
                                entry.ExeNames
                                |> Array.tryFindIndex (fun n -> String.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                                |> Option.defaultValue Int32.MaxValue

                            match best with
                            | Some(bestRank, _, _) when bestRank <= rank -> ()
                            | _ -> best <- Some(rank, entry, file)
                        | _ -> ()

                    match best with
                    | Some(_, entry, file) ->
                        let key = file.ToLowerInvariant()

                        if not (hits.ContainsKey(key)) then
                            hits.[key] <-
                                { ExePath = file
                                  Display = entry.Display
                                  System = entry.System }
                    | None -> ()
                with _ ->
                    ()

                if depth < maxDepth then
                    try
                        for sub in Directory.EnumerateDirectories(dir) do
                            let name = Path.GetFileName(sub)

                            if not (skipFolders.Contains(name)) && not (name.StartsWith(".")) then
                                descend sub (depth + 1)
                    with _ ->
                        ()

        if Directory.Exists(root) then descend root 0

    /// Where emulators actually get installed, with how far below each one is
    /// worth looking. Nothing outside this list is searched at all.
    let private searchRoots () : (string * int) list =
        let env (name: string) =
            try
                Environment.GetEnvironmentVariable(name)
            with _ ->
                ""

        let user =
            try
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            with _ ->
                ""

        // Downloads first, and deeper than the rest: an emulator is usually a
        // zip someone extracted where it landed, so it sits a folder or two
        // inside whatever the archive was called.
        let wellKnown =
            [ Path.Combine(user, "Downloads"), 4
              env "ProgramFiles", 3
              env "ProgramFiles(x86)", 3
              Path.Combine(env "LOCALAPPDATA", "Programs"), 3
              env "LOCALAPPDATA", 2
              env "APPDATA", 2
              Path.Combine(user, "Desktop"), 3
              Path.Combine(user, "Documents"), 3 ]
            |> List.filter (fun (p, _) -> not (String.IsNullOrWhiteSpace(p)))

        // Plus the folders people keep emulators in on their other drives. The
        // drive root is read, but only its promising folders are opened - that
        // is the difference between this and a library scan.
        let drives =
            try
                DriveInfo.GetDrives()
                |> Array.filter (fun d -> d.IsReady && d.DriveType = DriveType.Fixed)
                |> Array.collect (fun drive ->
                    try
                        drive.RootDirectory.GetDirectories()
                        |> Array.filter (fun d ->
                            let name = d.Name.ToLowerInvariant()

                            not (skipFolders.Contains(d.Name))
                            && candidateRoots |> Array.exists (fun c -> name.Contains(c)))
                        |> Array.map (fun d -> d.FullName, 4)
                    with _ ->
                        [||])
                |> List.ofArray
            with _ ->
                []

        wellKnown @ drives
        |> List.distinctBy (fun (p, _) -> p.TrimEnd('\\', '/').ToLowerInvariant())

    /// Every emulator the catalogue recognises, under the folders above.
    /// Never throws: a root that cannot be read is one that contributed
    /// nothing.
    let scan () : Found list =
        let hits = Dictionary<string, Found>(StringComparer.OrdinalIgnoreCase)
        let budget = Budget()

        for (root, depth) in searchRoots () do
            try
                walk root depth budget hits
            with _ ->
                ()

        hits.Values |> Seq.sortBy (fun f -> f.Display) |> List.ofSeq
