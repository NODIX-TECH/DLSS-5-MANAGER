// MSVC takes at most 16 KB in one string literal: the source is several
// adjacent raw literals, which the compiler joins into this one array.
static const char kFlowHlsl[] = R"hlsl(
// Motion is current -> previous, in motion-texture pixels. Reverse is
// previous -> current. Colour stays at output resolution, including the HUD.
//
// These kernels share one set of bindings. Once for every captured pair:
// ResidMain, SeedMain, SearchMain and TrackMain find the motion of small fast
// objects the estimate lost (a ball); BlockMain (a few rounds) and TexelMain
// refine the whole field block by block, so a player keeps one motion;
// HudMain updates the screen-locked layer - the HUD; FixMain rebuilds the
// motion under and around the HUD. CSMain then draws each frame in between.
Texture2D<float4> gPrev : register(t0);
Texture2D<float4> gCur : register(t1);
Texture2D<float2> gMv : register(t2);          // FixMain's result
Texture2D<float2> gReverse : register(t3);     // FixMain's result
Texture2D<float2> gRawMv : register(t4);       // as estimated
Texture2D<float2> gRawReverse : register(t5);  // as estimated
Texture2D<float2> gTrackMv : register(t6);     // TrackMain's result, then TexelMain's; FixMain's input
Texture2D<float2> gTrackReverse : register(t7);
RWTexture2D<float4> gDst : register(u0);
// Per pixel: 16 x (near a HUD verdict - FixMain) + 2 x (pairs it stood still,
// up to 4) + how surely it is HUD (0..1) - both HudMain.
RWTexture2D<float> gHud : register(u1);
RWTexture2D<float2> gFixMv : register(u2);
RWTexture2D<float2> gFixReverse : register(u3);
RWTexture2D<float2> gTrkMv : register(u4);
RWTexture2D<float2> gTrkReverse : register(u5);
// Per motion texel: how badly the estimate carries the pixels, f16 backward
// | f16 forward << 16.
RWTexture2D<uint> gResid : register(u6);
// Per 8x8-texel tile (kTile), two entries: the seed (x, y in output pixels, its
// residual, valid) and what the search found (motion in output pixels, cost,
// valid). After them, BlockMain's fields: four per kBlock x kBlock block
// (BlockAt).
RWStructuredBuffer<float4> gTiles : register(u7);
// Motion texels per tile side, per block side, and how many rounds BlockMain
// runs. The host sizes gTiles and dispatches from them (flow_mfg.inl).
static const uint kTile = 8;
static const uint kBlock = 4;
static const uint kBlockRounds = 4;
SamplerState gLin : register(s0);
// gPass: which round of BlockMain this is; kSkipSearch in TrackMain: the host
// ran none of the searches this pair (a fast source - see flow_mfg.inl), so
// the estimate is handed on as it is.
static const uint kSkipSearch = 0xFFFFu;
cbuffer FC : register(b0) { uint gW; uint gH; float gT; float gAccum; uint gBoth; uint gSearchRings; uint gPass; };
float Max3(float3 v) { return max(v.x, max(v.y, v.z)); }
bool Inside(float2 uv, float2 texel) {
    return all(uv >= texel * .5) && all(uv <= 1.0 - texel * .5);
}
float BaseOf(float state) { return isfinite(state) ? state - 16.0 * floor(state / 16.0) : 0; }
float StillOf(float state) { return clamp(floor(BaseOf(state) * .5), 0.0, 4.0); }
float HudOf(float state) { float b = BaseOf(state); return saturate(b - 2.0 * floor(b * .5)); }
bool NearOf(float state) { return isfinite(state) && state >= 16.0; }
float HudAt(float2 uv) {
    int2 q = clamp(int2(uv * float2(gW, gH)), int2(0, 0), int2(gW - 1, gH - 1));
    return HudOf(gHud[q]);
}
// A place that has stood still for several pairs close to a HUD verdict: the
// flat inside of a panel, a bar or a map, which no motion test can single out
// (moving it matches). A still place away from any HUD - a flat patch of a
// panning scene - is not. "Close" is worked out once per pair by FixMain.
bool PanelAt(float2 uv) {
    int2 q = clamp(int2(uv * float2(gW, gH)), int2(0, 0), int2(gW - 1, gH - 1));
    float state = gHud[q];
    return StillOf(state) >= 3 && NearOf(state);
}
// Fixed to the screen as far as the last pair knew: a HUD verdict, or the
// flat still inside of a panel beside one (PanelAt). Motion estimation and
// its checks leave such pixels out - they do not move with anything.
bool LockedAt(float2 uv) {
    int2 q = clamp(int2(uv * float2(gW, gH)), int2(0, 0), int2(gW - 1, gH - 1));
    float state = gHud[q];
    return HudOf(state) >= .5 || (StillOf(state) >= 3 && NearOf(state));
}
float2 Motion(float2 uv, float2 scale, bool reverse) {
    float2 m = reverse ? -gReverse.SampleLevel(gLin, uv, 0) : gMv.SampleLevel(gLin, uv, 0);
    return all(isfinite(m)) ? m * scale : 0;
}
// sides: allow following the surface from one end when the other is under
// the HUD. The crossing search passes false - it only takes a candidate
// matched at both ends, so a one-sided answer could never win there.
float4 Follow(float2 uv, float2 texel, float2 scale, float2 m, bool sides) {
    float2 p = uv + gT * m * texel, c = uv - (1.0 - gT) * m * texel;
    bool vp = Inside(p, texel), vc = Inside(c, texel);
    float3 cp = gPrev.SampleLevel(gLin, p, 0).rgb;
    float3 cc = gCur.SampleLevel(gLin, c, 0).rgb;
    float confidence = 1.0 - smoothstep(.04, .18, Max3(abs(cp - cc)));
    float2 back = m, forward = m;
    float reach = 3.0 + .05 * length(m);
    if (gBoth != 0) {
        back = Motion(c, scale, false); forward = Motion(p, scale, true);
        float error = max(length(back - forward), min(length(back - m), length(forward - m)));
        // Disagreement lowers trust, but is not proof of a wrong colour
        // correspondence on a flat/occluded surface. A hard zero here fell
        // back to unwarped endpoints and tore otherwise matching silhouettes.
        confidence *= .25 + .75 * (1.0 - smoothstep(1.0, reach, error));
    }
    // A valid endpoint can fill a newly uncovered image border. Never drag
    // a clamped border texel into the image, and never crossfade an occlusion.
    if (!vp || !vc) return float4(vp ? cp : cc, (vp || vc) ? .15 : 0);
    // One end of this path under the HUD: the colour there is the HUD's, not
    // the surface's, and fading between the two dragged the HUD out over the
    // scene - a band as wide as the motion, the widest at a low frame rate.
    // Follow the surface from the end where it can be seen, as far as that
    // end's own motion agrees.
    if (sides && confidence < .5 && dot(m, m) >= 2.25) {
        bool hideP = HudAt(p) >= .5, hideC = HudAt(c) >= .5;
        if (!hideP && !hideC && gBoth != 0) {
            // No verdict (the flat inside of a HUD panel has none): a place
            // of a panel that did not change while this path says it moved
            // is covered as well.
            hideP = Max3(abs(cp - gCur.SampleLevel(gLin, p, 0).rgb)) < .01 && PanelAt(p);
            hideC = Max3(abs(cc - gPrev.SampleLevel(gLin, c, 0).rgb)) < .01 && PanelAt(c);
        }
        if (hideP != hideC) {
            float trust = gBoth != 0
                ? .6 * (1.0 - smoothstep(1.0, reach, length((hideP ? back : forward) - m))) : .45;
            return float4(hideP ? cc : cp, trust);
        }
    }
    return float4(lerp(cp, cc, gT), confidence);
}

