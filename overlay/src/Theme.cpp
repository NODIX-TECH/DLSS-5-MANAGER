#define ImTextureID ImU64
#include <imgui.h>
#include <reshade.hpp>

#include "Theme.h"

#include <optional>

namespace dlss5
{
    namespace
    {
        ImVec4 rgb(uint32_t value, float alpha = 1.0f)
        {
            return ImVec4(
                static_cast<float>((value >> 16) & 0xFF) / 255.0f,
                static_cast<float>((value >> 8) & 0xFF) / 255.0f,
                static_cast<float>(value & 0xFF) / 255.0f,
                alpha);
        }

        /// Mixes an accent tint into a dark surface, which is what gives each
        /// theme its own glass rather than the same grey with a coloured button.
        ImVec4 surface(uint32_t tint, float base, float strength, float alpha)
        {
            const ImVec4 t = rgb(tint);
            return ImVec4(
                base + t.x * strength,
                base + t.y * strength,
                base + t.z * strength,
                alpha);
        }

        Palette g_palette = {};

        // ReShade forwards the ImGui *functions* through its table, but
        // ImGuiStyle's own members - the default constructor and
        // ScaleAllSizes - live in imgui.cpp, which an add-on never compiles.
        // So the style is saved by copy (implicitly generated, no constructor
        // involved) and the scaling below is applied by hand.
        std::optional<ImGuiStyle> g_saved_style;
        bool g_pushed = false;
    }

    const std::vector<Theme> &themes()
    {
        static const std::vector<Theme> list = {
            { "Neon Emerald",     0x00FF88, 0x0FA968, 0x00FF88, false },
            { "Cyber Cyan",       0x00F2FE, 0x0A9AAE, 0x00F2FE, false },
            { "Electric Violet",  0x8A2BE2, 0x5B1C95, 0x8A2BE2, false },
            { "Supernova Amber",  0xF59E0B, 0xA26908, 0xF59E0B, false },
            { "Eclipse Crimson",  0xEF4444, 0x9B2C2C, 0xEF4444, false },
            { "Graphite Minimal", 0xE2E8F0, 0x94A3B8, 0x7C8595, false },
        };

        return list;
    }

    const Theme &theme_by_name(std::string_view name)
    {
        const std::vector<Theme> &list = themes();

        for (const Theme &theme : list)
        {
            if (iequals(theme.name, name))
                return theme;
        }

        return list.front();
    }

    const Palette &palette()
    {
        return g_palette;
    }

