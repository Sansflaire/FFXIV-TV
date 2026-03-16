# FFXIV-TV — Active Issues

Last updated: 2026-03-15

---

### Infinite retry loop when stream is unplayable (0 frames decoded)

**Symptom:** Status rapidly spams "Resolving URL..." → "Playing" → "Resolving URL..."
**Root cause:** `SyncCoordinator.HandleEndOfMedia()` unconditionally loops single-URL playback.
When LibVLC fires `EndReached` because a stream is unplayable (bad format, DASH not supported,
CDN reject), `FramesDecoded == 0`. The code doesn't distinguish a real end-of-media from a
failed stream, so it re-calls `Play(url)` forever.
**Fix:** Add `FramesDecoded` counter to `VideoPlayer` (reset each play, incremented in DisplayCallback).
In `HandleEndOfMedia`, if `_vp.FramesDecoded == 0` at end, the stream failed — stop, don't loop.
**Status:** Fixed in v0.5.14. Moved to Resolved.

---

## Resolved Issues

| Phase | Description | File |
|-------|-------------|------|
| Phase 1 / 2 | D3D11 rendering, UV fixes, depth testing | `Phase2-D3D-Rendering-Notes.md` |
| Phase 3 / 3.5 | LibVLC DLL loading, video callbacks, URL streaming, crash on mode switch | `Phase3-Video-Playback-Notes.md` |
| Phase 3.5 | Infinite retry loop when stream is unplayable (0 frames decoded) — fixed v0.5.14 | `SyncCoordinator.cs`, `VideoPlayer.cs` |
