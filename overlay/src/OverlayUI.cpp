#define ImTextureID ImU64
#include <imgui.h>
#include <reshade.hpp>

#include "OverlayUI.h"
#include "Config.h"
#include "Telemetry.h"
#include "Theme.h"
#include "OptiScalerBridge.h"
#include "AddonBridge.h"

#include <cstdio>

namespace dlss5
{
    namespace
    {
        ImU32 to_u32(uint32_t rgb, float alpha = 1.0f)
        {
            return IM_COL32((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF,
                            static_cast<int>(alpha * 255.0f));
        }

        ImVec4 to_vec4(uint32_t rgb, float alpha = 1.0f)
        {
            return ImVec4(static_cast<float>((rgb >> 16) & 0xFF) / 255.0f,
                          static_cast<float>((rgb >> 8) & 0xFF) / 255.0f,
                          static_cast<float>(rgb & 0xFF) / 255.0f,
                          alpha);
        }

        /// ReShade forwards a subset of ImGui, and SetWindowFontScale is not
        /// in it - 1.92 replaced it with a font push carrying an explicit
        /// size, which is. These two wrap that so the call sites stay readable.
        void push_font_scale(float scale)
        {
            ImGui::PushFont(nullptr, ImGui::GetFontSize() * scale);
        }

        void pop_font_scale()
        {
            ImGui::PopFont();
        }

        /// GetMainViewport is not forwarded either. The display size in the IO
        /// block is the same rectangle for the single-viewport case an in-game
        /// overlay always is.
        ImVec2 display_size()
        {
            return ImGui::GetIO().DisplaySize;
        }

        /// Every key the hotkey picker offers, so the label and the picker can
        /// never disagree about what a code means.
        struct NamedKey
        {
            const char *name;
            int code;
        };

        // Letters come first: the default is Shift+O, and a letter with a
        // modifier is the binding least likely to collide with a game. Home is
        // deliberately absent - that is ReShade's own key.
        const NamedKey kKeys[] = {
            { "O", 'O' }, { "P", 'P' }, { "K", 'K' }, { "L", 'L' },
            { "M", 'M' }, { "N", 'N' }, { "J", 'J' }, { "H", 'H' },
            { "F1", VK_F1 },   { "F2", VK_F2 },   { "F3", VK_F3 },   { "F4", VK_F4 },
            { "F5", VK_F5 },   { "F6", VK_F6 },   { "F7", VK_F7 },   { "F8", VK_F8 },
            { "F9", VK_F9 },   { "F10", VK_F10 }, { "F11", VK_F11 }, { "F12", VK_F12 },
            { "Insert", VK_INSERT }, { "Delete", VK_DELETE }, { "End", VK_END },
            { "Page Up", VK_PRIOR }, { "Page Down", VK_NEXT }, { "None", 0 },
        };

        const char *key_name(int code)
        {
            for (const NamedKey &key : kKeys)
            {
                if (key.code == code)
                    return key.name;
            }

            return "None";
        }

        /// A "?" the user can hover for the documentation OptiScaler ships in
        /// its own ini. Nothing is duplicated into this binary.
        void help_marker(const std::string &text)
        {
            if (text.empty())
                return;

            ImGui::SameLine();
            ImGui::TextDisabled("(?)");

            if (ImGui::IsItemHovered(ImGuiHoveredFlags_DelayNormal))
            {
                ImGui::BeginTooltip();
                ImGui::PushTextWrapPos(ImGui::GetFontSize() * 34.0f);
                ImGui::TextUnformatted(text.c_str());
                ImGui::PopTextWrapPos();
                ImGui::EndTooltip();
            }
        }

        void heading(const char *text)
        {
            ImGui::Dummy(ImVec2(0.0f, 2.0f));
            ImGui::SeparatorText(text);
        }

        /// One big number with a caption under it, used across the top of the
        /// performance tab and inside the HUD.
        void stat(const char *caption, const char *value, uint32_t colour, float width)
        {
            ImGui::BeginGroup();

            ImGui::PushStyleColor(ImGuiCol_Text, to_vec4(colour));
            push_font_scale(1.55f);
            ImGui::TextUnformatted(value);
            pop_font_scale();
            ImGui::PopStyleColor();

            ImGui::PushStyleColor(ImGuiCol_Text, to_vec4(palette().text_dim));
            ImGui::TextUnformatted(caption);
            ImGui::PopStyleColor();

            ImGui::EndGroup();

            if (width > 0.0f)
                ImGui::SameLine(0.0f, width);
        }

        /// A filled bar with its own label, for VRAM and GPU load.
        void meter(const char *label, float fraction, const char *overlay_text)
        {
            const Palette &p = palette();
            fraction = std::clamp(fraction, 0.0f, 1.0f);

            const uint32_t colour = fraction > 0.9f ? p.bad : (fraction > 0.75f ? p.warn : p.accent);

            ImGui::PushStyleColor(ImGuiCol_PlotHistogram, to_vec4(colour));
            ImGui::ProgressBar(fraction, ImVec2(-FLT_MIN, 0.0f), overlay_text);
            ImGui::PopStyleColor();

            if (label != nullptr && label[0] != '\0')
            {
                ImGui::PushStyleColor(ImGuiCol_Text, to_vec4(p.text_dim));
                ImGui::TextUnformatted(label);
                ImGui::PopStyleColor();
            }
        }

        std::string format_bytes(uint64_t bytes)
        {
            char buffer[48] = {};

            if (bytes >= (1ull << 30))
                snprintf(buffer, sizeof(buffer), "%.2f GB", static_cast<double>(bytes) / (1ull << 30));
            else
                snprintf(buffer, sizeof(buffer), "%llu MB", static_cast<unsigned long long>(bytes >> 20));

            return buffer;
        }

        /// Draws one row of the OptiScaler setting tables.
        ///
        /// Every OptiScaler key understands the literal "auto", which is how it
        /// says "use my own default". That is a real third state, not a value,
        /// so it gets its own checkbox and the control beside it is disabled
        /// while it is on - showing a number there would be a guess.
        void setting_row(OptiScalerBridge &bridge, const Setting &setting)
        {
            ImGui::PushID(setting.key);

            const bool was_auto = bridge.is_auto(setting);
            bool is_auto = was_auto;

            ImGui::Checkbox("##auto", &is_auto);

            if (ImGui::IsItemHovered(ImGuiHoveredFlags_DelayNormal))
                ImGui::SetTooltip("Auto - leave this on OptiScaler's own default");

            ImGui::SameLine();

            if (is_auto != was_auto)
            {
                if (is_auto)
                {
                    bridge.set_auto(setting);
                }
                else
                {
                    // Coming off auto has to write something concrete. The
                    // midpoint of the documented range is the least surprising
                    // starting point, and booleans start switched on.
                    switch (setting.kind)
                    {
                    case SettingKind::Bool:
                        bridge.set_bool(setting, true);
                        break;
                    case SettingKind::Int:
                        bridge.set_int(setting, static_cast<int>(setting.minimum));
                        break;
                    case SettingKind::Float:
                        bridge.set_float(setting, setting.minimum + (setting.maximum - setting.minimum) * 0.5f);
                        break;
                    case SettingKind::Choice:
                        bridge.set_int(setting, 0);
                        break;
                    case SettingKind::Text:
                        bridge.set_text(setting, setting.choices != nullptr && setting.choice_count > 0
                                                     ? setting.choices[0] : "auto");
                        break;
                    }
                }
            }

            ImGui::BeginDisabled(is_auto);
            ImGui::SetNextItemWidth(-ImGui::GetFontSize() * 12.0f);

            switch (setting.kind)
            {
            case SettingKind::Bool:
            {
                bool value = bridge.get_bool(setting, false);
                if (ImGui::Checkbox(setting.label, &value))
                    bridge.set_bool(setting, value);
                break;
            }
            case SettingKind::Int:
            {
                int value = bridge.get_int(setting, static_cast<int>(setting.minimum));
                if (ImGui::SliderInt(setting.label, &value,
                                     static_cast<int>(setting.minimum), static_cast<int>(setting.maximum)))
                    bridge.set_int(setting, value);
                break;
            }
            case SettingKind::Float:
            {
                float value = bridge.get_float(setting, setting.minimum);
                if (ImGui::SliderFloat(setting.label, &value, setting.minimum, setting.maximum, "%.2f"))
                    bridge.set_float(setting, value);
                break;
            }
            case SettingKind::Choice:
            {
                int value = std::clamp(bridge.get_int(setting, 0), 0, std::max(0, setting.choice_count - 1));
                if (ImGui::Combo(setting.label, &value, setting.choices, setting.choice_count))
                    bridge.set_int(setting, value);
                break;
            }
            case SettingKind::Text:
            {
                const std::string current = bridge.get_text(setting, "auto");

                if (setting.choices != nullptr && setting.choice_count > 0)
                {
                    if (ImGui::BeginCombo(setting.label, current.c_str()))
                    {
                        for (int i = 0; i < setting.choice_count; ++i)
                        {
                            const bool selected = iequals(current, setting.choices[i]);

                            if (ImGui::Selectable(setting.choices[i], selected))
                                bridge.set_text(setting, setting.choices[i]);

                            if (selected)
                                ImGui::SetItemDefaultFocus();
                        }

                        ImGui::EndCombo();
                    }
                }
                else
                {
                    char buffer[128] = {};
                    snprintf(buffer, sizeof(buffer), "%s", current.c_str());

                    if (ImGui::InputText(setting.label, buffer, sizeof(buffer)))
                        bridge.set_text(setting, buffer);
                }
                break;
            }
            }

            ImGui::EndDisabled();
            help_marker(bridge.comment(setting));
            ImGui::PopID();
        }

        void draw_groups(OptiScalerBridge &bridge, const std::vector<SettingGroup> &groups, bool collapse_after)
        {
            int index = 0;

            for (const SettingGroup &group : groups)
            {
                const bool open_by_default = !collapse_after || index < 3;

                if (ImGui::CollapsingHeader(group.title,
                                            open_by_default ? ImGuiTreeNodeFlags_DefaultOpen : 0))
                {
                    ImGui::Indent(6.0f);

                    for (int i = 0; i < group.count; ++i)
                        setting_row(bridge, group.settings[i]);

                    ImGui::Unindent(6.0f);
                }

                ++index;
            }
        }

        // =================================================================
        // RENODX DLSS 5
        // =================================================================
        // RenoDX keeps its settings in ReShade's own configuration, so they are
        // reachable through the add-on API - `get_config_value` and
        // `set_config_value` - and nothing here touches RenoDX's memory,
        // patches its ImGui dispatch or pins a build hash. That means these
        // controls keep working across RenoDX updates instead of refusing to
        // run against anything but one exact binary.
        //
        // The per-preset values live in "RENODX-DLSS-preset<N>"; the ones that
        // apply whatever preset is selected live in "RENODX-DLSS".
        constexpr const char *kRenoGlobal = "RENODX-DLSS";
        constexpr const char *kRenoDlss5 = "RenoDX.DLSS5";

        enum class RenoKind
        {
            Toggle,
            Slider,
            Choice,
        };

        /// One row of an add-on's settings. `section` is the ReShade config
        /// section it lives in; the RenoDX per-preset rows leave it null and
        /// take the selected preset's section instead.
        struct RenoSetting
        {
            const char *key;
            const char *label;
            RenoKind kind;
            const char *section;
            float minimum;
            float maximum;
            const char *const *choices;
            int choice_count;
        };

        const char *const kNrStyles[] = { "A - Default", "B - Natural", "C - Cinematic" };
        const char *const kHookPoints[] = { "Off", "Auto", "Upscaled", "FrameGen", "Present" };
        const char *const kUiCorrection[] = { "Auto", "Off", "On" };
        const char *const kEncodings[] = { "Auto", "sRGB", "Linear", "PQ", "scRGB" };
        const char *const kQualityModes[] = { "Off", "DLAA", "Quality", "Balanced", "Performance", "Ultra performance" };
        const char *const kOptionsModes[] = { "DLSS-NR", "DLSS-G", "DLSS-SR / DLAA" };

        const RenoSetting kRenoGlobalControls[] = {
            { "NeuralUplift",                        "DLSS neural rendering", RenoKind::Toggle, kRenoDlss5, 0, 0, nullptr, 0 },
            { "NREnableUpscaling",                   "NR upscaling",          RenoKind::Toggle, kRenoDlss5, 0, 0, nullptr, 0 },
            { "OptionsMode",                         "Options mode",          RenoKind::Choice, kRenoGlobal, 0, 0, kOptionsModes, 3 },
            { "DirectNeuralRenderingHookPoint",      "Hook method",           RenoKind::Choice, kRenoGlobal, 0, 0, kHookPoints, 5 },
            { "DirectNeuralRenderingRequireDlss",    "Require DLSS",          RenoKind::Toggle, kRenoGlobal, 0, 0, nullptr, 0 },
            { "DirectNeuralRenderingEncoding",       "Encoding",              RenoKind::Choice, kRenoGlobal, 0, 0, kEncodings, 5 },
            { "DirectNeuralRenderingUiCorrectionMode", "UI correction",       RenoKind::Choice, kRenoGlobal, 0, 0, kUiCorrection, 3 },
            { "DirectNeuralRenderingDiffuseWhiteOverride", "Override diffuse white", RenoKind::Toggle, kRenoGlobal, 0, 0, nullptr, 0 },
            { "DirectNeuralRenderingDiffuseWhiteNits", "Diffuse white (nits)", RenoKind::Slider, kRenoGlobal, 80.0f, 1000.0f, nullptr, 0 },
            { "DirectNeuralRenderingDebug",          "Debug view",            RenoKind::Toggle, kRenoGlobal, 0, 0, nullptr, 0 },
            { "DLSSQualityMode",                     "DLSS quality mode",     RenoKind::Choice, kRenoGlobal, 0, 0, kQualityModes, 6 },
            { "DLSSAutoExposure",                    "DLSS auto exposure",    RenoKind::Toggle, kRenoGlobal, 0, 0, nullptr, 0 },
        };

        // The render-preset hint RenoDX sends DLSS for each quality mode. They
        // are separate keys rather than one, because DLSS takes the hint per
        // mode - which is why RenoDX stores them that way too.
        const char *const kRenderPresets[] = { "Default", "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K" };

        const RenoSetting kRenoRenderPresets[] = {
            { "DLSSPresetDLAA",             "DLAA",              RenoKind::Choice, kRenoGlobal, 0, 0, kRenderPresets, 12 },
            { "DLSSPresetUltraQuality",     "Ultra quality",     RenoKind::Choice, kRenoGlobal, 0, 0, kRenderPresets, 12 },
            { "DLSSPresetQuality",          "Quality",           RenoKind::Choice, kRenoGlobal, 0, 0, kRenderPresets, 12 },
            { "DLSSPresetBalanced",         "Balanced",          RenoKind::Choice, kRenoGlobal, 0, 0, kRenderPresets, 12 },
            { "DLSSPresetPerformance",      "Performance",       RenoKind::Choice, kRenoGlobal, 0, 0, kRenderPresets, 12 },
            { "DLSSPresetUltraPerformance", "Ultra performance", RenoKind::Choice, kRenoGlobal, 0, 0, kRenderPresets, 12 },
        };

        // section == nullptr means "the preset the user has selected".
        const RenoSetting kRenoPresetControls[] = {
            { "DirectNeuralRenderingStyle",                "NR style",          RenoKind::Choice, nullptr, 0, 0, kNrStyles, 3 },
            { "DirectNeuralRenderingIntensity",            "Overall intensity", RenoKind::Slider, nullptr, 0.0f, 2.0f, nullptr, 0 },
            { "DirectNeuralRenderingLocalStructureStrength", "Structure intensity", RenoKind::Slider, nullptr, 0.0f, 2.0f, nullptr, 0 },
            { "DirectNeuralRenderingGlobalToneStrength",   "Tone intensity",    RenoKind::Slider, nullptr, 0.0f, 2.0f, nullptr, 0 },
            { "DirectNeuralRenderingLocalToneStrength",    "Local tone intensity", RenoKind::Slider, nullptr, 0.0f, 2.0f, nullptr, 0 },
            { "DirectNeuralRenderingAutoMask",             "Character mask",    RenoKind::Toggle, nullptr, 0, 0, nullptr, 0 },
            { "DirectNeuralRenderingSkinStructureStrength", "Character / skin structure", RenoKind::Slider, nullptr, -1.0f, 2.0f, nullptr, 0 },
            { "DirectNeuralRenderingPassCount",            "Pass count",        RenoKind::Slider, nullptr, 1.0f, 3.0f, nullptr, 0 },
        };

        // =================================================================
        // NR PRE-UPSCALE  (nvngx.dll.addon64)
        // =================================================================
        // The neural-upstream add-on the DX12 / DX11 / DX9 routes carry. Its
        // own tab says "Saved per game in ReShade.ini", and it reads and writes
        // that through ReShade's config API - the same store this reaches - so
        // every value below is the live one, not a copy.
        constexpr const char *kNrPre = "NRPreUpscale";

        const char *const kCadence[] = { "Quality", "Balanced", "Performance" };
        const char *const kNrPresets[] = { "Reference", "High", "Medium", "Low" };

        const RenoSetting kNrPreCore[] = {
            { "Enabled",        "Enable DLSS-NR",  RenoKind::Toggle, kNrPre, 0, 0, nullptr, 0 },
            { "Cadence",        "How often it runs", RenoKind::Choice, kNrPre, 0, 0, kCadence, 3 },
            { "Preset",         "How far it may go", RenoKind::Choice, kNrPre, 0, 0, kNrPresets, 4 },
            { "EffectStrength", "Effect strength", RenoKind::Slider, kNrPre, 0.0f, 2.0f, nullptr, 0 },
        };

        const RenoSetting kNrPreExposure[] = {
            { "AutoPaperWhite", "Auto exposure", RenoKind::Toggle, kNrPre, 0, 0, nullptr, 0 },
            { "PaperWhite",     "Paper white",   RenoKind::Slider, kNrPre, 0.05f, 8.0f, nullptr, 0 },
        };

        const RenoSetting kNrPreNetwork[] = {
            { "AutoMask",        "Automatic mask", RenoKind::Toggle, kNrPre, 0, 0, nullptr, 0 },
            { "LocalStructure",  "Local structure", RenoKind::Slider, kNrPre, 0.0f, 2.0f, nullptr, 0 },
            { "LocalTone",       "Local tone",     RenoKind::Slider, kNrPre, 0.0f, 2.0f, nullptr, 0 },
            { "SkinStructure",   "Skin structure", RenoKind::Slider, kNrPre, -1.0f, 2.0f, nullptr, 0 },
            { "NetScale",        "Network scale",  RenoKind::Slider, kNrPre, 0.25f, 2.0f, nullptr, 0 },
        };

        const RenoSetting kNrPreReuse[] = {
            { "Reproject",       "Reproject",        RenoKind::Toggle, kNrPre, 0, 0, nullptr, 0 },
            { "DeltaReuse",      "Reuse the effect", RenoKind::Toggle, kNrPre, 0, 0, nullptr, 0 },
            { "DeltaAdvect",     "Follow motion",    RenoKind::Slider, kNrPre, 0.0f, 1.0f, nullptr, 0 },
            { "DepthReject",     "Disocclusion reject", RenoKind::Slider, kNrPre, 0.0f, 1.0f, nullptr, 0 },
            { "StructGate",      "Structure gate",   RenoKind::Slider, kNrPre, 0.0f, 1.0f, nullptr, 0 },
            { "DeltaClamp",      "Effect clamp",     RenoKind::Slider, kNrPre, 0.0f, 4.0f, nullptr, 0 },
            { "SkipPassthrough", "Pass the game's colour instead", RenoKind::Toggle, kNrPre, 0, 0, nullptr, 0 },
            { "AsyncNetwork",    "Run the network asynchronously", RenoKind::Toggle, kNrPre, 0, 0, nullptr, 0 },
        };

        /// The ReShade.ini beside the game, read only to *discover* which keys
        /// exist. Values never come from here - they come from ReShade's live
        /// config - so a stale read cannot show a stale number.
        IniFile &reshade_ini()
        {
            static IniFile file;
            static bool loaded = false;

            if (!loaded)
            {
                loaded = true;
                file.load(game_path(L"ReShade.ini"));
            }

            return file;
        }

        void reload_reshade_ini()
        {
            reshade_ini().load(game_path(L"ReShade.ini"));
        }

        /// True when an add-on is loaded, or has ever written the setting we
        /// would edit. Two independent checks, because a module can be present
        /// under a renamed file and a configuration can outlive an uninstall -
        /// and neither answer involves reaching into the add-on itself.
        bool addon_present(reshade::api::effect_runtime *runtime,
                           const wchar_t *module,
                           const char *section,
                           const char *key)
        {
            if (runtime == nullptr)
                return false;

            if (module != nullptr && GetModuleHandleW(module) != nullptr)
                return true;

            char probe[64] = {};
            size_t size = sizeof(probe) - 1;
            return reshade::get_config_value(runtime, section, key, probe, &size);
        }

        bool renodx_present(reshade::api::effect_runtime *runtime)
        {
            return addon_present(runtime, L"renodx-dlss.addon64", kRenoDlss5, "NeuralUplift")
                   || addon_present(runtime, L"renodx-dlss5.addon64", kRenoDlss5, "NeuralUplift");
        }

        /// Draws one RenoDX row. Returns true when the value changed.
        bool reno_row(reshade::api::effect_runtime *runtime, const RenoSetting &setting, int preset)
        {
            char section[64] = {};

            if (setting.section != nullptr)
                snprintf(section, sizeof(section), "%s", setting.section);
            else
                snprintf(section, sizeof(section), "%s-preset%d", kRenoGlobal, preset);

            bool changed = false;
            ImGui::PushID(setting.key);

            switch (setting.kind)
            {
            case RenoKind::Toggle:
            {
                // RenoDX writes these as 0/1 integers.
                int stored = 0;
                reshade::get_config_value(runtime, section, setting.key, stored);
                bool value = stored != 0;

                if (ImGui::Checkbox(setting.label, &value))
                {
                    reshade::set_config_value(runtime, section, setting.key, value ? 1 : 0);
                    changed = true;
                }
                break;
            }
            case RenoKind::Slider:
            {
                float value = 0.0f;

                if (!reshade::get_config_value(runtime, section, setting.key, value))
                    value = std::clamp(1.0f, setting.minimum, setting.maximum);

                ImGui::SetNextItemWidth(-ImGui::GetFontSize() * 13.0f);

                if (ImGui::SliderFloat(setting.label, &value, setting.minimum, setting.maximum, "%.2f"))
                {
                    reshade::set_config_value(runtime, section, setting.key, value);
                    changed = true;
                }
                break;
            }
            case RenoKind::Choice:
            {
                int value = 0;
                reshade::get_config_value(runtime, section, setting.key, value);
                value = std::clamp(value, 0, std::max(0, setting.choice_count - 1));

                ImGui::SetNextItemWidth(-ImGui::GetFontSize() * 13.0f);

                if (ImGui::Combo(setting.label, &value, setting.choices, setting.choice_count))
                {
                    reshade::set_config_value(runtime, section, setting.key, value);
                    changed = true;
                }
                break;
            }
            }

            ImGui::PopID();
            return changed;
        }

        /// Draws every key of a section the hand-written rows above did not
        /// already cover.
        ///
        /// This is what stops a setting from going missing: the rows give the
        /// known keys a proper label and range, and anything an add-on release
        /// adds afterwards still turns up here rather than being invisible
        /// until someone updates this file.
        void draw_undeclared(reshade::api::effect_runtime *runtime,
                             const char *section,
                             const RenoSetting *const *declared,
                             int declared_count)
        {
            if (runtime == nullptr)
                return;

            const std::vector<std::string> keys = reshade_ini().keys_in(section);
            bool any = false;

            for (const std::string &key : keys)
            {
                bool known = false;

                for (int i = 0; i < declared_count && !known; ++i)
                    known = iequals(declared[i]->key, key);

                if (known)
                    continue;

                if (!any)
                {
                    heading("Everything else this add-on saves");
                    any = true;
                }

                // Nothing is known about the type, so the stored text decides:
                // a bare 0 or 1 is a switch, anything else numeric is a value.
                char stored[64] = {};
                size_t size = sizeof(stored) - 1;
                reshade::get_config_value(runtime, section, key.c_str(), stored, &size);

                const std::string_view text = trim(std::string_view(stored, size));
                ImGui::PushID(key.c_str());

                if (text == "0" || text == "1")
                {
                    bool value = text == "1";

                    if (ImGui::Checkbox(key.c_str(), &value))
                        reshade::set_config_value(runtime, section, key.c_str(), value ? 1 : 0);
                }
                else
                {
                    float value = 0.0f;
                    const bool numeric = reshade::get_config_value(runtime, section, key.c_str(), value);

                    if (numeric)
                    {
                        ImGui::SetNextItemWidth(-ImGui::GetFontSize() * 13.0f);

                        if (ImGui::DragFloat(key.c_str(), &value, 0.01f))
                            reshade::set_config_value(runtime, section, key.c_str(), value);
                    }
                    else
                    {
                        ImGui::LabelText(key.c_str(), "%.*s", static_cast<int>(text.size()), text.data());
                    }
                }

                ImGui::PopID();
            }
        }

        template <size_t N>
        bool draw_reno_group(reshade::api::effect_runtime *runtime, const char *title,
                             const RenoSetting (&items)[N], int preset)
        {
            heading(title);
            bool changed = false;

            for (const RenoSetting &setting : items)
                changed |= reno_row(runtime, setting, preset);

            return changed;
        }


        /// Collects technique handles so the list can be drawn outside the
        /// enumeration callback, which must not call back into the runtime.
        struct TechniqueList
        {
            std::vector<reshade::api::effect_technique> handles;
            std::vector<std::string> names;
            std::vector<std::string> effects;
        };

        void collect_technique(reshade::api::effect_runtime *runtime,
                               reshade::api::effect_technique technique,
                               void *user_data)
        {
            auto *list = static_cast<TechniqueList *>(user_data);

            char name[128] = {};
            size_t name_size = sizeof(name);
            runtime->get_technique_name(technique, name, &name_size);

            char effect[256] = {};
            size_t effect_size = sizeof(effect);
            runtime->get_technique_effect_name(technique, effect, &effect_size);

            list->handles.push_back(technique);
            list->names.emplace_back(name);
            list->effects.emplace_back(effect);
        }
    }

