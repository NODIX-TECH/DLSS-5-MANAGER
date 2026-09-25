// FLOW MFG - LIVE FLOW's own frame multiplication, x2 to x6.
//
// DLSS 4.5 FG needs NVIDIA's frame-generation runtime and a card it accepts.
// This needs neither: the frames in between are made here, from the two real
// frames either side and the motion field LIVE FLOW already computes for the
// network - the same idea as Lossless Scaling, on any GPU.
//
// flow_shader.h uses current->previous and (when NVOFA is available)
// previous->current motion. Endpoint colour and forward/backward consistency
// reject unreliable paths; validated neighbouring vectors fill boundaries.
// Colour is warped at full output resolution; flow estimation is capped at
// 720p. No neural inference is run on generated frames.
//
// Small fast objects. The estimate loses an object that moves further between
// two frames than it is wide - a ball at 15 FPS it had right in one pair of
// eight - and a frame in between cannot place what has no motion: the ball
// jumped from real frame to real frame while the pitch moved smoothly. So
// once per pair, first, four passes find it (flow_shader.h): where the
// estimate does not carry the pixels, one seed per 8x8-texel tile is looked
// up in the previous frame by a block search, and each motion found is kept
// wherever it carries the pixels around it better than the estimate did.
//
// The HUD. Then, before the pair's first frame in between,
// two passes run: HudMain keeps a per-pixel verdict on the screen-locked layer
// (a pixel that stays put while the motion around it says it should have
// moved, and would not have matched if it had), and FixMain rebuilds the
// motion under and beside that layer from the scene's own motion around it.
// Frames in between then draw the HUD where it stands, follow the scene
// behind it from whichever side it can be seen, and keep a HUD's soft edge
// still while the scene under it moves. The verdict lives across pairs and
// resets only where the history does.
//
// Why it presents on a queue of its own. The network takes most of every
// frame on the GPU. On the queue the network uses, a frame in between
// would wait behind the next network pass and then arrive together with the
// real frame - the stutter frame generation is supposed to remove. So while
// FLOW MFG is on, the output window's swap chain is created on a second,
// high-priority queue, and everything that touches the window - the frames
// in between and the real ones - is drawn and presented from there by the
// presenter thread below. The main path only hands frames over.
//
// Turning it on or off therefore rebuilds the output window once (a swap
// chain cannot move between queues). HDR output keeps the ordinary path.

#include "flow_shader.h"

struct FlowSlot
{
    ID3D12Resource *prev = nullptr, *cur = nullptr, *mv = nullptr, *reverse = nullptr;   // NON_PIXEL_SHADER_RESOURCE at rest
    int state = 0;            // 0 free, 1 writing, 2 ready, 3 presenting - under the mutex
    UINT64 sequence = 0;
    unsigned count = 0;       // frames in between, before the real one
    bool interpolate = false, both = false;
    double interval = 0.016;
    double source_time = 0, ready_time = 0;
};

static struct FlowState
{
    ID3D12CommandQueue   *queue = nullptr;
    ID3D12RootSignature  *rs = nullptr;
    ID3D12PipelineState  *pso = nullptr;     // CSMain: a frame in between
    ID3D12PipelineState  *pso_hud = nullptr; // HudMain: once per pair
    ID3D12PipelineState  *pso_fix = nullptr; // FixMain: once per pair
    ID3D12PipelineState  *pso_resid = nullptr, *pso_seed = nullptr,   // the small-object tracker,
                         *pso_search = nullptr, *pso_track = nullptr; // once per pair
    ID3D12PipelineState  *pso_block = nullptr, *pso_texel = nullptr;  // the block search, once per pair
    ID3D12DescriptorHeap *heap = nullptr;   // one descriptor set per slot
    ID3D12Resource       *last = nullptr;   // the previous real frame, COPY_SOURCE at rest
    ID3D12Resource       *scratch = nullptr; // the frame being drawn, UNORDERED_ACCESS at rest
    ID3D12Resource       *still = nullptr;   // per pixel: pairs stood still, and the HUD verdict
    ID3D12Resource       *fix_mv = nullptr, *fix_reverse = nullptr; // FixMain's motion, NON_PIXEL_SHADER_RESOURCE at rest
    ID3D12Resource       *trk_mv = nullptr, *trk_reverse = nullptr; // TrackMain's motion, NON_PIXEL_SHADER_RESOURCE at rest
    ID3D12Resource       *resid = nullptr;   // how badly the estimate carries each texel, UNORDERED_ACCESS
    ID3D12Resource       *tiles = nullptr;   // seed and search result per tile, then the block search's
                                             // fields, UNORDERED_ACCESS
    UINT                  tiles_x = 0, tiles_y = 0, blocks_x = 0, blocks_y = 0;
    ID3D12Resource       *readback = nullptr; // only while a recording is running
    UINT                  rb_pitch = 0;
    FlowSlot slots[3];
    UINT w = 0, hgt = 0, mw = 0, mh = 0;
    std::mutex mutex;
    std::condition_variable wake;
    std::thread thread;
    std::atomic<bool> stop{false}, failed{false};
    UINT64 sequence = 0;
    std::chrono::steady_clock::time_point last_time;
    double last_source_time = 0;
    bool history = false;
    bool pipeline_failed = false;
    ~FlowState() { stop = true; wake.notify_all(); if (thread.joinable()) thread.join(); }
} g_flow;

// Per slot, in the shader's register order: previous, current, the repaired
// backward/forward motion, the slot's estimated backward/forward motion, the
// tracked backward/forward motion; then the output, the stillness/HUD state,
// the repaired and the tracked motion again (written), the residual and the
// tile records.
static const UINT kFlowDescriptors = 16;

// Motion texels per tile side, per block side, and how many rounds the block
// search runs - kTile, kBlock and kBlockRounds in flow_shader.h.
static const UINT kFlowTile = 8;
static const UINT kFlowBlock = 4;
static const UINT kFlowBlockRounds = 4;

static bool FlowWanted()
{
    return CurrentFlowCount() > 0 && !g_flow.failed && !g_hdr_capture && !HdrEnabled();
}

static bool FlowActive()
{
    return g_present_on_flow_queue && FlowWanted();
}

static ID3D12CommandQueue *FlowSwapQueue()
{
    if (!FlowWanted()) return h.queue;
    if (g_flow.queue == nullptr)
    {
        D3D12_COMMAND_QUEUE_DESC qd = {};
        qd.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
        qd.Priority = D3D12_COMMAND_QUEUE_PRIORITY_HIGH;
        if (FAILED(h.dev->CreateCommandQueue(&qd, __uuidof(ID3D12CommandQueue),
                                             reinterpret_cast<void **>(&g_flow.queue))))
        {
            Log("[flow] could not create the presenting queue - FLOW MFG stays off");
            g_flow.failed = true;
            return h.queue;
        }
    }
    return g_flow.queue;
}

