// Sharpness, edge smoothing and the COLOURS card - the LIVE FLOW sliders of
// those names.
//
// One compute pass over the finished picture, after the composite (and after
// halo reduction): v.output is copied aside and the pass writes it back. With
// sharpness and edge smoothing at 0 and every colour at 0 the pass is not
// recorded at all - the output is then exactly what it was before any of
// these sliders existed.
//
// The order, and why:
//
//   1. Stability     the pixel is pulled a little towards where it was last
//                    frame, found through the motion vectors the network was
//                    given (current -> previous, work-res pixels) and clamped
//                    to what the current neighbourhood holds, so a wrong vector
//                    can never paint an old colour in. It is what keeps a
//                    sharpened picture from shimmering and a smoothed edge from
//                    crawling. Only while sharpness or edge smoothing is on,
//                    never across a reset, and less the faster things move.
//   2. Edge smoothing  along an edge - never across it - the pixel is averaged
//                    with its neighbours on the same edge. Stair-steps and
//                    crawling on diagonals soften while the edge keeps its
//                    sharpness across. Flat areas and fine texture below the
//                    edge threshold are not touched.
//   3. History       what 1 and 2 made is next frame's history - before the
//                    sharpening and the colours, so neither can feed back into
//                    itself frame after frame.
//   4. Sharpness     contrast-adaptive sharpening (the FidelityFX CAS shape):
//                    each pixel is pushed away from its four neighbours by an
//                    amount that shrinks where the neighbourhood is already
//                    close to black or white, so it adds bite to soft texture
//                    without the overshoot rims plain unsharp masking draws.
//                    Held back on the edges step 2 just straightened, and in
//                    fast motion, where sharpening only draws crawling edges
//                    on what the eye cannot follow anyway.
//   5. Colours       brightness (a midtone curve: black and white stay put),
//                    contrast (an S-curve around mid-grey), saturation (gentler
//                    on colours that are already strong) and warmth (towards
//                    amber or blue at the same brightness). Last, so the
//                    comparison wipe's left half - the capture as it came in -
//                    is the honest "before".

static const char kFinishHlsl[] = R"hlsl(
Texture2D<float4>   gSrc  : register(t0);   // the composite, copied aside
Texture2D<float2>   gMv   : register(t1);   // current -> previous, in its own pixels
Texture2D<float4>   gHist : register(t2);   // last frame's steps 1-2
RWTexture2D<float4> gDst  : register(u0);   // the output
RWTexture2D<float4> gNext : register(u1);   // this frame's steps 1-2 (next frame's history)
SamplerState        gLin  : register(s0);
cbuffer PC : register(b0)
{
    uint gW; uint gH; float gSharp; float gEdge;
    uint gMvW; uint gMvH; uint gFlags; float gBright;
    float gContrast; float gSat; float gWarm; float gPad;
};
// gFlags: 1 = the history is last frame, at this size; 2 = the colours are on.
static const float3 kLuma = float3(0.299f, 0.587f, 0.114f);
static const float3 kLuma709 = float3(0.2126f, 0.7152f, 0.0722f);
float L(float3 c) { return dot(c, kLuma); }

