#pragma once

#include "Common.h"

#include <optional>

namespace dlss5
{
    /// A whole-file INI editor that keeps the file it was given.
    ///
    /// OptiScaler.ini is 1700 lines of hand-written documentation with a
    /// handful of values scattered through it. Rewriting it from a key/value
    /// map would throw all of that away, so this keeps every line exactly as
    /// it was read and edits values in place. A key that does not exist yet is
    /// appended to the end of its section rather than to the end of the file.
    ///
    /// Saving is atomic: the new contents go to a temporary file beside the
    /// target, the previous version is kept once as "<name>.bak", and only
    /// then does the replace happen. A half-written OptiScaler.ini would stop
    /// the game from starting, so this never leaves one behind.
    class IniFile
    {
    public:
        IniFile() = default;

        /// Reads the file. A missing file is not an error - it produces an
        /// empty document that can still be written to.
        bool load(const std::wstring &path);

        bool save();
        bool save_as(const std::wstring &path);

        const std::wstring &path() const { return m_path; }
        bool loaded() const { return m_loaded; }

        /// True when a value has been changed since the last load or save.
        bool dirty() const { return m_dirty; }

        bool has_section(std::string_view section) const;
        bool has_key(std::string_view section, std::string_view key) const;

        /// The raw text of a value, without its inline comment. Empty optional
        /// when the key is absent.
        std::optional<std::string> get(std::string_view section, std::string_view key) const;

        std::string get_or(std::string_view section, std::string_view key, std::string_view fallback) const;

        void set(std::string_view section, std::string_view key, std::string_view value);

        /// Every comment line directly above a key, joined with newlines. This
        /// is what OptiScaler documents its own settings with, so the overlay
        /// shows it as the tooltip rather than duplicating the text.
        std::string comment_for(std::string_view section, std::string_view key) const;

        /// Every key in a section, in file order. Used to find settings the
        /// overlay has no hand-written row for, so a new add-on release cannot
        /// hide a setting simply by being newer than this build.
        std::vector<std::string> keys_in(std::string_view section) const;

        /// Every section in the file, in file order.
        std::vector<std::string> sections() const;

    private:
        struct Line
        {
            std::string text;     // exactly as read, minus the newline
            std::string section;  // the section this line belongs to
            std::string key;      // empty for comments, blanks and headers
        };

        int find_line(std::string_view section, std::string_view key) const;
        int section_end(std::string_view section) const;

        std::wstring m_path;
        std::vector<Line> m_lines;
        bool m_loaded = false;
        bool m_dirty = false;
    };
}
