namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.Text.Json
open System.Collections.Generic
open DLSS_5_MANAGER.Models

/// Persistent per-game deep-scan cache.
///
/// Everything the Manage sheet and the installer need - the real executable,
/// where the DLSS and Streamline runtimes live, their versions, whether ReShade
/// is present - is computed once (during the library scan, or the moment a game
/// is added by hand) and written to disk. Opening a game afterwards is instant
/// and the installer never has to walk the folder tree again.
module AnalysisStore =

    [<CLIMutable>]
    type GameAnalysis =
        { GameId: string
          ExecutablePath: string
          ExecutableFolder: string
          ReShadeInstalled: bool
          DlssText: string
          DlssDirs: string[]
          StreamlineText: string
          StreamlineDirs: string[]
          /// nvngx_dlssnr.dll found -> DLSS 5 is on this game, whoever put it there.
          Dlss5Present: bool
          Dlss5Complete: bool
          Dlss5Missing: string[]
          /// True when this app performed the install and holds the backups.
          ModInstalled: bool
          AnalyzedAtUtc: string }

    let private storeLock = obj ()
    let private store = Dictionary<string, GameAnalysis>(StringComparer.OrdinalIgnoreCase)
    let mutable private isLoaded = false

    let private storePath () =
        let dir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS5Manager")

        Directory.CreateDirectory(dir) |> ignore
        Path.Combine(dir, "analysis_cache_v2.json")

    let key (game: GameItem) =
        if String.IsNullOrWhiteSpace(game.AppId) then game.Title else game.AppId

    // =====================================================================
    // PERSISTENCE
    // =====================================================================
    let private loadUnsafe () =
        if not isLoaded then
            isLoaded <- true

            try
                let path = storePath ()

                if File.Exists(path) then
                    let options = JsonSerializerOptions()
                    options.PropertyNameCaseInsensitive <- true
                    let items = JsonSerializer.Deserialize<GameAnalysis[]>(File.ReadAllText(path), options)

                    if not (isNull (box items)) then
                        for item in items do
                            if not (String.IsNullOrWhiteSpace(item.GameId)) then
                                store.[item.GameId] <- item
            with _ ->
                ()

    let private saveUnsafe () =
        try
            let options = JsonSerializerOptions()
            options.WriteIndented <- true
            let payload = store.Values |> Seq.toArray
            File.WriteAllText(storePath (), JsonSerializer.Serialize(payload, options))
        with _ ->
            ()

    let load () = lock storeLock (fun () -> loadUnsafe ())

    let save () =
        lock storeLock (fun () ->
            loadUnsafe ()
            saveUnsafe ())

    let tryGet (game: GameItem) : GameAnalysis option =
        lock storeLock (fun () ->
            loadUnsafe ()

            match store.TryGetValue(key game) with
            | true, v -> Some v
            | _ -> None)

    let put (analysis: GameAnalysis) =
        lock storeLock (fun () ->
            loadUnsafe ()
            store.[analysis.GameId] <- analysis)

    let remove (game: GameItem) =
        lock storeLock (fun () ->
            loadUnsafe ()
            store.Remove(key game) |> ignore)

    let clear () =
        lock storeLock (fun () ->
            isLoaded <- true
            store.Clear()
            saveUnsafe ())

    // =====================================================================
    // ANALYSIS
    // =====================================================================
    let private describe (folders: GameAnalyzer.ModFolder list) (label: string) =
        match folders with
        | [] -> sprintf "No %s runtime found" label
        | _ ->
            let newest =
                folders
                |> List.map GameAnalyzer.folderVersion
                |> List.sortWith (fun a b -> GameAnalyzer.compareVer b a)
                |> List.head

            sprintf "v%s in %d folder(s)" (GameAnalyzer.verText newest) folders.Length

    /// Full deep scan of a single game. This is the expensive part, so it only
    /// runs on first discovery, on an explicit re-scan, or after an install.
    let analyze (game: GameItem) : GameAnalysis =
        let exePath =
            if not (String.IsNullOrWhiteSpace(game.TargetExecutablePath))
               && File.Exists(game.TargetExecutablePath) then
                game.TargetExecutablePath
            else
                let (resolved, _) =
                    GameAnalyzer.resolveGameExecutable game.InstallDirectory game.Title

                resolved

        let folder =
            if String.IsNullOrWhiteSpace(exePath) then
                game.InstallDirectory
            else
                try Path.GetDirectoryName(exePath)
                with _ -> game.InstallDirectory

        let dlssFolders = GameAnalyzer.findDlssFolders game.InstallDirectory exePath
        let streamlineFolders = GameAnalyzer.findStreamlineFolders game.InstallDirectory exePath

        let dlssDirs = dlssFolders |> List.map (fun f -> f.Directory) |> List.toArray
        let streamlineDirs = streamlineFolders |> List.map (fun f -> f.Directory) |> List.toArray
        let dlss5 = ModInstaller.inspect game exePath dlssDirs streamlineDirs

        { GameId = key game
          ExecutablePath = exePath
          ExecutableFolder = folder
          ReShadeInstalled =
            not (String.IsNullOrWhiteSpace(exePath)) && GameAnalyzer.isReShadeInstalled exePath
          DlssText =
            if String.IsNullOrWhiteSpace(exePath) then "No executable detected"
            else describe dlssFolders "DLSS"
          DlssDirs = dlssDirs
          StreamlineText =
            if String.IsNullOrWhiteSpace(exePath) then "No executable detected"
            else describe streamlineFolders "Streamline"
          StreamlineDirs = streamlineDirs
          Dlss5Present = dlss5.Present
          Dlss5Complete = dlss5.Complete
          Dlss5Missing = dlss5.Missing
          ModInstalled = dlss5.ManagedByApp
          AnalyzedAtUtc = DateTime.UtcNow.ToString("o") }

    /// Manual add: the user already pointed at the executable, so there is
    /// nothing to resolve and no library to walk. We look at that file's own
    /// folder (two levels deep at most), which keeps the UI responsive on
    /// multi-hundred-gigabyte installs instead of freezing on a full tree walk.
    let analyzeExecutableOnly (game: GameItem) : GameAnalysis =
        let exePath = game.TargetExecutablePath

        let exeDir =
            try
                if String.IsNullOrWhiteSpace(exePath) then game.InstallDirectory
                else Path.GetDirectoryName(exePath)
            with _ ->
                game.InstallDirectory

        let dlssFolders = GameAnalyzer.findModFoldersDepth exeDir GameAnalyzer.dlssFileNames 2
        let streamlineFolders = GameAnalyzer.findModFoldersDepth exeDir GameAnalyzer.streamlineFileNames 2

        let dlssDirs = dlssFolders |> List.map (fun f -> f.Directory) |> List.toArray
        let streamlineDirs = streamlineFolders |> List.map (fun f -> f.Directory) |> List.toArray
        let dlss5 = ModInstaller.inspect game exePath dlssDirs streamlineDirs

        { GameId = key game
          ExecutablePath = exePath
          ExecutableFolder = exeDir
          ReShadeInstalled =
            not (String.IsNullOrWhiteSpace(exePath)) && GameAnalyzer.isReShadeInstalled exePath
          DlssText = describe dlssFolders "DLSS"
          DlssDirs = dlssDirs
          StreamlineText = describe streamlineFolders "Streamline"
          StreamlineDirs = streamlineDirs
          Dlss5Present = dlss5.Present
          Dlss5Complete = dlss5.Complete
          Dlss5Missing = dlss5.Missing
          ModInstalled = dlss5.ManagedByApp
          AnalyzedAtUtc = DateTime.UtcNow.ToString("o") }

    /// Analyze and persist a single game.
    let refresh (game: GameItem) : GameAnalysis =
        let analysis = analyze game
        put analysis
        save ()
        analysis

    /// Exe-only analysis for a hand-picked executable, persisted immediately.
    let refreshExecutableOnly (game: GameItem) : GameAnalysis =
        let analysis = analyzeExecutableOnly game
        put analysis
        save ()
        analysis

    /// Analyze a whole batch (library scan / folder import) and persist once.
    let refreshMany (games: GameItem list) (onProgress: int -> int -> string -> unit) : unit =
        let total = games.Length
        let mutable index = 0

        for game in games do
            index <- index + 1
            onProgress index total game.Title

            try
                put (analyze game)
            with _ ->
                ()

        save ()

    /// Only analyze the games we have never seen before - used after adding
    /// folders so existing entries are not rescanned.
    let refreshMissing (games: GameItem list) (onProgress: int -> int -> string -> unit) : unit =
        let pending = games |> List.filter (fun g -> (tryGet g).IsNone)
        if not pending.IsEmpty then refreshMany pending onProgress