float3 Grade(float3 c)
{
    c = saturate(c);
    // Brightness: a power curve. Mid-grey moves, black and white do not, so
    // nothing clips and nothing turns grey.
    if (gBright != 0.0f)
        c = pow(max(c, 1e-5f), exp2(-gBright * 0.7f));
    // Contrast: towards a smoothstep S-curve, or towards mid-grey.
    if (gContrast > 0.0f)
        c = lerp(c, c * c * (3.0f - 2.0f * c), gContrast * 0.85f);
    else if (gContrast < 0.0f)
        c = lerp(c, 0.5f + (c - 0.5f) * 0.5f, -gContrast);
    // Saturation around the pixel's own luminance.
    if (gSat != 0.0f)
    {
        float l = dot(c, kLuma709);
        float chroma = max(c.r, max(c.g, c.b)) - min(c.r, min(c.g, c.b));
        float gain = gSat > 0.0f ? 1.0f + gSat * (1.0f - 0.6f * saturate(chroma)) : 1.0f + gSat;
        c = l + (c - l) * gain;
    }
    // Warmth: amber against blue, then back to the same luminance.
    if (gWarm != 0.0f)
    {
        float l0 = dot(c, kLuma709);
        float3 t = c * float3(1.0f + 0.10f * gWarm, 1.0f + 0.015f * gWarm, 1.0f - 0.10f * gWarm);
        float l1 = dot(t, kLuma709);
        c = l1 > 1e-4f ? t * (l0 / l1) : t;
    }
    return saturate(c);
}

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= gW || id.y >= gH) return;
    int2 p = int2(id.xy);
    int2 hi = int2(int(gW) - 1, int(gH) - 1);
    float2 size = float2(gW, gH);
    float3 c  = gSrc[p].rgb;
    float3 n  = gSrc[clamp(p + int2(0, -1), int2(0, 0), hi)].rgb;
    float3 s  = gSrc[clamp(p + int2(0, 1), int2(0, 0), hi)].rgb;
    float3 e  = gSrc[clamp(p + int2(1, 0), int2(0, 0), hi)].rgb;
    float3 w  = gSrc[clamp(p + int2(-1, 0), int2(0, 0), hi)].rgb;
    float3 mn = min(c, min(min(n, s), min(e, w)));
    float3 mx = max(c, max(max(n, s), max(e, w)));

    // The motion under this pixel, in output pixels.
    float2 mv = 0.0f;
    if (gMvW > 0 && gMvH > 0)
    {
        int2 q = clamp(int2((float2(p) + 0.5f) * float2(gMvW, gMvH) / size),
                       int2(0, 0), int2(int(gMvW) - 1, int(gMvH) - 1));
        mv = gMv[q] * size / float2(gMvW, gMvH);
        if (!all(isfinite(mv))) mv = 0.0f;
    }
    float speed = length(mv);

    // 1. Stability.
    float3 center = c;
    float temporal = 0.30f * gEdge + 0.15f * gSharp;
    if ((gFlags & 1u) != 0u && temporal > 0.0f)
    {
        float2 prev = float2(p) + 0.5f + mv;
        if (all(prev >= 0.0f) && all(prev <= size))
        {
            float3 h = gHist.SampleLevel(gLin, prev / size, 0).rgb;
            float3 held = clamp(h, mn, mx);
            // A history that needed a lot of clamping was the wrong place;
            // a fast mover has too little of it left to trust.
            float trust = saturate(1.0f - length(h - held) * 12.0f) *
                          (1.0f - saturate((speed - 6.0f) / 26.0f));
            center = lerp(c, held, temporal * trust);
        }
    }

    // 2. Edge smoothing: the luma gradient (Sobel) gives the edge's normal;
    // the tangent is where the averaging goes.
    float3 aa = center;
    float edgeAmount = 0.0f;
    if (gEdge > 0.0f)
    {
        float lnw = L(gSrc[clamp(p + int2(-1, -1), int2(0, 0), hi)].rgb);
        float lne = L(gSrc[clamp(p + int2(1, -1), int2(0, 0), hi)].rgb);
        float lsw = L(gSrc[clamp(p + int2(-1, 1), int2(0, 0), hi)].rgb);
        float lse = L(gSrc[clamp(p + int2(1, 1), int2(0, 0), hi)].rgb);
        float ln = L(n), ls = L(s), le = L(e), lw = L(w), lc = L(c);
        float gx = (lne + 2.0f * le + lse) - (lnw + 2.0f * lw + lsw);
        float gy = (lsw + 2.0f * ls + lse) - (lnw + 2.0f * ln + lne);
        float grad = sqrt(gx * gx + gy * gy);
        float range = max(max(max(ln, ls), max(le, lw)), lc) - min(min(min(ln, ls), min(le, lw)), lc);
        if (grad > 0.06f && range > 0.04f)
        {
            float2 tangent = float2(-gy, gx) / grad;
            float2 texel = 1.0f / size;
            float2 uv = (float2(p) + 0.5f) * texel;
            float3 a = gSrc.SampleLevel(gLin, uv + tangent * texel * 0.75f, 0).rgb;
            float3 b = gSrc.SampleLevel(gLin, uv - tangent * texel * 0.75f, 0).rgb;
            float3 a2 = gSrc.SampleLevel(gLin, uv + tangent * texel * 1.75f, 0).rgb;
            float3 b2 = gSrc.SampleLevel(gLin, uv - tangent * texel * 1.75f, 0).rgb;
            float3 along = (center * 2.0f + a * 2.0f + b * 2.0f + a2 + b2) / 8.0f;
            edgeAmount = gEdge * saturate((grad - 0.06f) * 6.0f);
            aa = lerp(center, along, edgeAmount);
        }
    }

    // 3. History.
    gNext[p] = float4(aa, 1.0f);

    // 4. Sharpness (CAS): the weight is negative, scaled by how much headroom
    // the neighbourhood leaves before clipping. At rest and away from edges it
    // is exactly the sharpening this slider always gave.
    float3 result = aa;
    if (gSharp > 0.0f)
    {
        float3 mnc = min(mn, aa);
        float3 mxc = max(mx, aa);
        float3 amp = sqrt(saturate(min(mnc, 1.0f - mxc) / max(mxc, 1e-4f)));
        // CAS's own range: -1/8 .. -1/5. Steeper than -1/5 and the divisor
        // below reaches zero on a flat patch (amp = 1).
        float peak = -1.0f / lerp(8.0f, 5.0f, gSharp);
        float3 wgt = amp * peak;
        float3 cas = saturate((aa + (n + s + e + w) * wgt) / (1.0f + 4.0f * wgt));
        float keep = lerp(1.0f, 0.45f, saturate((speed - 2.0f) / 22.0f)) * (1.0f - edgeAmount);
        result = lerp(aa, cas, keep);
    }

    // 5. Colours.
    if ((gFlags & 2u) != 0u) result = Grade(result);
    gDst[p] = float4(result, 1.0f);
}
)hlsl";

