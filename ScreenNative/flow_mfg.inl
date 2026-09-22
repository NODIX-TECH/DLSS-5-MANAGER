// FLOW MFG - LIVE FLOW's own frame multiplication, x2 to x6.
//
// DLSS 4.5 FG needs NVIDIA's frame-generation runtime and a card it accepts.
// This needs neither: the frames in between are made here, from the two real
// frames either side and the motion field LIVE FLOW already computes for the
// network - the same idea as Lossless Scaling, on any GPU.
//
// How a frame in between is made (kFlowHlsl), at time t between the
// previous real frame P and the current one C:
//   * The motion field says where each pixel of C was in P. Walking that
//     path a fraction t of the way gives where it is at t, so the picture
//     at t is P sampled at y + t*m and C sampled at y - (1-t)*m, blended
//     by t.
//   * Where those two samples disagree, the motion was wrong there - an
//     edge the flow blurred, something that appeared - and a blend would
//     be a double image. Three answers are tried in order and each is
//     believed only as far as it explains both real frames: the vector
//     under the pixel, then the motion of the whole region around it (the
//     camera, or the object the pixel belongs to - what saves a face or a
//     moving picture whose own edge confuses the flow), and last the real
//     frame nearer in time, which is at least true of some moment.
//   * Where a pixel has not changed for several real frames it is shown as
//     it is and never warped. That is the HUD, the subtitles, the score in
//     the corner - the part frame generation usually smears. It is learnt
//     over time (gStill) rather than decided on one pair of frames, so a
//     still panel over a moving scene keeps its stillness and a HUD that
//     changes gives it up at once.
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

static const char kFlowHlsl[] = R"hlsl(
Texture2D<float4>   gPrev  : register(t0);
Texture2D<float4>   gCur   : register(t1);
Texture2D<float2>   gMv    : register(t2);
RWTexture2D<float4> gDst   : register(u0);
RWTexture2D<float>  gStill : register(u1);
SamplerState        gLin   : register(s0);
cbuffer FC : register(b0) { uint gW; uint gH; float gT; float gAccum; };
float Max3(float3 v) { return max(v.x, max(v.y, v.z)); }

// Eight directions around a pixel, for the motion of the region it is in.
static const float2 kAround[8] = {
    float2( 1.0f, 0.0f), float2(-1.0f, 0.0f), float2( 0.0f, 1.0f), float2( 0.0f,-1.0f),
    float2( 0.7f, 0.7f), float2(-0.7f, 0.7f), float2( 0.7f,-0.7f), float2(-0.7f,-0.7f) };

