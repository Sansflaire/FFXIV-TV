# Working: Game UI Drawn Over Rect

**Last verified:** v0.5.147
**Status:** PARTIALLY WORKING — content visible (gradient/video correct), game UI drawn IN FRONT of rect on ~56% of frames (CF-DI path). Remaining ~44% use BB-fallback (rect over HUD ordering — original known issue). White rect from v0.5.146 OMSetRT-LDR firing too early is fixed.

---

## The Mechanism That Works (v0.5.147)

**CF-DI (~56% of frames) + BB-fallback (~44% of frames) into the BGRA8 LDR intermediate. Content visible on all frames; HUD ordering correct on CF-DI frames.**

### v0.5.146 OMSetRT-LDR inject — REVERTED in v0.5.147 (caused white rect)

v0.5.146 added OMSetRT-LDR: inject when FFXIV calls `OMSetRenderTargets(LDR)` after scene end. This achieved `omSetRtCount: 0`. **But**: it fired on the FIRST LDR bind (tonemap setup, LDR not yet filled). The unknown tonemap mechanism then overwrote our inject with the tonemapped scene — bright sky in the rect position = pure white rect.

**v0.5.147 fix:** Added `prevNoDsvPtr == rtvPtr` guard to OMSetRT-LDR, requiring LDR to have been the PREVIOUS no-DSV surface (second bind = HUD setup). In practice FFXIV does not re-bind LDR before HUD draws on miss frames, so `omSetRtLdrCount = 0`. CF-DI handles ~56% of frames; BB-fallback (wrong ordering) handles the remaining ~44%.

**Root cause of the 44% miss**: LDR RTV ptr rotates across swapchain buffers. `_targetInjectRtvPtr` learned from frame N does not match frame N+1's LDR ptr on ~44% of frames. CF-DI guard `rtvPtr == _targetInjectRtvPtr` fails → miss → BB-fallback fires with wrong ordering (rect over HUD).

---

## The Mechanism That Works (v0.5.132 — still applies for CF-DI path)

**CF-DI (Cross-Frame DrawIndexed) inject into the BGRA8 LDR intermediate.**

### FFXIV D3D11 Rendering Pipeline (confirmed)

```
3D scene geometry → R16G16B16A16_Float (DSV bound, reversed-Z)
                          ↓
             Bloom / tonemap / post-process chain
                          ↓
             BGRA8 LDR intermediate (no DSV, full-res)
                          ↓
   ← CF-DI INJECT HERE (Original runs first, then we draw rect)
                          ↓
             Native UI draws INTO BGRA8 intermediate  ← HUD is baked in here
                          ↓
             Composite DrawIndexed: intermediate → Backbuffer (BB)
                          ↓
             Dalamud ImGui Draw → BB
                          ↓
             Present
```

**Why this works:**
1. We hook `DrawIndexed`. On the first qualifying DrawIndexed to a BGRA8 full-res non-BB surface (`cfValid && !_knownBackbufferRtvPtrs.Contains(rtvPtr)`), we call `Original()` first so the tonemap blit completes, then inject the rect.
2. Our rect is now baked into the BGRA8 intermediate.
3. FFXIV native UI (hotbar, inventory, map, etc.) draws into the BGRA8 intermediate AFTER our inject.
4. The composite blit carries everything — scene + rect + HUD — to the BB together.
5. HUD was drawn after our rect in the same surface → HUD is always on top. No special ordering tricks. It falls out of the pipeline naturally.

**Why no bloom:**
The inject is POST-TONEMAP. R16G16B16A16_Float (the HDR pre-bloom surface) is never touched for color. Our pixel shader (`PS_LDR`) writes directly to the BGRA8 LDR surface, bypassing FFXIV's bloom pass entirely.

### Inject Code (D3DRenderer.cs)