// The COLOURS card: brightness, contrast, saturation and warmth, -1..1 each,
// 0 = untouched. They travel in the resize command's `preset` slot - dead in
// the runtime since 310.8.0 and read by nothing in this worker - one byte
// each around 128, lowest byte brightness; RESIZE_FLAG_COLORS in the flags
// says the slot means something, so an older client's zero there can never
// read as "everything at -1". NS_COLORS (the same word, hex) is what a worker
// started before any resize reads.
static constexpr uint32_t RESIZE_FLAG_COLORS = 0x10000u;
static float g_colors[4] = { 0.0f, 0.0f, 0.0f, 0.0f };
static bool  g_colors_read = false;

static void ColorsFromWord(uint32_t word)
{
    for (int i = 0; i < 4; ++i)
    {
        const int byte = static_cast<int>((word >> (8 * i)) & 0xFFu);
        const float v = byte == 0 ? 0.0f : static_cast<float>(byte - 128) / 127.0f;
        g_colors[i] = v < -1.0f ? -1.0f : (v > 1.0f ? 1.0f : v);
    }
}

static void ReadColorsOnce()
{
    if (g_colors_read) return;
    g_colors_read = true;
    char buf[16] = {};
    const DWORD got = GetEnvironmentVariableA("NS_COLORS", buf, sizeof(buf));
    if (got > 0 && got < sizeof(buf))
    {
        ColorsFromWord(static_cast<uint32_t>(strtoul(buf, nullptr, 16)));
        if (g_colors[0] != 0.0f || g_colors[1] != 0.0f || g_colors[2] != 0.0f || g_colors[3] != 0.0f)
            Log("[colors] brightness %+.2f, contrast %+.2f, saturation %+.2f, warmth %+.2f from NS_COLORS",
                g_colors[0], g_colors[1], g_colors[2], g_colors[3]);
    }
}

