#include "Common.h"

#include <cstdio>
#include <mutex>

namespace dlss5
{
    namespace
    {
        std::wstring resolve_game_directory()
        {
            // The executable that started the process, not our own module: the
            // add-on sits beside it, and so does everything it configures.
            wchar_t buffer[MAX_PATH * 4] = {};
            const DWORD length = GetModuleFileNameW(nullptr, buffer, static_cast<DWORD>(std::size(buffer)));

            if (length == 0 || length >= std::size(buffer))
                return {};

            std::wstring path(buffer, length);
            const size_t slash = path.find_last_of(L"\\/");
            return slash == std::wstring::npos ? std::wstring() : path.substr(0, slash);
        }
    }

    const std::wstring &game_directory()
    {
        static const std::wstring directory = resolve_game_directory();
        return directory;
    }

    std::wstring game_path(std::wstring_view file_name)
    {
        const std::wstring &root = game_directory();

        if (root.empty())
            return std::wstring(file_name);

        std::wstring path = root;
        path += L'\\';
        path.append(file_name);
        return path;
    }

    bool file_exists(const std::wstring &path)
    {
        if (path.empty())
            return false;

        const DWORD attributes = GetFileAttributesW(path.c_str());
        return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) == 0;
    }

    std::string narrow(std::wstring_view text)
    {
        if (text.empty())
            return {};

        const int size = WideCharToMultiByte(
            CP_UTF8, 0, text.data(), static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);

        if (size <= 0)
            return {};

        std::string result(static_cast<size_t>(size), '\0');
        WideCharToMultiByte(
            CP_UTF8, 0, text.data(), static_cast<int>(text.size()), result.data(), size, nullptr, nullptr);
        return result;
    }

    std::wstring widen(std::string_view text)
    {
        if (text.empty())
            return {};

        const int size = MultiByteToWideChar(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), nullptr, 0);

        if (size <= 0)
            return {};

        std::wstring result(static_cast<size_t>(size), L'\0');
        MultiByteToWideChar(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), result.data(), size);
        return result;
    }

    bool iequals(std::string_view a, std::string_view b)
    {
        if (a.size() != b.size())
            return false;

        for (size_t i = 0; i < a.size(); ++i)
        {
            const char ca = (a[i] >= 'A' && a[i] <= 'Z') ? static_cast<char>(a[i] - 'A' + 'a') : a[i];
            const char cb = (b[i] >= 'A' && b[i] <= 'Z') ? static_cast<char>(b[i] - 'A' + 'a') : b[i];

            if (ca != cb)
                return false;
        }

        return true;
    }

    std::string_view trim(std::string_view text)
    {
        const auto is_space = [](char c) { return c == ' ' || c == '\t' || c == '\r' || c == '\n'; };

        while (!text.empty() && is_space(text.front()))
            text.remove_prefix(1);

        while (!text.empty() && is_space(text.back()))
            text.remove_suffix(1);

        return text;
    }

    void log(std::string_view message)
    {
        static std::mutex mutex;
        static const std::wstring path = game_path(L"dlss5-overlay.log");

        std::lock_guard<std::mutex> guard(mutex);

        FILE *file = _wfopen(path.c_str(), L"a");

        if (file == nullptr)
            return;

        SYSTEMTIME now = {};
        GetLocalTime(&now);
        fprintf(file, "[%02u:%02u:%02u] %.*s\n", now.wHour, now.wMinute, now.wSecond,
                static_cast<int>(message.size()), message.data());
        fclose(file);
    }
}
