# FFXIV D3D11 Rendering Research
*Written 2026-03-29 — covers rendering images, writing to the depth buffer, rendering world-space
objects, spawning geometry, and drawing shapes inside Dalamud FFXIV plugins.*

---

## 1. FFXIV Rendering Pipeline — Confirmed Stage Map

FFXIV's per-frame D3D11 pipeline, as confirmed by FFXIV-TV diagnostics and cross-referenced with
reference plugin analyses and xivr-Ex source:

```
Stage 1 — 3D Scene Pass (DSV-bound draws)
  OMSetRenderTargets(RTV=R16G16B16A16_Float [MainSceneRTV], DSV=MainSceneDSV)
  DrawIndexed / Draw / DrawIndexedInstanced × many      ← 3D geometry, shadows, characters
  OMSetRenderTargets(RTV=R16, DSV=shadowDSV) × several  ← shadow passes (numViews=0 — skip)
  ...

Stage 2 — Post-Processing / Bloom / Tonemap (first no-DSV call triggers _inUiPass)
  OMSetRenderTargets(RTV=R16,     DSV=null)              ← bloom accumulation passes
  OMSetRenderTargets(RTV=BGRA8,   DSV=null)              ← tonemap writes LDR intermediate
  DrawIndexed / Draw / Dispatch / CopyResource           ← tonemap mechanism varies per frame

Stage 3 — HUD / UI Pass
  [FFXIV HUD draws fire on the BGRA8 LDR intermediate — chat, hotbar, minimap]
  OMSetRenderTargets(RTV=BackBuffer, DSV=null)           ← single BB bind; composite LDR→BB
  [remaining HUD may continue on BB]

Stage 4 — Dalamud ImGui
  IDXGISwapChain::Present hook fires
    ImGui_ImplDX11_RenderDrawData → OMSetRenderTargets(BB) ← Dalamud's own bind

Stage 5 — Present
```

**Key facts:**
- FFXIV binds the swapchain backbuffer **exactly once** per frame.
- All 2D HUD draws follow on the same bound surface — there is no rebind.
- The tonemap fill varies every frame: ~40% DrawIndexed, ~20% Draw, ~37% Dispatch, ~3% CopyResource.
- Shadow passes use `numViews=0` — must be excluded from DSV tracking and transition detection.

---

## 2. How to Render Images in FFXIV

### Phase 1: ImGui WorldToScreen (simplest, no depth)

```csharp
// Plugin.cs — in IUiBuilder.Draw handler
var gameGui = Service.Get<IGameGui>();
var worldPos = new Vector3(x, y, z);
if (gameGui.WorldToScreen(worldPos, out var screenPos)) {
    var drawList = ImGui.GetBackgroundDrawList();
    drawList.AddImage(textureWrap.Handle, screenPos - halfSize, screenPos + halfSize);
}
```

**Limitation:** No depth testing. Objects always render on top of characters. Use only as a fallback.

