#include "IniFile.h"

#include <cstdio>

namespace dlss5
{
    namespace
    {
        /// Splits "Key=Value ; note" into the key and the value, leaving the
        /// note alone. Returns false for anything that is not a key line.
        bool split_entry(std::string_view line, std::string_view &key, std::string_view &value)
        {
            const std::string_view trimmed = trim(line);

            if (trimmed.empty() || trimmed.front() == ';' || trimmed.front() == '#' || trimmed.front() == '[')
                return false;

            const size_t equals = trimmed.find('=');

            if (equals == std::string_view::npos)
                return false;

            key = trim(trimmed.substr(0, equals));
            value = trim(trimmed.substr(equals + 1));

            // An inline comment belongs to the line, not to the value.
            const size_t comment = value.find(';');
            if (comment != std::string_view::npos)
                value = trim(value.substr(0, comment));

            return !key.empty();
        }

        std::string section_name(std::string_view line)
        {
            const std::string_view trimmed = trim(line);

            if (trimmed.size() < 2 || trimmed.front() != '[')
                return {};

            const size_t close = trimmed.find(']');

            if (close == std::string_view::npos)
                return {};

            return std::string(trim(trimmed.substr(1, close - 1)));
        }

        bool is_comment(std::string_view line)
        {
            const std::string_view trimmed = trim(line);
            return !trimmed.empty() && (trimmed.front() == ';' || trimmed.front() == '#');
        }

        bool read_all(const std::wstring &path, std::string &out)
        {
            FILE *file = _wfopen(path.c_str(), L"rb");

            if (file == nullptr)
                return false;

            fseek(file, 0, SEEK_END);
            const long size = ftell(file);
            fseek(file, 0, SEEK_SET);

            if (size < 0)
            {
                fclose(file);
                return false;
            }

            out.resize(static_cast<size_t>(size));

            const size_t read = size > 0 ? fread(out.data(), 1, static_cast<size_t>(size), file) : 0;
            fclose(file);
            out.resize(read);
            return true;
        }
    }

    bool IniFile::load(const std::wstring &path)
    {
        m_path = path;
        m_lines.clear();
        m_dirty = false;
        m_loaded = false;

        std::string contents;

        if (!read_all(path, contents))
            return false;

        m_loaded = true;

        std::string current_section;
        size_t start = 0;

        while (start <= contents.size())
        {
            size_t end = contents.find('\n', start);
            const bool last = end == std::string::npos;

            if (last)
                end = contents.size();

            std::string_view raw(contents.data() + start, end - start);

            if (!raw.empty() && raw.back() == '\r')
                raw.remove_suffix(1);

            Line line;
            line.text.assign(raw);

            std::string name = section_name(raw);

            if (!name.empty())
            {
                current_section = name;
                line.section = current_section;
            }
            else
            {
                line.section = current_section;

                std::string_view key, value;
                if (split_entry(raw, key, value))
                    line.key.assign(key);
            }

            m_lines.push_back(std::move(line));

            if (last)
                break;

            start = end + 1;
        }

        return true;
    }

    int IniFile::find_line(std::string_view section, std::string_view key) const
    {
        for (size_t i = 0; i < m_lines.size(); ++i)
        {
            if (!m_lines[i].key.empty() && iequals(m_lines[i].section, section) && iequals(m_lines[i].key, key))
                return static_cast<int>(i);
        }

        return -1;
    }

    int IniFile::section_end(std::string_view section) const
    {
        int last = -1;

        for (size_t i = 0; i < m_lines.size(); ++i)
        {
            if (iequals(m_lines[i].section, section))
                last = static_cast<int>(i);
        }

        return last;
    }

    bool IniFile::has_section(std::string_view section) const
    {
        for (const Line &line : m_lines)
        {
            if (iequals(line.section, section))
                return true;
        }

        return false;
    }

    bool IniFile::has_key(std::string_view section, std::string_view key) const
    {
        return find_line(section, key) >= 0;
    }

    std::optional<std::string> IniFile::get(std::string_view section, std::string_view key) const
    {
        const int index = find_line(section, key);

        if (index < 0)
            return std::nullopt;

        std::string_view found_key, value;

        if (!split_entry(m_lines[static_cast<size_t>(index)].text, found_key, value))
            return std::nullopt;

        return std::string(value);
    }

    std::string IniFile::get_or(std::string_view section, std::string_view key, std::string_view fallback) const
    {
        if (auto value = get(section, key); value.has_value() && !value->empty())
            return *value;

        return std::string(fallback);
    }

