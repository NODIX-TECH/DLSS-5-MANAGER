namespace DLSS_5_MANAGER.Services

open System
open System.Collections.Generic
open System.IO

/// Does this game carry an anti-cheat?
///
/// A DLSS 5 install puts DLLs beside the executable - a proxy dxgi.dll, a
/// ReShade, add-ons - which is exactly what an anti-cheat looks for, and in a
/// game played online that can end in a ban. The Manage sheet warns before
/// anyone installs: the answer here is advice, never a block.
///
/// Almost every anti-cheat installs something of its own beside the game, so
/// the folder is the evidence: a bounded walk of the install folder and the
/// executable's folder looks for the names each one ships under. The few that
/// live entirely elsewhere - Valve's VAC, Riot Vanguard, Call of Duty's
/// Ricochet, Blizzard's - leave nothing in the folder, and those games are
/// known by name instead.
module AntiCheat =

    /// Folder names, exactly (any case), and who they belong to.
    let private folderNames =
        dict
            [ "easyanticheat", "Easy Anti-Cheat"
              "easyanticheat_eos", "Easy Anti-Cheat"
              "battleye", "BattlEye"
              "eaanticheat", "EA Javelin Anticheat"
              "anticheatexpert", "Tencent ACE"
              "gameguard", "nProtect GameGuard"
              "xigncode", "XIGNCODE3"
              "xigncode3", "XIGNCODE3"
              "hshield", "AhnLab HackShield"
              "blackcipher", "Nexon BlackCipher"
              "equ8", "EQU8"
              "punkbuster", "PunkBuster"
              "vanguard", "Riot Vanguard" ]

    /// File names, exactly (any case).
    let private fileNames =
        dict
            [ "easyanticheat_x64.dll", "Easy Anti-Cheat"
              "easyanticheat_x86.dll", "Easy Anti-Cheat"
              "easyanticheat_setup.exe", "Easy Anti-Cheat"
              "easyanticheat_eos_setup.exe", "Easy Anti-Cheat"
              "easyanticheat_eos.sys", "Easy Anti-Cheat"
              "start_protected_game.exe", "Easy Anti-Cheat"
              "beservice.exe", "BattlEye"
              "beservice_x64.exe", "BattlEye"
              "beclient_x64.dll", "BattlEye"
              "beclient.dll", "BattlEye"
              "bedaisy.sys", "BattlEye"
              "eaanticheat.gameservicelauncher.exe", "EA Javelin Anticheat"
              "eaanticheat.installer.exe", "EA Javelin Anticheat"
              "gamemon.des", "nProtect GameGuard"
              "gameguard.des", "nProtect GameGuard"
              "x3.xem", "XIGNCODE3"
              "xhunter1.sys", "XIGNCODE3"
              "pbcl.dll", "PunkBuster"
              "pnkbstra.exe", "PunkBuster"
              "mhyprot2.sys", "miHoYo protect"
              "mhyprot3.sys", "miHoYo protect"
              "hoyokprotect.sys", "HoYoverse protect"
              "mhypbase.dll", "miHoYo protect"
              "ace-base.sys", "Tencent ACE"
              "sguard64.exe", "Tencent ACE"
              "neacsafe64.sys", "NetEase anti-cheat"
              "neacclient.exe", "NetEase anti-cheat"
              "vgk.sys", "Riot Vanguard"
              "randgrid.sys", "Ricochet"
              "atvi-randgrid.sys", "Ricochet"
              "equ8_conf.json", "EQU8" ]

    /// Games whose anti-cheat leaves nothing in the game folder. Matched on
    /// the title with everything but letters and digits dropped, so
    /// "Counter-Strike 2" and "COUNTER STRIKE 2" are one name.
    let private knownTitles =
        [ "counterstrike2", "Valve Anti-Cheat (VAC)"
          "counterstrikeglobaloffensive", "Valve Anti-Cheat (VAC)"
          "dota2", "Valve Anti-Cheat (VAC)"
          "deadlock", "Valve Anti-Cheat (VAC)"
          "teamfortress2", "Valve Anti-Cheat (VAC)"
          "valorant", "Riot Vanguard"
          "leagueoflegends", "Riot Vanguard"
          "callofduty", "Ricochet"
          "warzone", "Ricochet"
          "overwatch", "Blizzard anti-cheat" ]

    let private squash (text: string) =
        if isNull text then ""
        else String(text.ToLowerInvariant() |> Seq.filter Char.IsLetterOrDigit |> Array.ofSeq)

    /// Anything named like an anti-cheat that is not in the lists above -
    /// THE FINALS keeps its own as Installers\AntiCheatInstaller.exe.
    let private looksLikeOne (name: string) =
        let n = name.ToLowerInvariant()
        n.Contains("anticheat") || n.Contains("anti-cheat") || n.Contains("anti_cheat")

    /// BattlEye's protected launcher is the game's own name with "_BE".
    let private isBattlEyeLauncher (name: string) =
        name.EndsWith("_BE.exe", StringComparison.OrdinalIgnoreCase)

    // Bounds for the walk. A game folder is mostly archives in a handful of
    // directories, and every anti-cheat sits near the top of it, so three
    // levels are plenty - and the caps keep a pathological folder quick.
    [<Literal>]
    let private MaxDepth = 3

    [<Literal>]
    let private MaxEntries = 20000

    [<Literal>]
    let private MaxFolders = 600

    /// Folders not worth entering: huge, and nobody ships an anti-cheat there.
    let private skip =
        HashSet<string>(
            [ "__overlay"; "shadercache"; "_commonredist"; "redist"; "redistributables"; "directx"
              "content"; "paks"; "movies"; "videos"; "archive"; "data"; "reshade-shaders" ],
            StringComparer.OrdinalIgnoreCase
        )

    let private byName (name: string) (isFolder: bool) : string option =
        let key = name.ToLowerInvariant()

        if isFolder then
            match folderNames.TryGetValue(key) with
            | true, who -> Some who
            | _ -> if looksLikeOne name then Some "" else None
        else
            match fileNames.TryGetValue(key) with
            | true, who -> Some who
            | _ ->
                if isBattlEyeLauncher name then Some "BattlEye"
                elif looksLikeOne name then Some ""
                else None

    let private walk (root: string) : string option =
        let mutable found: string option = None
        let mutable entries = 0
        let mutable folders = 0
        let pending = Queue<string * int>()
        pending.Enqueue((root, 0))

        while found.IsNone && pending.Count > 0 && entries < MaxEntries && folders < MaxFolders do
            let (dir, depth) = pending.Dequeue()
            folders <- folders + 1

            try
                // FileSystemInfo carries the attributes the listing already
                // read, so telling a folder from a file costs nothing more.
                for entry in DirectoryInfo(dir).EnumerateFileSystemInfos() do
                    if found.IsNone && entries < MaxEntries then
                        entries <- entries + 1
                        let isFolder = entry.Attributes.HasFlag(FileAttributes.Directory)

                        match byName entry.Name isFolder with
                        | Some who -> found <- Some who
                        | None ->
                            // Links are not followed: a junction can lead
                            // anywhere, including back up the tree.
                            if isFolder && depth + 1 < MaxDepth && not (skip.Contains(entry.Name))
                               && not (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) then
                                pending.Enqueue((entry.FullName, depth + 1))
            with _ ->
                ()

        found

    /// The anti-cheat this game carries, by name ("" when something is only
    /// named like one), or None. Quick - a few hundred directory entries - but
    /// it touches the disk, so callers run it off the UI thread.
    let detect (title: string) (installDir: string) (exePath: string) : string option =
        let byTitle =
            let t = squash title
            if t = "" then None
            else knownTitles |> List.tryPick (fun (key, who) -> if t.Contains(key) then Some who else None)

        match byTitle with
        | Some _ -> byTitle
        | None ->
            let exeDir =
                try
                    if String.IsNullOrWhiteSpace(exePath) then "" else Path.GetDirectoryName(exePath)
                with _ -> ""

            // The executable's folder first: it is where a protected launcher
            // or the anti-cheat's own DLL usually sits, and it is small.
            let roots =
                [ exeDir; installDir ]
                |> List.filter (fun d -> not (String.IsNullOrWhiteSpace(d)) && Directory.Exists(d))
                |> List.distinctBy (fun d -> d.TrimEnd('\\', '/').ToLowerInvariant())

            roots |> List.tryPick walk
