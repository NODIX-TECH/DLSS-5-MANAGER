#pragma once

#include "Common.h"

struct ImGuiStyle;

namespace dlss5
{
    /// One accent identity. The surfaces stay the same dark glass across every
    /// theme - what changes is the accent and the tint mixed into the panel,
    /// which is what keeps six themes looking like one product.
    struct Theme
    {
        const char *name;
        uint32_t accent;        ///< 0xRRGGBB
        uint32_t accent_dim;
        uint32_t tint;          ///< mixed into the surfaces at low strength
        bool light_surfaces;    ///< the one theme that is not dark glass
    };

    /// Every theme the overlay ships, in the order the pickers show them.
    /// DLSS 5 MANAGER offers the same list, so the name written into
    /// dlss5-overlay.ini always resolves here.
    const std::vector<Theme> &themes();

    /// Looks a theme up by name, falling back to the first one.
    const Theme &theme_by_name(std::string_view name);

    /// Applies a theme to the ImGui style ReShade handed us, and returns the
    /// style it replaced so the caller can put it back when it is done - the
    /// overlay shares its context with ReShade's own UI and every add-on
    /// beside it, so it must not leave the style changed.
    void push_style(const Theme &theme, float opacity, float scale);
    void pop_style();

    /// Colours the widgets read, resolved from the active theme.
    struct Palette
    {
        uint32_t accent;
        uint32_t accent_dim;
        uint32_t text;
        uint32_t text_dim;
        uint32_t good;
        uint32_t warn;
        uint32_t bad;
        uint32_t surface;
        uint32_t border;
    };

    const Palette &palette();
}
