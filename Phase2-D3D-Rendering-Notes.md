# FFXIV-TV Phase 2 — D3D11 Rendering Notes (RESOLVED)

All problems in this phase are resolved. Last updated 2026-03-15 (v0.5.20).

---

## What WAS Working (entering Phase 2)

- D3D11 device obtained correctly via `Device.Instance()->D3D11Forwarder`
- `_device.ImmediateContext` is the correct context
- ImGui draw callback fires at the right time (inside `ImGui_ImplDX11_RenderDrawData`)
- D3D11 quad renders at the correct world-space position with correct perspective
- State save/restore: VB, IB, InputLayout, VS, PS, GS, HS, DS, CB, SRV, Sampler, RS, Blend, DSS
- Phase 1 fallback (ImGui overlay) still works
- Black backing quad via ImGui renders at correct position

---

## ✅ Problem 1 RESOLVED — Image renders correctly

### Root Cause Chain (final)
1. `IDalamudTextureWrap.Handle` is not a raw SRV pointer → fixed by loading texture ourselves
   via `System.Drawing.Common` → `ID3D11ShaderResourceView`
2. All TEXCOORD semantics (TEXCOORD0, TEXCOORD1) silently zeroed in this D3D context
3. `SV_VertexID` always 0 in this context
4. Fix: PS computes UV via **bilinear inverse from SV_POSITION** + screen-space corners in cbuffer b1
5. Sign bug in bilinear_uv linear case: `v = Cv/Bv` → correct is `v = -Cv/Bv`
6. `RSGetViewports` signature: `RSGetViewports(ref uint, Viewport[])` (not `RSGetViewports(int, Viewport[])`)

### Round Log Summary
- **R3–R6:** SV_VertexID always 0, UV always (0,0)
- **R4:** Dalamud texture handle is NOT a raw SRV pointer; load image ourselves
- **R7–R9:** VB UV writes produce (0,0); game GS strips TEXCOORD when GSSetShader(null)
- **R10:** Passthrough GS + VB UV — still (0,0); both fixes needed but SV_VertexID still broken
- **R11–R13:** Shader binding works; TEXCOORD silently zeroed by something after GS
- **R14:** PSSetShader works (hardcoded green confirmed)
- **R15:** TEXCOORD1 also broken; ALL interpolated semantics zeroed in this D3D context
- **R16:** Bilinear inverse from SV_POSITION — still yellow (sign bug)
- **R17:** Fixed sign bug + RSGetViewports signature → UV gradient confirmed working
- **R18:** Restored tex.Sample → image renders correctly ✅

---

## ✅ Problem 2 RESOLVED — Depth testing fully working

### Root Cause Chain (final)
1. `UiBuilder.Draw` fires AFTER FFXIV unbinds its depth buffer → `OMGetRenderTargets` at
   draw time always returns null DSV
2. Fix: hook `ID3D11DeviceContext::OMSetRenderTargets` (vtable index 33) to track DSV
   during scene rendering
3. Initial hook captured DSVs from shadow/depth-only passes (numViews=0, no RTV) →
   wrong texture dimensions → D3D11 silently ignores DSV → depth test fails everything
4. Fix: filter hook to only capture when `numViews > 0 && ppRTVs[0] != 0` (main scene pass)
5. `DrawBlackBacking` (ImGui, no depth) drew solid black over screen area → characters in
   front appeared as black silhouettes (D3D depth test failed those pixels, leaving them black)
6. Fix: removed `DrawBlackBacking` from Phase 2 path

### Round Log Summary
- **R19:** OMSetRenderTargets vtable hook installed; DSV captured but no log confirming it
- **R20:** Confirmed: first-frame timing issue; `_trackedDsv` captured 8ms AFTER first callback
- **R21:** `effectiveDsv=non-null usingDepth=True` every frame — DSV bound but wrong DSV
- **R22:** Filter to numViews>0 → correct main-scene DSV captured → depth works but black silhouette
- **R22b:** Removed `DrawBlackBacking` from Phase 2 path → fully working ✅

### Key D3D11 Facts Discovered
- FFXIV uses **reversed-Z** (near=1.0, far=0.0); `DepthFunc=Greater`, `DepthWriteMask=Zero`
- OMSetRenderTargets vtable index 33 in ID3D11DeviceContext
- Shadow passes: `OMSetRenderTargets(numViews=0, null, shadowDSV)` — must be excluded
- Main scene pass: `OMSetRenderTargets(numViews≥1, &mainRTV, sceneDSV)` — this is the DSV we want
- `IGameInteropProvider.HookFromAddress<T>(vtable[idx], detour)` — correct hook API
- `Hook<T>` from `Dalamud.Hooking` (NOT `IHook<T>`)

---

## ✅ Problem 3 RESOLVED — Game UI (chat/hotbar/map) hidden behind video rect (v0.5.18)

### Root Cause
`Draw()` runs in Dalamud's ImGui phase. The ImGui phase fires **after** the 3D→2D render
transition within each frame. The inline draw was gated on `_cachedDrawReady` (set in `Draw()`),
so `_cachedDrawReady` was always `false` when the OMSetRenderTargets detour fired at the
transition. The fallback ImGui background draw list always fired instead — it draws after native
UI (chat, hotbar, map), covering them.

### Fix
Remove `_cachedDrawReady` from the inline draw condition. Use the previous frame's `_activeSrv`
and `_storedScreen` (set in `Draw()`) — they are valid for the next frame's inline draw because
the game's render loop is: 3D→[transition]→2D→ImGui. The inline draw at the transition uses
data written by the PREVIOUS frame's ImGui phase, which is one frame stale but stable and correct.