// Follow one candidate vector and see how well it explains both real frames.
// x,y,z is the colour it gives at time t; w is how far it may be believed.
float4 Follow(float2 uv, float2 texel, float2 size, float2 m, float t)
{
    float2 uvC = uv - (1.0f - t) * m * texel;
    float2 uvP = uv + t * m * texel;
    float3 cC = gCur.SampleLevel(gLin, uvC, 0).rgb;
    float3 cP = gPrev.SampleLevel(gLin, uvP, 0).rgb;
    // The two ends are the same thing seen in two frames: if they do not look
    // alike, the vector is wrong here - something was uncovered, or the flow
    // guessed across an edge - and blending them would show a double image.
    float trust = 1.0f - smoothstep(0.06f, 0.22f, Max3(abs(cC - cP)));
    // A path that leaves the picture has nothing to sample at its end: the
    // sampler clamps, and the edge pixel is smeared inwards - the streaks
    // along the border of a picture that is moving as a whole.
    float2 pixC = uvC * size, pixP = uvP * size;
    float border = min(min(min(pixC.x, size.x - pixC.x), min(pixC.y, size.y - pixC.y)),
                       min(min(pixP.x, size.x - pixP.x), min(pixP.y, size.y - pixP.y)));
    trust *= saturate(border / 6.0f);
    return float4(lerp(cP, cC, t), trust);
}

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= gW || id.y >= gH) return;
    float4 c0 = gCur[id.xy];
    float4 p0 = gPrev[id.xy];

    // How long this pixel has stood still, in real frames: up a quarter for
    // every frame it does not change, straight back to nothing the moment it
    // does. Only the first frame in between counts, so the answer is per real
    // frame however many frames are made from it. This is the HUD, found by
    // how it behaves rather than by looking for panels: four quiet frames and
    // it is left alone, one change and it rejoins the picture.
    float seen = gStill[id.xy];
    if (!isfinite(seen)) seen = 0.0f;
    seen = saturate(seen);
    if (gAccum > 0.5f)
    {
        seen = Max3(abs(p0.rgb - c0.rgb)) > 0.035f ? 0.0f : min(1.0f, seen + 0.25f);
        gStill[id.xy] = seen;
    }
    if (gT >= 0.999f) { gDst[id.xy] = c0; return; }

    float2 size = float2(gW, gH);
    float2 texel = 1.0f / size;
    float2 uv = (float2(id.xy) + 0.5f) * texel;
    uint mw, mh;
    gMv.GetDimensions(mw, mh);
    float2 scale = size / float2(mw, mh);

    // Where the pixel that passes through here is coming from. The field is
    // written on the current frame's grid and says where each of ITS pixels
    // was, so reading it at this spot is only a first guess; following it
    // back twice lands on the pixel whose path really crosses this place,
    // which is what keeps moving edges from being dragged sideways.
    float2 m = gMv.SampleLevel(gLin, uv, 0) * scale;
    [unroll] for (int step = 0; step < 2; ++step)
        m = gMv.SampleLevel(gLin, uv - (1.0f - gT) * m * texel, 0) * scale;

    // What the whole region is doing, from far enough out that one bad vector
    // cannot set it: a camera pan, or the body a face belongs to. Where the
    // vector under the pixel is not to be believed this usually still is.
    float2 wide = 0.0f;
    [unroll] for (int k = 0; k < 8; ++k)
        wide += gMv.SampleLevel(gLin, uv + kAround[k] * 24.0f * texel, 0);
    wide = wide * scale / 8.0f;

    float4 local = Follow(uv, texel, size, m, gT);
    // A motion boundary shows as the field disagreeing with itself a couple
    // of pixels away. Warping across one tears; this is where a face's
    // features used to be pulled apart.
    float2 mE = gMv.SampleLevel(gLin, uv + float2(2.0f, 0.0f) * texel, 0) * scale;
    float2 mW = gMv.SampleLevel(gLin, uv - float2(2.0f, 0.0f) * texel, 0) * scale;
    float2 mS = gMv.SampleLevel(gLin, uv + float2(0.0f, 2.0f) * texel, 0) * scale;
    float2 mN = gMv.SampleLevel(gLin, uv - float2(0.0f, 2.0f) * texel, 0) * scale;
    float spread = max(max(length(mE - m), length(mW - m)), max(length(mS - m), length(mN - m)));
    local.w *= 1.0f - smoothstep(1.5f, 6.0f, spread);

    float4 region = Follow(uv, texel, size, wide, gT);

    // Last resort, true of one real frame if of no moment in between: for
    // small motion a cross-fade is invisible; for large motion the nearer
    // frame alone beats a double image.
    float fast = saturate((length(m) - 2.0f) / 10.0f);
    float3 fade = lerp(p0.rgb, c0.rgb, gT);
    float3 safe = lerp(fade, gT < 0.5f ? p0.rgb : c0.rgb, fast);
    float3 result = lerp(lerp(safe, region.rgb, region.w), local.rgb, local.w);

    // What has been standing still is shown as it is and never warped. The
    // history says which pixels those are; the two frames in hand say whether
    // it is still true right now, over a small neighbourhood so the edge of a
    // panel keeps its stillness instead of losing it to the scene behind it.
    float now = 0.0f;
    float3 lo = min(p0.rgb, c0.rgb);
    float3 hi = max(p0.rgb, c0.rgb);
    [unroll] for (int y = -1; y <= 1; ++y)
    [unroll] for (int x = -1; x <= 1; ++x)
    {
        int2 q = clamp(int2(id.xy) + int2(x, y) * 2, int2(0, 0), int2(int(gW) - 1, int(gH) - 1));
        float3 qp = gPrev[q].rgb;
        float3 qc = gCur[q].rgb;
        now += 1.0f - smoothstep(0.02f, 0.06f, Max3(abs(qp - qc)));
        lo = min(lo, min(qp, qc));
        hi = max(hi, max(qp, qc));
    }

    // Nothing here may be a colour that is in neither frame. A vector that
    // lands on the wrong side of an edge makes exactly that - a speck, or a
    // smear that belongs to nothing - and the pixels needed to catch it have
    // just been read for the line above, so it costs no sampling. The slack is
    // taken from how much the two frames already disagree around here, which
    // is the room real motion needs and nothing more.
    float3 slack = (hi - lo) * 0.25f + 0.02f;
    result = clamp(result, lo - slack, hi + slack);

    result = lerp(result, fade, min(now / 9.0f, seen));
    gDst[id.xy] = float4(result, 1.0f);
}
)hlsl";