// ---- Once per pair: the HUD ---------------------------------------------
// A HUD stands still on the screen while the scene under it moves, and the
// motion estimate cannot be trusted on it: it sees a big panel as still and
// gives thin text the motion of the scene around it. So a pixel counts as
// HUD when it did not change at all, the scene around it moved, and moving
// it with the scene would not have matched - backwards into the previous
// frame nor forwards into the current one. Both ways: a flat patch of scene
// beside a HUD fails one of them (its source is under the HUD) but not the
// other. A scene pixel that changed exactly as its motion says drops out at
// once. Flat pixels give no evidence - and need none, nothing can drag them.
float2 RawMotion(float2 uv, float2 scale) {
    float2 m = gRawMv.SampleLevel(gLin, uv, 0);
    return all(isfinite(m)) ? m * scale : 0;
}
// How far a colour is from the other frame's at the place a motion leads
// to; negative when that place is off the picture.
float Miss(float3 colour, float2 uv, float2 texel, float2 m, bool intoCurrent) {
    float2 to = uv + m * texel;
    if (!Inside(to, texel)) return -1;
    float3 there = intoCurrent ? gCur.SampleLevel(gLin, to, 0).rgb : gPrev.SampleLevel(gLin, to, 0).rgb;
    return Max3(abs(colour - there));
}
[numthreads(8, 8, 1)]
void HudMain(uint3 id : SV_DispatchThreadID) {
    if (id.x >= gW || id.y >= gH) return;
    float state = gHud[id.xy];
    float score = HudOf(state), stood = StillOf(state);
    if (gAccum < 0) score = stood = 0;
    if (gAccum <= 0) { gHud[id.xy] = 2.0 * stood + score; return; }
    float2 size = float2(gW, gH), texel = 1.0 / size;
    float2 uv = (float2(id.xy) + .5) * texel;
    uint mw, mh; gRawMv.GetDimensions(mw, mh);
    float2 scale = size / float2(mw, mh);
    float3 now = gCur[id.xy].rgb, before = gPrev[id.xy].rgb;
    float change = Max3(abs(now - before));
    // How many pairs in a row it has not changed - what CSMain's rule for
    // unchanged pixels needs, so a slowly shaded patch of a pan is not held.
    stood = change > .012 ? 0 : min(4.0, stood + 1.0);
    float2 own = RawMotion(uv, scale);
    float ownMiss = dot(own, own) >= 2.25 ? Miss(now, uv, texel, own, false) : -1;
    // Changed exactly as its own motion says: scenery, whatever it was.
    if (change > .06 && ownMiss >= 0 && ownMiss < min(.04, change * .4)) {
        gHud[id.xy] = 2.0 * stood + score * .2;
        return;
    }
    // The scene's motion here. Its own vector is not enough (see above), so
    // a ring further out votes too and a vector median drops the minority.
    float unit = max(1.0, gH / 720.0);
    const float2 ring[8] = {float2(24,0),float2(-24,0),float2(0,24),float2(0,-24),
        float2(48,48),float2(-48,48),float2(48,-48),float2(-48,-48)};
    float2 votes[9];
    votes[0] = own;
    [unroll] for (int i = 0; i < 8; ++i) votes[i + 1] = RawMotion(uv + ring[i] * unit * texel, scale);
    float2 scene = own;
    float lowest = 1e30;
    [unroll] for (int a = 0; a < 9; ++a) {
        float cost = 0;
        [unroll] for (int b = 0; b < 9; ++b) { float2 d = abs(votes[a] - votes[b]); cost += d.x + d.y; }
        if (cost < lowest) { lowest = cost; scene = votes[a]; }
    }
    bool moving = dot(scene, scene) >= 2.25;
    float back = moving ? Miss(now, uv, texel, scene, false) : -1;
    if (change > .06) {
        // A HUD that changes (a counter ticking over) keeps its place;
        // something that keeps changing without moving lets go slowly.
        bool explained = back >= 0 && back < min(.04, change * .4);
        gHud[id.xy] = 2.0 * stood + (explained ? score * .2 : max(0.0, score - .15));
        return;
    }
    if (moving && change <= .02) {
        float ahead = Miss(before, uv, texel, -scene, true);
        bool backFails = back > .08, aheadFails = ahead > .08;
        bool fails = (back >= 0 || ahead >= 0) && (back < 0 || backFails) && (ahead < 0 || aheadFails);
        // Along a HUD stroke that lies in the direction of motion the shifted
        // colour is the stroke again, and one shift can land on the next
        // letter. A stroke still stands out against the scenery moving past
        // it: neighbours that changed, yet stayed on the same side of this
        // pixel's colour in both frames. On both sides that is enough; on one
        // side only with a failed shift as well - a flat patch of scene beside
        // texture sliding along it has one such side and nothing else.
        bool side[4];
        const int2 around[4] = {int2(-2,0),int2(2,0),int2(0,-2),int2(0,2)};
        [unroll] for (int e = 0; e < 4; ++e) {
            int2 q = clamp(int2(id.xy) + around[e], int2(0, 0), int2(gW - 1, gH - 1));
            float3 sideNow = gCur[q].rgb, sideBefore = gPrev[q].rgb;
            float3 edgeNow = now - sideNow, edgeBefore = before - sideBefore;
            side[e] = Max3(abs(sideNow - sideBefore)) > .06 && dot(edgeNow, edgeBefore) > 0 &&
                      min(Max3(abs(edgeNow)), Max3(abs(edgeBefore))) > .12;
        }
        bool anchored = (side[0] && side[1]) || (side[2] && side[3]) ||
            ((backFails || aheadFails) && (side[0] || side[1] || side[2] || side[3]));
        // The tracker (which runs first) set this texel still where the
        // estimate had it moving - standing still is what carries its pixels -
        // while the scene around it moves: the plainest evidence the motion
        // vectors give of something fixed to the screen.
        float2 tracked = gTrackMv[clamp(int2(uv * float2(mw, mh)), int2(0, 0), int2(mw - 1, mh - 1))];
        bool pinned = all(isfinite(tracked)) && dot(tracked * scale, tracked * scale) < .25 && dot(own, own) >= 2.25;
        // Its own motion moves it clearly apart from the scene and carries it
        // and the pixels beside it: part of an object the camera follows - a
        // ball, the player with it - still on the screen for a moment, not
        // fixed to it. Held as HUD it stayed behind while the rest moved.
        float2 carry = all(isfinite(tracked)) ? tracked * scale : 0;
        bool carried = false;
        // Where the verdict already holds, that motion counts only if the
        // refinement found it - it differs from the estimate. Over a HUD
        // element held still the block search has nothing to judge by and
        // keeps the estimate, which can line up with a bar or a stroke.
        bool found = !LockedAt(uv) || length(carry - own) > 1.5;
        if (found && dot(carry, carry) >= 2.25 && length(carry - scene) >= 1.5) {
            float worst = Miss(now, uv, texel, carry, false);
            [unroll] for (int c = 0; c < 4; ++c) {
                int2 q = clamp(int2(id.xy) + around[c], int2(0, 0), int2(gW - 1, gH - 1));
                worst = max(worst, Miss(gCur[q].rgb, (float2(q) + .5) * texel, texel, carry, false));
            }
            // And the motion shows nearby: places a few pixels out that did
            // change, and exactly as this motion says - the edge of the ball
            // or the player moving with it. Inside a flat panel held still any
            // motion along it "carries" the pixels, and nothing around moved.
            uint shown = 0;
            [unroll] for (int s = 0; s < 8; ++s) {
                float2 o = float2(cos(s * .785398), sin(s * .785398)) * 6.0 * unit;
                int2 q = clamp(int2(float2(id.xy) + o), int2(0, 0), int2(gW - 1, gH - 1));
                float3 there = gCur[q].rgb;
                if (Max3(abs(there - gPrev[q].rgb)) > .06) {
                    float miss = Miss(there, (float2(q) + .5) * texel, texel, carry, false);
                    shown += miss >= 0 && miss < .06 ? 1u : 0u;
                }
            }
            carried = worst >= 0 && worst < .06 && shown >= 2;
        }
        if (carried) score = max(0.0, score - .25);
        else if (fails || anchored || pinned) score = min(1.0, score + .5);
    }
    gHud[id.xy] = 2.0 * stood + score;
}

)hlsl"
R"hlsl(// ---- Once per pair: the motion under and around the HUD ------------------
// What was estimated there is the HUD's (still) or a blend of the HUD and the
// scene, and the scene pixels beside a HUD element followed it: they stuck to
// it, or dragged it along. Under the HUD the scene's motion is taken from the
// nearest clean estimate in each direction (a weighted vector median); beside
// it, that motion replaces the estimate only where it explains the pixels
// better. Everything else is copied as it is.
float Covered(float2 pixel, float reach) {
    const float2 around[9] = {float2(0,0),float2(1,0),float2(-1,0),float2(0,1),float2(0,-1),
        float2(.7,.7),float2(-.7,.7),float2(.7,-.7),float2(-.7,-.7)};
    float h = 0;
    [unroll] for (int i = 0; i < 9; ++i) {
        int2 q = clamp(int2(pixel + around[i] * reach), int2(0, 0), int2(gW - 1, gH - 1));
        h = max(h, HudOf(gHud[q]));
    }
    return h;
}
// How well a vector (output pixels) carries the pixels around one texel
// from one frame to the other, leaving out whatever the HUD covers at either
// end. Negative when nothing is left to compare.
float Fit(float2 pixel, float2 m, bool reverse) {
    float2 size = float2(gW, gH), texel = 1.0 / size;
    float total = 0, used = 0;
    [loop] for (int y = -1; y <= 1; ++y)
    [loop] for (int x = -1; x <= 1; ++x) {
        float2 a = (pixel + float2(x, y) * 1.5) * texel, b = a + m * texel;
        if (!Inside(a, texel) || !Inside(b, texel) || HudAt(a) >= .5 || HudAt(b) >= .5) continue;
        float3 here = reverse ? gPrev.SampleLevel(gLin, a, 0).rgb : gCur.SampleLevel(gLin, a, 0).rgb;
        float3 there = reverse ? gCur.SampleLevel(gLin, b, 0).rgb : gPrev.SampleLevel(gLin, b, 0).rgb;
        total += Max3(abs(here - there));
        used += 1;
    }
    return used > 0 ? total / used : -1;
}
// Within 48 pixels (at 720p) of a HUD verdict.
float NearHud(float2 pixel) {
    float unit = max(1.0, gH / 720.0);
    const float2 dirs[8] = {float2(1,0),float2(-1,0),float2(0,1),float2(0,-1),
        float2(.707,.707),float2(-.707,.707),float2(.707,-.707),float2(-.707,-.707)};
    const float radii[3] = {6.0, 20.0, 48.0};
    [loop] for (int r = 0; r < 3; ++r)
    [loop] for (int d = 0; d < 8; ++d) {
        int2 q = clamp(int2(pixel + dirs[d] * radii[r] * unit), int2(0, 0), int2(gW - 1, gH - 1));
        if (HudOf(gHud[q]) >= .5) return 1;
    }
    return 0;
}
[numthreads(8, 8, 1)]
void FixMain(uint3 id : SV_DispatchThreadID) {
    uint mw, mh; gRawMv.GetDimensions(mw, mh);
    if (id.x >= mw || id.y >= mh) return;
    // The motion as TrackMain left it: the estimate, with the small fast
    // objects it lost put back.
    float2 raw = gTrackMv[id.xy], rawBack = gTrackReverse[id.xy];
    float2 size = float2(gW, gH), scale = size / float2(mw, mh);
    float2 pixel = (float2(id.xy) + .5) * scale;
    // Mark this texel's own pixels as near a HUD verdict or not. Only the
    // mark changes: whatever other threads read from these pixels meanwhile
    // (the verdict, the stillness) is the same either way.
    // Only a pixel that has stood still can be the inside of a panel, so the
    // search is spent only where one has - little of a moving picture.
    int2 first = int2(floor(float2(id.xy) * scale)), last = min(int2(floor(float2(id.xy + 1) * scale)), int2(gW, gH));
    bool stood = false;
    for (int sy = first.y; sy < last.y; ++sy)
        for (int sx = first.x; sx < last.x; ++sx)
            stood = stood || StillOf(gHud[int2(sx, sy)]) >= 3;
    float near = gAccum < 0 || !stood ? 0 : NearHud(pixel);
    for (int y = first.y; y < last.y; ++y)
        for (int x = first.x; x < last.x; ++x)
            gHud[int2(x, y)] = BaseOf(gHud[int2(x, y)]) + 16.0 * near;
    // One estimate covers a few motion texels, and the blend spreads further.
    float reach = 6.0 * max(scale.x, scale.y);
    if (gAccum < 0 || Covered(pixel, reach) < .5) {
        gFixMv[id.xy] = raw; gFixReverse[id.xy] = rawBack;
        return;
    }
    const float2 dirs[8] = {float2(1,0),float2(-1,0),float2(0,1),float2(0,-1),
        float2(.707,.707),float2(-.707,.707),float2(.707,-.707),float2(-.707,-.707)};
    const float steps[6] = {1.5, 2.25, 3.4, 5.1, 7.6, 11.4};
    float2 found[8], foundBack[8];
    float weight[8];
    [loop] for (int d = 0; d < 8; ++d) {
        found[d] = raw; foundBack[d] = rawBack; weight[d] = 0;
        [loop] for (int s = 0; s < 6; ++s) {
            float2 q = pixel + dirs[d] * steps[s] * reach;
            if (any(q < 0) || any(q >= size)) break;
            if (Covered(q, reach) < .5) {
                float2 at = q / size;
                found[d] = gTrackMv.SampleLevel(gLin, at, 0);
                foundBack[d] = gTrackReverse.SampleLevel(gLin, at, 0);
                weight[d] = all(isfinite(found[d])) && all(isfinite(foundBack[d])) ? 1.0 / steps[s] : 0;
                break;
            }
        }
    }
    float2 fixMv = raw, fixBack = rawBack;
    float lowest = 1e30, lowestBack = 1e30;
    [loop] for (int a = 0; a < 8; ++a) {
        if (weight[a] <= 0) continue;
        float cost = 0, costBack = 0;
        [unroll] for (int b = 0; b < 8; ++b) {
            float2 e = abs(found[a] - found[b]), f = abs(foundBack[a] - foundBack[b]);
            cost += weight[b] * (e.x + e.y);
            costBack += weight[b] * (f.x + f.y);
        }
        if (cost < lowest) { lowest = cost; fixMv = found[a]; }
        if (costBack < lowestBack) { lowestBack = costBack; fixBack = foundBack[a]; }
    }
    if (lowest < 1e30) {
        float under = 0;
        [unroll] for (int k = 0; k < 4; ++k)
            under = max(under, HudAt((pixel + (float2(k & 1, k >> 1) - .5) * scale * .5) / size));
        if (under < .5) {
            // Beside the HUD: only where it explains the pixels better.
            float kept = Fit(pixel, raw * scale, false), fixed = Fit(pixel, fixMv * scale, false);
            if (!(fixed >= 0 && (kept < 0 || fixed + .01 < kept))) fixMv = raw;
            kept = Fit(pixel, rawBack * scale, true); fixed = Fit(pixel, fixBack * scale, true);
            if (gBoth == 0 || !(fixed >= 0 && (kept < 0 || fixed + .01 < kept))) fixBack = rawBack;
        }
    }
    gFixMv[id.xy] = fixMv;
    gFixReverse[id.xy] = fixBack;
}

)hlsl"
R"hlsl(// ---- Once per pair: small fast objects -------------------------------------
// The estimate loses an object that moves further between two frames than it
// is wide - a ball, a hand, anything thrown: at the coarse levels it works on
// the object is gone, and it hands back the scene's motion. Measured on a
// football at 15 FPS it had the ball right in one pair of eight.
//
// ResidMain marks where the estimate does not carry the pixels; SeedMain takes
// the worst spot of each tile; SearchMain looks it up in the previous frame -
// coarse to fine, weighted towards the pixels that moved; TrackMain offers each
// motion found to the texels around its seed, and (reversed) around the place
// it came from, and keeps it where it carries their pixels better than the
// estimate did. Everything that was right stays as it was.

