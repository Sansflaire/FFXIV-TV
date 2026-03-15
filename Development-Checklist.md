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
- [x] Verify screen renders with correct depth (character in front occludes screen)
- [x] Verify image displays correctly on quad — confirmed (Round 18)
- [ ] Verify screen perspective-corrects as camera moves — in-game test
- [x] Cleanup: release all D3D11 resources on Dispose (Vortice COM objects)

### Regression
- [x] Phase 1 fallback mode still works if D3D11 init fails
- [x] No D3D11 device leak on plugin reload (AddRef on borrow, Release on Dispose)

---

## Phase 3 — Video Playback (Local File)

Goal: Play a video file on the screen with basic playback controls.
Library chosen: **LibVLCSharp** + `VideoLAN.LibVLC.Windows` NuGet (bundles libvlc DLLs).

### Video Decode Library
- [x] Research decode options: LibVLCSharp vs Windows Media Foundation vs FFmpeg.AutoGen
- [x] Choose library: LibVLCSharp (VideoLAN.LibVLC.Windows bundles DLLs, clean BGRA callback API)
- [x] Add NuGet references to csproj: `LibVLCSharp` + `VideoLAN.LibVLC.Windows`
- [x] `dotnet build` succeeds with LibVLCSharp referenced
- [ ] Prototype: open a video file with LibVLC, receive at least one BGRA frame via callback

### Dynamic Texture Pipeline
- [x] Create `VideoPlayer` class: owns LibVLC instance, media player, pinned BGRA pixel buffer
- [x] Frame callbacks: LockCallback pins buffer for LibVLC write, DisplayCallback marks frame dirty (volatile flags)
- [x] Create D3D11 dynamic texture (CPU-writable, Dynamic/Write, B8G8R8A8_UNorm, video resolution)
- [x] Render thread: UploadFrame — if dirty and VLC not writing, Map(WriteDiscard)/memcpy/Unmap
- [x] D3DRenderer: expose Device property, SetVideoPlayer(), prefer VideoPlayer SRV over static image SRV
- [x] Plugin.cs: VideoPlayer lifecycle, play/pause/stop commands, device wiring after D3D init
- [x] MainWindow: video path input + Play/Pause/Stop buttons + status label
- [ ] Verify first video frame appears on the world-space screen (in-game test)

### Playback Controls
- [x] `/fftv play <path>` command starts video (stops any current video first)
- [x] `/fftv pause` toggles pause/resume
- [x] `/fftv stop` stops playback and reverts to static image (or placeholder)
- [x] Settings window: video file path input + Play / Pause / Stop buttons
- [x] Video loops when it reaches the end (`EndReached` event → stop + play from background thread)
- [ ] Correct frame timing (LibVLC delivers frames at video FPS via callback, not game FPS) — verify in-game
- [ ] Audio playback works via system audio (LibVLC handles this automatically) — verify in-game

### Cleanup / Regression
- [x] `VideoPlayer.Dispose()` stops playback and releases LibVLC resources cleanly
- [ ] Plugin reload (disable/enable) does not leak LibVLC instances or D3D11 textures — verify in-game
- [x] Phase 1 fallback (ImGui overlay) still works when D3D11 unavailable
- [x] Static image path still works when no video is loaded

---

## Phase 3.5 — URL / Web Video

Goal: Play video from HTTP/HTTPS URLs (direct streams and YouTube) on the in-game screen.
LibVLC already supports HTTP natively; YouTube requires yt-dlp to extract the direct stream URL.

### VideoPlayer Changes
- [x] Detect URL input (starts with `http://` or `https://`) vs local file path
- [x] Direct URL path: skip `File.Exists`, use LibVLC network stream (1920x1080 default)
- [x] YouTube URL path: run yt-dlp subprocess (`--get-url --format best`) → extract direct stream URL
- [x] yt-dlp lookup: explicit `YtDlpPath` prop → plugin dir → system `where` fallback
- [x] Unified `Play(string pathOrUrl)` handles both files and URLs
- [x] `Status` property: "Stopped" / "Loading..." / "Resolving YouTube URL..." / "Connecting..." / "Playing" / "Paused" / "Error: ..."
- [x] Graceful error if URL fails to load (log + status text, no crash)

### Configuration
- [x] Add `ContentMode` enum (`Image`, `LocalVideo`, `UrlVideo`) to `Configuration.cs`
- [x] Add `ActiveMode`, `VideoPath`, `VideoUrl`, `YtDlpPath` fields to `Configuration.cs`

### UI
- [x] Content Source section with `Mode` dropdown replaces separate Image / Video sections
- [x] Image mode: shows image file path + Apply (unchanged behavior)
- [x] Local Video mode: shows file path input + Play / Pause / Stop + status label
- [x] URL/Stream mode: shows URL input + Play / Pause / Stop + status label + yt-dlp path field

### Plugin.cs / Commands
- [x] `/fftv play <url>` auto-switches mode to UrlVideo/LocalVideo and saves config
- [x] `YtDlpPath` kept in sync from config to VideoPlayer each frame
- [x] Image path cleared from D3DRenderer when in video mode (avoids stale texture)

