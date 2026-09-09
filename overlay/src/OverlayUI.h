#pragma once

#include "Common.h"

namespace reshade::api { struct effect_runtime; }

namespace dlss5
{
    class Telemetry;

    /// Everything the overlay draws.
    ///
    /// Two entry points, because ReShade offers two places to draw and they
    /// mean different things:
    ///
    ///   draw_panel  - the registered overlay. ReShade shows it as a tab in
    ///                 its own UI, so it is only on screen when the user has
    ///                 opened that, and input is already handled.
    ///   draw_hud    - called every frame from the reshade_overlay event, so
    ///                 the read-out stays on screen while playing. It draws a
    ///                 borderless, click-through window and never takes input.
    class OverlayUI
    {
    public:
        void initialise(Telemetry *telemetry);

        /// The tab inside ReShade's own overlay. Short on purpose: it exists
        /// so the panel can be found by someone who does not know the hotkey.
        void draw_panel(reshade::api::effect_runtime *runtime);

        /// Called every frame from `reshade_overlay`. Draws the HUD, and the
        /// panel itself when it is open - as a window of its own that ReShade's
        /// dock space cannot absorb.
        void draw_standalone(reshade::api::effect_runtime *runtime);

        /// Anything cached about the effect list or RenoDX is dropped.
        void invalidate();

        bool panel_open() const { return m_open; }

    private:
        void draw_body(reshade::api::effect_runtime *runtime);
        void draw_header(reshade::api::effect_runtime *runtime);
        void draw_hud(reshade::api::effect_runtime *runtime);

        void tab_performance(reshade::api::effect_runtime *runtime);
        void tab_nr_pre_upscale(reshade::api::effect_runtime *runtime);
        void tab_renodx(reshade::api::effect_runtime *runtime);

        /// Draws one add-on's own panel inside ours. The widgets are its own,
        /// over its own state, so the two views can never disagree.
        void draw_bridge(class AddonBridge &bridge, reshade::api::effect_runtime *runtime);
        void tab_dlss_nr();
        void tab_upscaler();
        void tab_frame_gen();

        void poll_hotkey(reshade::api::effect_runtime *runtime);

        Telemetry *m_telemetry = nullptr;
        bool m_open = false;
        bool m_ready = false;

        /// True only while the panel is the reason the cursor is drawn.
        bool m_drew_cursor = false;

        /// Which RenoDX preset section the DLSS 5 controls read and write.
        int m_renodx_preset = 1;

        /// Set by a save, cleared when the user changes something else.
        std::string m_notice;
    };

    OverlayUI &overlay_ui();
}
