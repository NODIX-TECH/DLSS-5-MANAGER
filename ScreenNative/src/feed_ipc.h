// dlss5-feed IPC: the 32-bit in-game add-on <-> 64-bit host protocol.
//
// Everything heavy stays on the GPU: the game side CREATES the four shared
// textures on D3D11 (the direction the driver accepts, see the phase-0 spike)
// and sends its local NT-handle values; the host duplicates them out of the
// game process and opens them on D3D12. The host creates the two shared
// fences on D3D12 and duplicates them INTO the game process. The pipe carries
// only these fixed-size structs.
//
// Sync per frame n: game copies inputs, Signal(in_fence, n), sends FeedFrameMsg,
// records Wait(out_fence, n) + blit; host waits in_fence >= n, evaluates,
// Signal(out_fence, n). A pipe break on either side means "stop feeding".

#pragma once
#include <cstdint>

#define FEED_IPC_MAGIC   0x35534C44u  // 'DLS5'
#define FEED_IPC_VERSION 1u
#define FEED_PIPE_FMT    "\\\\.\\pipe\\dlss5-feed.%lu"   // %lu = game PID

enum FeedSlot { FEED_COLOR = 0, FEED_OUTPUT, FEED_DEPTH, FEED_MV, FEED_SLOTS };

#pragma pack(push, 1)

struct FeedHello        // game -> host, once
{
    uint32_t magic;
    uint32_t version;
    uint32_t pid;
};

struct FeedHelloAck     // host -> game, once
{
    uint32_t magic;
    uint32_t version;
};

struct FeedBuild        // game -> host, on every resolution/format change
{
    uint32_t width, height;
    uint32_t color_fmt;          // DXGI_FORMAT of the shared Color/Output pair
    uint32_t output_fmt;
    int32_t  hdr;                // resolved flags, not cfg values
    int32_t  depth_inverted;
    int32_t  flags_override;     // -1 = none
    int32_t  transport;          // 1 = no NGX: host copies Color -> Output (cross-process transport test)
    float    mv_scale_x, mv_scale_y;
    uint64_t tex[FEED_SLOTS];    // NT-handle VALUES in the game process (host duplicates them out)
};

struct FeedBuildAck     // host -> game
{
    int32_t  ok;                 // 1 = feature ready
    uint32_t ngx_result;         // NVSDK_NGX_Result of the create (0x1 = success)
    uint64_t fence_in;           // handle values valid in the GAME process (host duplicated them in)
    uint64_t fence_out;
};

struct FeedFrameMsg     // game -> host, per frame
{
    uint64_t n;                  // fence value for this frame
    uint32_t reset;              // 1 = reset temporal history
};

#pragma pack(pop)