// How far a motion (output pixels) is from carrying the pixels around one
// place into the other frame: 3x3 samples, 2 pixels apart. Pixels fixed to
// the screen (LockedAt) are left out at either end - they stand still
// whatever moves beside them: beside a panel they made a wrong motion that
// lined its border up look like the best one, and scenery coming out from
// behind it look carried by some flat patch elsewhere. -1 when nothing else
// is left: no evidence either way.
float Carry(float2 pixel, float2 m, bool reverse) {
    float2 texel = 1.0 / float2(gW, gH);
    float total = 0, used = 0;
    [unroll] for (int y = -1; y <= 1; ++y)
    [unroll] for (int x = -1; x <= 1; ++x) {
        float2 a = (pixel + float2(x, y) * 2.0) * texel, b = a + m * texel;
        if (LockedAt(a) || LockedAt(b)) continue;
        float3 here = reverse ? gPrev.SampleLevel(gLin, a, 0).rgb : gCur.SampleLevel(gLin, a, 0).rgb;
        float3 there = reverse ? gCur.SampleLevel(gLin, b, 0).rgb : gPrev.SampleLevel(gLin, b, 0).rgb;
        total += Max3(abs(here - there));
        used += 1;
    }
    return used > 0 ? total / used : -1;
}
float2 Finite(float2 v) { return all(isfinite(v)) ? v : 0; }
// How far the search reaches, in output pixels: as far as the pair is long.
float SearchRadius() {
    return clamp(float(gSearchRings), 2.0, 8.0) * 20.0 * max(1.0, gH / 720.0);
}
[numthreads(8, 8, 1)]
void ResidMain(uint3 id : SV_DispatchThreadID) {
    uint mw, mh; gRawMv.GetDimensions(mw, mh);
    if (id.x >= mw || id.y >= mh) return;
    float2 scale = float2(gW, gH) / float2(mw, mh);
    float2 pixel = (float2(id.xy) + .5) * scale;
    float rb = max(0.0, Carry(pixel, Finite(gRawMv[id.xy]) * scale, false));
    float rf = gBoth != 0 ? max(0.0, Carry(pixel, Finite(gRawReverse[id.xy]) * scale, true)) : 0;
    gResid[id.xy] = f32tof16(rb) | (f32tof16(rf) << 16);
}