    void push_style(const Theme &theme, float opacity, float scale)
    {
        ImGuiStyle &style = ImGui::GetStyle();

        if (!g_pushed)
        {
            g_saved_style = style;
            g_pushed = true;
        }

        const float a = std::clamp(opacity, 0.3f, 1.0f);
        const float s = std::clamp(scale, 0.6f, 2.0f);

        // ---- Geometry: the 8-12px radii the design system asks for --------
        // Everything that is a length is scaled here rather than through
        // ScaleAllSizes, which is not part of the add-on surface.
        style.WindowRounding = 12.0f * s;
        style.ChildRounding = 10.0f * s;
        style.FrameRounding = 8.0f * s;
        style.PopupRounding = 10.0f * s;
        style.ScrollbarRounding = 8.0f * s;
        style.GrabRounding = 8.0f * s;
        style.TabRounding = 8.0f * s;

        // Borders stay hairlines at any scale; a scaled 1px border reads as a
        // frame rather than an edge.
        style.WindowBorderSize = 1.0f;
        style.ChildBorderSize = 1.0f;
        style.FrameBorderSize = 1.0f;
        style.PopupBorderSize = 1.0f;

        style.WindowPadding = ImVec2(16.0f * s, 14.0f * s);
        style.FramePadding = ImVec2(11.0f * s, 7.0f * s);
        style.ItemSpacing = ImVec2(10.0f * s, 9.0f * s);
        style.ItemInnerSpacing = ImVec2(8.0f * s, 6.0f * s);
        style.IndentSpacing = 18.0f * s;
        style.ScrollbarSize = 12.0f * s;
        style.GrabMinSize = 12.0f * s;
        style.CellPadding = ImVec2(8.0f * s, 6.0f * s);

        style.WindowTitleAlign = ImVec2(0.0f, 0.5f);
        style.SeparatorTextBorderSize = 1.0f;
        style.SeparatorTextAlign = ImVec2(0.0f, 0.5f);
        style.SeparatorTextPadding = ImVec2(16.0f * s, 8.0f * s);

        // ---- Colour -------------------------------------------------------
        const ImVec4 accent = rgb(theme.accent);
        const ImVec4 accent_dim = rgb(theme.accent_dim);

        const ImVec4 window = theme.light_surfaces
            ? ImVec4(0.93f, 0.94f, 0.96f, a)
            : surface(theme.tint, 0.055f, 0.012f, a);

        const ImVec4 child = theme.light_surfaces
            ? ImVec4(0.97f, 0.97f, 0.99f, a * 0.9f)
            : surface(theme.tint, 0.075f, 0.016f, a * 0.85f);

        const ImVec4 frame = theme.light_surfaces
            ? ImVec4(0.88f, 0.89f, 0.92f, 1.0f)
            : surface(theme.tint, 0.105f, 0.02f, 1.0f);

        const ImVec4 frame_hover = theme.light_surfaces
            ? ImVec4(0.83f, 0.85f, 0.89f, 1.0f)
            : surface(theme.tint, 0.145f, 0.03f, 1.0f);

        const ImVec4 text = theme.light_surfaces ? ImVec4(0.10f, 0.12f, 0.15f, 1.0f)
                                                 : ImVec4(0.94f, 0.96f, 0.98f, 1.0f);
        const ImVec4 text_dim = theme.light_surfaces ? ImVec4(0.38f, 0.42f, 0.48f, 1.0f)
                                                     : ImVec4(0.58f, 0.63f, 0.70f, 1.0f);

        const ImVec4 border = theme.light_surfaces
            ? ImVec4(0.0f, 0.0f, 0.0f, 0.12f)
            : ImVec4(1.0f, 1.0f, 1.0f, 0.13f);

        ImVec4 *c = style.Colors;

        c[ImGuiCol_Text] = text;
        c[ImGuiCol_TextDisabled] = text_dim;
        c[ImGuiCol_WindowBg] = window;
        c[ImGuiCol_ChildBg] = child;
        c[ImGuiCol_PopupBg] = surface(theme.tint, 0.04f, 0.012f, 0.98f);
        c[ImGuiCol_Border] = border;
        c[ImGuiCol_BorderShadow] = ImVec4(0.0f, 0.0f, 0.0f, 0.0f);

        c[ImGuiCol_FrameBg] = frame;
        c[ImGuiCol_FrameBgHovered] = frame_hover;
        c[ImGuiCol_FrameBgActive] = ImVec4(accent.x, accent.y, accent.z, 0.22f);

        c[ImGuiCol_TitleBg] = surface(theme.tint, 0.045f, 0.01f, a);
        c[ImGuiCol_TitleBgActive] = surface(theme.tint, 0.07f, 0.02f, a);
        c[ImGuiCol_TitleBgCollapsed] = surface(theme.tint, 0.04f, 0.01f, a * 0.8f);
        c[ImGuiCol_MenuBarBg] = surface(theme.tint, 0.06f, 0.015f, a);

        c[ImGuiCol_ScrollbarBg] = ImVec4(0.0f, 0.0f, 0.0f, 0.16f);
        c[ImGuiCol_ScrollbarGrab] = surface(theme.tint, 0.20f, 0.04f, 0.9f);
        c[ImGuiCol_ScrollbarGrabHovered] = ImVec4(accent.x, accent.y, accent.z, 0.55f);
        c[ImGuiCol_ScrollbarGrabActive] = accent;

        c[ImGuiCol_CheckMark] = accent;
        c[ImGuiCol_SliderGrab] = accent_dim;
        c[ImGuiCol_SliderGrabActive] = accent;

        c[ImGuiCol_Button] = surface(theme.tint, 0.13f, 0.025f, 1.0f);
        c[ImGuiCol_ButtonHovered] = ImVec4(accent.x, accent.y, accent.z, 0.28f);
        c[ImGuiCol_ButtonActive] = ImVec4(accent.x, accent.y, accent.z, 0.45f);

        c[ImGuiCol_Header] = ImVec4(accent.x, accent.y, accent.z, 0.20f);
        c[ImGuiCol_HeaderHovered] = ImVec4(accent.x, accent.y, accent.z, 0.32f);
        c[ImGuiCol_HeaderActive] = ImVec4(accent.x, accent.y, accent.z, 0.44f);

        c[ImGuiCol_Separator] = border;
        c[ImGuiCol_SeparatorHovered] = ImVec4(accent.x, accent.y, accent.z, 0.5f);
        c[ImGuiCol_SeparatorActive] = accent;

        c[ImGuiCol_ResizeGrip] = ImVec4(accent.x, accent.y, accent.z, 0.18f);
        c[ImGuiCol_ResizeGripHovered] = ImVec4(accent.x, accent.y, accent.z, 0.40f);
        c[ImGuiCol_ResizeGripActive] = accent;

        c[ImGuiCol_Tab] = surface(theme.tint, 0.09f, 0.02f, 1.0f);
        c[ImGuiCol_TabHovered] = ImVec4(accent.x, accent.y, accent.z, 0.34f);
        c[ImGuiCol_TabSelected] = ImVec4(accent.x, accent.y, accent.z, 0.26f);
        c[ImGuiCol_TabSelectedOverline] = accent;
        c[ImGuiCol_TabDimmed] = surface(theme.tint, 0.07f, 0.015f, 1.0f);
        c[ImGuiCol_TabDimmedSelected] = surface(theme.tint, 0.11f, 0.025f, 1.0f);
        c[ImGuiCol_TabDimmedSelectedOverline] = accent_dim;

        c[ImGuiCol_PlotLines] = accent;
        c[ImGuiCol_PlotLinesHovered] = accent;
        c[ImGuiCol_PlotHistogram] = accent;
        c[ImGuiCol_PlotHistogramHovered] = accent;

        c[ImGuiCol_TableHeaderBg] = surface(theme.tint, 0.09f, 0.02f, 1.0f);
        c[ImGuiCol_TableBorderStrong] = border;
        c[ImGuiCol_TableBorderLight] = ImVec4(border.x, border.y, border.z, border.w * 0.6f);
        c[ImGuiCol_TableRowBg] = ImVec4(0.0f, 0.0f, 0.0f, 0.0f);
        c[ImGuiCol_TableRowBgAlt] = ImVec4(1.0f, 1.0f, 1.0f, 0.025f);

        c[ImGuiCol_TextSelectedBg] = ImVec4(accent.x, accent.y, accent.z, 0.30f);
        c[ImGuiCol_NavCursor] = accent;
        c[ImGuiCol_ModalWindowDimBg] = ImVec4(0.0f, 0.0f, 0.0f, 0.55f);

        g_palette.accent = theme.accent;
        g_palette.accent_dim = theme.accent_dim;
        g_palette.text = theme.light_surfaces ? 0x1A1F27u : 0xF0F4F8u;
        g_palette.text_dim = theme.light_surfaces ? 0x616B7Au : 0x94A3B8u;
        g_palette.good = 0x4ADE80;
        g_palette.warn = 0xFBBF24;
        g_palette.bad = 0xEF4444;
        g_palette.surface = theme.light_surfaces ? 0xEDEFF4u : 0x11141Bu;
        g_palette.border = theme.light_surfaces ? 0x00000020u : 0xFFFFFF22u;
    }

    void pop_style()
    {
        // The ImGui context belongs to ReShade and is shared with its own UI
        // and every other add-on, so the style always goes back exactly as it
        // was found.
        if (!g_pushed || !g_saved_style.has_value())
            return;

        ImGui::GetStyle() = *g_saved_style;
        g_pushed = false;
    }
}
