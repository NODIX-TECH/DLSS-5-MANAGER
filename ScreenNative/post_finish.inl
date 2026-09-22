// Sharpness and edge smoothing - the two LIVE FLOW sliders of those names.
//
// One compute pass over the finished picture, after the composite (and after
// halo reduction): v.output is copied aside and the pass writes it back.
// Both at 0 and the pass is not recorded at all - the output is then exactly
// what it was before these sliders existed.
//
//   Sharpness       contrast-adaptive sharpening (the FidelityFX CAS shape):
//                   each pixel is pushed away from its four neighbours by an
//                   amount that shrinks where the neighbourhood is already
//                   close to black or white, so it adds bite to soft texture
//                   without the overshoot rims plain unsharp masking draws.
//   Edge smoothing  edge-directed smoothing: along an edge - never across it -
//                   the pixel is averaged with its neighbours on the same
//                   edge. Stair-steps and crawling on diagonals soften while
//                   the edge keeps its sharpness across. Flat areas and fine
//                   texture below the edge threshold are not touched.
//
// Edge smoothing wins on edges, sharpening everywhere else, so the two never
// fight over the same pixel.

static const char kFinishHlsl[] = R"hlsl(
Texture2D<float4>   gSrc : register(t0);
RWTexture2D<float4> gDst : register(u0);
SamplerState        gLin : register(s0);
cbuffer PC : register(b0) { uint gW; uint gH; float gSharp; float gEdge; };
static const float3 kLuma = float3(0.299f, 0.587f, 0.114f);
float L(float3 c) { return dot(c, kLuma); }
[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= gW || id.y >= gH) return;
    int2 p = int2(id.xy);
    int2 hi = int2(int(gW) - 1, int(gH) - 1);
    float3 c  = gSrc[p].rgb;
    float3 n  = gSrc[clamp(p + int2(0, -1), int2(0, 0), hi)].rgb;
    float3 s  = gSrc[clamp(p + int2(0, 1), int2(0, 0), hi)].rgb;
    float3 e  = gSrc[clamp(p + int2(1, 0), int2(0, 0), hi)].rgb;
    float3 w  = gSrc[clamp(p + int2(-1, 0), int2(0, 0), hi)].rgb;
    float3 result = c;

    // Sharpness (CAS): the weight is negative, scaled by how much headroom
    // the neighbourhood leaves before clipping.
    if (gSharp > 0.0f)
    {
        float3 mn = min(c, min(min(n, s), min(e, w)));
        float3 mx = max(c, max(max(n, s), max(e, w)));
        float3 amp = sqrt(saturate(min(mn, 1.0f - mx) / max(mx, 1e-4f)));
        // CAS's own range: -1/8 .. -1/5. Steeper than -1/5 and the divisor
        // below reaches zero on a flat patch (amp = 1).
        float peak = -1.0f / lerp(8.0f, 5.0f, gSharp);
        float3 wgt = amp * peak;
        result = saturate((c + (n + s + e + w) * wgt) / (1.0f + 4.0f * wgt));
    }

    // Edge smoothing: the luma gradient (Sobel) gives the edge's normal; the
    // tangent is where the averaging goes.
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
            float2 texel = 1.0f / float2(gW, gH);
            float2 uv = (float2(p) + 0.5f) * texel;
            float3 a = gSrc.SampleLevel(gLin, uv + tangent * texel * 0.75f, 0).rgb;
            float3 b = gSrc.SampleLevel(gLin, uv - tangent * texel * 0.75f, 0).rgb;
            float3 a2 = gSrc.SampleLevel(gLin, uv + tangent * texel * 1.75f, 0).rgb;
            float3 b2 = gSrc.SampleLevel(gLin, uv - tangent * texel * 1.75f, 0).rgb;
            float3 along = (c * 2.0f + a * 2.0f + b * 2.0f + a2 + b2) / 8.0f;
            float amount = gEdge * saturate((grad - 0.06f) * 6.0f);
            result = lerp(result, along, amount);
        }
    }
    gDst[p] = float4(result, 1.0f);
}
)hlsl";

static ID3D12RootSignature  *g_finish_rs;
static ID3D12PipelineState  *g_finish_pso;
static ID3D12DescriptorHeap *g_finish_heap;
static ID3D12Resource       *g_finish_bound[2];
static ID3D12Resource       *g_finish_tmp;          // copy of the composite, NPSR at rest
static UINT                  g_finish_w, g_finish_h;
static bool                  g_finish_failed;