groupshared float gsBest[kTile * kTile];
groupshared uint gsWhere[kTile * kTile];
// One group per tile: its worst-carried texel, if it is bad enough to look up.
[numthreads(kTile, kTile, 1)]
void SeedMain(uint3 gid : SV_GroupID, uint3 tid : SV_GroupThreadID, uint index : SV_GroupIndex) {
    uint mw, mh; gRawMv.GetDimensions(mw, mh);
    uint tilesX = (mw + kTile - 1) / kTile;
    uint2 at = gid.xy * kTile + tid.xy;
    float r = 0;
    if (at.x < mw && at.y < mh) r = f16tof32(gResid[at] & 0xFFFF);
    gsBest[index] = isfinite(r) ? r : 0;
    gsWhere[index] = index;
    GroupMemoryBarrierWithGroupSync();
    [unroll] for (uint stride = kTile * kTile / 2; stride > 0; stride >>= 1) {
        if (index < stride && gsBest[index + stride] > gsBest[index]) {
            gsBest[index] = gsBest[index + stride];
            gsWhere[index] = gsWhere[index + stride];
        }
        GroupMemoryBarrierWithGroupSync();
    }
    if (index == 0) {
        uint tile = gid.y * tilesX + gid.x;
        uint w = gsWhere[0];
        float2 scale = float2(gW, gH) / float2(mw, mh);
        float2 pixel = (float2(gid.xy * kTile + uint2(w % kTile, w / kTile)) + .5) * scale;
        bool valid = gAccum >= 0 && gsBest[0] > .12;
        gTiles[tile * 2] = float4(pixel, gsBest[0], valid ? 1 : 0);
        gTiles[tile * 2 + 1] = float4(0, 0, 1, 0);
    }
}

