// Halo reduction - the LIVE FLOW slider of that name.
//
// What it removes. Measured on real content (tests/screen_bench.py, a football
// broadcast frame): along a strong edge the network lifts the bright side by
// up to +10 levels and lets the lift fade over ~15 of its own pixels - a wide
// local-contrast rim, the same shape an oversized unsharp mask draws. At the
// usual half-resolution work size that rim is 30 screen pixels wide, which is
// the outline people see around players and heads. Crisp synthetic edges get
// almost none; soft, real footage gets the most.
//
// How. Everything happens on the network's change D = nr_out - nr_in, at the
// network's own resolution:
//
//   Tbil = cross-bilateral blur of D, guided by the input's luma - wide
//          (radius 64, sigma 32), but only across pixels that look alike,
//          so it never mixes the two sides of an edge. Inside one region
//          it is the region's own average change: the relighting, without
//          the rim that hugs the region's border.
//   Dc   = D - H * box3(D - Tbil)
//          What differs from that average at a scale wider than three
//          pixels is the rim, and it goes. Fine detail - the one- and
//          two-pixel structure the network adds - is under the box and
//          stays.
//
// The composite then brings Dc up to the screen edge-aware (kResidualHlsl).
// H is the slider, 0..1. At 0 none of this runs and the picture is exactly
// what it was before the slider existed.
//
// Offline, on the captured frame: the rim at the shirt edge went from +9.6 to
// +2.7 levels at H = 1, and the flat inside of the shirt did not move.
//
// Cost: three small compute passes at the work resolution, recorded after
// the network's closing GPU timestamp - so "eval on GPU" in the phase log
// still measures the network alone, and the frame time shows the rest.

static constexpr uint32_t RESIZE_FLAG_HALO = 0x4u;   // bits 8..15 carry the amount, 0..255

// The amount the client last asked for, 0..1. NS_HALO decides the first
// frame (the worker starts before any RNSZ); every RNSZ carrying
// RESIZE_FLAG_HALO overwrites it.
static float g_halo = -1.0f;

static float CurrentHalo()
{
    if (g_halo < 0.0f)
    {
        char buf[16] = {};
        const DWORD got = GetEnvironmentVariableA("NS_HALO", buf, sizeof(buf));
        float f = (got > 0 && got < sizeof(buf)) ? static_cast<float>(atof(buf)) : 0.0f;
        g_halo = (f > 0.0f) ? (f < 1.0f ? f : 1.0f) : 0.0f;
        if (g_halo > 0.0f) Log("[halo] reduction %.2f from NS_HALO", g_halo);
    }
    return g_halo;
}

static void HaloFromResizeFlags(uint32_t flags)
{
    if ((flags & RESIZE_FLAG_HALO) == 0) return;
    const float amount = static_cast<float>((flags >> 8) & 0xFFu) / 255.0f;
    if (g_halo < 0.0f || amount != g_halo)
        Log("[halo] reduction %.2f", amount);
    g_halo = amount;
}

// One cross-bilateral pass along a direction. gMode 0: the source is
// gA - gB (nr_out - nr_in, the change itself); gMode 1: the source is gA.
// The guide is always the network's input.
static const char kHaloBlurHlsl[] = R"hlsl(
Texture2D<float4>   gGuide : register(t0);
Texture2D<float4>   gA     : register(t1);
Texture2D<float4>   gB     : register(t2);
RWTexture2D<float4> gDst   : register(u0);
cbuffer HC : register(b0) { uint gW; uint gH; int gDirX; int gDirY; int gRadius; int gStep; uint gMode; float gSigmaR; uint gMvW; uint gMvH; };
static const float3 kLuma = float3(0.299f, 0.587f, 0.114f);
[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= gW || id.y >= gH) return;
    int2 p = int2(id.xy);
    int2 hi = int2(int(gW) - 1, int(gH) - 1);
    int2 dir = int2(gDirX, gDirY);
    float lc = dot(gGuide[p].rgb, kLuma);
    float ss = max(1.0f, float(gRadius) * 0.5f);
    float inv_ss = 1.0f / (2.0f * ss * ss);
    float inv_sr = 1.0f / (2.0f * gSigmaR * gSigmaR);
    float4 acc = 0.0f;
    float wsum = 0.0f;
    [loop] for (int k = -gRadius; k <= gRadius; k += gStep)
    {
        int2 q = clamp(p + dir * k, int2(0, 0), hi);
        float dl = dot(gGuide[q].rgb, kLuma) - lc;
        float w = exp(-float(k * k) * inv_ss - dl * dl * inv_sr);
        float4 s = gMode == 0 ? float4(gA[q].rgb - gB[q].rgb, 0.0f) : gA[q];
        acc += s * w;
        wsum += w;
    }
    gDst[p] = acc / max(wsum, 1e-6f);
}
)hlsl";

