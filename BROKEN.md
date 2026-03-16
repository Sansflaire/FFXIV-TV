# FFXIV-TV — Active Issues

Last updated: 2026-03-15 (v0.5.25)

---

## Active Issue — Chat box background renders behind rect

**Symptom:** The rect is correctly behind 3D-world characters (depth testing works), and chat text
renders in front of the rect. However, the dark chat-box panel background is NOT visible — the rect
color shows through where the background should be, making chat harder to read.

**Hypothesis:** FFXIV's 2D pass rendering order is not fully uniform. The chat panel background
appears to draw at a different point in the pipeline than the chat text. The current injection point
(immediately after `ClearRenderTargetView` on the backbuffer) may fire AFTER the chat background
has already been drawn to the backbuffer, meaning our rect overwrites it. The chat text then draws
on top of our rect, which is why text is readable but the background is gone.

**What was tried:**
- v0.5.18: OMSetRenderTargets hook at 3D→2D transition (first no-DSV call). Fired to intermediate
  FFXIV RT, not backbuffer — draw was invisible.
- v0.5.23: Learn backbuffer ptr from `ExecuteDrawCallback`, fire OMSetRenderTargets hook when
  that exact ptr is bound. Correct surface but FFXIV calls ClearRenderTargetView after our draw,
  wiping our pixels.
- v0.5.24–v0.5.25: Hook `ClearRenderTargetView` (vtable[50]); call Original first, then draw
  immediately after. Rect is now stable and visible; depth testing works. BUT: chat background
  appears behind rect (above hypothesis).

**Next to try:**
- Add a log counter to ClearRTV detour to see how many times it fires per frame and at what point
  in the frame each fire happens. If it fires more than once and our `_frameInjectionDone` flag
  prevents re-injection on the second fire, we may need to inject on a LATER ClearRTV call.
- Alternatively: try hooking `IDXGISwapChain::Present` (vtable[8]) and drawing just before present
  — this fires after ALL FFXIV rendering including 2D UI, which would mean we're on top of
  everything (chat, hotbar, map) but would lose depth-testing against 3D geometry.

---

## Resolved Issues (v0.5.24 – v0.5.25)

| Version | Description | Root Cause | Fix |
|---------|-------------|------------|-----|
| v0.5.25 | **Rect flickering rapidly (30 Hz blink)** | FFXIV swapchain double-buffering: FLIP_DISCARD rotates through backbuffer RTV pointers A and B each frame. `_knownBackbufferRtvPtr` (single nint) only captured the first pointer seen. On B-frames, ClearRTV hook condition failed → no injection → `_frameInjectionDone` still true (set by fallback on A-frames) → Draw() skipped fallback too → TV invisible every B-frame → 50% duty cycle = 30 Hz blink. Also: `_frameInjectionDone = true` in `ExecuteDrawCallback` blocked the fallback from running on frames where inline missed, preventing new pointers from being learned. | Replace `_knownBackbufferRtvPtr: nint` with `_knownBackbufferRtvPtrs: HashSet<nint>`. Remove `_frameInjectionDone = true` from `ExecuteDrawCallback` so fallback runs freely and adds all backbuffer pointers to the set. ClearRTV hook uses `_knownBackbufferRtvPtrs.Contains(pRTV)`. |
| v0.5.24 | **Rect not displaying (ClearRTV wipes inline draw)** | Inline draw at backbuffer bind point (OMSetRenderTargets hook, learned from ExecuteDrawCallback) was correct surface but FFXIV calls `ClearRenderTargetView(backbuffer, black)` after binding it, wiping our pixels before the frame is presented. | Added `ClearRenderTargetView` hook at vtable[50]. Call `Original` first (do the clear), then draw immediately after — FFXIV will not clear again after this point in the frame. |

## Resolved Issues (v0.5.21 – v0.5.23)