groupshared float3 gsPatch[169];
groupshared float gsWeight[169];
groupshared float gsCost[64];
groupshared float2 gsMove[64];
groupshared float4 gsRunner[64];   // per thread: its best motion, cost, and runner-up
// The seed's 13x13 neighbourhood in the current frame against the previous
// frame moved by d, weighted towards the pixels that moved. every: 2 samples
// every other row and column (the coarse pass), 1 all of them.
float PatchCost(float2 centre, float2 d, uint every) {
    float2 texel = 1.0 / float2(gW, gH);
    float total = 0, mass = 0;
    [loop] for (uint j = 0; j < 13; j += every)
    [loop] for (uint i = 0; i < 13; i += every) {
        uint k = j * 13 + i;
        float2 at = (centre + float2(int(i) - 6, int(j) - 6) + d) * texel;
        total += gsWeight[k] * Max3(abs(gPrev.SampleLevel(gLin, at, 0).rgb - gsPatch[k]));
        mass += gsWeight[k];
    }
    // A match that leaves the picture is no match: the clamp repeats the edge.
    return total / max(mass, 1e-4) + (Inside((centre + d) * texel, texel) ? 0 : 1);
}
// The group's lowest cost and its motion into slot 0.
void Lowest(uint index, float cost, float2 move) {
    gsCost[index] = cost;
    gsMove[index] = move;
    GroupMemoryBarrierWithGroupSync();
    [unroll] for (uint stride = 32; stride > 0; stride >>= 1) {
        if (index < stride && gsCost[index + stride] < gsCost[index]) {
            gsCost[index] = gsCost[index + stride];
            gsMove[index] = gsMove[index + stride];
        }
        GroupMemoryBarrierWithGroupSync();
    }
}
// One group per tile. Every thread takes part in every barrier; a tile with
// no seed just has nothing to try.
[numthreads(64, 1, 1)]
void SearchMain(uint3 gid : SV_GroupID, uint index : SV_GroupIndex) {
    uint mw, mh; gRawMv.GetDimensions(mw, mh);
    uint tilesX = (mw + kTile - 1) / kTile;
    uint tile = gid.y * tilesX + gid.x;
    float4 seed = gTiles[tile * 2];
    bool active = seed.w > .5;
    float2 size = float2(gW, gH), texel = 1.0 / size, scale = size / float2(mw, mh);
    for (uint k = index; k < 169; k += 64) {
        float2 at = seed.xy + float2(int(k % 13) - 6, int(k / 13) - 6);
        gsPatch[k] = gCur.SampleLevel(gLin, at * texel, 0).rgb;
        int2 q = clamp(int2(at / scale), int2(0, 0), int2(mw - 1, mh - 1));
        float r = f16tof32(gResid[q] & 0xFFFF);
        // HUD in the patch (the verdict so far) stands still while the rest
        // moves: no single motion matches both, so it is left out.
        gsWeight[k] = LockedAt(at * texel) ? 0 : .2 + (isfinite(r) ? min(r, 1.0) : 0);
    }
    GroupMemoryBarrierWithGroupSync();
    // A flat patch matches anywhere: it has no motion to find. (The place an
    // object left, over a plain background, is the usual one.)
    float3 lo = 1, hi = 0;
    for (uint p = 0; p < 169; ++p) { lo = min(lo, gsPatch[p]); hi = max(hi, gsPatch[p]); }
    active = active && Max3(hi - lo) > .10;
    // Coarse: every 4 pixels as far as the pair reaches, half the samples.
    uint reach = uint(SearchRadius() / 4.0);
    uint side = 2 * reach + 1;
    // Each thread also keeps its runner-up more than 6 pixels from its best:
    // a real match (a ball on grass) has one clear minimum, a half-fitting
    // one - HUD and scenery in one patch - several about as good.
    float best = 1e30, second = 1e30;
    float2 move = 0;
    if (active)
        [loop] for (uint c = index; c < side * side; c += 64) {
            float2 d = (float2(c % side, c / side) - float(reach)) * 4.0;
            float cost = PatchCost(seed.xy, d, 2);
            if (cost < best) {
                if (any(abs(d - move) > 6.0)) second = min(second, best);
                best = cost; move = d;
            } else if (cost < second && any(abs(d - move) > 6.0)) second = cost;
        }
    gsRunner[index] = float4(move, best, second);
    Lowest(index, best, move);
    float2 around = gsMove[0];
    float coarse = gsCost[0], runnerUp = 1e30;
    [loop] for (uint r = 0; r < 64; ++r) {
        float4 other = gsRunner[r];
        runnerUp = min(runnerUp, any(abs(other.xy - around) > 6.0) ? other.z : other.w);
    }
    GroupMemoryBarrierWithGroupSync();
    // Then whole pixels around the best, all samples...
    best = 1e30; move = around;
    if (active)
        [loop] for (uint f = index; f < 121; f += 64) {
            float2 d = around + float2(f % 11, f / 11) - 5.0;
            float cost = PatchCost(seed.xy, d, 1);
            if (cost < best) { best = cost; move = d; }
        }
    Lowest(index, best, move);
    around = gsMove[0];
    GroupMemoryBarrierWithGroupSync();
    // ...and quarter pixels.
    best = 1e30; move = around;
    if (active)
        [loop] for (uint q = index; q < 81; q += 64) {
            float2 d = around + (float2(q % 9, q / 9) - 4.0) * .25;
            float cost = PatchCost(seed.xy, d, 1);
            if (cost < best) { best = cost; move = d; }
        }
    Lowest(index, best, move);
    if (index == 0 && active) {
        // Kept only if it matches, and clearly better than what was estimated.
        float2 raw = Finite(gRawMv.SampleLevel(gLin, seed.xy * texel, 0)) * scale;
        float kept = PatchCost(seed.xy, raw, 1);
        bool found = gsCost[0] < .25 && gsCost[0] + .05 < kept && coarse < .75 * runnerUp;
        gTiles[tile * 2 + 1] = float4(gsMove[0], gsCost[0], found ? 1 : 0);
    }
}

// Per motion texel: the estimate, or a motion found nearby where it carries
// this texel's pixels better. Backward: seeds of this tile and its eight
// neighbours. Forward: seeds whose object came from around here.
[numthreads(8, 8, 1)]
void TrackMain(uint3 id : SV_DispatchThreadID) {
    uint mw, mh; gRawMv.GetDimensions(mw, mh);
    if (id.x >= mw || id.y >= mh) return;
    float2 outMv = gRawMv[id.xy], outBack = gRawReverse[id.xy];
    if (gAccum >= 0 && gPass != kSkipSearch) {
        float2 scale = float2(gW, gH) / float2(mw, mh);
        float2 pixel = (float2(id.xy) + .5) * scale;
        uint packed = gResid[id.xy];
        float rb = f16tof32(packed & 0xFFFF), rf = f16tof32(packed >> 16);
        int tilesX = int((mw + kTile - 1) / kTile), tilesY = int((mh + kTile - 1) / kTile);
        int2 tile = int2(id.xy / kTile);
        // A motion found nearby is taken only where it removes most of the
        // estimate's error (below 60% of it, and by .03 at least): beside a
        // HUD a half-fitting one used to be taken over a blurred estimate,
        // and dragged the scene there the wrong way.
        if (isfinite(rb) && rb >= .06) {
            float best = min(rb - .03, rb * .6);
            [loop] for (int ty = -1; ty <= 1; ++ty)
            [loop] for (int tx = -1; tx <= 1; ++tx) {
                int2 t = tile + int2(tx, ty);
                if (t.x < 0 || t.y < 0 || t.x >= tilesX || t.y >= tilesY) continue;
                float4 found = gTiles[(t.y * tilesX + t.x) * 2 + 1];
                if (found.w < .5) continue;
                float e = Carry(pixel, found.xy, false);
                if (e >= 0 && e < best) { best = e; outMv = found.xy / scale; }
            }
        }
        if (gBoth != 0 && isfinite(rf) && rf >= .06) {
            float tilePx = float(kTile) * max(scale.x, scale.y);
            int k = int(ceil(SearchRadius() / tilePx)) + 1;
            float best = min(rf - .03, rf * .6);
            [loop] for (int ty = -k; ty <= k; ++ty)
            [loop] for (int tx = -k; tx <= k; ++tx) {
                int2 t = tile + int2(tx, ty);
                if (t.x < 0 || t.y < 0 || t.x >= tilesX || t.y >= tilesY) continue;
                uint i = uint(t.y * tilesX + t.x) * 2;
                float4 found = gTiles[i + 1];
                if (found.w < .5) continue;
                // Where that object was in the previous frame.
                float2 from = gTiles[i].xy + found.xy;
                if (any(abs(from - pixel) > tilePx * 1.5)) continue;
                float e = Carry(pixel, -found.xy, true);
                if (e >= 0 && e < best) { best = e; outBack = -found.xy / scale; }
            }
        }
    }
    gTrkMv[id.xy] = outMv;
    gTrkReverse[id.xy] = outBack;
}
)hlsl"
R"hlsl(// ---- Once per pair: the motion, block by block ------------------------------
// The estimate hands back the scene's motion for whatever is smaller than its
// own motion - the ball, and just as much a player running across a panning
// camera: on a football match it had the player with the ball wrong on three
// motion texels of four, and the tracker above still on one of five. So the
// whole field is refined the way frame-rate converters in television sets find
// true motion (3-D recursive search): every block of kBlock x kBlock texels
// tries the motion of the blocks around it, the estimate, the tracked motion
// and small steps from its own, and keeps what carries its pixels - and half
// as much a margin around them - best. Round after round, a motion that is
// right on part of a player (an edge, the number on the shirt) spreads over
// all of it, and wherever the estimate was right it stays. Both directions:
// dispatched with z = 2, z = 1 the previous -> current field. TexelMain then
// gives every texel the motion of its own block or a neighbouring one,
// whichever carries it best, so an edge follows the object, not the grid.
// Measured on the football match (15 FPS, motion texels off by more than 2):
// ball 26% -> 7%, the player with the ball 18% -> 9%, other players 10% -> 4%.