static void CloseFlow()
{
    g_flow.stop = true;
    g_flow.wake.notify_all();
    if (g_flow.thread.joinable()) g_flow.thread.join();
    if (g_present_swap != nullptr && g_flow_waitable != nullptr)
    {
        IDXGISwapChain2 *sc2 = nullptr;
        if (SUCCEEDED(g_present_swap->QueryInterface(__uuidof(IDXGISwapChain2), reinterpret_cast<void **>(&sc2))) && sc2)
        {
            sc2->SetMaximumFrameLatency(3);
            sc2->Release();
        }
    }
    for (auto &slot : g_flow.slots)
    {
        for (ID3D12Resource **r : { &slot.prev, &slot.cur, &slot.mv, &slot.reverse })
            if (*r != nullptr) { (*r)->Release(); *r = nullptr; }
        slot.state = 0;
    }
    if (g_flow.last != nullptr) { g_flow.last->Release(); g_flow.last = nullptr; }
    if (g_flow.scratch != nullptr) { g_flow.scratch->Release(); g_flow.scratch = nullptr; }
    if (g_flow.still != nullptr) { g_flow.still->Release(); g_flow.still = nullptr; }
    if (g_flow.fix_mv != nullptr) { g_flow.fix_mv->Release(); g_flow.fix_mv = nullptr; }
    if (g_flow.fix_reverse != nullptr) { g_flow.fix_reverse->Release(); g_flow.fix_reverse = nullptr; }
    for (ID3D12Resource **r : { &g_flow.trk_mv, &g_flow.trk_reverse, &g_flow.resid, &g_flow.tiles })
        if (*r != nullptr) { (*r)->Release(); *r = nullptr; }
    g_flow.tiles_x = g_flow.tiles_y = g_flow.blocks_x = g_flow.blocks_y = 0;
    if (g_flow.readback != nullptr) { g_flow.readback->Release(); g_flow.readback = nullptr; }
    g_flow.rb_pitch = 0;
    g_flow.w = g_flow.hgt = g_flow.mw = g_flow.mh = 0;
    g_flow.history = false;
    g_flow.last_source_time = 0;
}

