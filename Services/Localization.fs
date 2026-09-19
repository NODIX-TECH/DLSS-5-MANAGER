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

        // ---- Community ------------------------------------------------------
        member _.TabCommunity = get "tab_community"
        member _.CommunitySection = get "community_section"
        member _.CommunityJoinTitle = get "community_join_title"
        member _.CommunityJoinHint = get "community_join_hint"
        member _.CommunityNamePlaceholder = get "community_name_placeholder"
        member _.BtnJoinCommunity = get "btn_join_community"
        member _.BtnRefresh = get "btn_refresh"
        member _.BtnCommunityGuides = get "btn_community_guides"
        member _.CommunityLoading = get "community_loading"
        member _.CommunityLoadingMore = get "community_loading_more"

        // ---- PULSE (chat) and the community toolbar ----------------------
        member _.PulseTitle = get "pulse_title"
        member _.PulseTagline = get "pulse_tagline"
        member _.PulsePlaceholder = get "pulse_placeholder"
        member _.PulseEmpty = get "pulse_empty"
        member _.PulseLoadingOlder = get "pulse_loading_older"
        member _.PulseImageExpired = get "pulse_image_expired"
        member _.PulseRules = get "pulse_rules"
        member _.PulseJoinFirst = get "pulse_join_first"
        member _.ChatReply = get "chat_reply"
        member _.ChatReplyingTo = get "chat_replying_to"
        member _.ChatSending = get "chat_sending"
        member _.ChatPhoto = get "chat_photo"
        member _.ChatSaveImage = get "chat_save_image"
        member _.ChatClose = get "chat_close"
        member _.ChatReact = get "chat_react"
        member _.BtnSend = get "btn_send"
        member _.BtnAttachImage = get "btn_attach_image"
        member _.BtnDelete = get "btn_delete"
        member _.CommunityTabGames = get "community_tab_games"


        // ---- LIVE FLOW ---------------------------------------------------
        // Supplied as one translated set and placed here under the app's own
        // key scheme, prefixed so none of it can collide with a name the rest
        // of the interface already owns.
        member _.LfProcessing = get "lf_processing"
        member _.LfDlss5 = get "lf_dlss_5"
        member _.LfDlss45Fg = get "lf_dlss_4_5_fg"
        member _.LfOff = get "lf_off"
        member _.LfOn = get "lf_on"
        member _.LfEnhance = get "lf_enhance"
        member _.LfMultiPass = get "lf_multi_pass"
        member _.LfNetworkResolution = get "lf_network_resolution"
        member _.LfFast = get "lf_fast"
        member _.LfBalanced = get "lf_balanced"
        member _.LfQuality = get "lf_quality"
        member _.LfSource = get "lf_source"
        member _.LfRefresh = get "lf_refresh"
        member _.LfReconnect = get "lf_reconnect"
        member _.LfSelectAWindow = get "lf_select_a_window"
        member _.LfGoToApp = get "lf_go_to_app"
        member _.LfClear = get "lf_clear"
        member _.LfEffect = get "lf_effect"
        member _.LfProfile = get "lf_profile"
        member _.LfModel = get "lf_model"
        member _.LfIntensity = get "lf_intensity"
        member _.LfLocalTone = get "lf_local_tone"
        member _.LfLocalStructure = get "lf_local_structure"
        member _.LfSkinStructure = get "lf_skin_structure"
        member _.LfComparison = get "lf_comparison"
        member _.LfBeforeAfterWipe = get "lf_before_after_wipe"
        member _.LfMyPresets = get "lf_my_presets"
        member _.LfPresetName = get "lf_preset_name"
        member _.LfSavedPresets = get "lf_saved_presets"
        member _.LfNameYourPreset = get "lf_name_your_preset"
        member _.LfChooseAPreset = get "lf_choose_a_preset"
        member _.LfSaveNew = get "lf_save_new"
        member _.LfUpdateSelected = get "lf_update_selected"
        member _.LfApply = get "lf_apply"
        member _.LfDelete = get "lf_delete"
        member _.LfSettings = get "lf_settings"
        member _.LfProcessingGpu = get "lf_processing_gpu"
        member _.LfDisplay = get "lf_display"
        member _.LfMotionEstimation = get "lf_motion_estimation"
        member _.LfSkipStaticFrames = get "lf_skip_static_frames"
        member _.LfHdrCompatibility = get "lf_hdr_compatibility"
        member _.LfCapture = get "lf_capture"
        member _.LfMicrophone = get "lf_microphone"
        member _.LfRecordingRate = get "lf_recording_rate"
        member _.LfSystemAudio = get "lf_system_audio"
        member _.LfChooseFolder = get "lf_choose_folder"
        member _.LfScreenshot = get "lf_screenshot"
        member _.LfRecord = get "lf_record"
        member _.LfObsOutputSpout2 = get "lf_obs_output_spout2"
        member _.LfObsHelp = get "lf_obs_help"
        member _.LfGetObsSpout2Plugin = get "lf_get_obs_spout2_plugin"
        member _.LfNone = get "lf_none"
        member _.LfForCreators = get "lf_for_creators"
        member _.LfObsGuide = get "lf_obs_guide"
        member _.LfObsGuideTitle = get "lf_obs_guide_title"
        member _.LfObsSteps = get "lf_obs_steps"
        member _.LfShortcutOverlay = get "lf_shortcut_overlay"
        member _.LfShortcutHintOverlay = get "lf_shortcut_hint_overlay"
        member _.LfShortcutDlss = get "lf_shortcut_dlss"
        member _.LfShortcutHintDlss = get "lf_shortcut_hint_dlss"
        member _.LfShortcutListening = get "lf_shortcut_listening"
        member _.LfShortcutPresets = get "lf_shortcut_presets"
        member _.LfShortcutChange = get "lf_shortcut_change"
        member _.LfOpenOverlayNow = get "lf_open_overlay_now"
        // ---- The gallery -------------------------------------------------
        member _.GalleryTitle = get "gallery_title"
        member _.GalleryTagline = get "gallery_tagline"
        member _.GalleryWall = get "gallery_wall"
        member _.GalleryQueue = get "gallery_queue"
        member _.GalleryUpload = get "gallery_upload"
        member _.GalleryCaption = get "gallery_caption"
        member _.GalleryEmpty = get "gallery_empty"
        member _.GalleryQueueEmpty = get "gallery_queue_empty"
        member _.GalleryApprove = get "gallery_approve"
        member _.GalleryReject = get "gallery_reject"
        member _.GalleryPromote = get "gallery_promote"
        member _.GalleryModeration = get "gallery_moderation"
        member _.FilterRoute = get "filter_route"
        member _.FilterResult = get "filter_result"
        member _.FilterSort = get "filter_sort"
        member _.FilterClear = get "filter_clear"
        member _.SearchingFor = get "searching_for"
        member _.CommunityEmptyTitle = get "community_empty_title"
        member _.CommunityEmptyHint = get "community_empty_hint"
        member _.CommunityShare = get "community_share"
        member _.CommunityPostTitle = get "community_post_title"
        member _.CommunityResult = get "community_result"
        member _.CommunityInstallMethod = get "community_install_method"
        member _.CommunityNote = get "community_note"
        member _.CommunityNotePlaceholder = get "community_note_placeholder"
        member _.CommunitySpecs = get "community_specs"
        member _.BtnDetectSpecs = get "btn_detect_specs"
        member _.BtnRemoveSpecs = get "btn_remove_specs"
        member _.BtnPost = get "btn_post"
        member _.BtnReply = get "btn_reply"
        member _.ReplyPlaceholder = get "reply_placeholder"
        member _.NoReportsTitle = get "no_reports_title"
        member _.StatusWorking = get "status_working"
        member _.StatusMixed = get "status_mixed"
        member _.StatusBroken = get "status_broken"

        // ---- Features card -------------------------------------------------
        member _.FeaturesSection = get "features_section"
        member _.PerformanceMode = get "performance_mode"
        member _.PerformanceTagline = get "performance_tagline"
        member _.AmdTagline = get "amd_tagline"
        member _.SupportTagline = get "support_tagline"

        // ---- Dynamic Overlay ------------------------------------------------
        member _.OverlaySection = get "overlay_section"
        member _.OverlayTitle = get "overlay_title"
        member _.OverlayTagline = get "overlay_tagline"
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

        /// Same order as `ModInstaller.overlayThemes`. The English name is what
        /// is saved and what the in-game add-on reads; this is only what the
        /// style picker shows.
        member _.OverlayThemeNames =
            [| get "overlay_theme_neon_emerald"
               get "overlay_theme_cyber_cyan"
               get "overlay_theme_electric_violet"
               get "overlay_theme_supernova_amber"
               get "overlay_theme_eclipse_crimson"
               get "overlay_theme_graphite_minimal" |]

        // ---- Community toolbar dropdowns ----------------------------------
        /// Same order as `CommunityFilters.routeKeys`. OptiScaler, ReShade and
        /// AMD are names and read the same in every language.
        member _.CommunityRouteOptions =
            [| get "filter_route_all"; "OptiScaler"; "ReShade"; get "route_emulator"; "AMD" |]

        /// Same order as `CommunityFilters.resultKeys`.
        member _.CommunityResultOptions =
            [| get "filter_result_any"; get "result_working"; get "result_mixed"; get "result_broken" |]

        /// Same order as `CommunityFilters.sortKeys`.
        member _.CommunitySortOptions =
            [| get "sort_recent"; get "sort_reports"; get "sort_title" |]

        // ---- Counters ------------------------------------------------------
        /// "TOTAL GAMES: {count}" with the placeholder filled in.
        member _.TotalGames(count: int) =
            (get "total_games").Replace("{count}", string count)

        member _.StatusGamesReady(count: int) =
            (get "status_games_ready").Replace("{count}", string count)

    /// The strings the whole app is showing right now.
    ///
    /// MainViewModel owns the language and writes this whenever it changes, so
    /// a view that is not bound to MainViewModel - LIVE FLOW and its overlay,
    /// which carry their own view-model - can read the same set without it
    /// being threaded through every constructor.
    let mutable current = Strings("en")