// Dc = D - H * box3(D - Tbil). t0 nr_in, t1 nr_out, t2 Tbil, t3 motion.
//
// H rises towards full strength at a moving object's border. That is where the
// network's rim is at its worst: it carries its history, and along a border
// that moves the history lags behind the object, so the lift trails it as a
// ghost outline - the halo around a running player. The motion field says
// where those borders are: two vectors a few pixels apart that disagree. On
// anything that moves as one piece, and on a still picture, the field agrees
// with itself and H is exactly the slider.
static const char kHaloFixHlsl[] = R"hlsl(
Texture2D<float4>   gIn  : register(t0);
Texture2D<float4>   gOut : register(t1);
Texture2D<float4>   gBil : register(t2);
Texture2D<float2>   gMv  : register(t3);
RWTexture2D<float4> gDst : register(u0);
cbuffer HC : register(b0) { uint gW; uint gH; int gDirX; int gDirY; int gRadius; int gStep; uint gMode; float gHalo; uint gMvW; uint gMvH; };
float2 MotionAt(int2 q, int2 mhi)
{
    float2 m = gMv[clamp(q, int2(0, 0), mhi)];
    return all(isfinite(m)) ? m : float2(0.0f, 0.0f);
}
[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= gW || id.y >= gH) return;
    int2 p = int2(id.xy);
    int2 hi = int2(int(gW) - 1, int(gH) - 1);
    float3 e = 0.0f;
    [unroll] for (int y = -1; y <= 1; ++y)
    [unroll] for (int x = -1; x <= 1; ++x)
    {
        int2 q = clamp(p + int2(x, y), int2(0, 0), hi);
        e += (gOut[q].rgb - gIn[q].rgb) - gBil[q].rgb;
    }
    float3 d = gOut[p].rgb - gIn[p].rgb;
    float gain = gHalo;
    if (gMvW > 0 && gMvH > 0)
    {
        // Two rings, 5 and 12 of the network's pixels out: the rim reaches
        // about 15, so a border that close still counts.
        float2 toMv = float2(gMvW, gMvH) / float2(gW, gH);
        int2 mhi = int2(int(gMvW) - 1, int(gMvH) - 1);
        int2 q = int2((float2(p) + 0.5f) * toMv);
        float2 m0 = MotionAt(q, mhi);
        float spread = 0.0f;
        [unroll] for (int ring = 0; ring < 2; ++ring)
        {
            float2 r = (ring == 0 ? 5.0f : 12.0f) * toMv;
            int2 rx = int2(max(1.0f, round(r.x)), 0);
            int2 ry = int2(0, max(1.0f, round(r.y)));
            spread = max(spread, length(MotionAt(q + rx, mhi) - m0));
            spread = max(spread, length(MotionAt(q - rx, mhi) - m0));
            spread = max(spread, length(MotionAt(q + ry, mhi) - m0));
            spread = max(spread, length(MotionAt(q - ry, mhi) - m0));
        }
        // In the network's own pixels: under one pixel of disagreement is
        // noise in the field, four and more is a border.
        float border = saturate((spread / max(toMv.x, 1e-4f) - 0.75f) / 3.25f);
        gain = gHalo + (1.0f - gHalo) * 0.6f * border;
    }
    gDst[p] = float4(d - gain * (e / 9.0f), 0.0f);
}
)hlsl";