static bool ColorsActive()
{
    ReadColorsOnce();
    return g_colors[0] != 0.0f || g_colors[1] != 0.0f || g_colors[2] != 0.0f || g_colors[3] != 0.0f;
}

static void ColorsFromResize(uint32_t flags, uint32_t word)
{
    if ((flags & RESIZE_FLAG_COLORS) == 0) return;
    ReadColorsOnce();
    float before[4];
    memcpy(before, g_colors, sizeof(before));
    ColorsFromWord(word);
    if (memcmp(before, g_colors, sizeof(before)) != 0)
        Log("[colors] brightness %+.2f, contrast %+.2f, saturation %+.2f, warmth %+.2f",
            g_colors[0], g_colors[1], g_colors[2], g_colors[3]);
}

static ID3D12RootSignature  *g_finish_rs;
static ID3D12PipelineState  *g_finish_pso;
static ID3D12DescriptorHeap *g_finish_heap;          // two sets of 3 SRV + 2 UAV, one per history
static ID3D12Resource       *g_finish_bound[2][5];
static ID3D12Resource       *g_finish_tmp;          // copy of the composite, NPSR at rest
static ID3D12Resource       *g_finish_hist[2];      // ping-pong history, NPSR at rest
static UINT                  g_finish_w, g_finish_h;
static UINT                  g_finish_ping;         // which history is last frame's
static bool                  g_finish_hist_valid;
static bool                  g_finish_failed;

static void CloseFinishTargets()
{
    if (g_finish_tmp != nullptr) { g_finish_tmp->Release(); g_finish_tmp = nullptr; }
    for (auto &hist : g_finish_hist)
        if (hist != nullptr) { hist->Release(); hist = nullptr; }
    g_finish_w = g_finish_h = 0;
    g_finish_ping = 0;
    g_finish_hist_valid = false;
    for (auto &set : g_finish_bound)
        for (auto &slot : set) slot = nullptr;
}

// A frame that went out without the pass: the history no longer holds the
// frame before the next one.
static void FinishSkipped()
{
    g_finish_hist_valid = false;
}

