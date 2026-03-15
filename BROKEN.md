# FFXIV-TV Phase 2 — Known Broken Issues

Last updated: 2026-03-15

---

## What IS Working

- D3D11 device obtained correctly via `Device.Instance()->D3D11Forwarder`
- `_device.ImmediateContext` is the correct context
- ImGui draw callback fires at the right time (inside `ImGui_ImplDX11_RenderDrawData`)
- D3D11 quad renders at the correct world-space position with correct perspective
- State save/restore: VB, IB, InputLayout, VS, PS, GS, HS, DS, CB, SRV, Sampler, RS, Blend, DSS
- Phase 1 fallback (ImGui overlay) still works
- Black backing quad via ImGui renders at correct position

---

## Problem 1: Image shows as solid sky-blue instead of actual texture content

### Symptom
The quad renders at the correct position and correct size, but displays a solid
uniform sky-blue color regardless of which image is loaded.
The sky-blue color matches the top-left pixel of the reference test image (a character
photo with blue sky background), which means UV = (0,0) everywhere.

### Root Cause Chain (what we know)
1. **M44 fix was wrong** — `viewProj.M44 = 1f` was crushing a value that should be ~1142
   (the camera position offset). This made clip W = -1133 instead of +5, putting all
   geometry behind the camera. **FIXED** — removed the override.

2. **GS/HS/DS not nulled** — FFXIV's geometry shader was still bound from the 3D scene
   pass. It intercepted VS output and produced output with no TEXCOORD semantic.
   PS received (0,0). With constant UV diagnostic (`float2(0.5,0.5)`) this showed as
   solid black. **FIXED** — added `GSSetShader(null)`, `HSSetShader(null)`, `DSSetShader(null)`
   in SetState.

3. **UV still all (0,0)** — After fixing GS, the texture samples at (0,0) = top-left pixel.
   The positions ARE correct (geometry renders in the right place), but UV data from the
   vertex buffer is apparently not reaching the VS input `v.uv`, OR it's all zero.

### Current Hypothesis
The vertex shader is reading `v.uv` from the TEXCOORD element of the vertex buffer,
but getting (0,0) for all 4 vertices. Either:

**A) Vertex buffer write is broken:**
`mapped.AsSpan<ScreenVertex>(4)` writes to the mapped memory but UV data ends up zero.
Positions work because (0,0,0) is wrong too but hard to detect visually.
Wait — positions ARE correct (quad in right world place), so the write IS working for
position data. UV should also work since it's in the same struct.

**B) ScreenVertex struct layout mismatch:**
`sizeof(ScreenVertex)` = 20 (Vector3=12 + Vector2=8, no padding).
Input layout: POSITION at offset 0, TEXCOORD at offset 12.
If this is wrong (e.g. Vector3 unexpectedly padded to 16 bytes on this runtime),
TEXCOORD would read from the wrong bytes = garbage, not consistent (0,0).

**C) A second GS/tesselation-related issue:**
Some other shader stage is still active that rewrites TEXCOORD to (0,0) after VS.
Unlikely since we null GS/HS/DS explicitly.

**D) The SRV is valid but for the wrong texture:**
`wrap.Handle.Handle` (ulong from IDalamudTextureWrap.Handle.Handle) might point to a
different texture than the user's image — e.g., a cached Dalamud default texture that
happens to be a small sky-blue solid.
This would explain solid uniform color that NEVER shows the actual image content.
This is actually consistent with Phase 1 working (Phase 1 uses `AddImageQuad` with
`wrap.Handle` directly, which Dalamud routes correctly; Phase 2 extracts the raw SRV
pointer and uses it directly in D3D11, which may bypass Dalamud's texture routing).

### Round 3 — SV_VertexID still shows sky-blue

UV gradient returned solid black → UV=(0,0) for all vertices regardless of VB content.

**Fix 1 applied:** Removed TEXCOORD from VB/input layout. VS now synthesises UV from
`SV_VertexID` (0=TL(0,0), 1=TR(1,0), 2=BR(1,1), 3=BL(0,1) via static table).
VB is now positions-only (Vector3, 12 bytes/vertex).

**Result:** Still solid sky-blue (image top-left pixel colour). Two remaining hypotheses:

**A) SV_VertexID is always 0** → UV always (0,0) → samples top-left → sky-blue
**B) SRV (`wrap.Handle.Handle`) is a wrong/placeholder texture** (e.g. a 1×1 blue solid).
   UV would then be correct but every UV samples the same blue pixel.

