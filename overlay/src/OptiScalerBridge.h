#pragma once

#include "Common.h"
#include "IniFile.h"

namespace dlss5
{
    /// What kind of control a setting deserves.
    enum class SettingKind
    {
        Bool,
        Int,
        Float,
        Choice,   ///< an integer with named values
        Text,     ///< free string, drawn as a combo of known values
    };

    /// One row in a settings tab.
    ///
    /// The table below is the whole UI definition: the tabs walk it and draw
    /// whatever it says, so adding a setting is one line here and nothing
    /// else. The documentation OptiScaler writes above each key in its own ini
    /// becomes the tooltip, which is why nothing here repeats that text.
    struct Setting
    {
        const char *section;
        const char *key;
        const char *label;
        SettingKind kind;
        float minimum;
        float maximum;
        const char *const *choices;   ///< Choice / Text: null-terminated array
        int choice_count;
    };

    /// A group of settings drawn under one heading.
    struct SettingGroup
    {
        const char *title;
        const Setting *settings;
        int count;
    };

    /// Live view of the game's OptiScaler.ini.
    ///
    /// OptiScaler reads its configuration when it starts and exposes its own
    /// in-game menu for the handful of things it can change live, so this
    /// writes the file and says so: the values land on the next launch. That
    /// is the documented, safe way to drive it, and it is why every write goes
    /// through IniFile's atomic save with its one-time .bak.
    class OptiScalerBridge
    {
    public:
        /// Looks for OptiScaler.ini beside the game. Safe to call repeatedly.
        void refresh();

        bool available() const { return m_available; }
        const std::wstring &path() const { return m_path; }

        /// True when the neural-upstream build is what is installed - the only
        /// one whose [DlssNr] section exists, and the only one the overlay is
        /// offered for on the OptiScaler route.
        bool has_neural_section() const { return m_neural; }

        /// Everything the tabs draw.
        static const std::vector<SettingGroup> &dlss_nr_groups();
        static const std::vector<SettingGroup> &upscaler_groups();
        static const std::vector<SettingGroup> &frame_gen_groups();

        /// True when the key is left on OptiScaler's own default.
        bool is_auto(const Setting &setting) const;
        void set_auto(const Setting &setting);

        bool  get_bool(const Setting &setting, bool fallback) const;
        int   get_int(const Setting &setting, int fallback) const;
        float get_float(const Setting &setting, float fallback) const;
        std::string get_text(const Setting &setting, const char *fallback) const;

        void set_bool(const Setting &setting, bool value);
        void set_int(const Setting &setting, int value);
        void set_float(const Setting &setting, float value);
        void set_text(const Setting &setting, std::string_view value);

        /// The documentation block above the key in OptiScaler's own file.
        std::string comment(const Setting &setting) const;

        bool dirty() const { return m_file.dirty(); }

        /// Writes the file. Returns false and leaves the original untouched if
        /// anything went wrong.
        bool save();

        /// Last message worth showing the user, set by save().
        const std::string &status() const { return m_status; }

    private:
        IniFile m_file;
        std::wstring m_path;
        bool m_available = false;
        bool m_neural = false;
        std::string m_status;
    };

    OptiScalerBridge &optiscaler();
}
