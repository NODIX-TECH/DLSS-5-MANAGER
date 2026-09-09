// DLSS 5 Overlay - a ReShade 6.x add-on.
//
// Everything the overlay draws goes through ReShade's own ImGui context and
// renderer, which is what keeps this add-on to configuration and drawing: no
// swap chain hooks, no window subclassing, no memory patching, and nothing
// reaching into another add-on's internals. ReShade already owns the present
// path in every game this ships into, including the OptiScaler route, where
// OptiScaler loads ReShade itself through its documented [Plugins] LoadReshade
// switch.

#define ImTextureID ImU64
#include <imgui.h>
#include <reshade.hpp>

#include "Common.h"
#include "Config.h"
#include "OptiScalerBridge.h"
#include "OverlayUI.h"
#include "Telemetry.h"

extern "C" __declspec(dllexport) const char *NAME = "DLSS 5 Overlay";
extern "C" __declspec(dllexport) const char *DESCRIPTION =
    "Shift+O opens the panel. Telemetry and live ReShade, RenoDX and neural-rendering control, installed by DLSS 5 MANAGER.";
extern "C" __declspec(dllexport) const char *AUTHOR = "NODIX TECH";

namespace
{
    dlss5::Telemetry g_telemetry;
    bool g_registered = false;

    /// Fired once per presented frame, which is where the frame timer belongs:
    /// it is the only place with a stable one-frame period.
    void on_reshade_present(reshade::api::effect_runtime *)
    {
        g_telemetry.frame();
    }

    /// Called between ReShade's own ImGui NewFrame and EndFrame every frame -
    /// whether or not its UI is open - which is the only place an add-on can
    /// draw something of its own that stands apart from ReShade's window.
    void on_reshade_overlay(reshade::api::effect_runtime *runtime)
    {
        dlss5::overlay_ui().draw_standalone(runtime);
    }

    /// The tab ReShade shows inside its own overlay.
    void on_overlay(reshade::api::effect_runtime *runtime)
    {
        dlss5::overlay_ui().draw_panel(runtime);
    }

    /// The game's swap chain is up, so the configuration beside it can be read.
    /// Reading it any earlier would mean touching the disk from DllMain, under
    /// the loader lock.
    void on_init_effect_runtime(reshade::api::effect_runtime *)
    {
        static bool loaded = false;

        if (loaded)
            return;

        loaded = true;

        dlss5::config().load();
        dlss5::optiscaler().refresh();
        g_telemetry.initialise();
        g_telemetry.sensor_interval_ms = dlss5::config().sensor_interval_ms;
        dlss5::overlay_ui().initialise(&g_telemetry);
    }

    /// RenoDX and the effect list both change across a reload, so anything the
    /// UI cached about them is dropped.
    void on_reloaded_effects(reshade::api::effect_runtime *)
    {
        dlss5::overlay_ui().invalidate();
    }

    bool register_everything(HMODULE addon, HMODULE reshade_module)
    {
        if (g_registered)
            return true;

        if (!reshade::register_addon(addon, reshade_module))
            return false;

        reshade::register_event<reshade::addon_event::init_effect_runtime>(on_init_effect_runtime);
        reshade::register_event<reshade::addon_event::reshade_present>(on_reshade_present);
        reshade::register_event<reshade::addon_event::reshade_overlay>(on_reshade_overlay);
        reshade::register_event<reshade::addon_event::reshade_reloaded_effects>(on_reloaded_effects);
        reshade::register_overlay("DLSS 5 MANAGER", on_overlay);

        g_registered = true;
        reshade::log::message(reshade::log::level::info, "DLSS 5 Overlay registered. Shift+O opens the panel.");
        return true;
    }

    void unregister_everything(HMODULE addon, HMODULE reshade_module)
    {
        if (!g_registered)
            return;

        reshade::unregister_overlay("DLSS 5 MANAGER", on_overlay);
        reshade::unregister_event<reshade::addon_event::reshade_reloaded_effects>(on_reloaded_effects);
        reshade::unregister_event<reshade::addon_event::reshade_overlay>(on_reshade_overlay);
        reshade::unregister_event<reshade::addon_event::reshade_present>(on_reshade_present);
        reshade::unregister_event<reshade::addon_event::init_effect_runtime>(on_init_effect_runtime);

        g_telemetry.shutdown();
        reshade::unregister_addon(addon, reshade_module);
        g_registered = false;
    }
}

// ReShade calls these when it loads the add-on, and hands over its own module
// handle rather than leaving us to search the process for it. Registering here
// instead of in DllMain also keeps every allocation, COM call and file read out
// from under the loader lock - the DXGI factory the telemetry opens would be a
// real deadlock risk there.
extern "C" __declspec(dllexport) bool AddonInit(HMODULE addon, HMODULE reshade_module)
{
    return register_everything(addon, reshade_module);
}

extern "C" __declspec(dllexport) void AddonUninit(HMODULE addon, HMODULE reshade_module)
{
    unregister_everything(addon, reshade_module);
}

// A ReShade build that does not look for AddonInit falls back to this. It finds
// the ReShade module by scanning the process, which works whatever the DLL is
// named - ReShade64.dll on the OptiScaler route included.
BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    switch (reason)
    {
    case DLL_PROCESS_ATTACH:
        DisableThreadLibraryCalls(module);
        register_everything(module, nullptr);
        break;

    case DLL_PROCESS_DETACH:
        unregister_everything(module, nullptr);
        break;
    }

    return TRUE;
}