static void CloseFinishTargets()
{
    if (g_finish_tmp != nullptr) { g_finish_tmp->Release(); g_finish_tmp = nullptr; }
    g_finish_w = g_finish_h = 0;
    g_finish_bound[0] = g_finish_bound[1] = nullptr;
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
    ranges[0].NumDescriptors = 1;
    ranges[1].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_UAV;
    ranges[1].NumDescriptors = 1;
    ranges[1].OffsetInDescriptorsFromTableStart = 1;
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
    hd.NumDescriptors = 2;
    hd.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
    if (FAILED(h.dev->CreateDescriptorHeap(&hd, __uuidof(ID3D12DescriptorHeap), reinterpret_cast<void **>(&g_finish_heap))))
        return false;
    g_finish_failed = false;
    Log("[finish] pipeline ready (CAS sharpening, edge-directed smoothing)");
    return true;
}

static bool EnsureFinishTargets(UINT w, UINT hgt)
{
    if (g_finish_tmp != nullptr && g_finish_w == w && g_finish_h == hgt) return true;
    CloseFinishTargets();
    D3D12_HEAP_PROPERTIES def = {};
    def.Type = D3D12_HEAP_TYPE_DEFAULT;
    D3D12_RESOURCE_DESC td = {};
    td.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    td.Width = w; td.Height = hgt; td.DepthOrArraySize = 1; td.MipLevels = 1;
    td.Format = DXGI_FORMAT_R8G8B8A8_UNORM; td.SampleDesc.Count = 1;
    td.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN;
    if (FAILED(h.dev->CreateCommittedResource(&def, D3D12_HEAP_FLAG_NONE, &td,
            D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, nullptr, __uuidof(ID3D12Resource),
            reinterpret_cast<void **>(&g_finish_tmp))))
    { Log("[finish] %ux%u target failed", w, hgt); return false; }
    g_finish_w = w;
    g_finish_h = hgt;
    return true;
}

// Records the pass at the end of the evaluate list. output is in
// UNORDERED_ACCESS on entry and on exit, as everywhere downstream expects.
static void FinishOutput(ID3D12Resource *output, UINT w, UINT hgt, float sharp, float edge)
{
    D3D12_RESOURCE_BARRIER pre[] = {
        Transition(output, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_COPY_SOURCE),
        Transition(g_finish_tmp, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_COPY_DEST),
    };
    h.list->ResourceBarrier(_countof(pre), pre);
    h.list->CopyResource(g_finish_tmp, output);
    D3D12_RESOURCE_BARRIER mid[] = {
        Transition(output, D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATE_UNORDERED_ACCESS),
        Transition(g_finish_tmp, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE),
    };
    h.list->ResourceBarrier(_countof(mid), mid);
    if (g_finish_bound[0] != g_finish_tmp || g_finish_bound[1] != output)
    {
        const UINT stride = h.dev->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
        D3D12_CPU_DESCRIPTOR_HANDLE cpu = g_finish_heap->GetCPUDescriptorHandleForHeapStart();
        D3D12_SHADER_RESOURCE_VIEW_DESC sd = {};
        sd.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        sd.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
        sd.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
        sd.Texture2D.MipLevels = 1;
        h.dev->CreateShaderResourceView(g_finish_tmp, &sd, cpu);
        cpu.ptr += stride;
        D3D12_UNORDERED_ACCESS_VIEW_DESC ud = {};
        ud.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        ud.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
        h.dev->CreateUnorderedAccessView(output, nullptr, &ud, cpu);
        g_finish_bound[0] = g_finish_tmp;
        g_finish_bound[1] = output;
    }
    ID3D12DescriptorHeap *heaps[] = { g_finish_heap };
    h.list->SetDescriptorHeaps(1, heaps);
    h.list->SetComputeRootSignature(g_finish_rs);
    h.list->SetPipelineState(g_finish_pso);
    UINT32 c[4] = { w, hgt, 0, 0 };
    memcpy(&c[2], &sharp, sizeof(float));
    memcpy(&c[3], &edge, sizeof(float));
    h.list->SetComputeRoot32BitConstants(0, 4, c, 0);
    h.list->SetComputeRootDescriptorTable(1, g_finish_heap->GetGPUDescriptorHandleForHeapStart());
    h.list->Dispatch((w + 7) / 8, (hgt + 7) / 8, 1);
}
