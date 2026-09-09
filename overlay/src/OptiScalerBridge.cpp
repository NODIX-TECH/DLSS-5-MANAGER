#include "OptiScalerBridge.h"

#include <charconv>
#include <cstdio>

namespace dlss5
{
    namespace
    {
        constexpr const char *kAuto = "auto";

        // ---- Named value lists -------------------------------------------
        const char *const kDownscalers[] = {
            "FSR1", "Bicubic", "Catmull-Rom", "Lanczos2", "Lanczos3", "Kaiser2", "Kaiser3", "MAGIC"
        };

        const char *const kDebugViews[] = {
            "Off", "Model input", "Raw model answer", "Difference x20"
        };

        const char *const kStyles[] = { "Standard", "Natural", "Cinematic" };

        const char *const kPresets[] = { "0", "1", "2", "3" };

        const char *const kDx11Upscalers[] = {
            "auto", "fsr22", "fsr31", "xess", "xess_12", "fsr21_12", "fsr22_12", "ffx_12", "dlss", "dlss_12"
        };

        const char *const kDx12Upscalers[] = { "auto", "xess", "fsr21", "fsr22", "ffx", "dlss" };

        const char *const kVulkanUpscalers[] = {
            "auto", "fsr21", "fsr22", "ffx", "xess", "fsr21_12", "ffx_12", "dlss"
        };

        const char *const kSharpenShaders[] = { "auto", "rcas", "da", "lcda" };

        const char *const kFpsOverlayTypes[] = {
            "Just FPS", "Simple", "Detailed", "Detailed + Graph", "Full", "Full + Graph", "Reflex timings"
        };

        const char *const kFpsOverlayPos[] = { "Top Left", "Top Right", "Bottom Left", "Bottom Right" };

        template <typename T, size_t N>
        constexpr int count_of(const T (&)[N]) { return static_cast<int>(N); }

        // =================================================================
        // DLSS NEURAL RENDERING
        // =================================================================
        const Setting kNrCore[] = {
            { "DlssNr", "Enabled",         "Enable neural rendering",     SettingKind::Bool,   0, 0, nullptr, 0 },
            { "DlssNr", "RunBeforeSR",     "Apply before super resolution", SettingKind::Bool, 0, 0, nullptr, 0 },
            { "DlssNr", "ApplyAfterRR",    "Apply after ray reconstruction (DX12)", SettingKind::Bool, 0, 0, nullptr, 0 },
            { "DlssNr", "RRPasses",        "NR passes after RR",          SettingKind::Int,    1, 3, nullptr, 0 },
            { "DlssNr", "RRWorkingScale",  "NR model scale after RR",     SettingKind::Float,  0.25f, 2.0f, nullptr, 0 },
        };

        const Setting kNrCost[] = {
            { "DlssNr", "Passes",            "Model passes",        SettingKind::Int,   1, 3, nullptr, 0 },
            { "DlssNr", "WorkingScale",      "Model resolution",    SettingKind::Float, 0.25f, 2.0f, nullptr, 0 },
            { "DlssNr", "ScalingDownscaler", "Enlargement",         SettingKind::Choice, 0, 7, kDownscalers, count_of(kDownscalers) },
        };

        const Setting kNrStrength[] = {
            { "DlssNr", "TransferStrength", "Detail strength", SettingKind::Float, 0.0f, 2.0f, nullptr, 0 },
            { "DlssNr", "ColourStrength",   "Colour strength", SettingKind::Float, 0.0f, 2.0f, nullptr, 0 },
        };

        const Setting kNrPass1[] = {
            { "DlssNr", "Preset",         "Preset",          SettingKind::Choice, 0, 3, kPresets, count_of(kPresets) },
            { "DlssNr", "Style",          "Style",           SettingKind::Choice, 0, 2, kStyles, count_of(kStyles) },
            { "DlssNr", "Intensity",      "Intensity",       SettingKind::Float, 0.0f, 2.0f, nullptr, 0 },
            { "DlssNr", "LocalStructure", "Local structure", SettingKind::Float, 0.0f, 2.0f, nullptr, 0 },
            { "DlssNr", "LocalTone",      "Local tone",      SettingKind::Float, 0.0f, 2.0f, nullptr, 0 },
            { "DlssNr", "SkinStructure",  "Skin structure",  SettingKind::Float, -1.0f, 2.0f, nullptr, 0 },
            { "DlssNr", "AutoMask",       "Auto skin mask",  SettingKind::Bool, 0, 0, nullptr, 0 },
        };

