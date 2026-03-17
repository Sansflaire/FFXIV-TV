# PyonPix — Full Technical Analysis
**Source:** `https://github.com/priprii/FFXIVPlugins/blob/main/bin/PyonPix.zip`
**Analyzed:** 2026-03-15 by decompiling PyonPix.dll with ilspycmd (11,757 lines)
**Version:** 1.0.0.0 — Dalamud API Level 14, net10.0-windows
**Dependencies:** SharpDX 4.2.0 (D3D11, DXGI, D3DCompiler, Mathematics), PyonPix.Browser.dll (native, unmanaged)

---

## EXECUTIVE SUMMARY

PyonPix is a massively more capable version of what FFXIV-TV is building. Their core architectural
insight is using an **embedded Chromium browser** instead of LibVLC. This solves every video
compatibility problem instantly — YouTube, Reddit, aniwatch, any web video plays natively because a
real browser handles it. They also inject real FFXIV scene lights driven by the average color on screen.

**What to adopt from them:**
1. Shader architecture — ScreenTransform matrix + procedural box geometry (replaces our 4-vertex buffer)
2. Border + edge feather system in HLSL
3. Real FFXIV scene light injection (the signatures work)
4. Average screen color → light color pipeline (GPU 16×16 downsample trick)
5. Render timing via OMSetRenderTargets + Present double-hook
6. Multiple screens ("Pix") per-config model

**What NOT to adopt:**
- SharpDX — deprecated. We use Vortice which is correct. Port the concepts, not the API calls.
- Their browser DLL is proprietary/closed-source — we can't use it directly.
- We should investigate WebView2 as our equivalent path.

---

## 1. ARCHITECTURE OVERVIEW

### Services
```
Plugin
├── PlayerService       — local player position/rotation cache (updated each framework tick)
├── PixService          — manages "Pix" objects (each Pix = one screen with ID, config, state)
├── DXService           — gets D3D11 device/context from DXGI swap chain; holds AdapterLuid
├── BrowserService      — manages Chromium browser via PyonPix.Browser.dll interop
├── ExtensionsService   — Chrome extension install/update/uninstall via Chrome Web Store API
├── DataService         — manages per-Pix User Data Folders (browser caches), size tracking
├── LightService        — injects real FFXIV SceneLight objects via game memory signatures
└── RendererService     — D3D11 draw pass; hooks OMSetRenderTargets + Present
```

### Windows
```
MainWindow      — list of all Pix, quick controls per Pix (toggle, navigate, mute)
BrowserWindow   — interactive browser window (address bar, back/fwd, tabs)
PixConfigWindow — per-Pix settings: transform, renderer, audio, light
ExtensionsWindow— Chrome extension manager
DataWindow      — browser cache viewer/manager
ConfigWindow    — global settings
```

### Commands
```
/pyonpix             — toggle main window
/pyonpix browser     — toggle browser window
/pyonpix extensions  — toggle extension manager
/pyonpix data        — toggle data manager
/pyonpix config      — toggle config window
/pyonpix {PIXID}     — toggle a specific Pix on/off
/pix                 — alias for /pyonpix
```

---

## 2. PIX CONCEPT (MULTIPLE SCREENS)

A "Pix" is a single screen instance. You can have **multiple Pix** simultaneously, each independent.