struct FlowSlot
{
    ID3D12Resource *prev = nullptr, *cur = nullptr, *mv = nullptr;   // NON_PIXEL_SHADER_RESOURCE at rest
    int state = 0;            // 0 free, 1 writing, 2 ready, 3 presenting - under the mutex
    UINT64 sequence = 0;
    unsigned count = 0;       // frames in between, before the real one
    bool interpolate = false;
    double interval = 0.016;
};

static struct FlowState
{
    ID3D12CommandQueue   *queue = nullptr;
    ID3D12RootSignature  *rs = nullptr;
    ID3D12PipelineState  *pso = nullptr;
    ID3D12DescriptorHeap *heap = nullptr;   // one set of four per slot
    ID3D12Resource       *last = nullptr;   // the previous real frame, COPY_SOURCE at rest
    ID3D12Resource       *scratch = nullptr; // the frame being drawn, UNORDERED_ACCESS at rest
    ID3D12Resource       *still = nullptr;   // how long each pixel has stood still (the HUD)
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
    bool history = false;
    bool pipeline_failed = false;
    ~FlowState() { stop = true; wake.notify_all(); if (thread.joinable()) thread.join(); }
} g_flow;

static HANDLE g_flow_waitable = nullptr;

// Per slot: previous, current, motion, the frame being drawn, the stillness.
static const UINT kFlowDescriptors = 5;

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
    g_flow_waitable = nullptr;
    for (auto &slot : g_flow.slots)
    {
        for (ID3D12Resource **r : { &slot.prev, &slot.cur, &slot.mv })
            if (*r != nullptr) { (*r)->Release(); *r = nullptr; }
        slot.state = 0;
    }
    if (g_flow.last != nullptr) { g_flow.last->Release(); g_flow.last = nullptr; }
    if (g_flow.scratch != nullptr) { g_flow.scratch->Release(); g_flow.scratch = nullptr; }
    if (g_flow.still != nullptr) { g_flow.still->Release(); g_flow.still = nullptr; }
    if (g_flow.readback != nullptr) { g_flow.readback->Release(); g_flow.readback = nullptr; }
    g_flow.rb_pitch = 0;
    g_flow.w = g_flow.hgt = g_flow.mw = g_flow.mh = 0;
    g_flow.history = false;
}

