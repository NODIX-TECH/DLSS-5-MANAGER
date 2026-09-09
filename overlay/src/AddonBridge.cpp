#define ImTextureID ImU64
#include <imgui.h>
#include <reshade.hpp>

#include "AddonBridge.h"

#include <cstring>

namespace dlss5
{
    namespace
    {
        /// Calls the add-on's own panel, and survives it going wrong.
        ///
        /// The callback is found by reading the add-on's code rather than by
        /// being told where it is, so however carefully that is checked, the
        /// call is still into something this build has never seen. A game must
        /// not go down because of an overlay, so it sits behind a structured
        /// handler - in its own function, with nothing needing unwinding, which
        /// is the only shape MSVC allows one in.
        bool call_panel_guarded(void *callback, reshade::api::effect_runtime *runtime)
        {
            __try
            {
                reinterpret_cast<void (*)(reshade::api::effect_runtime *)>(callback)(runtime);
                return true;
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                return false;
            }
        }

        template <typename Fn>
        void for_each_section(void *module, bool executable, Fn &&visit)
        {
            auto *base = static_cast<unsigned char *>(module);
            const auto *dos = reinterpret_cast<const IMAGE_DOS_HEADER *>(base);

            if (dos->e_magic != IMAGE_DOS_SIGNATURE)
                return;

            const auto *nt = reinterpret_cast<const IMAGE_NT_HEADERS64 *>(base + dos->e_lfanew);

            if (nt->Signature != IMAGE_NT_SIGNATURE)
                return;

            const auto *section = IMAGE_FIRST_SECTION(nt);

            for (unsigned i = 0; i < nt->FileHeader.NumberOfSections; ++i, ++section)
            {
                const bool is_code = (section->Characteristics & IMAGE_SCN_MEM_EXECUTE) != 0;

                if (is_code == executable)
                    visit(base + section->VirtualAddress, section->Misc.VirtualSize);
            }
        }

        bool address_in_code(void *module, const void *address)
        {
            bool inside = false;

            for_each_section(module, true, [&](unsigned char *start, size_t size) {
                const auto *p = static_cast<const unsigned char *>(address);

                if (p >= start && p < start + size)
                    inside = true;
            });

            return inside;
        }

        /// Where the add-on's overlay title sits in its own data.
        const unsigned char *find_string(void *module, const char *text)
        {
            const size_t length = strlen(text);
            const unsigned char *found = nullptr;

            for_each_section(module, false, [&](unsigned char *start, size_t size) {
                if (found != nullptr || size < length + 1)
                    return;

                for (size_t i = 0; i + length + 1 <= size; ++i)
                {
                    if (start[i] == static_cast<unsigned char>(text[0]) &&
                        memcmp(start + i, text, length + 1) == 0)
                    {
                        found = start + i;
                        return;
                    }
                }
            });

            return found;
        }

        /// Strategy one: anchor on the title the add-on registered.
        ///
        /// When the title is a string literal the compiler loads it with
        /// `lea rcx, [rip+title]`, and the callback follows in rdx. Precise
        /// when it applies - but an add-on that keeps its title in a variable
        /// (RenoDX passes its exported NAME) loads rcx with a `mov` from that
        /// variable instead, and then there is no literal to anchor to.
        void *find_by_title(void *module, const char *title)
        {
            const unsigned char *title_address = find_string(module, title);

            if (title_address == nullptr)
                return nullptr;

            void *callback = nullptr;

            for_each_section(module, true, [&](unsigned char *start, size_t size) {
                if (callback != nullptr || size < 16)
                    return;

                for (size_t i = 0; i + 16 < size; ++i)
                {
                    // 48 8D 0D disp32  =  lea rcx, [rip+disp32]
                    if (start[i] != 0x48 || start[i + 1] != 0x8D || start[i + 2] != 0x0D)
                        continue;

                    int32_t displacement = 0;
                    memcpy(&displacement, start + i + 3, sizeof(displacement));

                    if (start + i + 7 + displacement != title_address)
                        continue;

                    // 48 8D 15 disp32  =  lea rdx, [rip+disp32], normally the
                    // next instruction but a compiler may separate them.
                    for (size_t j = i + 7; j < i + 48 && j + 7 < size; ++j)
                    {
                        if (start[j] != 0x48 || start[j + 1] != 0x8D || start[j + 2] != 0x15)
                            continue;

                        int32_t target = 0;
                        memcpy(&target, start + j + 3, sizeof(target));

                        void *candidate = start + j + 7 + target;

                        if (address_in_code(module, candidate))
                        {
                            callback = candidate;
                            return;
                        }
                    }
                }
            });

            return callback;
        }