uint2 BlockCount() {
    uint mw, mh; gRawMv.GetDimensions(mw, mh);
    return uint2((mw + kBlock - 1) / kBlock, (mh + kBlock - 1) / kBlock);
}
// Where a block's entry is in gTiles: after the tracker's two per tile. Slices
// 0/1 hold the current -> previous field (one round reads the one, writes the
// other), 2/3 the previous -> current one. Per block: motion (output pixels,
// in the field's own direction), cost, 1.
uint BlockAt(int2 block, uint slice) {
    uint mw, mh; gRawMv.GetDimensions(mw, mh);
    uint base = ((mw + kTile - 1) / kTile) * ((mh + kTile - 1) / kTile) * 2;
    uint2 n = BlockCount();
    uint2 b = uint2(clamp(block, int2(0, 0), int2(n) - 1));
    return base + (slice * n.y + b.y) * n.x + b.x;
}
// The estimate (or the tracked motion) of one motion texel, output pixels, in
// the field's own direction. One texel, not a blend: between a ball and the
// grass a blend is the motion of neither.
float2 EstimateAt(int2 at, bool reverse, bool tracked, float2 scale) {
    uint mw, mh; gRawMv.GetDimensions(mw, mh);
    int3 q = int3(clamp(at, int2(0, 0), int2(mw - 1, mh - 1)), 0);
    float2 v;
    if (tracked) v = reverse ? gTrackReverse.Load(q) : gTrackMv.Load(q);
    else v = reverse ? gRawReverse.Load(q) : gRawMv.Load(q);
    return Finite(v) * scale;
}
// Texels of a block (offsets from its corner) whose tracked motion is tried
// too - the tracker's motion can sit on a few texels only.
static const int2 kInside[7] = {int2(1,1),int2(1,2),int2(2,1),int2(0,0),int2(3,0),int2(0,3),int2(3,3)};

// The window a block is judged on, as offsets in texels from its corner: its
// own 16 texels, then every other texel of a margin one block wide.
static const int2 kMargin[32] = {int2(-4,-4),int2(-2,-4),int2(0,-4),int2(2,-4),int2(4,-4),int2(6,-4),
    int2(-4,-2),int2(-2,-2),int2(0,-2),int2(2,-2),int2(4,-2),int2(6,-2),int2(-4,0),int2(-2,0),int2(4,0),
    int2(6,0),int2(-4,2),int2(-2,2),int2(4,2),int2(6,2),int2(-4,4),int2(-2,4),int2(0,4),int2(2,4),
    int2(4,4),int2(6,4),int2(-4,6),int2(-2,6),int2(0,6),int2(2,6),int2(4,6),int2(6,6)};
// The blocks whose motion is tried: the eight around, and four three out,
// which carry a motion across a flat patch in fewer rounds.
static const int2 kNear[12] = {int2(-1,0),int2(1,0),int2(0,-1),int2(0,1),int2(-1,-1),int2(1,-1),
    int2(-1,1),int2(1,1),int2(-3,0),int2(3,0),int2(0,-3),int2(0,3)};
// Small steps from the block's own motion, in texels (doubled in the first round).
static const float2 kNudge[8] = {float2(1,0),float2(-1,0),float2(0,1),float2(0,-1),
    float2(.5,.5),float2(-.5,-.5),float2(.5,-.5),float2(-.5,.5)};
groupshared float3 gsHere[48];
groupshared float2 gsHereAt[48];
groupshared float gsHereW[48];
// A sample's miss counts up to this much: past it a sample simply does not
// match. Uncapped, how badly the wrong samples missed - noise against other
// noise - swayed the choice more than the samples that did match, and a
// motion a pixel off won by chance.
static const float kMissCap = .25;
// How badly a motion (output pixels) carries the window into the other frame.
// HUD pixels (LockedAt) at either end and samples carried off the picture tell
// nothing - scenery coming out from under a panel, like scenery at the edge of
// a pan, came from a place that cannot be seen, and counted as misses they
// made some other motion win there. -1 when too little is left to judge
// (under a quarter of the window).
float WindowCost(float2 m, bool reverse) {
    float2 texel = 1.0 / float2(gW, gH);
    float total = 0, mass = 0, full = 0;
    [loop] for (uint k = 0; k < 48; ++k) {
        float w = gsHereW[k];
        full += k < 16 ? 1.0 : .5;
        if (w <= 0) continue;
        float2 b = (gsHereAt[k] + m) * texel;
        if (!Inside(b, texel) || LockedAt(b)) continue;
        float3 there;
        if (reverse) there = gCur.SampleLevel(gLin, b, 0).rgb; else there = gPrev.SampleLevel(gLin, b, 0).rgb;
        total += w * min(Max3(abs(gsHere[k] - there)), kMissCap);
        mass += w;
    }
    return mass >= .25 * full ? total / mass : -1;
}
// One group of 32 per block and direction: a candidate per thread, the best
// kept. Penalties keep a block on its neighbours' motion unless another one
// is clearly better.
[numthreads(32, 1, 1)]
void BlockMain(uint3 gid : SV_GroupID, uint index : SV_GroupIndex) {
    uint mw, mh; gRawMv.GetDimensions(mw, mh);
    int2 block = int2(gid.xy);
    bool reverse = gid.z != 0;
    uint src = (reverse ? 2u : 0u) + (gPass & 1u), dst = (reverse ? 2u : 0u) + ((gPass + 1u) & 1u);
    float2 size = float2(gW, gH), texel = 1.0 / size, scale = size / float2(mw, mh);
    bool first = gPass == 0;
    int2 corner = block * int(kBlock), middle = corner + int(kBlock / 2);
    // A block the estimate already carries - every texel of it below
    // TrackMain's bar (ResidMain) - keeps it: most of a picture, the pitch,
    // the sky, and all the rounds cost there went for nothing (27 ms a pair
    // at 1440p on an RTX 3060, searching everywhere). A texel held still as
    // HUD has no residual (nothing of it is compared), so it is searched: on
    // a player wrongly held, the motion his edges show is what lets go of him.
    if (index < kBlock * kBlock) {
        int2 t = corner + int2(index % kBlock, index / kBlock);
        bool inside = all(t < int2(mw, mh));
        uint packed = inside ? gResid[t] : 0u;
        float r = f16tof32(reverse ? packed >> 16 : packed & 0xFFFF);
        gsCost[index] = inside && LockedAt((float2(t) + .5) / float2(mw, mh)) ? 1.0 : (isfinite(r) ? r : 0);
    }
    GroupMemoryBarrierWithGroupSync();
    [unroll] for (uint span = kBlock * kBlock / 2; span > 0; span >>= 1) {
        if (index < span) gsCost[index] = max(gsCost[index], gsCost[index + span]);
        GroupMemoryBarrierWithGroupSync();
    }
    bool quiet = gsCost[0] < .06;
    GroupMemoryBarrierWithGroupSync();
    if (quiet) {
        if (index == 0)
            gTiles[BlockAt(block, dst)] = float4(first ? EstimateAt(middle, reverse, true, scale) : gTiles[BlockAt(block, src)].xy, 0, 1);
        return;
    }
    // Settled: this block and every block it takes a motion from kept theirs
    // in the last round, so this one would come out the same again (.w: 2
    // where a round changed the motion, 1 where it did not).
    if (gPass >= 2) {
        // Worked out once, then shared: the whole group leaves together.
        if (index == 0) {
            bool settled = gTiles[BlockAt(block, src)].w < 1.5;
            [unroll] for (int n = 0; n < 12; ++n) settled = settled && gTiles[BlockAt(block + kNear[n], src)].w < 1.5;
            gsCost[0] = settled ? 1 : 0;
        }
        GroupMemoryBarrierWithGroupSync();
        bool settled = gsCost[0] > .5;
        GroupMemoryBarrierWithGroupSync();
        if (settled) {
            if (index == 0) gTiles[BlockAt(block, dst)] = gTiles[BlockAt(block, src)];
            return;
        }
    }
    for (uint k = index; k < 48; k += 32) {
        int2 t = block * int(kBlock) + (k < 16 ? int2(k % 4, k / 4) : kMargin[k - 16]);
        float2 at = (float2(t) + .5) * scale;
        float2 uv = at * texel;
        bool counted = all(t >= 0) && t.x < int(mw) && t.y < int(mh) && !LockedAt(uv);
        gsHereAt[k] = at;
        gsHere[k] = reverse ? gPrev.SampleLevel(gLin, uv, 0).rgb : gCur.SampleLevel(gLin, uv, 0).rgb;
        gsHereW[k] = counted ? (k < 16 ? 1.0 : .5) : 0;
    }
    GroupMemoryBarrierWithGroupSync();
    // The first round starts from the tracked motion, the others from the last.
    float2 mine = first ? EstimateAt(middle, reverse, true, scale) : gTiles[BlockAt(block, src)].xy;
    float2 candidate = mine;
    float penalty = 0;
    bool used = true;
    if (index >= 1 && index <= 12) {
        int2 near = block + kNear[index - 1];
        int2 nearMiddle = clamp(near, int2(0, 0), int2(BlockCount()) - 1) * int(kBlock) + int(kBlock / 2);
        candidate = first ? EstimateAt(nearMiddle, reverse, true, scale) : gTiles[BlockAt(near, src)].xy;
    } else if (index == 13) {
        // The estimate and the tracked motion do not change from round to
        // round: once tried, what won has spread with the rest.
        candidate = EstimateAt(middle, reverse, false, scale); penalty = .004; used = first;
    } else if (index >= 14 && index <= 20) {
        candidate = EstimateAt(corner + kInside[index - 14], reverse, true, scale); penalty = .002; used = first;
    } else if (index >= 21 && index <= 28) {
        candidate = mine + kNudge[index - 21] * (first ? 2.0 : 1.0) * scale; penalty = .008;
    } else if (index > 28) used = false;
    float cost = used ? WindowCost(candidate, reverse) : 1e30;
    if (cost < 0) cost = index == 0 ? 0 : 1e30;    // nothing to judge by: stay
    cost += penalty;
    gsCost[index] = cost;
    gsMove[index] = candidate;
    GroupMemoryBarrierWithGroupSync();
    [unroll] for (uint stride = 16; stride > 0; stride >>= 1) {
        if (index < stride && gsCost[index + stride] < gsCost[index]) {
            gsCost[index] = gsCost[index + stride];
            gsMove[index] = gsMove[index + stride];
        }
        GroupMemoryBarrierWithGroupSync();
    }
    if (index == 0) gTiles[BlockAt(block, dst)] = float4(gsMove[0], gsCost[0], any(gsMove[0] != mine) ? 2 : 1);
}