| Version | Description | Root Cause | Fix |
|---------|-------------|------------|-----|
| v0.5.23 | **Rect no longer displays when UI-layering fix active** | The `ppRTVs[0]` at the first no-DSV OMSetRenderTargets transition (v0.5.22's injection point) is an **intermediate FFXIV render target** (post-processing stage), not the final backbuffer. FFXIV's pipeline overwrites it before the frame is presented. ~50,000 inline draws produced nothing visible. The fallback path (ImGui-time `OMGetRenderTargets`) does reach the actual backbuffer. | Instead of firing at the first no-DSV transition, learn the real backbuffer RTV pointer from `ExecuteDrawCallback` (`OMGetRenderTargets` at ImGui time → saved as `_knownBackbufferRtvPtr`). Hook watches for that exact pointer with `pDSV==0`. Inline draw fires there — before native 2D UI draws, to the actual backbuffer. First frame uses fallback to bootstrap the pointer; frame 2+ uses inline. |
| v0.5.22 | **Game UI (chat/hotbar/map) still rendering over rect (v0.5.21 fix insufficient)** | `_injectedThisTransition` was reset to `false` by intermediate DSV calls during FFXIV's 2D pass. By the time `Draw()` ran, the flag was already false → fallback added regardless → fired after native UI → covered chat/hotbar/map. | Added `volatile bool _frameInjectionDone` — set by inline draw, only reset by `Draw()`. Immune to intermediate DSV resets. Used in both the inline draw condition (`!_frameInjectionDone` prevents re-injection) and in `Draw()` (skip fallback if inline fired). |
| v0.5.21 | **Game UI (chat/hotbar/map) rendering over rect** | Fallback `ImGui.GetBackgroundDrawList().AddCallback()` fires during ImGui phase, after native 2D UI, covering chat/hotbar/map. `_injectedThisTransition` check in `Draw()` failed to suppress it because intermediate DSV calls reset the flag before `Draw()` ran. | (Partial fix — see v0.5.22.) |

---

## Resolved Issues (v0.5.18 – v0.5.20)

| Version | Description | Root Cause | Fix |
|---------|-------------|------------|-----|
| v0.5.20 | **Game crash — OMSetRenderTargetsDetour: RTV COM refcount stolen each frame** | `new ID3D11RenderTargetView(ppRTVs[0])` creates a Vortice wrapper WITHOUT calling AddRef. `ExecuteInlineDraw` called `rtv.Dispose()` → COM `Release()` every frame, decrementing the game's own RTV refcount. After N frames (initial refcount was ~3: game ref + D3D internal binding ref) the COM object hit refcount 0, D3D freed it, game dereferenced freed pointer → hardware AV, uncatchable by try/catch. | `AddRef()` on wrapper immediately after creation; `Dispose()` only in detour's `finally` (balanced); removed `rtv.Dispose()` from `ExecuteInlineDraw` (caller now owns lifetime). |
| v0.5.19 | **Game crash — OMSetRenderTargetsDetour: managed exceptions escaped to native** | Entire detour body was unguarded. Any managed exception (from COM wrappers, string formatting, etc.) propagated through Dalamud's native hook trampoline frames, which don't handle CLR exception propagation — game terminates. Also used `_omSetRTHook!.Original` (null-forgiving on hook field, explicitly prohibited in CLAUDE.md). | Wrapped entire detour body in `try/catch(Exception)`; moved `_omSetRTHook?.Original(...)` to `finally` block so the game's D3D call always completes. |
| v0.5.18 | **Game UI (chat/hotbar/map) hidden behind video rect** | `Draw()` (which set `_cachedDrawReady = true`) runs in Dalamud's ImGui phase, which fires AFTER the 3D→2D render transition each frame. The inline draw was gated on `_cachedDrawReady`, so it was always false at the transition. The fallback ImGui background draw list fired instead — it draws AFTER native UI, covering chat/hotbar/map. | Removed `_cachedDrawReady` gate from the inline draw condition. Use previous-frame `_activeSrv`/`_storedScreen` instead. Added `_initialized && _dsReverseZ != null && _dsNoDepth != null && _cbParams != null` guards so inline draw only fires when all D3D resources are ready. |
| v0.5.18 | **Game crash — inline draw fired before D3D resources initialized** | First fix attempt removed `_cachedDrawReady` gate without adding `_initialized`/resource null guards. The detour fired immediately on the first frame before `CreateResources()` had populated `_dsReverseZ`, `_dsNoDepth`, `_cbParams`. | Added explicit null checks for all D3D resources in the inline draw condition. |
| v0.5.18 | **YouTube URL/Stream mode: resolves → plays → instantly stops (0 frames)** | Two causes: (1) yt-dlp requires `--js-runtimes node` to extract YouTube URLs; deno is default but not installed — node v22 is at `C:\Program Files\nodejs\node.exe`. (2) YouTube's yt-dlp `-j` JSON does NOT hoist stream URL to top-level `url`/`manifest_url` — URL is inside `formats[].url` and must be found by scanning for best merged format (acodec≠none AND vcodec≠none). | Added `--js-runtimes node` to both yt-dlp `-j` and pipe commands. Added `formats[]` fallback scan in VideoPlayer JSON parsing. |
| v0.5.18 | **Browser (WebView2) silently does nothing after Stop() — status stays "Stopped"** | `Stop()` cleared `_webViewReady = false`. `Navigate()` checked `_webViewReady` and bailed early. WebView2 was still running and ready to navigate, but the flag said otherwise. | Removed `_webViewReady = false` from `Stop()`. Changed `Navigate()` to check `_webView?.CoreWebView2 != null` instead of `_webViewReady`. |
| v0.5.18 | **Browser (WebView2) shows bot-check / "I'm not a robot" page on Reddit/YouTube** | WebView2 default User-Agent identifies the browser as an embedded/automation WebView. Sites detect this and redirect to bot-check pages. | Spoof Edge UA via `_webView.CoreWebView2.Settings.UserAgent` (`Mozilla/5.0 ... Edg/131.0.0.0`). |
| v0.5.18 | **Browser (WebView2) fails with BadImageFormatException on init** | NuGet's `ReferenceCopyLocalPaths` copies the x86 `WebView2Loader.dll` from the package. FFXIV is an x64 process. P/Invoke searches the game exe directory first; when it found an x86 DLL, loading it threw `BadImageFormatException`. | Exclude `WebView2Loader.dll` from `_RefsCopy` in csproj; copy the x64 version from `runtimes/win-x64/native/` after all other copies. Preload it via `NativeLibrary.Load(loaderPath)` in `BrowserPlayer` before WebView2 environment creation so the already-loaded x64 module is used by subsequent P/Invoke calls. |
| v0.5.18 | **⚙ settings button corrupts ALL Dalamud text (global ImGui style stack)** | `DrawScreenSection()` read `_settingsOpen` for both the `PushStyleColor` guard and the `PopStyleColor` guard, but the button click mutated `_settingsOpen` between the two checks. An unmatched `PopStyleColor` corrupts the global ImGui style stack for that frame and all subsequent frames — breaks text rendering in ALL plugins until game restart. | Snapshot `bool settingsWasOpen = _settingsOpen` before the button call. Use `settingsWasOpen` for both Push and Pop guards. |
| v0.5.18 | **Reddit URLs play video-only (no audio) in URL/Stream mode** | Reddit serves DASH streams with separate audio/video tracks. yt-dlp returns `acodec=none` for the video track. LibVLC can't merge separate DASH A/V. | Detect `acodec=none` in yt-dlp JSON → use `PlayViaPipeAsync` to pipe yt-dlp's merged A/V output directly to LibVLC via `StreamMediaInput`. |
| v0.5.18 | **BrowserPlayer init error type hidden (only message logged)** | Generic `catch (Exception ex)` only logged `ex.Message`, losing the exception type needed for diagnosis (e.g. distinguishing `BadImageFormatException` from `WebView2RuntimeNotFoundException`). | Log `ex.GetType().Name` + `ex.Message`. Also retry env creation with process-unique folder on failure (avoids locked folder from previous plugin instance on hot-reload). |

---

## Resolved Issues (older)

| Phase | Description | File |
|-------|-------------|------|
| Phase 1 / 2 | D3D11 rendering, UV fixes, depth testing | `Phase2-D3D-Rendering-Notes.md` |
| Phase 3 / 3.5 | LibVLC DLL loading, video callbacks, URL streaming, crash on mode switch | `Phase3-Video-Playback-Notes.md` |
| Phase 3.5 | Infinite retry loop when stream is unplayable (0 frames decoded) — fixed v0.5.14 | `SyncCoordinator.cs`, `VideoPlayer.cs` |
