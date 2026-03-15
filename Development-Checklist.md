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

- [ ] `dotnet build` compiles cleanly
- [ ] Plugin loads without error in Dalamud log
- [ ] `/fftv` opens settings window
- [ ] `/fftv place` places screen 3 units in front of player
- [ ] `/fftv hide` toggles screen visibility
- [ ] Settings window — Position sliders update screen location in real time
- [ ] Settings window — Yaw slider rotates screen orientation
- [ ] Settings window — Width / Height sliders resize screen
- [ ] Settings window — "Lock 16:9" button constrains aspect ratio
- [ ] Settings window — "Place at Player" button snaps screen to player position
- [ ] Solid purple placeholder quad renders when no image is loaded
- [ ] Quad correctly tracks movement of screen between locations
- [ ] Image path text input loads a PNG/JPEG from disk
- [ ] Image displays correctly on the quad (correct orientation, no UV flip)
- [ ] Tint / alpha controls visually affect the displayed image
- [ ] Config persists across plugin reload (position, path, visibility)
- [ ] Corner debug dots (DEBUG build) appear at correct screen corners
- [ ] Screen disappears when any corner goes behind the camera (graceful handling)

---

## Phase 2 — D3D11 World-Space Rendering (Proper Depth)

Goal: Replace the ImGui overlay with actual D3D11 geometry injected into the render pipeline
so characters and world geometry correctly occlude the screen.

### Research / Setup
- [ ] Identify method to obtain D3D11 device pointer from FFXIV (FFXIVClientStructs or Dalamud interop)
- [ ] Identify correct render hook point (pre-UI pass, after scene geometry)
- [ ] Confirm access to game camera view/projection matrix (FFXIVClientStructs Camera struct)
- [ ] Prototype: successfully get D3D11 device and call `GetImmediateContext()`

### Shader Authoring
- [ ] Write `ScreenVS.hlsl` (world-space vertex shader, ViewProj cbuffer)
- [ ] Write `ScreenPS.hlsl` (simple texture sampler)
- [ ] Compile shaders at runtime via `D3DCompile` (SharpDX or D3DCompiler.dll P/Invoke)
- [ ] Verify shader compilation succeeds on first run

### D3D11 Resource Setup
- [ ] Create vertex buffer (4 vertices: position + UV)
- [ ] Create index buffer (2 triangles)
- [ ] Create constant buffer for ViewProj matrix
- [ ] Create sampler state (linear filtering, clamp)
- [ ] Create rasterizer state (no backface cull for two-sided screen)
- [ ] Create blend state (alpha blending for transparent edges)
- [ ] Create depth-stencil state (depth read enabled, write disabled)

### Frame Rendering
- [ ] Hook render function via `IGameInteropProvider`
- [ ] Each frame: update vertex buffer with current world-space corner positions
- [ ] Each frame: update ViewProj cbuffer from game camera matrices
- [ ] Each frame: bind texture, draw indexed quad
- [ ] Verify screen renders with correct depth (character in front occludes screen)
- [ ] Verify screen perspective-corrects as camera moves
- [ ] Cleanup: release all D3D11 resources on Dispose

### Regression
- [ ] Phase 1 fallback mode still works if D3D11 init fails
- [ ] No D3D11 device leak on plugin reload

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