    void OverlayUI::initialise(Telemetry *telemetry)
    {
        m_telemetry = telemetry;
        m_ready = true;
    }

    void OverlayUI::invalidate()
    {
        m_notice.clear();

        // An effect reload can move an add-on, so what the bridges found about
        // it is re-established from scratch rather than trusted.
        renodx_bridge().reset();
        nr_bridge().reset();
    }

    // =====================================================================
    // HOTKEY
    // =====================================================================
    void OverlayUI::poll_hotkey(reshade::api::effect_runtime *runtime)
    {
        const Config &c = config();

        if (runtime == nullptr || c.hotkey == 0)
            return;

        // ReShade already edge-triggers this against its own input history,
        // which is the only history that knows whether the key press belonged
        // to the game or to an overlay that was up at the time.
        if (!runtime->is_key_pressed(static_cast<uint32_t>(c.hotkey)))
            return;

        // Modifiers match exactly, in both directions: a plain F8 binding must
        // not fire while the user is holding Ctrl for something in the game.
        if (runtime->is_key_down(VK_CONTROL) != c.hotkey_ctrl)
            return;

        if (runtime->is_key_down(VK_SHIFT) != c.hotkey_shift)
            return;

        if (runtime->is_key_down(VK_MENU) != c.hotkey_alt)
            return;

        // The Windows key belongs to the desktop, never to a game overlay.
        if (runtime->is_key_down(VK_LWIN) || runtime->is_key_down(VK_RWIN))
            return;

        m_open = !m_open;
    }