static bool EnsureFlowPipeline()
{
    if (g_flow.pso != nullptr) return true;
    if (g_flow.pipeline_failed) return false;
    g_flow.pipeline_failed = true;
    HMODULE compiler = LoadLibraryW(L"d3dcompiler_47.dll");
    auto compile = compiler ? reinterpret_cast<PFN_D3DCompile_>(GetProcAddress(compiler, "D3DCompile")) : nullptr;
    HMODULE d3d12 = GetModuleHandleW(L"d3d12.dll");
    auto serialize = d3d12 ? reinterpret_cast<PFN_D3D12SerializeRootSignature_>(
                                 GetProcAddress(d3d12, "D3D12SerializeRootSignature")) : nullptr;
    if (compile == nullptr || serialize == nullptr) { Log("[flow] compiler unavailable"); return false; }
    ID3DBlob *code = nullptr, *errors = nullptr;
    HRESULT hr = compile(kFlowHlsl, sizeof(kFlowHlsl) - 1, "flow.hlsl", nullptr, nullptr,
                         "CSMain", "cs_5_0", 0, 0, &code, &errors);
    if (FAILED(hr) || code == nullptr)
    {
        Log("[flow] shader failed 0x%08X: %s", hr,
            errors ? static_cast<const char *>(errors->GetBufferPointer()) : "(no log)");
        if (errors) errors->Release();
        return false;
    }
    if (errors) { errors->Release(); errors = nullptr; }
    D3D12_DESCRIPTOR_RANGE ranges[2] = {};
    ranges[0].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
    ranges[0].NumDescriptors = 3;
    ranges[1].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_UAV;
    ranges[1].NumDescriptors = 2;               // the frame being drawn, and the stillness
    ranges[1].OffsetInDescriptorsFromTableStart = 3;
    D3D12_ROOT_PARAMETER params[2] = {};
    params[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
    params[0].Constants.Num32BitValues = 4;
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
    if (FAILED(hr) || rs_blob == nullptr) { Log("[flow] root signature failed 0x%08X", hr); code->Release(); return false; }
    hr = h.dev->CreateRootSignature(0, rs_blob->GetBufferPointer(), rs_blob->GetBufferSize(),
                                    __uuidof(ID3D12RootSignature), reinterpret_cast<void **>(&g_flow.rs));
    rs_blob->Release();
    if (FAILED(hr)) { code->Release(); return false; }
    D3D12_COMPUTE_PIPELINE_STATE_DESC pd = {};
    pd.pRootSignature = g_flow.rs;
    pd.CS.pShaderBytecode = code->GetBufferPointer();
    pd.CS.BytecodeLength = code->GetBufferSize();
    hr = h.dev->CreateComputePipelineState(&pd, __uuidof(ID3D12PipelineState), reinterpret_cast<void **>(&g_flow.pso));
    code->Release();
    if (FAILED(hr)) { Log("[flow] pipeline failed 0x%08X", hr); return false; }
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
    UINT64 value = 0, previous = 0, shown = 0;
    auto report = std::chrono::steady_clock::now();
    if (g_flow_waitable != nullptr) WaitForSingleObject(g_flow_waitable, 2000);   // the first returns at once

    auto show = [&](unsigned slot_index, float t, bool accum) {
        winrt::com_ptr<ID3D12Resource> bb;
        if (FAILED(g_present_swap->GetBuffer(g_present_swap->GetCurrentBackBufferIndex(), IID_PPV_ARGS(bb.put()))) ||
            FAILED(alloc->Reset()) || FAILED(list->Reset(alloc.get(), nullptr))) return false;
        ID3D12DescriptorHeap *heaps[] = { g_flow.heap };
        list->SetDescriptorHeaps(1, heaps);
        list->SetComputeRootSignature(g_flow.rs);
        list->SetPipelineState(g_flow.pso);
        UINT32 c[4] = { g_flow.w, g_flow.hgt, 0, 0 };
        const float accumulate = accum ? 1.0f : 0.0f;
        memcpy(&c[2], &t, sizeof(float));
        memcpy(&c[3], &accumulate, sizeof(float));
        list->SetComputeRoot32BitConstants(0, 4, c, 0);
        D3D12_GPU_DESCRIPTOR_HANDLE gpu = g_flow.heap->GetGPUDescriptorHandleForHeapStart();
        gpu.ptr += static_cast<UINT64>(slot_index) * kFlowDescriptors * stride;
        list->SetComputeRootDescriptorTable(1, gpu);
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
        const HRESULT pr = g_present_swap->Present(0, 0);
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
        return true;
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
        const auto start = std::chrono::steady_clock::now();
        const unsigned count = chosen->count;
        // A frame that was dropped on the way must not be interpolated towards.
        if (chosen->interpolate && count > 0 && chosen->sequence == previous + 1)
        {
            for (unsigned k = 1; k <= count && !g_flow.stop && !g_flow.failed; ++k)
            {
                const auto deadline = start + std::chrono::duration<double>(chosen->interval * k / (count + 1));
                // Late already: a frame from the past is worse than none.
                if (std::chrono::steady_clock::now() >= deadline) continue;
                if (g_flow_waitable != nullptr && WaitForSingleObject(g_flow_waitable, 2000) != WAIT_OBJECT_0)
                    Log("[flow] waitable timeout - the compositor stalled");
                // The moment this frame will be seen, not the step it is in
                // the queue: the compositor releases back buffers on its own
                // vblanks, so a fixed 1/N step drifts against them and the
                // motion judders. Reading the clock here is the difference
                // between "the third of four" and "what is true now".
                const double elapsed = std::chrono::duration<double>(
                    std::chrono::steady_clock::now() - start).count();
                const float when = static_cast<float>(
                    std::clamp(elapsed / std::max(chosen->interval, 1e-4), 0.02, 0.98));
                // The stillness is counted once for the pair, on the first
                // frame made from it, whether that pair gives one frame or
                // five: it measures real frames, not made-up ones.
                if (!show(chosen_index, when, k == 1)) g_flow.failed = true;
                std::unique_lock<std::mutex> lock(g_flow.mutex);
                if (g_flow.wake.wait_until(lock, deadline, [&] {
                        if (g_flow.stop) return true;
                        for (auto &slot : g_flow.slots)
                            if (slot.state == 2 && slot.sequence > chosen->sequence) return true;
                        return false;
                    }))
                    break;
            }
        }
        if (!g_flow.stop && !g_flow.failed)
        {
            if (g_flow_waitable != nullptr && WaitForSingleObject(g_flow_waitable, 2000) != WAIT_OBJECT_0)
                Log("[flow] waitable timeout on the real frame");
            if (!show(chosen_index, 1.0f, false)) g_flow.failed = true;
        }
        previous = chosen->sequence;
        {
            std::lock_guard<std::mutex> lock(g_flow.mutex);
            chosen->state = 0;
        }
        const double elapsed = std::chrono::duration<double>(start - report).count();
        if (elapsed >= 2.0)
        {
            Log("[flow] displayed %.1f FPS (real + generated), x%u", shown / elapsed, count + 1);
            shown = 0;
            report = start;
        }
    }
    CloseHandle(event);
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
    bool ok = g_flow.last != nullptr && g_flow.scratch != nullptr && g_flow.still != nullptr;
    for (auto &slot : g_flow.slots)
    {
        slot.prev = MakeTex(w, hgt, DXGI_FORMAT_R8G8B8A8_UNORM, false);
        slot.cur = MakeTex(w, hgt, DXGI_FORMAT_R8G8B8A8_UNORM, false);
        slot.mv = MakeTex(mw, mh, DXGI_FORMAT_R16G16_FLOAT, false);
        slot.state = 0;
        ok = ok && slot.prev && slot.cur && slot.mv;
    }
    if (!ok) { Log("[flow] %ux%u textures failed", w, hgt); CloseFlow(); return false; }
    if (!BeginCommands()) { CloseFlow(); return false; }
    std::vector<D3D12_RESOURCE_BARRIER> setup;
    setup.push_back(Transition(g_flow.last, D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_COPY_SOURCE));
    setup.push_back(Transition(g_flow.scratch, D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_UNORDERED_ACCESS));
    setup.push_back(Transition(g_flow.still, D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_UNORDERED_ACCESS));
    for (auto &slot : g_flow.slots)
        for (ID3D12Resource *r : { slot.prev, slot.cur, slot.mv })
            setup.push_back(Transition(r, D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE));
    h.list->ResourceBarrier(static_cast<UINT>(setup.size()), setup.data());
    if (!WaitFenceValue(h.fence, EndCommands(), 30000)) { CloseFlow(); return false; }

    const UINT stride = h.dev->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
    D3D12_CPU_DESCRIPTOR_HANDLE cpu = g_flow.heap->GetCPUDescriptorHandleForHeapStart();
    for (auto &slot : g_flow.slots)
    {
        ID3D12Resource *srv[3] = { slot.prev, slot.cur, slot.mv };
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
            g_flow_waitable = sc2->GetFrameLatencyWaitableObject();
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
    const double interval = g_flow.history ? std::chrono::duration<double>(now - g_flow.last_time).count() : 0.016;
    const bool interpolate = g_flow.history && !g_fg_reset && interval < 0.12;
    g_flow.last_time = now;

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
    h.list->CopyResource(slot->prev, g_flow.last);
    h.list->CopyResource(slot->cur, color);
    h.list->CopyResource(slot->mv, v.mv.tex);
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
    {
        std::lock_guard<std::mutex> lock(g_flow.mutex);
        slot->sequence = ++g_flow.sequence;
        slot->count = CurrentFlowCount();
        slot->interpolate = interpolate;
        slot->interval = std::clamp(interval, 0.004, 0.12);
        slot->state = 2;
    }
    g_flow.history = true;
    g_flow.wake.notify_one();
    return true;
}
