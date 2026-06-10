# FFXIV-TV — Concept, Goal, and Technical Breakdown

## What We're Building

A Dalamud plugin that spawns a virtual "TV screen" in FFXIV's 3D world space and displays
video or images on it — visible to all players who have the plugin loaded.

Inspired by a private mod seen in player housing and outdoor zones that achieves:
- A flat rectangle placed anywhere in the game world (including outdoors, non-housing areas)
- Video playing on the rectangle in real-time
- Proper 3D perspective, depth occlusion (characters can stand in front of it)
- Shared across multiple players simultaneously

## Evidence from Observed Screenshots

Three screenshots analyzed:
1. Housing room with TV furniture showing video — could be texture replacement of furniture
2. Housing room, different wall — same approach
3. **OUTDOOR open zone** — rectangle floating in open world, character lying IN FRONT of it
   with correct depth. This proves it is NOT a 2D screen overlay. It is genuine 3D geometry.

### Conclusion
The outdoor image confirms the technique uses **D3D11 world-space geometry injection**:
- A vertex buffer quad is drawn in the game's 3D world
- Custom vertex shader transforms world-space positions using the game's view/projection matrix
- Custom pixel shader samples a 2D texture (the video frame)
- The draw call is injected into the render pipeline BEFORE the game's post-processing/UI,
  giving proper depth testing against scene geometry and characters

The "other players can see it" feature means each client runs the plugin and renders the
screen locally, synchronized by a shared source (URL or WebSocket stream from the host).

## Technical Architecture

### Rendering Pipeline Position

```
FFXIV Render Frame:
  [Scene 3D Geometry] ← inject our quad here (Phase 2)
  [Lighting / Shadow]
  [Post-Processing]
  [ImGui Overlay]      ← Phase 1 draws here (no depth, but simpler)
  [Present to screen]
```

### Phase 1: WorldToScreen + ImGui (No Depth)

The simplest approach. No D3D11 knowledge required:

1. Store screen as: center position (Vector3), yaw angle, width, height
2. Compute 4 world-space corners from center + right/up vectors
3. Call `IGameGui.WorldToScreen(corner, out screenPos)` for each corner
4. If all 4 project in front of camera: draw with `ImGui.GetBackgroundDrawList().AddImageQuad()`
5. Texture loaded via `ITextureProvider.GetFromFile(path)`

**Limitation:** No depth testing — screen always renders on top of characters/walls.
Good enough for proof of concept.

### Phase 2: D3D11 Geometry Injection (With Depth)

Proper world-space rendering:

1. Get D3D11 device pointer (via FFXIVClientStructs render engine or Dalamud interop)
2. Compile HLSL vertex + pixel shaders at startup
3. Create vertex buffer: 4 vertices with (x,y,z,u,v) in world space
4. Get game camera view/projection matrices from FFXIVClientStructs Camera
5. Hook a pre-UI render function (via IGameInteropProvider) to inject draw calls
6. Each frame: update VB with new world position, set texture, draw quad

**D3D11 Library: `TerraFX.Interop.Windows` (NuGet)**
Dalamud dropped SharpDX as of v10. TerraFX is the correct modern interop library for raw D3D11.
This is what Dalamud itself uses internally. Add as a NuGet reference in the csproj.

Note: Vortice DLLs are present in Glamourer's install folder but are NOT the right choice here.
TerraFX provides the thin COM interop layer needed to issue D3D11 draw calls from C#.

**Shader compilation options:**
- Best: pre-compile HLSL to bytecode offline → embed as `byte[]` in source (no runtime dep)
- Alternative: P/Invoke `d3dcompiler_47.dll` (always present on Windows) for runtime compilation

**HLSL Vertex Shader sketch:**
```hlsl
cbuffer FrameConstants : register(b0)
{
    matrix ViewProj;
};
struct VSIn  { float3 pos : POSITION; float2 uv : TEXCOORD; };
struct VSOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD; };
VSOut main(VSIn v) {
    VSOut o;
    o.pos = mul(float4(v.pos, 1.0), ViewProj);
    o.uv = v.uv;
    return o;
}
```

**HLSL Pixel Shader sketch:**
```hlsl
Texture2D tex : register(t0);
SamplerState samp : register(s0);
float4 main(float2 uv : TEXCOORD) : SV_TARGET {
    return tex.Sample(samp, uv);
}
```

### Phase 3: Video Frames

- Use LibVLCSharp or Windows Media Foundation to decode video
- Each frame: copy pixel data into a D3D11 staging texture, then CopyResource to GPU texture
- Target: ~30fps texture updates

### Phase 4: Network Sync

```
Host PC (you)                    Other Players
┌──────────────┐                ┌──────────────────┐
│ FFXIV-TV     │                │ FFXIV-TV         │
│ + WebSocket  │ ──URL/sync───► │ + WebSocket      │
│   Server     │                │   Client         │
│ (localhost)  │                │ (same content)   │
└──────────────┘                └──────────────────┘
```

- Host starts a WebSocket server on a configurable port
- Clients enter the host's IP:port in the plugin settings
- Sync payload: `{ "type": "play", "url": "...", "timestamp": 12.5 }`
- Each client fetches the stream URL independently and seeks to the same timestamp
- No video is actually relayed through the server — just control messages

## Key Dalamud APIs Used

| API | Purpose |
|-----|---------|
| `IGameGui.WorldToScreen` | Project 3D world pos to 2D screen pos |
| `ITextureProvider.GetFromFile` | Load image file as GPU texture |
| `IDalamudTextureWrap.ImGuiHandle` | Pass texture to ImGui draw calls |
| `IGameInteropProvider` | Hook game render functions (Phase 2) |
| `IFramework.Update` | Per-frame tick for video decode + upload |
| `IClientState.LocalPlayer` | Get player world position for "place here" |

## File Layout

```
FFXIV-TV/
  .gitignore
  LICENSE                 Copyright Sansflaire
  README.md               Public-facing documentation
  Basics.md               This file — concept + technical detail
  CLAUDE.md               AI context (gitignored)
  FFXIV-TV.json           Dalamud plugin manifest
  src/
    FFXIV-TV.csproj       Build config (net10.0-windows, Dalamud refs)
    Plugin.cs             Entry point, service injection, command registration
    Configuration.cs      Screen position/size/URL config, IPluginConfiguration
    ScreenDefinition.cs   Data model for a placed screen (pos, yaw, w, h)
    ScreenRenderer.cs     Phase 1: WorldToScreen + ImGui quad rendering
    Windows/
      MainWindow.cs       Settings UI (position sliders, image path, etc.)
```

## Development Steps (Ordered)

1. [x] Project scaffolding (csproj, manifest, Plugin.cs)
2. [ ] ScreenDefinition + Configuration
3. [ ] ScreenRenderer Phase 1 — colored rectangle in world space (prove projection)
4. [ ] ScreenRenderer Phase 1 — textured quad (load image from disk)
5. [ ] MainWindow — position/yaw/size sliders, image path input
6. [ ] ScreenRenderer Phase 2 — D3D11 injection with depth
7. [ ] Video frame decode + upload
8. [ ] WebSocket server + client sync
