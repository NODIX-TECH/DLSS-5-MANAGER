#include "Config.h"

#include <charconv>

namespace dlss5
{
    namespace
    {
        constexpr const char *kSection = "Overlay";

        bool to_bool(std::string_view text, bool fallback)
        {
            if (iequals(text, "true") || text == "1" || iequals(text, "yes") || iequals(text, "on"))
                return true;

            if (iequals(text, "false") || text == "0" || iequals(text, "no") || iequals(text, "off"))
                return false;

            return fallback;
        }

        int to_int(std::string_view text, int fallback)
        {
            text = trim(text);

            if (text.empty())
                return fallback;

            int value = 0;

            // Hex is how virtual-key codes are written everywhere else, so a
            // key written as 0x77 has to read back as 119.
            if (text.size() > 2 && text[0] == '0' && (text[1] == 'x' || text[1] == 'X'))
            {
                const auto result = std::from_chars(text.data() + 2, text.data() + text.size(), value, 16);
                return result.ec == std::errc() ? value : fallback;
            }

            const auto result = std::from_chars(text.data(), text.data() + text.size(), value);
            return result.ec == std::errc() ? value : fallback;
        }

        float to_float(std::string_view text, float fallback)
        {
            text = trim(text);

            if (text.empty())
                return fallback;

            // from_chars for float is available but the INI may carry a comma
            // decimal separator from a hand edit; strtof with the C locale is
            // the forgiving option and this is not a hot path.
            const std::string copy(text);
            char *end = nullptr;
            const float value = strtof(copy.c_str(), &end);

            return (end != nullptr && end != copy.c_str()) ? value : fallback;
        }

        std::string from_bool(bool value) { return value ? "true" : "false"; }

        std::string from_int(int value) { return std::to_string(value); }

        std::string from_float(float value)
        {
            char buffer[32] = {};
            snprintf(buffer, sizeof(buffer), "%.3f", value);
            return buffer;
        }

        template <typename Enum>
        Enum clamp_enum(int value, int low, int high, Enum fallback)
        {
            return value >= low && value <= high ? static_cast<Enum>(value) : fallback;
        }
    }

    void Config::load()
    {
        m_path = game_path(L"dlss5-overlay.ini");
        m_file.load(m_path);

        const auto read_bool = [&](const char *key, bool fallback) {
            return to_bool(m_file.get_or(kSection, key, from_bool(fallback)), fallback);
        };
        const auto read_int = [&](const char *key, int fallback) {
            return to_int(m_file.get_or(kSection, key, from_int(fallback)), fallback);
        };
        const auto read_float = [&](const char *key, float fallback) {
            return to_float(m_file.get_or(kSection, key, from_float(fallback)), fallback);
        };

        enabled = read_bool("Enabled", enabled);
        theme = m_file.get_or(kSection, "Theme", theme);

        hotkey = read_int("HotKey", hotkey);
        hotkey_ctrl = read_bool("HotKeyCtrl", hotkey_ctrl);
        hotkey_shift = read_bool("HotKeyShift", hotkey_shift);
        hotkey_alt = read_bool("HotKeyAlt", hotkey_alt);

        hud_detail = clamp_enum<HudDetail>(read_int("HudDetail", static_cast<int>(hud_detail)), 0, 3, hud_detail);
        hud_corner = clamp_enum<HudCorner>(read_int("HudCorner", static_cast<int>(hud_corner)), 0, 3, hud_corner);
        hud_scale = std::clamp(read_float("HudScale", hud_scale), 0.5f, 3.0f);
        hud_opacity = std::clamp(read_float("HudOpacity", hud_opacity), 0.1f, 1.0f);

        hud_show_lows = read_bool("HudShowLows", hud_show_lows);
        hud_show_vram = read_bool("HudShowVram", hud_show_vram);
        hud_show_gpu = read_bool("HudShowGpu", hud_show_gpu);
        hud_show_graph = read_bool("HudShowGraph", hud_show_graph);

        panel_opacity = std::clamp(read_float("PanelOpacity", panel_opacity), 0.3f, 1.0f);
        panel_scale = std::clamp(read_float("PanelScale", panel_scale), 0.6f, 2.0f);

        sensor_interval_ms = std::clamp(read_int("SensorIntervalMs", sensor_interval_ms), 50, 2000);
    }

    bool Config::save()
    {
        if (m_path.empty())
            m_path = game_path(L"dlss5-overlay.ini");

        m_file.set(kSection, "Enabled", from_bool(enabled));
        m_file.set(kSection, "Theme", theme);

        m_file.set(kSection, "HotKey", from_int(hotkey));
        m_file.set(kSection, "HotKeyCtrl", from_bool(hotkey_ctrl));
        m_file.set(kSection, "HotKeyShift", from_bool(hotkey_shift));
        m_file.set(kSection, "HotKeyAlt", from_bool(hotkey_alt));

        m_file.set(kSection, "HudDetail", from_int(static_cast<int>(hud_detail)));
        m_file.set(kSection, "HudCorner", from_int(static_cast<int>(hud_corner)));
        m_file.set(kSection, "HudScale", from_float(hud_scale));
        m_file.set(kSection, "HudOpacity", from_float(hud_opacity));

        m_file.set(kSection, "HudShowLows", from_bool(hud_show_lows));
        m_file.set(kSection, "HudShowVram", from_bool(hud_show_vram));
        m_file.set(kSection, "HudShowGpu", from_bool(hud_show_gpu));
        m_file.set(kSection, "HudShowGraph", from_bool(hud_show_graph));

        m_file.set(kSection, "PanelOpacity", from_float(panel_opacity));
        m_file.set(kSection, "PanelScale", from_float(panel_scale));

        m_file.set(kSection, "SensorIntervalMs", from_int(sensor_interval_ms));

        return m_file.save_as(m_path);
    }

    Config &config()
    {
        static Config instance;
        return instance;
    }
}