static bool EnsureFinishPipeline()
{
    if (g_finish_pso != nullptr) return true;
    if (g_finish_failed) return false;
    g_finish_failed = true;
    HMODULE compiler = LoadLibraryW(L"d3dcompiler_47.dll");
    auto compile = compiler ? reinterpret_cast<PFN_D3DCompile_>(GetProcAddress(compiler, "D3DCompile")) : nullptr;
    HMODULE d3d12 = GetModuleHandleW(L"d3d12.dll");
    auto serialize = d3d12 ? reinterpret_cast<PFN_D3D12SerializeRootSignature_>(
                                 GetProcAddress(d3d12, "D3D12SerializeRootSignature")) : nullptr;
    if (compile == nullptr || serialize == nullptr) { Log("[finish] compiler unavailable"); return false; }
    ID3DBlob *code = nullptr, *errors = nullptr;
    HRESULT hr = compile(kFinishHlsl, sizeof(kFinishHlsl) - 1, "finish.hlsl", nullptr, nullptr,
                         "CSMain", "cs_5_0", 0, 0, &code, &errors);
    if (FAILED(hr) || code == nullptr)
    {
        Log("[finish] shader failed 0x%08X: %s", hr,
            errors ? static_cast<const char *>(errors->GetBufferPointer()) : "(no log)");
        if (errors) errors->Release();
        return false;
    }
    if (errors) { errors->Release(); errors = nullptr; }
    D3D12_DESCRIPTOR_RANGE ranges[2] = {};
    ranges[0].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
    ranges[0].NumDescriptors = 3;
    ranges[1].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_UAV;
    ranges[1].NumDescriptors = 2;
    ranges[1].OffsetInDescriptorsFromTableStart = 3;
    D3D12_ROOT_PARAMETER params[2] = {};
    params[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
    params[0].Constants.Num32BitValues = 12;
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
    if (FAILED(hr) || rs_blob == nullptr) { Log("[finish] root signature failed 0x%08X", hr); code->Release(); return false; }
    hr = h.dev->CreateRootSignature(0, rs_blob->GetBufferPointer(), rs_blob->GetBufferSize(),
                                    __uuidof(ID3D12RootSignature), reinterpret_cast<void **>(&g_finish_rs));
    rs_blob->Release();
    if (FAILED(hr)) { code->Release(); return false; }
    D3D12_COMPUTE_PIPELINE_STATE_DESC pd = {};
    pd.pRootSignature = g_finish_rs;
    pd.CS.pShaderBytecode = code->GetBufferPointer();
    pd.CS.BytecodeLength = code->GetBufferSize();
    hr = h.dev->CreateComputePipelineState(&pd, __uuidof(ID3D12PipelineState), reinterpret_cast<void **>(&g_finish_pso));
    code->Release();
    if (FAILED(hr)) { Log("[finish] pipeline failed 0x%08X", hr); return false; }
    D3D12_DESCRIPTOR_HEAP_DESC hd = {};
    hd.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
    hd.NumDescriptors = 10;
    hd.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
    if (FAILED(h.dev->CreateDescriptorHeap(&hd, __uuidof(ID3D12DescriptorHeap), reinterpret_cast<void **>(&g_finish_heap))))
        return false;
    g_finish_failed = false;
    Log("[finish] pipeline ready (motion-aware stability, edge smoothing, CAS sharpening, colours)");
    return true;
}

static ID3D12Resource *MakeFinishTexture(UINT w, UINT hgt, bool uav)
{
    D3D12_HEAP_PROPERTIES def = {};
    def.Type = D3D12_HEAP_TYPE_DEFAULT;
    D3D12_RESOURCE_DESC td = {};
    td.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    td.Width = w; td.Height = hgt; td.DepthOrArraySize = 1; td.MipLevels = 1;
    td.Format = DXGI_FORMAT_R8G8B8A8_UNORM; td.SampleDesc.Count = 1;
    td.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN;
    if (uav) td.Flags = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
    ID3D12Resource *r = nullptr;
    if (FAILED(h.dev->CreateCommittedResource(&def, D3D12_HEAP_FLAG_NONE, &td,
            D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, nullptr, __uuidof(ID3D12Resource),
            reinterpret_cast<void **>(&r))))
        return nullptr;
    return r;
}

static bool EnsureFinishTargets(UINT w, UINT hgt)
{
    if (g_finish_tmp != nullptr && g_finish_hist[0] != nullptr && g_finish_hist[1] != nullptr &&
        g_finish_w == w && g_finish_h == hgt)
        return true;
    CloseFinishTargets();
    g_finish_tmp = MakeFinishTexture(w, hgt, false);
    g_finish_hist[0] = MakeFinishTexture(w, hgt, true);
    g_finish_hist[1] = MakeFinishTexture(w, hgt, true);
    if (g_finish_tmp == nullptr || g_finish_hist[0] == nullptr || g_finish_hist[1] == nullptr)
    { Log("[finish] %ux%u targets failed", w, hgt); CloseFinishTargets(); return false; }
    g_finish_w = w;
    g_finish_h = hgt;
    return true;
}

static void BindFinishSet(UINT set, ID3D12Resource *mv, ID3D12Resource *output)
{
    ID3D12Resource *want[5] = { g_finish_tmp, mv, g_finish_hist[set], output, g_finish_hist[1 - set] };
    if (memcmp(g_finish_bound[set], want, sizeof(want)) == 0) return;
    const UINT stride = h.dev->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
    D3D12_CPU_DESCRIPTOR_HANDLE cpu = g_finish_heap->GetCPUDescriptorHandleForHeapStart();
    cpu.ptr += static_cast<SIZE_T>(set) * 5 * stride;
    D3D12_SHADER_RESOURCE_VIEW_DESC sd = {};
    sd.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
    sd.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
    sd.Texture2D.MipLevels = 1;
    sd.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    h.dev->CreateShaderResourceView(g_finish_tmp, &sd, cpu);
    cpu.ptr += stride;
    // A null view when there is no motion texture: the shader then reads
    // zero motion, which is "nothing moved" - never wrong, only weaker.
    sd.Format = DXGI_FORMAT_R16G16_FLOAT;
    h.dev->CreateShaderResourceView(mv, &sd, cpu);
    cpu.ptr += stride;
    sd.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    h.dev->CreateShaderResourceView(g_finish_hist[set], &sd, cpu);
    cpu.ptr += stride;
    D3D12_UNORDERED_ACCESS_VIEW_DESC ud = {};
    ud.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    ud.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
    h.dev->CreateUnorderedAccessView(output, nullptr, &ud, cpu);
    cpu.ptr += stride;
    h.dev->CreateUnorderedAccessView(g_finish_hist[1 - set], nullptr, &ud, cpu);
    memcpy(g_finish_bound[set], want, sizeof(want));
}

// Records the pass at the end of the evaluate list. output is in
// UNORDERED_ACCESS on entry and on exit, as everywhere downstream expects.
// mv is the network's motion texture (NON_PIXEL_SHADER_RESOURCE, as the
// network leaves it), mv_w x mv_h its size; reset says the history belongs to
// another scene and must not be used.
static void FinishOutput(ID3D12Resource *output, UINT w, UINT hgt, float sharp, float edge,
                         ID3D12Resource *mv, UINT mv_w, UINT mv_h, bool reset)
{
    const UINT set = g_finish_ping;          // history[set] is last frame's
    ID3D12Resource *next = g_finish_hist[1 - set];
    D3D12_RESOURCE_BARRIER pre[] = {
        Transition(output, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_COPY_SOURCE),
        Transition(g_finish_tmp, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_COPY_DEST),
    };
    h.list->ResourceBarrier(_countof(pre), pre);
    h.list->CopyResource(g_finish_tmp, output);
    D3D12_RESOURCE_BARRIER mid[] = {
        Transition(output, D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATE_UNORDERED_ACCESS),
        Transition(g_finish_tmp, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE),
        Transition(next, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_UNORDERED_ACCESS),
    };
    h.list->ResourceBarrier(_countof(mid), mid);
    BindFinishSet(set, mv, output);
    ID3D12DescriptorHeap *heaps[] = { g_finish_heap };
    h.list->SetDescriptorHeaps(1, heaps);
    h.list->SetComputeRootSignature(g_finish_rs);
    h.list->SetPipelineState(g_finish_pso);
    const bool colors = ColorsActive();
    const UINT flags = ((g_finish_hist_valid && !reset) ? 1u : 0u) | (colors ? 2u : 0u);
    UINT32 c[12] = { w, hgt, 0, 0, mv != nullptr ? mv_w : 0u, mv != nullptr ? mv_h : 0u, flags, 0, 0, 0, 0, 0 };
    memcpy(&c[2], &sharp, sizeof(float));
    memcpy(&c[3], &edge, sizeof(float));
    memcpy(&c[7], &g_colors[0], sizeof(float));
    memcpy(&c[8], &g_colors[1], sizeof(float));
    memcpy(&c[9], &g_colors[2], sizeof(float));
    memcpy(&c[10], &g_colors[3], sizeof(float));
    h.list->SetComputeRoot32BitConstants(0, 12, c, 0);
    D3D12_GPU_DESCRIPTOR_HANDLE gpu = g_finish_heap->GetGPUDescriptorHandleForHeapStart();
    gpu.ptr += static_cast<UINT64>(set) * 5 *
               h.dev->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
    h.list->SetComputeRootDescriptorTable(1, gpu);
    h.list->Dispatch((w + 7) / 8, (hgt + 7) / 8, 1);
    D3D12_RESOURCE_BARRIER post = Transition(next, D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
                                             D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
    h.list->ResourceBarrier(1, &post);
    // The pass wrote history[1 - set]; next frame reads it.
    g_finish_ping = 1 - set;
    g_finish_hist_valid = true;
}