static bool EnsureFlowPipeline()
{
    if (g_flow.pso != nullptr && g_flow.heap != nullptr) return true;
    if (g_flow.pipeline_failed) return false;
    g_flow.pipeline_failed = true;
    HMODULE compiler = LoadLibraryW(L"d3dcompiler_47.dll");
    auto compile = compiler ? reinterpret_cast<PFN_D3DCompile_>(GetProcAddress(compiler, "D3DCompile")) : nullptr;
    HMODULE d3d12 = GetModuleHandleW(L"d3d12.dll");
    auto serialize = d3d12 ? reinterpret_cast<PFN_D3D12SerializeRootSignature_>(
                                 GetProcAddress(d3d12, "D3D12SerializeRootSignature")) : nullptr;
    if (compile == nullptr || serialize == nullptr) { Log("[flow] compiler unavailable"); return false; }
    // One source, nine kernels: the frame in between, and the passes run
    // once per pair - the small-object tracker, the block search and the HUD.
    const char *entries[9] = { "CSMain", "HudMain", "FixMain", "ResidMain", "SeedMain", "SearchMain", "TrackMain",
                               "BlockMain", "TexelMain" };
    ID3DBlob *codes[9] = {};
    ID3DBlob *logs[9] = {};
    HRESULT results[9] = {};
    // Side by side: one after another the kernels took three seconds, all of
    // them inside the frame that turned FLOW MFG on - close to the host's
    // five-second watchdog on a slower processor. D3DCompile may run on
    // several threads at once. A thread that cannot be started leaves its
    // kernel to this one.
    //
    // And once only: the code is kept in %TEMP%\DLSS5Manager-flow, named by a
    // hash of this source, so every later start - and every FLOW MFG switched
    // on - loads it instead of stalling on the compiler again (~1.2 s each
    // time). A changed shader has another name; a bad or partial file fails
    // D3D12 and is compiled afresh.
    typedef HRESULT(WINAPI *PFN_D3DCreateBlob_)(SIZE_T, ID3DBlob **);
    auto make_blob = reinterpret_cast<PFN_D3DCreateBlob_>(GetProcAddress(compiler, "D3DCreateBlob"));
    UINT64 source_hash = 1469598103934665603ull;
    for (size_t k = 0; k + 1 < sizeof(kFlowHlsl); ++k) { source_hash ^= UINT8(kFlowHlsl[k]); source_hash *= 1099511628211ull; }
    wchar_t cache_dir[MAX_PATH] = {};
    const DWORD temp_len = GetTempPathW(MAX_PATH, cache_dir);
    const bool cache = make_blob != nullptr && temp_len > 0 && temp_len < MAX_PATH - 64;
    if (cache) { wcscat_s(cache_dir, L"DLSS5Manager-flow"); CreateDirectoryW(cache_dir, nullptr); }
    auto cache_path = [&](int i) {
        wchar_t path[MAX_PATH];
        swprintf_s(path, L"%s\\%016llX-%S.cso", cache_dir, source_hash, entries[i]);
        return std::wstring(path);
    };
    bool loaded[9] = {};
    auto build = [&](int i) {
        if (cache)
        {
            HANDLE f = CreateFileW(cache_path(i).c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
            if (f != INVALID_HANDLE_VALUE)
            {
                LARGE_INTEGER size = {};
                DWORD got = 0;
                if (GetFileSizeEx(f, &size) && size.QuadPart > 0 && size.QuadPart < (16 << 20) &&
                    SUCCEEDED(make_blob(SIZE_T(size.QuadPart), &codes[i])) &&
                    ReadFile(f, codes[i]->GetBufferPointer(), DWORD(size.QuadPart), &got, nullptr) && got == DWORD(size.QuadPart))
                { CloseHandle(f); results[i] = S_OK; loaded[i] = true; return; }
                CloseHandle(f);
                if (codes[i]) { codes[i]->Release(); codes[i] = nullptr; }
            }
        }
        results[i] = compile(kFlowHlsl, sizeof(kFlowHlsl) - 1, "flow.hlsl", nullptr, nullptr,
                             entries[i], "cs_5_0", 0, 0, &codes[i], &logs[i]);
        if (cache && SUCCEEDED(results[i]) && codes[i])
        {
            // Written under a temporary name and moved in whole: another
            // worker starting at the same moment never reads half a file.
            const std::wstring final_path = cache_path(i), part = final_path + L".part";
            HANDLE f = CreateFileW(part.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, 0, nullptr);
            if (f != INVALID_HANDLE_VALUE)
            {
                DWORD put = 0;
                const BOOL ok = WriteFile(f, codes[i]->GetBufferPointer(), DWORD(codes[i]->GetBufferSize()), &put, nullptr);
                CloseHandle(f);
                if (!ok || !MoveFileExW(part.c_str(), final_path.c_str(), MOVEFILE_REPLACE_EXISTING)) DeleteFileW(part.c_str());
            }
        }
    };
    const auto compile_start = std::chrono::steady_clock::now();
    {
        std::vector<std::thread> builders;
        bool started[9] = {};
        for (int i = 1; i < 9; ++i)
        {
            try { builders.emplace_back(build, i); started[i] = true; }
            catch (...) {}
        }
        build(0);
        for (auto &b : builders) b.join();
        for (int i = 1; i < 9; ++i) if (!started[i]) build(i);
    }
    HRESULT hr = S_OK;
    for (int i = 0; i < 9; ++i)
    {
        if (FAILED(results[i]) || codes[i] == nullptr)
        {
            Log("[flow] shader %s failed 0x%08X: %s", entries[i], results[i],
                logs[i] ? static_cast<const char *>(logs[i]->GetBufferPointer()) : "(no log)");
            for (ID3DBlob *l : logs) if (l) l->Release();
            for (ID3DBlob *c : codes) if (c) c->Release();
            return false;
        }
    }
    for (ID3DBlob *&l : logs) if (l) { l->Release(); l = nullptr; }
    int from_cache = 0;
    for (bool l : loaded) from_cache += l ? 1 : 0;
    Log("[flow] %d kernels ready in %.0f ms (%d from the cache)", int(_countof(entries)),
        std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - compile_start).count(), from_cache);
    ID3DBlob *errors = nullptr;
    D3D12_DESCRIPTOR_RANGE ranges[2] = {};
    ranges[0].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
    ranges[0].NumDescriptors = 8;               // colour x2, repaired, estimated and tracked motion x2
    ranges[1].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_UAV;
    ranges[1].NumDescriptors = 8;               // frame, HUD state, repaired x2, tracked x2, residual, tiles
    ranges[1].OffsetInDescriptorsFromTableStart = 8;
    D3D12_ROOT_PARAMETER params[2] = {};
    params[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
    params[0].Constants.Num32BitValues = 7;     // cbuffer FC: size, t, accumulate, both, rings, block round
    params[0].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
    params[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
    params[1].DescriptorTable.NumDescriptorRanges = 2;
    params[1].DescriptorTable.pDescriptorRanges = ranges;
    params[1].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
    D3D12_STATIC_SAMPLER_DESC samp = {};
    samp.Filter = D3D12_FILTER_MIN_MAG_MIP_LINEAR;
    samp.AddressU = samp.AddressV = samp.AddressW = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
    samp.MaxLOD = D3D12_FLOAT32_MAX;
    samp.ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
    D3D12_ROOT_SIGNATURE_DESC rsd = {};
    rsd.NumParameters = _countof(params);
    rsd.pParameters = params;
    rsd.NumStaticSamplers = 1;
    rsd.pStaticSamplers = &samp;
    ID3DBlob *rs_blob = nullptr;
    hr = serialize(&rsd, D3D_ROOT_SIGNATURE_VERSION_1, &rs_blob, &errors);
    if (errors) errors->Release();
    auto release_codes = [&]() { for (ID3DBlob *&c : codes) if (c) { c->Release(); c = nullptr; } };
    if (FAILED(hr) || rs_blob == nullptr) { Log("[flow] root signature failed 0x%08X", hr); release_codes(); return false; }
    if (g_flow.rs == nullptr)
        hr = h.dev->CreateRootSignature(0, rs_blob->GetBufferPointer(), rs_blob->GetBufferSize(),
                                        __uuidof(ID3D12RootSignature), reinterpret_cast<void **>(&g_flow.rs));
    rs_blob->Release();
    if (FAILED(hr)) { release_codes(); return false; }
    ID3D12PipelineState *made[9] = {};
    for (int i = 0; i < 9 && SUCCEEDED(hr); ++i)
    {
        D3D12_COMPUTE_PIPELINE_STATE_DESC pd = {};
        pd.pRootSignature = g_flow.rs;
        pd.CS.pShaderBytecode = codes[i]->GetBufferPointer();
        pd.CS.BytecodeLength = codes[i]->GetBufferSize();
        hr = h.dev->CreateComputePipelineState(&pd, __uuidof(ID3D12PipelineState), reinterpret_cast<void **>(&made[i]));
    }
    release_codes();
    if (FAILED(hr))
    {
        Log("[flow] pipeline failed 0x%08X", hr);
        for (ID3D12PipelineState *p : made) if (p) p->Release();
        return false;
    }
    g_flow.pso = made[0]; g_flow.pso_hud = made[1]; g_flow.pso_fix = made[2];
    g_flow.pso_resid = made[3]; g_flow.pso_seed = made[4]; g_flow.pso_search = made[5]; g_flow.pso_track = made[6];
    g_flow.pso_block = made[7]; g_flow.pso_texel = made[8];
    D3D12_DESCRIPTOR_HEAP_DESC hd = {};
    hd.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
    hd.NumDescriptors = kFlowDescriptors * _countof(g_flow.slots);
    hd.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
    if (FAILED(h.dev->CreateDescriptorHeap(&hd, __uuidof(ID3D12DescriptorHeap), reinterpret_cast<void **>(&g_flow.heap))))
        return false;
    g_flow.pipeline_failed = false;
    return true;
}

// The buffer a recording is read back through. Built the first time a
// recording asks for pixels and kept until FLOW MFG stops, so nothing is
// spent on it in the ordinary case.
static bool EnsureFlowReadback()
{
    if (g_flow.readback != nullptr) return true;
    if (g_flow.w == 0 || g_flow.hgt == 0) return false;
    const UINT pitch = (g_flow.w * 4 + D3D12_TEXTURE_DATA_PITCH_ALIGNMENT - 1) &
                       ~(D3D12_TEXTURE_DATA_PITCH_ALIGNMENT - 1);
    D3D12_HEAP_PROPERTIES heap = {};
    heap.Type = D3D12_HEAP_TYPE_READBACK;
    D3D12_RESOURCE_DESC rd = {};
    rd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
    rd.Width = static_cast<UINT64>(pitch) * g_flow.hgt;
    rd.Height = 1;
    rd.DepthOrArraySize = 1;
    rd.MipLevels = 1;
    rd.Format = DXGI_FORMAT_UNKNOWN;
    rd.SampleDesc.Count = 1;
    rd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    if (FAILED(h.dev->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &rd,
                                              D3D12_RESOURCE_STATE_COPY_DEST, nullptr,
                                              __uuidof(ID3D12Resource),
                                              reinterpret_cast<void **>(&g_flow.readback))))
    { Log("[flow] read back buffer failed - the recording keeps the real frames"); return false; }
    g_flow.rb_pitch = pitch;
    Log("[flow] recording reads back %ux%u from the presenter", g_flow.w, g_flow.hgt);
    return true;
}

// The presenter owns the window while FLOW MFG is on: it draws every frame -
// in between and real - into its scratch texture, copies it to the back
// buffer and presents, all on its own queue, paced by the compositor.
static void FlowPresenter()
{
    winrt::com_ptr<ID3D12CommandAllocator> alloc;
    winrt::com_ptr<ID3D12GraphicsCommandList> list;
    winrt::com_ptr<ID3D12Fence> fence;
    if (FAILED(h.dev->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(alloc.put()))) ||
        FAILED(h.dev->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, alloc.get(), nullptr, IID_PPV_ARGS(list.put()))) ||
        FAILED(h.dev->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(fence.put()))))
    { g_flow.failed = true; return; }
    list->Close();
    HANDLE event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (!event) { g_flow.failed = true; return; }
    const UINT stride = h.dev->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
    UINT64 value = 0, previous = 0, shown = 0, generated = 0, dropped = 0;
    auto report = std::chrono::steady_clock::now();
    bool buffer_ready = false;
    auto wait_buffer = [&]() {
        if (buffer_ready || g_flow_waitable == nullptr) return true;
        buffer_ready = WaitForSingleObject(g_flow_waitable, 2000) == WAIT_OBJECT_0;
        if (!buffer_ready) Log("[flow] frame latency wait timed out");
        return buffer_ready;
    };
    double refresh = 60.0;
    MONITORINFOEXW monitor = {}; monitor.cbSize = sizeof(monitor);
    DEVMODEW mode = {}; mode.dmSize = sizeof(mode);
    if (GetMonitorInfoW(MonitorFromWindow(g_present_hwnd, MONITOR_DEFAULTTONEAREST), &monitor) &&
        EnumDisplaySettingsW(monitor.szDevice, ENUM_CURRENT_SETTINGS, &mode) && mode.dmDisplayFrequency > 1)
        refresh = mode.dmDisplayFrequency;
    // Present(0) lets DWM pick the newest frame instead of keeping a late
    // frame for another whole refresh. Keep one frame in flight.
    Log("[flow] display pacing %.1f Hz, clocked mailbox, latency 1", refresh);

    auto seconds = []() {
        LARGE_INTEGER q, f; QueryPerformanceCounter(&q); QueryPerformanceFrequency(&f);
        return double(q.QuadPart) / double(f.QuadPart);
    };
    // The searches (tracker, block search) only while there is room for them.
    // Where the source is slow because the card is full - the game and the
    // network sharing it - their time came out of the network, which slowed,
    // which made the pairs longer and the searches more likely: 68 FPS down to
    // 13 in ten seconds on an RTX 3060, and a settings change then stalled the
    // worker past the host's watchdog. A pair whose per-pair work took more
    // than a fifth of its interval turns them off; they are tried again 3 s on.
    bool search_ok = true, searched = false;
    double search_retry = 0;
    HANDLE timer = CreateWaitableTimerExW(nullptr, nullptr, 0x00000002, TIMER_ALL_ACCESS);
    if (!timer) timer = CreateWaitableTimerW(nullptr, FALSE, nullptr);
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_ABOVE_NORMAL);
    auto wait_until = [&](double deadline) {
        while (!g_flow.stop) {
            const double remaining = deadline - seconds();
            if (remaining <= 0) break;
            // A high-resolution timer avoids the 15.6ms condition-variable
            // timeout quantum. Spin only the final 150us, never a whole frame.
            if (timer && remaining > .0003) {
                LARGE_INTEGER due; due.QuadPart = -LONGLONG(std::min(.005, remaining - .00015) * 1e7);
                if (SetWaitableTimer(timer, &due, 0, nullptr, nullptr, FALSE))
                    WaitForSingleObject(timer, 10);
                else SwitchToThread();
            } else SwitchToThread();
        }
    };
    double last_present = 0, delay = 0, render_budget = .0015, cadence_interval = 0;
    double intervals[5] = {}; unsigned interval_count = 0, interval_index = 0;
    double ages[32] = {}; unsigned age_count = 0, age_index = 0;
    char trace_path[MAX_PATH] = {};
    FILE *trace = nullptr;
    if (GetEnvironmentVariableA("NS_FLOW_TRACE", trace_path, sizeof(trace_path)))
        fopen_s(&trace, trace_path, "w");
    if (trace) fprintf(trace, "present,sequence,alpha,source,ready,interval,draw_ms\n");

    auto show = [&](unsigned slot_index, float t, float accumulate, double deadline) {
        const double draw_start = seconds();
        winrt::com_ptr<ID3D12Resource> bb;
        if (FAILED(g_present_swap->GetBuffer(g_present_swap->GetCurrentBackBufferIndex(), IID_PPV_ARGS(bb.put()))) ||
            FAILED(alloc->Reset()) || FAILED(list->Reset(alloc.get(), nullptr))) return false;
        ID3D12DescriptorHeap *heaps[] = { g_flow.heap };
        list->SetDescriptorHeaps(1, heaps);
        list->SetComputeRootSignature(g_flow.rs);
        const auto &pair = g_flow.slots[slot_index];
        // The search for a surface crossing a pixel reaches as far as the
        // pair is long: at 7.5 FPS a surface travels eight 60-Hz ticks.
        const UINT32 rings = std::clamp(UINT32(std::ceil(pair.interval * 60.0)), 2u, 8u);
        UINT32 c[7] = { g_flow.w, g_flow.hgt, 0, 0, UINT32(pair.both), rings, 0 };
        memcpy(&c[2], &t, sizeof(float));
        memcpy(&c[3], &accumulate, sizeof(float));
        list->SetComputeRoot32BitConstants(0, 7, c, 0);
        D3D12_GPU_DESCRIPTOR_HANDLE gpu = g_flow.heap->GetGPUDescriptorHandleForHeapStart();
        gpu.ptr += static_cast<UINT64>(slot_index) * kFlowDescriptors * stride;
        list->SetComputeRootDescriptorTable(1, gpu);
        list->SetPipelineState(g_flow.pso);
        list->Dispatch((g_flow.w + 7) / 8, (g_flow.hgt + 7) / 8, 1);
        D3D12_RESOURCE_BARRIER pre[] = {
            Transition(g_flow.scratch, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_COPY_SOURCE),
            Transition(bb.get(), D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_COPY_DEST),
        };
        list->ResourceBarrier(_countof(pre), pre);
        list->CopyResource(bb.get(), g_flow.scratch);
        // Out to whoever is recording, from here rather than from the
        // handover: this is the only place every frame passes through, so a
        // Spout recorder (OBS) gets the frames in between as well - the
        // recording runs at the rate FLOW MFG shows, not the network's.
        SpoutBridgeCopy(list.get(), g_flow.scratch, g_flow.w, g_flow.hgt);
        // And into the buffer the client's own recorder reads, while there is
        // one. Same picture, same moment - the recording then holds the
        // frames in between instead of only the network's.
        const bool exporting = ExportingPresented() && EnsureFlowReadback();
        if (exporting)
        {
            D3D12_TEXTURE_COPY_LOCATION dst = {};
            dst.pResource = g_flow.readback;
            dst.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
            dst.PlacedFootprint.Footprint.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
            dst.PlacedFootprint.Footprint.Width = g_flow.w;
            dst.PlacedFootprint.Footprint.Height = g_flow.hgt;
            dst.PlacedFootprint.Footprint.Depth = 1;
            dst.PlacedFootprint.Footprint.RowPitch = g_flow.rb_pitch;
            D3D12_TEXTURE_COPY_LOCATION src = {};
            src.pResource = g_flow.scratch;
            src.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
            list->CopyTextureRegion(&dst, 0, 0, 0, &src, nullptr);
        }
        D3D12_RESOURCE_BARRIER post[] = {
            Transition(g_flow.scratch, D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATE_UNORDERED_ACCESS),
            Transition(bb.get(), D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_PRESENT),
        };
        list->ResourceBarrier(_countof(post), post);
        if (FAILED(list->Close())) return false;
        ID3D12CommandList *lists[] = { list.get() };
        g_flow.queue->ExecuteCommandLists(1, lists);
        if (FAILED(g_flow.queue->Signal(fence.get(), ++value)) ||
            FAILED(fence->SetEventOnCompletion(value, event)) ||
            WaitForSingleObject(event, 2000) != WAIT_OBJECT_0 ||
            fence->GetCompletedValue() < value) return false;
        const double draw_seconds = seconds() - draw_start;
        render_budget += .1 * (std::clamp(draw_seconds, .0003, .006) - render_budget);
        // Finish GPU work BEFORE the presentation deadline. Waiting before
        // dispatch added a variable GPU delay to every interpolation sample.
        wait_until(deadline);
        const HRESULT pr = g_present_swap->Present(0, 0);
        last_present = seconds();
        if (trace) {
            const auto &s = g_flow.slots[slot_index];
            const double stamp = seconds();
            fprintf(trace, "%.7f,%llu,%.5f,%.7f,%.7f,%.7f,%.4f\n",
                stamp, s.sequence, t, s.source_time, s.ready_time, s.interval, (stamp-draw_start)*1000);
        }
        buffer_ready = false;
        if (pr == DXGI_STATUS_MODE_CHANGED)
        {
            Log("[flow] present reports a mode change - skipping a frame");
            return true;
        }
        if (pr == DXGI_STATUS_OCCLUDED) return true;
        if (FAILED(pr)) return false;
        SpoutBridgeSend();
        if (exporting)
        {
            // After the present, not before it: the pixels are already on
            // their way to the screen and the copy out costs the recording,
            // not the picture.
            void *mapped = nullptr;
            D3D12_RANGE whole = { 0, static_cast<SIZE_T>(g_flow.rb_pitch) * g_flow.hgt };
            if (SUCCEEDED(g_flow.readback->Map(0, &whole, &mapped)) && mapped != nullptr)
            {
                PublishPresented(static_cast<const BYTE *>(mapped), g_flow.rb_pitch,
                                 g_flow.w, g_flow.hgt);
                D3D12_RANGE none = { 0, 0 };
                g_flow.readback->Unmap(0, &none);
            }
        }
        RevealOnFirstPresent();
        ++shown;
        if (t < 1.0f) ++generated;
        return true;
    };

    // Once per pair, as soon as it is picked and before any of its frames is
    // timed: the motion refined (the small-object tracker, then the block
    // search), the HUD verdict (HudMain), then the motion rebuilt under and
    // beside the HUD (FixMain), which every frame of the pair reuses. Done
    // inside the first frame's draw it made that frame late under load - the
    // network shares the GPU - and a late frame costs the next slot.
    // accumulate: 1 carries the history on, -1 starts it again.
    auto prepare = [&](unsigned slot_index, float accumulate) {
        if (FAILED(alloc->Reset()) || FAILED(list->Reset(alloc.get(), nullptr))) return false;
        ID3D12DescriptorHeap *heaps[] = { g_flow.heap };
        list->SetDescriptorHeaps(1, heaps);
        list->SetComputeRootSignature(g_flow.rs);
        const float t = 0.0f;
        // The block search reaches as far as the pair is long.
        const UINT32 rings = std::clamp(UINT32(std::ceil(g_flow.slots[slot_index].interval * 60.0)), 2u, 8u);
        UINT32 c[7] = { g_flow.w, g_flow.hgt, 0, 0, UINT32(g_flow.slots[slot_index].both), rings, 0 };
        memcpy(&c[2], &t, sizeof(float));
        memcpy(&c[3], &accumulate, sizeof(float));
        list->SetComputeRoot32BitConstants(0, 7, c, 0);
        D3D12_GPU_DESCRIPTOR_HANDLE gpu = g_flow.heap->GetGPUDescriptorHandleForHeapStart();
        gpu.ptr += static_cast<UINT64>(slot_index) * kFlowDescriptors * stride;
        list->SetComputeRootDescriptorTable(1, gpu);
        auto uav_barrier = [](ID3D12Resource *r) {
            D3D12_RESOURCE_BARRIER b = {};
            b.Type = D3D12_RESOURCE_BARRIER_TYPE_UAV;
            b.UAV.pResource = r;
            return b;
        };
        D3D12_RESOURCE_BARRIER still_barrier = uav_barrier(g_flow.still);
        D3D12_RESOURCE_BARRIER open[] = {
            still_barrier,
            Transition(g_flow.fix_mv, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_UNORDERED_ACCESS),
            Transition(g_flow.fix_reverse, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_UNORDERED_ACCESS),
            Transition(g_flow.trk_mv, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_UNORDERED_ACCESS),
            Transition(g_flow.trk_reverse, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_UNORDERED_ACCESS),
        };
        list->ResourceBarrier(_countof(open), open);
        // The small-object tracker and the block search are for a slow source:
        // there a ball moves further between two frames than it is wide and
        // the estimate loses it. From 40 FPS up the estimate keeps up, and the
        // searches (some 7 ms a pair at 1440p on an RTX 3060) would come out of
        // the game's own frame time - TrackMain then hands the estimate on.
        const bool search = g_flow.slots[slot_index].interval >= 1.0 / 40.0 && search_ok;
        searched = search;
        if (!search)
        {
            list->SetComputeRoot32BitConstant(0, 0xFFFFu, 6);   // kSkipSearch
            list->SetPipelineState(g_flow.pso_track);
            list->Dispatch((g_flow.mw + 7) / 8, (g_flow.mh + 7) / 8, 1);
            list->SetComputeRoot32BitConstant(0, 0, 6);
        }
        else
        {
        // The small-object tracker: residual, a seed per tile, the search,
        // then every texel takes what carries it best.
        list->SetPipelineState(g_flow.pso_resid);
        list->Dispatch((g_flow.mw + 7) / 8, (g_flow.mh + 7) / 8, 1);
        D3D12_RESOURCE_BARRIER resid_barrier = uav_barrier(g_flow.resid);
        list->ResourceBarrier(1, &resid_barrier);
        list->SetPipelineState(g_flow.pso_seed);
        list->Dispatch(g_flow.tiles_x, g_flow.tiles_y, 1);
        D3D12_RESOURCE_BARRIER tiles_barrier = uav_barrier(g_flow.tiles);
        list->ResourceBarrier(1, &tiles_barrier);
        list->SetPipelineState(g_flow.pso_search);
        list->Dispatch(g_flow.tiles_x, g_flow.tiles_y, 1);
        list->ResourceBarrier(1, &tiles_barrier);
        list->SetPipelineState(g_flow.pso_track);
        list->Dispatch((g_flow.mw + 7) / 8, (g_flow.mh + 7) / 8, 1);
        // The block search starts from the tracked motion (read), a round at
        // a time - the round is the last constant - both directions at once;
        // TexelMain then writes its result over the tracked motion.
        D3D12_RESOURCE_BARRIER to_read[] = {
            tiles_barrier,
            Transition(g_flow.trk_mv, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE),
            Transition(g_flow.trk_reverse, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE),
        };
        list->ResourceBarrier(_countof(to_read), to_read);
        list->SetPipelineState(g_flow.pso_block);
        for (UINT32 round = 0; round < kFlowBlockRounds; ++round)
        {
            list->SetComputeRoot32BitConstant(0, round, 6);
            list->Dispatch(g_flow.blocks_x, g_flow.blocks_y, 2);
            list->ResourceBarrier(1, &tiles_barrier);
        }
        list->SetComputeRoot32BitConstant(0, 0, 6);
        D3D12_RESOURCE_BARRIER to_write[] = {
            Transition(g_flow.trk_mv, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_UNORDERED_ACCESS),
            Transition(g_flow.trk_reverse, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_UNORDERED_ACCESS),
        };
        list->ResourceBarrier(_countof(to_write), to_write);
        list->SetPipelineState(g_flow.pso_texel);
        list->Dispatch((g_flow.mw + 7) / 8, (g_flow.mh + 7) / 8, 2);
        }
        // The HUD verdict reads what the tracker found (a texel it pinned
        // still), and FixMain starts from the tracked motion.
        D3D12_RESOURCE_BARRIER tracked[] = {
            Transition(g_flow.trk_mv, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE),
            Transition(g_flow.trk_reverse, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE),
        };
        list->ResourceBarrier(_countof(tracked), tracked);
        list->SetPipelineState(g_flow.pso_hud);
        list->Dispatch((g_flow.w + 7) / 8, (g_flow.hgt + 7) / 8, 1);
        list->ResourceBarrier(1, &still_barrier);
        list->SetPipelineState(g_flow.pso_fix);
        list->Dispatch((g_flow.mw + 7) / 8, (g_flow.mh + 7) / 8, 1);
        // FixMain also marks the pixels near a HUD verdict in the state.
        D3D12_RESOURCE_BARRIER close[] = {
            still_barrier,
            Transition(g_flow.fix_mv, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE),
            Transition(g_flow.fix_reverse, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE),
        };
        list->ResourceBarrier(_countof(close), close);
        if (FAILED(list->Close())) return false;
        ID3D12CommandList *lists[] = { list.get() };
        g_flow.queue->ExecuteCommandLists(1, lists);
        return SUCCEEDED(g_flow.queue->Signal(fence.get(), ++value)) &&
               SUCCEEDED(fence->SetEventOnCompletion(value, event)) &&
               WaitForSingleObject(event, 2000) == WAIT_OBJECT_0 && fence->GetCompletedValue() >= value;
    };

    while (!g_flow.stop && !g_flow.failed)
    {
        FlowSlot *chosen = nullptr;
        unsigned chosen_index = 0;
        {
            std::unique_lock<std::mutex> lock(g_flow.mutex);
            g_flow.wake.wait(lock, [] {
                if (g_flow.stop) return true;
                for (auto &s : g_flow.slots) if (s.state == 2) return true;
                return false;
            });
            if (g_flow.stop) break;
            for (unsigned i = 0; i < _countof(g_flow.slots); ++i)
            {
                FlowSlot &s = g_flow.slots[i];
                if (s.state == 2 && (!chosen || s.sequence > chosen->sequence)) { chosen = &s; chosen_index = i; }
            }
            for (auto &s : g_flow.slots) if (s.state == 2 && &s != chosen) s.state = 0;
            chosen->state = 3;
        }
        if (previous && chosen->sequence > previous + 1)
            dropped += chosen->sequence - previous - 1; // includes overwritten ready slots
        // A pair the presenter skipped keeps the HUD verdict and the
        // stillness count: its two frames are still consecutive captures.
        // Only a real break in the history (the first frame, a reset, a
        // stall, a long gap) clears them.
        const double prepare_start = seconds();
        if (!prepare(chosen_index, chosen->interpolate ? 1.0f : -1.0f)) { g_flow.failed = true; break; }
        // Counted into the pair's age below, so the timing plans for it.
        const double prepared = seconds() - prepare_start;
        if (searched && prepared > 0.2 * chosen->interval)
        {
            if (search_ok)
                Log("[flow] the card is full (%.1f ms for a %.1f ms pair) - the searches wait",
                    prepared * 1000.0, chosen->interval * 1000.0);
            search_ok = false;
            search_retry = seconds() + 3.0;
        }
        else if (!search_ok && seconds() >= search_retry)
            search_ok = true;
        const auto start = std::chrono::steady_clock::now();
        const unsigned count = chosen->count;
        const unsigned steps = std::min(count + 1, std::max(1u,
            static_cast<unsigned>(std::round(chosen->interval * refresh))));
        const bool continuous = chosen->interpolate && chosen->sequence == previous + 1;
        const double step = chosen->interval / steps;
        const double source = chosen->source_time > 0 ? chosen->source_time : chosen->ready_time;
        const double age = std::clamp(chosen->ready_time + prepared - source, 0.0, .12);
        if (!continuous) {
            age_count = age_index = interval_count = interval_index = 0;
            delay = cadence_interval = 0;
        }
        ages[age_index++ % 32] = age;
        age_count = std::min(32u, age_count + 1);
        std::vector<double> sorted(ages, ages + age_count);
        std::sort(sorted.begin(), sorted.end());
        // A bounded jitter cushion, not an accumulating queue of pairs.
        // Low latency: the first generated frame is ready immediately and
        // the endpoint follows after N-1 slots, not a whole extra interval.
        const double desired = sorted[(age_count - 1) * 3 / 4] +
            chosen->interval - step + render_budget + .0005;
        // A real cadence step (e.g. 30 -> 15 -> 30 FPS) changes the amount of
        // interpolation lookahead. Following it at only .1 slot per pair held
        // the low-FPS delay for many frames after the source recovered. The
        // missed-slot / no-burst rules below safely discard obsolete samples.
        intervals[interval_index++ % 5] = chosen->interval;
        interval_count = std::min(5u, interval_count + 1);
        std::vector<double> recent(intervals, intervals + interval_count);
        std::sort(recent.begin(), recent.end());
        const double cadence = recent[interval_count / 2];
        // Require a persistent change, not a single missed capture or the
        // alternating intervals produced when source/display rates differ.
        const bool cadence_change = continuous && interval_count >= 3 && cadence_interval > 0 &&
            (cadence > cadence_interval * 1.65 || cadence < cadence_interval / 1.65);
        if (cadence_interval == 0 || cadence_change) cadence_interval = cadence;
        else cadence_interval += .1 * (cadence - cadence_interval);
        if (delay == 0 || age_count < 4 || cadence_change) delay = desired;
        else delay += std::clamp(desired - delay, -step * .1, step * .1);
        const double end = source + delay;
        for (unsigned k = continuous ? 1 : steps; k <= steps && !g_flow.stop && !g_flow.failed; ++k) {
            double deadline = continuous ? end - (steps - k) * step : seconds();
            // A missed slot is dropped; never catch up with a burst of old
            // samples. Do keep the real endpoint when the source stops.
            if (k < steps && (seconds() > deadline + step * .35 ||
                             deadline < last_present + step * .65)) continue;
            deadline = std::max(deadline, last_present + step * .65);
            wait_until(deadline - render_budget);
            if (g_flow.stop) break;
            if (!wait_buffer()) { g_flow.failed = true; break; }
            if (k < steps && seconds() > deadline + step * .35) continue;
            if (!show(chosen_index, float(k) / steps, 0.0f, deadline)) g_flow.failed = true;
        }
        previous = chosen->sequence;
        const double source_interval_ms = chosen->interval * 1000.0;
        {
            std::lock_guard<std::mutex> lock(g_flow.mutex);
            chosen->state = 0;
        }
        const double elapsed = std::chrono::duration<double>(start - report).count();
        if (elapsed >= 2.0)
        {
            Log("[flow] displayed %.1f FPS (real + generated), x%u", shown / elapsed, count + 1);
            Log("[flow] cadence: real=%llu generated=%llu dropped-pairs=%llu over=%.3fs source-interval=%.3fms",
                shown - generated, generated, dropped, elapsed, source_interval_ms);
            shown = 0;
            generated = dropped = 0;
            report = start;
        }
    }
    CloseHandle(event);
    if (timer) CloseHandle(timer);
    if (trace) fclose(trace);
    if (g_flow.failed)
    {
        Log("[flow] presenter failed - back to the ordinary output");
        g_present_stale = true;
    }
}