    // =====================================================================
    // HUD - always on screen while playing
    // =====================================================================
    void OverlayUI::draw_standalone(reshade::api::effect_runtime *runtime)
    {
        Config &c = config();

        poll_hotkey(runtime);

        if (!c.enabled)
            return;

        const Theme &theme = theme_by_name(c.theme);

        // ---- The panel, standing on its own ------------------------------
        // This is what makes it a panel rather than another ReShade tab. Three
        // things are needed and all three were missing before:
        //
        //   NoDocking + a zero dock id, or ReShade's dock space absorbs the
        //   window and it only ever appears inside ReShade's own overlay;
        //   MouseDrawCursor, or there is no pointer to aim with while the
        //   game's cursor is captured;
        //   block_input_next_frame every frame it is up, or the camera follows
        //   the mouse underneath it.
        if (m_open)
        {
            if (runtime != nullptr && runtime->is_key_pressed(VK_ESCAPE))
                m_open = false;
        }

        if (m_open)
        {
            ImGuiIO &io = ImGui::GetIO();
            io.MouseDrawCursor = true;
            m_drew_cursor = true;

            if (runtime != nullptr)
                runtime->block_input_next_frame();

            push_style(theme, c.panel_opacity, c.panel_scale);

            const ImVec2 screen = display_size();
            const float width = std::min(ImGui::GetFontSize() * 40.0f, std::max(320.0f, screen.x - 40.0f));
            const float height = std::min(ImGui::GetFontSize() * 40.0f, std::max(240.0f, screen.y - 40.0f));

            ImGui::SetNextWindowDockID(0, ImGuiCond_Always);
            ImGui::SetNextWindowSize(ImVec2(width, height), ImGuiCond_FirstUseEver);
            ImGui::SetNextWindowPos(ImVec2(screen.x * 0.5f, screen.y * 0.5f), ImGuiCond_FirstUseEver, ImVec2(0.5f, 0.5f));
            ImGui::SetNextWindowBgAlpha(c.panel_opacity);

            bool open = true;

            constexpr ImGuiWindowFlags flags =
                ImGuiWindowFlags_NoDocking | ImGuiWindowFlags_NoSavedSettings | ImGuiWindowFlags_NoCollapse;

            if (ImGui::Begin("DLSS 5 MANAGER", &open, flags))
            {
                draw_header(runtime);
                draw_body(runtime);
            }

            ImGui::End();
            pop_style();

            if (!open)
                m_open = false;
        }
        else if (m_drew_cursor)
        {
            // Hand the cursor back the moment the panel closes, or it stays
            // painted over the game. Only ever undone if we were the ones who
            // turned it on: ReShade raises the same flag for its own overlay,
            // and clearing that would leave its UI without a pointer.
            ImGui::GetIO().MouseDrawCursor = false;
            m_drew_cursor = false;
        }

        draw_hud(runtime);
    }

