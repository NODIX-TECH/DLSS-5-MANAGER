namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.Text.Json

/// User-supplied additions to an install, and the switches that decide which
/// of the bundled payload files travel with it.
///
/// Both live in one small file under %LOCALAPPDATA% because they answer the
/// same question - "what actually gets copied next to the game" - and the
/// installer reads them together.
module ExtrasStore =

    [<CLIMutable>]
    type ExtraItem =
        { /// What the settings row shows: the file or folder name.
          Name: string
          /// Where it is read from. The original stays where the user put it.
          SourcePath: string
          /// A folder is deployed as a folder; a file lands beside the exe.
          IsFolder: bool
          Enabled: bool
          /// Install routes this travels with, by their manifest keys
          /// ("optiscaler" / "dx12" / "dx11" / "dx9" / "amd" / "emulator").
          /// Empty means every route, which is what a new extra starts as.
          Modes: string[]
          /// Everything picked in one go shares this, so a folder's worth of
          /// files reads as one entry instead of twenty. Empty on entries
          /// added before grouping existed - each of those is its own group.
          GroupId: string }

    [<CLIMutable>]
    type ExtrasConfig =
        { Extras: ExtraItem[]
          /// Bundled payload filenames the user has switched off.
          DisabledPayloads: string[] }

    let private emptyConfig = { Extras = [||]; DisabledPayloads = [||] }

    let private storePath () =
        let dir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS5Manager")

        Directory.CreateDirectory(dir) |> ignore
        Path.Combine(dir, "extras.json")

    let load () : ExtrasConfig =
        try
            let path = storePath ()

            if File.Exists(path) then
                let options = JsonSerializerOptions()
                options.PropertyNameCaseInsensitive <- true
                let c = JsonSerializer.Deserialize<ExtrasConfig>(File.ReadAllText(path), options)

                { Extras = (if isNull (box c.Extras) then [||] else c.Extras)
                  DisabledPayloads = (if isNull (box c.DisabledPayloads) then [||] else c.DisabledPayloads) }
            else
                emptyConfig
        with _ ->
            emptyConfig

    let save (config: ExtrasConfig) : unit =
        try
            let options = JsonSerializerOptions()
            options.WriteIndented <- true
            File.WriteAllText(storePath (), JsonSerializer.Serialize(config, options))
        with _ ->
            ()

    // =====================================================================
    // PAYLOAD SWITCHES
    // =====================================================================
    /// A payload is on unless it has been explicitly switched off, so a file
    /// the user has never touched always ships.
    let isPayloadEnabled (fileName: string) : bool =
        let config = load ()

        config.DisabledPayloads
        |> Array.exists (fun n -> String.Equals(n, fileName, StringComparison.OrdinalIgnoreCase))
        |> not

    let setPayloadEnabled (fileName: string) (enabled: bool) : unit =
        let config = load ()

        let without =
            config.DisabledPayloads
            |> Array.filter (fun n -> not (String.Equals(n, fileName, StringComparison.OrdinalIgnoreCase)))

        let updated =
            if enabled then without else Array.append without [| fileName |]

        save { config with DisabledPayloads = updated }

    // =====================================================================
    // EXTRAS
    // =====================================================================
    let list () : ExtraItem list = load () |> fun c -> List.ofArray c.Extras

    /// An entry from before grouping existed stands alone under its own path.
    let groupKey (item: ExtraItem) =
        if isNull (box item.GroupId) || String.IsNullOrWhiteSpace(item.GroupId) then
            item.SourcePath
        else
            item.GroupId

    /// Every group, in the order the entries were added.
    let groups () : (string * ExtraItem list) list =
        list () |> List.groupBy groupKey

    /// Everything picked in one go lands as a single group, so a folder's
    /// worth of files reads as one entry instead of twenty. Paths already in
    /// the list are skipped rather than added twice.
    let addBatch (picks: (string * bool) list) : int =
        let config = load ()

        let alreadyThere (path: string) =
            config.Extras
            |> Array.exists (fun e -> String.Equals(e.SourcePath, path, StringComparison.OrdinalIgnoreCase))

        let fresh = picks |> List.filter (fun (path, _) -> not (alreadyThere path))

        if fresh.IsEmpty then
            0
        else
            let groupId = Guid.NewGuid().ToString("N")

            let added =
                fresh
                |> List.map (fun (path, isFolder) ->
                    { Name = Path.GetFileName(path.TrimEnd('\\', '/'))
                      SourcePath = path
                      IsFolder = isFolder
                      Enabled = true
                      Modes = [||]
                      GroupId = groupId })
                |> List.toArray

            save { config with Extras = Array.append config.Extras added }
            added.Length

    /// Which routes carry this extra. No selection at all means every route -
    /// the useful default, and what the picker shows as "ALL".
    ///
    /// `keys` carries both the plain route ("dx11") and the exact variant
    /// ("dx11-32"), so a selection made before variants existed still matches
    /// and a new one can be as specific as the user wants.
    let appliesTo (item: ExtraItem) (keys: string list) =
        isNull (box item.Modes)
        || item.Modes.Length = 0
        || item.Modes
           |> Array.exists (fun m -> keys |> List.exists (fun k -> String.Equals(m, k, StringComparison.OrdinalIgnoreCase)))


    // =====================================================================
    // GROUP OPERATIONS
    // =====================================================================
    // Everything the user acts on is a group, so these all take a group key
    // and apply to every entry inside it at once.
    let private updateGroup (key: string) (f: ExtraItem -> ExtraItem) : unit =
        let config = load ()

        let updated =
            config.Extras
            |> Array.map (fun e -> if String.Equals(groupKey e, key, StringComparison.OrdinalIgnoreCase) then f e else e)

        save { config with Extras = updated }

    let removeGroup (key: string) : unit =
        let config = load ()

        let kept =
            config.Extras
            |> Array.filter (fun e -> not (String.Equals(groupKey e, key, StringComparison.OrdinalIgnoreCase)))

        save { config with Extras = kept }

    let setGroupEnabled (key: string) (enabled: bool) : unit =
        updateGroup key (fun e -> { e with Enabled = enabled })

    let toggleGroupMode (key: string) (modeKey: string) : unit =
        let config = load ()

        let current =
            config.Extras
            |> Array.tryFind (fun e -> String.Equals(groupKey e, key, StringComparison.OrdinalIgnoreCase))
            |> Option.map (fun e -> if isNull (box e.Modes) then [||] else e.Modes)
            |> Option.defaultValue [||]

        let next =
            if current |> Array.exists (fun m -> String.Equals(m, modeKey, StringComparison.OrdinalIgnoreCase)) then
                current |> Array.filter (fun m -> not (String.Equals(m, modeKey, StringComparison.OrdinalIgnoreCase)))
            else
                Array.append current [| modeKey |]

        updateGroup key (fun e -> { e with Modes = next })

    /// Back to every route.
    let clearGroupModes (key: string) : unit = updateGroup key (fun e -> { e with Modes = [||] })
