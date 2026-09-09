#include "Telemetry.h"

#include <dxgi1_4.h>
#include <wrl/client.h>

namespace dlss5
{
    namespace
    {
        using Microsoft::WRL::ComPtr;

        // -----------------------------------------------------------------
        // NVAPI, loaded at run time
        // -----------------------------------------------------------------
        // NVAPI ships no import library we can rely on being present, and the
        // whole interface is reached through one exported query function. So
        // it is loaded by name and every entry point is optional: on an AMD or
        // Intel machine nothing here resolves and the sensor fields simply
        // stay marked invalid, which is what the UI already draws for.
        class NvApi
        {
        public:
            bool initialise()
            {
                if (m_tried)
                    return m_ready;

                m_tried = true;

                m_module = LoadLibraryW(L"nvapi64.dll");

                if (m_module == nullptr)
                    return false;

                m_query = reinterpret_cast<QueryInterface_t>(
                    reinterpret_cast<void *>(GetProcAddress(m_module, "nvapi_QueryInterface")));

                if (m_query == nullptr)
                    return false;

                // Interface ids are the published NVAPI function hashes.
                const auto initialise_fn   = reinterpret_cast<Status_t>(m_query(0x0150E828));
                m_enum_gpus                = reinterpret_cast<EnumGpus_t>(m_query(0xE5AC921F));
                m_usages                   = reinterpret_cast<Usages_t>(m_query(0x189A1FDF));
                m_thermal                  = reinterpret_cast<Thermal_t>(m_query(0xE3640A56));
                m_clocks                   = reinterpret_cast<Clocks_t>(m_query(0x60DED2ED));
                m_tach                     = reinterpret_cast<Tach_t>(m_query(0x5F608315));

                if (initialise_fn == nullptr || initialise_fn() != 0)
                    return false;

                if (m_enum_gpus == nullptr)
                    return false;

                uint32_t count = 0;

                if (m_enum_gpus(m_gpus, &count) != 0 || count == 0)
                    return false;

                m_gpu_count = count;
                m_ready = true;
                return true;
            }

            void shutdown()
            {
                if (m_module != nullptr)
                {
                    FreeLibrary(m_module);
                    m_module = nullptr;
                }

                m_ready = false;
            }

            bool ready() const { return m_ready; }

            bool load_percent(int &out) const
            {
                if (!m_ready || m_usages == nullptr)
                    return false;

                // NV_GPU_DYNAMIC_PSTATES_INFO_EX: version, flags, then 8
                // { present, percentage } pairs. Index 0 is the graphics engine.
                struct DynamicPStates
                {
                    uint32_t version;
                    uint32_t flags;
                    struct { uint32_t present; uint32_t percentage; } domain[8];
                };

                DynamicPStates states = {};
                states.version = MakeVersion(sizeof(DynamicPStates), 1);

                if (m_usages(m_gpus[0], &states) != 0)
                    return false;

                if (states.domain[0].present == 0)
                    return false;

                out = static_cast<int>(states.domain[0].percentage);
                return true;
            }

            bool temperature_celsius(int &out) const
            {
                if (!m_ready || m_thermal == nullptr)
                    return false;

                struct ThermalSettings
                {
                    uint32_t version;
                    uint32_t count;
                    struct
                    {
                        uint32_t controller;
                        int32_t  default_min;
                        int32_t  default_max;
                        int32_t  current;
                        uint32_t target;
                    } sensor[3];
                };

                ThermalSettings settings = {};
                settings.version = MakeVersion(sizeof(ThermalSettings), 2);

                if (m_thermal(m_gpus[0], 0, &settings) != 0 || settings.count == 0)
                    return false;

                out = static_cast<int>(settings.sensor[0].current);
                return true;
            }

            bool core_clock_mhz(int &out) const
            {
                if (!m_ready || m_clocks == nullptr)
                    return false;

                // NV_GPU_CLOCK_FREQUENCIES: version, flags, then 32 domains of
                // { present, frequency in kHz }. Domain 0 is the graphics clock.
                struct ClockFrequencies
                {
                    uint32_t version;
                    uint32_t flags;
                    struct { uint32_t present; uint32_t frequency_khz; } domain[32];
                };

                ClockFrequencies clocks = {};
                clocks.version = MakeVersion(sizeof(ClockFrequencies), 3);

                if (m_clocks(m_gpus[0], &clocks) != 0)
                    return false;

                if (clocks.domain[0].present == 0)
                    return false;

                out = static_cast<int>(clocks.domain[0].frequency_khz / 1000);
                return true;
            }