### Pix Properties
```
Info:
  - Id (string GUID-like)
  - Name (display name)
  - Type: Video | Audio | Image | Game | Light | Other

Browser:
  - Uri (current URL)
  - GpuAcceleration (bool)
  - PersistentCache (bool — keep UDF on despawn)
  - ScaleMode: BrowserWindow | GameWindow | GameWindowWhenHidden | CustomScale | CustomScaleWhenHidden
  - CustomScale (Vector2)

Renderer:
  - Position (Vector3)
  - Rotation (Quaternion)
  - Scale (Vector3) ← key insight: 3D scale, not just W/H
  - ScreenTint (Vector4 rgba)
  - BackColour (Vector4 — back face color; if alpha=0 mirrors front)
  - EdgeColour (Vector4 — box sides color)
  - BorderColour (Vector4)
  - BorderWidthH, BorderWidthV (float, 0-0.5 proportion of texture)
  - BorderMode: Padding (inset UV) | Overlay (blend over)
  - BorderFeather (float — soft border transition)
  - EdgeFeather (float — smooth silhouette anti-aliasing)
  - DepthOffset (float)
  - Depth (bool — depth testing on/off)
  - DepthComparison: LessEqual | GreaterEqual
  - CullMode: None | Front | Back

Audio:
  - SpatialEnabled (bool)
  - Volume (float 0-1)
  - FalloffMaxDistance (float)
  - FalloffStrength (float)

Light:
  - Enabled (bool)
  - Position (Vector3 — relative to screen center)
  - Rotation (Quaternion — relative to screen)
  - LightType: Directional | PointLight | SpotLight | AreaLight
  - Colour (Vector4 rgba)
  - Intensity (float)
  - ScreenColourInfluence (float 0-1 — how much screen avg color affects light color)
  - InfluenceColourIntensity (float)
  - InfluenceBrightnessIntensity (float)
  - InfluenceGammaCurve (float)
  - Range (float)
  - LightAngle (float)
  - FalloffType: Linear | Quadratic | Cubic
  - FalloffAngle (float)
  - FalloffPower (float)
  - Flags: Reflections | DynamicShadows | CharacterShadows | ObjectShadows
  - ShadowRange, ShadowNear, ShadowFar (float)
```

### Territory Spawn/Despawn
Pix can be configured per territory: auto-spawns when you enter a zone, auto-despawns when leaving.
- `SpawnBehaviour` flags: Navigate, Unmute
- `DespawnBehaviour` flags: Hide, Collapse, Mute, Shutdown

---

## 3. BROWSER SYSTEM (THE CORE)

### Architecture
PyonPix.Browser.dll is a **native (unmanaged) DLL** — not .NET managed code. It wraps an embedded
Chromium browser (likely WebView2 or CEF). Because it's unmanaged, ilspycmd cannot decompile it.

The managed C# code talks to it through `BrowserInterop` (a static P/Invoke class):

```csharp
// Key interop calls:
BrowserInterop.Initialize(configPath, processId, adapterLuid, debug)
BrowserInterop.CreateTab(tabId, gpuAccel, x, y, w, h, extensions, extensionCount)
BrowserInterop.Navigate(tabId, normalizedUri)
BrowserInterop.Resize(tabId, x, y, w, h)
BrowserInterop.Reposition(tabId, x, y)
BrowserInterop.SetFocusedTab(tabId, byUserInput)
BrowserInterop.UpdateSpatialAudio(tabId, left, right)
BrowserInterop.RegisterCallbacks(onLog, onHostReady, onHostFailed, onTabReady, onTabFailed,
    onTabDestroyed, onFrameReady, onCursorChanged, onNavigationStarting, onNavigationCompleted,
    onNavigationCanceled, onHistoryChanged, onTitleChanged, onFavIconChanged, onJsAlert,
    onJsConfirm, onExtensionOperation)
```

### Shared Texture Pipeline
The most important callback:
```csharp
OnFrameReady = delegate(string tabId, nint sharedHandle, uint w, uint h)
{
    // Browser rendered a frame into a D3D11 shared texture on the same GPU
    // Open it from our device and create an SRV for rendering:
    using Texture2D texture2D = _dx.D3D11Device.OpenSharedResource<Texture2D>(sharedHandle);
    ShaderResourceView srv = new ShaderResourceView(_dx.D3D11Device, texture2D, srvDesc);
    // Store SRV per tab; RendererService binds it to pixel shader each frame
};
```

**Key:** The browser DLL receives the DXGI AdapterLuid at init time, so it allocates the shared
texture on the SAME GPU. `OpenSharedResource<T>` is the standard cross-process D3D11 texture share.
This means zero CPU copy — browser → GPU texture → pixel shader, all on GPU.