    void OverlayUI::draw_hud(reshade::api::effect_runtime *)
    {
        Config &c = config();

        if (c.hud_detail == HudDetail::Off || m_telemetry == nullptr)
            return;

        const Theme &theme = theme_by_name(c.theme);

        const TelemetrySnapshot &t = m_telemetry->snapshot();

        push_style(theme, c.hud_opacity, c.hud_scale);

        const ImVec2 screen = display_size();
        const float margin = 18.0f * c.hud_scale;

        const int corner = static_cast<int>(c.hud_corner);
        const ImVec2 pivot((corner & 1) ? 1.0f : 0.0f, (corner & 2) ? 1.0f : 0.0f);
        const ImVec2 position(
            (corner & 1) ? screen.x - margin : margin,
            (corner & 2) ? screen.y - margin : margin);

        ImGui::SetNextWindowPos(position, ImGuiCond_Always, pivot);
        ImGui::SetNextWindowBgAlpha(c.hud_opacity);

        constexpr ImGuiWindowFlags flags =
            ImGuiWindowFlags_NoDecoration | ImGuiWindowFlags_NoInputs | ImGuiWindowFlags_NoNav |
            ImGuiWindowFlags_AlwaysAutoResize | ImGuiWindowFlags_NoSavedSettings |
            ImGuiWindowFlags_NoFocusOnAppearing | ImGuiWindowFlags_NoDocking;

        if (ImGui::Begin("##dlss5-hud", nullptr, flags))
        {
            const Palette &p = palette();
            char buffer[64] = {};

            // The whole read-out is drawn at the user's scale; the frame rate
            // itself gets an extra step on top so it reads at a glance.
            push_font_scale(c.hud_scale);

            snprintf(buffer, sizeof(buffer), "%.0f", t.fps);
            ImGui::PushStyleColor(ImGuiCol_Text, to_vec4(p.accent));
            push_font_scale(1.9f);
            ImGui::TextUnformatted(buffer);
            pop_font_scale();
            ImGui::PopStyleColor();

            ImGui::SameLine();
            ImGui::PushStyleColor(ImGuiCol_Text, to_vec4(p.text_dim));
            ImGui::TextUnformatted("FPS");
            ImGui::PopStyleColor();

            if (c.hud_detail >= HudDetail::Compact)
            {
                ImGui::Text("%.2f ms", t.frame_ms);

                if (c.hud_show_lows)
                    ImGui::Text("1%%  %.0f     0.1%%  %.0f", t.fps_low_1, t.fps_low_01);

                if (c.hud_show_vram && t.vram_valid)
                {
                    const uint64_t total = t.vram_budget_bytes > 0 ? t.vram_budget_bytes : t.vram_total_bytes;
                    const float fraction = total > 0
                        ? static_cast<float>(static_cast<double>(t.vram_used_bytes) / static_cast<double>(total))
                        : 0.0f;

                    snprintf(buffer, sizeof(buffer), "%s / %s",
                             format_bytes(t.vram_used_bytes).c_str(), format_bytes(total).c_str());

                    ImGui::PushItemWidth(ImGui::GetFontSize() * 11.0f);
                    meter("VRAM", fraction, buffer);
                    ImGui::PopItemWidth();
                }

                if (c.hud_show_gpu && t.gpu_load_valid)
                {
                    if (t.gpu_temp_valid)
                        ImGui::Text("GPU  %d%%   %d C", t.gpu_load_percent, t.gpu_temp_celsius);
                    else
                        ImGui::Text("GPU  %d%%", t.gpu_load_percent);
                }
            }

            if (c.hud_detail == HudDetail::Full && c.hud_show_graph)
            {
                float history[128] = {};
                const size_t count = m_telemetry->frame_history(history, std::size(history));

                if (count > 1)
                {
                    ImGui::PlotLines("##frametime", history, static_cast<int>(count), 0, nullptr,
                                     0.0f, std::max(t.frame_ms_max, 1.0f),
                                     ImVec2(ImGui::GetFontSize() * 12.0f, ImGui::GetFontSize() * 3.2f));
                }
            }

            pop_font_scale();
        }

        ImGui::End();
        pop_style();
    }

