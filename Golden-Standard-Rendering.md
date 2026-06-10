# FFXIV-TV — Golden Standard Rendering Reference

**Version**: v0.5.164
**Status**: ✅ Fully working. All goals achieved.
**Date confirmed**: 2026-04-18

This document describes the complete, confirmed-working rendering pipeline for FFXIV-TV.
Reference it before touching any injection, shader, or hook code. If something breaks,
compare against this state first.

---

## What "working" means

- TV screen is visible in the 3D game world at the configured position
- 3D geometry and characters correctly occlude the TV (depth testing works)
- All game HUD elements (chat, hotbar, map, nameplates, icons, party frames) render **in front** of the TV
- Dalamud/ImGui windows render in front of the TV
- No crashes, no flicker, no visual corruption

---

## The FFXIV render pipeline (relevant portion)

This is the confirmed frame structure at the HUD stage, established through diagnostic
instrumentation across dozens of versions.

```
[3D scene pass]
  OMSetRenderTargets(R16F HDR surface + MainSceneDSV)   ← _mainSceneDsvPtr, _mainSceneRtvPtr
  DrawIndexed × N  (geometry, characters, environment)
  → R16F HDR surface filled with 3D scene

[Post-processing — all compute/UAV, no OMSetRenderTargets changes]
  DLSS / SSAO / bloom / tonemap
  → Tonemap OUTPUT written to LDR BGRA8 surface (0x...BE0 in current session)
    via compute shader UAV write. No ClearRenderTargetView. No DrawIndexed.
    LDR now contains: complete tonemapped 3D scene, ready for HUD.

[HUD pass — _inUiPass = true]
  OMSetRenderTargets(LDR BGRA8, no DSV)                 ← sets _inUiPass = true
  DrawIndexed  (first HUD element — e.g. party icon)    ← CF-DI fires HERE
  DrawIndexed  (nameplate text)
  DrawIndexed  (chat box)
  DrawIndexed × N  (hotbar, map, all other HUD)

[BB composite]
  OMSetRenderTargets(Backbuffer BGRA8, no DSV)           ← IsBackbuffer() = true
  DrawIndexed  (composite blit: LDR → BB)
  DrawIndexed / Draw × N  (any remaining passes on BB)

[Dalamud ImGui]
  ImGui render pass on BB                                ← always on top of everything
```

**Key confirmed facts:**
- FFXIV **never** calls `ClearRenderTargetView` on the LDR BGRA8 surface. It overwrites LDR
  via UAV/compute. Any strategy based on "inject after LDR clear" is impossible.
- There are multiple BGRA8 full-res surfaces per frame (SMAA, TAA, shadowmap intermediates,
  etc.). Only `_targetInjectRtvPtr` (the one that reaches the BB composite) is the real LDR.
- Tonemap has NO DrawIndexed. The first DrawIndexed on LDR is always a HUD element.
- The depth buffer (`_mainSceneDsvPtr`) is frozen from the 3D scene pass and remains valid
  through the entire HUD pass for depth testing purposes.

---

## The injection: how it works

### Hook

`DrawIndexedDetour` (vtable[12] on the D3D11 immediate context).

### Trigger condition

```csharp
CfDiEnabled                         // toggle (default true)
&& pCtx == _contextPtr              // our device context
&& _sceneDrawnThisFrame             // 3D scene has rendered this frame
&& !_frameInjectionDone             // haven't injected yet this frame
&& _inUiPass                        // past the 3D→2D transition
&& _currentBbRtvPtr == 0            // BB not yet bound (still in HUD-on-LDR phase)
&& _initialized && resources valid  // shaders, cbuffer, depth states ready
&& rtvPtr == _targetInjectRtvPtr    // current RTV is the known LDR surface
&& cfValid (IsLdrFullRes)           // double-check: BGRA8 full-res, not BB
```

### What happens

```
1. _frameInjectionDone = true         (block any subsequent inject paths)
2. ExecuteInlineDraw(ldrRtv,          (draw TV rect onto LDR)
       useDepth: true,
       overrideDepthState: _dsReverseZ,
       useLdrShader: true)
3. calledOriginal = true
4. _drawIndexedHook.Original(...)     (first HUD DrawIndexed executes, draws on top)
5. [all subsequent HUD draws execute normally via finally → all on top of TV]
6. [BB composite carries LDR (TV + HUD) to BB]
```

**Inject-first is critical.** Original() runs AFTER our inject so that the first HUD
element (and all subsequent ones) draws on top of the TV. If Original() ran first
(Original-first), the first HUD element would be under the TV.

### Fallback

If CF-DI misses (e.g. `_targetInjectRtvPtr` not yet learned on first frame), the OMSetRT
inject at BB-bind time fires as a fallback. It still draws with depth testing, but TV may
appear in front of some HUD on those rare frames. This is acceptable — it only affects the
first 1–2 frames after plugin load before `_targetInjectRtvPtr` is learned.

---

## Key state machine fields