```csharp
// CF-DI: fires on the first qualifying BGRA8 non-BB DrawIndexed every frame.
if (pCtx == _contextPtr && _sceneDrawnThisFrame && !_frameInjectionDone
    && _inUiPass && _currentBbRtvPtr == 0 && _currentNoDsvRtvPtr != 0
    && _initialized && _storedScreen != null && _psLdr != null
    && _dsReverseZWrite != null && _dsNoDepth != null && _cbParams != null)
{
    nint rtvPtr = _currentNoDsvRtvPtr;
    if (!_postBloomRtvCache.TryGetValue(rtvPtr, out bool cfValid))
    {
        cfValid = IsLdrFullRes(rtvPtr);
        _postBloomRtvCache[rtvPtr] = cfValid;
    }
    if (cfValid && !_knownBackbufferRtvPtrs.Contains(rtvPtr))
    {
        calledOriginal = true;
        _drawIndexedHook?.Original(pCtx, indexCount, startIndex, baseVertex);
        _frameInjectionDone = true;
        // ... draw rect ...
        ExecuteInlineDraw(ldrRtv, useDepth: true, restoreAfterDraw: true,
                          overrideDepthState: _dsReverseZWrite, useLdrShader: true);
    }
}
```

### OMSetRT Fallback

On frames where CF-DI misses (BGRA8 surface not yet seen / timing edge cases), `OmSetRtInjectEnabled = true` fires at BB-bind time. This fires when `OMSetRenderTargets` is called to bind the backbuffer — at that moment `OMGetRenderTargets` returns the intermediate (still has scene, no HUD yet). We inject into the intermediate before calling Original(). The composite then carries rect to BB, and HUD draws on BB after.

Fallback inject ordering: intermediate has no HUD → rect in front of HUD on those frames (ordering wrong). But it prevents bright white flashing on missed CF-DI frames, which is the worse visual artifact.

---

## Key Fields

| Field | Role |
|-------|------|
| `_sceneDrawnThisFrame` | Set true when OMSetRT transitions away from `(MainDSV, R16)` — marks end of 3D scene pass |
| `_inUiPass` | Set true after `_sceneDrawnThisFrame`, cleared each frame — restricts CF-DI to the UI/composite phase |
| `_currentBbRtvPtr` | Pointer to backbuffer RTV once bound. CF-DI only fires when `== 0` (pre-BB-bind) |
| `_currentNoDsvRtvPtr` | Last non-DSV RT bound — the BGRA8 intermediate during the UI pass |
| `_postBloomRtvCache` | `Dictionary<nint, bool>` — caches `IsLdrFullRes()` results per surface pointer |
| `_knownBackbufferRtvPtrs` | `HashSet<nint>` — surfaces known to be BB; prevents CF-DI from firing on BB |
| `_frameInjectionDone` | Set `true` immediately after inject — blocks all other paths from firing |
| `OmSetRtInjectEnabled` | `true` = OMSetRT fallback active. Prevents white flashing when CF-DI misses. |
| `_dsReverseZWrite` | Depth state: reversed-Z, write enabled. Writes rect depth so subsequent geometry occludes correctly. |
| `_psLdr` | `PS_LDR` pixel shader — outputs to BGRA8 LDR directly, no sRGB→linear needed |

---

## Critical Bugs Fixed to Get Here

### 1. PrepareHooks Double-Call (v0.5.130)

`PrepareHooks()` was called from both `Plugin.cs` (unconditionally, every frame) AND from `Draw()`/`DrawBlack()`. Execution sequence per frame:
1. Plugin.cs: `PrepareHooks()` → `_targetInjectRtvPtr = _lastSeenValidRtvPtr (0x2C3...)`, `_lastSeenValidRtvPtr = 0`
2. `Draw()`: `PrepareHooks()` again → `_targetInjectRtvPtr = _lastSeenValidRtvPtr (0)` — OVERWRITES the correct value

Result: `_targetInjectRtvPtr` was always 0 → CF injection permanently broken every session.

**Fix:** Removed `PrepareHooks()` calls from `Draw()` and `DrawBlack()`. Plugin.cs calls it once.

### 2. Cross-Frame Target Matching (v0.5.131)

The old CF-DI implementation required `rtvPtr == _targetInjectRtvPtr` (learned from previous frame). FFXIV's BGRA8 intermediate rotates across swapchain buffers (~3 buffers). The learned ptr from frame N often didn't match the ptr in frame N+1 → CF-DI fired only ~57% of frames → bright white flashing on the other 43%.

