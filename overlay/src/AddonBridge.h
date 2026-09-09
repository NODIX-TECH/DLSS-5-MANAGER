#pragma once

#include "Common.h"

namespace reshade::api { struct effect_runtime; }

namespace dlss5
{
    /// Shows another add-on's own settings panel inside ours.
    ///
    /// **Why this rather than mirroring it.** An earlier version read every
    /// widget the add-on drew and rebuilt it here, which meant translating each
    /// control and carrying a second copy of its state. It worked for one
    /// add-on and not the other, and any control shape it did not recognise was
    /// simply lost. This does the opposite: it calls the add-on's own overlay
    /// callback inside one of our windows, with ReShade's real ImGui interface
    /// untouched, so the add-on draws its actual panel and writes its actual
    /// variables.
    ///
    /// The consequences are all good ones. There is **one** copy of the state,
    /// so the panel here and the add-on's own tab can never disagree - both are
    /// the same widgets over the same memory. Nothing is translated, so nothing
    /// can be missed. And an add-on update changes its panel here at the same
    /// moment it changes it in ReShade, because it *is* its panel.
    ///
    /// Nothing is written into the add-on: the only thing taken from it is the
    /// address of the function ReShade already calls every frame.
    class AddonBridge
    {
    public:
        struct Target
        {
            const wchar_t *module;
            /// What the same add-on was called before it was renamed. A game
            /// modded by an older build of the manager still has the old file
            /// beside it, and the panel has to keep working there. May be null.
            const wchar_t *module_legacy;
            /// The overlay title the add-on registered, which is what anchors
            /// the search for its callback.
            const char *overlay_title;
            const char *display;
        };

        explicit AddonBridge(const Target &target) : m_target(target) {}

        /// True when the add-on's panel can be drawn here.
        bool available();

        /// Draws it. Safe to call only between Begin and End of a window.
        void draw(reshade::api::effect_runtime *runtime);

        const std::string &status() const { return m_status; }
        const char *display() const { return m_target.display; }

        /// Forget what was found, so a reload re-establishes it.
        void reset();

    private:
        bool locate();

        Target m_target;

        void *m_module = nullptr;
        void *m_callback = nullptr;
        bool m_located = false;

        /// Faults survived. Two and the panel is left alone for good.
        int m_failures = 0;

        std::string m_status;
    };

    AddonBridge &renodx_bridge();
    AddonBridge &nr_bridge();
}