static bool EnsureFlow(UINT w, UINT hgt, UINT mw, UINT mh)
{
    if (g_flow.thread.joinable() && g_flow.w == w && g_flow.hgt == hgt && g_flow.mw == mw && g_flow.mh == mh)
        return true;
    CloseFlow();
    if (!EnsureFlowPipeline() || g_flow.queue == nullptr) return false;
    g_flow.last = MakeTex(w, hgt, DXGI_FORMAT_R8G8B8A8_UNORM, false);
    g_flow.scratch = MakeTex(w, hgt, DXGI_FORMAT_R8G8B8A8_UNORM, true);
    g_flow.still = MakeTex(w, hgt, DXGI_FORMAT_R32_FLOAT, true);
    g_flow.fix_mv = MakeTex(mw, mh, DXGI_FORMAT_R16G16_FLOAT, true);
    g_flow.fix_reverse = MakeTex(mw, mh, DXGI_FORMAT_R16G16_FLOAT, true);
    g_flow.trk_mv = MakeTex(mw, mh, DXGI_FORMAT_R16G16_FLOAT, true);
    g_flow.trk_reverse = MakeTex(mw, mh, DXGI_FORMAT_R16G16_FLOAT, true);
    g_flow.resid = MakeTex(mw, mh, DXGI_FORMAT_R32_UINT, true);
    g_flow.tiles_x = (mw + kFlowTile - 1) / kFlowTile;
    g_flow.tiles_y = (mh + kFlowTile - 1) / kFlowTile;
    g_flow.blocks_x = (mw + kFlowBlock - 1) / kFlowBlock;
    g_flow.blocks_y = (mh + kFlowBlock - 1) / kFlowBlock;
    {
        // Two float4 per tile: the seed and what the search found; then four
        // per block: the block search's two fields, each read one round and
        // written the next.
        D3D12_HEAP_PROPERTIES hp = {};
        hp.Type = D3D12_HEAP_TYPE_DEFAULT;
        D3D12_RESOURCE_DESC rd = {};
        rd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
        rd.Width = (UINT64(g_flow.tiles_x) * g_flow.tiles_y * 2 + UINT64(g_flow.blocks_x) * g_flow.blocks_y * 4) * 16;
        rd.Height = 1;
        rd.DepthOrArraySize = 1;
        rd.MipLevels = 1;
        rd.Format = DXGI_FORMAT_UNKNOWN;
        rd.SampleDesc.Count = 1;
        rd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        rd.Flags = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
        h.dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd, D3D12_RESOURCE_STATE_COMMON, nullptr,
                                       __uuidof(ID3D12Resource), reinterpret_cast<void **>(&g_flow.tiles));
    }
    bool ok = g_flow.last != nullptr && g_flow.scratch != nullptr && g_flow.still != nullptr &&
              g_flow.fix_mv != nullptr && g_flow.fix_reverse != nullptr &&
              g_flow.trk_mv != nullptr && g_flow.trk_reverse != nullptr &&
              g_flow.resid != nullptr && g_flow.tiles != nullptr;
    for (auto &slot : g_flow.slots)
    {
        slot.prev = MakeTex(w, hgt, DXGI_FORMAT_R8G8B8A8_UNORM, false);
        slot.cur = MakeTex(w, hgt, DXGI_FORMAT_R8G8B8A8_UNORM, false);
        slot.mv = MakeTex(mw, mh, DXGI_FORMAT_R16G16_FLOAT, false);
        slot.reverse = MakeTex(mw, mh, DXGI_FORMAT_R16G16_FLOAT, false);
        slot.state = 0;
        ok = ok && slot.prev && slot.cur && slot.mv && slot.reverse;
    }
    if (!ok) { Log("[flow] %ux%u textures failed", w, hgt); CloseFlow(); return false; }
    if (!BeginCommands()) { CloseFlow(); return false; }
    std::vector<D3D12_RESOURCE_BARRIER> setup;
    setup.push_back(Transition(g_flow.last, D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_COPY_SOURCE));
    setup.push_back(Transition(g_flow.scratch, D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_UNORDERED_ACCESS));
    setup.push_back(Transition(g_flow.still, D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_UNORDERED_ACCESS));
    setup.push_back(Transition(g_flow.fix_mv, D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE));
    setup.push_back(Transition(g_flow.fix_reverse, D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE));
    setup.push_back(Transition(g_flow.trk_mv, D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE));
    setup.push_back(Transition(g_flow.trk_reverse, D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE));
    setup.push_back(Transition(g_flow.resid, D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_UNORDERED_ACCESS));
    setup.push_back(Transition(g_flow.tiles, D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_UNORDERED_ACCESS));
    for (auto &slot : g_flow.slots)
        for (ID3D12Resource *r : { slot.prev, slot.cur, slot.mv, slot.reverse })
            setup.push_back(Transition(r, D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE));
    h.list->ResourceBarrier(static_cast<UINT>(setup.size()), setup.data());
    if (!WaitFenceValue(h.fence, EndCommands(), 30000)) { CloseFlow(); return false; }

    const UINT stride = h.dev->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
    D3D12_CPU_DESCRIPTOR_HANDLE cpu = g_flow.heap->GetCPUDescriptorHandleForHeapStart();
    for (auto &slot : g_flow.slots)
    {
        // The repaired motion is shared: the presenter draws one pair at a
        // time, and FixMain rewrites it before that pair's first frame.
        ID3D12Resource *srv[8] = { slot.prev, slot.cur, g_flow.fix_mv, g_flow.fix_reverse, slot.mv, slot.reverse,
                                   g_flow.trk_mv, g_flow.trk_reverse };
        for (ID3D12Resource *r : srv)
        {
            D3D12_SHADER_RESOURCE_VIEW_DESC sd = {};
            sd.Format = r->GetDesc().Format;
            sd.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
            sd.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
            sd.Texture2D.MipLevels = 1;
            h.dev->CreateShaderResourceView(r, &sd, cpu);
            cpu.ptr += stride;
        }
        D3D12_UNORDERED_ACCESS_VIEW_DESC ud = {};
        ud.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        ud.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
        h.dev->CreateUnorderedAccessView(g_flow.scratch, nullptr, &ud, cpu);
        cpu.ptr += stride;
        D3D12_UNORDERED_ACCESS_VIEW_DESC sud = {};
        sud.Format = DXGI_FORMAT_R32_FLOAT;
        sud.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
        h.dev->CreateUnorderedAccessView(g_flow.still, nullptr, &sud, cpu);
        cpu.ptr += stride;
        for (ID3D12Resource *r : { g_flow.fix_mv, g_flow.fix_reverse, g_flow.trk_mv, g_flow.trk_reverse })
        {
            D3D12_UNORDERED_ACCESS_VIEW_DESC mud = {};
            mud.Format = DXGI_FORMAT_R16G16_FLOAT;
            mud.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
            h.dev->CreateUnorderedAccessView(r, nullptr, &mud, cpu);
            cpu.ptr += stride;
        }
        D3D12_UNORDERED_ACCESS_VIEW_DESC rud = {};
        rud.Format = DXGI_FORMAT_R32_UINT;
        rud.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
        h.dev->CreateUnorderedAccessView(g_flow.resid, nullptr, &rud, cpu);
        cpu.ptr += stride;
        D3D12_UNORDERED_ACCESS_VIEW_DESC tud = {};
        tud.Format = DXGI_FORMAT_UNKNOWN;
        tud.ViewDimension = D3D12_UAV_DIMENSION_BUFFER;
        tud.Buffer.NumElements = g_flow.tiles_x * g_flow.tiles_y * 2 + g_flow.blocks_x * g_flow.blocks_y * 4;
        tud.Buffer.StructureByteStride = 16;
        h.dev->CreateUnorderedAccessView(g_flow.tiles, nullptr, &tud, cpu);
        cpu.ptr += stride;
    }
    g_flow.w = w; g_flow.hgt = hgt; g_flow.mw = mw; g_flow.mh = mh;
    g_flow.stop = false;
    g_flow.history = false;
    // Latency 1 and the waitable, exactly as DLSS FG's presenter does: the
    // compositor releases one back buffer per vblank and the presenter
    // waits for it instead of guessing the clock.
    if (g_present_swap != nullptr)
    {
        IDXGISwapChain2 *sc2 = nullptr;
        if (SUCCEEDED(g_present_swap->QueryInterface(__uuidof(IDXGISwapChain2), reinterpret_cast<void **>(&sc2))) && sc2)
        {
            sc2->SetMaximumFrameLatency(1);
            if (!g_flow_waitable) g_flow_waitable = sc2->GetFrameLatencyWaitableObject();
            sc2->Release();
        }
    }
    g_flow.thread = std::thread(FlowPresenter);
    Log("[flow] FLOW MFG x%u at %ux%u (motion %ux%u), presented on its own queue",
        CurrentFlowCount() + 1, w, hgt, mw, mh);
    return true;
}