### Testing
- [ ] Direct MP4 URL plays correctly on the world-space screen
- [ ] YouTube URL resolves via yt-dlp and plays (requires yt-dlp.exe present)
- [ ] Error shown cleanly if URL is invalid or yt-dlp not found
- [ ] Local file paths still work unchanged
- [ ] Image mode still works unchanged
- [ ] Mode dropdown persists across plugin reload (saved in config)
- [ ] Plugin reload does not leak yt-dlp subprocess handles

---

## Phase 3.6 — Video Playlist

Goal: Play a list of videos in order, automatically advancing when each one ends, then loop the list.

### Data Model
- [x] Add `Playlist` class or `List<string> PlaylistItems` to `Configuration.cs`
- [x] Add `PlaylistIndex` (current position) and `PlaylistLoop` (bool) to `Configuration.cs`
- [x] Persist playlist across plugin reload

### VideoPlayer / SyncCoordinator
- [x] `VideoPlayer`: fires `EndOfMedia` event on end; SyncCoordinator decides next action
- [x] `VideoPlayer`: on last item end and `PlaylistLoop = true`, wrap index to 0 and play first
- [x] `SyncCoordinator`: on EndOfMedia, advance playlist or loop single file; host broadcasts play to clients via existing `BroadcastPlay`

### UI
- [x] Playlist panel in MainWindow: ordered list of file paths / URLs (add/remove/reorder)
- [x] "Add" text input + Add button for file paths and URLs
- [x] Remove (✕) per-item button; ▲/▼ buttons for reorder
- [x] "Loop" toggle checkbox
- [x] Current item highlighted in green; index shown (e.g. "Item 2 / 5")
- [x] `/fftv playlist add <path>` command appends an item
- [x] `/fftv playlist clear` command empties the list

### Network Sync
- [x] Host advances playlist on EndOfMedia, broadcasts play via existing SyncCoordinator.Play() (URL-only)
- [ ] Host broadcasts playlist state (items + index + loop flag) to clients on connect (future: Phase 4 enhancement)

---

## Phase 4 — Network Sync (Multi-Player)

Goal: Host PC serves a WebSocket server; all connected clients play the same content simultaneously.

### WebSocket Server (Host)
- [x] Research: chose System.Net.WebSockets + TcpListener (no urlacl/admin required)
- [x] Implement lightweight WebSocket server on configurable port
- [x] Settings window: Host/Client role dropdown, port input, connection count display
- [x] Server broadcasts JSON control messages: play, pause, resume, stop, seek
- [x] Screen config broadcast: cx, cy, cz, yaw, width, height synced to clients on connect + change
- [x] Heartbeat / ping to detect disconnected clients — Timer fires every 20s: broadcasts {type:"ping"}, prunes non-Open WebSockets; WebSocket keepAliveInterval=20s also sends protocol-level pings

### WebSocket Client (Viewers)
- [x] Settings window: address input, Connect/Disconnect button, status indicator
- [x] Client connects to host on button press
- [x] On `play` / `pause` / `resume` / `stop` / `seek`: execute immediately
- [x] On `screen`: update screen position/yaw/size from host
- [x] Auto-reconnect on disconnect with exponential backoff

### Host-side URL resolution
- [x] Host resolves YouTube URLs via yt-dlp before broadcasting (clients receive direct CDN URL)
- [x] Direct .mp4/.webm URLs broadcast as-is

### UPnP
- [x] SSDP discovery of IGD gateway
- [x] AddPortMapping / DeletePortMapping via SOAP
- [x] GetPublicIpAsync (ipify.org) shown in UI for clients to connect to
- [x] UPnP status shown in Host UI (green check when mapped)

### Volume / Mute
- [x] Volume slider (0–100), local only — not synced
- [x] Mute/Unmute button, local only — not synced
- [x] Volume and mute state persisted in config and restored on reload

### Testing
- [ ] Two remote clients see synchronized video (< 500ms drift)
- [ ] Plugin unload cleanly stops server / disconnects client

---

## Phase 5 — UI/UX Overhaul

