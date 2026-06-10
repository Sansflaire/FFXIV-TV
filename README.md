# FFXIV-TV

A Dalamud plugin for FFXIV that renders a virtual screen in world space — capable of displaying
images and (eventually) live video/screen-share streams visible to all players running the plugin.

## Features (Planned by Phase)

### Phase 1 — World-Space Image Display
- Place a flat rectangle in 3D world space at any position/orientation
- Display a static image loaded from disk onto the rectangle
- Position/size controlled via in-game settings UI
- Uses IGameGui.WorldToScreen projection + ImGui background draw list

### Phase 2 — D3D11 Injection (Proper Depth)
- Hook game render pipeline to inject a textured quad pre-ImGui
- Proper depth testing: game characters and world geometry occlude the screen correctly
- Custom HLSL vertex + pixel shaders
- Access game camera view/projection matrices via FFXIVClientStructs

### Phase 3 — Video Playback
- Decode local video file frame-by-frame
- Upload frames to D3D11 texture dynamically each frame
- Playback controls (play, pause, seek) via in-game UI

### Phase 4 — Network Sync (Multi-Player)
- Host runs a lightweight WebSocket server locally
- Clients connect and receive URL / stream source
- Each client renders the stream independently
- All players with the plugin see the same video simultaneously

## Usage

```
/fftv        — Open main settings window
/fftv place  — Place screen at current player position
/fftv hide   — Toggle screen visibility
```

## Building

```bash
cd devPlugins/FFXIV-TV/src
dotnet build
```

DLL auto-copies to `devPlugins/FFXIV-TV/` after build.

## Author

Sansflaire