// Nothing was handed over (starved or failed), but the defer-tail contract
// still wants a fence after the evaluate: an empty submission gives one.
static bool FlowToken()
{
    if (!BeginCommands()) return false;
    const UINT64 fence = EndCommands();
    if (!WaitFenceValue(h.fence, fence, 30000)) return false;
    g_flow_present_fence = fence;
    return true;
}

// Bounded, opt-in diagnostics: replay identical captured colours and estimated
// motion through the production shader without a live desktop or timing noise.
static void DumpFlowPair(FlowSlot &slot)
{
    char folder[MAX_PATH] = {};
    if (!GetEnvironmentVariableA("NS_FLOW_DUMP", folder, sizeof(folder))) return;
    const UINT64 sequence = g_flow.sequence + 1;
    if (sequence < 50 || sequence >= 58 || g_flow.w != 1280) return;
    char path[MAX_PATH]; FILE *file = nullptr;
    sprintf_s(path, "%s/pair-%04llu.json", folder, sequence);
    if (!fopen_s(&file, path, "w") && file) {
        fprintf(file, "{\"colour\":[%u,%u],\"motion\":[%u,%u]}",
            g_flow.w, g_flow.hgt, g_flow.mw, g_flow.mh); fclose(file);
    }
    for (auto pair : {std::make_pair(slot.prev, "prev"), std::make_pair(slot.cur, "cur"),
                      std::make_pair(slot.mv, "mv"), std::make_pair(slot.reverse, "reverse")}) {
        auto desc = pair.first->GetDesc();
        D3D12_PLACED_SUBRESOURCE_FOOTPRINT fp{}; UINT rows; UINT64 rowbytes, bytes;
        h.dev->GetCopyableFootprints(&desc, 0, 1, 0, &fp, &rows, &rowbytes, &bytes);
        D3D12_HEAP_PROPERTIES hp{}; hp.Type = D3D12_HEAP_TYPE_READBACK;
        D3D12_RESOURCE_DESC rd{}; rd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
        rd.Width = bytes; rd.Height = 1; rd.DepthOrArraySize = rd.MipLevels = 1;
        rd.SampleDesc.Count = 1; rd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        winrt::com_ptr<ID3D12Resource> rb;
        if (FAILED(h.dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd,
            D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(rb.put()))) || !BeginCommands()) return;
        auto pre = Transition(pair.first, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_COPY_SOURCE);
        h.list->ResourceBarrier(1, &pre);
        D3D12_TEXTURE_COPY_LOCATION src{}, dst{};
        src.pResource = pair.first; src.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
        dst.pResource = rb.get(); dst.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT; dst.PlacedFootprint = fp;
        h.list->CopyTextureRegion(&dst, 0, 0, 0, &src, nullptr);
        auto post = Transition(pair.first, D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
        h.list->ResourceBarrier(1, &post);
        if (!WaitFenceValue(h.fence, EndCommands(), 30000)) return;
        BYTE *data = nullptr; D3D12_RANGE read{0, SIZE_T(bytes)}, written{0, 0};
        if (FAILED(rb->Map(0, &read, reinterpret_cast<void **>(&data)))) return;
        sprintf_s(path, "%s/%s-%04llu.bin", folder, pair.second, sequence);
        if (!fopen_s(&file, path, "wb") && file) {
            for (UINT y = 0; y < rows; ++y) fwrite(data + SIZE_T(y) * fp.Footprint.RowPitch, 1, SIZE_T(rowbytes), file);
            fclose(file);
        }
        rb->Unmap(0, &written);
    }
}