- [ ] Remove Phase 1 Sandbox checkbox and all UsePhase1Sandbox code
- [x] Tint/opacity: add to D3D11 pixel shader (cbuffer b2) so it works for image AND video
- [ ] Add tab bar to main window: "Player" tab + "Network" tab
- [ ] Add gear button (⚙) → settings pop-out window containing: Always Draw, Black Backing, tint, yt-dlp path
- [ ] yt-dlp settings: show host-only warning note in settings window
- [ ] yt-dlp: auto-include in GitHub Actions build (download latest from GitHub releases)
- [ ] yt-dlp: startup check — compare bundled vs latest release, log if outdated
- [x] Client: only disable Screen Transform controls when actually connected to a host — not just because SyncMode == Client (fixes: client can't adjust rect while disconnected)
- [ ] Client controls: HIDE (not grey out) screen position/yaw/size/mode controls when host has locked them
  (currently uses BeginDisabled; change to conditional render — `if (!isClient)` blocks)

---

## Phase 6 — Rendering Quality Fixes

- [x] Fix UI occlusion: inject draw at 3D→2D pipeline transition via OMSetRenderTargets hook;
  ImGui callback kept as fallback if transition not detected
- [x] Improve sampler quality: anisotropic filtering (16x) for screens at steep angles
- [x] Revert UNorm_SRgb texture change (caused double-gamma darkening — FFXIV RT is linear UNorm)
- [x] Add per-screen brightness multiplier (PS shader cbuffer b2, slider 0.0–4.0, default 1.0)
- [x] Stop → show black backing rect instead of nothing (video mode with no texture draws backing)
- [x] Fix: stop/no-texture black rect MUST use D3D11 pipeline (not ImGui overlay) — ImGui has no depth and covers all UI
- [x] Fix: Stop shows gradient (not last video frame) — VideoPlayer.HasTexture returns false when VLCState.Stopped
- [x] Fix: gradient corners pop (not smooth) — was mapping sawtooth into [0.50–0.90] hue band causing hard jump at wrap; now uses full hue wheel (seamless) with low saturation (0.40) for soft pastel tones
- [x] Idle gradient screensaver: when no video is playing, render a slow animated gradient across the quad; each corner independently cycles through cool hues at a slightly different phase, so corners are always distinct colors; drawn via D3D11 (depth-tested)
- [ ] Add per-screen gamma/contrast controls (curves, not just multiply)
- [ ] World light emission from screen rect (area light injected into FFXIV deferred lighting pass — complex, game-version-sensitive)

---

## Phase 7 — Multiple Screens

- [ ] Refactor config: replace single `ScreenDefinition Screen` with `List<ScreenConfig> Screens`
- [ ] ScreenConfig struct: id (Guid), label, ScreenDefinition, ContentMode, ImagePath, VideoPath, VideoUrl, TintRGBA
- [ ] D3DRenderer: draw all visible screens per frame
- [ ] VideoPlayer pool: one VideoPlayer per screen in video mode
- [ ] MainWindow: screen list with add/remove/select, per-screen editing panel
- [ ] Network sync: all screen messages include screen ID
- [ ] `/fftv add` — create new screen; `/fftv remove <id>` — remove

---

## Phase 8 — Screen Capture / Streaming

- [ ] Research capture API: DXGI Desktop Duplication vs Windows.Graphics.Capture
- [ ] Add "Screen Capture" ContentMode
- [ ] Implement DXGI Desktop Duplication for full-screen or window capture
- [ ] Region selector UI (drag overlay or pixel coordinate inputs)
- [ ] Captured frames → BGRA pipeline → D3D11 dynamic texture (same as video)
- [ ] Host streams capture to clients: design relay approach (local HLS/RTMP vs raw frame relay)

---

## Phase 9 — Client Management (Nicknames, Permissions, Blacklist)

- [ ] Nickname field in settings; auto-fill from FFXIV character name if blank; cannot be blank
- [ ] First-time flow: hide all tabs until nickname is accepted ("Accept" button)
- [ ] Client sends nickname to host on connect; host and other clients receive it
- [ ] Host UI: "Connected Clients" list showing nicknames + IP
- [ ] Blacklist: host can ban client by nickname/IP; rejected on next connection attempt
- [ ] Permissions per client: host can grant/revoke control over screen position, playback, URL
- [ ] Permission changes broadcast to all clients
- [ ] Client hides (not greys) controls they don't have permission for
- [ ] Share room / co-host: multiple clients can have full host permissions

---

## Phase 10 — Audio Streaming

- [ ] Add "Audio" ContentMode: local audio file + online audio URL
- [ ] Audio-only playback via LibVLC (no texture, just audio decode)
- [ ] Sync audio play/pause/seek to clients (same protocol as video)
- [ ] Volume/mute controls apply to audio mode
- [ ] Host-to-client audio relay design (direct URL share vs stream relay)

---

## Phase 11 — In-game Right-click Integration

- [ ] Research Dalamud ContextMenu API for adding options on player right-click
- [ ] Detect if target player is running FFXIV-TV (via IPC advertisement or chat flag)
- [ ] Add right-click option "Connect to FFXIV-TV Host" on detected players
- [ ] Add right-click option "Invite to FFXIV-TV Lobby" when in host mode
- [ ] Invitation notification: target receives in-game prompt with accept/decline

---

## Known Limitations & Notes

- **Game native 2D UI (chat, map, hotbar) currently renders BEHIND the video screen.**
  Phase 6 fixes this by moving draw injection to the 3D→2D render pipeline transition.
- **Tint/opacity does nothing in Phase 2 mode** — the D3D shader does not apply it yet.
  Phase 5 adds tint to the pixel shader.
- **WorldToScreen clips at camera plane** — if any corner goes behind the camera,
  the entire screen stops rendering. Phase 2 handles proper frustum clipping.
- **Multi-player requires all viewers to run the plugin** — vanilla players cannot see screens.
- **Screen capture relay to clients not yet designed** — Phase 8 needs architecture decision.