        const Setting kNrPass2[] = {
            { "DlssNr", "Pass2Preset",         "Preset",          SettingKind::Choice, 0, 3, kPresets, count_of(kPresets) },
            { "DlssNr", "Pass2Style",          "Style",           SettingKind::Choice, 0, 2, kStyles, count_of(kStyles) },
            { "DlssNr", "Pass2Intensity",      "Intensity",       SettingKind::Float, 0.0f, 2.0f, nullptr, 0 },
            { "DlssNr", "Pass2LocalStructure", "Local structure", SettingKind::Float, 0.0f, 2.0f, nullptr, 0 },
            { "DlssNr", "Pass2LocalTone",      "Local tone",      SettingKind::Float, 0.0f, 2.0f, nullptr, 0 },
            { "DlssNr", "Pass2SkinStructure",  "Skin structure",  SettingKind::Float, -1.0f, 2.0f, nullptr, 0 },
            { "DlssNr", "Pass2AutoMask",       "Auto skin mask",  SettingKind::Bool, 0, 0, nullptr, 0 },
        };

        const Setting kNrPass3[] = {
            { "DlssNr", "Pass3Preset",         "Preset",          SettingKind::Choice, 0, 3, kPresets, count_of(kPresets) },
            { "DlssNr", "Pass3Style",          "Style",           SettingKind::Choice, 0, 2, kStyles, count_of(kStyles) },
            { "DlssNr", "Pass3Intensity",      "Intensity",       SettingKind::Float, 0.0f, 2.0f, nullptr, 0 },
            { "DlssNr", "Pass3LocalStructure", "Local structure", SettingKind::Float, 0.0f, 2.0f, nullptr, 0 },
            { "DlssNr", "Pass3LocalTone",      "Local tone",      SettingKind::Float, 0.0f, 2.0f, nullptr, 0 },
            { "DlssNr", "Pass3SkinStructure",  "Skin structure",  SettingKind::Float, -1.0f, 2.0f, nullptr, 0 },
            { "DlssNr", "Pass3AutoMask",       "Auto skin mask",  SettingKind::Bool, 0, 0, nullptr, 0 },
        };

        const Setting kNrExposure[] = {
            { "DlssNr", "WhitePointScale", "Paper white (x game exposure)", SettingKind::Float, 0.1f, 8.0f, nullptr, 0 },
            { "DlssNr", "MaxRatio",        "Highlight guard",               SettingKind::Float, 1.0f, 8.0f, nullptr, 0 },
        };

        const Setting kNrSkin[] = {
            { "DlssNr", "SkinToneEnabled",   "Skin and environment filter", SettingKind::Bool, 0, 0, nullptr, 0 },
            { "DlssNr", "SkinProtection",    "Skin protection",             SettingKind::Bool, 0, 0, nullptr, 0 },
            { "DlssNr", "SkinDetail",        "Skin detail",                 SettingKind::Float, 0.0f, 1.0f, nullptr, 0 },
            { "DlssNr", "SkinColour",        "Skin colour",                 SettingKind::Float, 0.0f, 1.0f, nullptr, 0 },
            { "DlssNr", "EnvironmentDetail", "Environment detail",          SettingKind::Float, 0.0f, 1.0f, nullptr, 0 },
            { "DlssNr", "EnvironmentColour", "Environment colour",          SettingKind::Float, 0.0f, 1.0f, nullptr, 0 },
            { "DlssNr", "ShowSkinMask",      "Show skin mask",              SettingKind::Bool, 0, 0, nullptr, 0 },
        };

        const Setting kNrDebug[] = {
            { "DlssNr", "DebugView",   "Debug view",   SettingKind::Choice, 0, 3, kDebugViews, count_of(kDebugViews) },
            { "DlssNr", "AutoCapture", "Auto capture", SettingKind::Bool, 0, 0, nullptr, 0 },
        };