    // =====================================================================
    // PANEL
    // =====================================================================
    /// The tab ReShade shows inside its own overlay. It is deliberately just a
    /// way in: the panel itself is a window of its own, and duplicating every
    /// control here would mean two of everything to keep in step.
    void OverlayUI::draw_panel(reshade::api::effect_runtime *runtime)
    {
        Config &c = config();

        if (!c.enabled)
        {
            ImGui::TextDisabled("The overlay is switched off.");
            ImGui::Spacing();

            if (ImGui::Button("Turn it on"))
            {
                c.enabled = true;
                c.save();
            }

            return;
        }

        char key[64] = {};
        snprintf(key, sizeof(key), "%s%s%s%s",
                 c.hotkey_ctrl ? "Ctrl+" : "",
                 c.hotkey_shift ? "Shift+" : "",
                 c.hotkey_alt ? "Alt+" : "",
                 key_name(c.hotkey));

        ImGui::TextWrapped(
            "The DLSS 5 panel opens on its own, over the game - press %s. "
            "Escape closes it, and the game's input is held while it is up.", key);

        ImGui::Spacing();

        if (ImGui::Button("Open the panel now"))
        {
            m_open = true;

            // Step out of the way: two overlays fighting for the cursor is
            // worse than either of them alone.
            if (runtime != nullptr)
                runtime->open_overlay(false, reshade::api::input_source::none);
        }

        ImGui::Spacing();
        ImGui::TextDisabled("Theme and hotkey are set in DLSS 5 MANAGER; the counter is on the panel.");
    }