### Navigation
```
pix://           — internal blank/home
file:///         — local files
about:           — browser about pages
extension://     — Chrome extension pages
https://         — external web
(bare domain)    — auto-prepended with https://
(unknown text)   — falls back to Google search
```

### Chrome Extensions
- Downloads .crx files from Chrome Web Store API (extension ID → download → install on next tab create)
- Stores in plugin config directory
- Auto-update check on browser start
- Per-tab extension enable/disable list

### GPU Acceleration
Each Pix has `GpuAcceleration` bool. When disabled, browser renders in software (CPU). Useful for
screens where GPU accel causes issues.

### Browser Scale Modes
| Mode | Behavior |
|------|----------|
| `BrowserWindow` | Browser renders at exactly the size of the plugin's BrowserWindow |
| `GameWindow` | Always renders at full game resolution (best for world screens) |
| `GameWindowWhenHidden` | Full game res when BrowserWindow is hidden, window size when visible |
| `CustomScale` | Fixed custom resolution |
| `CustomScaleWhenHidden` | Custom res when hidden, window size when visible |

For world-screen Pix, `GameWindow` is optimal — browser renders at game resolution regardless of
whether the control window is open.

---

## 4. SHADER SYSTEM (ADOPT THIS)

### Key Insight: Procedural Box Geometry — No Vertex Buffer
Instead of creating and updating a vertex buffer with 4 corners, PyonPix uses:
- **No vertex buffer, no input layout** (`ctx.InputAssembler.InputLayout = null`)
- `ctx.Draw(36, 0)` — 36 vertices for a box (6 faces × 2 triangles × 3 verts)
- The vertex shader generates all positions from `SV_VertexID`
- Box geometry encoded as local-space `float3` positions in the HLSL, multiplied by `ScreenTransform`

### cbuffer (Single, All in One)
```hlsl
cbuffer ShaderParams : register(b0) {
    row_major float4x4 CameraView;        // game camera view matrix
    row_major float4x4 CameraProjection;  // game camera projection matrix
    row_major float4x4 ScreenTransform;   // TRS matrix for the screen in world space
    float4 FrontTint;                     // rgba tint applied to texture (front face)
    float4 EdgeColour;                    // rgba for box side faces
    float4 BackColour;                    // rgba for back face (alpha=0 → mirror front)
    float4 BorderColour;                  // rgba for border area
    float BorderWidthH;                   // horizontal border width (0-0.4999)
    float BorderWidthV;                   // vertical border width (0-0.4999)
    int   BorderMode;                     // 0=Padding (inset UV), 1=Overlay (blend over)
    float BorderFeather;                  // soft border edge transition
    float EdgeFeather;                    // silhouette edge anti-aliasing strength
    float DepthOffset;                    // view-space Z offset before projection
    float pad1, pad2;
};
```

### Vertex Shader (Procedural Box)
```hlsl
VSOut main(uint id : SV_VertexID) {
    uint indices[36] = {
        0,1,2, 2,1,3,      // front  (faceId 0)
        4,5,6, 6,5,7,      // back   (faceId 1)
        8,9,10, 10,9,11,   // right  (faceId 2, +X)
        12,13,14, 14,13,15,// left   (faceId 3, -X)
        16,17,18, 18,17,19,// top    (faceId 4, +Y)
        20,21,22, 22,21,23 // bottom (faceId 5, -Y)
    };
    uint i = indices[id];

    // Local-space positions for a unit box centered at origin
    float3 local[24]; // 4 verts per face
    // front: local Z = +0.5
    local[0]=(-0.5,-0.5, 0.5); local[1]=(0.5,-0.5, 0.5);
    local[2]=(-0.5, 0.5, 0.5); local[3]=(0.5, 0.5, 0.5);
    // back: local Z = -0.5 (UV mirrored)
    // ... etc for all 6 faces

    // UV: front and back use full texture; sides use (0,0)
    float2 uvs[24];
    uvs[0]=(0,1); uvs[1]=(1,1); uvs[2]=(0,0); uvs[3]=(1,0); // front
    uvs[4]=(0,1); uvs[5]=(1,1); uvs[6]=(0,0); uvs[7]=(1,0); // back
    for(int k=8;k<24;k++) uvs[k]=(0,0);                      // sides

    uint faceIndex = id / 6; // which of 6 faces
    float4 worldPos = mul(ScreenTransform, float4(local[i], 1.0));
    float4 viewPos  = mul(CameraView, worldPos);
    viewPos.z += DepthOffset;  // depth offset in view space before projection

    VSOut vs;
    vs.pos    = mul(CameraProjection, viewPos);
    vs.uv     = uvs[i];
    vs.faceId = faceIndex;
    return vs;
}
```