        // =================================================================
        // UPSCALERS
        // =================================================================
        const Setting kUpscalerPick[] = {
            { "Upscalers", "Dx11Upscaler",   "DirectX 11 upscaler", SettingKind::Text, 0, 0, kDx11Upscalers, count_of(kDx11Upscalers) },
            { "Upscalers", "Dx12Upscaler",   "DirectX 12 upscaler", SettingKind::Text, 0, 0, kDx12Upscalers, count_of(kDx12Upscalers) },
            { "Upscalers", "VulkanUpscaler", "Vulkan upscaler",     SettingKind::Text, 0, 0, kVulkanUpscalers, count_of(kVulkanUpscalers) },
        };

        const Setting kSharpness[] = {
            { "Sharpness", "Shader",            "Sharpening shader", SettingKind::Text, 0, 0, kSharpenShaders, count_of(kSharpenShaders) },
            { "Sharpness", "OverrideSharpness", "Override sharpness", SettingKind::Bool, 0, 0, nullptr, 0 },
            { "Sharpness", "Sharpness",         "Strength",           SettingKind::Float, 0.0f, 1.3f, nullptr, 0 },
        };

        const Setting kOutputScaling[] = {
            { "OutputScaling", "Enabled",    "Output scaling", SettingKind::Bool,  0, 0, nullptr, 0 },
            { "OutputScaling", "Multiplier", "Ratio",          SettingKind::Float, 0.5f, 3.0f, nullptr, 0 },
            { "OutputScaling", "Downscaler", "Downscaler",     SettingKind::Choice, 0, 7, kDownscalers, count_of(kDownscalers) },
        };

        const Setting kFramerate[] = {
            { "Framerate", "FramerateLimit", "Frame rate limit", SettingKind::Float, 0.0f, 360.0f, nullptr, 0 },
        };

        const Setting kMenu[] = {
            { "Menu", "OverlayMenu",   "OptiScaler menu",      SettingKind::Bool,   0, 0, nullptr, 0 },
            { "Menu", "Scale",         "Menu scale",           SettingKind::Float,  0.5f, 2.0f, nullptr, 0 },
            { "Menu", "ShowFps",       "OptiScaler FPS overlay", SettingKind::Bool, 0, 0, nullptr, 0 },
            { "Menu", "FpsOverlayType","FPS overlay type",     SettingKind::Choice, 0, 6, kFpsOverlayTypes, count_of(kFpsOverlayTypes) },
            { "Menu", "FpsOverlayPos", "FPS overlay position", SettingKind::Choice, 0, 3, kFpsOverlayPos, count_of(kFpsOverlayPos) },
        };

        // =================================================================
        // FRAME GENERATION
        // =================================================================
        const Setting kFrameGen[] = {
            { "FrameGen", "Enabled",     "Frame generation",  SettingKind::Bool, 0, 0, nullptr, 0 },
            { "FrameGen", "FGType",      "Frame gen backend", SettingKind::Text, 0, 0, nullptr, 0 },
        };

        const Setting kOptiFg[] = {
            { "OptiFG", "Enabled",        "OptiFG",             SettingKind::Bool, 0, 0, nullptr, 0 },
            { "OptiFG", "DebugView",      "OptiFG debug view",  SettingKind::Bool, 0, 0, nullptr, 0 },
            { "OptiFG", "HUDFix",         "HUD fix",            SettingKind::Bool, 0, 0, nullptr, 0 },
            { "OptiFG", "AllowHighPriority", "High priority queue", SettingKind::Bool, 0, 0, nullptr, 0 },
        };

        std::string trim_copy(std::string_view text) { return std::string(trim(text)); }
    }

    const std::vector<SettingGroup> &OptiScalerBridge::dlss_nr_groups()
    {
        static const std::vector<SettingGroup> groups = {
            { "Neural rendering", kNrCore, count_of(kNrCore) },
            { "Cost", kNrCost, count_of(kNrCost) },
            { "How much of it lands", kNrStrength, count_of(kNrStrength) },
            { "Pass 1", kNrPass1, count_of(kNrPass1) },
            { "Pass 2", kNrPass2, count_of(kNrPass2) },
            { "Pass 3", kNrPass3, count_of(kNrPass3) },
            { "Exposure", kNrExposure, count_of(kNrExposure) },
            { "Skin and environment", kNrSkin, count_of(kNrSkin) },
            { "Compare", kNrDebug, count_of(kNrDebug) },
        };

        return groups;
    }

