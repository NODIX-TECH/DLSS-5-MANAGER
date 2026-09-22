// Spout2 bridge: publishes the worker's D3D12 output texture to any Spout2
// receiver (OBS Spout2 Capture source, spout_receiver.exe test tool).
//
// The overlay window is hidden from screen capture (WDA_EXCLUDEFROMCAPTURE)
// so external recorders cannot see the processed picture. WDA does NOT
// affect D3D shared textures (it is enforced by DWM at composition time),
// so a Spout2 sender is a clean zero-copy channel: OBS pulls the shared
// texture directly, no self-capture loop (the DDA input never sees it).
//
// The worker's output is a D3D12 resource; Spout2 shares D3D11 textures.
// The bridge creates a D3D11 shared texture, opens it in D3D12, copies the
// output into it inside the existing command list (SpoutBridgeCopy), and
// hands the D3D11 side to SpoutDX::SendTexture after the fence fires
// (SpoutBridgeSend).
//
// Enabled with NS_SPOUT=1 (like NS_NR_SMALL): off by default, the bridge
// costs nothing when disabled.
#pragma once

#include <d3d11.h>
#include <d3d12.h>

// Create the D3D11 device, the shared texture (lazily, on first publish)
// and the SpoutDX sender. Returns false when Spout is disabled or the
// device cannot be created (the worker keeps running without it).
bool SpoutBridgeInit(ID3D12Device *dev);

// Add a CopyResource(output -> spout shared texture) to the current list.
// Call between BeginCommands() and EndCommands().
void SpoutBridgeCopy(ID3D12GraphicsCommandList *list, ID3D12Resource *src,
                     UINT w, UINT h);

// Send the shared texture to Spout receivers. Call after the fence fired.
void SpoutBridgeSend();

// Release everything. Safe to call when not initialized.
void SpoutBridgeShutdown();
