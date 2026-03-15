# FFXIV-TV Development Checklist

**IMPORTANT:** This file must be updated whenever a task is completed. Check off items as they are done.

---

## Phase 0 — Project Scaffolding

- [x] Create `FFXIV-TV/` folder in devPlugins
- [x] Create `.gitignore` (excludes bin/, obj/, CLAUDE.md)
- [x] Create `LICENSE` (Copyright Sansflaire)
- [x] Create `README.md`
- [x] Create `Basics.md` (concept + technical detail)
- [x] Create `CLAUDE.md` (AI context, gitignored)
- [x] Create `Development-Checklist.md` (this file)
- [x] Create `FFXIV-TV.json` (Dalamud manifest, DalamudApiLevel 14)
- [x] Create `src/FFXIV-TV.csproj` (net10.0-windows, Dalamud refs, auto-copy post-build)
- [x] Create `src/Plugin.cs` (entry point, command registration, draw hook)
- [x] Create `src/Configuration.cs` (IPluginConfiguration, screen def + image path)
- [x] Create `src/ScreenDefinition.cs` (world-space screen model, corner computation)
- [x] Create `src/ScreenRenderer.cs` (Phase 1: WorldToScreen + ImGui quad)
- [x] Create `src/Windows/MainWindow.cs` (settings UI)
- [x] First successful `dotnet build` with no errors
- [ ] Register DLL in `/xlsettings` Dev Plugin Locations
- [ ] Enable plugin in `/xlplugins`
- [ ] Verify `/fftv` command opens settings window

---

## Phase 1 — World-Space Image Display (ImGui Overlay)

Goal: A flat rectangle visible in the game world at a configurable position, showing either a
solid color placeholder or an image loaded from disk.

- [x] `dotnet build` compiles cleanly
- [ ] Plugin loads without error in Dalamud log
- [ ] `/fftv` opens settings window
- [ ] `/fftv place` places screen 3 units in front of player
- [ ] `/fftv hide` toggles screen visibility
- [x] Settings window — Position sliders update screen location in real time
- [x] Settings window — Yaw slider rotates screen orientation
- [x] Settings window — Width / Height sliders resize screen
- [x] Settings window — "Lock 16:9" button constrains aspect ratio
- [x] Settings window — "Place at Player" button snaps screen to player position
- [x] Solid purple placeholder quad renders when no image is loaded
- [x] Quad correctly tracks movement of screen between locations
- [x] Image path text input loads a PNG/JPEG from disk
- [x] Image displays correctly on the quad (correct orientation, no UV flip)
- [x] Tint / alpha controls visually affect the displayed image
- [x] Config persists across plugin reload (position, path, visibility)
- [x] Corner debug dots (DEBUG build) appear at correct screen corners
- [x] "Always Draw" option prevents culling when screen center goes off-camera
- [x] Black backing quad drawn behind image (configurable)

---

## Phase 2 — D3D11 World-Space Rendering (Proper Depth)

Goal: Replace the ImGui overlay with actual D3D11 geometry injected into the render pipeline
so characters and world geometry correctly occlude the screen.

### Research / Setup
- [x] Identify method to obtain D3D11 device pointer from FFXIV (ImGui_ImplDX11_Data at offsets [0]/[1])
- [x] Identify correct render hook point (UiBuilder.Draw — runs in D3D present, after scene geometry)
- [x] Confirm access to game camera view/projection matrix (`Control.Instance()->ViewProjectionMatrix`, M44=1f fix)
- [x] Prototype: successfully get D3D11 device and call `GetImmediateContext()`

### Shader Authoring
- [x] Write vertex shader HLSL (world-space, `row_major float4x4 ViewProj` cbuffer, `mul(pos, ViewProj)`)
- [x] Write pixel shader HLSL (bilinear inverse UV from SV_POSITION — all TEXCOORD semantics
      are silently zeroed in this D3D context; SV_POSITION is the only reliable per-pixel data)
- [x] Compile shaders at runtime via `Vortice.D3DCompiler.Compiler.Compile()`
- [x] Verify shader compilation succeeds at runtime (in-game test)

### D3D11 Resource Setup
- [x] Create vertex buffer (4 vertices: position + UV, dynamic)
- [x] Create index buffer (6 indices, two triangles)
- [x] Create constant buffer for ViewProj matrix
- [x] Create sampler state (linear filtering, clamp)
- [x] Create rasterizer state (no backface cull for two-sided screen)
- [x] Create blend state (alpha blending — NonPremultiplied)
- [x] Create depth-stencil state (reversed-Z: ComparisonFunction.Greater, write disabled)
- [x] Fallback depth-stencil state for when no DSV is bound (DepthEnable=false)