### Pixel Shader (Border + Edge Feather + Face Dispatch)
```hlsl
float4 main(VSOut vs) : SV_Target {
    float2 uv = vs.uv;

    // Silhouette edge anti-aliasing
    float edgeX    = min(uv.x, 1.0 - uv.x);
    float edgeY    = min(uv.y, 1.0 - uv.y);
    float edgeDist = min(edgeX, edgeY);
    float pw       = fwidth(edgeDist) * EdgeFeather;
    float edgeAlpha = smoothstep(0.0, max(pw, 1e-6), edgeDist);

    // Border
    float bw = min(saturate(BorderWidthH), 0.4999);
    float bh = min(saturate(BorderWidthV), 0.4999);
    float dx = min(uv.x - bw, (1.0 - bw) - uv.x);
    float dy = min(uv.y - bh, (1.0 - bh) - uv.y);
    float signedDist  = min(dx, dy);
    float bpw         = fwidth(signedDist) * BorderFeather;
    float borderBlend = saturate(smoothstep(0.0, max(bpw, 1e-6), signedDist));

    // UV inset for Padding mode
    float2 sampleUV = uv;
    if (BorderMode == 0) {
        float2 inset     = float2(bw, bh);
        float2 innerSize = max(float2(1.0-2*bw, 1.0-2*bh), 1e-6);
        sampleUV = clamp((uv - inset) / innerSize, 0.0, 1.0);
    }

    float4 tex = ScreenTex.Sample(Samp, sampleUV);
    tex.rgb *= FrontTint.rgb;
    tex.a   *= FrontTint.a;

    // Blend border colour
    float3 outRgb = lerp(BorderColour.rgb * BorderColour.a, tex.rgb * tex.a, borderBlend);
    float  outA   = lerp(BorderColour.a, tex.a, borderBlend);
    outA *= edgeAlpha;

    // Pre-multiply final alpha
    if (outA > 0.0) outRgb = outRgb / max(outA, 1e-6);

    // Face dispatch
    if (vs.faceId == 0)      return float4(outRgb, outA);   // front: full texture
    else if (vs.faceId == 1) {
        if (BackColour.a > 0.001)
            return float4(BackColour.rgb, BackColour.a);     // back: solid color
        else
            return float4(outRgb, outA);                     // back: mirror front
    }
    else return float4(EdgeColour.rgb, EdgeColour.a);        // sides: edge color
}
```

### C# ScreenTransform Matrix
```csharp
// Position, Rotation (Quaternion), Scale (Vector3 — width, height, thickness!)
r.ScreenTransform = Matrix4x4.CreateScale(renderer.Scale)
                  * Matrix4x4.CreateFromQuaternion(renderer.Rotation)
                  * Matrix4x4.CreateTranslation(renderer.Position);
// Uploaded transposed to cbuffer
ScreenTransform = Matrix4x4.Transpose(r.ScreenTransform.Value)
```

**Why this is better than our approach:**
- No vertex buffer to manage — zero GPU memory allocation per screen
- Rotation is a proper Quaternion (not just yaw) — arbitrary orientation in 3D space
- Scale X,Y,Z → naturally creates width, height, AND depth for a box, all in one matrix
- No bilinear UV trick needed — standard UV from position local space

---

## 5. RENDER TIMING (ADOPT THIS)

PyonPix hooks two D3D11 functions directly:

```csharp
// Hook 1: IDXGISwapChain::Present — increment frame counter only
private unsafe int PresentDetour(void* swapChain, uint syncInterval, uint flags)
{
    PresentIndex++;
    return HookPresent.Original(swapChain, syncInterval, flags);
}

// Hook 2: ID3D11DeviceContext::OMSetRenderTargets — detect scene transition, draw
private unsafe void OMSetRenderTargetsDetour(void* ctx, uint numViews, void** rtvArray, void* dsv)
{
    HookOMSetRenderTargets.Original(ctx, numViews, rtvArray, dsv);

    // Detect main scene DSV and RTV by matching swap chain dimensions + format
    // Track SceneRendered = (current call is using main DSV + main RTV)

    // Draw trigger: scene was just rendered AND a new Present has happened since last draw
    if (SceneRendered && LastPresentIndex != PresentIndex)
    {
        LastPresentIndex = PresentIndex;
        Draw(); // render all Pix
    }
}
```

This is more precise than UiBuilder.Draw because:
- It fires exactly at the right point in the D3D pipeline (after scene, before UI)
- Frame tracking via PresentIndex prevents double-draw in one frame
- State save/restore is done manually (no Dalamud intervention)

---

## 6. LIGHT SYSTEM (ADOPT THIS — MAJOR FEATURE)

PyonPix injects real FFXIV `SceneLight` objects into the game's render pipeline.
These are real lights that cast shadows, affect character lighting, and interact with the game world.

### Game Signatures
```csharp
[Signature("E8 ?? ?? ?? ?? 48 89 84 FB ?? ?? ?? ?? 48 85 C0 0F 84 ?? ?? ?? ?? 48 8B C8")]
SceneLightCtorDelegate _sceneLightCtor;   // constructor

[Signature("E8 ?? ?? ?? ?? 48 8B 94 FB ?? ?? ?? ?? 48 8B 0D ?? ?? ?? ??")]
SceneLightInitializeDelegate _sceneLightInit;  // initializer

[Signature("F6 41 38 01")]
SceneLightSetupDelegate _sceneLightSpawn;  // adds to scene
```

### Spawn a Light
```csharp
// Allocate SceneLight in game memory
SceneLight* ptr = IMemorySpace.GetDefaultSpace().Malloc<SceneLight>(8uL);
_sceneLightCtor(ptr);    // construct
_sceneLightInit(ptr);    // initialize
_sceneLightSpawn(ptr);   // add to scene
((long*)ptr)[7] |= 2L;   // enable visible flag

// Write properties to RenderLight*
renderLight->LightType  = LightType.PointLight;
renderLight->Flags      = LightFlags.DynamicShadows | LightFlags.CharacterShadows;
renderLight->Color      = new ColorHDR(rgba, intensityMultiplier);
renderLight->Range      = range;
renderLight->LightAngle = angle;
renderLight->FalloffType = FalloffType.Quadratic;
renderLight->FalloffAngle = falloffAngle;
renderLight->Falloff    = falloffPower;
renderLight->ShadowNear = shadowNear;
renderLight->ShadowFar  = shadowFar;
renderLight->Transform  = &sceneLight->Transform;

// Write position/rotation to the Transform
Unsafe.Write(&sceneLight->Transform.Position, Vector3.op_Implicit(worldPos));
Unsafe.Write(&sceneLight->Transform.Rotation, Quaternion.op_Implicit(rotation));
```

### ColorHDR Format (FFXIV's HDR color)
```csharp
// FFXIV stores HDR color as (R², G², B²) × (4)² with separate Intensity float
// So RGB values are in the range [0, (4×1)²] = [0, 16]
// The RGB property does the conversion:
get { return Vector3.SquareRoot(_vec3) / 4f; }   // internal → linear [0,1]
set { value *= 4f; _vec3 = value * value; }        // linear [0,1] → internal
```

### Screen Color → Light Color Pipeline
Every frame the renderer renders a Pix, it also downsamples the screen texture to 16×16
and reads back the average RGB:

```csharp
// GPU: render browser SRV to 16×16 staging texture (avgVS/avgPS — full-screen tri)
ctx.OutputMerger.SetRenderTargets(_avgRTV);
ctx.Rasterizer.SetViewport(new Viewport(0, 0, 16, 16));
ctx.Draw(3, 0); // full-screen triangle

// CPU readback: Map staging, sum all 256 pixels, divide
ctx.CopyResource(_avgTexture, _avgStaging);
DataBox box = ctx.MapSubresource(_avgStaging, 0, MapMode.Read, ...);
// sum r,g,b across 16×16 pixels → average

// Temporal smoothing (exponential moving average with time-based accumulation)
// History ring buffer of 64 samples; weight by time window
// EMA: value = Lerp(prev, historyAvg, 1 - exp(-dt/tau))
```

The smoothed average is then passed to `LightService.UpdateById()` which adjusts the light color.

### Despawn
```csharp
// Must run on framework thread
private void InvokeDtor(SceneLight* light)
{
    GetVirtualFunc<CleanupRenderDelegate>(light, 1)(light);  // vf[1]: cleanup render
    GetVirtualFunc<DestructorDelegate>(light, 0)(light, false); // vf[0]: destructor
}
```

### Light Position (Relative to Screen)
The light position and rotation are stored relative to the screen in its config.
At render time, they're transformed to world space:
```csharp
Vector3 worldPos = Vector3.Transform(light.Position, screenRotation) + screenPosition;
Quaternion worldRot = Quaternion.Normalize(screenRotation * light.Rotation);
```

---

## 7. SPATIAL AUDIO

Updated every 100ms (not every frame):

```csharp
// Determine listener position/direction (character or camera)
Vector3 listenerPos = (listenerType == Camera) ? cameraPos : playerPos;
Vector3 listenerFwd = Vector3.Normalize(Vector3.Transform(Vector3.UnitX, rotation));
// (uses player rotation or camera forward)

foreach (var renderer in renderers)
{
    // Get screen world position from ScreenTransform
    Vector3 screenPos = renderer.ScreenTransform.Value.Translation;
    Vector3 toScreen  = screenPos - listenerPos;
    float dist = toScreen.Length();

    // Distance-based volume falloff
    float maxDist = Max(0.01f, audio.FalloffMaxDistance);
    float falloff = Clamp(dist / maxDist, 0, 1);
    float vol = audio.Volume * (1f - falloff) * masterVolume;
    // FalloffStrength curves the rolloff further

    // Stereo pan: dot product of (toScreen normalized) with listener right vector
    Vector3 right = Vector3.Cross(listenerFwd, Vector3.UnitY);
    float pan = Dot(Normalize(toScreen), right); // -1 = hard left, +1 = hard right

    float leftVol  = vol * Clamp(1 - pan, 0, 1);
    float rightVol = vol * Clamp(1 + pan, 0, 1);

    BrowserInterop.UpdateSpatialAudio(renderer.id, leftVol, rightVol);
}
```

---

## 8. DX SERVICE (HOW THEY GET THE DEVICE)

```csharp
public class DXService
{
    public SharpDX.Direct3D11.Device? D3D11Device { get; private set; }
    public DeviceContext? D3D11Context { get; private set; }
    public SwapChain? DXGISwapChain { get; private set; }
    public LUID AdapterLuid { get; private set; }  // passed to browser for shared textures

    // Called from a hook or UiBuilder callback to initialize:
    D3D11Device  = DXGISwapChain.GetDevice<SharpDX.Direct3D11.Device>();
    D3D11Context = D3D11Device.ImmediateContext;
    BackBufferTex = swapChain.GetBackBuffer<Texture2D>(0);
    BackBufferRTV = new RenderTargetView(D3D11Device, BackBufferTex);

    // Get LUID for browser init:
    SharpDX.DXGI.Device dxgiDevice = D3D11Device.QueryInterface<SharpDX.DXGI.Device>();
    AdapterLuid = dxgiDevice.Adapter.Description.Luid.ToLUID();
}
```