// Tuned on the captured frame (see the header): wide enough to take the
// whole rim into the region's average, and a range step that keeps white
// shirt and green grass apart while grass blades still count as grass.
static constexpr int   kHaloRadius = 64;
static constexpr int   kHaloStep = 4;
static constexpr float kHaloSigmaR = 0.06f;

static ID3D12RootSignature  *g_halo_rs;
static ID3D12PipelineState  *g_halo_blur_pso;
static ID3D12PipelineState  *g_halo_fix_pso;
static ID3D12DescriptorHeap *g_halo_heap;          // three sets of 4 SRV + 1 UAV
static ID3D12Resource       *g_halo_bound[3][5];
static bool                  g_halo_pipeline_failed;

struct HaloTargets
{
    ID3D12Resource *th = nullptr;    // horizontal pass
    ID3D12Resource *tb = nullptr;    // Tbil
    ID3D12Resource *dc = nullptr;    // the corrected change the composite reads
    ID3D12Resource *raw = nullptr;   // full-res network output (Enhance off only)
    UINT w = 0, hgt = 0;
    UINT raw_w = 0, raw_h = 0;
};
static HaloTargets g_halo_t;

static void CloseHaloTargets()
{
    for (ID3D12Resource **r : { &g_halo_t.th, &g_halo_t.tb, &g_halo_t.dc, &g_halo_t.raw })
        if (*r != nullptr) { (*r)->Release(); *r = nullptr; }
    g_halo_t = HaloTargets{};
    for (auto &set : g_halo_bound)
        for (auto &slot : set) slot = nullptr;
    // The residual set may point at the released delta.
    g_res_delta_bound = nullptr;
    g_res_native_bound = nullptr;
}