            bool fan_percent(int &out) const
            {
                if (!m_ready || m_tach == nullptr)
                    return false;

                uint32_t rpm = 0;

                if (m_tach(m_gpus[0], &rpm) != 0)
                    return false;

                // The tachometer reports rpm, not a percentage. Reporting it as
                // rpm would need a maximum we do not have, so this is left to
                // the caller to label; it is exposed as-is.
                out = static_cast<int>(rpm);
                return true;
            }

        private:
            using QueryInterface_t = void *(*)(uint32_t);
            using Status_t   = int (*)();
            using EnumGpus_t = int (*)(void **, uint32_t *);
            using Usages_t   = int (*)(void *, void *);
            using Thermal_t  = int (*)(void *, uint32_t, void *);
            using Clocks_t   = int (*)(void *, void *);
            using Tach_t     = int (*)(void *, uint32_t *);

            static constexpr uint32_t MakeVersion(size_t size, uint32_t version)
            {
                return static_cast<uint32_t>(size) | (version << 16);
            }

            HMODULE m_module = nullptr;
            QueryInterface_t m_query = nullptr;
            EnumGpus_t m_enum_gpus = nullptr;
            Usages_t   m_usages = nullptr;
            Thermal_t  m_thermal = nullptr;
            Clocks_t   m_clocks = nullptr;
            Tach_t     m_tach = nullptr;

            void *m_gpus[64] = {};
            uint32_t m_gpu_count = 0;
            bool m_ready = false;
            bool m_tried = false;
        };

        NvApi &nvapi()
        {
            static NvApi instance;
            return instance;
        }