In Vortice terms:
```csharp
// device.QueryInterface<IDXGIDevice>() → adapter.GetDesc() → .AdapterLuid
```

---

## 9. STRUCTS — SceneLight and RenderLight

```csharp
[StructLayout(LayoutKind.Explicit, Size = 160)]
struct SceneLight
{
    [FieldOffset(0)]   nint* _vf;          // virtual function table pointer
    [FieldOffset(0)]   DrawObject DrawObject;
    [FieldOffset(80)]  Transform Transform;   // position + rotation
    [FieldOffset(128)] nint Culling;
    [FieldOffset(136)] byte Flags00;
    [FieldOffset(137)] byte Flags01;
    [FieldOffset(144)] RenderLight* RenderLight;  // pointer to render light data
}

[StructLayout(LayoutKind.Explicit, Size = 160)]
struct RenderLight
{
    [FieldOffset(24)]  LightFlags Flags;        // Reflections, DynamicShadows, etc.
    [FieldOffset(28)]  LightType LightType;     // Point, Spot, Area, Directional
    [FieldOffset(32)]  Transform* Transform;    // pointer back to SceneLight's transform
    [FieldOffset(40)]  ColorHDR Color;          // HDR color (16 bytes: R²,G²,B², Intensity)
    [FieldOffset(56)]  Vector3 _unkVec0;
    [FieldOffset(68)]  Vector3 _unkVec1;
    [FieldOffset(80)]  Vector4 _unkVec2;
    [FieldOffset(96)]  float ShadowNear;
    [FieldOffset(100)] float ShadowFar;
    [FieldOffset(104)] FalloffType FalloffType;
    [FieldOffset(112)] Vector2 AreaAngle;
    [FieldOffset(120)] float _unk0;
    [FieldOffset(128)] float Falloff;           // falloff power
    [FieldOffset(132)] float LightAngle;        // spot cone angle
    [FieldOffset(136)] float FalloffAngle;      // spot falloff start angle
    [FieldOffset(140)] float Range;
    [FieldOffset(144)] float CharaShadowRange;
}

// FFXIV HDR Color encoding
[StructLayout(LayoutKind.Explicit, Size = 16)]
struct ColorHDR
{
    [FieldOffset(0)]  Vector3 _vec3;  // = (R × 4)², (G × 4)², (B × 4)²
    [FieldOffset(0)]  float Red;
    [FieldOffset(4)]  float Green;
    [FieldOffset(8)]  float Blue;
    [FieldOffset(12)] float Intensity;

    // Linear [0,1] RGB ↔ internal HDR storage:
    // get: sqrt(_vec3) / 4
    // set: value *= 4; _vec3 = value * value
}
```

---

## 10. WHAT PYONPIX DOESN'T HAVE (OUR ADVANTAGES)

- **No network sync visible** — no WebSocket server or client. They may sync via the browser itself
  (using a shared web-based sync service), but no P2P sync is in the C# code.
- **No URL resolution/yt-dlp** — the browser handles all video natively.
- **No playlist system** — no ordered queue of URLs.
- Their **SharpDX dependency** is deprecated (Dalamud dropped SharpDX support in API 14+).
  Somehow they ship SharpDX DLLs themselves and it works — but it's risky long-term.

---

## 11. ADOPTION ROADMAP FOR FFXIV-TV

### Priority 1: Shader Architecture Upgrade
Replace our current shader approach with PyonPix's:

1. **Remove vertex buffer** — use procedural geometry with `Draw(36, 0)` + `SV_VertexID`
2. **ScreenTransform matrix** — replace our 4-corner Vector3 array with a TRS matrix in cbuffer
   - `Matrix4x4.CreateScale(w, h, 0.01f) * Matrix4x4.CreateFromQuaternion(rot) * Matrix4x4.CreateTranslation(pos)`
   - Thin Z scale makes it nearly flat while still being a box
3. **Border system** — add BorderWidthH/V, BorderMode, BorderFeather, BorderColour to cbuffer
4. **Edge feather** — add EdgeFeather using `fwidth() + smoothstep()` for anti-aliased edges
5. **Back face** — add BackColour (for solid back face or mirror)
6. **FrontTint** — rename our existing tint
7. **Full Quaternion rotation** — replace yaw-only rotation with Quaternion in config