| Field | Purpose |
|-------|---------|
| `_inUiPass` | True after DSV→no-DSV OMSetRT transition (or BGRA8 fallback) — marks post-scene phase |
| `_sceneDrawnThisFrame` | True once main scene DSV transition confirmed — latches until PrepareHooks resets |
| `_frameInjectionDone` | Volatile bool — set by first successful inject; blocks all other paths for the frame |
| `_targetInjectRtvPtr` | The stable LDR BGRA8 RTV pointer. Learned in PrepareHooks from previous frame's `_lastSeenValidRtvPtr`. Reset to 0 on territory change. |
| `_trackedDsv` | Frozen scene depth stencil view — captured at `_inUiPass` start, never updated during HUD phase |
| `_cachedLiveRtv` | Current frame's BB RTV from renderer singleton walk (`renderer→+0x70→+0x60→+0x68`). Used by `IsBackbuffer()`. |
| `_dsReverseZ` | Depth-stencil state: ComparisonFunction.Greater (reversed-Z), DepthWriteMask.Zero (read-only) |
| `_dsReverseZWrite` | Same but DepthWriteMask.All — used when we also need to write depth |
| `_postBloomRtvCache` | Per-frame cache of `IsLdrFullRes()` results keyed by RTV ptr. Cleared in PrepareHooks. |

---

## `IsBackbuffer()` — how the BB is identified

Primary: `_cachedLiveRtv` — walked from the FFXIV renderer singleton each frame.
Signature: `48 8B 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 85 C0` → `DAT_1427F1A80`
Walk: `renderer → +0x70 → +0x60 → +0x68` = current frame's BB RTV pointer.

Fallback: `_knownBackbufferRtvPtrs` — learned via texture ptr matching from Dalamud's
ImGui BB bind (the last OMSetRenderTargets call after `_pendingLearnBackbuffer` is set).

`IsLdrFullRes()` — BGRA8 + full render resolution (from `kdev->Width/Height`) + not in BB set.

---

## `ExecuteInlineDraw` — what it does

1. Saves full D3D11 pipeline state (VS, PS, IA, OM, RS, viewports, cbuffers, SRVs, samplers)
2. Calls `_omSetRTHook.Original(ldrRtv, _trackedDsv)` — binds LDR + scene depth (re-entrant, bypasses hook)
3. Sets `_dsReverseZ` as depth-stencil state (Greater, no write)
4. Updates vertex buffer with current world-space screen corners
5. Updates cbuffer with current ViewProj matrix + tint + brightness
6. Draws 6 vertices (2 triangles = quad)
7. Restores full pipeline state

The `restoreAfterDraw: true` flag ensures FFXIV's subsequent draws (HUD, composite) see
the exact pipeline state they expect. Without this, HUD draws would use our shaders.

---

## Depth testing — why it works

- `_trackedDsv` is captured from the 3D scene pass (mainSceneDSV, full render resolution)
- It contains depth values written by all 3D geometry (characters, environment, etc.)
- Reversed-Z: near = 1.0, far = 0.0. `ComparisonFunction.Greater` means our pixel passes
  only if our depth value is GREATER than what's in the buffer (i.e., closer to camera than
  the geometry already drawn there)
- `DepthWriteMask.Zero`: we don't write depth, so the scene depth buffer is unchanged after our inject
- Dimension check (`_depthCompatible`): if DSV and RTV don't match dimensions, depth is skipped
  (D3D11 silently renders nothing on mismatch — always check before binding both)

---

## Checkpoint: v0.5.164 state

**Confirmed working by Trist (Sansflaire), 2026-04-18:**
- TV visible in world space at configured position ✓
- Characters correctly occlude the TV (stand in front → TV hidden behind them) ✓
- All game HUD elements in front of TV (chat, hotbar, map, nameplates, party icons) ✓
- Inject path: CF-DI (DrawIndexedDetour), inject-first, `_targetInjectRtvPtr` guard ✓
- `cfDiCount` increments at ~60fps, `omSetRtCount = 0` (CF-DI handles all frames) ✓
- No crashes, no flicker ✓

**If this breaks:**
1. Check `/inject` endpoint — is `cfDiCount` incrementing? Is `lastInjectRtvPtr` matching `targetInjectPtr`?
2. If `cfDiCount = 0`: `_targetInjectRtvPtr` may be stale (territory change, patch). Check `targetInjectPtr` vs `lastSeenValidPtr`.
3. If TV invisible despite inject firing: check `lastInjectFmt` and `lastInjectSize` — should be `B8G8R8A8_UNorm` at render resolution.
4. If HUD behind TV again: CF-DI may have reverted to Original-first. Confirm inject-first order in `DrawIndexedDetour`.
5. If depth broken (TV over characters): check `_trackedDsv` is being captured, `_depthCompatible` is not forcing no-depth fallback.
6. If BB identification fails (`cachedLiveRtv = 0`): renderer singleton signature may need update after a game patch.

---

## What NOT to do (hard-won lessons)

| Don't | Why |
|-------|-----|
| Inject into R16F HDR surface | Bloom overwrites it. Every single time. |
| Inject into any surface at or after BB-bind | TV ends up over HUD. Pipeline is past the point of no return. |
| Use `ClearRTV` as HUD-start boundary | FFXIV never ClearRTVs LDR. It uses compute/UAV writes. |
| Fire CF-DI on any IsLdrFullRes surface | Multiple BGRA8 full-res intermediates exist. Only `_targetInjectRtvPtr` reaches the screen. |
| Use `useDepth: false` for the inject | TV becomes a flat overlay over everything including characters. |
| Use `ComparisonFunction.Greater` (strict) when inject tests its own written depth | Exact match fails. Use GreaterEqual if you need to test against previously-written depth. |
| Update `_trackedDsv` during `_inUiPass` | Post-processing DSVs have different dimensions → D3D11 silently renders nothing. |
| Use Original-first in CF-DI | The first DrawIndexed on LDR is a HUD element, not a tonemap blit. Original-first puts that element under TV. |
