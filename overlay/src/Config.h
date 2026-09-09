#pragma once

#include "Common.h"
#include "IniFile.h"

namespace dlss5
{
    /// Where the HUD sits on screen.
    enum class HudCorner : int
    {
        TopLeft = 0,
        TopRight = 1,
        BottomLeft = 2,
        BottomRight = 3,
    };

    /// How much the HUD shows.
    enum class HudDetail : int
    {
        Off = 0,        ///< no HUD, panel only
        Fps = 1,        ///< one number
        Compact = 2,    ///< fps + frame time + vram
        Full = 3,       ///< everything, with the graph
    };

    /// The overlay's own settings, in "dlss5-overlay.ini" next to the game.
    ///
    /// DLSS 5 MANAGER writes this file at install time - that is how the
    /// theme picker and the on/off switch in the app reach the overlay - and
    /// the Settings tab writes it back when the user changes something in
    /// game. Both sides go through the same atomic save.
    struct Config
    {
        bool enabled = true;

        std::string theme = "Neon Emerald";

        /// Virtual-key code that toggles the panel. 0 disables the hotkey and
        /// leaves the panel reachable through ReShade's own overlay only.
        ///
        /// Shift+O by default. A letter behind a modifier is the binding least
        /// likely to collide with a game's own, and the modifiers are matched
        /// exactly - so plain O never fires it, and neither does Ctrl+Shift+O.
        int hotkey = 'O';
        bool hotkey_ctrl = false;
        bool hotkey_shift = true;
        bool hotkey_alt = false;

        HudDetail hud_detail = HudDetail::Compact;
        HudCorner hud_corner = HudCorner::TopLeft;
        float hud_scale = 1.0f;
        float hud_opacity = 0.88f;

        bool hud_show_lows = true;
        bool hud_show_vram = true;
        bool hud_show_gpu = true;
        bool hud_show_graph = true;

        float panel_opacity = 0.94f;
        float panel_scale = 1.0f;

        /// How often the hardware sensors are re-read. Half a second is slow
        /// enough that the read-out costs nothing worth measuring, and still
        /// far faster than any of these numbers actually move.
        int sensor_interval_ms = 500;

        void load();
        bool save();

        /// The file this was read from, whether or not it existed.
        const std::wstring &path() const { return m_path; }

    private:
        std::wstring m_path;
        IniFile m_file;
    };

    Config &config();
}