    /// The strip along the top of the standalone panel: who we are on the left,
    /// what the frame is costing on the right. Drawn as one inset card so the
    /// tabs below read as content rather than as more chrome.
    void OverlayUI::draw_header(reshade::api::effect_runtime *runtime)
    {
        const Palette &p = palette();
        const float rounding = ImGui::GetStyle().FrameRounding;

        ImGui::PushStyleColor(ImGuiCol_ChildBg, ImGui::GetStyle().Colors[ImGuiCol_FrameBg]);
        ImGui::PushStyleVar(ImGuiStyleVar_ChildRounding, rounding);
        ImGui::PushStyleVar(ImGuiStyleVar_WindowPadding, ImVec2(12.0f, 9.0f));

        ImGui::BeginChild("##header", ImVec2(0.0f, 0.0f),
                          ImGuiChildFlags_AutoResizeY | ImGuiChildFlags_AlwaysUseWindowPadding);

        // A dot in the accent, so the panel is recognisable at a glance even
        // before the words are read.
        const ImVec2 cursor = ImGui::GetCursorScreenPos();
        const float radius = ImGui::GetFontSize() * 0.30f;
        ImGui::GetWindowDrawList()->AddCircleFilled(
            ImVec2(cursor.x + radius, cursor.y + ImGui::GetFontSize() * 0.62f), radius, to_u32(p.accent));

        ImGui::Dummy(ImVec2(radius * 2.0f + 6.0f, 0.0f));
        ImGui::SameLine();

        push_font_scale(1.2f);
        ImGui::TextUnformatted("DLSS 5 MANAGER");
        pop_font_scale();

        if (m_telemetry != nullptr)
        {
            const TelemetrySnapshot &t = m_telemetry->snapshot();

            char reading[96] = {};

            if (t.vram_valid)
                snprintf(reading, sizeof(reading), "%.0f FPS   %.2f ms   %s",
                         t.fps, t.frame_ms, format_bytes(t.vram_used_bytes).c_str());
            else
                snprintf(reading, sizeof(reading), "%.0f FPS   %.2f ms", t.fps, t.frame_ms);

            const float width = ImGui::CalcTextSize(reading).x;
            const float close = ImGui::GetFontSize() * 4.2f;
            const float available = ImGui::GetContentRegionAvail().x;

            ImGui::SameLine(0.0f, std::max(12.0f, available - width - close));
            ImGui::PushStyleColor(ImGuiCol_Text, to_vec4(p.accent));
            ImGui::TextUnformatted(reading);
            ImGui::PopStyleColor();
        }

        ImGui::SameLine();
        ImGui::SetCursorPosX(std::max(ImGui::GetCursorPosX(),
                                      ImGui::GetWindowWidth() - ImGui::GetFontSize() * 3.8f));

        if (ImGui::SmallButton("Close"))
        {
            m_open = false;

            if (runtime != nullptr)
                runtime->block_input_next_frame();
        }

        ImGui::EndChild();
        ImGui::PopStyleVar(2);
        ImGui::PopStyleColor();

        ImGui::Spacing();
    }

    void OverlayUI::draw_body(reshade::api::effect_runtime *runtime)
    {
        OptiScalerBridge &bridge = optiscaler();
        const Palette &p = palette();

        // The read-out lives in the header now, so this only carries whatever
        // the last action had to say.
        if (!m_notice.empty())
        {
            ImGui::PushStyleColor(ImGuiCol_Text, to_vec4(p.good));
            ImGui::TextWrapped("%s", m_notice.c_str());
            ImGui::PopStyleColor();
            ImGui::Spacing();
        }

        if (ImGui::BeginTabBar("##dlss5-tabs", ImGuiTabBarFlags_FittingPolicyScroll))
        {
            if (ImGui::BeginTabItem("Performance"))
            {
                tab_performance(runtime);
                ImGui::EndTabItem();
            }

            // The two add-ons the ReShade routes actually install come before
            // OptiScaler's own settings. Each shows only when its add-on has
            // written a configuration, so a tab is never a dead end.
            if (addon_present(runtime, L"nvngx.dll.addon64", kNrPre, "Enabled") &&
                ImGui::BeginTabItem("NR Pre-Upscale"))
            {
                tab_nr_pre_upscale(runtime);
                ImGui::EndTabItem();
            }

            if (renodx_present(runtime) && ImGui::BeginTabItem("RenoDX"))
            {
                tab_renodx(runtime);
                ImGui::EndTabItem();
            }

            // The OptiScaler tabs only exist when OptiScaler does. On a plain
            // ReShade install there is no ini to edit and an empty tab would
            // just be a dead end.
            if (bridge.available())
            {
                if (bridge.has_neural_section() && ImGui::BeginTabItem("DLSS-NR"))
                {
                    tab_dlss_nr();
                    ImGui::EndTabItem();
                }

                if (ImGui::BeginTabItem("Upscaler"))
                {
                    tab_upscaler();
                    ImGui::EndTabItem();
                }

                if (ImGui::BeginTabItem("Frame gen"))
                {
                    tab_frame_gen();
                    ImGui::EndTabItem();
                }
            }

            ImGui::EndTabBar();
        }
    }