static bool EnsureHaloPipeline()
{
    if (g_halo_fix_pso != nullptr) return true;
    if (g_halo_pipeline_failed) return false;
    g_halo_pipeline_failed = true;   // until everything below has worked

    HMODULE compiler = LoadLibraryW(L"d3dcompiler_47.dll");
    auto compile = compiler ? reinterpret_cast<PFN_D3DCompile_>(GetProcAddress(compiler, "D3DCompile")) : nullptr;
    HMODULE d3d12 = GetModuleHandleW(L"d3d12.dll");
    auto serialize = d3d12 ? reinterpret_cast<PFN_D3D12SerializeRootSignature_>(
                                 GetProcAddress(d3d12, "D3D12SerializeRootSignature")) : nullptr;
    if (compile == nullptr || serialize == nullptr) { Log("[halo] compiler unavailable"); return false; }

    ID3DBlob *blur = nullptr, *fix = nullptr, *errors = nullptr;
    HRESULT hr = compile(kHaloBlurHlsl, sizeof(kHaloBlurHlsl) - 1, "halo_blur.hlsl", nullptr, nullptr,
                         "CSMain", "cs_5_0", 0, 0, &blur, &errors);
    if (FAILED(hr) || blur == nullptr)
    {
        Log("[halo] blur shader failed 0x%08X: %s", hr,
            errors ? static_cast<const char *>(errors->GetBufferPointer()) : "(no log)");
        if (errors) errors->Release();
        return false;
    }
    if (errors) { errors->Release(); errors = nullptr; }
    hr = compile(kHaloFixHlsl, sizeof(kHaloFixHlsl) - 1, "halo_fix.hlsl", nullptr, nullptr,
                 "CSMain", "cs_5_0", 0, 0, &fix, &errors);
    if (FAILED(hr) || fix == nullptr)
    {
        Log("[halo] fix shader failed 0x%08X: %s", hr,
            errors ? static_cast<const char *>(errors->GetBufferPointer()) : "(no log)");
        if (errors) errors->Release();
        blur->Release();
        return false;
    }
    if (errors) { errors->Release(); errors = nullptr; }

    D3D12_DESCRIPTOR_RANGE ranges[2] = {};
    ranges[0].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
    ranges[0].NumDescriptors = 4;
    ranges[0].OffsetInDescriptorsFromTableStart = 0;
    ranges[1].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_UAV;
    ranges[1].NumDescriptors = 1;
    ranges[1].OffsetInDescriptorsFromTableStart = 4;
    D3D12_ROOT_PARAMETER params[2] = {};
    params[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
    params[0].Constants.Num32BitValues = 10;
    params[0].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
    params[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
    params[1].DescriptorTable.NumDescriptorRanges = 2;
    params[1].DescriptorTable.pDescriptorRanges = ranges;
    params[1].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
    D3D12_ROOT_SIGNATURE_DESC rsd = {};
    rsd.NumParameters = _countof(params);
    rsd.pParameters = params;

    ID3DBlob *rs_blob = nullptr;
    hr = serialize(&rsd, D3D_ROOT_SIGNATURE_VERSION_1, &rs_blob, &errors);
    if (errors) errors->Release();
    if (FAILED(hr) || rs_blob == nullptr) { Log("[halo] root signature failed 0x%08X", hr); blur->Release(); fix->Release(); return false; }
    hr = h.dev->CreateRootSignature(0, rs_blob->GetBufferPointer(), rs_blob->GetBufferSize(),
                                    __uuidof(ID3D12RootSignature), reinterpret_cast<void **>(&g_halo_rs));
    rs_blob->Release();
    if (FAILED(hr)) { Log("[halo] CreateRootSignature failed 0x%08X", hr); blur->Release(); fix->Release(); return false; }

    D3D12_COMPUTE_PIPELINE_STATE_DESC pd = {};
    pd.pRootSignature = g_halo_rs;
    pd.CS.pShaderBytecode = blur->GetBufferPointer();
    pd.CS.BytecodeLength = blur->GetBufferSize();
    hr = h.dev->CreateComputePipelineState(&pd, __uuidof(ID3D12PipelineState), reinterpret_cast<void **>(&g_halo_blur_pso));
    blur->Release();
    if (FAILED(hr)) { Log("[halo] blur pipeline failed 0x%08X", hr); fix->Release(); return false; }
    pd.CS.pShaderBytecode = fix->GetBufferPointer();
    pd.CS.BytecodeLength = fix->GetBufferSize();
    hr = h.dev->CreateComputePipelineState(&pd, __uuidof(ID3D12PipelineState), reinterpret_cast<void **>(&g_halo_fix_pso));
    fix->Release();
    if (FAILED(hr)) { Log("[halo] fix pipeline failed 0x%08X", hr); return false; }

    D3D12_DESCRIPTOR_HEAP_DESC hd = {};
    hd.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
    hd.NumDescriptors = 15;
    hd.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
    hr = h.dev->CreateDescriptorHeap(&hd, __uuidof(ID3D12DescriptorHeap), reinterpret_cast<void **>(&g_halo_heap));
    if (FAILED(hr)) { Log("[halo] descriptor heap failed 0x%08X", hr); return false; }

    g_halo_pipeline_failed = false;
    Log("[halo] pipeline ready (cross-bilateral r=%d step=%d, sigma_r=%.2f, motion-aware borders)", kHaloRadius, kHaloStep, kHaloSigmaR);
    return true;
}

static ID3D12Resource *MakeHaloTexture(UINT w, UINT hgt, DXGI_FORMAT format)
{
    D3D12_HEAP_PROPERTIES def = {};
    def.Type = D3D12_HEAP_TYPE_DEFAULT;
    D3D12_RESOURCE_DESC td = {};
    td.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    td.Width = w; td.Height = hgt; td.DepthOrArraySize = 1; td.MipLevels = 1;
    td.Format = format; td.SampleDesc.Count = 1;
    td.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN;
    td.Flags = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
    ID3D12Resource *r = nullptr;
    if (FAILED(h.dev->CreateCommittedResource(&def, D3D12_HEAP_FLAG_NONE, &td, D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
                                              nullptr, __uuidof(ID3D12Resource), reinterpret_cast<void **>(&r))))
        return nullptr;
    return r;
}

// The three float targets at w x hgt, plus - for a full-resolution network -
// the RGBA8 texture it writes instead of the output. Sizes only change with
// an RNSZ, and ReleaseVideoTextures closes these on every one of those after
// the GPU has drained, so nothing here is freed while in flight.
static bool EnsureHaloTargets(UINT w, UINT hgt, bool need_raw)
{
    if (g_halo_t.dc == nullptr || g_halo_t.w != w || g_halo_t.hgt != hgt)
    {
        ID3D12Resource *raw = g_halo_t.raw;
        const UINT raw_w = g_halo_t.raw_w, raw_h = g_halo_t.raw_h;
        g_halo_t.raw = nullptr;
        CloseHaloTargets();
        g_halo_t.raw = raw;
        g_halo_t.raw_w = raw_w;
        g_halo_t.raw_h = raw_h;
        g_halo_t.th = MakeHaloTexture(w, hgt, DXGI_FORMAT_R16G16B16A16_FLOAT);
        g_halo_t.tb = MakeHaloTexture(w, hgt, DXGI_FORMAT_R16G16B16A16_FLOAT);
        g_halo_t.dc = MakeHaloTexture(w, hgt, DXGI_FORMAT_R16G16B16A16_FLOAT);
        if (g_halo_t.th == nullptr || g_halo_t.tb == nullptr || g_halo_t.dc == nullptr)
        {
            Log("[halo] %ux%u targets failed - reduction off", w, hgt);
            CloseHaloTargets();
            return false;
        }
        g_halo_t.w = w;
        g_halo_t.hgt = hgt;
        Log("[halo] targets ready at %ux%u", w, hgt);
    }
    if (need_raw && (g_halo_t.raw == nullptr || g_halo_t.raw_w != w || g_halo_t.raw_h != hgt))
    {
        if (g_halo_t.raw != nullptr) { g_halo_t.raw->Release(); g_halo_t.raw = nullptr; }
        g_halo_t.raw = MakeHaloTexture(w, hgt, DXGI_FORMAT_R8G8B8A8_UNORM);
        if (g_halo_t.raw == nullptr) { Log("[halo] %ux%u network target failed", w, hgt); return false; }
        g_halo_t.raw_w = w;
        g_halo_t.raw_h = hgt;
    }
    return true;
}

static void BindHaloSet(UINT slot, ID3D12Resource *t0, ID3D12Resource *t1, ID3D12Resource *t2,
                        ID3D12Resource *mv, ID3D12Resource *u0)
{
    ID3D12Resource *want[5] = { t0, t1, t2, mv, u0 };
    if (memcmp(g_halo_bound[slot], want, sizeof(want)) == 0) return;
    const UINT stride = h.dev->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
    D3D12_CPU_DESCRIPTOR_HANDLE cpu = g_halo_heap->GetCPUDescriptorHandleForHeapStart();
    cpu.ptr += static_cast<SIZE_T>(slot) * 5 * stride;
    for (int i = 0; i < 4; ++i)
    {
        D3D12_SHADER_RESOURCE_VIEW_DESC sd = {};
        // The motion slot may be empty: a null view reads as zero motion,
        // which leaves the reduction exactly the slider.
        sd.Format = want[i] != nullptr ? want[i]->GetDesc().Format : DXGI_FORMAT_R16G16_FLOAT;
        sd.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
        sd.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
        sd.Texture2D.MipLevels = 1;
        h.dev->CreateShaderResourceView(want[i], &sd, cpu);
        cpu.ptr += stride;
    }
    D3D12_UNORDERED_ACCESS_VIEW_DESC ud = {};
    ud.Format = u0->GetDesc().Format;
    ud.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
    h.dev->CreateUnorderedAccessView(u0, nullptr, &ud, cpu);
    memcpy(g_halo_bound[slot], want, sizeof(want));
}

static UINT g_halo_mv_w, g_halo_mv_h;   // the motion bound for this frame's passes

static void HaloDispatch(ID3D12PipelineState *pso, UINT slot, UINT w, UINT hgt,
                         int dx, int dy, uint32_t mode, float param)
{
    const UINT stride = h.dev->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
    ID3D12DescriptorHeap *heaps[] = { g_halo_heap };
    h.list->SetDescriptorHeaps(1, heaps);
    h.list->SetComputeRootSignature(g_halo_rs);
    h.list->SetPipelineState(pso);
    UINT32 c[10] = { w, hgt, static_cast<UINT32>(dx), static_cast<UINT32>(dy),
                     static_cast<UINT32>(kHaloRadius), static_cast<UINT32>(kHaloStep), mode, 0,
                     g_halo_mv_w, g_halo_mv_h };
    memcpy(&c[7], &param, sizeof(float));
    h.list->SetComputeRoot32BitConstants(0, 10, c, 0);
    D3D12_GPU_DESCRIPTOR_HANDLE gpu = g_halo_heap->GetGPUDescriptorHandleForHeapStart();
    gpu.ptr += static_cast<UINT64>(slot) * 5 * stride;
    h.list->SetComputeRootDescriptorTable(1, gpu);
    h.list->Dispatch((w + 7) / 8, (hgt + 7) / 8, 1);
}

// Records the three passes into the open list. nr_in and nr_out must be
// shader resources already; afterwards g_halo_t.dc is a shader resource too,
// for the composite, and HaloDone puts it back. mv is the network's motion
// texture (a shader resource, mv_w x mv_h) or null.
static void HaloFilter(ID3D12Resource *nr_in, ID3D12Resource *nr_out, UINT w, UINT hgt, float halo,
                       ID3D12Resource *mv, UINT mv_w, UINT mv_h)
{
    g_halo_mv_w = mv != nullptr ? mv_w : 0u;
    g_halo_mv_h = mv != nullptr ? mv_h : 0u;
    BindHaloSet(0, nr_in, nr_out, nr_in, mv, g_halo_t.th);
    HaloDispatch(g_halo_blur_pso, 0, w, hgt, 1, 0, 0u, kHaloSigmaR);
    D3D12_RESOURCE_BARRIER b1 = Transition(g_halo_t.th, D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
                                           D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
    h.list->ResourceBarrier(1, &b1);

    BindHaloSet(1, nr_in, g_halo_t.th, nr_in, mv, g_halo_t.tb);
    HaloDispatch(g_halo_blur_pso, 1, w, hgt, 0, 1, 1u, kHaloSigmaR);
    D3D12_RESOURCE_BARRIER b2 = Transition(g_halo_t.tb, D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
                                           D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
    h.list->ResourceBarrier(1, &b2);

    BindHaloSet(2, nr_in, nr_out, g_halo_t.tb, mv, g_halo_t.dc);
    HaloDispatch(g_halo_fix_pso, 2, w, hgt, 0, 0, 2u, halo);
    D3D12_RESOURCE_BARRIER b3[] = {
        Transition(g_halo_t.dc, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE),
        Transition(g_halo_t.th, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_UNORDERED_ACCESS),
        Transition(g_halo_t.tb, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_UNORDERED_ACCESS),
    };
    h.list->ResourceBarrier(_countof(b3), b3);
}

static void HaloDone()
{
    D3D12_RESOURCE_BARRIER back = Transition(g_halo_t.dc, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,
                                             D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
    h.list->ResourceBarrier(1, &back);
}