// Per motion texel and direction (z = 2): its own block's motion or one of the
// four next to it, by how well it carries the 3x3 texels around it. Written
// over the tracked motion, which FixMain reads next.
[numthreads(8, 8, 1)]
void TexelMain(uint3 id : SV_DispatchThreadID) {
    uint mw, mh; gRawMv.GetDimensions(mw, mh);
    if (id.x >= mw || id.y >= mh) return;
    bool reverse = id.z != 0;
    // No history, or a texel the estimate carries (and not held still as HUD,
    // as BlockMain): TrackMain's motion stays.
    uint packed = gResid[id.xy];
    float r = f16tof32(reverse ? packed >> 16 : packed & 0xFFFF);
    bool held = LockedAt((float2(id.xy) + .5) / float2(mw, mh));
    if (gAccum < 0 || !(r >= .06 || held)) return;
    uint slice = (reverse ? 2u : 0u) + (kBlockRounds & 1u);
    float2 size = float2(gW, gH), texel = 1.0 / size, scale = size / float2(mw, mh);
    int2 block = int2(id.xy / kBlock);
    const int2 around[5] = {int2(0,0), int2(-1,0), int2(1,0), int2(0,-1), int2(0,1)};
    float best = 1e30;
    float2 move = 0;
    [loop] for (int i = 0; i < 5; ++i) {
        float2 m = gTiles[BlockAt(block + around[i], slice)].xy;
        float total = 0, mass = 0;
        [unroll] for (int y = -1; y <= 1; ++y)
        [unroll] for (int x = -1; x <= 1; ++x) {
            float2 at = (float2(int2(id.xy) + int2(x, y)) + .5) * scale;
            float2 a = at * texel, b = (at + m) * texel;
            // As WindowCost: off the picture or HUD, no evidence either way.
            if (!Inside(a, texel) || !Inside(b, texel) || LockedAt(a) || LockedAt(b)) continue;
            float3 here, there;
            if (reverse) { here = gPrev.SampleLevel(gLin, a, 0).rgb; there = gCur.SampleLevel(gLin, b, 0).rgb; }
            else { here = gCur.SampleLevel(gLin, a, 0).rgb; there = gPrev.SampleLevel(gLin, b, 0).rgb; }
            total += min(Max3(abs(here - there)), kMissCap);
            mass += 1;
        }
        // Nothing to judge by: only the texel's own block can have it.
        float cost = (mass > 0 ? total / mass : (i == 0 ? 0 : 1e30)) + (i == 0 ? 0 : .01);
        if (cost < best) { best = cost; move = m; }
    }
    if (reverse) gTrkReverse[id.xy] = move / scale; else gTrkMv[id.xy] = move / scale;
}
)hlsl"
R"hlsl(
// ---- Every frame in between ------------------------------------------------
// Beside the HUD - its anti-aliased edge, a text shadow, a see-through panel -
// a pixel is part HUD and part scene: colour = still layer + k x scene. Both
// real frames give the colour, and the scene's motion gives the scene at this
// place at either time from where it can be seen, so k follows; the frame in
// between then keeps the HUD part still while the scene part moves. Warping
// such a pixel dragged the HUD's edge along; holding it froze the scene.
// Returns 0 away from the HUD, 1 with the layered colour, and 2 on a HUD edge
// where the scene cannot be seen at either time (dense text): there a fade
// between the real frames keeps the edge where it is.
uint Layered(uint2 id, float2 uv, float2 texel, float2 s, float3 before, float3 now, out float3 result) {
    result = lerp(before, now, gT);
    float near = 0;
    [unroll] for (int y = -1; y <= 1; ++y)
    [unroll] for (int x = -1; x <= 1; ++x)
        near = max(near, HudOf(gHud[clamp(int2(id) + int2(x, y), int2(0, 0), int2(gW - 1, gH - 1))]));
    if (near < .5) return 0;
    if (dot(s, s) < 2.25) return 2;
    float2 a = uv + s * texel, b = uv - s * texel;          // the scene here now / before
    float2 p = uv + gT * s * texel, c = uv - (1.0 - gT) * s * texel;
    if (!Inside(a, texel) || !Inside(b, texel) || HudAt(a) >= .5 || HudAt(b) >= .5) return 2;
    bool vp = Inside(p, texel) && HudAt(p) < .5, vc = Inside(c, texel) && HudAt(c) < .5;
    if (!vp && !vc) return 2;
    float3 sceneNow = gPrev.SampleLevel(gLin, a, 0).rgb, sceneBefore = gCur.SampleLevel(gLin, b, 0).rgb;
    float3 fromPrev = gPrev.SampleLevel(gLin, p, 0).rgb, fromCur = gCur.SampleLevel(gLin, c, 0).rgb;
    float3 sceneThen = vp && vc ? lerp(fromPrev, fromCur, gT) : (vp ? fromPrev : fromCur);
    float3 dScene = sceneNow - sceneBefore, dPixel = now - before;
    float energy = dot(dScene, dScene);
    if (energy < .0048) return 2;                          // the scene barely changed here
    float k = saturate(dot(dPixel, dScene) / energy);
    if (Max3(abs(dPixel - k * dScene)) > .06) return 2;    // not a layer over this scene
    result = saturate(now + k * (sceneThen - sceneNow));
    return 1;
}