For camera clipping, use the [Pictomancy](https://github.com/sourpuh/ffxiv_pictomancy) library:
```csharp
PictoService.Initialize(pluginInterface);
// Then in Draw:
PictoService.DrawList.AddCircleFilled(worldPos, radius, color);
```

### Phase 2: D3D11 Injection (correct depth, world-space)

World-space rendering with proper depth occlusion requires hooking the D3D11 device context via
`IGameInteropProvider`. See Section 5 for the complete setup.

**Confirmed working injection point** (v0.5.132 CF-DI path):

1. Hook `OMSetRenderTargets` (vtable[33]) to detect the 3D→2D transition and learn the BGRA8 LDR surface.
2. Hook `DrawIndexed` (vtable[12]), `Draw` (vtable[13]), `Dispatch` (vtable[41]), `CopyResource` (vtable[47]).
3. When a draw call fires on the known LDR BGRA8 surface for the first time in a frame:
   - Call `Original()` first (tonemap blit runs, filling the LDR surface with the composited scene).
   - Then inject the rect with your custom shader.
4. FFXIV's HUD draws follow on the backbuffer → HUD naturally appears in front. ✓

---

## 3. Getting the D3D11 Device and Context

```csharp
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using Vortice.Direct3D11;

unsafe {
    var kdev = Device.Instance();
    nint devicePtr = (nint)kdev->D3D11Forwarder;  // ID3D11Device vtbl at offset 0xE0AA8

    var device = new ID3D11Device(devicePtr);
    device.AddRef();                               // Vortice doesn't AddRef on construction

    var context = device.ImmediateContext;
    nint contextPtr = context.NativePointer;
    nint* vtable = *(nint**)contextPtr;
}
```

**Render resolution** (needed for fullscreen viewport and surface dimension checks):
```csharp
uint w = kdev->Width;
uint h = kdev->Height;
```

**ViewProjection matrix** (update every frame, not just once):
```csharp
using FFXIVClientStructs.FFXIV.Client.Game.Control;
var viewProj = Control.Instance()->ViewProjectionMatrix;  // Matrix4x4, row-major for HLSL
```

---

## 4. Hooking D3D11 DeviceContext Vtable

### Vtable Indices (ID3D11DeviceContext — confirmed working in FFXIV-TV)

| Index | Method | Used For |
|-------|--------|----------|
| 12 | `DrawIndexed` | CF-DI inject, primary path |
| 13 | `Draw` | CF-Draw inject, fallback |
| 20 | `DrawIndexedInstanced` | Instanced tonemap blits |
| 21 | `DrawInstanced` | Instanced tonemap blits |
| 33 | `OMSetRenderTargets` | DSV tracking, BB identification, transition detection |
| 39 | `DrawIndexedInstancedIndirect` | Indirect variants |
| 40 | `DrawInstancedIndirect` | Indirect variants |
| 41 | `Dispatch` | Compute tonemap path (~37% of frames) |
| 47 | `CopyResource` | CopyResource tonemap path |
| 50 | `ClearRenderTargetView` | Track HUD RT clear sequence |

### Hook Installation Pattern

```csharp
nint* vtable = *(nint**)contextPtr;

// IGameInteropProvider.HookFromAddress<TDelegate>(address, detour)
_omSetRTHook = gameInterop.HookFromAddress<OMSetRenderTargetsDelegate>(
    vtable[33], OMSetRenderTargetsDetour);
_drawIndexedHook = gameInterop.HookFromAddress<DrawIndexedDelegate>(
    vtable[12], DrawIndexedDetour);
// ... etc

_omSetRTHook.Enable();
_drawIndexedHook.Enable();
```

### Re-entrancy Guard (MANDATORY)

Any D3D11 call inside a detour (e.g. `OMSetRenderTargets` inside `ExecuteInlineDraw`) re-enters
all hooked methods. A thread-static guard is required:

```csharp
[ThreadStatic] private static bool _inHookDetour;

private void DrawIndexedDetour(nint ctx, int indexCount, int startIndex, int baseVertex)
{
    if (_inHookDetour) { _drawIndexedHook.Original(ctx, indexCount, startIndex, baseVertex); return; }
    _inHookDetour = true;
    try {
        // your injection logic here
        _drawIndexedHook.Original(ctx, indexCount, startIndex, baseVertex);
    } catch (Exception ex) {
        PluginLog.Error(ex, "DrawIndexedDetour exception");
    } finally {
        _inHookDetour = false;
    }
}
```

**Original must always be called in `finally`.** A detour that exits without calling Original hangs or crashes the game.

---

## 5. Writing to the Depth Buffer (Reverse-Z)

FFXIV uses **reverse-Z** throughout the scene pass. Near plane = z=1.0, far plane = z=0.0 in NDC.
"Closer objects have higher Z." The standard D3D11 `LESS` comparison will fail — you must use
`GREATER` or `GREATER_EQUAL`.

### Depth State Configurations

```csharp
// Scene inject — write depth (FFXIV geometry renders after us and must depth-test against rect)
_dsReverseZWrite = device.CreateDepthStencilState(new DepthStencilDescription {
    DepthEnable    = true,
    DepthWriteMask = DepthWriteMask.All,
    DepthFunc      = ComparisonFunction.GreaterEqual,   // GreaterEqual for scene inject
    StencilEnable  = false,
});

// Post-tonemap BB inject — test only, don't write (nothing renders after us into same surface)
_dsReverseZ = device.CreateDepthStencilState(new DepthStencilDescription {
    DepthEnable    = true,
    DepthWriteMask = DepthWriteMask.Zero,
    DepthFunc      = ComparisonFunction.Greater,        // strict Greater fine for BB inject
    StencilEnable  = false,
});

// No depth — for LDR/HUD surfaces without a DSV
_dsNoDepth = device.CreateDepthStencilState(new DepthStencilDescription {
    DepthEnable   = false,
    StencilEnable = false,
});
```

### Critical: DSV/RTV Dimension Mismatch

**D3D11 silently renders nothing if the DSV and RTV have different pixel dimensions.** No error,
no exception — the draw call simply does nothing. Shadow passes bind DSVs at sub-resolutions.
Always verify dimensions before binding:

```csharp
bool CheckDepthCompatibility(ID3D11RenderTargetView rtv) {
    using var res = rtv.Resource;
    var tex = new ID3D11Texture2D(res.NativePointer);
    tex.AddRef();
    var desc = tex.Description;
    tex.Release();
    var dsvDesc = _trackedDsv.Description;
    return (desc.Width == dsvDesc.Width && desc.Height == dsvDesc.Height);
}
```

### Latching the Scene DSV

The main scene DSV must be captured during the 3D pass and held frozen during post-processing.
Post-processing may bind shadow-map DSVs at smaller resolutions — these must not overwrite the
latched value:

```csharp
// In OMSetRenderTargetsDetour — ONLY update _trackedDsv while still in 3D pass
if (hasDsv && !_inUiPass && numViews > 0 && ppRTVs[0] != 0) {
    _trackedDsv?.Dispose();
    _trackedDsv = new ID3D11DepthStencilView(pDSV);
    _trackedDsv.AddRef();
}
```

**Never update `_trackedDsv` during `_inUiPass`.**

---

## 6. Rendering World-Space Objects (Geometry)

### Method A: SV_VertexID (no vertex buffer, recommended)

FFXIV's D3D context has a known issue where interpolated `TEXCOORD` semantics from VS are
silently zeroed by the rasterizer in some pipeline states. The confirmed workaround is to read
UVs from a static array in the VS indexed by `SV_VertexID` — this bypasses the rasterizer
interpolation path that zeroes the values.

**Vertex Shader (flat world-space quad, 6 vertices, TriangleList):**
```hlsl
cbuffer CbParams : register(b0) {
    row_major float4x4 ViewProj;
    row_major float4x4 ScreenTransform;   // world-space TRS for the screen
    float Brightness; float Gamma; float Contrast; float BloomCap;
    float4 Tint;
};

static const float3 kPos[6] = {
    float3(-0.5f,  0.5f, 0.0f),  // TL
    float3( 0.5f,  0.5f, 0.0f),  // TR
    float3(-0.5f, -0.5f, 0.0f),  // BL
    float3( 0.5f,  0.5f, 0.0f),  // TR (repeated for second triangle)
    float3( 0.5f, -0.5f, 0.0f),  // BR
    float3(-0.5f, -0.5f, 0.0f),  // BL (repeated)
};
static const float2 kUV[6] = {
    float2(0,0), float2(1,0), float2(0,1),
    float2(1,0), float2(1,1), float2(0,1),
};

struct VSOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

VSOut main(uint id : SV_VertexID) {
    float4 world = mul(float4(kPos[id], 1.0f), ScreenTransform);
    VSOut o;
    o.pos = mul(world, ViewProj);
    o.uv  = kUV[id];   // static array — not interpolated by rasterizer, works in FFXIV
    return o;
}
```

Draw call: `context.Draw(6, 0)` — no vertex buffer, no input layout required.

### Method B: Box Geometry (36 vertices, 6-face box)

```hlsl
// 24 unique positions (4 per face), 36 vertex indices (6 tris × 6 verts per face pair)
// VS reads kPositions[kIndices[id]] for position and kFaceUV[kFaceId[id]] for UV
// PS dispatches: face 0 = front (full texture), faces 1-5 = sides/back (solid tint)
```

Draw call: `context.Draw(36, 0)`. ScreenTransform encodes full TRS (position, quaternion rotation,
width/height/depth scale). Best for a box with visible sides.

### ScreenTransform Matrix

Build from world-space screen definition:

```csharp
static Matrix4x4 BuildScreenTransform(Vector3 center, float yaw, float width, float height) {
    // Rotation: yaw around Y axis (FFXIV Y-up world)
    var rot = Matrix4x4.CreateRotationY(yaw);
    // Scale: width × height, no Z scale for flat quad
    var scale = Matrix4x4.CreateScale(width, height, 1.0f);
    // Translation
    var trans = Matrix4x4.CreateTranslation(center);
    // Column-major TRS: scale → rotate → translate
    return scale * rot * trans;
}
```

### Fullscreen Triangle (no world-space, for post-processing inject)

```hlsl
// Rory Driscoll pattern — 3 vertices, single large triangle that covers the viewport
struct Output { float4 pos : SV_POSITION; float2 uv : TEXCOORD; };
Output main(uint id : SV_VertexID) {
    Output o;
    o.uv  = float2((id << 1) & 2, id & 2);
    o.pos = float4(o.uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}
// Draw(3, 0) — no VB, no input layout
```

---

## 7. Pixel Shaders for FFXIV Surfaces

### HDR Scene Inject (R16G16B16A16_Float target)

Fires PRE-BLOOM. Write sRGB values directly — FFXIV's ACES tonemapper handles them correctly.
**Do NOT add a `pow(x, 2.2)` linearization step** — it makes the image nearly black.
Apply a `BloomCap` to prevent FFXIV's bloom from glowing your content (default 0.35):

```hlsl
Texture2D tex : register(t0);
SamplerState samp : register(s0);
struct VSOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

float4 main(VSOut i) : SV_TARGET {
    float4 color = tex.Sample(samp, i.uv);
    color.rgb *= Brightness;
    color.rgb  = saturate((color.rgb - 0.5f) * Contrast + 0.5f);
    color.rgb  = pow(saturate(color.rgb), 1.0f / max(Gamma, 0.001f));
    color.rgb *= Tint.rgb;

    // BloomCap: clamp proportionally to prevent FFXIV bloom amplification
    float maxComp = max(color.r, max(color.g, color.b));
    if (BloomCap > 0.001f && maxComp > BloomCap)
        color.rgb *= BloomCap / maxComp;

    return float4(color.rgb, color.a * Tint.a);
}
```

### LDR Inject (B8G8R8A8_UNorm target)

Fires POST-BLOOM, POST-TONEMAP. Write sRGB values directly to the display-ready surface.
No bloom cap needed — fires after the bloom pass:

```hlsl
float4 main(VSOut i) : SV_TARGET {
    float4 color = tex.Sample(samp, i.uv);
    color.rgb *= Brightness;
    color.rgb  = saturate((color.rgb - 0.5f) * Contrast + 0.5f);
    color.rgb  = pow(saturate(color.rgb), 1.0f / max(Gamma, 0.001f));
    color.rgb *= Tint.rgb;
    return float4(color.rgb, color.a * Tint.a);
}
```

### Color Format Summary

| Inject target | Format | Write |
|---------------|--------|-------|
| R16G16B16A16_Float | HDR linear float | sRGB values directly, apply BloomCap |
| B8G8R8A8_UNorm | 8-bit display-ready | sRGB values directly, no BloomCap |

---

## 8. State Save and Restore

Every injection must save and restore pipeline state exactly. Vortice `Get*` calls increment the
COM refcount on returned objects — you own those references and must call `.Dispose()` after restore.

Minimum state to save for a world-space rect injection:

```csharp
struct SavedState {
    public PrimitiveTopology Topology;
    public ID3D11VertexShader? VS;       // +ref, must Dispose
    public ID3D11PixelShader? PS;        // +ref, must Dispose
    public ID3D11GeometryShader? GS;     // +ref, must Dispose — null it during inject
    public ID3D11HullShader? HS;         // +ref, must Dispose — null it during inject
    public ID3D11DomainShader? DS;       // +ref, must Dispose — null it during inject
    public ID3D11Buffer? VsCb0;          // +ref, must Dispose
    public ID3D11Buffer? PsCb0;          // +ref, must Dispose
    public ID3D11ShaderResourceView? PsSrv0; // +ref, must Dispose
    public ID3D11SamplerState? PsSamp0;  // +ref, must Dispose
    public ID3D11RasterizerState? Rs;    // +ref, must Dispose
    public Viewport[] Viewports;
    public ID3D11BlendState? BlendState; // +ref, must Dispose
    public Color4 BlendFactor;
    public uint SampleMask;
    public ID3D11DepthStencilState? DssState; // +ref, must Dispose
    public uint StencilRef;
    // NOTE: VB, IB, InputLayout are NOT needed when using SV_VertexID (no vertex buffer)
}
```

**Viewport is critical:** Post-processing passes leave the rasterizer viewport set to a sub-resolution
(e.g. 1024×1024 for a shadow or downsample pass). Always set a viewport matching your target
RTV's full dimensions before drawing, then restore the saved viewports after.

---

## 9. Swapchain Backbuffer Identification

FFXIV creates its own RTVs for swapchain buffers; Dalamud creates separate RTVs for the same
textures. Comparing RTV pointers directly fails — compare underlying texture pointers.

### Two-Step Learning Algorithm (confirmed working in v0.5.132)

**Step 1 — Learn backbuffer texture pointers (per frame, at PrepareHooks):**

Dalamud's ImGui backend issues the last `OMSetRenderTargets` of every frame. Capture that RTV
pointer (`_lastNoDsvRtvPtr`), then at the start of the next frame query its underlying texture:

```csharp
if (_lastNoDsvRtvPtr != 0) {
    using var v = new ID3D11View(_lastNoDsvRtvPtr);
    v.AddRef();
    using var res = v.Resource;
    _knownBackbufferTexturePtrs.Add(res.NativePointer);
}
```

DXGI rotates through 2–3 swapchain buffers. After ~3 frames the set is complete.

**Step 2 — Classify RTVs during `_inUiPass` (in OMSetRenderTargetsDetour):**

```csharp
if (_inUiPass && !_checkedRtvPtrs.Contains(rtvPtr)) {
    _checkedRtvPtrs.Add(rtvPtr);   // reset this set per-frame!
    using var cv = new ID3D11View(rtvPtr);
    cv.AddRef();
    using var res = cv.Resource;
    if (_knownBackbufferTexturePtrs.Contains(res.NativePointer))
        _knownBackbufferRtvPtrs.Add(rtvPtr);
}
```

**Critical:** `_checkedRtvPtrs` must be cleared each frame (`PrepareHooks`). If it persists, FFXIV's
3-buffer rotation means 2 of the 3 RTV pointers are checked in frame 1 as "not BB" (before BB is
known), added to `_checkedRtvPtrs`, and never re-evaluated. Only the first RTV seen ever gets
classified as BB → `_currentBbRtvPtr` is 0 for 2 out of every 3 frames.

### LDR Surface Identification

```csharp
bool IsLdrFullRes(nint rtvPtr) {
    using var view = new ID3D11View(rtvPtr);
    view.AddRef();
    using var res = view.Resource;
    using var tex = new ID3D11Texture2D(res.NativePointer);
    tex.AddRef();
    var d = tex.Description;
    bool fullRes = d.Width == _kdev->Width && d.Height == _kdev->Height;
    // CONFIRMED: use positive allowlist, NOT negative exclusion.
    // Negative exclusion (not R16/R32/R11) passes R8_UNorm shadow maps as "LDR" → wrong inject target.
    bool isLdr = d.Format == Format.B8G8R8A8_UNorm
              || d.Format == Format.B8G8R8A8_UNorm_SRgb
              || d.Format == Format.R8G8B8A8_UNorm
              || d.Format == Format.R8G8B8A8_UNorm_SRgb;
    return fullRes && isLdr;
}
```

---

## 10. Drawing Shapes and Overlays

### ImGui Draw List (2D, no depth)

```csharp
var dl = ImGui.GetBackgroundDrawList();  // behind plugin windows, on top of game scene
dl.AddRectFilled(topLeft, bottomRight, 0xFF00FF00);  // solid rect, AABBGGRR
dl.AddCircle(center, radius, 0xFFFFFFFF, segments: 32, thickness: 2.0f);
dl.AddPolyline(points, numPoints, 0xFF0000FF, flags: 0, thickness: 1.5f);
dl.AddImageQuad(texHandle, p1, p2, p3, p4, uv1, uv2, uv3, uv4, 0xFFFFFFFF);
```

`ImGui.GetForegroundDrawList()` renders on top of plugin ImGui windows too.

Both fire during Dalamud's ImGui phase — **after all game rendering including HUD.** Anything drawn
here is always on top of FFXIV UI.

### Pictomancy (world-space draws with camera clipping)

```csharp
// NuGet: sourpuh.ffxiv.Pictomancy
PictoService.Initialize(pluginInterface);

// In Draw callback:
var dl = PictoService.DrawList;
dl.AddCircleFilled(worldPos, radius, 0x80FF0000u);  // world-space, clipped at camera plane
dl.AddLine(fromWorld, toWorld, 0xFFFFFFFF, thickness: 3.0f);
dl.AddText(worldPos, 0xFFFFFFFF, "label");
```

Handles behind-camera projection wrapping that raw `IGameGui.WorldToScreen` does not. Does not
provide D3D11 depth occlusion — characters still render in front of shapes drawn with Pictomancy.

### Raw D3D11 Textured Rect (world-space, depth-correct)

See Sections 5–8 for the full pipeline. Minimum to draw a rect in 3D space:

1. Load texture: `ITextureProvider.GetFromFile(new FileInfo(path))` → `ISharedImmediateTexture`
2. Per-frame: `sharedTex.GetWrapOrDefault()` → `IDalamudTextureWrap`
3. Get SRV from wrap: `new ID3D11ShaderResourceView(wrap.Handle)` (or from `Texture.D3D11ShaderResourceView`)
4. In inject: bind SRV, cbuffer, VS, PS, set topology `TriangleList`, `Draw(6, 0)`
5. Result: correctly depth-occluded by FFXIV characters and geometry ✓

---

## 11. Shader Compilation

### Option A: Pre-compile offline (recommended for production)

```bash
# fxc.exe from Windows SDK
fxc /T vs_5_0 /E main /Fh Shaders/VS.h /Vn VS_bytecode Shaders/VS.hlsl
fxc /T ps_5_0 /E main /Fh Shaders/PS.h /Vn PS_bytecode Shaders/PS.hlsl
# Embed byte arrays in C# as resources
```

Pre-compiled bytecode eliminates runtime compilation overhead and the 340ms render-thread hitch
from `D3DCompiler_47.dll` loading. Embed the `.cso` bytes as `byte[]` literals or embedded resources.

### Option B: Runtime compile via D3DCompiler_47.dll (dev/debug)

```csharp
// Compiler.Compile from Vortice.D3DCompiler
using Vortice.D3DCompiler;
var result = Compiler.Compile(hlslSource, "main", "myshader.hlsl", "ps_5_0");
if (result.HasErrors) throw new Exception(result.GetErrors());
using var blob = result.Bytecode;
_ps = device.CreatePixelShader(blob.AsSpan());
```

**Always do this on a background thread**, not on the render thread:
```csharp
_shaderCompileTask = Task.Run(() => CompileShaders());
// In TryInitialize: if (!_shaderCompileTask.IsCompleted) return false; // defer
```

---

## 12. BROKEN.md Issue Analysis

### Root Cause: BB Learning Race (v0.5.119 / v0.5.120)

**Why `ldrInjectCount` stayed at 0:**

The `_checkedRtvPtrs` set was never cleared per-frame. DXGI's 3-buffer rotation means:
- Frame 1: RTV A seen first, classified correctly as LDR by `IsLdrFullRes`.
- Frame 2: RTV B seen, goes through STEP B — but RTV A was already in `_checkedRtvPtrs` from frame 1,
  so it's skipped. `_knownBackbufferRtvPtrs` only contains one of the three RTV pointers.
- Frames 3+: `_currentBbRtvPtr == 0` on the frames that rotate to pointers B and C → no inject.

**Fix (v0.5.121, confirmed):**
- Clear `_checkedRtvPtrs` in `PrepareHooks` every frame.
- Re-add Draw BB inject (composite to BB is a `Draw` call, not `DrawIndexed`).

### Root Cause: `_targetInjectRtvPtr` not matched (CF inject never fires)

**Why `targetRtv` was set but no inject fired:**

The `targetRtvPtr` (LDR surface from last frame) was being compared against RTVs in the 10-call
diagnostic window. But the actual LDR tonemap Draw/DrawIndexed call fires later in the frame
(after the diagnostic window's 10-call cap). The CF pattern relies on learning the RTV from
the prior frame and matching it in the current frame — if the call fires after index 10, the
diagnostic doesn't see it, but the actual hook does.

This is a diagnostic artifact. The fix was simply ensuring `_checkedRtvPtrs` was properly reset
so the RTV was correctly identified as LDR, letting `_targetInjectRtvPtr` be set and matched.

### The One Remaining Problem (at time of BROKEN.md last update)

**HUD behind rect (v0.5.83 → v0.5.122 attempts):** Fully catalogued in BROKEN.md entries #1–22.
The confirmed working solution (v0.5.132, per memory) is **CF-DI into BGRA8 LDR intermediate**:
inject into the LDR surface after the tonemap DrawIndexed fires, before FFXIV's HUD draws follow.

---

## 13. Approaches Not Yet Tried / Experimental

### Alpha Channel Masking (FFXIV-specific)

FFXIV writes UI presence into the **alpha channel of the backbuffer**. After the BB bind, the
alpha channel encodes which pixels have UI over them (alpha=1 = UI pixel, alpha=0 = scene pixel).
This could theoretically be used to mask rect pixels where UI is present:

1. Copy BB to a staging texture (CPU readback, expensive).
2. In pixel shader: `if (bbAlpha > 0.5) discard;` — hide rect pixels covered by UI.

Problem: D3D11 cannot read and write the same resource in a single pass. You'd need a copy or
a structured buffer intermediate. Also, the BB alpha is only written after HUD draws on BB,
not on the LDR intermediate. Untried in FFXIV-TV. Probably not worth the complexity given
the LDR inject approach works cleanly.

### Independent Overlay Window (No D3D Hook)

For cases where depth is not needed (2D HUD-style overlays, not world-space):
- Create a transparent, click-through overlay `HWND` positioned over the game window.
- D3D11 device on the overlay window renders into its own swap chain.
- Benefits: zero game pipeline interaction, no hook required, zero crash risk.
- Limitation: no depth against FFXIV geometry, transparent areas handled by OS compositor.

Dalamud already does this for plugin windows via ImGui. Only relevant for a plugin that needs
to draw outside the game's D3D device entirely.

---

## 14. Reference: Confirmed-Working Plugin Architectures

### Browsingway (Styr1x/Browsingway)
- Uses `OnAcceleratedPaint()` CEF callback → D3D11 shared texture
- Draws via Dalamud ImGui (`IUiBuilder.Draw`) — no D3D hook
- 2D overlay only, no world-space depth

### xivr-Ex (ProjectMimer/xivr-Ex)
- Passes D3D11 texture pointers to native C++ DLL
- Native DLL writes directly to FFXIV render targets by pointer
- Uses `RenderTargetManager` texture array (offset +0x20, indices 107 / 10 for main + depth)
- Fragile: indices shift on game updates
- Hooks FFXIV skeleton render function for IK, controller input for VR remapping

### FFXIV-TV (this plugin, v0.5.132)
- Primary path: CF-DI — DrawIndexed hook on LDR BGRA8 intermediate
- Fallback paths: CF-Draw, CF-Dispatch, CF-Copy, OMSetRT-LDR, OMSetRT-BB
- Depth: CheckDepthCompatibility + _dsReverseZWrite for LDR inject
- Result: HUD in front ✓, video + audio ✓, no bloom ✓ (fires post-tonemap)

---

## 15. Targeted Research: Compute Tonemap, UAV Binding, Shader Hashes, and Related Topics

*Added 2026-03-30 — web-researched answers to 8 specific open questions.*

---

### Topic 1: FFXIV Compute-Tonemap UAV Binding — Definitive Answer

**0x85E777EF is a pixel shader (ps_5_0), not a compute shader. FFXIV does NOT use a compute UAV
path for its main tonemap.**

Confirmed from RenoDX's FFXIV game folder (`src/games/ffxiv/`): the file is named
`Tonemap_0x85E777EF.ps_5_0.hlsl` — the `ps_5_0` suffix is the profile, pixel shader stage 5.0.
RenoDX's `addon.cpp` registers it as a standard `CustomShaderEntry` alongside all other FFXIV
pixel shaders and intercepts it via ReShade's `bind_pipeline` event (pixel shader stage). The
decompiled HLSL reads from `sInputT` via `Sample()` and writes to `SV_TARGET0` — standard RTV
output path that requires `OMSetRenderTargets`, not `CSSetUnorderedAccessViews`.

The `ReshadeEffectShaderToggler.ini` for FFXIV does list one compute shader `0x4C699FFE` (Group0
"Motion vectors") — but that is a completely separate pass for motion vector data, unrelated to
tonemap or LDR surface binding.

**On BGRA8 as a UAV in D3D11:** `DXGI_FORMAT_B8G8R8A8_UNORM` UAV typed-store support is optional
at Feature Level 11.0 (requires `D3D11_FORMAT_SUPPORT2_UAV_TYPED_STORE` capability query). FFXIV
targets broad hardware compatibility and does not use BGRA8 as a compute UAV output. No evidence
of this pattern exists in any FFXIV rendering analysis.

**Conclusion: The ~44% CF-DI miss rate is NOT a compute path issue.** The tonemap always
uses `OMSetRenderTargets` + `DrawIndexed`/`Draw`. The CF-DI miss is a draw-variant classification
issue — on those frames the tonemap blit may use `Draw` instead of `DrawIndexed`, or `DrawInstanced`
(vtable[21]), meaning the CF-DI (DrawIndexed hook) never fires but CF-Draw could catch it. The
correct universal fix is OMSetRT-LDR, which fires on the OMSetRenderTargets call itself regardless
of which draw variant follows.

---

### Topic 2: Complete ID3D11DeviceContext Vtable Table

Verified from Windows SDK `d3d11.h`. The vtable inherits from `ID3D11DeviceChild` which inherits
from `IUnknown`. **Device context own methods start at index 7** (not 4 — ID3D11DeviceChild adds
4 slots: GetDevice[3], GetPrivateData[4], SetPrivateData[5], SetPrivateDataInterface[6]).

| Index | Method | Notes |
|-------|--------|-------|
| 0 | QueryInterface | IUnknown |
| 1 | AddRef | IUnknown |
| 2 | Release | IUnknown |
| 3 | GetDevice | ID3D11DeviceChild |
| 4 | GetPrivateData | ID3D11DeviceChild |
| 5 | SetPrivateData | ID3D11DeviceChild |
| 6 | SetPrivateDataInterface | ID3D11DeviceChild |
| 7 | VSSetConstantBuffers | ID3D11DeviceContext |
| 8 | PSSetShaderResources | |
| 9 | PSSetShader | |
| 10 | PSSetSamplers | |
| 11 | VSSetShader | |
| **12** | **DrawIndexed** | CF-DI hook |
| **13** | **Draw** | CF-Draw hook |
| 14 | Map | |
| 15 | Unmap | |
| 16 | PSSetConstantBuffers | |
| 17 | IASetInputLayout | |
| 18 | IASetVertexBuffers | |
| 19 | IASetIndexBuffer | |
| 20 | DrawIndexedInstanced | |
| 21 | DrawInstanced | |
| 22 | GSSetConstantBuffers | |
| 23 | GSSetShader | |
| 24 | IASetPrimitiveTopology | |
| 25 | VSSetShaderResources | |
| 26 | VSSetSamplers | |
| 27 | Begin | |
| 28 | End | |
| 29 | GetData | |
| 30 | SetPredication | |
| 31 | GSSetShaderResources | |
| 32 | GSSetSamplers | |
| **33** | **OMSetRenderTargets** | OMSetRT hook |
| 34 | OMSetRenderTargetsAndUnorderedAccessViews | |
| 35 | OMSetBlendState | |
| 36 | OMSetDepthStencilState | |
| 37 | SOSetTargets | |
| 38 | DrawAuto | |
| 39 | DrawIndexedInstancedIndirect | |
| 40 | DrawInstancedIndirect | |
| **41** | **Dispatch** | Compute dispatch |
| 42 | DispatchIndirect | |
| 43 | RSSetState | |
| 44 | RSSetViewports | |
| 45 | RSSetScissorRects | |
| 46 | CopySubresourceRegion | |
| **47** | **CopyResource** | |
| 48 | UpdateSubresource | |
| 49 | CopyStructureCount | |
| **50** | **ClearRenderTargetView** | |
| 51 | ClearUnorderedAccessViewUint | |
| 52 | ClearUnorderedAccessViewFloat | |
| 53 | ClearDepthStencilView | |
| 54 | GenerateMips | |
| 55 | SetResourceMinLOD | |
| 56 | GetResourceMinLOD | |
| 57 | ResolveSubresource | |
| 58 | ExecuteCommandList | |
| 59 | HSSetShaderResources | |
| 60 | HSSetShader | |
| 61 | HSSetSamplers | |
| 62 | HSSetConstantBuffers | |
| 63 | DSSetShaderResources | |
| 64 | DSSetShader | |
| 65 | DSSetSamplers | |
| 66 | DSSetConstantBuffers | |
| 67 | CSSetShaderResources | |
| **68** | **CSSetUnorderedAccessViews** | Compute UAV bind |
| **69** | **CSSetShader** | Compute shader bind |
| 70 | CSSetSamplers | |
| 71 | CSSetConstantBuffers | |
| 72 | VSGetConstantBuffers | (Get* methods follow) |

---

### Topic 3: Shader Hash Identification at Runtime (CSSetShader / PSSetShader)

**There is no D3D11 API to retrieve bytecode from a live `ID3D11ComputeShader` or
`ID3D11PixelShader`.** `GetPrivateData` only returns data the application explicitly stored —
FFXIV did not. `ID3D11ShaderReflection` works on blobs, not live COM objects.

**The confirmed correct approach** (used by both RenoDX and ReshadeEffectShaderToggler): hook
`ID3D11Device::CreateComputeShader` (and/or `CreatePixelShader`) at plugin load time, hash the
bytecode at creation, and store `shader_ptr → hash` in a dictionary. Look up the pointer in
`CSSetShader`/`PSSetShader` detours.

**ID3D11Device vtable indices** (also confirmed from SDK):

| Index | Method |
|-------|--------|
| 0–2 | IUnknown (QueryInterface, AddRef, Release) |
| 3 | CreateBuffer |
| 4 | CreateTexture1D |
| 5 | CreateTexture2D |
| 6 | CreateTexture3D |
| 7 | CreateShaderResourceView |
| 8 | CreateUnorderedAccessView |
| 9 | CreateRenderTargetView |
| 10 | CreateDepthStencilView |
| 11 | CreateInputLayout |
| 12 | CreateVertexShader |
| 13 | CreateGeometryShader |
| 14 | CreateGeometryShaderWithStreamOutput |
| **15** | **CreatePixelShader** |
| 16 | CreateHullShader |
| 17 | CreateDomainShader |
| **18** | **CreateComputeShader** |

**CRC32 hash (IEEE polynomial, 0xEDB88320)** of the raw bytecode bytes is the hash RenoDX
uses. Confirmed from `renodx::utils::hash::ComputeCRC32`. Note that the hash values in the
"Working" doc (e.g. `0x85E777EF`) are this format.

**Implementation pattern:**

```csharp
// At TryInitialize:
nint* devVtbl = *(nint**)_device.NativePointer;
_createPsHook  = gameInterop.HookFromAddress<CreatePixelShaderDelegate>(devVtbl[15],  CreatePsDetour);
_createCsHook  = gameInterop.HookFromAddress<CreateComputeShaderDelegate>(devVtbl[18], CreateCsDetour);
_createPsHook.Enable();
_createCsHook.Enable();

// In detour:
void CreatePsDetour(nint dev, byte* bytecode, nuint len, nint classLink, nint* ppShader) {
    _createPsHook.Original(dev, bytecode, len, classLink, ppShader);
    if (*ppShader != 0) {
        uint hash = Crc32(new ReadOnlySpan<byte>(bytecode, (int)len));
        _shaderHashes[*ppShader] = hash;
    }
}

// In PSSetShader detour:
void PSSetShaderDetour(nint ctx, nint pShader, nint* ppInstances, uint count) {
    if (_shaderHashes.TryGetValue(pShader, out uint hash) && hash == 0x85E777EF) {
        // This is the tonemap shader being bound — inject before draw fires
    }
    _psSetShaderHook.Original(ctx, pShader, ppInstances, count);
}
```

**Note:** PSSetShader / CSSetShader fire on the bind, not on the draw itself. You still need to
wait for the subsequent draw call to inject after the tonemap blit. Typical pattern: set a
`_tonemapShaderActive` flag in PSSetShader, act in the following DrawIndexed/Draw detour.

---

### Topic 4: OMSetRenderTargets Call Count — Draw vs Dispatch Frames

No public source documents a different OMSetRT sequence on the ~44% "miss" frames vs the ~56%
"hit" frames. Given that the tonemap is confirmed as a pixel shader (Topic 1), both frame types
bind the BGRA8 LDR surface via `OMSetRenderTargets` before any draw fires. The miss-frame
difference is in the draw variant used for the tonemap blit:

| Frame type | Tonemap blit | CF-DI fires? | CF-Draw fires? | OMSetRT-LDR fires? |
|------------|-------------|--------------|----------------|---------------------|
| "Hit" (~56%) | DrawIndexed (vtable[12]) | ✓ | ✓ (if DI missed) | ✓ |
| "Miss" (~44%) | Draw/DrawInstanced/other | ✗ | ✓ | ✓ |

**OMSetRT-LDR is invariant to draw variant — it fires on the OMSetRenderTargets call, not the
draw.** This is why it achieves ~99% coverage: it fires whenever FFXIV binds the LDR surface,
regardless of what draw variant follows.

The pipeline sequence is identical on both frame types:
```
... bloom passes (R16) ...
OMSetRenderTargets(BGRA8, null)    ← OMSetRT-LDR fires HERE (second LDR bind)
DrawIndexed/Draw/DrawInstanced     ← tonemap blit (variant varies)
[HUD draws]
OMSetRenderTargets(BackBuffer)
```

---

### Topic 5: IDXGISwapChain::Present Hook

**Present is vtable[8].** Full confirmed layout (from Windows SDK `dxgi.h`):

| Index | Interface | Method |
|-------|-----------|--------|
| 0–2 | IUnknown | QueryInterface, AddRef, Release |
| 3–6 | IDXGIObject | SetPrivateData, SetPrivateDataInterface, GetPrivateData, GetParent |
| 7 | IDXGIDeviceSubObject | GetDevice |
| **8** | IDXGISwapChain | **Present** |
| 9 | | GetBuffer |
| 10 | | SetFullscreenState |
| 11 | | GetFullscreenState |
| 12 | | GetDesc |
| 13 | | ResizeBuffers |
| 14 | | ResizeTarget |
| 15 | | GetContainingOutput |
| 16 | | GetFrameStatistics |
| 17 | | GetLastPresentCount |

**Dalamud's Present hook modes** (from `InterfaceManager.AsHook.cs`):
- **ReShade path (default):** Hooks `ReShadeDxgiSwapChain::on_present` — ReShade's wrapper. Dalamud's
  detour fires, calls `RenderDalamudDraw`, then calls Original (which is ReShade's on_present, which
  eventually calls the real `IDXGISwapChain::Present`).
- **VTable path (fallback):** Hooks `IDXGISwapChain::Present` at vtable[8] directly.

**Plugin stacking on Present:** Safe. Reloaded.Hooks uses trampoline chains — multiple hooks on the
same address form an ordered chain. Each hook's `Original` calls the next detour in the chain. This
pattern is observed in production plugins: hooking Present at vtable[8] for frame counting works
while Dalamud's hook is active, with no conflict. Always call Original in `finally`.

**Value of hooking Present for frame cleanup:** Yes — Present fires exactly once per frame, after all
rendering is complete. It's a reliable frame-end marker for resetting per-frame state (`_frameInjectionDone`,
`_sceneDrawnThisFrame`, `_inUiPass`, etc.). Currently FFXIV-TV resets these in `PrepareHooks()` which
fires from the game thread (Framework tick) — slightly earlier than Present. Using Present instead would
give a cleaner guarantee that reset fires after the last render of that frame, not before. Tradeoff: one
more hook; benefit: eliminates any race between PrepareHooks and the render thread.

---

### Topic 6: FFXIV ClearRenderTargetView on LDR

**FFXIV does NOT clear the BGRA8 LDR intermediate.** The confirmed pipeline is: FFXIV binds BGRA8
via `OMSetRenderTargets`, then immediately draws the tonemap blit into it, overwriting the previous
frame's contents. No `ClearRenderTargetView(BGRA8)` call fires.

ClearRTV calls that DO fire during `_inUiPass` are on R16G16B16A16_Float intermediate surfaces
(bloom accumulation passes, positions 6 and 13 in the logged sequence from v0.5.32) and on shadow
DSV surfaces. The DXGI backbuffer is also never cleared (confirmed v0.5.32).

**Using ClearRTV as a frame-start marker for LDR is not viable.** Use OMSetRenderTargets-based
detection instead.

The ClearRTV surfaces logged in v0.5.32 (positions 6 and 13 in the ClearRTV sequence during
`_inUiPass`) are intermediate HDR surfaces in the post-processing chain — not the LDR output.
These are the surfaces that caused visual artifacts when injected into (BROKEN.md approach #5:
"3D geo flickers").

---

### Topic 7: GShade/ReShade Injection Point for FFXIV

**Standard ReShade / GShade:** Both inject at `IDXGISwapChain::Present` only — after all game
rendering including HUD is complete. Effects applied via standard ReShade always sit on top of
the HUD. There is no built-in scene/UI separation.

**ReshadeEffectShaderToggler-FFXIV** (4lex4nder/ReshadeEffectShaderToggler-FFXIV): Adds
FFXIV-specific injection points via shader hash detection in ReShade's draw pipeline events.
The confirmed ini groups:

| Group | Name | Hashes | Purpose |
|-------|------|--------|---------|
| Group0 | Motion vectors | `0x4C699FFE` (CS) | Motion vector compute pass |
| Group2 | UI | `0xE66DAE4E, 0xF2F6BDE8, 0xB0D428D9, 0x6CA9BB69, 0xE00FBAFD` (PS) | Scene→HUD boundary |

**How Group2 "UI" works:** ReshadeEffectShaderToggler watches ReShade's `bind_pipeline` event. When
one of the 5 Group2 pixel shader hashes is bound and the draw is at swapchain resolution
(`MatchSwapchainResolutionOnly=2`), it fires the effect group injection. The 5 hashes are FFXIV's
HUD/UI draw shaders — the first one bound marks the exact scene→HUD transition. `InvocationLocation=0`
means "apply before this draw" — inject effects immediately before the first HUD draw.
`RequeueAfterRTMatchingFailure=True` handles cases where the RTV doesn't match on the first try.

**Applying this to FFXIV-TV:** The 5 UI shader hashes give us the exact PS hashes for FFXIV's
HUD draw calls. If we hook `PSSetShader` and detect any of these hashes, we know HUD is about to
start. This is another injection trigger option:

```
Detect PSSetShader(UI_SHADER_HASH) AND _inUiPass AND !_frameInjectionDone
  → this fires just before the first HUD draw
  → inject into _currentNoDsvRtvPtr (currently-bound BGRA8)
  → HUD draws follow → HUD in front ✓
```

This would be a more precise trigger than OMSetRT-LDR (which fires at LDR bind, before knowing
if tonemap has filled it yet). **Confirmed HUD pixel shader hashes for detection:**
`0xE66DAE4E`, `0xF2F6BDE8`, `0xB0D428D9`, `0x6CA9BB69`, `0xE00FBAFD`.

---

### Topic 8: RenoDX FFXIV Tonemap Analysis

RenoDX (`github.com/clshortfuse/renodx`, game folder `src/games/ffxiv/`) replaces FFXIV's
tonemap and LUT shaders with HDR-aware versions. All replacements are pixel shaders — no compute
shaders in the FFXIV game folder.

**Complete FFXIV shader inventory from RenoDX (all ps_5_0 unless noted):**

| Hash | File | Pass | Stage |
|------|------|------|-------|
| `0x85E777EF` | `Tonemap_0x85E777EF.ps_5_0.hlsl` | Main tonemap (LUT-based) | Post-bloom, writes to BGRA8 LDR |
| `0x27EBC404` | `LUT_0x27EBC404.ps_5_0.hlsl` | LUT application | Downstream of tonemap |
| `0x1F264D17` | `LUT_0x1F264D17.ps_5_0.hlsl` | LUT permutation (Dawntrail 7.2+) | Downstream of tonemap |
| `0xF8F57F0A` | `PostTonemapPreLUT_0xF8F57F0A.ps_5_0.hlsl` | Post-tonemap, pre-LUT | Between tonemap and LUT |
| `0xBF06786C` | `BloomPass1_0xBF06786C.ps_5_0.hlsl` | Bloom pass 1 PS | Pre-tonemap |
| `0xFE5B6B3E` | `BloomPass1_0xFE5B6B3E.vs_5_0.hlsl` | Bloom pass 1 VS | Pre-tonemap |
| `0x5E42F039` | `BloomPass2_0x5E42F039.ps_5_0.hlsl` | Bloom pass 2 PS | Pre-tonemap |
| `0x9B242D09` | `BloomPass2_0x9B242D09.vs_5_0.hlsl` | Bloom pass 2 VS | Pre-tonemap |
| `0xCDC56365` | `Vignette_0xCDC56365.ps_5_0.hlsl` | Vignette | Post-tonemap |
| `0x6CFFD968` | `Copy_0x6CFFD968.ps_5_0.hlsl` | Copy (copyTracker) | Various |
| `0xB0CE42B9` | `Copy_0xB0CE42B9.ps_5_0.hlsl` | Copy (copyTracker) | Various |
| `0xF6E81A1B` | `FullscreenGammaCorrection_0xF6E81A1B.ps_5_0.hlsl` | Gamma correction | Post-LUT |

**What 0x85E777EF actually does** (from decompiled HLSL):
1. Samples HDR linear input from `sInputT`
2. Multiplies by `cCommonTexParam.y` (exposure)
3. Squares values (~gamma 2.0 linearization)
4. Runs luminance-LUT-based colorize via `sToneMapT`
5. Takes square root (gamma re-encoding)
6. Outputs to `SV_TARGET0` (writes to BGRA8 LDR RTV)

**No `cs_5_0` files exist in the FFXIV RenoDX folder.** Zero compute shaders in the tonemap chain.

**RenoDX HDR swap chain upgrade:** `addon.cpp` includes `swap_chain_upgrade_targets` that attempt
to replace `b8g8r8a8_unorm` → `r16g16b16a16_float` for the LDR surface. In vanilla FFXIV (no
RenoDX installed), the surface remains BGRA8. This is why FFXIV-TV's `IsLdrFullRes()` check for
non-float formats is correct.

**RenoDX PSSetShader injection timing:** Their `bind_pipeline` callback fires on `PSSetShader`.
At that point the shader is bound but no draw has fired yet. They replace the shader bytecode
immediately, and FFXIV's next draw call uses the replacement. This confirms that PSSetShader
fires before the actual draw — there is a window between PSSetShader and DrawIndexed/Draw where
you can act.

---

### Summary: What Changed Based on This Research

1. **The ~44% miss is NOT compute.** Tonemap is ps_5_0 always. The miss is a draw-variant issue.
   OMSetRT-LDR (which fires on `OMSetRenderTargets`, not on a draw) is the correct 100% solution.

2. **PSSetShader (vtable[9]) hook + tonemap hash `0x85E777EF`** gives a precise "tonemap is about
   to fire" signal — can be used as an alternative or additional trigger.

3. **PSSetShader + HUD shader hashes** (`0xE66DAE4E` et al.) give a precise "HUD is about to start"
   signal — injection just before this fires catches the exact scene→HUD boundary.

4. **CreatePixelShader (device vtable[15])** is the hook point for building `shader_ptr → hash`
   maps. Hook at init, hash at creation, look up in PSSetShader detour.

5. **Present (IDXGISwapChain vtable[8])** is safe to hook alongside Dalamud. Use for frame cleanup
   as an alternative to Framework-tick-based PrepareHooks if a tighter boundary is needed.

6. **ClearRTV on LDR never fires.** Not a viable frame marker.

---

## Sources

- FFXIV-TV `D3DRenderer.cs` (v0.5.132) — primary confirmed-working implementation
- FFXIV-TV `Phase2-D3D-Rendering-Notes.md` — Phase 2 debug history
- FFXIV-TV `Working Game UI Drawn Over Rect.md` — current working state and confirmed pipeline
- [xivr-Ex (ProjectMimer/xivr-Ex)](https://github.com/ProjectMimer/xivr-Ex)
- [Browsingway (Styr1x/Browsingway)](https://github.com/Styr1x/Browsingway)
- [Pictomancy (sourpuh/ffxiv_pictomancy)](https://github.com/sourpuh/ffxiv_pictomancy)
- [FFXIVClientStructs — Device, SwapChain, Texture, ImmediateContext](https://github.com/aers/FFXIVClientStructs)
- [Dalamud IUiBuilder API](https://dalamud.dev/api/Dalamud.Interface/Interfaces/IUiBuilder/)
- [RenoDX FFXIV game folder (clshortfuse/renodx)](https://github.com/clshortfuse/renodx)
- [ReshadeEffectShaderToggler-FFXIV (4lex4nder)](https://github.com/4lex4nder/ReshadeEffectShaderToggler-FFXIV)
- [Windows SDK d3d11.h — ID3D11DeviceContext vtable](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nn-d3d11-id3d11devicecontext)
- [Windows SDK dxgi.h — IDXGISwapChain vtable](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nn-dxgi-idxgiswapchain)
- [3Dmigoto D3D11 injection reference (bo3b)](https://github.com/bo3b/3Dmigoto)
- [Fullscreen triangle pattern — Rory Driscoll](https://gist.github.com/rorydriscoll/1495603)
- [ImGui DX11 backend state save/restore](https://github.com/BalazsJako/ColorTextEditorDemo/blob/master/imgui_impl_dx11.cpp)
- [D3D11_DEPTH_STENCIL_DESC — Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/ns-d3d11-d3d11_depth_stencil_desc)
