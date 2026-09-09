#pragma once

// One place for the includes and the two or three helpers every translation
// unit here ends up wanting. Nothing in this file allocates or has state.

#include <windows.h>

#include <string>
#include <string_view>
#include <vector>
#include <algorithm>
#include <cstdint>

namespace dlss5
{
    /// The folder the game's executable lives in - where OptiScaler, ReShade
    /// and our own settings file all sit. Resolved once, from the module we
    /// were loaded into rather than from the working directory, because a game
    /// is free to change the latter at any time.
    const std::wstring &game_directory();

    /// Full path to a file next to the game executable.
    std::wstring game_path(std::wstring_view file_name);

    /// True when a readable file exists at this path.
    bool file_exists(const std::wstring &path);

    std::string  narrow(std::wstring_view text);
    std::wstring widen(std::string_view text);

    /// Case-insensitive comparison, ASCII only - every key we compare is a
    /// hand-written INI identifier.
    bool iequals(std::string_view a, std::string_view b);

    /// Trims spaces and tabs from both ends without copying the middle twice.
    std::string_view trim(std::string_view text);

    /// Writes a line to the add-on's own log, next to the game. Diagnostics
    /// only - the overlay never depends on it succeeding.
    void log(std::string_view message);
}