// The scene's own motion around a place: a vector median of the motion there
// and on a ring 24 and 48 pixels out (at 720p) - far enough to reach past a
// player or a ball, so it is the pitch's motion, not theirs.
float2 SceneMotion(float2 uv, float2 texel, float2 scale) {
    float unit = max(1.0, gH / 720.0);
    const float2 ring[8] = {float2(24,0),float2(-24,0),float2(0,24),float2(0,-24),
        float2(48,48),float2(-48,48),float2(48,-48),float2(-48,-48)};
    float2 votes[9];
    votes[0] = Motion(uv, scale, false);
    [unroll] for (int i = 0; i < 8; ++i) votes[i + 1] = Motion(uv + ring[i] * unit * texel, scale, false);
    float2 scene = votes[0];
    float lowest = 1e30;
    [unroll] for (int a = 0; a < 9; ++a) {
        float cost = 0;
        [unroll] for (int b = 0; b < 9; ++b) { float2 d = abs(votes[a] - votes[b]); cost += d.x + d.y; }
        if (cost < lowest) { lowest = cost; scene = votes[a]; }
    }
    return scene;
}

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    if (id.x >= gW || id.y >= gH) return;
    float4 p0 = gPrev[id.xy], c0 = gCur[id.xy];
    if (gT >= 1) { gDst[id.xy] = c0; return; }
    if (gT <= 0) { gDst[id.xy] = p0; return; }
    // The HUD is drawn as it stands - from the nearer real frame when it
    // changed (a counter ticking over) - never moved with the scene.
    float still = Max3(abs(p0.rgb - c0.rgb));
    float3 overlay = still < .02 ? c0.rgb : (gT < .5 ? p0.rgb : c0.rgb);
    float state = gHud[id.xy];
    float layer = smoothstep(.3, .7, HudOf(state));
    if (layer >= 1) { gDst[id.xy] = float4(overlay, c0.a); return; }
    float2 size = float2(gW, gH), texel = 1.0 / size;
    float2 uv = (float2(id.xy) + .5) * texel;
    uint mw, mh; gMv.GetDimensions(mw, mh);
    float2 scale = size / float2(mw, mh);
    float2 m = Motion(uv, scale, false);
    [unroll] for (int i = 0; i < 2; ++i)
        m = Motion(uv - (1.0 - gT) * m * texel, scale, false);
    float4 best = Follow(uv, texel, scale, m, true);
    float2 bestMove = m;
    if (gBoth != 0) {
        float2 f = Motion(uv, scale, true);
        [unroll] for (int j = 0; j < 2; ++j)
            f = Motion(uv + gT * f * texel, scale, true);
        float4 candidate = Follow(uv, texel, scale, f, true);
        if (candidate.w > best.w) { best = candidate; bestMove = f; }
    }
    // At a boundary, test actual neighbouring vectors instead of averaging
    // foreground and background into a motion belonging to neither object.
    if (best.w < .8) {
        const float2 offsets[4] = {float2(-4,0),float2(4,0),float2(0,-4),float2(0,4)};
        [loop] for (int k = 0; k < 4; ++k) {
            float2 candidateMv = Motion(uv + offsets[k] * texel * scale, scale, false);
            float4 candidate = Follow(uv, texel, scale, candidateMv, true);
            if (candidate.w > best.w) { best = candidate; bestMove = candidateMv; }
        }
    }
    // A small object can cross a pixel that was background in BOTH inputs.
    // Inverse lookup alone follows the background and erases the object.
    // Look for a nearby moving surface and validate its two endpoints. The
    // background may itself be panning. Restricting dilation to zero local
    // motion left holes/jagged edges on a moving object over moving scenery.
    // Only a candidate that moves further apart from the scene around - with
    // matching, consistent endpoints - can win. "Faster" alone was wrong the
    // moment the camera follows the play: the pitch then moves faster than
    // the ball, won as the surface in front, and painted grass over the ball
    // (lost in one generated frame of eight on a broadcast-style football
    // camera; none now).
    bool crossing = false;
    if (gBoth != 0) {
        const float2 ring[8] = {float2(1,0),float2(-1,0),float2(0,1),float2(0,-1),
            float2(.707,.707),float2(-.707,.707),float2(.707,-.707),float2(-.707,-.707)};
        float2 scene = SceneMotion(uv, texel, scale);
        float bestLength = length(bestMove - scene);
        // A few pixels apart from the scene at least: the estimate wavers by
        // that much on its own, and inside a flat HUD panel any motion matches
        // at both ends - a waver taken as a crossing moved the panel's text.
        float apart = 4.0 * max(1.0, gH / 720.0);
        // At 15 FPS a surface travels about four 60-Hz ticks between inputs.
        // The old fixed radius could not reach it and erased small objects.
        // Spend the wider, bidirectional search only on these longer pairs.
        uint rings = clamp(gSearchRings, 2u, 8u);
        [loop] for (uint direction = 0; direction < (rings > 2 ? 2u : 1u); ++direction)
        [loop] for (uint radius = 1; radius <= rings; ++radius)
        [loop] for (int r = 0; r < 8; ++r) {
            float2 moving = Motion(uv + ring[r] * (radius * 8.0) * texel * scale, scale, direction != 0);
            if (length(moving - scene) > max(bestLength + 1.0, apart)) {
                float4 candidate = Follow(uv, texel, scale, moving, false);
                // A surface moving apart from the scene that matches at both
                // of its ends passes in front of this pixel: the background
                // behind a ball is seen at both ends as well, so a perfect
                // background match must not outrank it - that left the ball
                // hollow, a ring of yellow around grass. Among crossings, the
                // surer one.
                if (candidate.w > .9 && (!crossing || candidate.w > best.w)) {
                    best = candidate; bestMove = moving; bestLength = length(moving - scene); crossing = true;
                }
            }
        }
    }
    float3 safe = gT < .5 ? p0.rgb : c0.rgb;
    float3 result = best.w > .1 ? best.rgb : safe;
    // A pixel that has not changed for several pairs shows what it showed -
    // the inside of a HUD panel no motion can be read from, or a flat patch
    // of scene - unless a surface was found crossing it in between. No clamp
    // against the UNWARPED neighbourhood: it erases objects crossing it.
    // The ordinary lookup can lead straight to a small fast object passing a
    // still pixel too - the tracker's motion takes it there. It counts as a
    // crossing when that surface moved at both of its ends (the object left
    // the one place and arrived at the other). The inside of a static panel
    // the estimate called moving did not, and keeps the rule below.
    if (!crossing && still < .008 && best.w > .9 && dot(bestMove, bestMove) >= 2.25 &&
        Max3(abs(best.rgb - c0.rgb)) > .08) {
        float2 p = uv + gT * bestMove * texel, c = uv - (1.0 - gT) * bestMove * texel;
        float leftP = Max3(abs(gPrev.SampleLevel(gLin, p, 0).rgb - gCur.SampleLevel(gLin, p, 0).rgb));
        float cameC = Max3(abs(gCur.SampleLevel(gLin, c, 0).rgb - gPrev.SampleLevel(gLin, c, 0).rgb));
        crossing = leftP > .06 && cameC > .06;
    }
    if (!crossing && still < .008 && StillOf(state) >= 3) result = c0.rgb;
    else if (!crossing) {
        float3 layered;
        uint edge = Layered(id.xy, uv, texel, Motion(uv, scale, false), p0.rgb, c0.rgb, layered);
        // A scene surface followed with confidence beside the HUD stays.
        if (edge == 1 || (edge == 2 && best.w < .8)) result = layered;
    }
    gDst[id.xy] = float4(saturate(lerp(result, overlay, layer)), c0.a);
}
)hlsl";