### Round 4 — Arithmetic UV, still solid sky-blue

Root cause of UV issue found: HLSL `{0,0}` brace-init zeroed the array. Fixed with arithmetic.
BUT — sky-blue persisted with correct UVs. Therefore hypothesis D was always correct.

**Confirmed root cause:** `IDalamudTextureWrap.Handle.Handle` is NOT a raw
`ID3D11ShaderResourceView*`. Dalamud uses an internal handle table for its texture system;
Phase 1's `AddImageQuad(wrap.Handle)` goes through Dalamud's routing which resolves the
handle; Phase 2's raw D3D11 SRV extraction bypasses it and binds a wrong/placeholder texture.

**Fix 3 applied:** D3DRenderer now loads the image file itself using `System.Drawing.Common`
(added as NuGet 9.0.3) into its own `ID3D11ShaderResourceView`. Dalamud's texture wrap is
no longer used in Phase 2 at all.
- `SetImagePath(string path)` — reloads texture if path changed, called from Plugin each frame
- `LoadTexture(string path)` — Bitmap → Format32bppArgb → `Format.B8G8R8A8_UNorm` D3D11 texture
- `Draw(ScreenDefinition)` — no longer takes a texture handle parameter
- `Plugin.cs` passes `Config.ImagePath` directly instead of `wrap.Handle.Handle`

**Result:** Still solid sky-blue. Own texture confirmed correct (image file loaded by us, not
Dalamud). UV=(0,0) at all fragments, same as before.