        /// The adapter the game is most likely rendering on: the one with the
        /// largest dedicated video memory that is not the Microsoft software
        /// adapter. Good enough, and it never needs the game's device.
        bool find_adapter(ComPtr<IDXGIAdapter3> &out, std::string &name)
        {
            ComPtr<IDXGIFactory1> factory;

            if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory))))
                return false;

            ComPtr<IDXGIAdapter1> best;
            SIZE_T best_memory = 0;

            for (UINT i = 0;; ++i)
            {
                ComPtr<IDXGIAdapter1> adapter;

                if (factory->EnumAdapters1(i, &adapter) == DXGI_ERROR_NOT_FOUND)
                    break;

                DXGI_ADAPTER_DESC1 desc = {};

                if (FAILED(adapter->GetDesc1(&desc)))
                    continue;

                if ((desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0)
                    continue;

                if (desc.DedicatedVideoMemory > best_memory)
                {
                    best_memory = desc.DedicatedVideoMemory;
                    best = adapter;
                    name = narrow(desc.Description);
                }
            }

            if (!best)
                return false;

            return SUCCEEDED(best.As(&out));
        }

        ComPtr<IDXGIAdapter3> &adapter()
        {
            static ComPtr<IDXGIAdapter3> instance;
            return instance;
        }
    }

    void Telemetry::initialise()
    {
        if (m_initialised)
            return;

        LARGE_INTEGER frequency = {};
        QueryPerformanceFrequency(&frequency);
        m_frequency = frequency.QuadPart;

        LARGE_INTEGER now = {};
        QueryPerformanceCounter(&now);
        m_last_tick = now.QuadPart;
        m_last_slow_tick = now.QuadPart;

        std::string name;

        if (find_adapter(adapter(), name))
        {
            m_snapshot.adapter_name = name;

            DXGI_ADAPTER_DESC desc = {};
            if (adapter() && SUCCEEDED(adapter()->GetDesc(&desc)))
                m_snapshot.vram_total_bytes = desc.DedicatedVideoMemory;
        }

        if (nvapi().initialise())
            m_snapshot.sensor_source = "NVAPI";

        m_initialised = true;
    }

    void Telemetry::shutdown()
    {
        nvapi().shutdown();
        adapter().Reset();
        m_initialised = false;
    }

    void Telemetry::frame()
    {
        if (!m_initialised || m_frequency == 0)
            return;

        LARGE_INTEGER now = {};
        QueryPerformanceCounter(&now);

        const int64_t delta = now.QuadPart - m_last_tick;
        m_last_tick = now.QuadPart;

        if (delta <= 0)
            return;

        const float milliseconds = static_cast<float>(
            static_cast<double>(delta) * 1000.0 / static_cast<double>(m_frequency));

        // A frame longer than a second is a load screen or an alt-tab, not a
        // frame worth putting in the history.
        if (milliseconds > 1000.0f)
            return;

        m_frames[m_frame_next] = milliseconds;
        m_frame_next = (m_frame_next + 1) % kHistory;

        if (m_frame_count < kHistory)
            ++m_frame_count;

        // Exponential smoothing, so the big number is readable rather than a
        // blur. The raw value still drives the graph and the percentiles.
        constexpr float smoothing = 0.1f;

        m_snapshot.frame_ms = m_snapshot.frame_ms <= 0.0f
            ? milliseconds
            : m_snapshot.frame_ms + (milliseconds - m_snapshot.frame_ms) * smoothing;

        m_snapshot.fps = m_snapshot.frame_ms > 0.0f ? 1000.0f / m_snapshot.frame_ms : 0.0f;

        const int64_t slow_delta = now.QuadPart - m_last_slow_tick;
        const int64_t interval = m_frequency * static_cast<int64_t>(std::max(50, sensor_interval_ms)) / 1000;

        if (slow_delta >= interval)
        {
            m_last_slow_tick = now.QuadPart;
            refresh_percentiles();
            refresh_video_memory();
            refresh_sensors();
        }
    }

    void Telemetry::refresh_percentiles()
    {
        if (m_frame_count == 0)
            return;

        // A copy, so sorting never disturbs the ring the graph reads.
        std::vector<float> sorted;
        sorted.reserve(m_frame_count);

        for (size_t i = 0; i < m_frame_count; ++i)
            sorted.push_back(m_frames[i]);

        std::sort(sorted.begin(), sorted.end());

        m_snapshot.frame_ms_min = sorted.front();
        m_snapshot.frame_ms_max = sorted.back();

        // The 1% low is the frame rate of the slowest one percent of frames, so
        // it comes from the far end of the sorted frame times.
        const auto percentile = [&](double fraction) -> float {
            const size_t index = static_cast<size_t>(
                static_cast<double>(sorted.size() - 1) * (1.0 - fraction));
            const float ms = sorted[std::min(index, sorted.size() - 1)];
            return ms > 0.0f ? 1000.0f / ms : 0.0f;
        };

        m_snapshot.fps_low_1 = percentile(0.01);
        m_snapshot.fps_low_01 = percentile(0.001);
    }

    void Telemetry::refresh_video_memory()
    {
        if (!adapter())
            return;

        DXGI_QUERY_VIDEO_MEMORY_INFO info = {};

        if (FAILED(adapter()->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &info)))
        {
            m_snapshot.vram_valid = false;
            return;
        }

        m_snapshot.vram_valid = true;
        m_snapshot.vram_used_bytes = info.CurrentUsage;
        m_snapshot.vram_budget_bytes = info.Budget;

        if (m_snapshot.vram_total_bytes == 0)
            m_snapshot.vram_total_bytes = info.Budget;
    }

    void Telemetry::refresh_sensors()
    {
        if (!nvapi().ready())
            return;

        int value = 0;

        m_snapshot.gpu_load_valid = nvapi().load_percent(value);
        if (m_snapshot.gpu_load_valid)
            m_snapshot.gpu_load_percent = std::clamp(value, 0, 100);

        m_snapshot.gpu_temp_valid = nvapi().temperature_celsius(value);
        if (m_snapshot.gpu_temp_valid)
            m_snapshot.gpu_temp_celsius = value;

        m_snapshot.gpu_clock_valid = nvapi().core_clock_mhz(value);
        if (m_snapshot.gpu_clock_valid)
            m_snapshot.gpu_clock_mhz = value;

        m_snapshot.gpu_fan_valid = nvapi().fan_percent(value);
        if (m_snapshot.gpu_fan_valid)
            m_snapshot.gpu_fan_percent = value;
    }

    size_t Telemetry::frame_history(float *out, size_t capacity) const
    {
        if (out == nullptr || capacity == 0 || m_frame_count == 0)
            return 0;

        const size_t count = std::min(capacity, m_frame_count);

        // Walk backwards from the newest sample so the caller gets the most
        // recent `count` frames, oldest first.
        for (size_t i = 0; i < count; ++i)
        {
            const size_t offset = count - i;
            const size_t index = (m_frame_next + kHistory - offset) % kHistory;
            out[i] = m_frames[index];
        }

        return count;
    }
}
