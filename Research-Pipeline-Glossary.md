# FFXIV-TV — Complete Pipeline & Rendering Glossary
*Created 2026-03-31. Maintained by Aria (Claude). This document is a living research reference.*

---

## ⚠️ CORE DIRECTIVES FOR THIS DOCUMENT

**This document MUST be the single authoritative reference for every technical concept used in
FFXIV-TV's rendering, injection, and generative pipeline.** Its purpose is to eliminate guessing,
reduce research redundancy, and give any future Claude instance immediate deep context.

### Mandatory Rules

1. **Never guess at a concept that should be here.** If you're about to write "I think X works like..."
   — stop and look it up. Research goes here, not in commit messages or inline comments.

2. **Every new concept gets a section.** If you introduce a new D3D11 feature, shader technique, FFXIV
   quirk, or pipeline stage that isn't already documented, add it to this file before shipping the code.

3. **Confirmed facts supersede theory.** When FFXIV-TV diagnostics (StatusApi / /inject / /render)
   confirm or contradict something written here, update this file immediately. Mark confirmed-by-live-test
   facts with `✓ confirmed`.

4. **Link to source.** For every non-trivial claim, note where the knowledge comes from:
   live test, open source code (with URL), docs (with URL), or decompiler analysis.

5. **Dead ends go in BROKEN.md, not here.** This document covers what is true and useful.
   Failed approaches belong in BROKEN.md with a cross-reference.

6. **Keep sections flat and scannable.** This file will grow large. Use H2 for major categories,
   H3 for individual concepts, and a one-line summary before the detail.

---

## TABLE OF CONTENTS