**Fix:** Removed `_targetInjectRtvPtr` requirement. CF-DI now fires on the first qualifying BGRA8 non-BB surface EVERY frame, no cross-frame matching needed.

### 3. R16 Color Inject (v0.5.125 — DO NOT REPEAT)

Writing color into `R16G16B16A16_Float` triggers FFXIV's full HDR tonemap + bloom pipeline. ANY color value gets amplified to blinding white glow. The image was completely destroyed. This is a dead end.

**Rule:** NEVER write color into R16G16B16A16_Float. Depth-only into R16 is safe; color is not.

---

## Pipeline Confirmation Sources

- **xivr-Ex source**: Confirmed `RenderTargetManager` layout — scene RT at `rtManagerAddr + 0x20 + (0x8 * 107)`, depth at `+ (0x8 * 10)`.
- **ReShade / RenoDX analysis**: Confirmed tonemap shader hash `0x85E777EF`, post-tonemap pre-LUT hash `0xF8F57F0A`. UI shader hashes: 3865947726, 4076256744, 2966694105, 1823062889, 3759127293, 4142406171.
- **LdrLog diagnostic**: `0x2C3518E91E0` confirmed as `B8G8R8A8_UNorm 2560x1440` — the BGRA8 intermediate.
- **`/hud` API endpoint**: Confirmed `InventoryExpansion` overlapping rect while inventory was open; screenshot confirmed inventory rendered in front of rect.

---

## Mandatory Verification Test

After EVERY inject change:

```
curl http://localhost:17777/hud           # confirm anyAddonOverlapsRect: true (with a UI panel open)
screenshot                                # verify game UI visibly in front of rect within its bounds
curl http://localhost:17777/inject        # check ldrInjectCount, lastInjectPath distribution
```

If game UI is NOT visibly in front of rect within its bounds → the fix failed. Do not ship.

---

## Old Scene Inject (v0.5.104 and earlier) — No Longer Used

The old mechanism injected into `R16G16B16A16_Float` (pre-bloom) using an `OMSetRenderTargets` trigger at the end of the 3D geometry pass. HUD ordering was correct (R16 → bloom → tonemap → BGRA8 → composite → BB → HUD — HUD always last), but the image went through FFXIV's bloom pass and got glow/halo artifacts. A BloomCap shader clamp reduced but did not eliminate bloom. The old mechanism is fully documented above this section for historical reference.

The v0.5.132 CF-DI approach is strictly better: no bloom, correct HUD ordering, video at full quality.

---

## Rules: Do Not Regress

1. **Never write color into R16G16B16A16_Float** — pre-bloom surface, causes blinding glow. Confirmed dead end in v0.5.125.
2. **Never remove `_frameInjectionDone = true`** from CF-DI or OMSetRT-LDR — without it, multiple inject paths fire on the same frame, double-injecting.
3. **Never call `PrepareHooks()` more than once per frame** — second call resets `_targetInjectRtvPtr` to 0.
4. **Never add cross-frame surface matching** back to CF-DI — swapchain surface rotation causes ~43% miss rate.
5. **Never remove the OMSetRT-LDR inject** — it covers ~99% of frames. CF-DI alone only fires ~1% now. The BB fallback (old `OmSetRtInjectEnabled`) can stay disabled since OMSetRT-LDR handles misses correctly.
6. **Never remove the `sceneWasDrawnBeforeThisCall` guard** from OMSetRT-LDR — without it, the inject fires on the scene-end OMSetRT (before tonemap), and the tonemap blit overwrites the rect → invisible.
7. **Dead ends (do not re-investigate)**: CopyResource (vtable[47]) = 0 calls on immediate context. Dispatch (vtable[41]) = 2 calls/session. Indirect draw hooks (vtable[39,40]) = no impact. The ~37% tonemap fill mechanism is unknown and unhooked — OMSetRT-LDR is the correct workaround.
8. **Run the mandatory `/hud` + screenshot test** after every inject change before shipping.
