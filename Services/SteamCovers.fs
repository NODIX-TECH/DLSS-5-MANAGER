namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Collections.Generic
open System.Threading

/// Cover art for games the library found no picture for.
///
/// The community grid has always looked better than the local one, and the
/// reason is not luck: the server resolves every title to Steam's own library
/// artwork - straight from the AppId when it knows one, and through the
/// storefront's search when it does not. These are the same two steps, done
/// here, for the games on this machine.
///
/// The artwork stays on Steam's CDN and is kept on disk afterwards, so a title
/// is looked up once and never again - including the ones that turn out to
/// have no artwork at all, which are remembered as misses so they are not
/// asked about on every launch.
module SteamCovers =

    let private coverUrl (appId: string) =
        sprintf
            "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/%s/library_600x900.jpg"
            appId

    let cacheDir =
        lazy
            (let dir =
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DLSS5Manager",
                    "Cache",
                    "Covers"
                )

             try
                 Directory.CreateDirectory(dir) |> ignore
             with _ ->
                 ()

             dir)

    let private client =
        lazy
            (let c = new HttpClient()
             c.Timeout <- TimeSpan.FromSeconds(20.)
             c.DefaultRequestHeaders.Add("User-Agent", "DLSS5MANAGER")
             c)

    /// Four at a time. A library of two hundred games would otherwise open two
    /// hundred connections the moment the grid is drawn.
    let private gate = new SemaphoreSlim(4)

    let private safeName (key: string) =
        use sha = SHA256.Create()

        sha.ComputeHash(Encoding.UTF8.GetBytes(key))
        |> Array.take 12
        |> Array.map (fun b -> b.ToString("x2"))
        |> String.concat ""

    /// The numeric Steam id the scanner already knows, when it knows one.
    /// "steam_1091500" carries it; no other launcher's id does.
    let steamIdOf (appId: string) =
        if String.IsNullOrWhiteSpace(appId) then ""
        elif appId.StartsWith("steam_", StringComparison.OrdinalIgnoreCase) then appId.Substring(6)
        else ""

    /// Roman numerals as the digits they stand for, so "Grand Theft Auto V"
    /// and "Grand Theft Auto 5" are one title. "i" is left alone: it is far
    /// more often the word than the number.
    let private numerals =
        dict [ "ii", "2"; "iii", "3"; "iv", "4"; "v", "5"; "vi", "6"; "vii", "7"; "viii", "8"
               "ix", "9"; "x", "10"; "xi", "11"; "xii", "12"; "xiii", "13" ]

    /// A title reduced to what two spellings of the same game share: case,
    /// trademark signs and punctuation all go, and numerals become digits.
    let private normalise (text: string) =
        if String.IsNullOrWhiteSpace(text) then
            ""
        else
            let lowered = text.ToLowerInvariant().Replace("\u2122", "").Replace("\u00ae", "").Replace("\u00a9", "")

            // Accents folded to their letter: the store says "Ragnar\u00f6k", a
            // launcher or a folder often says "Ragnarok".
            let folded =
                lowered.Normalize(NormalizationForm.FormD)
                |> Seq.filter (fun c ->
                    Globalization.CharUnicodeInfo.GetUnicodeCategory(c) <> Globalization.UnicodeCategory.NonSpacingMark)
                |> Seq.toArray
                |> String

            Regex.Replace(folded, "[^a-z0-9]+", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun t ->
                match numerals.TryGetValue(t) with
                | true, digits -> digits
                | _ -> t)
            |> String.concat " "

    /// Words that name an edition of a game rather than a different game:
    /// "Cyberpunk 2077 Ultimate Edition" is still Cyberpunk 2077.
    let private editionWords =
        HashSet<string>(
            [ "edition"; "remastered"; "remaster"; "definitive"; "goty"; "game"; "of"; "year"
              "complete"; "deluxe"; "director"; "directors"; "s"; "cut"; "enhanced"; "ultimate"
              "legacy"; "gold"; "premium"; "standard"; "anniversary"; "hd"; "classic"; "redux" ]
        )

    /// How sure we are that a storefront result is this game, 0 to 100. 50 is
    /// the bar for "the same game". Public because the library scanner checks
    /// its own search results with it too, so both agree on what a match is.
    ///
    /// The numbers in a title decide first. A sequel, a remake's year or the
    /// next game in a series differs from the one wanted by exactly a number,
    /// and every other test here would call those close: "Alan Wake 2" starts
    /// with "Alan Wake", and "Red Dead Redemption" shares three words of four
    /// with "Red Dead Redemption 2". That closeness is how a card came to show
    /// the other game's cover. Different numbers, different game - score 0.
    let score (wanted: string) (candidate: string) =
        // "The" is dropped only for comparing: "Witcher 3" is The Witcher 3.
        let words (text: string) =
            (normalise text).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            |> Array.filter (fun t -> t <> "the")

        let ta = words wanted
        let tb = words candidate
        let numbers (tokens: string[]) =
            tokens |> Array.filter (fun t -> t |> Seq.forall Char.IsDigit) |> Set.ofArray

        if ta.Length = 0 || tb.Length = 0 then 0
        elif ta = tb then 100
        elif numbers ta <> numbers tb then 0
        else
            // One title is the other plus edition words: the same game.
            let (shorter, longer) = if ta.Length <= tb.Length then (ta, tb) else (tb, ta)

            let isEdition =
                longer.[.. shorter.Length - 1] = shorter
                && longer.[shorter.Length ..] |> Array.forall editionWords.Contains

            if isEdition then
                90
            else
                let sa = HashSet<string>(ta)
                let sb = HashSet<string>(tb)
                let common = sa |> Seq.filter sb.Contains |> Seq.length
                let union = HashSet<string>(Seq.append sa sb).Count
                if union = 0 then 0 else int (70.0 * float common / float union)

    /// Asks the storefront which game a title is - the same endpoint the server
    /// uses - and takes the result whose name actually matches.
    ///
    /// It used to take the first result whatever it was called, which is how a
    /// card came to show another game's art: the search ranks by popularity,
    /// not by how close the name is. Nothing close enough means no cover at
    /// all, which is better than the wrong one.
    ///
    /// Some id when a result matched, Some "" when the store answered and
    /// nothing did, None when it could not be asked at all. Those last two used
    /// to be the same "", so a search that merely timed out was remembered as
    /// "this game has no cover" - for good.
    let private searchAppId (title: string) : string option =
        try
            let url =
                "https://store.steampowered.com/api/storesearch/?cc=us&l=en&term=" + Uri.EscapeDataString(title)

            let text = client.Value.GetStringAsync(url).GetAwaiter().GetResult()
            use doc = JsonDocument.Parse(text)

            match doc.RootElement.TryGetProperty("items") with
            | true, items when items.ValueKind = JsonValueKind.Array ->
                let best =
                    items.EnumerateArray()
                    |> Seq.choose (fun item ->
                        match item.TryGetProperty("id"), item.TryGetProperty("name") with
                        | (true, id), (true, name) -> Some(string (id.GetInt32()), score title (name.GetString()))
                        | _ -> None)
                    |> Seq.sortByDescending snd
                    |> Seq.tryHead

                match best with
                | Some(id, sure) when sure >= 50 -> Some id
                | _ -> Some ""
            | _ -> Some ""
        with _ ->
            None

    /// The artwork itself: Some bytes, Some [||] when Steam has none for this
    /// game, None when the download failed and is worth trying another time.
    let private downloadCover (steamId: string) : byte[] option =
        try
            use response = client.Value.GetAsync(coverUrl steamId).GetAwaiter().GetResult()

            if response.StatusCode = Net.HttpStatusCode.NotFound
               || response.StatusCode = Net.HttpStatusCode.Forbidden then
                Some [||]
            elif not response.IsSuccessStatusCode then
                None
            else
                let bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()

                // Steam answers some missing images with a tiny placeholder
                // rather than a 404, so size is what separates art from nothing.
                if bytes.Length < 1024 then Some [||] else Some bytes
        with _ ->
            None

    /// The cached file for a game, fetched the first time it is asked for.
    ///
    /// Returns "" when there is nothing to show: a missing cover is a
    /// placeholder on a card, never an error anybody has to read.
    let resolve (appId: string) (title: string) : string =
        try
            // A Steam id is exact and keyed by itself. Anything found by name
            // is keyed under a prefix that changes whenever the matching does,
            // so a cover an older rule got wrong is looked up again rather than
            // kept forever. A title with no Latin letters at all normalises to
            // nothing, so it is keyed by itself - otherwise every such game
            // would share one cache entry.
            let knownId = steamIdOf appId

            // "s2": misses recorded under "s" may be downloads that only timed
            // out, from before the two were told apart, so they are asked again.
            let key =
                if knownId <> "" then "s2|" + knownId
                else
                    match normalise title with
                    | "" when not (String.IsNullOrWhiteSpace(title)) -> "t3|" + title.Trim().ToLowerInvariant()
                    | "" -> ""
                    | name -> "t3|" + name

            if String.IsNullOrWhiteSpace(key) then
                ""
            else

            let file = Path.Combine(cacheDir.Value, safeName key + ".jpg")
            let miss = Path.Combine(cacheDir.Value, safeName key + ".none")

            if File.Exists(file) then file
            elif File.Exists(miss) then ""
            else

            gate.Wait()

            try
                // Another card may have fetched it while this one waited.
                if File.Exists(file) then
                    file
                else

                // Only a real answer is remembered. A miss is written when the
                // store said "no such game" or Steam has no artwork for it; a
                // network that failed leaves nothing behind, so the card asks
                // again next time instead of keeping its icon for good.
                let remember () =
                    try File.WriteAllText(miss, "") with _ -> ()
                    ""

                match (if knownId <> "" then Some knownId else searchAppId title) with
                | None -> ""
                | Some "" -> remember ()
                | Some steamId ->
                    match downloadCover steamId with
                    | None -> ""
                    | Some bytes when bytes.Length = 0 -> remember ()
                    | Some bytes ->
                        File.WriteAllBytes(file, bytes)
                        file
            finally
                gate.Release() |> ignore
        with _ ->
            ""