Added explicit resource guards to prevent firing before `CreateResources()` has run:
```csharp
if (_prevCallHadDsv && isUiPass && !_injectedThisTransition
    && _initialized && _activeSrv != null && _storedScreen != null
    && _dsReverseZ != null && _dsNoDepth != null && _cbParams != null)
```

### Frame Order (confirmed)
```
Frame N:
  [3D scene renders]  ← OMSetRenderTargets with DSV (we capture _trackedDsv)
  [3D→2D transition] ← OMSetRenderTargets without DSV (inline draw fires here)
                        Uses _activeSrv/_storedScreen from frame N-1's ImGui phase
  [Native 2D UI]      ← chat, hotbar, map render on top
  [ImGui phase]       ← Draw() writes _activeSrv/_storedScreen for frame N+1
                        Fallback callback fires but _drawPending=false → skipped
```

---

## ✅ Problem 4 RESOLVED — Game crash in OMSetRenderTargetsDetour: unguarded exceptions (v0.5.19)

### Root Cause
Any managed exception escaping a Dalamud hook detour propagates through the native hook
trampoline frames in `Dalamud.Boot.dll`. Those frames do not handle CLR exception propagation —
the process terminates with exception code `0x12345679` (CLR abort signal). The entire detour
body was unguarded. The DSV tracking code (`_trackedDsv?.Dispose()`, `new ID3D11DepthStencilView`,
`AddRef()`) could throw managed exceptions under certain COM states. Also used `_omSetRTHook!.Original`
— the null-forgiving operator on the hook field, which is explicitly prohibited in CLAUDE.md
because the hook is null after Dispose, and the detour can fire concurrently with Dispose.

### Fix
1. Wrap the ENTIRE detour body in `try/catch(Exception)` with a `Plugin.Log.Warning` fallback.
2. Move `_omSetRTHook?.Original(...)` to the `finally` block so the game's D3D call ALWAYS
   completes, even if our code throws. The game's render is never interrupted.
3. Change `_omSetRTHook!.Original` to `_omSetRTHook?.Original` (null-safe).

```csharp
private void OMSetRenderTargetsDetour(...)
{
    try
    {
        // ... all our logic ...
    }
    catch (Exception ex)
    {
        Plugin.Log.Warning($"[FFXIV-TV] OMSetRenderTargetsDetour exception: {ex.GetType().Name}: {ex.Message}");
    }
    finally
    {
        _omSetRTHook?.Original(pCtx, numViews, ppRTVs, pDSV);
    }
}
```

### Why try/catch alone is not sufficient
Hardware exceptions (access violations) from unsafe code are **not** catchable by try/catch in
.NET 5+. They terminate the process immediately. The only protection against AVs is preventing
the conditions that cause them — see Problem 5.

---

## ✅ Problem 5 RESOLVED — Game crash: RTV COM refcount stolen each frame (v0.5.20)

### Root Cause
In `OMSetRenderTargetsDetour`, the inline draw was created as:
```csharp
try { ExecuteInlineDraw(new ID3D11RenderTargetView(ppRTVs[0])); }
```

**Vortice COM wrappers do NOT call AddRef on construction.** `new ID3D11RenderTargetView(ptr)`
borrows the pointer — it wraps it without incrementing the COM refcount. `ExecuteInlineDraw`
then called `rtv.Dispose()` in its finally block, which calls COM `Release()`.

Every frame: borrow ref (no AddRef) → Release() → game loses 1 refcount.

The game's RTV had an initial refcount of approximately 3:
- Game's own creation reference
- D3D internal binding reference (set when the game binds the RTV to the pipeline)
- Possibly one more from earlier in the frame

After frame 1: refcount = 2. After frame 2: refcount = 1. After frame 3: refcount = 0.
**D3D frees the COM object.** On frame 4, the game calls `OMSetRenderTargets` with the now-
dangling pointer → hardware AV → uncatchable → game crash.

This explained the diagnostic pattern perfectly: 3 successful inline draws logged at the same
millisecond, then crash ~2-3 seconds later.

### Fix
```csharp
// Take our own reference — MUST match the Dispose() in the finally block.
var inlineRtv = new ID3D11RenderTargetView(ppRTVs[0]);
inlineRtv.AddRef();
try { ExecuteInlineDraw(inlineRtv); }
catch (Exception ex) { Plugin.Log.Warning(...); }
finally { inlineRtv.Dispose(); } // Release our AddRef — always balanced, game's ref untouched
```

Removed `rtv.Dispose()` from `ExecuteInlineDraw`'s finally block. The caller (the detour) now
owns and manages the RTV lifetime. `ExecuteInlineDraw` is a "borrowed reference" consumer.

### General Rule — Vortice COM Wrappers
| Operation | AddRef called? | Notes |
|-----------|---------------|-------|
| `new ID3D11Xxx(nint ptr)` | **NO** | Borrows the pointer. Dispose() will Release. |
| `device.CreateXxx(...)` | Yes (via D3D API) | You own the reference. |
| `context.OMGetRenderTargets(...)` | Yes (per COM output semantics) | You own the reference. |
| `context.VSGetShader()` etc. | Yes (per COM output semantics) | You own the reference. |

Rule: if you wrap a raw pointer you got from native code (e.g., hook parameters), you do NOT own
a reference unless you call `AddRef()`. If you intend to `Dispose()` the wrapper, you MUST
`AddRef()` first.