    const std::vector<SettingGroup> &OptiScalerBridge::upscaler_groups()
    {
        static const std::vector<SettingGroup> groups = {
            { "Upscaler", kUpscalerPick, count_of(kUpscalerPick) },
            { "Sharpening", kSharpness, count_of(kSharpness) },
            { "Output scaling", kOutputScaling, count_of(kOutputScaling) },
            { "Frame rate", kFramerate, count_of(kFramerate) },
            { "OptiScaler menu", kMenu, count_of(kMenu) },
        };

        return groups;
    }

    const std::vector<SettingGroup> &OptiScalerBridge::frame_gen_groups()
    {
        static const std::vector<SettingGroup> groups = {
            { "Frame generation", kFrameGen, count_of(kFrameGen) },
            { "OptiFG", kOptiFg, count_of(kOptiFg) },
        };

        return groups;
    }

    void OptiScalerBridge::refresh()
    {
        m_path = game_path(L"OptiScaler.ini");
        m_available = m_file.load(m_path);
        m_neural = m_available && m_file.has_section("DlssNr");

        if (!m_available)
            m_status = "OptiScaler.ini was not found next to the game.";
        else
            m_status.clear();
    }

    bool OptiScalerBridge::is_auto(const Setting &setting) const
    {
        const auto value = m_file.get(setting.section, setting.key);
        return !value.has_value() || value->empty() || iequals(*value, kAuto);
    }

    void OptiScalerBridge::set_auto(const Setting &setting)
    {
        m_file.set(setting.section, setting.key, kAuto);
    }

    bool OptiScalerBridge::get_bool(const Setting &setting, bool fallback) const
    {
        const auto value = m_file.get(setting.section, setting.key);

        if (!value.has_value())
            return fallback;

        if (iequals(*value, "true") || *value == "1")
            return true;

        if (iequals(*value, "false") || *value == "0")
            return false;

        return fallback;
    }

    int OptiScalerBridge::get_int(const Setting &setting, int fallback) const
    {
        const auto value = m_file.get(setting.section, setting.key);

        if (!value.has_value())
            return fallback;

        const std::string text = trim_copy(*value);
        int result = 0;
        const auto parsed = std::from_chars(text.data(), text.data() + text.size(), result);
        return parsed.ec == std::errc() ? result : fallback;
    }

    float OptiScalerBridge::get_float(const Setting &setting, float fallback) const
    {
        const auto value = m_file.get(setting.section, setting.key);

        if (!value.has_value())
            return fallback;

        const std::string text = trim_copy(*value);
        char *end = nullptr;
        const float result = strtof(text.c_str(), &end);
        return (end != nullptr && end != text.c_str()) ? result : fallback;
    }

    std::string OptiScalerBridge::get_text(const Setting &setting, const char *fallback) const
    {
        const auto value = m_file.get(setting.section, setting.key);
        return value.has_value() && !value->empty() ? *value : std::string(fallback);
    }

    void OptiScalerBridge::set_bool(const Setting &setting, bool value)
    {
        m_file.set(setting.section, setting.key, value ? "true" : "false");
    }

    void OptiScalerBridge::set_int(const Setting &setting, int value)
    {
        m_file.set(setting.section, setting.key, std::to_string(value));
    }

    void OptiScalerBridge::set_float(const Setting &setting, float value)
    {
        char buffer[32] = {};
        snprintf(buffer, sizeof(buffer), "%.3f", value);
        m_file.set(setting.section, setting.key, buffer);
    }

    void OptiScalerBridge::set_text(const Setting &setting, std::string_view value)
    {
        m_file.set(setting.section, setting.key, value);
    }

    std::string OptiScalerBridge::comment(const Setting &setting) const
    {
        return m_file.comment_for(setting.section, setting.key);
    }

    bool OptiScalerBridge::save()
    {
        if (!m_available)
        {
            m_status = "OptiScaler.ini was not found next to the game.";
            return false;
        }

        if (m_file.save())
        {
            m_status = "Saved. OptiScaler reads its settings at start-up, so restart the game to apply.";
            return true;
        }

        m_status = "Could not write OptiScaler.ini - the original is untouched.";
        return false;
    }

    OptiScalerBridge &optiscaler()
    {
        static OptiScalerBridge instance;
        return instance;
    }
}