**Conclusion:** The game's GS is still active after `GSSetShader(null)`. Vortice's null-shader
call apparently does not clear it. The GS re-emits only SV_POSITION, silently dropping TEXCOORD.
Positions survive (GS passes them), UV is zeroed (GS doesn't output TEXCOORD).

**Fix 4 applied:** Compile our own passthrough GS (gs_5_0) that explicitly re-emits both
SV_POSITION and TEXCOORD. Bind it with `GSSetShader(_gs!)` instead of null.
This replaces the game's GS for our draw call, guaranteeing TEXCOORD survives to PS.

**Result (Round 5 — confirmed in-game):** Still solid sky-blue after Fix 4.
Passthrough GS did not fix it. UV is STILL (0,0) at all fragments.

**New analysis:** The passthrough GS faithfully re-emits what it receives from the VS.
If the VS outputs UV=(0,0) for all vertices, the GS would pass through (0,0) correctly — the
GS was never the root cause. The real issue is in the VS's SV_VertexID computation.

**Root cause (Round 5 hypothesis):**
With `DrawIndexed(6, 0, 0)` and index buffer [0,1,2,0,2,3], SV_VertexID for the 4th unique
vertex (index value 3, first seen at draw position 5) may receive vid=5 rather than vid=3
depending on D3D11/driver behaviour. Our ternary `(vid >= 2u)` maps vid=5 to y=1 correctly,
but the exact vid values for cached vertices are driver-defined and not guaranteed.
More critically, with any ambiguity in vid, ALL vertices could collapse to vid=0 in some
environments, yielding UV=(0,0) for all.

**Fix 5 applied:** Switch from `DrawIndexed(6, 0, 0)` with TriangleList to
`Draw(4, 0)` with `TriangleStrip`. No index buffer needed.
- VB order changed: TL=v0, TR=v1, BL=v2, BR=v3 (strip topology)

**Result (Round 6 — confirmed in-game):** STILL solid sky-blue. TriangleStrip + Draw(4,0)
did not fix it. SV_VertexID approach is not the root cause (or SV_VertexID is itself broken
in this context).

**Round 6 — Diagnostic build**

PS changed to UV-as-color diagnostic: `float4(1-uv.x, 1-uv.y, 0, 1)`.

**Result (Round 6 — confirmed in-game):** Solid YELLOW → UV = (0,0) at every pixel.

**Root cause confirmed:** `SV_VertexID` is always 0 in this D3D context. Every attempt to compute
UV from `SV_VertexID` (Rounds 3–6) failed for the same underlying reason — the system value is
simply not populated. All previous hypotheses about the game's GS dropping TEXCOORD were incorrect;
the GS was never involved in the UV=(0,0) symptom.

**Fix 7 applied:** UV moved into the vertex buffer. `ScreenVertex` now holds `Vector3 Position`
+ `Vector2 UV` (20 bytes). VS reads TEXCOORD directly from VB input — no `SV_VertexID`.
- Input layout gains `TEXCOORD` at byte offset 12
- `UpdateVB` writes explicit UV per vertex: TL=(0,0) TR=(1,0) BL=(0,1) BR=(1,1)
- GS remains absent
- PS restored to `tex.Sample(samp, uv)`

**Result (Round 7 — confirmed in-game):** Still solid sky-blue. Texture confirmed loaded at
631x713 (dalamud.log). VS=916B PS=616B confirms VB-UV shaders were active.

**Round 8 — UV diagnostic with raw float VB writes**

`AsSpan<ScreenVertex>(4)` struct-based writes may not be placing UV data at the correct byte
offsets. Switching to `AsSpan<float>(20)` — 5 raw floats per vertex, explicitly writing each
byte position:

```
f[0..2] = pos.XYZ,  f[3]=uvX, f[4]=uvY    ← v0 TL (0,0)
f[5..7] = pos.XYZ,  f[8]=uvX, f[9]=uvY    ← v1 TR (1,0)
f[10..12]= pos.XYZ, f[13]=uvX, f[14]=uvY  ← v2 BL (0,1)
f[15..17]= pos.XYZ, f[18]=uvX, f[19]=uvY  ← v3 BR (1,1)
```

UV diagnostic PS restored. Expected:
- **Yellow** = UV still (0,0) even with raw float writes → TEXCOORD read broken at shader level
- **Gradient** = UV working → previous sky-blue was from image content

**Result (Round 8 — confirmed in-game):** Still full yellow. Raw float writes at exact byte
offsets still produce UV=(0,0). VB write is not the problem.

**Round 9 — hardcoded UV in VS**

VS hardcoded to `o.uv = float2(0.5f, 0.5f)` — no VB, no SV_VertexID.
UV diagnostic PS. GSSetShader(null).

**Result (Round 9 — confirmed in-game):** Still full yellow. Even with hardcoded UV in VS,
the PS receives (0,0). TEXCOORD is stripped between VS output and PS input.

**Root cause confirmed:** The game's GS is active at draw call time and cannot be cleared by
calling `GSSetShader(null)`. It strips all user semantics (TEXCOORD), outputting only
SV_POSITION. The VS TEXCOORD output never reaches the PS.

**Why passthrough GS appeared broken in Round 5:** The passthrough GS WAS working correctly —
it faithfully forwarded UV=(0,0) to the PS (because SV_VertexID was broken → UV was 0,0 in
the VS). We incorrectly attributed the sky-blue to the GS not working. When we later removed
the GS to try VB UV, the game's GS re-intercepted and confirmed it was always the issue.

**Fix 10 applied:** Passthrough GS restored AND VB UV with raw float writes combined.
These two fixes were each individually present in earlier rounds but never together:
- Round 4+5: passthrough GS + SV_VertexID UV (SV_VertexID broken → UV=0,0 forwarded)
- Rounds 7-9: VB UV + no passthrough GS (game's GS stripped TEXCOORD)
- Round 10: passthrough GS + VB UV (raw floats) — first time both are present simultaneously.

`ScreenVertex` has `Vector3 Position + Vector2 UV` (20 bytes). VB written as raw floats:
TL=(0,0) TR=(1,0) BL=(0,1) BR=(1,1). Input layout has POSITION+TEXCOORD. PS = tex.Sample.

**Result (Round 10 — confirmed in-game):** Still solid sky-blue. Passthrough GS + VB raw float UV
did not fix it. UV is still (0,0) at the PS.

**Round 11 — VS hardcoded UV + passthrough GS + UV diagnostic**

The one combination never tested: passthrough GS + a UV source that is guaranteed correct.
Both previous passthrough GS tests (Rounds 4-5, Round 10) had a broken UV source
(SV_VertexID=0 and possibly bad VB respectively). We need to rule out whether the GS is
actually forwarding TEXCOORD to the PS at all.

**Fix 11 applied:**
- VS: hardcoded `o.uv = float2(0.5f, 0.5f)` — no VB, no SV_VertexID dependency
- GS: passthrough (existing `_gs`) — unchanged
- PS: UV diagnostic `float4(1-uv.x, 1-uv.y, 0, 1)`

Expected:
- **Olive** (~0.5, 0.5, 0): passthrough GS forwards TEXCOORD correctly → issue was VB data
- **Yellow** (UV=0,0): game's GS still wins even with `GSSetShader(_gs!)` → need different approach

**Result (Round 11 — confirmed in-game):** Still solid yellow.

**Round 12 — GS hardcoded UV**

New question: is our GS running AT ALL? Round 11 had VS hardcode (0.5,0.5) and GS forward from
VS input. If GS input doesn't receive TEXCOORD from VS (semantic linkage broken), forwarding
would output (0,0). This round: GS outputs hardcoded (0.5,0.5) — no dependency on VS input.

**Fix 12 applied:**
- VS: hardcoded `o.uv = float2(0.5f, 0.5f)` — unchanged from R11
- GS: outputs hardcoded `float2(0.5f, 0.5f)` — does NOT forward input[i].uv
- PS: UV diagnostic `float4(1-uv.x, 1-uv.y, 0, 1)` — unchanged from R11

Expected:
- **Olive** (~0.5, 0.5, 0): our GS runs, but VS→GS TEXCOORD linkage is broken (fixable)
- **Yellow**: our GS is NOT running at all — `GSSetShader(_gs!)` has no effect

**Result (Round 12 — confirmed in-game):** Still solid yellow. **Our GS is not running.**

**Root cause still unknown:** Could be Vortice wrapping bug, could be that VSSetShader also
doesn't work and game's VS runs instead (outputting no TEXCOORD → UV defaults to 0,0).

**Round 13 — Shader pointer logging diagnostic**

**Result (confirmed via dalamud.log):**
```
_vs=0x2B343C5E678  _gs=0x2B343C5E7F8
vsBefore=0x2B3ECAA4AF8  vsAfter=0x2B343C5E678  match=True
gsBefore=0x (null — ImGui had nulled it)  gsAfter=0x2B343C5E7F8  match=True
```
Both VSSetShader and GSSetShader work. Our shaders ARE being bound. Vortice wrapping is fine.
ImGui had already nulled the GS (gsBefore=null), confirming ImGui calls GSSetShader(null) before
our callback fires. Despite correct shader binding, TEXCOORD is still (0,0) at PS.

**Root cause update:** Shaders bind correctly. Problem is specifically TEXCOORD not propagating
through the VS→GS→PS chain even with:
- VS hardcoded (0.5,0.5)
- GS hardcoded (0.5,0.5) — Round 12
- PS reads TEXCOORD → gets (0,0) → yellow

**Round 14 — PS no-input hardcoded green**

Critical question: is our PSSetShader working? We assumed yes because "yellow = our UV diagnostic
formula with UV=0,0". But if something else outputs yellow, our PS might not run at all.

**Fix 14 applied:** PS takes NO INPUTS. Hardcoded green `float4(0,1,0,1)`.
This cannot output yellow regardless of UV or any other state.

**Result (Round 14 — confirmed in-game):** Solid GREEN. Our PS IS running. PSSetShader works.
TEXCOORD0 is specifically broken — the semantic doesn't propagate through VS→PS even with
null GS and VS hardcoded output. All prior UV=(0,0) was TEXCOORD0 silently zeroed.

**Root cause hypothesis:** TEXCOORD0 (semantic index 0) is intercepted or clamped somewhere in
the pipeline — possibly by the game's rendering system or a D3D11 hook that zeroes TEXCOORD0
interpolants. Other semantics may not be affected.

**Round 15 — TEXCOORD1 instead of TEXCOORD0, no GS**

Switch every use of `TEXCOORD` to `TEXCOORD1` (semantic index 1). Drop the GS entirely (null).

**Fix 15 applied:**
- VS: outputs `float2 uv : TEXCOORD1` hardcoded (0.5,0.5)
- GS: removed — `GSSetShader(null)` in SetState
- PS: reads `float2 uv : TEXCOORD1`, UV-as-color diagnostic

Expected:
- **Olive** (~0.5,0.5,0): TEXCOORD1 works → TEXCOORD0 was the specifically broken slot
- **Yellow**: TEXCOORD1 also broken → all interpolated semantics are zeroed
- **Green**: PS but UV still not connected (default 0,0 with correct formula = yellow, not green)

**⚠️ CRASH WARNING — R15 CAUSED GAME CRASH (TWICE)**

**Root cause:** The R13 diagnostic block used `_gs!` (null-forgiving) after `_gs` was set to null
(GS compilation removed in R15). The `!` suppresses the compiler warning but crashes at runtime
with NullReferenceException inside the render callback — which kills the game.

**Rule:** NEVER use `!` (null-forgiving operator) on fields that may be null. If removing
something that's referenced elsewhere, find and remove ALL references before building.
The render callback has no try/catch for NullReferenceException — any unhandled exception
inside `ExecuteDrawCallback` crashes the game immediately.

**Fix applied:** Removed the entire R13 diagnostic block and `_shaderDiagLogged` field.

**Result (Round 15 — confirmed in-game):** Full yellow. TEXCOORD1 is also broken.
**ALL interpolated user-defined semantics are silently zeroed** in this D3D context —
TEXCOORD0, TEXCOORD1, all confirmed broken across rounds 3–15. GS makes no difference.
SV_POSITION (rasterizer system value) is the only reliable per-pixel data.

---

### Round 16 — Bilinear inverse UV from SV_POSITION (current approach)

**Root cause:** All interpolated semantics (TEXCOORD*) are silently zeroed in this context.
SV_POSITION is a rasterizer system value — it's populated by the rasterizer, not interpolated,
so it survives whatever is stripping our TEXCOORD data.

**Fix 16 applied:** Bypass TEXCOORD entirely. Compute UV in PS from SV_POSITION:
1. Draw() stores world corners (`_wTL`, `_wTR`, `_wBL`, `_wBR`) and `_storedViewProj`
2. ExecuteDrawCallback calls `UpdateCbCorners()` — gets current viewport via RSGetViewports,
   projects world corners to screen pixels via ViewProj, uploads to PS cbuffer b1 (`_cbCorners`)
3. VS: no TEXCOORD output — just `float4 main(VSIn v) : SV_POSITION`
4. PS: `float4 main(float4 pos : SV_POSITION)` — calls `bilinear_uv(pos.xy, TL.xy, TR.xy, BL.xy, BR.xy)`
   to solve inverse bilinear mapping from screen pixel → UV(u,v) in [0,1]
5. PS: `tex.Sample(samp, uv)` — actual texture output

Non-perspective-correct (screen-space bilinear). Acceptable for near-head-on viewing.
`_gs` field and GS compilation completely removed. No passthrough GS needed.

**Result (Round 16 — confirmed in-game):** Still solid yellow. Bilinear inverse approach is
not producing an image. Yellow is inconsistent with tex.Sample on this image (top-left = sky-blue).
Either bilinear_uv is computing wrong UV or the PS cbuffer data is wrong/missing.

---

### Round 17 — Diagnostic: UV gradient + bilinear sign fix + viewport logging

**Root cause hypothesis:** Two possible issues:
1. Sign bug in bilinear_uv linear case: `v = Cv/Bv` should be `v = -Cv/Bv`
   (derived from Bv*v + Cv = 0 → v = -Cv/Bv)
2. Viewport scale mismatch: RSGetViewports may return non-pixel values, making the
   corner screen positions in a different space than SV_POSITION (which is always pixels)

**Fix 17 applied:**
- Fixed bilinear_uv linear case: `v = -Cv / (Bv + 1e-10)` (was `Cv/Bv`)
- Added one-shot logging in UpdateCbCorners: viewport dimensions + all 4 corner screen positions
- PS changed to UV gradient diagnostic: `float4(uv.x, uv.y, 0, 1)` instead of tex.Sample
  Expected: smooth gradient (black=TL, red=TR, green=BL, yellow=BR) if UV is computing correctly.
  Solid yellow = UV all (1,1). Solid black = UV all (0,0). Solid color = UV stuck.
- RSGetViewports signature fixed: `RSGetViewports(ref uint, Viewport[])` (was broken)

**Result (Round 17 — confirmed in-game):** Perfect UV gradient — black TL, red TR, green BL,
yellow BR. Bilinear inverse UV is computing correctly after the sign fix.
Bonus: character's feet visible THROUGH the bottom of the quad — depth testing is working!

---

### Round 18 — Switch PS back to tex.Sample (image rendering)

**Fix 18:** Remove R17 diagnostic (`float4(uv.x, uv.y, 0, 1)`) → restore `tex.Sample(samp, uv)`.
All diagnostic logging and `_diagLogged` field removed. PS comment cleaned up.

**Result (Round 18 — confirmed in-game):** ✅ IMAGE RENDERS CORRECTLY on the world-space quad.
Depth testing confirmed working — character's feet occlude the bottom of the screen.

---

## ✅ Problem 1 RESOLVED — Image now renders correctly

**Root cause chain (final):**
1. `IDalamudTextureWrap.Handle` is not a raw SRV pointer → fixed by loading texture ourselves
2. All TEXCOORD semantics (TEXCOORD0, TEXCOORD1) silently zeroed in this D3D context
3. SV_VertexID always 0 in this context
4. Fix: PS computes UV via bilinear inverse from SV_POSITION + screen-space corners in cbuffer b1
5. Sign bug in bilinear_uv linear case: `v = Cv/Bv` → should be `v = -Cv/Bv`
6. RSGetViewports signature: `RSGetViewports(ref uint, Viewport[])` (not `RSGetViewports(int, Viewport[])`)

---

## Problem 2: Screen draws over all world geometry (no depth testing) — STILL OPEN

### Symptom
The D3D quad renders on top of walls, bulletin boards, and all game geometry.
Characters should occlude the screen when in front of it; world geometry should also occlude it.

### Root Cause
`_savedDsv` (captured in `Draw()` via `OMGetRenderTargets`) is almost certainly null.
By the time `UiBuilder.Draw` fires, the game has already transitioned past its 3D scene pass
and unbound the depth buffer for the UI pass. So the saved DSV is null → we fall back to
`_dsNoDepth` → no depth testing.

### What Was Tried
- Round 1: Save DSV in `Draw()` at UiBuilder.Draw time via `OMGetRenderTargets`.
  **Doesn't work** — confirmed via R19 diagnostic: `savedDsv=null currentDsv=null`.
  FFXIV unbinds the depth buffer before UiBuilder.Draw fires.

### Fix Applied (Round 19)
Hook `ID3D11DeviceContext::OMSetRenderTargets` (vtable index 33) via
`IGameInteropProvider.HookFromAddress`. The detour fires on every OMSetRenderTargets call.
If the call is on our context and has a non-null DSV, save it as `_trackedDsv` (AddRef).
ExecuteDrawCallback uses `_trackedDsv` as the effective DSV for depth testing.

This captures FFXIV's scene depth buffer while it's being bound during scene rendering,
before the UI pass unbinds it.

**Result (Round 19 — confirmed in-game):** Still draws over all geometry. Hook installed
successfully ("OMSetRenderTargets hook installed." in log), but no evidence _trackedDsv
was ever captured — no diagnostic log from inside the detour.

### Round 20 — Diagnostic: confirm hook fires and DSV captured

**Result (Round 20 — confirmed via log):**
```
05:02:42.969 — callback frame=1: trackedDsv=null effectiveDsv=null  (first frame, DSV not yet captured)
05:02:42.977 — _trackedDsv captured: 0x299AEA53BE0  (8ms AFTER first callback)
```
The callback fires on frame 1 before the hook has ever seen a DSV. After frame 1,
`_trackedDsv` IS set. But because the one-shot log already fired, we can't confirm
whether frames 2+ see `effectiveDsv=non-null`. The screen still draws over everything.

**Root cause (partial):** Timing — first frame has no DSV. Subsequent frames have `_trackedDsv`
non-null, but depth testing still doesn't work. Unknown whether issue is:
- `_trackedDsv` null on frames 2+ (unexpected reset)
- DSV/RTV format or dimension mismatch (D3D11 silently ignores DSV)
- Correct DSV bound but depth test logic wrong

### Round 21 — Diagnostic: per-frame logging of effectiveDsv state

**Result (Round 21 — confirmed via log):**
```
frame=67680 trackedDsv=non-null effectiveDsv=non-null usingDepth=True
frame=67860 trackedDsv=non-null effectiveDsv=non-null usingDepth=True
... (same every 180 frames, thousands of frames)
```
`effectiveDsv=non-null` and `usingDepth=True` every frame in steady state. The DSV IS
being passed to `OMSetRenderTargets` and `_dsReverseZ` IS being used. Yet screen still
draws over all world geometry.

**Root cause hypothesis:** The captured DSV is from a **shadow/depth-only pass**, not the
main scene. Shadow passes call `OMSetRenderTargets(numViews=0, null, dsv)` — no RTV, just
depth. Our hook saves the last non-null DSV regardless of whether an RTV is bound.
When we bind a shadow-map DSV (e.g., 2048×2048) alongside ImGui's backbuffer RTV (2560×1440),
D3D11 either silently ignores the DSV (dimension mismatch) or uses shadow depth values
(completely wrong Z range), so depth test never rejects our fragments.

### Round 22 — Filter: only capture DSVs bound with a real RTV (main scene pass)

**Result (Round 22 — confirmed in-game):** ✅ DEPTH TESTING NOW WORKS (partially).
- Character BEHIND screen is correctly hidden by the screen ✓
- Character IN FRONT of screen correctly passes through ✓
- BUT: character in front appears as a **solid black silhouette** ✗

**Root cause of black silhouette:** `Plugin.cs` called `_screenRenderer.DrawBlackBacking(Config)`
before `_d3dRenderer.Draw()`. That ImGui call draws a solid black quad over the entire screen
area with NO depth testing. When D3D runs, depth fails for character pixels → they stay black
from the backing, never showing the character's actual color.

**Fix R22b:** Removed `_screenRenderer.DrawBlackBacking(Config)` from the Phase 2 D3D path
in `Plugin.cs`. The D3D draw handles the screen itself; no ImGui backing needed.

**Result (Round 22b — confirmed in-game):** ✅ FULLY WORKING.
Character in front occludes screen with correct colors. World geometry occludes screen
correctly. Screen correctly occludes characters and geometry behind it.

---

## ✅ Problem 2 RESOLVED — Depth testing fully working

**Root cause chain (final):**
1. `UiBuilder.Draw` fires AFTER FFXIV unbinds its depth buffer → `OMGetRenderTargets` at
   draw time always returns null DSV
2. Fix: hook `ID3D11DeviceContext::OMSetRenderTargets` (vtable index 33) to track DSV
   during scene rendering
3. Initial hook captured DSVs from shadow/depth-only passes (numViews=0, no RTV) →
   wrong texture dimensions → depth test silently failed
4. Fix: filter hook to only capture when `numViews > 0 && ppRTVs[0] != 0` (main scene pass)
5. `DrawBlackBacking` (ImGui, no depth) drew solid black over screen area → characters in
   front appeared as black silhouettes (D3D depth test failed those pixels, left them black)
6. Fix: removed `DrawBlackBacking` from Phase 2 path

---

## Problem 2: Characters render in front of the screen (no depth testing)

### Symptom
The D3D quad always draws on top of characters and world geometry. Characters
should occlude the screen when standing in front of it.

### Root Cause
During the ImGui draw callback, the ImGui DX11 backend binds only an RTV (backbuffer)
with NO depth-stencil view (DSV). Our depth-stencil state has `DepthEnable = false`
because there's nothing to test against.

### Fix Applied (Round 1)
In `Draw()` (called during `UiBuilder.Draw`, before ImGui swaps render targets), save the
current DSV via `OMGetRenderTargets`. In `ExecuteDrawCallback`, get the current RTV that
ImGui set up, then `OMSetRenderTargets(rtv, savedDsv)` before our draw call.
- Depth state: `DepthEnable=true`, `DepthWriteMask.Zero` (read-only), `DepthFunc=Greater`
  (reversed-Z: draw if our fragment Z > stored depth = we're closer to camera).
- Falls back to `_dsNoDepth` if `savedDsv` is null (game may have unbound depth before UI pass).
- Restores `OMSetRenderTargets(rtv, null)` after our draw (back to ImGui's original state).

Status: **in-flight — if savedDsv is always null, depth testing still won't work.
Check dalamud.log for "Loaded texture" to confirm texture, then observe whether
characters occlude the screen to confirm depth works.**

---

## Notes

- Phase 1 (ImGui overlay, `AddImageQuad`) shows the image correctly. The texture IS
  loading. This is the reference baseline.
- `wrap.Handle.Handle` is non-zero (~0x2B480D104A8 range), a plausible COM pointer.
- AddRef on that pointer does not crash, so it IS a valid COM object.
- The SRV being bound to register t0 produces a solid sky-blue color at all UVs —
  which could mean: (a) a 1×1 blue texture, or (b) all UVs are 0 and the actual
  image's top-left is blue.
