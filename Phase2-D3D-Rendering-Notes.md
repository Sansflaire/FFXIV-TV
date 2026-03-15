# FFXIV-TV Phase 2 — D3D11 Rendering Notes (RESOLVED)

All problems in this phase are resolved. Archived 2026-03-15.

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
