# FFXIV-TV — Active Issues

Last updated: 2026-03-15

---

## Phase 3 — Video Playback (Local File)

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

## Phase 3.5 — URL / Web Video

### BUG: Video shows magenta placeholder, depth testing broken

**Status: FIXED (2026-03-15)**

**Symptom:** Switching to Local Video mode shows the magenta ImGui placeholder instead of
video frames. Audio plays correctly (LibVLC is decoding) but no pixels appear on screen.
The placeholder also draws OVER the character (no depth testing).

**Root cause:** `Plugin.OnDraw()` was changed to clear `ImagePath` in video mode
(`SetImagePath("")`). With no image SRV and no video SRV yet (first frame hasn't arrived),
`D3DRenderer.HasTexture` is false. The fallback path calls `_screenRenderer.DrawPlaceholder()`
which uses the ImGui overlay — no depth testing. Worse, `D3DRenderer.Draw()` is never
called, so `VideoPlayer.UploadFrame()` never runs, so `EnsureTexture()` never creates the
GPU texture. The video SRV stays null forever — video frames can never appear.

**Fix:** In video modes, always call `D3DRenderer.Draw()` regardless of `HasTexture`, so
`UploadFrame()` runs and can create the texture on the first frame. Only use the ImGui
placeholder in Image mode when no image is loaded.

### BUG: Game crash when switching from local video to URL video while playing

**Status: FIXED (2026-03-15)**

**Symptom:** Game crashed when playing a local video, switching to URL mode, and clicking
Play before the local video had stopped.

**Root causes (three races):**

1. **Stale async task / double StartPlayback**: `Play(local)` launches a background task
   awaiting `media.Parse()`. User calls `Play(url)` → `Stop()` → new task launched. When
   the old task's parse finishes, both tasks call `StartPlayback()` concurrently → both
   call `AllocatePixelBuffer()` → second call frees the pinned GCHandle while first task
   still holds it → heap corruption crash.

2. **Forced `_vlcWriting = false` in Stop()**: `Stop()` was directly clearing `_vlcWriting`
   while LibVLC's decode thread might still be between `LockCallback` (buffer pointer live)
   and `UnlockCallback`. This let `UploadFrame()` memcpy into a buffer LibVLC was actively
   writing → memory corruption.

3. **`OnEndReached` loop race**: End-of-file restart could race with a new `Play()` call,
   both trying to stop/start the player concurrently.

**Fix:**
- `_playVersion` counter (Interlocked.Increment). Every `Play()` increments it; all async
  tasks check the version before `StartPlayback()` and abort if stale. `Dispose()` also
  increments to cancel in-flight tasks.
- Removed forced `_vlcWriting = false` from `Stop()`. Added spin-wait (500ms timeout)
  after `_player.Stop()` to let the current LibVLC callback complete naturally.
- `OnEndReached` captures version at fire time and aborts if it has since changed.

---

### What we know going in

- LibVLC natively supports HTTP/HTTPS video URLs — `new Media(_libVlc, new Uri(url))` works
  for direct `.mp4`, `.m3u8` (HLS), `.webm`, RTMP, etc. No extra plugins needed.
- `media.Parse(MediaParseOptions.ParseNetwork)` must be used for URLs instead of `ParseLocal`.
- YouTube URLs are NOT direct video URLs — the actual stream must be extracted first.
  Tool of choice: **yt-dlp** (`yt-dlp --get-url --format best <url>` → direct stream URL).
- `VideoPlayer.Play()` currently calls `File.Exists(path)` and rejects anything that isn't
  a local file. It must be updated to detect URLs and take a different code path.

### Plan

```
User input: "https://..." or "C:\path\to\file.mp4"
                │
           Is it a URL?  (starts with http:// or https://)
          ┌────┴────┐
         Yes        No
          │          └─ existing local-file path (File.Exists check)
     Is YouTube?
    ┌─────┴─────┐
   Yes          No
    │            └─ pass URL directly to LibVLC (ParseNetwork)
  run yt-dlp --get-url
    └─ use extracted URL with LibVLC
```

yt-dlp lookup order:
1. Plugin directory (`devPlugins/FFXIV-TV/yt-dlp.exe`) — user drops it in
2. Configurable path in Configuration (`YtDlpPath`)
3. System PATH fallback

---

## Resolved Issues (Phase 1 / Phase 2)

See `Phase2-D3D-Rendering-Notes.md` for the full Phase 2 debug history (Rounds 1–22).

Key Phase 2 wins:
- R17: Bilinear UV sign fix (`-Cv/Bv` not `Cv/Bv`)
- R18: Image confirmed displaying on quad
- R22: DSV captured via OMSetRenderTargets hook, depth testing working

## What Was Working at End of Phase 3

- D3D11 world-space quad renders correctly with proper depth testing
- Static image from disk loads and displays on the quad (`System.Drawing` → D3D11 SRV)
- Characters and geometry correctly occlude the screen; screen occludes geometry behind it
- `OMSetRenderTargets` vtable hook captures main-scene DSV reliably every frame
- State save/restore: ImGui pipeline unaffected by our draw calls
- Phase 1 fallback (ImGui overlay) still works when D3D11 unavailable
- Phase 3 infrastructure: VideoPlayer.cs, Plugin.cs wiring, MainWindow video controls — all compile
- LibVLC DLL loading: fixed csproj paths, DLLs copy correctly to plugin dir
- Local video file playback via LibVLC working (BGRA callback → dynamic D3D11 texture → render)
