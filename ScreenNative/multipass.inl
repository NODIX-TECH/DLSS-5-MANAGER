// Independent persistent histories for sequential NR layers. The original
// input is immutable; composition and the comparison wipe happen only once.
static NVSDK_NGX_Handle* g_extra_features[2] = {};
static ID3D12Resource* g_pass_scratch = nullptr;
static bool g_pass_ready = false;
static unsigned g_pass_count = 1;

static void ReleaseMultiPass()
{
    for (auto& feature : g_extra_features) {
        if (feature) SafeReleaseFeature(feature);
        feature = nullptr;
    }
    if (g_pass_scratch) g_pass_scratch->Release();
    g_pass_scratch = nullptr;
    g_pass_ready = false;
    g_pass_count = 1;
}

static bool EnsureMultiPass(VideoState& v)
{
    if (g_pass_ready) return true;
    char value[8] = {};
    GetEnvironmentVariableA("DLSS_MANAGER_PASSES", value, sizeof(value));
    const int count = atoi(value);
    g_pass_count = count >= 1 && count <= 3 ? count : 1;
    if (g_pass_count == 1) { g_pass_ready = true; return true; }
    ID3D12Resource* result = v.nr_small ? v.nr_out : v.output;
    auto desc = result->GetDesc();
    D3D12_HEAP_PROPERTIES heap = {};
    heap.Type = D3D12_HEAP_TYPE_DEFAULT;
    if (FAILED(h.dev->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &desc,
        D3D12_RESOURCE_STATE_UNORDERED_ACCESS, nullptr, IID_PPV_ARGS(&g_pass_scratch)))) return false;
    const auto first = h.feature;
    const int flags = NVSDK_NGX_DLSS_Feature_Flags_MVLowRes | NVSDK_NGX_DLSS_Feature_Flags_AutoExposure |
                      NVSDK_NGX_DLSS_Feature_Flags_DepthInverted;
    for (unsigned i = 0; i < g_pass_count - 1; ++i) {
        h.feature = nullptr;
        NVSDK_NGX_Result resultCode;
        const bool ok = CreateFeature(v.w, v.hgt, flags, &resultCode,
            v.nr_small || !v.upscale ? 0 : v.full_w, v.nr_small || !v.upscale ? 0 : v.full_h);
        g_extra_features[i] = h.feature;
        h.feature = first;
        if (!ok) { ReleaseMultiPass(); Log("[multipass] extra feature creation failed"); return false; }
    }
    g_pass_ready = true;
    Log("[multipass] %u independent layers ready", g_pass_count);
    return true;
}

static NVSDK_NGX_Result EvaluateExtraLayers(ID3D12Resource* result, int reset)
{
    NVSDK_NGX_Result status = NVSDK_NGX_Result_Success;
    for (unsigned i = 0; i < g_pass_count - 1; ++i) {
        auto read = Transition(result, D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
                               D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
        h.list->ResourceBarrier(1, &read);
        h.params->Set("DLSSNR.Color", result);
        h.params->Set("DLSSNR.Output", g_pass_scratch);
        h.params->Set("DLSSNR.Reset", static_cast<unsigned>(reset));
        // Avoid repeatedly lifting shadows across layers.
        h.params->Set("DLSSNR.LocalToneStrength", 0.0f);
        DWORD exception = 0;
        __try { status = g_nr_evaluate(h.list, g_extra_features[i], h.params, nullptr); }
        __except (EXCEPTION_EXECUTE_HANDLER) { exception = GetExceptionCode(); }
        if (exception || NVSDK_NGX_FAILED(status)) return NVSDK_NGX_Result_Fail;
        D3D12_RESOURCE_BARRIER copy[] = {
            Transition(result, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_COPY_DEST),
            Transition(g_pass_scratch, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_COPY_SOURCE)
        };
        h.list->ResourceBarrier(2, copy);
        h.list->CopyResource(result, g_pass_scratch);
        D3D12_RESOURCE_BARRIER back[] = {
            Transition(result, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_UNORDERED_ACCESS),
            Transition(g_pass_scratch, D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATE_UNORDERED_ACCESS)
        };
        h.list->ResourceBarrier(2, back);
    }
    return status;
}