### Frame Rendering
- [x] Device/context obtained lazily on first draw frame (TryInitialize pattern)
- [x] Each frame: query current DSV via OMGetRenderTargets to detect if depth buffer available
- [x] Each frame: update vertex buffer with current world-space corner positions
- [x] Each frame: update ViewProj cbuffer from `Control.Instance()->ViewProjectionMatrix`
- [x] Each frame: borrow SRV from Dalamud texture (AddRef/Dispose pattern), bind and draw
- [x] Full pipeline state save/restore so ImGui is unaffected
- [x] `dotnet build` succeeds with 0 errors
- [x] Verify screen renders with correct depth (character in front occludes screen) — confirmed
- [x] Verify image displays correctly on quad — confirmed (Round 18)
- [ ] Verify screen perspective-corrects as camera moves — in-game test
- [x] Cleanup: release all D3D11 resources on Dispose (Vortice COM objects)

### Regression
- [x] Phase 1 fallback mode still works if D3D11 init fails
- [x] No D3D11 device leak on plugin reload (AddRef on borrow, Release on Dispose)

---

## Phase 3 — Video Playback (Local File)

Goal: Play a video file on the screen with basic playback controls.

### Video Decode Library
- [ ] Research decode options: LibVLCSharp vs Windows Media Foundation vs FFmpeg.AutoGen
- [ ] Choose library and add NuGet reference to csproj
- [ ] Prototype: decode a single frame from an .mp4 file to a byte array

### Dynamic Texture Pipeline
- [ ] Create D3D11 staging texture (CPU writable, BGRA32 format, video resolution)
- [ ] Create D3D11 shader resource texture (GPU readable)
- [ ] Each frame: lock staging texture, copy decoded frame pixels, unlock, CopyResource to GPU tex
- [ ] Verify first video frame appears on screen

### Playback
- [ ] `/fftv play <path>` command starts video
- [ ] `/fftv pause` toggles pause
- [ ] `/fftv stop` stops and clears screen
- [ ] Settings window: video file path input + Play/Pause/Stop buttons
- [ ] Video loops when it reaches the end
- [ ] Correct frame timing (decode at video FPS, not game FPS)
- [ ] Audio playback (optional — via system audio, separate from texture pipeline)

---

## Phase 4 — Network Sync (Multi-Player)

Goal: Host PC serves a WebSocket server; all connected clients play the same content
simultaneously. No video is relayed — only control messages.

### WebSocket Server (Host)
- [ ] Research: System.Net.WebSockets vs third-party library (e.g., WebSocketSharp)
- [ ] Implement lightweight WebSocket server on configurable port
- [ ] Settings window: "Host Mode" toggle, port input, connection count display
- [ ] Server broadcasts JSON control messages: `{ "type": "play", "url": "...", "ts": 0.0 }`
- [ ] Server broadcasts: `{ "type": "pause" }`, `{ "type": "stop" }`, `{ "type": "seek", "ts": N }`
- [ ] Heartbeat / ping to detect disconnected clients

### WebSocket Client (Viewers)
- [ ] Settings window: "Join Mode" toggle, host IP:port input, connection status indicator
- [ ] Client connects to host on button press
- [ ] On `play` message: load URL/file and begin playback at specified timestamp
- [ ] On `pause` / `seek` / `stop`: execute immediately
- [ ] Auto-reconnect on disconnect with backoff

### URL / Stream Support
- [ ] Support YouTube URLs via yt-dlp subprocess (extract direct stream URL)
- [ ] Support direct .mp4 / .webm URLs (HTTP range requests or HLS)
- [ ] Support local file paths (host and clients must have same path — or file server)
- [ ] Error handling: gracefully show error text on screen if URL fails to load

### Testing
- [ ] Two clients on same machine see synchronized video (< 500ms drift)
- [ ] Latency compensation: server includes its current timestamp in messages, client seeks to match
- [ ] Plugin unload cleanly stops server / disconnects client

---

## Phase 5 — Polish & QoL

- [ ] Screen can be locked (prevents accidental move via sliders)
- [ ] Multiple screens support (list of ScreenDefinitions in config)
- [ ] Screen name labels (show name above screen as floating text)
- [ ] Opacity fade based on player distance from screen
- [ ] `/fftv list` lists all placed screens by name
- [ ] `/fftv remove <name>` removes a screen
- [ ] Settings window: screen list tab + per-screen editing
- [ ] Import/export screen layout as JSON

---

## Known Limitations & Notes

- **Phase 1 has no depth testing** — the screen always renders on top of characters.
  Phase 2 fixes this with D3D11 injection.
- **WorldToScreen clips at camera plane** — if any corner goes behind the camera,
  the entire screen stops rendering. Phase 2 handles proper frustum clipping.
- **Multi-player requires all viewers to run the plugin** — there is no way to show
  the screen to vanilla players. This is by design.
- **Video decode on the game thread must be bounded** — use a background thread for
  decode and only upload the latest frame on the render tick.
