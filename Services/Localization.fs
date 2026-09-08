namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.Text.Json
open System.Collections.Generic

/// User interface translations.
///
/// Every string lives in `languages\languages.JSON` - one object per language
/// code, the same 44 keys in each. English is the fallback, so a key that is
/// ever missing shows readable text instead of blowing up.
///
/// The UI does not bind to the dictionary directly. It binds through a
/// `Strings` snapshot exposed as one property, so switching language means
/// swapping a single object and every label on screen re-reads itself - no
/// per-string change notifications to keep in sync.
module Localization =

    /// Display names stay in their own language: someone who cannot read the
    /// current UI language still has to be able to find their own.
    let availableLanguages =
        [| "en", "English"
           "ar", "العربية"
           "fa", "فارسی"
           "tr", "Türkçe"
           "es", "Español"
           "ru", "Русский"
           "uk", "Українська"
           "fr", "Français"
           "de", "Deutsch"
           "it", "Italiano"
           "pt", "Português"
           "zh", "中文" |]

    let private empty = Dictionary<string, string>() :> IReadOnlyDictionary<string, string>

    let private table =
        lazy
            (try
                let candidates =
                    [ Path.Combine(AppContext.BaseDirectory, "languages", "languages.JSON")
                      Path.Combine(AppContext.BaseDirectory, "languages", "languages.json") ]

                match candidates |> List.tryFind File.Exists with
                | None -> Dictionary<string, Dictionary<string, string>>()
                | Some path ->
                    let options = JsonSerializerOptions()
                    options.PropertyNameCaseInsensitive <- true

                    let parsed =
                        JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(
                            File.ReadAllText(path),
                            options
                        )

                    if isNull (box parsed) then Dictionary<string, Dictionary<string, string>>() else parsed
             with _ ->
                 Dictionary<string, Dictionary<string, string>>())

    let private forCode (code: string) : IReadOnlyDictionary<string, string> =
        match table.Value.TryGetValue(if isNull code then "" else code) with
        | true, v -> v :> IReadOnlyDictionary<string, string>
        | _ -> empty

    let isKnownLanguage (code: string) =
        availableLanguages |> Array.exists (fun (c, _) -> c = code)

    let displayName (code: string) =
        availableLanguages
        |> Array.tryFind (fun (c, _) -> c = code)
        |> Option.map snd
        |> Option.defaultValue "English"

    /// The language the OS is set to, when we have it - a first run should
    /// already be in the user's own language.
    let systemLanguage () =
        try
            let code = Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            if isKnownLanguage code then code else "en"
        with _ ->
            "en"

    /// One language's strings, resolved once. Bound to as `Loc.Games` and
    /// friends; replacing the whole object is what refreshes the window.
    type Strings(code: string) =
        let primary = forCode code
        let fallback = forCode "en"

        let get (key: string) =
            match primary.TryGetValue(key) with
            | true, v when not (String.IsNullOrWhiteSpace(v)) -> v
            | _ ->
                match fallback.TryGetValue(key) with
                | true, v -> v
                | _ -> key

        member _.Code = code

        // ---- Header / navigation ----------------------------------------
        member _.ClickManage = get "click_manage"
        member _.DragMove = get "drag_move"
        member _.Games = get "games"
        member _.AddGame = get "add_game"
        member _.SearchGames = get "search_games"
        member _.Settings = get "settings"
        member _.Minimize = get "minimize"
        member _.Maximize = get "maximize"
        member _.Close = get "close"
        member _.BackToGames = get "back_to_games"

        // ---- Settings ----------------------------------------------------
        member _.SettingsPreferences = get "settings_preferences"
        member _.InterfaceNavigation = get "interface_navigation"
        member _.NavbarPosition = get "navbar_position"
        member _.TopBar = get "top_bar"
        member _.SideBar = get "side_bar"
        member _.VisualAtmosphere = get "visual_atmosphere"
        member _.ColorAtmosphere = get "color_atmosphere"
        member _.GeometricMotif = get "geometric_motif"
        member _.LibraryCache = get "library_cache"
        member _.FastCacheGameIndex = get "fast_cache_game_index"
        member _.RescanLibrary = get "rescan_library"
        member _.ClearCache = get "clear_cache"
        member _.ModPayloadFiles = get "mod_payload_files"
        member _.ModPayloadDesc = get "mod_payload_desc"
        member _.Replace = get "replace"
        member _.Restore = get "restore"

        // ---- Manage sheet ------------------------------------------------
        member _.Installed = get "installed"
        member _.NotInstalled = get "not_installed"
        member _.OpenGameFolder = get "open_game_folder"
        member _.ChangeExe = get "change_exe"
        member _.RemoveDlss = get "remove_dlss"
        member _.InstallMode = get "install_mode"
        member _.OptiScalerDesc = get "optiscaler_desc"

        // ---- Emulators ----------------------------------------------------
        member _.TabEmulators = get "tab_emulators"
        member _.NoEmulatorsTitle = get "no_emulators_title"
        member _.NoEmulatorsDesc = get "no_emulators_desc"
        member _.BtnAddEmulator = get "btn_add_emulator"
        member _.BtnDetectEmulators = get "btn_detect_emulators"

        // ---- Support & about ----------------------------------------------
        member _.SectionSupportTutorials = get "section_support_tutorials"
        member _.SupportTitle = get "support_title"
        member _.SupportDesc = get "support_desc"
        member _.BtnWatchTutorials = get "btn_watch_tutorials"
        member _.BtnSupportKofi = get "btn_support_kofi"
        member _.AppTitle = get "app_title"
        member _.CreditsBuiltBy = get "credits_built_by"
        member _.BtnCheckUpdates = get "btn_check_updates"
        member _.Copyright = get "copyright"

        // ---- Manage sheet, AMD mode, extras --------------------------------
        member _.GameExecutable = get "game_executable"
        member _.BtnInstall = get "btn_install"
        member _.AmdMode = get "amd_mode"
        member _.Beta = get "beta"
        member _.AmdTitle = get "amd_title"
        member _.AmdDesc = get "amd_desc"

        // ---- In-game overlay ----------------------------------------------
        member _.OverlaySection = get "overlay_section"
        member _.OverlayTitle = get "overlay_title"
        member _.OverlayDesc = get "overlay_desc"
        member _.OverlayStyle = get "overlay_style"
        member _.OverlayHotkey = get "overlay_hotkey"
        member _.OverlayHotkeyDesc = get "overlay_hotkey_desc"
        member _.Extras = get "extras"
        member _.ExtrasDesc = get "extras_desc"
        member _.BtnAddExtra = get "btn_add_extra"

        // ---- Theme names --------------------------------------------------
        /// Same order as `MainViewModel.atmosphereKeys`; the English key is what
        /// gets saved and matched, this is only what the user reads.
        member _.AtmosphereNames =
            [| get "theme_neon_emerald"
               get "theme_obsidian_onyx"
               get "theme_supernova_flare"
               get "theme_cyber_nebula"
               get "theme_emerald_horizon"
               get "theme_midnight_titanium"
               get "theme_frost_glacier"
               get "theme_eclipse_crimson"
               get "theme_deep_astral" |]

        /// Same order as `MainViewModel.GeometricMotifs`.
        member _.MotifNames =
            [| get "theme_orbital_spheres"
               get "theme_prism_auroras"
               get "theme_floating_crystals"
               get "theme_stardust_particles"
               get "theme_cyber_flux"
               get "theme_minimal_clean" |]

        // ---- Counters ------------------------------------------------------
        /// "TOTAL GAMES: {count}" with the placeholder filled in.
        member _.TotalGames(count: int) =
            (get "total_games").Replace("{count}", string count)

        member _.StatusGamesReady(count: int) =
            (get "status_games_ready").Replace("{count}", string count)
