# FFXIV-TV Phase 3 — Video Playback Issues

Last updated: 2026-03-15

---

## What IS Working (entering Phase 3)

- D3D11 world-space quad renders correctly with proper depth testing
- Static image from disk loads and displays on the quad (`System.Drawing` → D3D11 SRV)
- Characters and geometry correctly occlude the screen; screen occludes geometry behind it
- `OMSetRenderTargets` vtable hook captures main-scene DSV reliably every frame
- State save/restore: ImGui pipeline unaffected by our draw calls
- Phase 1 fallback (ImGui overlay) still works when D3D11 unavailable

---

## Phase 3 Plan

Library: **LibVLCSharp** + `VideoLAN.LibVLC.Windows` NuGet (bundles libvlc DLLs).

LibVLC delivers raw BGRA frames via `SetVideoCallbacks` on a background thread.
Plugin copies pixels into a CPU-writable staging texture; render thread uploads to GPU.

```
LibVLC thread   → lock(_frameLock) → memcpy BGRA → _stagingPixels[] → _frameDirty=true
Render thread   → if _frameDirty: Map(staging) → memcpy → Unmap → CopyResource(staging→gpu) → _frameDirty=false
D3DRenderer     → bind gpu SRV (VideoPlayer active) else static image SRV
```

---

## Active Issues

None yet — implementation not started.