    // =====================================================================
    // TABS
    // =====================================================================
    void OverlayUI::tab_performance(reshade::api::effect_runtime *)
    {
        if (m_telemetry == nullptr)
            return;

        const TelemetrySnapshot &t = m_telemetry->snapshot();
        const Palette &p = palette();
        Config &c = config();

        char buffer[64] = {};

        // The two the read-out is actually judged by, right where someone
        // looking at the numbers will reach for them.
        heading("On-screen counter");

        bool counter_on = c.hud_detail != HudDetail::Off;

        if (ImGui::Checkbox("Show the counter over the game", &counter_on))
        {
            c.hud_detail = counter_on ? HudDetail::Compact : HudDetail::Off;
            c.save();
        }

        ImGui::BeginDisabled(!counter_on);
        ImGui::SetNextItemWidth(ImGui::GetFontSize() * 16.0f);

        if (ImGui::SliderFloat("Counter size", &c.hud_scale, 0.5f, 3.0f, "%.2fx"))
            c.save();

        ImGui::EndDisabled();

        heading("Now");

        snprintf(buffer, sizeof(buffer), "%.0f", t.fps);
        stat("FPS", buffer, p.accent, 26.0f);

        snprintf(buffer, sizeof(buffer), "%.2f", t.frame_ms);
        stat("frame ms", buffer, p.text, 26.0f);

        snprintf(buffer, sizeof(buffer), "%.0f", t.fps_low_1);
        stat("1% low", buffer, p.warn, 26.0f);

        snprintf(buffer, sizeof(buffer), "%.0f", t.fps_low_01);
        stat("0.1% low", buffer, p.bad, 0.0f);

        heading("Frame time");

        float history[Telemetry::kHistory] = {};
        const size_t count = m_telemetry->frame_history(history, std::size(history));

        if (count > 1)
        {
            snprintf(buffer, sizeof(buffer), "min %.2f    max %.2f ms", t.frame_ms_min, t.frame_ms_max);
            ImGui::PlotLines("##frametime-graph", history, static_cast<int>(count), 0, buffer,
                             0.0f, std::max(t.frame_ms_max * 1.1f, 1.0f),
                             ImVec2(-FLT_MIN, ImGui::GetFontSize() * 6.0f));
        }
        else
        {
            ImGui::TextDisabled("Collecting frames...");
        }

        heading("Video memory");

        if (t.vram_valid)
        {
            const uint64_t total = t.vram_budget_bytes > 0 ? t.vram_budget_bytes : t.vram_total_bytes;
            const float fraction = total > 0
                ? static_cast<float>(static_cast<double>(t.vram_used_bytes) / static_cast<double>(total))
                : 0.0f;

            snprintf(buffer, sizeof(buffer), "%s / %s  (%.0f%%)",
                     format_bytes(t.vram_used_bytes).c_str(), format_bytes(total).c_str(), fraction * 100.0f);

            meter(nullptr, fraction, buffer);

            if (t.vram_total_bytes > 0)
                ImGui::TextDisabled("Adapter total: %s", format_bytes(t.vram_total_bytes).c_str());
        }
        else
        {
            ImGui::TextDisabled("Video memory reporting is unavailable on this adapter.");
        }

        heading("GPU");

        if (!t.adapter_name.empty())
            ImGui::TextDisabled("%s", t.adapter_name.c_str());

        if (t.gpu_load_valid)
        {
            snprintf(buffer, sizeof(buffer), "%d%%", t.gpu_load_percent);
            meter("Core load", static_cast<float>(t.gpu_load_percent) / 100.0f, buffer);
        }

        if (t.gpu_temp_valid)
            ImGui::Text("Temperature   %d C", t.gpu_temp_celsius);

        if (t.gpu_clock_valid)
            ImGui::Text("Core clock    %d MHz", t.gpu_clock_mhz);

        if (t.gpu_fan_valid)
            ImGui::Text("Fan           %d rpm", t.gpu_fan_percent);

        if (!t.gpu_load_valid && !t.gpu_temp_valid)
        {
            ImGui::TextDisabled(
                "Core load, temperature and clocks come from the vendor's own monitoring library.\n"
                "NVAPI was not available in this process, so only the numbers above are shown.");
        }
    }

    /// One add-on's own panel, drawn inside ours.
    ///
    /// Our theme is already pushed, so its widgets come out in the overlay's
    /// colours rather than ReShade's - which is the only thing about it that
    /// changes here. Everything else is exactly the panel it draws in its own
    /// tab, over exactly the same state.
    void OverlayUI::draw_bridge(AddonBridge &bridge, reshade::api::effect_runtime *runtime)
    {
        const Palette &p = palette();

        ImGui::PushStyleColor(ImGuiCol_Text, to_vec4(p.text_dim));
        ImGui::TextWrapped(
            "%s's own panel, drawn here. It is the same panel and the same settings as its tab in "
            "ReShade - not a copy - so a change in either is a change in both, immediately.",
            bridge.display());
        ImGui::PopStyleColor();

        ImGui::Spacing();
        bridge.draw(runtime);
    }

    void OverlayUI::tab_nr_pre_upscale(reshade::api::effect_runtime *runtime)
    {
        if (runtime == nullptr)
            return;

        ImGui::TextWrapped(
            "The neural-upstream add-on, which runs neural rendering at render resolution before the "
            "game's own upscale. F7 toggles it.");

        // Its own panel first; the configuration editor below is the fallback.
        if (nr_bridge().available())
        {
            draw_bridge(nr_bridge(), runtime);

            ImGui::Spacing();

            if (!ImGui::CollapsingHeader("Saved configuration (applies on reload)",
                                         ImGuiTreeNodeFlags_DefaultOpen))
                return;

            ImGui::Indent(6.0f);
        }
        else
        {
            ImGui::PushStyleColor(ImGuiCol_Text, to_vec4(palette().warn));
            ImGui::TextWrapped("%s", nr_bridge().status().c_str());
            ImGui::PopStyleColor();
            ImGui::Spacing();
        }

        bool changed = false;

        changed |= draw_reno_group(runtime, "Neural rendering", kNrPreCore, m_renodx_preset);
        changed |= draw_reno_group(runtime, "Exposure", kNrPreExposure, m_renodx_preset);
        changed |= draw_reno_group(runtime, "Network parameters", kNrPreNetwork, m_renodx_preset);

        if (ImGui::CollapsingHeader("Reuse and reprojection"))
        {
            ImGui::Indent(6.0f);

            for (const RenoSetting &setting : kNrPreReuse)
                changed |= reno_row(runtime, setting, m_renodx_preset);

            ImGui::Unindent(6.0f);
        }
        if (changed)
            m_notice.clear();

        const RenoSetting *declared[std::size(kNrPreCore) + std::size(kNrPreExposure) +
                                    std::size(kNrPreNetwork) + std::size(kNrPreReuse)] = {};
        int count = 0;

        for (const RenoSetting &s : kNrPreCore)     declared[count++] = &s;
        for (const RenoSetting &s : kNrPreExposure) declared[count++] = &s;
        for (const RenoSetting &s : kNrPreNetwork)  declared[count++] = &s;
        for (const RenoSetting &s : kNrPreReuse)    declared[count++] = &s;

        draw_undeclared(runtime, kNrPre, declared, count);

        ImGui::Spacing();

        if (ImGui::Button("Re-scan saved settings"))
        {
            reload_reshade_ini();
            m_notice = "Re-read ReShade.ini, so any setting added since the game started shows up.";
        }

        if (nr_bridge().available())
            ImGui::Unindent(6.0f);
    }

