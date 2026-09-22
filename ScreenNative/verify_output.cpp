// Integration probe: receive processed pixels through the public Spout2 output.
#include "spout/SpoutDX.h"
#include <d3d11.h>
#include <dxgi1_2.h>
#include <cstdio>
#include <cmath>
#include <cstring>

static bool DesktopContainsDivider(ID3D11Device* device, ID3D11DeviceContext* context) {
    IDXGIDevice* dx = nullptr; IDXGIAdapter* adapter = nullptr;
    IDXGIOutput* output = nullptr; IDXGIOutput1* output1 = nullptr;
    IDXGIOutputDuplication* duplication = nullptr;
    bool green = false;
    if (SUCCEEDED(device->QueryInterface(IID_PPV_ARGS(&dx))) && SUCCEEDED(dx->GetAdapter(&adapter)) &&
        SUCCEEDED(adapter->EnumOutputs(0, &output)) && SUCCEEDED(output->QueryInterface(IID_PPV_ARGS(&output1))) &&
        SUCCEEDED(output1->DuplicateOutput(device, &duplication))) {
        for (int i = 0; i < 50 && !green; ++i) {
            DXGI_OUTDUPL_FRAME_INFO info = {}; IDXGIResource* frame = nullptr;
            if (FAILED(duplication->AcquireNextFrame(100, &info, &frame))) continue;
            ID3D11Texture2D* texture = nullptr;
            if (SUCCEEDED(frame->QueryInterface(IID_PPV_ARGS(&texture)))) {
                D3D11_TEXTURE2D_DESC desc = {}; texture->GetDesc(&desc);
                desc.Usage = D3D11_USAGE_STAGING; desc.BindFlags = desc.MiscFlags = 0;
                desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
                ID3D11Texture2D* staging = nullptr;
                if (SUCCEEDED(device->CreateTexture2D(&desc, nullptr, &staging))) {
                    context->CopyResource(staging, texture);
                    D3D11_MAPPED_SUBRESOURCE mapped = {};
                    if (SUCCEEDED(context->Map(staging, 0, D3D11_MAP_READ, 0, &mapped))) {
                        const auto* row = static_cast<const unsigned char*>(mapped.pData) + desc.Height / 2 * mapped.RowPitch;
                        const int split = static_cast<int>(desc.Width * .35);
                        for (int x = split - 5; x <= split + 5; ++x) {
                            if (x < 0 || x >= static_cast<int>(desc.Width)) continue;
                            const auto* p = row + x * 4;
                            green |= p[1] == 185 && ((p[0] == 118 && p[2] == 0) || (p[2] == 118 && p[0] == 0));
                        }
                        context->Unmap(staging, 0);
                    }
                    staging->Release();
                }
                texture->Release();
            }
            frame->Release(); duplication->ReleaseFrame();
        }
    }
    if (duplication) duplication->Release(); if (output1) output1->Release();
    if (output) output->Release(); if (adapter) adapter->Release(); if (dx) dx->Release();
    puts(green ? "PASS Windows desktop capture contains the processed green divider" : "FAIL Windows desktop capture missed processed output");
    return green;
}

int main(int argc, char** argv) {
    ID3D11Device* device = nullptr;
    ID3D11DeviceContext* context = nullptr;
    if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0,
        nullptr, 0, D3D11_SDK_VERSION, &device, nullptr, &context))) return 1;
    if (argc > 1 && strcmp(argv[1], "--desktop") == 0) {
        const bool ok = DesktopContainsDivider(device, context);
        context->Release(); device->Release(); return ok ? 0 : 4;
    }
    spoutDX receiver;
    if (!receiver.OpenDirectX11(device)) return 2;
    receiver.SetReceiverName("DLSS 5 MANAGER Screen");
    ID3D11Texture2D* texture = nullptr;
    bool green = false;
    for (int attempt = 0; attempt < 200 && !green; ++attempt) {
        if (!texture) {
            if (receiver.ReceiveTexture()) {
                D3D11_TEXTURE2D_DESC desc = {};
                desc.Width = receiver.GetSenderWidth(); desc.Height = receiver.GetSenderHeight();
                desc.MipLevels = desc.ArraySize = desc.SampleDesc.Count = 1;
                desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
                desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
                device->CreateTexture2D(&desc, nullptr, &texture);
            }
        } else if (receiver.ReceiveTexture(&texture)) {
            D3D11_TEXTURE2D_DESC desc = {};
            texture->GetDesc(&desc);
            desc.Usage = D3D11_USAGE_STAGING; desc.BindFlags = 0;
            desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            ID3D11Texture2D* staging = nullptr;
            if (SUCCEEDED(device->CreateTexture2D(&desc, nullptr, &staging))) {
                context->CopyResource(staging, texture);
                D3D11_MAPPED_SUBRESOURCE mapped = {};
                if (SUCCEEDED(context->Map(staging, 0, D3D11_MAP_READ, 0, &mapped))) {
                    const auto* row = static_cast<const unsigned char*>(mapped.pData) + desc.Height / 2 * mapped.RowPitch;
                    const int split = static_cast<int>(desc.Width * 0.35);
                    for (int x = split - 5; x <= split + 5; ++x) {
                        if (x < 0 || x >= static_cast<int>(desc.Width)) continue;
                        const auto* p = row + x * 4;
                        // Accept RGBA/BGRA receiver swizzles, never the old orange divider.
                        green |= p[1] == 185 && ((p[0] == 118 && p[2] == 0) || (p[2] == 118 && p[0] == 0));
                    }
                    context->Unmap(staging, 0);
                    if (green) printf("PASS processed output %ux%u with green comparison divider from %s\n", desc.Width, desc.Height, receiver.GetSenderName());
                }
                staging->Release();
            }
        }
        Sleep(100);
    }
    if (texture) texture->Release();
    receiver.ReleaseReceiver();
    context->Release(); device->Release();
    if (!green) puts("FAIL no processed green divider received");
    return green ? 0 : 3;
}