1. [D3D11 Pipeline Primitives — Draw & Dispatch Methods](#d3d11-pipeline-primitives)
2. [D3D11 Resource View Types](#d3d11-resource-view-types)
3. [D3D11 State Objects](#d3d11-state-objects)
4. [DXGI Formats & the SwapChain](#dxgi-formats--the-swapchain)
5. [HLSL Shader Concepts](#hlsl-shader-concepts)
6. [Rendering Technique Concepts](#rendering-technique-concepts)
7. [FFXIV-Specific Pipeline Stages](#ffxiv-specific-pipeline-stages)
8. [FFXIV-TV Inject Detection Logic](#ffxiv-tv-inject-detection-logic)
9. [Post-Processing Parameters](#post-processing-parameters)
10. [Dalamud & Plugin Hooking Infrastructure](#dalamud--plugin-hooking-infrastructure)
11. [Supporting Libraries & Reference Plugins](#supporting-libraries--reference-plugins)
12. [Video & Browser Rendering Patterns](#video--browser-rendering-patterns)
13. [ID3D11DeviceContext — Complete Vtable Quick Reference](#id3d11devicecontext--complete-vtable-quick-reference)
14. [Re-entrancy and Thread Safety in D3D11 Hooks](#re-entrancy-and-thread-safety-in-d3d11-hooks)
15. [Vortice.Direct3D11 — AddRef Gotcha](#vortice-direct3d11--addref-gotcha)

---

## D3D11 Pipeline Primitives

### DrawIndexed
*vtable[12] on ID3D11DeviceContext. Indexed draw call — geometry described by an index buffer.*

**Signature:** `void DrawIndexed(uint IndexCount, uint StartIndexLocation, int BaseVertexLocation)`
The most common draw path in FFXIV's scene pass. Used for mesh geometry, HUD elements, and — ~40%
of frames — the tonemap blit that fills the LDR intermediate surface. ✓ confirmed by FFXIV-TV cfDiCount.

Index buffer provides an array of integers pointing into a vertex buffer. Allows vertex reuse
(e.g., a quad = 6 indices into 4 verts). Most efficient path for static geometry.

**FFXIV-TV use:** CF-DI (DrawIndexed inject) — primary inject path. Hook fires before Original.
Inject our rect draw first, then call Original to let tonemap blit fill the LDR surface around us.
Result: our rect is in LDR surface before HUD draws. ✓ confirmed working v0.5.132+.

Source: [Microsoft Docs](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-drawindexed)

---

### Draw
*vtable[13] on ID3D11DeviceContext. Non-indexed draw — vertex buffer only, no index reuse.*

**Signature:** `void Draw(uint VertexCount, uint StartVertexLocation)`
Used when geometry doesn't benefit from index reuse (e.g., procedurally-generated quads, particle
strips). In FFXIV, roughly 20% of frames use this for the tonemap blit.

Our injected rect itself uses `Draw(6, 0)` — 6 vertices, no index buffer, TriangleList topology.
The `cbkFrameCount` diagnostic increments only when this `Draw(6,0)` actually executes — confirming
the draw fired (vs `ldrInjectCount` which increments before the call). ✓ confirmed.

**FFXIV-TV use:** CF-Draw (Draw inject) — secondary fallback inject path. ~1% of frames.

Source: [Microsoft Docs](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-draw)

---

### DrawIndexedInstanced
*vtable[20] on ID3D11DeviceContext. Instanced indexed draw — renders multiple instances in one call.*

**Signature:** `void DrawIndexedInstanced(uint IndexCountPerInstance, uint InstanceCount, uint StartIndexLocation, int BaseVertexLocation, uint StartInstanceLocation)`
Used for rendering many identical meshes (foliage, particles, crowd NPCs) efficiently.
FFXIV's tonemap blit occasionally routes through this path (~3% of frames per diagnostics).

**FFXIV-TV note:** Not currently hooked as a primary inject path but vtable[20] is in the index table.
Hook if CF-DI/CF-Draw misses increase.

Source: [Microsoft Docs](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-drawindexedinstanced)

---

### DrawInstanced
*vtable[21] on ID3D11DeviceContext. Non-indexed instanced draw.*

**Signature:** `void DrawInstanced(uint VertexCountPerInstance, uint InstanceCount, uint StartVertexLocation, uint StartInstanceLocation)`
Less common than DrawIndexedInstanced. Listed for completeness.

---

### DrawInstancedIndirect / DrawIndexedInstancedIndirect
*vtable[40] and vtable[39]. Indirect draw — arguments read from a GPU buffer.*

The CPU supplies a GPU buffer containing draw parameters; the GPU reads them without a CPU roundtrip.
Used in GPU-driven rendering pipelines where the draw count is determined by the GPU (e.g., after
a compute-culling pass). FFXIV appears to use these sparingly or not at all for the tonemap path.

Source: [Microsoft Docs](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-drawinstancedindirect)

---

### Dispatch
*vtable[41] on ID3D11DeviceContext. Launches a compute shader.*

**Signature:** `void Dispatch(uint ThreadGroupCountX, uint ThreadGroupCountY, uint ThreadGroupCountZ)`
Compute shaders run on the GPU without the traditional vertex→rasterizer→pixel pipeline.
Used heavily for post-processing (bloom, tonemapping, screen-space effects) because they can
read and write arbitrary memory without needing render target geometry.

In FFXIV, ~37% of frames use Dispatch as the tonemap mechanism that fills the LDR intermediate.
`_cfDispatchCount` tracks these. When Dispatch fires inside the inject window (_inUiPass=true),
it represents the tonemap compute kernel writing to the BGRA8 LDR surface.

CF-Dispatch path: hook Dispatch at vtable[41], call Original first (tonemap writes LDR), then
inject our rect — same ordering as CF-DI. ✓ confirmed by dispatchInWindow > 0 in diagnostics.

Source: [Microsoft Docs](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-dispatch)

---

### DispatchIndirect
*vtable[42] on ID3D11DeviceContext. Compute dispatch with GPU-sourced arguments.*

**Signature:** `void DispatchIndirect(ID3D11Buffer* pBufferForArgs, uint AlignedByteOffsetForArgs)`
Like DrawInstancedIndirect but for compute. Arguments (ThreadGroupCounts) read from a GPU buffer.
Used in advanced GPU-driven pipelines. ✓ confirmed NOT used by FFXIV's tonemap path:
`dispatchIndirectInWindow: 0` across all frames sampled. Hook present but never fires.

---

### CopyResource
*vtable[47] on ID3D11DeviceContext. GPU-to-GPU resource copy with no format conversion.*

**Signature:** `void CopyResource(ID3D11Resource* pDstResource, ID3D11Resource* pSrcResource)`
Copies an entire resource. Both must have identical dimensions and format.
FFXIV uses CopyResource for ~3% of tonemap blits (copies composited HDR into staging buffer or
copies fully composited LDR across to the swapchain BB).

**Exact constraints (violations silently fail or produce debug-layer errors):**
1. `pDst != pSrc` — different resources.
2. Same resource type (Texture2D→Texture2D, Buffer→Buffer, etc.).
3. **Identical dimensions** — width, height, depth, array size, AND mip count must all match.
4. **Compatible DXGI formats** — identical, OR same typeless group
   (e.g. `R32_FLOAT` ↔ `R32_UINT` both in the `R32_TYPELESS` group). Cross-group = illegal.
5. Neither resource currently mapped (staging resources must be Unmapped first).
6. Destination cannot be `D3D11_USAGE_IMMUTABLE`.
7. Multisampled: both src and dst must have identical sample count/quality.
   To copy MSAA→non-MSAA, use `ResolveSubresource` instead.
8. **Asynchronous** — queued into the command buffer, not synchronous from CPU perspective.

**Detection in a hook:** `CopyResource` where src = full-resolution RGBA16F or BGRA8 texture
and dst = another full-res texture = strong signal of a post-process stage boundary. Compare
`pSrcResource` against your tracked intermediate RT pointer (extracted from `OMSetRenderTargets`).

Also the primary pattern for staging texture uploads: write to CPU-accessible staging texture,
CopyResource → GPU texture. See [Staging Texture](#staging-texture--cpu-upload-pattern).

Source: [Microsoft Docs](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-copyresource)

---

### ClearRenderTargetView
*vtable[50] on ID3D11DeviceContext. Clears an RTV to a solid color.*

**Signature:** `void ClearRenderTargetView(ID3D11RenderTargetView* pRenderTargetView, const FLOAT ColorRGBA[4])`
Called by FFXIV to reset render targets between stages. HUD pass begins with a ClearRTV on the
LDR intermediate (clears previous frame's content before writing HUD elements).
Tracking ClearRTV calls can help identify the boundary between tonemap-fill and HUD draws.

---

### OMSetRenderTargets
*vtable[33] on ID3D11DeviceContext. Binds one or more render targets and an optional depth buffer.*

**Signature:** `void OMSetRenderTargets(uint NumViews, ID3D11RenderTargetView* const* ppRenderTargetViews, ID3D11DepthStencilView* pDepthStencilView)`
The Output-Merger stage sets where pixel data lands. Called hundreds of times per frame.
Every stage transition in FFXIV's pipeline is signaled by an OMSetRenderTargets call.

**FFXIV-TV use:** Hooked at vtable[33] to:
- Detect the 3D scene → post-processing transition (DSV drops to null)
- Learn the BGRA8 LDR surface pointer when it's first bound without DSV post-scene
- Learn the backbuffer pointer (single BB bind per frame)
- Track `_prevDsvPtr` to detect `isMainSceneTransition`
- Set `_inUiPass = true` when LDR is detected

Key diagnostic counters: `omSetRtCount` (how many times omsetrt path injected),
`omSetRtMissSceneNotDrawn`, `omSetRtMissInUiPassFalse`, `omSetRtLdrCount`.

**Shadow pass exclusion:** FFXIV shadow passes call OMSetRenderTargets with `numViews=0`.
These must be ignored — only process calls with `numViews >= 1`.

Source: [Microsoft Docs](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-omsetrendertargets)

---

### ImmediateContext vs DeferredContext
*Two modes of submitting GPU commands in D3D11.*

**ImmediateContext:** Single thread. Commands execute immediately (or are batched by the driver).
Only one ImmediateContext per device. All FFXIV rendering uses the ImmediateContext.

**DeferredContext:** Can be created per-thread for command list building. Commands are recorded
then submitted to the ImmediateContext via `ExecuteCommandList`. Not used in FFXIV's primary path.

FFXIV-TV checks `pCtx == _contextPtr` in every detour to confirm we're intercepting the
ImmediateContext and not a deferred context. This guard prevents false triggers.

---

## D3D11 Resource View Types

### Render Target View (RTV)
*ID3D11RenderTargetView — a "write here" handle for a texture surface.*

An RTV describes which mip slice and array element of a texture2D to render into.
Created from a texture with `device.CreateRenderTargetView(tex, desc)`.
Bound via `OMSetRenderTargets(numViews, &pRTV, pDSV)`.

FFXIV creates RTVs for:
- MainSceneRTV: R16G16B16A16_Float — the full HDR 3D scene surface
- Bloom accumulation RTVs: intermediate R16 surfaces
- LDR intermediate: B8G8R8A8_UNorm — tonemap output, HUD draw target
- Back buffer RTV: the swapchain's presentable surface

**FFXIV-TV:** `_mainSceneRtvPtr` stores the RTV pointer of the HDR surface, set when first seen
bound with `MainSceneDSV`. Non-zero means Stage 1 has started.

---

### Depth Stencil View (DSV)
*ID3D11DepthStencilView — a "depth test / stencil" handle bound alongside RTVs.*

Provides per-pixel depth values for depth testing (closer = wins). Also can supply stencil data.
Created from a `D3D11_BIND_DEPTH_STENCIL` texture (typically D24_UNORM_S8_UINT or D32_FLOAT).

**Key distinction:** Post-processing passes (bloom, tonemap, HUD) do NOT bind a DSV — they write
full-screen quads without depth testing. This is the primary signal used to detect Stage 2.
`hasDsv = (pDSV != 0)` in OMSetRenderTargetsDetour is the gate for `isMainSceneTransition`.

`_mainSceneDsvPtr` — set once per session when the game's main DSV is first seen bound with the
HDR RTV. Used as the ground truth for identifying Stage 1 draw calls.

FFXIV uses **reverse-Z**: near=1.0, far=0.0. See [Reversed-Z Depth Buffer](#reversed-z-depth-buffer).

---

### Shader Resource View (SRV)
*ID3D11ShaderResourceView — a "read from here" handle for shader sampling.*

Describes a texture that a shader can read via `Texture2D.Sample(sampler, uv)`.
Created with `device.CreateShaderResourceView(tex, desc)`.
Bound to a shader stage: `context.PSSetShaderResources(slot, srv)`.

**FFXIV-TV:** `activeSrvSource` tracks which SRV the pixel shader reads (gradient, video frame,
browser frame, loaded image). `_videoSrv`, `_gradientSrv` etc are SRV wrappers around different
source textures. The gradient SRV is the diagnostic default — confirms the shader is running.

---

### Unordered Access View (UAV)
*ID3D11UnorderedAccessView — a read/write handle for compute shaders.*

Allows compute shaders to write to textures or buffers at arbitrary positions.
Used in FFXIV's compute tonemap passes (Dispatch-based tonemap writes to a UAV, not an RTV).
Not directly used by FFXIV-TV's inject code, but explains why compute tonemaps don't bind RTVs.

---

### Staging Texture / CPU Upload Pattern
*A CPU-writable D3D11 texture used as an upload buffer for video frames.*

```
CPU writes pixels → staging texture (D3D11_USAGE_STAGING)
CopyResource(gpuTex, stagingTex) → pixels on GPU
```

Staging textures use `D3D11_USAGE_STAGING` + `D3D11_CPU_ACCESS_WRITE`. The GPU texture uses
`D3D11_USAGE_DEFAULT`. `Map(stagingTex, 0, D3D11_MAP_WRITE, ...)` locks the CPU-side pointer.
`Unmap()` releases it, then `CopyResource` uploads. All on render thread to avoid sync issues.

Used in FFXIV-TV Phase 3 (video frames): decode each frame to CPU buffer → Map → memcpy → Unmap → CopyResource.

**RowPitch gotcha:** Staging textures may have row padding. `D3D11_MAPPED_SUBRESOURCE.RowPitch`
can be larger than `width * bytesPerPixel`. Always copy row-by-row using RowPitch, never flat memcpy
unless RowPitch == expectedStride.

Source: [Microsoft Docs on Texture Upload](https://learn.microsoft.com/en-us/windows/win32/direct3d11/overviews-direct3d-11-resources-subresources-copying)

---

### Constant Buffer (cbuffer / ID3D11Buffer)
*A small GPU buffer that holds per-draw uniform data readable by all shaders.*

Created with `D3D11_BIND_CONSTANT_BUFFER` usage. Updated via `context.UpdateSubresource(buf, data)`
or `Map(WRITE_DISCARD) + memcpy + Unmap` each frame. Bound to shaders:
`context.VSSetConstantBuffers(slot, buf)` / `context.PSSetConstantBuffers(slot, buf)`.

**HLSL `cbuffer` layout rules (CRITICAL):**
- D3D11 cbuffers are packed in 16-byte (float4) rows.
- Each member starts on a 16-byte boundary if it straddles a row. A `float3` + `float` fits in
  one row. A `float3` followed by a `float4` will NOT — the float4 gets bumped to next row.
- Use `packoffset` to force layout, or structure fields as float4 multiples.
- Mismatch between C# struct layout and HLSL cbuffer layout is a common silent bug.
- Use `[StructLayout(LayoutKind.Sequential, Pack=16)]` on C# side to match.

**FFXIV-TV:** `_cbParams` is a single merged cbuffer (replaced old b0/b1/b2 split). Contains:
ViewProj matrix (64 bytes) + ScreenTransform (64 bytes) + post-processing floats (brightness,
gamma, contrast, tint, bloomCap).

---

## D3D11 State Objects

### BlendState (ID3D11BlendState)
*Controls how the output of a pixel shader is combined with the existing render target pixel.*

Created with `device.CreateBlendState(BlendDescription)`. Key fields per render target:
- `BlendEnable`: true = blend. false = overwrite.
- `SrcBlend` / `DestBlend`: how src and dest RGBA factors are computed (ONE, SRC_ALPHA, etc.)
- `BlendOp`: ADD (typical), SUBTRACT, etc.

**Full blend equation — two separate operations run in parallel:**
```
FinalRGB   = SrcBlend        * PixelShaderRGB   [BlendOp]      DestBlend        * RenderTargetRGB
FinalAlpha = SrcBlendAlpha   * PixelShaderAlpha [BlendOpAlpha] DestBlendAlpha   * RenderTargetAlpha
```
`BlendOp` options: ADD (default), SUBTRACT, REV_SUBTRACT, MIN, MAX.

**Standard straight/unassociated alpha:**
```csharp
SrcBlend       = D3D11_BLEND_SRC_ALPHA       // multiply src RGB by its alpha
DestBlend      = D3D11_BLEND_INV_SRC_ALPHA   // multiply dst RGB by (1 - src alpha)
BlendOp        = ADD
SrcBlendAlpha  = ONE
DestBlendAlpha = ZERO
// Result: out = srcRGB * srcA + dstRGB * (1 - srcA)   (classic "over" compositing)
```

**Pre-multiplied alpha** (used by Direct2D, WIC, professional compositing pipelines):
Source texture stores `(R*A, G*A, B*A, A)` — RGB already has alpha baked in.
```csharp
SrcBlend  = ONE               // src is premultiplied, don't multiply again
DestBlend = INV_SRC_ALPHA
// Result: out = srcRGB + dstRGB * (1 - srcA)   (numerically more stable, correct for AA)
```

**`RenderTargetWriteMask` bitmask** (`D3D11_COLOR_WRITE_ENABLE`):
```
RED=1, GREEN=2, BLUE=4, ALPHA=8, ALL=15  (default)
```
Setting `RenderTargetWriteMask = 0` means **no color channel is written at all**. The pixel shader
still runs (depth is written if enabled), but no output goes to the render target. Canonical use:
- **Depth prepass / z-prepass**: populate depth buffer without color writes; render full-shading
  pass with `DepthFunc=EQUAL, DepthWriteMask=ZERO` — guarantees each pixel shaded exactly once.
- **Shadow map generation** to a depth-only DSV (no RTV bound).
- **Stencil-only write passes**.

Our rect uses pre-multiplied alpha or full opacity depending on tint.A.

**`_depthOnlyBlendState`:** A special blend state with `RenderTargetWriteMask=0` (no color writes).
Used for a depth-prime pass: draws the rect's depth into the DSV without touching the color buffer.
This reserves depth for later correct occlusion.

Source: [D3D11_RENDER_TARGET_BLEND_DESC](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/ns-d3d11-d3d11_render_target_blend_desc) | [D3D11_BLEND](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/ne-d3d11-d3d11_blend) | [Output-Merger Stage](https://learn.microsoft.com/en-us/windows/win32/direct3d11/d3d10-graphics-programming-guide-output-merger-stage)

---

### RasterizerState (ID3D11RasterizerState)
*Controls how triangles are converted to pixels: culling, fill mode, depth clamp, scissor.*

Key fields:
- `CullMode`: Back (default) = cull back-facing triangles. None = draw both sides. Front = cull front.
- `FillMode`: Solid (default) vs Wireframe. Wireframe renders only edges — combined with CULL_NONE
  gives full wireframe visualization for mesh debugging.
- `FrontCounterClockwise`: winding order. D3D11 default = **FALSE** = clockwise = front face
  (opposite of OpenGL's CCW default). Set TRUE if importing OpenGL-winding meshes.
- `DepthClipEnable`: see below. Default = TRUE.
- `ScissorEnable`: restrict drawing to scissor rectangles set via `RSSetScissorRects`. Default FALSE.
  Up to 16 simultaneous scissor rects. Used for: ImGui panel clipping, dirty-region optimization.

**`DepthClipEnable` and why to disable it for reversed-Z:**
When TRUE (default), D3D11 clips geometry where NDC z is outside [0,1] — including geometry
past the far plane. With reversed-Z and an **infinite far plane**, there is no far plane value to
clip against. More critically, stencil shadow volumes that extend through/past the camera need
their caps to survive clipping. Setting `DepthClipEnable=FALSE` lets geometry extend past the
near and far clipping planes — depth gets clamped to [0,1] rather than clipped away. Correct for:
- Infinite-far-plane reversed-Z setups (open world, precise depth)
- Stencil shadow volumes (avoids special-case handling for geometry through the camera)

**Default rasterizer state (all defaults per Microsoft docs):**
```
FillMode              = Solid
CullMode              = Back
FrontCounterClockwise = FALSE     // clockwise = front face
DepthBias             = 0
SlopeScaledDepthBias  = 0.0f
DepthBiasClamp        = 0.0f
DepthClipEnable       = TRUE
ScissorEnable         = FALSE
MultisampleEnable     = FALSE
AntialiasedLineEnable = FALSE
```

**FFXIV-TV:** Uses `CullMode=None` (both sides visible — the screen can be viewed from behind)
and `DepthClipEnable=false` to avoid clipping at the near plane under reverse-Z.

Source: [D3D11_RASTERIZER_DESC](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/ns-d3d11-d3d11_rasterizer_desc) | [D3D11_CULL_MODE](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/ne-d3d11-d3d11_cull_mode)

---

### DepthStencilState (ID3D11DepthStencilState)
*Controls depth testing and depth writing per draw call.*

**Full struct:**
```cpp
typedef struct D3D11_DEPTH_STENCIL_DESC {
    BOOL                       DepthEnable;       // depth test on/off
    D3D11_DEPTH_WRITE_MASK     DepthWriteMask;    // ZERO or ALL
    D3D11_COMPARISON_FUNC      DepthFunc;         // LESS, GREATER_EQUAL, etc.
    BOOL                       StencilEnable;
    UINT8                      StencilReadMask;   // bitmask for stencil reads
    UINT8                      StencilWriteMask;  // bitmask for stencil writes
    D3D11_DEPTH_STENCILOP_DESC FrontFace;         // ops for front-facing triangles
    D3D11_DEPTH_STENCILOP_DESC BackFace;          // ops for back-facing triangles
} D3D11_DEPTH_STENCIL_DESC;
```

Key fields:
- `DepthEnable`: false = skip depth test entirely, draw everywhere.
- `DepthWriteMask`: `ZERO` = depth test still runs but NO writes (transparent/decal pass).
  `ALL` = test runs AND writes new depth. Setting to ZERO does **not** disable testing.
- `DepthFunc`: comparison function. For reverse-Z: `GreaterEqual` or `Greater`.

**Complete DepthFunc comparison table (integer codes):**
```
NEVER          = 1  // Always fail
LESS           = 2  // Standard forward-Z (fails for reversed-Z)
EQUAL          = 3  // Used after depth prepass (exact match)
LESS_EQUAL     = 4  // Standard with tolerance
GREATER        = 5  // Reversed-Z (strict)
NOT_EQUAL      = 6
GREATER_EQUAL  = 7  // Reversed-Z preferred (handles same-surface redraw)
ALWAYS         = 8  // No depth test at all (overlay/UI)
```
"Source" = incoming pixel value; "Destination" = existing depth buffer value.

**When `DepthEnable=false` is required:**
- When NO depth buffer is bound: per spec, depth test always passes. Setting false is explicit.
- UI / overlay rendering (2D elements always on top).
- Post-process fullscreen quads (no further depth needed).
- FFXIV-TV: `_dsNoDepth` for LDR and BB inject paths where no DSV is bound.

**Stencil patterns (common use cases):**
- **Portal / stencil shadow**: write 1 to stencil for front faces, decrement for back;
  render shadow with `StencilFunc=EQUAL 1`.
- **Masked UI region**: render mask with `ALWAYS + REPLACE`, render content with `EQUAL`.
- **Silhouette/outline**: render object with `ALWAYS + REPLACE 1`, render scaled-up with `NOT_EQUAL 1`.

`D3D11_DEPTH_STENCILOP_DESC` per-face fields: `StencilFailOp`, `StencilDepthFailOp`,
`StencilPassOp` (what to do to stencil when tests pass/fail), `StencilFunc` (comparison).

**FFXIV-TV configurations:**
- `_dsReverseZWrite`: DepthEnable=true, DepthWriteMask.All, GreaterEqual. Used in scene inject
  (we write our rect's depth so subsequent geometry depth-tests against it).
- `_dsReverseZ`: DepthEnable=true, DepthWriteMask.Zero, Greater. Used in LDR/BB inject
  (nothing follows, just test so rect doesn't float in front of geometry).
- `_dsNoDepth`: DepthEnable=false. Used when no DSV is bound (post-processing stages).

See [Reversed-Z Depth Buffer](#reversed-z-depth-buffer) for why GreaterEqual is needed.

Source: [D3D11_DEPTH_STENCIL_DESC](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/ns-d3d11-d3d11_depth_stencil_desc) | [D3D11_COMPARISON_FUNC](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/ne-d3d11-d3d11_comparison_func) | [D3D11_DEPTH_WRITE_MASK](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/ne-d3d11-d3d11_depth_write_mask)

---

## DXGI Formats & the SwapChain

### DXGI Format (DXGI_FORMAT)
*An enum describing the data layout of a texture or buffer resource.*

Critically important — a mismatch between the format you write and the format of the bound RTV
will silently produce wrong output or be rejected entirely.

Common formats in FFXIV:
| Format | Bytes/px | Use in FFXIV |
|--------|----------|--------------|
| `R16G16B16A16_Float` | 8 | HDR scene buffer (Stage 1 RTV) |
| `B8G8R8A8_UNorm` | 4 | LDR intermediate (tonemap output, HUD target) |
| `R8G8B8A8_UNorm` | 4 | Alternative LDR (less common) |
| `R10G10B10A2_UNorm` | 4 | HDR10 swapchain on modern Windows |
| `D24_UNORM_S8_UINT` | 4 | Depth+stencil buffer (DSV) |
| `D32_FLOAT` | 4 | High-precision depth only |
| `R32_FLOAT` | 4 | Shadow map cascades |

**`_UNorm` suffix:** Unsigned normalized — integer stored but read as [0.0, 1.0] float in shaders.
**`_Float` suffix:** Raw IEEE-754 floating point — can exceed [0,1] range (needed for HDR).
**`B8G8R8A8` vs `R8G8B8A8`:** Channel ORDER differs. FFXIV's LDR uses B8G8R8A8 (BGRA byte order).
If your SRV expects RGBA but the texture is BGRA, colors will appear with red and blue swapped.

`IsLdrFullRes()` in FFXIV-TV checks: format == B8G8R8A8_UNorm AND width == screen width.
This identifies the LDR surface among all BGRA8 textures. ✓ confirmed.

---

### IDXGISwapChain
*The DXGI object managing the back buffer(s) and the Present call.*

Created by D3D11CreateDeviceAndSwapChain. Manages one or more back buffer textures that alternate
as the "current frame" surface. When Present is called, the current back buffer is displayed and
the next becomes the new render target.

FFXIV uses a standard double-buffered swapchain. The back buffer texture is a B8G8R8A8_UNorm (or
R10G10B10A2 on HDR displays) texture at screen resolution.

`IDXGISwapChain::Present` is vtable[8] on the swapchain object — common injection point for
ReShade, GShade, Dalamud's ImGui layer. SpecialK hooks at this level.

---

### IDXGISwapChain::Present Hook
*Hooking vtable[8] of the swapchain to execute after all game rendering but before flip.*

The canonical way to inject overlays. ReShade/GShade/SpecialK all hook Present.
Dalamud hooks Present to run ImGui. This is why Dalamud's layer (Stage 4) is always last —
it executes in the Present hook, after all game draw calls.

**Limitation for FFXIV-TV:** Injecting at Present means the game's HUD has already drawn.
Our rect would appear OVER the HUD (wrong). We must inject earlier — at the draw call that
fills the LDR surface, before HUD draws. This is the fundamental motivation for CF-DI / CF-Draw /
CF-Dispatch hooking.

---

### Back Buffer (BB)
*The swapchain's current render target — the texture that becomes the displayed frame.*

FFXIV binds the back buffer RTV exactly once per frame (in Stage 3). This single bind event is
how `_currentBbRtvPtr` is set in FFXIV-TV. The back buffer pointer is session-stable in FFXIV
(same texture each frame, only pointer changes on resize). `backbufferLearned: true` in /render
confirms it's been identified.

The BB is distinct from the LDR intermediate. FFXIV composites the LDR intermediate → BB in Stage 3.

---

### sRGB Gamma
*A perceptual color space where intensity is not linear with voltage — mimics human vision.*

sRGB applies a gamma curve (~2.2 power). Colors stored as sRGB uint8 (0–255) appear correct on
monitors because monitors expect sRGB input.

**D3D11 sRGB behavior:**
- `B8G8R8A8_UNorm_SRgb`: When you create an SRV or RTV from this format, hardware automatically
  linearizes (read) or gamma-encodes (write) values.
- `B8G8R8A8_UNorm`: Raw storage — no automatic sRGB conversion. You manage gamma yourself.
- FFXIV's LDR intermediate is `B8G8R8A8_UNorm` (not SRgb). Our pixel shader must handle
  linearization manually if source textures are sRGB-encoded.

**In pixel shaders:** `pow(color.rgb, 2.2)` = decode sRGB to linear. `pow(color.rgb, 1.0/2.2)` = encode.
Better: use the piecewise formula (handles near-black correctly). See gamma parameter in post-processing.

**FFXIV-TV BloomCap interaction:** The LDR BGRA8 surface doesn't have linear light. Our pixel
shader's `BloomCap` clamps output to avoid exceeding the game's bloom threshold, which is calibrated
to sRGB values in the range ~0.3–0.5. ✓ confirmed: values above bloom threshold cause glow on rect.

---

## HLSL Shader Concepts

### SV_VertexID (Procedural Quad)
*A system-value semantic providing the current vertex index — enables geometry without a vertex buffer.*

`uint vid : SV_VertexID` in the vertex shader receives 0,1,2,3,4,5 for `Draw(6, 0)` (TriangleList, 2 tris).
Map VID to UV:
```hlsl
float2 uv = float2((vid & 1) ? 1.0 : 0.0, (vid >> 1) & 1 ? 0.0 : 1.0);
```

**Full-screen triangle (preferred over 6-vertex quad for post-process):**
3 vertices, single triangle that covers the full NDC [-1,+1] range. Avoids overdraw seam
along the diagonal and is slightly cheaper (IA processes 3 verts instead of 6):
```hlsl
struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
VSOut VS(uint vid : SV_VertexID)
{
    float2 pos = float2((vid & 1) ? 3.0 : -1.0,   // x: -1, -1, 3
                        (vid & 2) ? -3.0 :  1.0);  // y:  1, -3, 1
    VSOut o;
    o.pos = float4(pos, 0.0, 1.0);
    o.uv  = float2((vid & 1) ? 2.0 : 0.0,
                   (vid & 2) ? 2.0 : 0.0);
    return o;
}
// Call: Draw(3, 0). UV goes 0..2; clamp/wrap sampler handles it correctly for [0,1] textures.
```

**Why no InputLayout needed:** When using SV_VertexID with no vertex buffer, the IA has nothing
to fetch from a buffer. Call `IASetInputLayout(null)` explicitly — D3D11 won't clear it automatically
and the debug layer will warn if a previous pass left one bound.

For a world-space screen, generate UVs and transform the position through the ScreenTransform TRS
then ViewProj. No vertex buffer allocation, no InputLayout needed.

**Pattern used here:** procedural quad + ScreenTransform cbuffer.

**Performance:** Slightly less GPU-friendly than a VBO for large meshes (no vertex cache reuse)
but negligible for a 6-vertex quad. Saves CPU-side buffer management.

Source: [SV_VertexID docs](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/dx-graphics-hlsl-semantics) | [IA stage without buffers](https://learn.microsoft.com/en-us/windows/win32/direct3d11/d3d10-graphics-programming-guide-input-assembler-stage-no-buffers)

---

### HLSL cbuffer Constant Packing
*The alignment rules D3D11 uses when reading constant buffers.*

HLSL packs cbuffer members into 16-byte (float4) rows. A member will NOT cross a 16-byte boundary
— it's pushed to the next row. This creates silent bugs when C# structs have different layouts.

Examples:
```hlsl
cbuffer Params : register(b0) {
    float4x4 ViewProj;      // 64 bytes (4 × float4) — always aligned
    float4x4 ScreenTRS;     // 64 bytes — always aligned
    float    Brightness;    // 4 bytes
    float    Gamma;         // 4 bytes — same float4 row as Brightness (fine)
    float    Contrast;      // 4 bytes — same row (fine)
    float    BloomCap;      // 4 bytes — same row (fine)
    float4   Tint;          // 16 bytes — NEW row: Brightness took 12 bytes, Tint would cross → next row
}
```

**THE SILENT MISMATCH CASE — `float2` followed by `float4`:**
```hlsl
cbuffer MyBuf { float2 A; float4 B; }
// HLSL: A at offset 0 (8 bytes), B bounced to offset 16. Total = 32 bytes.
```
```csharp
[StructLayout(LayoutKind.Sequential)]
struct MyBuf { public Vector2 A; public Vector4 B; }
// C#:   A at offset 0 (8 bytes), B at offset 8.  Total = 24 bytes.  ← WRONG, silent garbage
```
Fix: add two float padding fields after A to force B to offset 16:
```csharp
[StructLayout(LayoutKind.Sequential)]
struct MyBuf { public Vector2 A; public float _pad0; public float _pad1; public Vector4 B; }
```

**Array padding rule:** Each array element in a cbuffer occupies a full 16-byte register
regardless of element size. `float2 arr[4]` costs **64 bytes** (4 × 16), not 32.
C# array layout must account for this — typically use `Vector4[]` and pack manually.

**`packoffset` for explicit control:**
```hlsl
cbuffer MyBuffer {
    float4 Element1 : packoffset(c0);     // register c0.xyzw (offset 0)
    float1 Element2 : packoffset(c1);     // register c1.x    (offset 16)
    float1 Element3 : packoffset(c1.y);   // register c1.y    (offset 20)
}
```

**C# side:** `[StructLayout(LayoutKind.Sequential, Pack=16)]` and explicit field ordering matching
the HLSL layout. Use `float` for scalars and `Vector4` for float4.

Source: [HLSL packing rules](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/dx-graphics-hlsl-packing-rules) | [packoffset](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/dx-graphics-hlsl-variable-packoffset)

---

### Vertex Shader (VS) / Pixel Shader (PS)
*The two programmable shader stages used in FFXIV-TV's draw call.*

**VS** runs once per vertex (6 × for our Draw(6,0)). Takes VID → computes world position via
ScreenTransform, applies ViewProj, outputs SV_Position (clip-space). Also outputs UV to PS.

**PS** runs once per rasterized pixel. Receives interpolated UV from VS. Samples the source
texture (video frame, gradient, image, browser). Applies post-processing pipeline:
1. Sample: `float4 c = tex.Sample(samp, uv)`
2. Tint: `c.rgb *= Tint.rgb`
3. Brightness: `c.rgb *= Brightness`
4. Contrast: `c.rgb = (c.rgb - 0.5) * Contrast + 0.5`
5. Gamma: `c.rgb = pow(c.rgb, Gamma)`
6. BloomCap: `c.rgb = min(c.rgb, BloomCap)`

---

### Compute Shader (CS)
*A GPU program that runs outside the traditional render pipeline.*

No vertex, rasterizer, or pixel stage. Runs in parallel thread groups
(`numthreads(x, y, z)` in HLSL). Reads/writes via UAVs (Unordered Access Views).
Dispatched via `context.Dispatch(groupX, groupY, groupZ)`.

**`numthreads` declaration and system-value semantics:**
```hlsl
[numthreads(X, Y, Z)]
void CSMain(
    uint3 groupID          : SV_GroupID,          // which thread group (0..Dispatch-1)
    uint3 groupThreadID    : SV_GroupThreadID,     // thread within group (0..numthreads-1)
    uint3 dispatchThreadID : SV_DispatchThreadID,  // absolute = groupID*numthreads + groupThreadID
    uint  groupIndex       : SV_GroupIndex         // flat index = z*X*Y + y*X + x
) { ... }
```

**Hard limits for cs_5_0 (D3D11 hardware):**
| Constraint | Value |
|-----------|-------|
| Max X | 1024 |
| Max Y | 1024 |
| Max Z | 64 |
| Max X*Y*Z total | 1024 |
| Max Dispatch dimension | 65535 per axis |
| Max UAVs bound | 8 |

`Dispatch(x,y,z)` arguments = number of *thread groups*. Total threads = `x*X` in X dimension, etc.
Example: `Dispatch(240, 135, 1)` with `numthreads(8,8,1)` = fullscreen 1920×1080 at 8×8 tiles.

**UAV vs RTV for compute output:** Compute shaders **cannot write to RTVs**. They write exclusively
through UAVs (`RWTexture2D<float4>`, `RWStructuredBuffer<T>`, etc.) bound via `CSSetUnorderedAccessViews`.
To use compute output as a texture in a subsequent render pass: write to `RWTexture2D` UAV, then
rebind that same texture as an SRV in the PS pass. Cannot have a resource as both UAV and SRV
simultaneously — unbind from CS before binding as PS SRV.

FFXIV's tonemap compute writes to a UAV matching the LDR surface; this is why there's no
`OMSetRenderTargets` call immediately before/after the tonemap Dispatch. ✓

Used extensively in modern engines for post-processing (bloom, TAA, SSAO, tonemap) because
they're more flexible than full-screen quad draws. FFXIV uses compute for ~37% of tonemap frames. ✓

FFXIV-TV doesn't use compute itself, but must recognize Dispatch calls to detect and intercept
the compute tonemap path (CF-Dispatch inject).

Source: [HLSL Compute Shaders](https://learn.microsoft.com/en-us/windows/win32/direct3d11/direct3d-11-advanced-stages-compute-shader) | [numthreads](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/sm5-attributes-numthreads) | [Dispatch](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-dispatch)

---

### SamplerState (ID3D11SamplerState)
*Describes how texture coordinates outside [0,1] are handled and how filtering is applied.*

Key fields:
- `Filter`: Point (nearest), Linear (bilinear), Anisotropic (aniso).
- `AddressU/V/W`: Wrap, Clamp, Mirror, Border.
- `MaxAnisotropy`: 1–16 for anisotropic filtering.
- `ComparisonFunc` / `BorderColor`: for shadow comparison samplers.

FFXIV-TV uses `Filter=MinMagMipLinear, AddressU=V=Clamp` — bilinear interpolation, no wrapping.
This gives smooth texture sampling for video frames without edge artifacts.

---

### InputLayout (ID3D11InputLayout)
*Describes how vertex buffer data maps to VS input semantics. NOT needed when using SV_VertexID.*

`ID3D11InputLayout` binds a memory layout description (`D3D11_INPUT_ELEMENT_DESC[]`) to a compiled
VS bytecode input signature. The IA stage uses this to know how to fetch and convert each vertex attribute.

**Why procedural quads (SV_VertexID) don't need it:** When you call `Draw(N, 0)` with no vertex
buffer bound, the IA has nothing to fetch. Call `IASetInputLayout(null)` explicitly.

**How it works with a VBO:** Each `D3D11_INPUT_ELEMENT_DESC` specifies:
- `SemanticName` + `SemanticIndex` (e.g. `"POSITION", 0`)
- `Format` (e.g. `DXGI_FORMAT_R32G32B32_FLOAT` for `float3`)
- `InputSlot` (which vertex buffer slot, 0–15)
- `AlignedByteOffset` (byte offset within the vertex struct)

The IL is **validated at creation time** against the VS bytecode input signature — if a VS input
semantic has no matching element desc, `CreateInputLayout` fails with `E_INVALIDARG`.

**Debug-layer error if missing for a VBO draw:**
```
D3D11 ERROR: Input Layout is not set but shader needs it...
```
The draw still executes but reads garbage data.

**FFXIV-TV:** Does not use InputLayout (procedural quad via SV_VertexID). If vertex buffers are
ever added (e.g., for a multi-screen mode), InputLayout creation must match the VS signature exactly.

Source: [CreateInputLayout](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11device-createinputlayout)

---

### d3dcompiler_47 / Runtime Shader Compilation
*Windows system DLL that compiles HLSL source to bytecode at runtime.*

**Full C++ signature:**
```cpp
HRESULT D3DCompile(
  LPCVOID                pSrcData,      // HLSL source text (UTF-8)
  SIZE_T                 SrcDataSize,   // byte length of source
  LPCSTR                 pSourceName,   // filename for error messages (may be NULL)
  const D3D_SHADER_MACRO *pDefines,     // #define array, NULL-terminated, or NULL
  ID3DInclude            *pInclude,     // include handler, or D3D_COMPILE_STANDARD_FILE_INCLUDE
  LPCSTR                 pEntrypoint,   // e.g. "VSMain"
  LPCSTR                 pTarget,       // profile string
  UINT                   Flags1,        // D3DCOMPILE_* flags
  UINT                   Flags2,        // effect flags, 0 for shaders
  ID3DBlob               **ppCode,      // [out] compiled bytecode
  ID3DBlob               **ppErrorMsgs  // [out] error/warning text blob
);
```

**Target profile strings (SM 5.0 / D3D11):**
| Profile | Stage |
|---------|-------|
| `vs_5_0` | Vertex shader |
| `ps_5_0` | Pixel shader |
| `gs_5_0` | Geometry shader |
| `hs_5_0` / `ds_5_0` | Hull / Domain (tessellation) |
| `cs_5_0` | Compute shader (DirectCompute 5.0) |
| `vs_4_0` / `ps_4_0` | Shader Model 4 (D3D10 compat) |

**Compile flags (Flags1):**
| Flag | Effect |
|------|--------|
| `D3DCOMPILE_DEBUG` | Embed debug info (file/line/symbol) |
| `D3DCOMPILE_SKIP_OPTIMIZATION` | Disable optimizer (use with DEBUG) |
| `D3DCOMPILE_OPTIMIZATION_LEVEL3` | Max optimization (release builds) |
| `D3DCOMPILE_WARNINGS_ARE_ERRORS` | Treat warnings as errors |
| `D3DCOMPILE_ENABLE_STRICTNESS` | No legacy syntax |

Debug builds: `D3DCOMPILE_DEBUG | D3DCOMPILE_SKIP_OPTIMIZATION`
Release builds: `D3DCOMPILE_OPTIMIZATION_LEVEL3`

**Error handling:** On failure, `ppErrorMsgs` is a blob containing a null-terminated UTF-8 string.
Call `blob->GetBufferPointer()` cast to `char*`. Always check even on success for warnings.

**DLL location on Windows 10/11:** `C:\Windows\System32\d3dcompiler_47.dll` (inbox, no redistribution needed).

**FFXIV-TV approach:** Uses `Vortice.D3DCompiler` wrapping d3dcompiler_47 for runtime compilation.
```csharp
ReadOnlyMemory<byte> bytecode = Compiler.Compile(
    hlslSource, entryPoint: "VSMain", sourceName: "MyShader.hlsl", profile: "vs_5_0",
    shaderFlags: ShaderFlags.Debug | ShaderFlags.SkipOptimization);
```
Trade-off: no pre-baked bytecode needed, but compilation adds ~10ms on first use.

Alternative: offline compilation with `fxc.exe` → embed bytecode as `byte[]`. Faster startup, but
requires a separate build step.

Source: [D3DCompile](https://learn.microsoft.com/en-us/windows/win32/api/d3dcompiler/nf-d3dcompiler-d3dcompile) | [D3DCOMPILE constants](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/d3dcompile-constants)

---

## Rendering Technique Concepts

### Reversed-Z Depth Buffer
*A technique where near=1.0 and far=0.0, opposite of the traditional convention.*

Standard D3D11 convention: near plane → z=0.0, far plane → z=1.0 in NDC (after divide by w).
Reversed-Z: near plane → z=1.0, far plane → z=0.0.

**Why reversed-Z?** IEEE-754 floats have their densest precision near 0.0. In standard Z, the
far plane (where you need precision most) maps to z=1.0 — the worst end of float precision.
Reversed-Z flips this: far plane → z=0.0 (highest float precision), near → z=1.0 (wastes
the dense end where you don't need it, but the near plane already has plenty of depth range).
Nathan Reed's simulation confirms: **reversed-Z with a float depth buffer gives zero error rate**,
vs visible z-fighting with standard Z. The trick traces to SIGGRAPH '99 (Lapidous & Jiao)
and was re-popularized by MJP, Brano Kemen, and Emil Persson.

FFXIV uses reversed-Z throughout. ✓ Confirmed: standard `LESS` depth comparison → nothing renders;
`GREATER_EQUAL` works correctly. Depth buffer clear value in FFXIV is 0.0f (not 1.0f). ✓

Other games using reversed-Z: Doom (2016/id Tech 6), Battlefield/Frostbite, Microsoft Flight
Simulator, The Witcher 3 — now considered best practice for float depth buffers.

**Projection matrix for reversed-Z (finite far plane, row-major D3D convention):**
```
Standard:   P[2][2] = f/(f-n),    P[3][2] = -n*f/(f-n)   → depth at near=0, at far=1
Reversed:   P[2][2] = n/(n-f),    P[3][2] =  n*f/(f-n)   → depth at near=1, at far=0
```
For **infinite far plane** (recommended): `P[2][2] = 0`, `P[3][2] = near`. Depth = near/z.
Approaches 0 at infinity, equals 1 at z=near. Virtually no quality difference from finite far.

**For FFXIV-TV:** we read ViewProj directly from `Control.Instance()->ViewProjectionMatrix` —
already a correctly configured reverse-Z matrix. No manual construction needed. ✓

**Depth state for reverse-Z:**
```csharp
DepthFunc = ComparisonFunction.GreaterEqual  // closer = higher Z value = passes GREATER_EQUAL
```
Use `GREATER_EQUAL` (not strict `GREATER`) to avoid precision-edge failures when redrawing the
same surface. All 8 comparison functions: NEVER=1, LESS=2, EQUAL=3, LESS_EQUAL=4,
GREATER=5, NOT_EQUAL=6, GREATER_EQUAL=7, ALWAYS=8.

Sources:
- Nathan Reed "Depth Precision Visualized": https://developer.nvidia.com/content/depth-precision-visualized
- MJP "Attack of the Depth Buffer": https://therealmjp.github.io/posts/attack-of-the-depth-buffer/
- D3D11_COMPARISON_FUNC: https://learn.microsoft.com/en-us/windows/win32/api/d3d11/ne-d3d11-d3d11_comparison_func

---

### ViewProj Matrix (World → Clip Space)
*The combined View × Projection matrix that transforms world-space positions into clip space.*

**View matrix:** Transforms world-space → camera-space (eye at origin, looking down -Z).
**Projection matrix:** Transforms camera-space → clip-space (NDC after divide by w).
The combined `ViewProj = View × Projection` — applied as `clipPos = worldPos * ViewProj`
in row-vector convention (System.Numerics / D3D).

**Exact FFXIVClientStructs access:**
```csharp
// Namespace: FFXIVClientStructs.FFXIV.Client.Game.Control
// Field offset: 0x76B0 in the Control struct
Matrix4x4 vp = Control.Instance()->ViewProjectionMatrix;
```
Type is `FFXIVClientStructs.FFXIV.Common.Math.Matrix4x4`. Implicit cast operators exist:
```csharp
// Zero-cost reinterpret (memory layout is identical — 16 floats, 64 bytes):
System.Numerics.Matrix4x4 vp = Control.Instance()->ViewProjectionMatrix;
```
Updated every frame by the game. Must be read in the render callback, not cached. ✓

**Row-major storage, HLSL upload options — CRITICAL:**
System.Numerics (and FFXIVClientStructs) store rows consecutively in memory (row-major).
HLSL constant buffers default to **column-major** packing. Two correct approaches:

Option A — declare `row_major` in HLSL (FFXIV-TV uses this):
```hlsl
cbuffer Params : register(b0) { row_major float4x4 ViewProj; ... }
// Then upload without transpose — System.Numerics row-major matches 'row_major' declaration
```

Option B — transpose before upload (Pictomancy uses this):
```csharp
consts.ViewProj.Transpose();   // rotate the matrix so rows become columns
ctx.Context.UpdateSubresource(ref consts, _constantBuffer);
// HLSL has no 'row_major' declaration — reads as column-major (transposed = correct)
```
**Do NOT mix them** — transpose + `row_major` = double-transposed = wrong rendering.
FFXIV-TV uses Option A (`row_major` in HLSL shader). ✓ confirmed working.

**HLSL multiply order:**
```hlsl
// row-vector convention (correct for row-major matrices and 'mul(v, M)'):
float4 clipPos = mul(float4(worldPos, 1.0), ViewProj);
// col-vector convention (correct if matrix was transposed on upload):
// float4 clipPos = mul(ViewProj, float4(worldPos, 1.0));
```
`mul(vector, matrix)` in HLSL = row-vector. `mul(matrix, vector)` = column-vector. They differ by
a transpose — mixing them causes incorrect coordinate transforms.

**Separate View + Projection (when needed):**
```csharp
// Scene.Camera (user-facing game camera):
Scene.CameraManager.Instance()->CurrentCamera->ViewMatrix      // View only
// Render.Camera (low-level renderer):
Render.Camera* rc = SceneCamera->RenderCamera;
rc->ViewMatrix          // View
rc->ProjectionMatrix    // Projection
rc->FoV, AspectRatio, NearPlane, FarPlane
rc->StandardZ           // false = REVERSED-Z (FFXIV default)
rc->FiniteFarPlane
```

**`CameraParameter` shader struct** (per-frame GPU cbuffer, not directly accessible as singleton):
- `ViewProjectionMatrix` at offset 0x60
- `InverseViewProjectionMatrix` at offset 0xA0
- `InverseProjectionMatrix` at offset 0xE0
- `ProjectionMatrix` at offset 0x120
- `EyePosition` (Vector3) at offset 0x1A0

Sources: [FFXIVClientStructs Control.cs](https://github.com/aers/FFXIVClientStructs) | [Pictomancy DXRenderer.cs](https://github.com/sourpuh/ffxiv_pictomancy) | [Dalamud GameGui.cs](https://github.com/goatcorp/Dalamud)

---

### ScreenTransform TRS Matrix
*A Transform-Rotate-Scale matrix that positions, orients, and sizes the virtual screen in 3D space.*

A TRS matrix encodes: Translation (world position), Rotation (yaw/pitch/roll), Scale (width/height).
For a flat screen: scale = (width, height, 1), rotation = yaw matrix around Y-axis, translation = center.

Construction:
```csharp
var S = Matrix4x4.CreateScale(width, height, 1.0f);
var R = Matrix4x4.CreateRotationY(yaw);
var T = Matrix4x4.CreateTranslation(center);
var TRS = S * R * T;  // apply in SRT order
```

In the vertex shader: `worldPos = mul(float4(localPos, 1.0), ScreenTRS)` (transforms from
local quad space [-0.5,+0.5]² to world space), then `mul(float4(worldPos, 1.0), ViewProj)`.

FFXIV-TV uses this approach for full yaw/pitch/roll support. ✓

---

### NDC (Normalized Device Coordinates)
*The canonical coordinate space after perspective divide: x,y,z all in [-1,+1] (or [0,1] for D3D Z).*

After the vertex shader outputs `SV_Position` (clip-space), the rasterizer divides by W:
`ndc = clip.xyz / clip.w`. In D3D11: x=[-1,+1], y=[-1,+1], z=[0,1] (reversed-Z: near=1, far=0).

Then the viewport transform maps NDC to pixel coordinates:
`px = (ndc.x * 0.5 + 0.5) * width`, `py = (0.5 - ndc.y * 0.5) * height`.

**FFXIV-TV WorldToScreen projection for visibility check:**
The `/render` API computes NDC from ViewProj to determine if the screen's center is on-screen.
`screenCenter` → `mul(ViewProj)` → divide by W → NDC.x and NDC.y in [-1,+1] = visible. ✓

---

### Inject Ordering
*The critical timing of when our custom draw call fires relative to FFXIV's pipeline stages.*

**WRONG order:** inject during Stage 4 (Present/Dalamud). Our rect over everything including HUD.
**ALSO WRONG:** inject during Stage 1 (scene pass). Our rect over 3D geometry but under HUD... wait
— actually wrong because we'd corrupt the scene depth and blend state mid-render.
**CORRECT order:** inject during Stage 2, after tonemap fills LDR, before Stage 3 HUD draws.

The inject window opens when the first draw call targeting the BGRA8 LDR surface fires.
For CF-DI: we hook DrawIndexed. When it targets the LDR surface, we inject first (our rect → LDR),
then call Original (tonemap blit → fills LDR around/over our rect based on blend state).

Wait — actually the architecture is:
- Call Original FIRST (tonemap fills LDR with the composited scene)
- THEN inject our rect onto the now-filled LDR surface

This means our rect is drawn ON TOP of the scene in the LDR surface. Then HUD draws on top of our
rect (because HUD is in Stage 3, after our Stage 2 inject). ✓ confirmed: HUD in front.

**Why this is hard:** FFXIV uses 4 different draw call types for the tonemap blit (DrawIndexed,
Draw, Dispatch, CopyResource) and the mix varies frame-to-frame. Missing the tonemap call means
falling through to the fallback omsetrt path (injects at BB bind = AFTER HUD = wrong order). ✓

---

### Inject Window
*The per-frame period between "tonemap fill starts" and "HUD draws begin" — the only safe inject point.*

Opened by: detecting the first draw call into the BGRA8 LDR surface (either via transition detection
or via BGRA8 fallback detection).
Closed by: `_frameInjectionDone = true` after the first successful inject.

If the window is missed (e.g., all CF-* path detections fail), the omsetrt fallback path fires at
BB bind — which is post-HUD = wrong ordering.

`dispatchInWindow` counter shows how many Dispatches fire within the open inject window.
These represent compute tonemap calls that we're correctly intercepting. ✓

---

### _inUiPass Guard
*A boolean flag set true when we've detected we're in Stage 2 or later, enabling CF-* inject logic.*

Set true when:
1. `isMainSceneTransition` fires (DSV drops to null after Stage 1 = confirmed in Stage 2)
2. BGRA8 fallback: first no-DSV BGRA8 full-res surface seen after `_mainSceneRtvPtr != 0`
3. CF-Dispatch early detect: Dispatch fires with prevCallHadDsv=true (proxy for scene→compute transition)

Reset to false in `PrepareHooks()` each frame.

While `_inUiPass == false`: all draw hooks pass through immediately (no inject attempt).
While `_inUiPass == true`: draw hooks check if they're targeting the LDR surface and inject.

---

### BGRA8 Fallback Detection
*Secondary method to open the inject window when isMainSceneTransition misses.*

Root cause of 77% miss rate (original bug): `_prevDsvPtr` tracking in `isMainSceneTransition`
was corrupted by intermediate shadow/bloom passes between Stage 1 and Stage 2, causing the
transition detection to fire at the wrong time or not at all.

Fix: cache all BGRA8 full-res surfaces seen post-scene (`_postBloomRtvCache`). When a previously-
cached LDR surface gets a no-DSV bind after `_mainSceneRtvPtr != 0`, force `_inUiPass = true`.

`!hasDsv` guard added to prevent false triggers during Stage 1 transparent BGRA8+DSV passes. ✓
(v0.5.157 — `!hasDsv` reduced omSetRtCount from 89% back to 0% while keeping CF-DI at 100%). ✓

`IsLdrFullRes()` logic: query texture desc from the RTV, check B8G8R8A8_UNorm AND width == render width.
Results cached in `_postBloomRtvCache` (bool: isLdr) to avoid per-frame COM query overhead.

---

### isMainSceneTransition
*Detection of the transition from Stage 1 (DSV-bound) to Stage 2 (no-DSV) via OMSetRenderTargets.*

Fires when: `prevDsvPtr == _mainSceneDsvPtr` AND `newDsv == null` AND `hasRtvs == true`.
Meaning: the previous OMSetRT had the main scene DSV bound, this one drops it.

Fragile because: any intermediate pass between Stage 1 and Stage 2 that binds a different DSV
will update `_prevDsvPtr` away from `_mainSceneDsvPtr`, breaking the comparison.
Shadow passes (`numViews=0`) must be excluded from `_prevDsvPtr` updates to avoid this.

---

### _mainSceneDsvPtr / _mainSceneRtvPtr
*Session-lifetime pointers to the main 3D scene's depth buffer and HDR render target.*

`_mainSceneDsvPtr`: Set once when the first OMSetRT with both a full-res HDR RTV and a non-null
DSV fires. Identifies the main depth buffer. Used as ground truth for scene detection.

`_mainSceneRtvPtr`: Set when the HDR RTV is seen bound with `_mainSceneDsvPtr`. Proof that Stage 1
has started. Non-zero means the scene is at least partially drawn this frame. `mainSceneRtvEverSeen`
in /render confirms this was seen at least once. ✓

Both pointers are stable across frames (FFXIV doesn't recreate these on every frame). Only reset
on device lost / plugin reload.

---

### CF Paths (CF-DI, CF-Draw, CF-Dispatch, CF-omsetrt, CF-CopyResource)
*Abbreviations for the specific D3D11 call types that can be used as the tonemap inject trigger.*

| Path | Vtable | % frames (observed) | Status |
|------|--------|---------------------|--------|
| CF-DI (DrawIndexed) | [12] | ~40% | Primary ✓ |
| CF-Draw (Draw) | [13] | ~1% | Working ✓ |
| CF-DII (DrawIndexedInstanced) | [20] | ~3% | Not hooked |
| CF-Dispatch | [41] | ~37% | Hooked, enabled |
| CF-CopyResource | [47] | ~3% | Not hooked |
| CF-omsetrt | [33] | fallback | Injects at BB bind (wrong order) |
| CF-DispatchIndirect | [42] | 0% | Hooked, never fires ✓ |

`lastInjectPath` in /inject shows which path fired for the last frame. ✓

---

### BB Learn / Backbuffer Learning
*The process of identifying the swapchain's back buffer RTV pointer from the single BB bind per frame.*

FFXIV binds the back buffer RTV exactly once per frame. FFXIV-TV observes this single OMSetRT
call (no DSV, full-res, different from LDR surface) and stores the pointer as `_currentBbRtvPtr`.

`backbufferLearned: true` in /render confirms this pointer was identified. ✓
Once learned, draw calls targeting `_currentBbRtvPtr` are recognized as the BB inject fallback path.

---

## Post-Processing Parameters

### Brightness
*Linear multiplier applied to all output pixel RGB values. 1.0 = no change.*

`c.rgb *= Brightness` in pixel shader. Range typically 0.1–3.0.
Values > 1.0 will push output above the LDR surface's [0,1] range — gets clamped to 1.0.
With BloomCap active, effective max is determined by BloomCap, not Brightness.

### Gamma
*Power curve applied to output pixels to adjust perceptual brightness. 1.0 = no change.*

`c.rgb = pow(c.rgb, Gamma)` (applied after brightness). Range 0.1–3.0.
> 1.0 = darker midtones (more punch). < 1.0 = brighter midtones (washed out).
Note: for correct gamma workflow, apply BEFORE brightness (or make explicit which space you're in).

### Contrast
*Contrast around the 0.5 midpoint. 1.0 = no change. > 1.0 = more contrast.*

`c.rgb = (c.rgb - 0.5) * Contrast + 0.5`. Range 0.0–3.0.
Operates on linear signal — pixels at 0.5 are unchanged, pixels above/below are pushed further.

### Tint
*Per-channel RGBA multiplier. (1,1,1,1) = no change. A < 1 = transparent screen.*

`c *= Tint` (applied first in pipeline). Allows color grading and transparency.
Alpha < 1 makes the screen translucent — underlying scene bleeds through.

### BloomCap
*Maximum output brightness to prevent triggering FFXIV's bloom post-effect on injected content.*

`c.rgb = min(c.rgb, BloomCap)` — applied as final step.
FFXIV's bloom threshold is empirically ~0.3–0.5 in the LDR surface.
BloomCap default: 0.35. At this value, bright regions of the injected image are darkened to avoid
the bloom glow that would otherwise appear around high-contrast content.
Set to 0 to disable. Set to 1.0 for full brightness (bloom will glow on bright content).

---

## Dalamud & Plugin Hooking Infrastructure

### IGameInteropProvider
*Dalamud service for safely hooking game functions.*

`gameInterop.HookFromAddress<TDelegate>(fnPtr, detour)` → `Hook<TDelegate>`
`gameInterop.HookFromSignature<TDelegate>(sig, detour)` → finds function by AOB scan then hooks.

Internally wraps MinHook (x64 function detour library). Patches the first bytes of the target
function with a JMP to a generated trampoline. The trampoline calls the detour; the detour calls
`hook.Original(...)` to execute original bytes.

**D3D11 vtable hooking:** `HookFromAddress(vtable[N], detour)` — the vtable entry is a function
pointer (8 bytes on x64), not a function. MinHook can hook the target function that the pointer
points to. Works because D3D11's ImmediateContext vtable is a fixed layout COM interface. ✓

**After hooking:** Must call `hook.Enable()`. After disposal: `hook.Disable(); hook.Dispose()`.
Hooks NOT disabled on dispose will leave the game in a patched state = crash. ✓

---

### Dalamud Hook Detour Pattern
*The required structure for any function hooked via IGameInteropProvider.*

```csharp
private void DrawIndexedDetour(nint pContext, int indexCount, int startIndex, int baseVertex)
{
    if (_inHookDetour) { _drawIndexedHook!.Original(pContext, indexCount, startIndex, baseVertex); return; }
    _inHookDetour = true;
    try {
        // ... injection logic ...
        _drawIndexedHook!.Original(pContext, indexCount, startIndex, baseVertex);  // in try or finally
    }
    catch (Exception ex) { PluginLog.Error(ex, "..."); }
    finally { _inHookDetour = false; }
}
```

Rules:
- `Original()` MUST be called (finally ensures it even on exception).
- Re-entrancy guard `[ThreadStatic] private static bool _inHookDetour` prevents infinite loops
  when your injected draw call re-enters hooked methods.
- Wrap body in try/catch — unhandled exceptions in D3D detours **crash the game**. ✓
- Never use null-forgiving `!` on fields that might be null at call time. ✓ (CLAUDE.md rule)

---

### Re-entrancy Guard
*A per-thread boolean that prevents a hook detour from recursively calling itself.*

`[ThreadStatic] private static bool _inHookDetour;`

ThreadStatic = separate value per OS thread. Required because D3D11 calls can be made from
multiple threads simultaneously in some engines. FFXIV is single-threaded for rendering, but
the guard is still required because our own inject draw call triggers re-entry into the same hooks.

When `_inHookDetour = true`, all hook detours immediately call Original and return.

---

### FFXIVClientStructs Camera / ViewProj
*The FFXIVClientStructs accessor for the game's current ViewProjection matrix.*

```csharp
using FFXIVClientStructs.FFXIV.Client.Game.Control;
Matrix4x4 viewProj = Control.Instance()->ViewProjectionMatrix;
```

Row-major 4×4 float matrix. Updates every frame with the current camera transform.
Must be read every frame in the render callback (not cached — camera moves between frames). ✓

`viewProjDiag` in /render shows the 4 diagonal elements for quick sanity check.
`viewProjFull` shows all 16 values. ✓

---

### FFXIVClientStructs Kernel.Device
*The FFXIVClientStructs accessor for the FFXIV D3D11 device.*

```csharp
unsafe {
    var kdev = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance();
    nint devicePtr = (nint)kdev->D3D11Forwarder;
    uint width = kdev->Width;
    uint height = kdev->Height;
}
```

`D3D11Forwarder` is a pointer to the COM object that implements ID3D11Device.
`Width`/`Height` are the current render resolution (not necessarily window size on resize).

---

### Hot-Reload (Dalamud Dev Plugin)
*Dalamud automatically reloads a dev plugin when its DLL changes on disk.*

When a dev plugin DLL is rebuilt, Dalamud detects the file change and:
1. Disposes the old plugin instance (calls `IDisposable.Dispose()`)
2. Loads the new DLL
3. Creates a new plugin instance

No `/xlplugins` manual toggle needed. ✓ (All hook state is properly disposed in `DisposeResources`.)

**Settings reset on reload:** `Configuration.Load()` reads from JSON on each new instance creation.
Any in-memory configuration changes (CF path toggles via API) are lost on hot-reload unless saved.

---

## Supporting Libraries & Reference Plugins

### Vortice.Direct3D11
*C# bindings for Direct3D 11 used in FFXIV-TV. The Dalamud-compatible D3D11 library.*

NuGet: `Vortice.Direct3D11` (part of the Vortice.Windows family).
Replaces the deprecated SharpDX which Dalamud dropped in v10.

Key namespaces: `Vortice.Direct3D11`, `Vortice.DXGI`, `Vortice.D3DCompiler`, `Vortice.Mathematics`.

**Construction from raw pointer (no AddRef):**
```csharp
var device = new ID3D11Device(devicePtr);
device.AddRef();  // Vortice does NOT AddRef on construction from nint — must AddRef manually
```

**Release pattern:** Vortice objects are IDisposable — wrap in `using` or call `.Dispose()`.
Forgetting to Dispose leaks COM references → device never released → memory leak.

Source: [Vortice.Windows GitHub](https://github.com/amerkoleci/Vortice.Windows)

---

### TerraFX.Interop.Windows
*Alternative low-level Windows interop library — what Dalamud itself uses internally.*

Provides raw unsafe structs and function pointer tables for all Win32/COM APIs.
More verbose than Vortice but zero abstraction overhead.
FFXIV-TV uses Vortice (higher-level); TerraFX is available if needed for access to APIs
Vortice doesn't wrap.

---

### xivr-Ex
*FFXIV VR plugin. Primary reference for D3D11 hook patterns in a real Dalamud plugin.*

Hooks the D3D11 device context to intercept the render loop and inject VR eye renders.
Uses IGameInteropProvider for vtable hooks — same pattern as FFXIV-TV.
Source: [xivr-Ex GitHub](https://github.com/ProjectMimer/xivr-Ex)

Relevant patterns: OMSetRenderTargets hook, device/context acquisition from FFXIVClientStructs,
render callback registration. Confirmed working as of Dalamud v10 / API Level 14.

---

### GShade / ReShade
*D3D11 post-processing injectors. Hook IDXGISwapChain::Present to inject shader effects.*

ReShade hooks vtable[8] of IDXGISwapChain (Present) via a proxy DLL or direct hook.
GShade is a fork targeting FFXIV. Both run AFTER all game rendering and Dalamud.

**Not suitable for FFXIV-TV's use case** — Present hook is Stage 5, after HUD draws.
Our rect would be drawn over the HUD (wrong ordering). We need Stage 2 injection.

Useful reference for D3D11 hook techniques and DXGI interception.

Source: [ReShade GitHub](https://github.com/crosire/reshade)

---

### SpecialK
*Advanced D3D11/12 interposer for latency reduction, HDR, and frame generation.*

Hooks at multiple levels: Present, Draw*, Dispatch, OMSetRenderTargets.
Has deep knowledge of game rendering pipelines. Source available for reference.

SpecialK's per-draw-call hooking approach is architecturally similar to FFXIV-TV's CF paths.
Their detection of "UI pass" vs "scene pass" for HDR injection is directly analogous to our
`_inUiPass` guard.

Source: [SpecialK GitHub](https://github.com/SpecialKO/SpecialK)

---

### Pictomancy
*Dalamud library wrapping WorldToScreen with camera plane culling.*

Prevents the behind-camera "wrap-around" artifact where a world point behind the camera projects
to valid but wrong screen coordinates. Handles all edge cases for world-space overlay drawing.

FFXIV-TV uses raw `IGameGui.WorldToScreen` for Phase 1 (simple, known limitation). Consider
replacing with Pictomancy for Phase 1 edge case correctness.

Source: [Pictomancy GitHub](https://github.com/sourpuh/ffxiv_pictomancy)

---

### Browsingway
*Dalamud plugin rendering a CEF browser frame as an in-game overlay. Phase 3 reference.*

Pattern: CEF browser renders to an off-screen texture → shared D3D11 texture handle →
FFXIV-TV receives the texture and draws it as the screen content.

Key technique: D3D11 shared texture (created with `MiscFlags.Shared` or `KeyedMutex`).
The browser process creates the texture and passes the HANDLE. FFXIV-TV opens it with
`device.OpenSharedResource<ID3D11Texture2D>(handle)`.

Source: [Browsingway GitHub](https://github.com/Styr1x/Browsingway)

---

## Video & Browser Rendering Patterns

### LibVLCSharp
*C# bindings for libVLC — the VideoLAN media framework. Phase 3 video decode candidate.*

Can decode virtually any video format. Outputs frames as raw pixel buffers.
Pattern: `LibVLC.Media.Play()` → frame callback fires with decoded pixel data →
lock staging texture, memcpy frame → unlock → CopyResource to GPU.

Thread safety: frame callbacks fire on VLC's internal thread. The staging texture write must be
marshalled to the render thread. Use a ring buffer or double-buffer pattern.

Source: [LibVLCSharp GitHub](https://github.com/videolan/libvlcsharp)

---

### Windows Media Foundation (WMF)
*Windows-native media pipeline. Alternative to LibVLCSharp for video decode.*

Supports hardware-accelerated H.264/H.265 decode directly to a D3D11 texture (no CPU copy).
Use `MFCreateDXGISurfaceBuffer` to get a DXGI surface from a decoded frame, then wrap as SRV.

Tighter Windows integration than libVLC. Supports protected content (DRM). More complex API.
The hardware decode path (`MFT_MESSAGE_SET_D3D_MANAGER`) eliminates the CPU round-trip entirely.

---

### Staging Texture CPU Upload
*(See also [Staging Texture / CPU Upload Pattern](#staging-texture--cpu-upload-pattern) in Resource View Types)*

Full lifecycle for a video frame upload:

```csharp
// Create once:
var stagingDesc = new Texture2DDescription {
    Width = w, Height = h, Format = Format.B8G8R8A8_UNorm,
    ArraySize = 1, MipLevels = 1, SampleDescription = new(1,0),
    Usage = ResourceUsage.Staging, CpuAccessFlags = CpuAccessFlags.Write,
    BindFlags = BindFlags.None
};
var stagingTex = device.CreateTexture2D(stagingDesc);

var gpuDesc = stagingDesc with { Usage = ResourceUsage.Default,
    CpuAccessFlags = CpuAccessFlags.None, BindFlags = BindFlags.ShaderResource };
var gpuTex = device.CreateTexture2D(gpuDesc);
var srv = device.CreateShaderResourceView(gpuTex);

// Every frame:
var mapped = context.Map(stagingTex, 0, MapMode.WriteDiscard, MapFlags.None);
// Copy row-by-row using mapped.RowPitch:
for (int y = 0; y < h; y++) {
    Buffer.MemoryCopy(srcRow, (void*)(mapped.DataPointer + y * mapped.RowPitch), mapped.RowPitch, srcStride);
}
context.Unmap(stagingTex, 0);
context.CopyResource(gpuTex, stagingTex);
// Now srv contains the new frame — bind to PS slot 0.
```

---

### CEF Shared Texture (D3D11 Shared Resource)
*Cross-process D3D11 texture sharing via HANDLE — used in Browsingway and Phase 3 browser mode.*

A D3D11 texture created with `MiscFlags.Shared` gets a sharable HANDLE:
```csharp
using var dxgiResource = texture.QueryInterface<IDXGIResource>();
IntPtr handle = dxgiResource.SharedHandle;
```

Another process (or another device) opens it:
```csharp
var sharedTex = device.OpenSharedResource<ID3D11Texture2D>(handle);
var srv = device.CreateShaderResourceView(sharedTex);
```

For cross-process sync, use `MiscFlags.SharedKeyedMutex` — the producer acquires mutex before
writing, releases it; the consumer acquires before reading. Prevents tearing.

FFXIV-TV Phase 3 browser mode: browser process renders to shared texture, FFXIV plugin reads it.

---

## ID3D11DeviceContext — Complete Vtable Quick Reference

*Confirmed across: xosh.org vtable reference, Go dx11 package struct with explicit index comments,
3Dmigoto HookedContext.cpp, ReShade d3d11_device_context.cpp, Microsoft SDK docs.*

```
IUnknown:
  [0]  QueryInterface
  [1]  AddRef
  [2]  Release
ID3D11DeviceChild:
  [3]  GetDevice
  [4]  GetPrivateData
  [5]  SetPrivateData
  [6]  SetPrivateDataInterface
ID3D11DeviceContext:
  [7]  VSSetConstantBuffers
  [8]  PSSetShaderResources
  [9]  PSSetShader
  [10] PSSetSamplers
  [11] VSSetShader
  [12] DrawIndexed                        ← FFXIV-TV CF-DI hook
  [13] Draw                               ← FFXIV-TV CF-Draw hook
  [14] Map
  [15] Unmap
  [16] PSSetConstantBuffers
  [17] IASetInputLayout
  [18] IASetVertexBuffers
  [19] IASetIndexBuffer
  [20] DrawIndexedInstanced
  [21] DrawInstanced
  [22] GSSetConstantBuffers
  [23] GSSetShader
  [24] IASetPrimitiveTopology
  [25] VSSetShaderResources
  [26] VSSetSamplers
  [27] Begin
  [28] End
  [29] GetData
  [30] SetPredication
  [31] GSSetShaderResources
  [32] GSSetSamplers
  [33] OMSetRenderTargets                 ← FFXIV-TV stage transition oracle
  [34] OMSetRenderTargetsAndUnorderedAccessViews
  [35] OMSetBlendState
  [36] OMSetDepthStencilState
  [37] SOSetTargets
  [38] DrawAuto
  [39] DrawIndexedInstancedIndirect
  [40] DrawInstancedIndirect
  [41] Dispatch                           ← FFXIV-TV CF-Dispatch hook
  [42] DispatchIndirect                   ← FFXIV-TV CF-DispatchIndirect (never fires in FFXIV ✓)
  [43] RSSetState
  [44] RSSetViewports
  [45] RSSetScissorRects
  [46] CopySubresourceRegion
  [47] CopyResource                       ← stage boundary signal
  [48] UpdateSubresource
  [49] CopyStructureCount
  [50] ClearRenderTargetView
  [51] ClearUnorderedAccessViewUint
  [52] ClearUnorderedAccessViewFloat
  [53] ClearDepthStencilView
  [54] GenerateMips
  [55] SetResourceMinLOD
  [56] GetResourceMinLOD
  [57] ResolveSubresource
  [58] ExecuteCommandList
  [59–66] HS/DS Set* (tessellation)
  [67–71] CS Set* (compute)
  [72–114] Get* methods + ClearState / Flush / GetType / GetContextFlags / FinishCommandList
```

**Key vtable indices at a glance:**
| Method | Index | FFXIV-TV use |
|---|---|---|
| DrawIndexed | 12 | CF-DI primary inject ✓ |
| Draw | 13 | CF-Draw fallback inject ✓ |
| DrawIndexedInstanced | 20 | Not hooked (3% of frames) |
| DrawInstanced | 21 | Not hooked (rare) |
| DrawIndexedInstancedIndirect | 39 | Not hooked |
| DrawInstancedIndirect | 40 | Not hooked |
| OMSetRenderTargets | 33 | Stage transition oracle ✓ |
| Dispatch | 41 | CF-Dispatch hook ✓ |
| DispatchIndirect | 42 | Hooked, never fires in FFXIV ✓ |
| CopyResource | 47 | Potential CF-CopyResource path |

Sources: [xosh.org vtable reference](https://xosh.org/id3d10device-vtable/) | [Go dx11 package](https://pkg.go.dev/github.com/TKMAX777/winapi/dx11) | [3Dmigoto HookedContext.cpp](https://github.com/bo3b/3Dmigoto/blob/master/DirectX11/HookedContext.cpp) | [ReShade d3d11_device_context.cpp](https://github.com/crosire/reshade/blob/main/source/d3d11/d3d11_device_context.cpp)

---

## Re-entrancy and Thread Safety in D3D11 Hooks

### COM Thread Safety Rules
- **`ID3D11Device` methods** — fully free-threaded. Multiple threads can call `Create*` simultaneously.
- **`ID3D11DeviceChild`-derived objects** (textures, buffers, shaders) — free-threaded for refcounting.
- **`ID3D11DeviceContext` methods** — **NOT free-threaded**. Only one thread may call any
  Draw/Copy/Map/Set* method at a time. FFXIV uses exclusively the immediate context, single-threaded. ✓

### Why `pCtx == _contextPtr` is Critical
Hooking a vtable entry fires the detour for **every** `ID3D11DeviceContext` that shares that vtable.
If any third-party tool creates its own deferred context, your hook fires for that context too.
Without the pointer guard:
- You'd process unrelated deferred command list recording as FFXIV's main frame
- Your injected draws would go into a deferred command list, not the live frame
- Double-injection if another hook layer has its own context

FFXIV uses exclusively the immediate context — `GetType() == D3D11_DEVICE_CONTEXT_IMMEDIATE`.
The pointer compare is both sufficient and cheaper than calling `GetType()` per draw call.

### Re-entrancy Patterns

When you make D3D11 calls inside a hooked D3D11 call (e.g., inject a Draw from inside a Draw detour),
your hook fires again recursively. Three production patterns:

1. **Thread-local bool** (FFXIV-TV's approach):
   ```csharp
   [ThreadStatic] private static bool _inHookDetour;
   if (_inHookDetour) { _hook!.Original(...); return; }
   _inHookDetour = true;
   try { /* inject */ _hook!.Original(...); }
   finally { _inHookDetour = false; }
   ```

2. **Context pointer guard** (ReShade, FFXIV-TV): `pCtx == _contextPtr` — also a re-entrancy guard
   since injected calls go to the same context pointer.

3. **Thread-local address stack** (Kyle Halladay / Skyrim pattern): stores trampoline return addresses
   on a per-thread stack; `PopAddress()` on re-entrant path.

Source: [D3D11 device threading](https://learn.microsoft.com/en-us/windows/win32/direct3d11/overviews-direct3d-11-devices-intro)

---

## Vortice.Direct3D11 — AddRef Gotcha

**Critical:** Vortice does NOT call `AddRef` when constructing an object from a raw `nint` pointer.

```csharp
// WRONG — Dispose() will call Release() → refcount drops to 0 → object freed prematurely:
var device = new ID3D11Device(devicePtr);

// CORRECT — AddRef manually so Dispose can Release cleanly:
var device = new ID3D11Device(devicePtr);
device.AddRef();
```

This applies any time you get a raw COM pointer from a hook, from FFXIVClientStructs, or from
any non-Vortice API. If the pointer came from a Vortice method (e.g., `CreateTexture2D`), that
method already does the AddRef — don't double-AddRef.

`Marshal.GetObjectForIUnknown(ptr)` does AddRef automatically — but that returns a `object`,
not a typed Vortice wrapper. Use `new ID3D11X(ptr) + AddRef()` for typed access.

Source: [Vortice.Windows GitHub](https://github.com/amerkoleci/Vortice.Windows)

---
*End of Research-Pipeline-Glossary.md — keep this document current.*
