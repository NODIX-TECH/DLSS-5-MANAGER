#pragma once

#include "Common.h"

#include <array>

namespace dlss5
{
    /// What the HUD and the performance tab read every frame.
    ///
    /// Everything here is filled in by Telemetry::frame(), which is called
    /// once per presented frame. The hardware half (VRAM, GPU load, clocks) is
    /// refreshed on a slower interval because those queries cost far more than
    /// a timestamp and never change meaningfully between two frames.
    struct TelemetrySnapshot
    {
        // ---- Timing ------------------------------------------------------
        float fps = 0.0f;              ///< smoothed, what the big number shows
        float frame_ms = 0.0f;         ///< smoothed frame time
        float fps_low_1 = 0.0f;        ///< 1% low over the sample window
        float fps_low_01 = 0.0f;       ///< 0.1% low over the sample window
        float frame_ms_min = 0.0f;
        float frame_ms_max = 0.0f;

        // ---- Video memory (DXGI, always available) -----------------------
        bool     vram_valid = false;
        uint64_t vram_used_bytes = 0;
        uint64_t vram_budget_bytes = 0;
        uint64_t vram_total_bytes = 0;

        // ---- Adapter -----------------------------------------------------
        std::string adapter_name;

        // ---- GPU sensors (NVAPI when present, otherwise absent) ----------
        bool  gpu_load_valid = false;
        int   gpu_load_percent = 0;
        bool  gpu_temp_valid = false;
        int   gpu_temp_celsius = 0;
        bool  gpu_clock_valid = false;
        int   gpu_clock_mhz = 0;
        bool  gpu_fan_valid = false;
        int   gpu_fan_percent = 0;

        /// Where the sensor numbers came from, for the tab that shows it.
        const char *sensor_source = "none";
    };

    /// Frame timing plus a bounded history for the graph.
    ///
    /// The history is a fixed ring so the collector never allocates while the
    /// game is running, and the percentile lows are computed from a copy of it
    /// on the slow interval rather than every frame - sorting 512 floats at
    /// 300 fps would show up in a frame time graph measuring itself.
    class Telemetry
    {
    public:
        static constexpr size_t kHistory = 512;

        void initialise();
        void shutdown();

        /// Call once per presented frame.
        void frame();

        const TelemetrySnapshot &snapshot() const { return m_snapshot; }

        /// Frame times in milliseconds, oldest first, for the sparkline.
        /// Returns how many of `out` were filled.
        size_t frame_history(float *out, size_t capacity) const;

        /// How often the hardware sensors are re-read, in milliseconds.
        int sensor_interval_ms = 200;

    private:
        void refresh_video_memory();
        void refresh_sensors();
        void refresh_percentiles();

        TelemetrySnapshot m_snapshot;

        std::array<float, kHistory> m_frames = {};
        size_t m_frame_count = 0;
        size_t m_frame_next = 0;

        int64_t m_frequency = 0;
        int64_t m_last_tick = 0;
        int64_t m_last_slow_tick = 0;

        bool m_initialised = false;
    };
}
