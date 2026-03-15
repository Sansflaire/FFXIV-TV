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
- Phase 3 infrastructure: VideoPlayer.cs, Plugin.cs wiring, MainWindow video controls — all compile

---

## Phase 3 Plan

Library: **LibVLCSharp** + `VideoLAN.LibVLC.Windows` NuGet (bundles libvlc DLLs).

LibVLC delivers raw BGRA frames via `SetVideoCallbacks` on a background thread.
Plugin writes pixels into a pinned CPU buffer; render thread uploads to GPU dynamic texture.

```
LibVLC thread   → LockCallback pins _pixels[] for write → UnlockCallback clears _vlcWriting
LibVLC thread   → DisplayCallback sets _frameDirty = true
Render thread   → if _frameDirty && !_vlcWriting: Map(WriteDiscard) → memcpy → Unmap → _frameDirty=false
D3DRenderer     → prefer VideoPlayer.FrameSrv when HasTexture; else static image SRV
```

---

## Active Issues

### BUG: Plugin failed to load — `DllNotFoundException: libvlc`

**Status: FIXED (2026-03-15)**

**Symptom:** Plugin showed "load error" in `/xlplugins`. Log showed:
```
System.DllNotFoundException: Unable to load DLL 'libvlc' or one of its dependencies
  at LibVLCSharp.Shared.Core.Initialize(String libvlcDirectoryPath)
  at FFXIVTv.VideoPlayer..ctor(String pluginDir)
```

**Root cause:** The `VideoLAN.LibVLC.Windows` NuGet package places native DLLs at
`bin/x64/Debug/libvlc/win-x64/libvlc.dll` — NOT at `bin/x64/Debug/libvlc.dll`.
Our csproj post-build target looked for `$(TargetDir)libvlc.dll` (wrong path), so
`libvlc.dll` was never copied to `devPlugins/FFXIV-TV/`.

**Fix:** Updated csproj `_LibVlcNative` glob to `$(TargetDir)libvlc\win-x64\libvlc.dll`
and `_LibVlcPlugins` to `$(TargetDir)libvlc\win-x64\plugins\**\*`. Rebuild now copies
`libvlc.dll`, `libvlccore.dll`, and the `plugins/` tree to the plugin dir.

---

## Resolved Issues (Phase 2)

See `Phase2-D3D-Rendering-Notes.md` for the full Phase 2 debug history (Rounds 1–22).

Key Phase 2 wins:
- R17: Bilinear UV sign fix (`-Cv/Bv` not `Cv/Bv`)
- R18: Image confirmed displaying on quad
- R22: DSV captured via OMSetRenderTargets hook, depth testing working