    void IniFile::set(std::string_view section, std::string_view key, std::string_view value)
    {
        const int index = find_line(section, key);

        if (index >= 0)
        {
            Line &line = m_lines[static_cast<size_t>(index)];

            // Keep whatever inline comment the line carried.
            const size_t equals = line.text.find('=');
            std::string trailing;

            if (equals != std::string::npos)
            {
                const size_t comment = line.text.find(';', equals);
                if (comment != std::string::npos)
                    trailing = " " + line.text.substr(comment);
            }

            std::string replacement(line.key);
            replacement += '=';
            replacement.append(value);
            replacement += trailing;

            if (replacement != line.text)
            {
                line.text = std::move(replacement);
                m_dirty = true;
            }

            return;
        }

        // The key is new. Put it at the end of its section so it reads as part
        // of that block, creating the section itself when it is missing too.
        std::string entry(key);
        entry += '=';
        entry.append(value);

        const int end = section_end(section);

        if (end < 0)
        {
            if (!m_lines.empty() && !trim(m_lines.back().text).empty())
                m_lines.push_back(Line{ "", std::string(section), "" });

            m_lines.push_back(Line{ "[" + std::string(section) + "]", std::string(section), "" });
            m_lines.push_back(Line{ entry, std::string(section), std::string(key) });
        }
        else
        {
            m_lines.insert(m_lines.begin() + end + 1, Line{ entry, std::string(section), std::string(key) });
        }

        m_dirty = true;
    }

    std::string IniFile::comment_for(std::string_view section, std::string_view key) const
    {
        const int index = find_line(section, key);

        if (index <= 0)
            return {};

        // Walk back over the comment block sitting directly above the key.
        int first = index;

        while (first > 0 && is_comment(m_lines[static_cast<size_t>(first - 1)].text))
            --first;

        std::string result;

        for (int i = first; i < index; ++i)
        {
            std::string_view text = trim(m_lines[static_cast<size_t>(i)].text);

            if (!text.empty() && (text.front() == ';' || text.front() == '#'))
                text.remove_prefix(1);

            text = trim(text);

            if (text.empty())
                continue;

            if (!result.empty())
                result += '\n';

            result.append(text);
        }

        return result;
    }

    std::vector<std::string> IniFile::keys_in(std::string_view section) const
    {
        std::vector<std::string> result;

        for (const Line &line : m_lines)
        {
            if (!line.key.empty() && iequals(line.section, section))
                result.push_back(line.key);
        }

        return result;
    }

    std::vector<std::string> IniFile::sections() const
    {
        std::vector<std::string> result;

        for (const Line &line : m_lines)
        {
            if (line.section.empty())
                continue;

            const bool seen = std::any_of(result.begin(), result.end(),
                                          [&](const std::string &s) { return iequals(s, line.section); });

            if (!seen)
                result.push_back(line.section);
        }

        return result;
    }

    bool IniFile::save()
    {
        return save_as(m_path);
    }

    bool IniFile::save_as(const std::wstring &path)
    {
        if (path.empty())
            return false;

        std::string contents;
        contents.reserve(m_lines.size() * 48);

        for (size_t i = 0; i < m_lines.size(); ++i)
        {
            contents.append(m_lines[i].text);

            if (i + 1 < m_lines.size())
                contents.append("\r\n");
        }

        // Write beside the target so the replace below cannot cross volumes.
        const std::wstring temp = path + L".tmp";

        {
            FILE *file = _wfopen(temp.c_str(), L"wb");

            if (file == nullptr)
                return false;

            const size_t written = contents.empty()
                ? 0
                : fwrite(contents.data(), 1, contents.size(), file);

            const bool complete = written == contents.size();
            fflush(file);
            fclose(file);

            if (!complete)
            {
                DeleteFileW(temp.c_str());
                return false;
            }
        }

        // Keep the file the game shipped with, once, and never overwrite that
        // backup afterwards - the first version is the one worth going back to.
        const std::wstring backup = path + L".bak";

        if (file_exists(path) && !file_exists(backup))
            CopyFileW(path.c_str(), backup.c_str(), TRUE);

        bool replaced = false;

        if (file_exists(path))
        {
            replaced = MoveFileExW(temp.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH) != 0;
        }
        else
        {
            replaced = MoveFileExW(temp.c_str(), path.c_str(), MOVEFILE_WRITE_THROUGH) != 0;
        }

        if (!replaced)
        {
            DeleteFileW(temp.c_str());
            return false;
        }

        m_dirty = false;
        return true;
    }
}