    void OverlayUI::tab_renodx(reshade::api::effect_runtime *runtime)
    {
        if (runtime == nullptr)
            return;

        if (!renodx_present(runtime))
        {
            ImGui::TextWrapped(
                "The RenoDX DLSS 5 add-on is not in this game. It is installed by the DX12, DX11 and "
                "DX9 routes; the OptiScaler route deliberately does not carry it.");
            return;
        }

        // RenoDX's own panel is the real thing, so it comes first and the
        // configuration editor below folds away behind it - that one edits the
        // saved file, which only takes effect when RenoDX next loads, and is
        // here for the case where its panel could not be found at all.
        if (renodx_bridge().available())
        {
            draw_bridge(renodx_bridge(), runtime);

            ImGui::Spacing();

            if (!ImGui::CollapsingHeader("Saved configuration (applies on reload)",
                                         ImGuiTreeNodeFlags_DefaultOpen))
                return;

            ImGui::Indent(6.0f);
        }
        else
        {
            // Never fall through in silence: if the panel could not be reached
            // the editor below is a different thing, and the user has to know.
            ImGui::PushStyleColor(ImGuiCol_Text, to_vec4(palette().warn));
            ImGui::TextWrapped("%s", renodx_bridge().status().c_str());
            ImGui::PopStyleColor();
            ImGui::Spacing();
        }

        // RenoDX keeps three preset banks and the per-preset controls below
        // follow whichever one is selected here.
        ImGui::TextDisabled("PRESET");
        ImGui::SameLine();

        for (int preset = 1; preset <= 3; ++preset)
        {
            ImGui::PushID(preset);

            const bool active = m_renodx_preset == preset;

            if (active)
                ImGui::PushStyleColor(ImGuiCol_Button, ImGui::GetStyle().Colors[ImGuiCol_ButtonActive]);

            char label[16] = {};
            snprintf(label, sizeof(label), "#%d", preset);

            if (ImGui::Button(label))
                m_renodx_preset = preset;

            if (active)
                ImGui::PopStyleColor();

            ImGui::PopID();

            if (preset < 3)
                ImGui::SameLine();
        }

        bool changed = false;

        changed |= draw_reno_group(runtime, "Global controls", kRenoGlobalControls, m_renodx_preset);
        changed |= draw_reno_group(runtime, "Neural rendering", kRenoPresetControls, m_renodx_preset);

        if (ImGui::CollapsingHeader("DLSS render-preset hints"))
        {
            ImGui::Indent(6.0f);

            for (const RenoSetting &setting : kRenoRenderPresets)
                changed |= reno_row(runtime, setting, m_renodx_preset);

            ImGui::Unindent(6.0f);
        }

        // Anything RenoDX saves that has no row above still turns up, so a
        // newer build cannot hide a setting from this tab.
        {
            const RenoSetting *declared[std::size(kRenoGlobalControls) + std::size(kRenoPresetControls) +
                                        std::size(kRenoRenderPresets)] = {};
            int count = 0;

            for (const RenoSetting &s : kRenoGlobalControls) declared[count++] = &s;
            for (const RenoSetting &s : kRenoPresetControls) declared[count++] = &s;
            for (const RenoSetting &s : kRenoRenderPresets)  declared[count++] = &s;

            draw_undeclared(runtime, kRenoGlobal, declared, count);

            char preset_section[64] = {};
            snprintf(preset_section, sizeof(preset_section), "%s-preset%d", kRenoGlobal, m_renodx_preset);
            draw_undeclared(runtime, preset_section, declared, count);
        }

        if (changed)
            m_notice.clear();

        if (renodx_bridge().available())
            ImGui::Unindent(6.0f);
    }

    void OverlayUI::tab_dlss_nr()
    {
        OptiScalerBridge &bridge = optiscaler();

        // Where the values are coming from, said out loud. Without this the
        // tab looks identical whether it found the file or not.
        ImGui::PushStyleColor(ImGuiCol_Text, to_vec4(palette().text_dim));
        ImGui::TextWrapped("%s", narrow(bridge.path()).c_str());
        ImGui::PopStyleColor();

        ImGui::TextWrapped(
            "OptiScaler reads this file once, when the game starts, and keeps no channel open for "
            "anything to change it afterwards. Edits here are saved to the file and apply the next "
            "time you launch. For a change you can see right now, OptiScaler's own menu is on Insert. "
            "Every control is documented by OptiScaler itself; hover the (?) beside it.");

        ImGui::Spacing();
        draw_groups(bridge, OptiScalerBridge::dlss_nr_groups(), true);

        ImGui::Spacing();
        ImGui::Separator();

        ImGui::BeginDisabled(!bridge.dirty());

        if (ImGui::Button("Save to OptiScaler.ini"))
        {
            bridge.save();
            m_notice = bridge.status();
        }

        ImGui::EndDisabled();

        ImGui::SameLine();

        if (ImGui::Button("Reload from disk"))
        {
            bridge.refresh();
            m_notice = "Reloaded OptiScaler.ini.";
        }

        if (bridge.dirty())
        {
            ImGui::SameLine();
            ImGui::TextDisabled("unsaved changes");
        }
    }

    void OverlayUI::tab_upscaler()
    {
        OptiScalerBridge &bridge = optiscaler();

        draw_groups(bridge, OptiScalerBridge::upscaler_groups(), false);

        ImGui::Spacing();
        ImGui::Separator();

        ImGui::BeginDisabled(!bridge.dirty());

        if (ImGui::Button("Save to OptiScaler.ini"))
        {
            bridge.save();
            m_notice = bridge.status();
        }

        ImGui::EndDisabled();
    }

    void OverlayUI::tab_frame_gen()
    {
        OptiScalerBridge &bridge = optiscaler();

        ImGui::TextWrapped(
            "Frame generation is the part OptiScaler is most particular about; leave anything you "
            "are unsure of on auto and let it decide.");

        ImGui::Spacing();
        draw_groups(bridge, OptiScalerBridge::frame_gen_groups(), false);

        ImGui::Spacing();
        ImGui::Separator();

        ImGui::BeginDisabled(!bridge.dirty());

        if (ImGui::Button("Save to OptiScaler.ini"))
        {
            bridge.save();
            m_notice = bridge.status();
        }

        ImGui::EndDisabled();
    }

    OverlayUI &overlay_ui()
    {
        static OverlayUI instance;
        return instance;
    }
}