// The main path's half: copy the finished frame, the one before it and the
// motion between them into a free slot and hand it to the presenter.
static bool FlowPresent(VideoState &v, ID3D12Resource *color, D3D12_RESOURCE_STATES state)
{
    const UINT w = v.upscale ? v.full_w : v.w;
    const UINT hgt = v.upscale ? v.full_h : v.hgt;
    if (!EnsureFlow(w, hgt, v.w, v.hgt))
    {
        g_flow.failed = true;
        CloseFlow();
        g_present_stale = true;
        Log("[flow] could not start - back to the ordinary output");
        return FlowToken();
    }
    const auto now = std::chrono::steady_clock::now();
    const double source_time = (g_wgc_active || g_dda_active) ? g_capture_time : 0.0;
    if (g_flow.history && source_time > 0 && source_time == g_flow.last_source_time && !g_fg_reset)
        return FlowToken();
    const double interval = g_flow.history && source_time > g_flow.last_source_time && g_flow.last_source_time > 0
        ? source_time - g_flow.last_source_time
        : (g_flow.history ? std::chrono::duration<double>(now - g_flow.last_time).count() : 0.016);
    // Down to 5 FPS. Below that a pair is a pause more than a motion.
    const bool interpolate = g_flow.history && !g_fg_reset && interval < 0.2;
    g_flow.last_time = now;
    g_flow.last_source_time = source_time;

    FlowSlot *slot = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_flow.mutex);
        for (auto &s : g_flow.slots) if (s.state == 0) { slot = &s; break; }
        if (!slot) for (auto &s : g_flow.slots) if (s.state == 2) { slot = &s; break; }
        if (slot) slot->state = 1;
    }
    if (!slot) return FlowToken();   // every slot is on screen right now: drop this one

    if (!BeginCommands())
    {
        std::lock_guard<std::mutex> lock(g_flow.mutex);
        slot->state = 0;
        return false;
    }
    std::vector<D3D12_RESOURCE_BARRIER> pre = {
        Transition(slot->prev, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_COPY_DEST),
        Transition(slot->cur, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_COPY_DEST),
        Transition(slot->mv, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_COPY_DEST),
        Transition(v.mv.tex, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_COPY_SOURCE),
    };
    if (state != D3D12_RESOURCE_STATE_COPY_SOURCE)
        pre.push_back(Transition(color, state, D3D12_RESOURCE_STATE_COPY_SOURCE));
    h.list->ResourceBarrier(static_cast<UINT>(pre.size()), pre.data());
    h.list->CopyResource(slot->prev, g_flow.history ? g_flow.last : color);
    h.list->CopyResource(slot->cur, color);
    h.list->CopyResource(slot->mv, v.mv.tex);
    if (g_nvofa.bidirectional && g_nvofa.valid && g_nvofa.reverse && !g_nvofa.failed) {
        D3D12_RESOURCE_BARRIER rev_pre[] = {
            Transition(slot->reverse, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_COPY_DEST),
            Transition(g_nvofa.reverse.get(), D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_COPY_SOURCE)};
        h.list->ResourceBarrier(2, rev_pre);
        h.list->CopyResource(slot->reverse, g_nvofa.reverse.get());
        D3D12_RESOURCE_BARRIER rev_post[] = {
            Transition(slot->reverse, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE),
            Transition(g_nvofa.reverse.get(), D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE)};
        h.list->ResourceBarrier(2, rev_post);
    }
    D3D12_RESOURCE_BARRIER last_in = Transition(g_flow.last, D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATE_COPY_DEST);
    h.list->ResourceBarrier(1, &last_in);
    h.list->CopyResource(g_flow.last, color);
    // Nothing is exported from here while FLOW MFG is on. The presenter
    // exports what it shows - the frames in between as well as the real ones -
    // so an outside recorder sees the multiplied picture instead of the
    // network's rate. Exporting from both would mean two queues copying into
    // the one shared texture.
    std::vector<D3D12_RESOURCE_BARRIER> post = {
        Transition(g_flow.last, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_COPY_SOURCE),
        Transition(slot->prev, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE),
        Transition(slot->cur, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE),
        Transition(slot->mv, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE),
        Transition(v.mv.tex, D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE),
    };
    if (state != D3D12_RESOURCE_STATE_COPY_SOURCE)
        post.push_back(Transition(color, D3D12_RESOURCE_STATE_COPY_SOURCE, state));
    h.list->ResourceBarrier(static_cast<UINT>(post.size()), post.data());
    const UINT64 fence = EndCommands();
    if (!WaitFenceValue(h.fence, fence, 30000))
    {
        std::lock_guard<std::mutex> lock(g_flow.mutex);
        slot->state = 0;
        return false;
    }
    g_flow_present_fence = fence;
    DumpFlowPair(*slot);
    {
        std::lock_guard<std::mutex> lock(g_flow.mutex);
        slot->sequence = ++g_flow.sequence;
        slot->count = CurrentFlowCount();
        slot->interpolate = interpolate;
        slot->both = g_nvofa.bidirectional && g_nvofa.valid && g_nvofa.reverse && !g_nvofa.failed;
        slot->interval = std::clamp(interval, 0.004, 0.2);
        slot->source_time = source_time;
        LARGE_INTEGER q, f; QueryPerformanceCounter(&q); QueryPerformanceFrequency(&f);
        slot->ready_time = double(q.QuadPart) / double(f.QuadPart);
        slot->state = 2;
    }
    g_flow.history = true;
    g_flow.wake.notify_one();
    return true;
}