Cbuffer layout change:
```csharp
[StructLayout(LayoutKind.Sequential)]
struct ShaderParams {
    Matrix4x4 CameraView;        // 64 bytes
    Matrix4x4 CameraProjection;  // 64 bytes
    Matrix4x4 ScreenTransform;   // 64 bytes
    Vector4   FrontTint;         // 16 bytes
    Vector4   EdgeColour;        // 16 bytes
    Vector4   BackColour;        // 16 bytes
    Vector4   BorderColour;      // 16 bytes
    float     BorderWidthH;      // 4
    float     BorderWidthV;      // 4
    int       BorderMode;        // 4
    float     BorderFeather;     // 4
    float     EdgeFeather;       // 4
    float     DepthOffset;       // 4
    float     _pad0, _pad1;      // 8 (16-byte align)
}
// Total: 64+64+64+16+16+16+16+24 = 280 bytes
```

### Priority 2: Light System
Port the LightService from PyonPix:
1. Add game signatures to Plugin.cs (or LightService.cs)
2. Create `SceneLight` + `RenderLight` + `ColorHDR` structs matching FFXIV memory layout
3. Implement `LightService`: spawn/update/despawn per-screen lights
4. Add light config to Configuration.cs (LightEnabled, LightType, Colour, Intensity, Range, etc.)
5. Add light UI controls to MainWindow

### Priority 3: Average Screen Color for Light
Add `ComputeLight()` to D3DRenderer:
1. Create a 16×16 D3D11 render target + staging texture
2. After rendering each frame, draw a full-screen triangle with the browser SRV → 16×16 target
3. CopyResource to staging, MapSubresource to CPU-read it
4. Average all 256 pixels → pass to LightService for color influence
5. Implement temporal smoothing (EMA)

### Priority 4: Browser (Long-Term)
The browser DLL is proprietary. Options:
A. **WebView2 + shared texture** — Microsoft's WebView2 SDK supports rendering to a D3D11
   shared texture (`ICoreWebView2CompositionController` + `ICoreWebView2ExperimentalCompositionController5`).
   This requires significant work but is the clean path.
B. **Continue with LibVLC** — fix the Reddit/aniwatch issues via alternate format selectors,
   keep yt-dlp but improve its resilience. Simpler but always limited to what yt-dlp supports.
C. **Investigate existing CEF/.NET wrapper** — CefSharp or CefNet can render to offscreen bitmaps;
   connecting that to a D3D11 shared texture requires custom native code (like Pyon did).

For now, Option B is the path of least resistance. The shader/light improvements are achievable
without replacing LibVLC.

### Priority 5: Multiple Screens
The Pix model (multiple independent screens, each with ID, position, URL, light) is the right
long-term architecture. For FFXIV-TV this means:
- `ScreenDefinition` evolves to a `Screen` class with full Pix-equivalent config
- Multiple screens in config list
- Each screen has its own SRV slot in D3DRenderer
- Add "New Screen" / "Delete Screen" UI

---

## 12. KEY TAKEAWAYS

1. **Browser > LibVLC** — for video compatibility. The browser just works for everything.
   PyonPix never has "Resolving URL..." issues because the browser handles all formats natively.

2. **ScreenTransform matrix > 4 corners** — simpler shader, arbitrary 3D rotation, no UV tricks.
   We should upgrade to this immediately.

3. **Real lights are achievable** — the signatures are in the code. We can port this to Vortice.
   This is a massive differentiator vs. other plugins.

4. **Screen-color → light glow** is a beautiful effect and technically straightforward
   (GPU downsample + CPU readback + EMA smoothing).

5. **Spatial audio** requires browser support — their browser DLL handles per-tab volume natively.
   With LibVLC we'd need to adjust the `Volume` property based on distance, which we can do.

6. **Multiple screens** is the right product direction. Single-screen is a limitation.