        /// Strategy two: recognise the registration itself, whatever the title.
        ///
        /// Both `register_overlay` and `register_event` are inline wrappers
        /// around one cached `GetProcAddress` result, so they compile to the
        /// same shape:
        ///
        ///     48 8B 05 ..   mov rax, [rip+cached]
        ///     48 85 C0      test rax, rax
        ///     74 ..         je   past the call
        ///     48 8D 15 ..   lea  rdx, callback
        ///   [ B9 .. ]       mov  ecx, event id      <- events only
        ///     FF D0         call rax
        ///
        /// The event id is what tells them apart: `register_overlay` takes a
        /// title pointer in rcx, loaded further up, and never that immediate.
        /// So the overlay registration is the one whose `lea rdx` is followed
        /// straight by the call - and it must be the only one, or this is not
        /// the certainty it needs to be before calling the address.
        void *find_by_shape(void *module)
        {
            void *found = nullptr;
            int matches = 0;

            for_each_section(module, true, [&](unsigned char *start, size_t size) {
                if (size < 32)
                    return;

                for (size_t i = 0; i + 24 < size; ++i)
                {
                    if (start[i] != 0x48 || start[i + 1] != 0x8B || start[i + 2] != 0x05)
                        continue;

                    if (start[i + 7] != 0x48 || start[i + 8] != 0x85 || start[i + 9] != 0xC0)
                        continue;

                    if (start[i + 10] != 0x74)
                        continue;

                    if (start[i + 12] != 0x48 || start[i + 13] != 0x8D || start[i + 14] != 0x15)
                        continue;

                    // Straight to the call means no event id, so this is the
                    // overlay registration rather than an event one.
                    if (start[i + 19] != 0xFF || start[i + 20] != 0xD0)
                        continue;

                    int32_t displacement = 0;
                    memcpy(&displacement, start + i + 15, sizeof(displacement));

                    void *candidate = start + i + 19 + displacement;

                    if (!address_in_code(module, candidate))
                        continue;

                    ++matches;

                    if (found == nullptr)
                        found = candidate;
                }
            });

            return matches == 1 ? found : nullptr;
        }

        /// The add-on's overlay callback, by whichever route recognises it.
        void *find_overlay_callback(void *module, const char *title)
        {
            if (void *callback = find_by_title(module, title))
                return callback;

            return find_by_shape(module);
        }
    }

    void AddonBridge::reset()
    {
        m_located = false;
        m_module = nullptr;
        m_callback = nullptr;
        m_failures = 0;
        m_status.clear();
    }

    bool AddonBridge::locate()
    {
        HMODULE module = GetModuleHandleW(m_target.module);

        // Fall back to the name the add-on shipped under before it was renamed,
        // so a game modded by an older build still gets its panel.
        if (module == nullptr && m_target.module_legacy != nullptr)
            module = GetModuleHandleW(m_target.module_legacy);

        if (module == nullptr)
        {
            m_status = std::string(m_target.display) + " is not loaded in this game.";
            m_located = false;
            m_module = nullptr;
            m_callback = nullptr;
            return false;
        }

        if (m_located && m_module == module)
            return true;

        // A module that moved is a module that reloaded.
        if (m_module != module)
        {
            m_module = module;
            m_callback = nullptr;
            m_failures = 0;
        }

        m_callback = find_overlay_callback(module, m_target.overlay_title);

        if (m_callback == nullptr)
        {
            m_status = std::string(m_target.display) +
                       ": its settings panel could not be located, so it is left to its own tab.";
            m_located = false;
            return false;
        }

        m_located = true;
        m_status.clear();
        return true;
    }

    bool AddonBridge::available()
    {
        if (m_failures >= 2)
            return false;

        return locate();
    }

    void AddonBridge::draw(reshade::api::effect_runtime *runtime)
    {
        if (runtime == nullptr || !available())
        {
            if (!m_status.empty())
                ImGui::TextWrapped("%s", m_status.c_str());

            return;
        }

        // Its own widget ids are scoped to this window, so drawing the same
        // panel here and in ReShade's overlay at the same time is two views of
        // one state rather than a collision.
        ImGui::PushID(m_target.display);

        if (!call_panel_guarded(m_callback, runtime))
        {
            ++m_failures;

            if (m_failures >= 2)
            {
                m_status = std::string(m_target.display) +
                           ": its panel could not be drawn safely, so it is left to its own tab.";
                m_located = false;
            }
        }

        ImGui::PopID();
    }

    AddonBridge &renodx_bridge()
    {
        static AddonBridge instance({ L"renodx-dlss.addon64", L"renodx-dlss5.addon64", "RenoDX DLSS", "RenoDX" });
        return instance;
    }

    AddonBridge &nr_bridge()
    {
        static AddonBridge instance({ L"nvngx.dll.addon64", nullptr, "NR Pre-Upscale", "NR Pre-Upscale" });
        return instance;
    }
}
