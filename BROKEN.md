# FFXIV-TV — Active Issues

Last updated: 2026-07-11 (v0.5.219)

---

## PyonPixRenderer (Phase 6, v0.5.224+) — Bug Journal

### v0.5.235 — DEADLOCK during hot-reload (game hung, crashhandler killed it)

**Symptom**: Trist stepped away from the PC while v0.5.234 was actively drawing
in PyonPix mode. Came back to a fully frozen FFXIV window. No crash dump. No
new dalamud.log entries after `01:09:22 yt-dlp complete`. Crash handler killed
the process at `02:15:48` with `ERROR_BROKEN_PIPE (0x6D)` — the crash handler
couldn't read exception info because there was NO exception, just an
unresponsive render thread.

**Root cause** (inferred, not directly captured because there was no dump):

While Trist was away, my `dotnet build` command dropped v0.5.235.dll on disk.
Dalamud's file-change watcher scheduled a plugin reload. During the reload:

1. `Plugin.Dispose()` runs on the render thread.
2. It calls `_pyonPix.Dispose()` which calls `_omSetRtHook.Disable()` then
   `_omSetRtHook.Dispose()`.
3. Reloaded.Hooks' `Disable()` waits for any in-flight detour to complete
   before removing the JMP patch.
4. If the render thread was mid-detour at the exact instant of Dispose, and
   our detour ended with a call to `_omSetRtHook!.Original(...)` (the RTV
   restore we added in v0.5.231), that Original call could race with the
   hook teardown — leading to either a deadlock (both waiting on the other)
   or a jump to freed trampoline code.
5. Render thread stalls indefinitely. Game window stops updating. StatusApi
   background thread stays alive so /pyonpix and /version still respond,
   masking the hang.

**Fix (v0.5.236)**:
- Add a `Plugin.OnDraw` safety guard that force-flips `RenderMode = Legacy`
  the moment a saved config with `RenderMode = PyonPix` loads. This prevents
  `_pyonPix.TryInitialize()` from ever installing the OMSetRT hook until the
  underlying race condition is fixed.
- No functional PyonPix change yet — the hook install itself is fine; the
  problem is teardown-during-detour.
- To eventually re-enable PyonPix safely, the detour must either:
  1. Skip the RTV restore entirely (accept some game-side visual corruption
     as a cost of safety)
  2. Use a semaphore that Dispose can wait on before starting hook teardown
  3. Never call `Original(...)` after the primary hook body

**Lessons**:
- **Never call `_hook.Original(...)` from a non-trivial code path in a
  detour that outlives the primary hook body.** The RTV restore was
  well-motivated (prevent state corruption of the game's subsequent draws)
  but it turns the detour into a nested Original call, which is exactly the
  window where a hot-reload teardown can race.
- **A hang looks nothing like a crash in the logs.** No exception, no dump,
  just a broken pipe when the crash handler eventually gives up. Search
  criteria: `dalamud.log` stops updating + `crashhandler.log` has
  `Failed to read exception information` + `Terminating target process`.
- **Hot-reload triggered by unattended builds is a real risk.** Not just
  "you'll lose a session" — an unattended reload while the detour is
  in-flight bricks the game. Consider requiring an explicit user action
  (config setting, environment flag) before allowing hot-reload of a
  plugin that installs render-path hooks.

---

### v0.5.224 — HARD CRASH (Trist's game killed on plugin load)

**Symptom**: The moment `Config.RenderMode == RenderingMode.PyonPix` (the new default)
and `Plugin.OnDraw()` fired for the first time after the v0.5.224 DLL loaded,
`_pyonPix.TryInitialize()` was called → `InstallHooks(ctxPtr)` → and the game
process died with `System.AccessViolationException`. No FFXIV window, no
Dalamud recovery — full crash to desktop, dump file written to
`%AppData%\XIVLauncher\dalamud_appcrash_*.log` + `.dmp`.

**Root cause** (from crash dump call stack):
```
System.AccessViolationException: Attempted to read or write protected memory
   at Reloaded.Memory.Sources.Memory.Read[UIntPtr]
   at Reloaded.Hooks.Internal.FunctionPatcher.GetRewriteRIPRelativeJumpTarget
   at Reloaded.Hooks.Internal.FunctionPatcher.PatchRIPRelativeJump
   at Reloaded.Hooks.Hook.CreateHook
   at Dalamud.Hooking.HookFromAddress<PresentDelegate>
   at FFXIVTv.PyonPixRenderer.InstallHooks
```

I tried to hook `IDXGISwapChain::Present` (vtable slot 8). **Dalamud already
hooks that function** — the crash-time game-side call stack shows Dalamud's
own `DxgiSwapChainPresentDetour` at frame [32]. When my `HookFromAddress<T>`
called `Reloaded.Hooks` to install a chained hook on top, its `FunctionPatcher`
tried to disassemble the target function's instructions to relocate any
RIP-relative jumps for the trampoline. Dalamud's existing hook has replaced
that memory with structures the analyzer can't safely walk — the memory read
hit protected pages and the AV killed the process.

**Why the OMSetRenderTargets hook (slot 33) worked**: Dalamud does NOT hook
that slot, so my chained hook was on top of the game's raw vtable entry and
`FunctionPatcher` could analyze it cleanly.

**Fix (v0.5.226)**:
- Remove the `Present` hook entirely.
- PyonPix used it only as a frame counter (`PresentIndex++`).
- Add `PyonPixRenderer.IncrementFrameCounter()` and call it from
  `Plugin.OnDraw` — same functional behavior for the RTV-scoring recency
  bonus, zero hook conflict.
- OMSetRenderTargets hook stays (it works and it's the actual peer-portability
  mechanism).

**Immediate remediation (v0.5.225, no functional change)**: flipped
`Configuration.RenderMode` default to `Legacy` and added a `Plugin.OnDraw`
force-off so any user with the crashed 0.5.224 DLL cached could reload
safely into the legacy hook path.

**The exact mechanism**:

1. `HookFromAddress<T>(addr, detour)` uses `Reloaded.Hooks` under the hood.
2. To install a hook, Reloaded writes a `JMP` at `addr` to the detour. But
   that overwrites the first few bytes of the target function — those bytes
   need to still execute somewhere, so Reloaded copies them into a
   **trampoline** and rewrites any RIP-relative jumps in them to point back
   into the original function's original location.
3. To do that rewriting, `FunctionPatcher.GetRewriteRIPRelativeJumpTarget`
   calls `Reloaded.Memory.Sources.Memory.Read` to walk the target function's
   instructions with Iced.
4. Dalamud had already hooked Present. At `addr` there was no longer the
   original `Present` prologue — there was **Dalamud's own trampoline**,
   which contains structures Reloaded doesn't recognize. When Iced tried to
   disassemble past the end of what Reloaded expected, it read into
   protected memory.
5. That memory read is a native `MOV` — no `try/catch` can catch it. The
   CLR sees an `AccessViolationException`, propagates it up the stack, and
   the process dies because AV is fatal by default in the runtime.

**The reason OMSetRenderTargets (slot 33) worked**: Dalamud doesn't hook
slot 33. Reloaded found the raw D3D11 vtable entry pointing at the game's
own OMSetRT code, which Iced knows how to disassemble cleanly.

**The reason the fix (v0.5.226) works**: we don't hook Present at all.
PyonPix hooked it only to increment `PresentIndex` once per frame for
RTV-scoring recency bonuses. `Plugin.OnDraw` fires once per frame too —
Dalamud invokes it from *inside* Dalamud's own Present hook, which is
exactly why `HookFromAddress<PresentDelegate>` conflicted in the first
place. Incrementing our counter from `OnDraw` gives us the identical
per-frame tick without touching the vtable.

---

**PERMANENT RULE — hook only slots Dalamud does not touch**

Before installing ANY vtable hook via `HookFromAddress` or `HookFromSignature`
on a D3D-adjacent object (`ID3D11Device`, `ID3D11DeviceContext`,
`IDXGISwapChain`, `IDXGIFactory*`), verify Dalamud does not already hook
that slot. Chaining Reloaded.Hooks on top of Dalamud's own hook is not a
"maybe it works" situation — it is a **guaranteed process-terminating AV**
that will kill the game every time, and no `try/catch` can prevent it
because the fault is in native disassembler code.

**Known-Dalamud-hooked functions as of Dalamud v15.0.2.2 — DO NOT HOOK**:

| Target | Slot | Dalamud detour (from crash stack) |
|---|---|---|
| `IDXGISwapChain::Present` | 8 | `InterfaceManager.DxgiSwapChainPresentDetour` |
| `IDXGISwapChain::ResizeBuffers` | 13 | `InterfaceManager.ResizeBuffersDetour` (likely) |

**Confirmed-safe D3D vtable slots (used by shipping FFXIV-TV code)**:

| Target | Slot | Notes |
|---|---|---|
| `ID3D11DeviceContext::DrawIndexed` | 12 | Used by D3DRenderer, works chained |
| `ID3D11DeviceContext::Draw` | 13 | Used by D3DRenderer, works chained |
| `ID3D11DeviceContext::OMSetRenderTargets` | 33 | Used by D3DRenderer + PyonPixRenderer |
| `ID3D11DeviceContext::ClearRenderTargetView` | 50 | Used by D3DRenderer |
| `ID3D11DeviceContext::Dispatch` | 41 | Used by D3DRenderer |

**How to check a candidate slot before you hook it**:
1. Search Dalamud source (or crash dumps from other people) for
   `InterfaceManager.*Detour` names.
2. Enable the plugin with an intentional crash-early code path
   (`throw new Exception("about to hook X")` right before the
   `HookFromAddress`) — if the game survives that throw but crashes on
   your `HookFromAddress` call, the slot is Dalamud-hooked.
3. Look at the crash dump call stack. If you see any
   `Dalamud.Interface.Internal.InterfaceManager.*Detour` above your
   `InstallHooks`, that's the confirmation.

**How to substitute when the slot you want is Dalamud-hooked**:
- Present → use `UiBuilder.Draw` for frame ticks (fires once per frame
  inside Dalamud's Present detour anyway).
- ResizeBuffers → poll `Device.Instance()->SwapChain->BackBuffer` pointer
  each frame; when it changes, treat as resize.

**Lessons**:
- **Crash dumps are the fastest diagnostic.** The dalamud.log only
  contained events up to yt-dlp completion — the plugin never wrote its
  "PyonPixRenderer initialized" log line because the AV happened before
  the log could flush. `dalamud_appcrash_*.log` had the full CoreCLR call
  stack **plus** the game-side native stack, which independently confirmed
  Dalamud's Present hook was live at the moment of the crash.
- **AV in Reloaded.Hooks' `FunctionPatcher` is uncatchable.** The frames
  above your managed code are pure native disassembly walking. There is
  no `try/catch` you can wrap around `HookFromAddress` that will save
  you — the process dies before the throw returns.

---

## CopyBlitRenderer (Phase 5, v0.5.216+) — Bug Journal

Purpose of the renderer: mirror XivMediaPlayer's no-hooks pipeline so peers see the TV
correctly (the CF-DI hook path only draws right on the machine whose GPU/settings match
the pattern-matched inject moment; on peers it comes out invisible or as a "faint
grayscale shadow over dark colors" — that is HDR pre-tonemap injection into the wrong
surface, and the whole point of Phase 5 is to sidestep the hook entirely).

### v0.5.216 — Diagonal wedge / stripe across the screen (missing `row_major`)

**Symptom**: TV rendered as a thin diagonal stripe running from one corner to another
across the whole screen — not a rectangle. Also a full-screen colored gradient wedge
tapering to a vanishing point.

**Root cause**: HLSL cbuffer declared `float4x4 InvViewProj; float4x4 ViewProj;`
without the `row_major` keyword. `.NET Matrix4x4` stores in row-major memory; HLSL
defaults to column-major when reading from a constant buffer, so it saw the transpose
of both matrices. Ray reconstruction (`mul(ndc, InvViewProj)`) produced garbage
directions for most pixels; only the narrow subset of pixels whose garbage-rays
happened to graze the TV plane ended up filling — hence the stripe.

**Fix**: Add `row_major` to both matrix declarations in the cbuffer (matches
D3DRenderer.cs:495-496).

### v0.5.217 — Full-screen black (M44=0 → forced-invert produced degenerate InvViewProj)

**Symptom**: Whole screen goes black once the CopyBlit path activates. Not the
game — an opaque black rectangle covering the entire viewport.

**Diagnosis**: Swapped the shader for a solid-red output. Result was solid-red
covering the whole screen (v0.5.218) — proving:
1. RTV/blit pipeline works end-to-end.
2. Ray-plane u/v gate was passing on every pixel — meaning the ray reconstruction
   was producing hits at valid u/v for every ray direction.

**Root cause**: FFXIV's `Control.Instance()->ViewProjectionMatrix` has `M44 = 0`
(standard perspective-matrix layout in .NET row-major memory). I "fixed" that by
setting `M44 = 1` before calling `Matrix4x4.Invert`, but this turns a valid
perspective matrix into a DIFFERENT (invertible but wrong) matrix. Its inverse maps
every NDC pixel to a fixed cluster of world points that all lie inside the TV plane's
u/v rectangle. Solid black in the earlier version was the same broken u/v producing
the placeholder gradient which then got zeroed by an alpha/premultiply cascade.

**Fix (v0.5.219)**: Abandon the ray-plane / InvViewProj approach entirely. Switch to
D3DRenderer's proven convention — a real 6-vertex quad emitted from `SV_VertexID`,
transformed `local → world → clip` in the VS. The rasterizer only fills pixels
covered by the projected TV quad; everything outside stays at the transparent clear
color, which ImGui's straight-alpha blit passes through as the underlying game.

Kept the XMP-style parts that don't need InvViewProj:
- CopyResource depth capture from `RenderTargetManager.Instance()->DepthStencil`
- 5x5 Gaussian PCF depth compare in the PS against the depth SRV
- Offscreen RTV owned by the plugin, blitted via `ImGui.AddImageQuad`

The remaining "no hooks / everything plugin-owned" property (which is what makes it
render correctly on peers) is preserved. Only the ray-plane math is gone.

### Lessons

- **Never patch matrix elements to "make Invert work"**. If a matrix's M44=0 makes
  it appear non-invertible, it usually means the matrix is designed for column-vector
  math and .NET's row-vector `Invert` is confused, OR the matrix legitimately IS a
  perspective matrix whose M44 SHOULD be 0. Either way, the fix is to change the
  convention, not corrupt the input.
- **Add `row_major` to every cbuffer matrix** in this codebase — .NET Matrix4x4 is
  row-major memory but HLSL defaults column-major, and without the keyword the shader
  silently reads transposes. This burned D3DRenderer years ago too (see line 495).
- **Debug by tracing the pipeline top-down**. When a shader shows an unexpected
  shape, before touching math swap the PS body for a solid color — that isolates
  RTV/blit correctness from shader correctness in one build.

---

## CURRENT STATUS (v0.5.168) — ✅ NETWORK SYNC WORKING (cloudflared tunnel)

### Checkpoint: v0.5.168 — Working Multi-Player Sync via cloudflared

**Host flow**: Press "Create Room" → cloudflared binary auto-downloads (~30 MB, once) to plugin dir
→ subprocess spawns, connects outbound to Cloudflare → public `wss://xxx.trycloudflare.com` URL
appears in UI → click to copy.

**Client flow**: Paste tunnel URL (or LAN `IP:port` for same-network) into address field → Connect.
No firewall rules, no port forwarding, no account, no config. Just works.

**State-on-connect**: Host sends `screen` message + current playback state (`play` + seek position,
or `stop`) to each client the moment they connect, so late-joining clients immediately sync.

**Local file serving through tunnel**: `BuildFileUrl()` converts `wss://xxx.trycloudflare.com` →
`https://xxx.trycloudflare.com/file` so clients stream local video files through the tunnel.
For direct TCP ("LAN Only" mode), files are served as `http://HOST:PORT/file` with Range support.

**What was tried and discarded before this:**
- Direct TCP + UPnP: UPnP not found on Trist's router; Windows Firewall blocked inbound TCP.
  Users would need manual firewall rules — not acceptable for general use.
- fly.io relay server: required credit card, no free tier without payment.
- koyeb.com relay server: same, required credit card.
- cloudflared quick tunnels: free, outbound-only, no account, auto-downloads binary. ✅

**Known limitations of cloudflared tunnels:**
- Tunnel URL changes on every "Create Room" press (new ephemeral URL each time).
- ~5–10 second wait while cloudflared starts and connects (shown as "Starting..." in UI).
- Bandwidth is Cloudflare's free tier — may throttle large local video file transfers.
- `*.trycloudflare.com` tunnels are ephemeral (no custom domain). For persistent rooms, user
  would need a cloudflared account + named tunnel (not yet implemented).

---

## CURRENT STATUS (v0.5.164) — ✅ ALL MAJOR RENDERING GOALS ACHIEVED

**TV is visible. Characters and geometry correctly occlude it (depth testing). HUD renders in front. No regressions.**

### Checkpoint: v0.5.164 — Full HUD Ordering Fix (Golden Standard)

**Inject path**: CF-DI (DrawIndexedDetour) on `_targetInjectRtvPtr` (BGRA8 LDR, 2560×1440).
**Inject method**: Original-first (DrawIndexed executes first), then `ExecuteInlineDraw(useDepth: true, _dsReverseZ)`.
**Result**: TV is in the 3D world (depth-tested behind characters), and the game HUD renders on top.

### What was tried and why it failed for HUD ordering

**Full technical reference**: see `Golden-Standard-Rendering.md`.

**v0.5.159 — OMSetRT inject with depth** (`useDepth: true, _dsReverseZ`):
- Depth testing worked ✓. TV was correctly occluded by characters ✓.
- BUT: TV rendered in front of the game HUD ✗.
- Root cause: OMSetRT inject fires at BB-bind time. At that moment `OMGetRenderTargets` returns the
  LDR BGRA8 surface (`0x1B9E23BCBE0`). We inject into LDR, then BB binds. **However**, HUD had
  already drawn onto LDR BEFORE the BB-bind OMSetRT fired. So our inject draws on top of HUD on
  LDR, which then gets composited to BB. TV over HUD ✗.

**ClearRTV as HUD-start boundary** (v0.5.160–v0.5.161):
- Theory: FFXIV calls `ClearRenderTargetView` on LDR to wipe it before HUD draws. Inject after the
  clear, before any HUD draws → TV under HUD ✓.
- Confirmed false: `clearRtvCallCount=90299, clearRtvLdrFound=0`. FFXIV **never** calls ClearRTV on
  the LDR BGRA8 surface. It overwrites LDR via compute/UAV (tonemap) without ever clearing it.
  All ClearRTV calls are shadow maps, depth buffers, and other intermediates.

**CF-DI on any IsLdrFullRes surface** (v0.5.162):
- Enabled CF-DI but without target filter. Fired on `0x1B9AE67CC60` — a different BGRA8 surface
  that doesn't reach the screen. TV invisible.
- Root cause: multiple BGRA8 full-res surfaces exist (SMAA, TAA, various intermediates). CF-DI
  fired on the first one that passed `IsLdrFullRes`, which was NOT the final composite LDR.

**CF-DI on `_targetInjectRtvPtr` only** (v0.5.163) — **WORKING**:
- Added guard: `rtvPtr == _targetInjectRtvPtr` (the pointer OMSetRT learned as the stable LDR).
- CF-DI now fires specifically on the FIRST DrawIndexed on that surface during `_inUiPass`.
- At that moment: tonemap has already written the scene to LDR (via compute), but no HUD has drawn
  yet. Original() runs (first draw executes), then we inject. HUD draws follow on top. ✓

### Why this ordering works

FFXIV's pipeline at the HUD stage:
1. Compute/UAV tonemap fills LDR BGRA8 surface (`0x1B9E23BCBE0`) — no OMSetRT, no DrawIndexed
2. First DrawIndexed on LDR fires (some HUD setup or early composite draw) → CF-DI catches this
   - Original() runs first (that draw executes normally)
   - `ExecuteInlineDraw` draws TV rect onto LDR with scene depth buffer → depth testing works
   - `_frameInjectionDone = true` blocks all subsequent injects
3. Remaining HUD DrawIndexed calls on LDR execute normally (on top of our TV rect)
4. BB-bind OMSetRT fires → composite LDR (with TV + HUD) → BB
5. Dalamud ImGui renders on top of BB (on top of everything)

TV is between the first draw and all subsequent HUD draws. Net result: HUD in front of TV ✓.

---

## PREVIOUS STATUS (v0.5.119 → v0.5.120)

### Confirmed facts from v0.5.119 API data:
- `sceneInjectCount: 21571` → Scene inject (depth-only pass) fires every frame ✓
- `ldrInjectCount: 0` → **No color inject has EVER fired** in v0.5.119
- `/rect` colorSamples show real video colors — but these are from the **video decode CPU buffer**, NOT the rendered frame
- Screenshot at rect position showed near-black (#0F151C) — video is NOT rendered on screen
- Plugin API port was open (v0.5.119 loaded) at time of diagnosis

### Root question: Why does `ldrInjectCount` stay at 0?
Three color inject paths all gate on different conditions. If none fire, one of these is always false:
- **CF-DI inject**: `_sceneDrawnThisFrame && !_frameInjectionDone && _inUiPass && _currentBbRtvPtr == 0 && _currentNoDsvRtvPtr != 0 && rtvPtr == _targetInjectRtvPtr`
- **CF-Draw inject**: same conditions but in DrawDetour
- **BB inject**: `_currentBbRtvPtr != 0 && !_frameInjectionDone`

### Hypotheses (ranked by likelihood):
1. **`_currentBbRtvPtr` stays 0** — STEP B (RTV→texture matching) in `_inUiPass` never fires the BB match, so `_knownBackbufferRtvPtrs` is empty, `_currentBbRtvPtr` stays 0, BB inject blocked
2. **`_targetInjectRtvPtr` stays 0** — `_lastSeenValidRtvPtr` never gets set (no valid LDR full-res surface seen), CF inject never has a target to match, always passes the `rtvPtr == _targetInjectRtvPtr` check with both being 0 (but 0==0 would fire... wait, only if `rtvPtr != 0`)
3. **`_inUiPass` never becomes true** — scene→UI transition not detected. Possible if `_prevCallHadDsv` is wrong
4. **`_sceneDrawnThisFrame` is false** — scene inject is firing (21571 count) so this can't be the problem for CF paths
5. **`_psLdr` is null** — all three paths check `_psLdr != null`; if null, all blocked

### v0.5.120 diagnostic instrumentation added:
- PrepareHooks now captures end-of-frame state before reset: `_diagPrevBbRtv`, `_diagPrevFrameInj`, `_diagPrevLastSeen`
- Heartbeat (first 5 frames + every 60/300/600): logs `prevBbRtv`, `prevFrameInj`, `targetRtv`, `prevLastSeen`, `psLdr`
- DrawIndexedDetour: logs first 10 DI calls per frame for first 5 frames with all condition values
- DrawDetour: same for Draw calls

### v0.5.120 log findings (CONFIRMED):

**Draw sequence (frame 3):**
- `Draw#2@f3: inUiPass=False` — first draw BEFORE UI pass (scene inject fires in this window)
- `Draw#3@f3: inUiPass=True noDsvRtv=0x2C3519A6BA0` — first draw IN UI pass
- `Draw#4-10: noDsvRtv` varies, often 0x0 — DSV-bound calls during `_inUiPass` reset `_currentNoDsvRtvPtr`
- **ZERO DrawIndexed calls in entire 10-call diagnostic window** — composite is NOT DrawIndexed in first 10 calls, or fires after Draw#10
- `Heartbeat #4: prevBbRtv=0x2C33EB0DAA0` (non-zero, frame 3 identified BB) but `prevFrameInj=False` — BB was identified but inject didn't fire because Draw BB inject was removed in v0.5.119

**BB learning problem (CONFIRMED root cause #1):**
- `Heartbeat #5+: prevBbRtv=0x0` — only 1 of ~3 swapchain buffer RTVs was ever learned
- DXGI presents rotate through 2-3 different RTV ptrs. Only the first one seen gets added to `_knownBackbufferRtvPtrs`. The others were checked during startup (before BB was known), added to `_checkedRtvPtrs` as "not BB", and never re-evaluated.
- `bb-learn SKIP #1-5` repeatedly: `_pendingLearnBackbuffer` path consumed by already-checked RTV `0x2C46A8BCFE0`

**CF inject never fires (root cause #2):**
- `targetRtv=0x2C3518E91E0` was correctly set in frame 4 from frame 3's learning
- But no Draw with `noDsvRtv == targetRtv` fires in the diagnostic window → target surface drawn outside the 10-call logging cap

### v0.5.121 fixes applied:
1. **`_checkedRtvPtrs.Clear()` in PrepareHooks** — per-frame reset allows STEP B to re-evaluate all swapchain buffer RTVs each frame → `_currentBbRtvPtr` non-zero every frame
2. **Re-add Draw BB inject** (original-first, LDR shader) — since the composite to BB fires as a Draw (not DrawIndexed, per logs), Original runs first (composite: LDR→BB), then we inject rect on top. Subsequent HUD/ImGui Draws land on top → HUD in front.

---

## THE ONE PROBLEM — Game UI draws behind rect

**Goal:** Rect is visible as a D3D11 world-space object, with 2D HUD (chat, hotbar,
map, inventory) rendering IN FRONT of it.

**Current state (v0.5.83):** Rect visible. HUD draws behind rect. Compiled binary used
a stale DrawIndexedDetour inject (Original-first into BB after composite = v0.5.65 failure
re-introduced). v0.5.84 removes the DrawIndexedDetour inject — OMSetRT inject is now
the sole path (v0.5.69 approach: inject into currently-bound intermediate before BB bind).

**If v0.5.84 still shows HUD behind rect:** The OMSetRT inject fired but the intermediate
from OMGetRenderTargets already had HUD composited in. Check the log for "OMSetRT inject #1"
vs no inject logs (if no inject logs, BB tracking failed — check bbRtv count in heartbeat).

---

## Crash History

### v0.5.51 — SwapChain.Present crash (E0434352, managed .NET exception) — FIXED in v0.5.72

**Symptom:** Game crashes at `Client::Graphics::Kernel::SwapChain.Present` with CLR exception
code E0434352 (~10–14 seconds after loading plugin with a visible screen). No exception
logged before crash.

**Root cause (hypothesis):** Re-entrant hook detour calls from inside `ExecuteInlineDraw` —
when our PostTonemap inject calls `_context.OMSetRenderTargets(...)` and `_context.Draw(6, 0)`,
those re-enter `OMSetRenderTargetsDetour` / `DrawDetour`. If any Vortice call inside the
re-entrant detour throws a managed exception (e.g., DXGI_ERROR_INVALID_CALL from a COM HRESULT),
that exception can escape the re-entrant detour's finally block and propagate upward through
the native D3D call stack to `SwapChain.Present`, crashing the game.

**Secondary cause:** Shader compilation in `CreateResources()` called on the render thread →
340ms hitch on first frame (Dalamud "[HITCH] Long UiBuilder(FFXIV-TV) detected"). This blocks
the render thread, potentially triggering GPU driver timeouts or AMD RX 9070 driver-level
errors that corrupt the device state.

**Fix (v0.5.72):**
1. `[ThreadStatic] private static bool _inHookDetour` re-entrancy guard: all four hook detours
   check this flag at entry. If already set (re-entrant call from ExecuteInlineDraw), they
   immediately call Original and return, bypassing all managed logic and exception surface.
   The flag is set in the detour's try-entry and cleared in the finally, ensuring it resets
   even on exception.
2. Shader compilation (`Compiler.Compile`) moved to a background `Task.Run` in the
   `D3DRenderer` constructor. `TryInitialize` checks `_shaderCompileTask.IsCompleted` and
   returns false (defers) until compilation finishes. Eliminates the 340ms render-thread hitch.

---

## What We Know For Certain (Confirmed Facts)

1. **BB is bound exactly once per frame** (OMSetRenderTargets, no DSV). There is no second bind.
   *Source: v0.5.35 diagnostics — `bb bind #1` logged every frame, no `bb bind #2`.*

2. **The composite DrawIndexed fires on the BB** (vtable[12]) immediately after BB is bound.
   It reads one or more SRVs and blits the final image to the BB in one opaque pass.
   *Source: v0.5.34/35/36 analysis.*

3. **Confirmed working state (v0.5.47):** Rect visible, 3D objects/characters correctly
   occlude it (depth testing with `_trackedDsv` + dimension check works), 2D HUD
   (chat/hotbar/map) draws BEHIND rect. Rect is on top of 2D UI — not ideal, but 3D
   depth is correct. This is the current state.
   *Previously this was called "v0.5.36 state" — now confirmed working again in v0.5.47.*

4. **Injecting BEFORE the composite DrawIndexed → rect invisible.**
   Composite blit immediately overwrites whatever we drew. Confirmed v0.5.42.

5. **ClearRTV is never called on the backbuffer** — FFXIV only calls ClearRTV on intermediate
   surfaces. Any strategy based on "inject after ClearRTV on backbuffer" is impossible.
   *Confirmed v0.5.32.*

6. **The composite input texture (SRV[0]) has `matchRtv=0x0`** — it was never bound as an RTV
   through OMSetRenderTargets during `_inUiPass`. It's produced through the DSV-bound 3D pass,
   meaning it is the 3D scene texture, NOT the HUD. HUD may be a separate SRV input.
   *Source: v0.5.38/39 LogCompositeInputs analysis.*

7. **`_compositeInputRtv` can be created** — `CreateRenderTargetView` on the composite input
   texture succeeds (confirmed by the "Created composite input RTV" log entry).
   *Source: v0.5.40+ code, `_compositeInputRtv` is created in DrawIndexedDetour bookkeeping.*

8. **TEXCOORD / SV_VertexID / interpolated semantics are broken in this D3D context.**
   UV is computed via bilinear inverse from SV_POSITION. This IS working.
   *Source: Phase2-D3D-Rendering-Notes.md, v0.5.18.*

9. **The BGRA8 + RendererService timing approach DOES yield HUD in front of the rect.**
   Confirmed by reference screenshots (2026-03-17): chat box clearly renders over the world-space
   screen using this technique. Its approach (BGRA8 target + DepthWriteMask.All + RendererService
   timing) is what v0.5.63 implements.
   *Previous note was incorrect — that was based on an unverified assumption before deeper analysis.*

---

## Injection Approaches Tried (Complete History)

All attempts target the same goal: inject into the D3D pipeline so rect appears in 3D space with 2D UI in front.

| # | Approach | Result | Why It Failed |
|---|----------|--------|---------------|
| 1 | **ImGui `ExecuteDrawCallback` / `GetBackgroundDrawList`** | Rect covers all UI | Fires during Dalamud's ImGui phase, which is AFTER all native 2D UI draws. Rect is always on top of everything. |
| 2 | **OMSetRenderTargets inject at first no-DSV transition (`_inUiPass`)** | Rect invisible | First no-DSV RT is a post-processing intermediate. FFXIV overwrites it before the frame is presented. |
| 3 | **OMSetRenderTargets inject when BB RTV is bound** | Rect invisible | FFXIV immediately calls ClearRTV* on an intermediate surface (misidentified as the BB), wiping pixels. *(Later confirmed: FFXIV never ClearRTVs the BB at all — the early logic was wrong.)* |
| 4 | **ClearRTV hook — inject after clear on BB** | Rect invisible | FFXIV never ClearRTVs the actual DXGI backbuffer. The cleared pointer was always an intermediate. Strategy is impossible. *Confirmed v0.5.32.* |
| 5 | **ClearRTV hook — inject after ANY clear during `_inUiPass`** | Rect invisible (3D geo flickers) | Fires on post-processing surfaces (positions 6, 13 in sequence). Drawing into these surfaces with depth testing feeds into the lighting pipeline → 3D geometry at rect's world location flickers. No rect visible. |
| 6 | **`_lastClearedUiPassRtvPtr` — inject into most-recently-cleared surface before composite DrawIndexed** | Never ran | Backbuffer-learning cascade was broken by DrawPlaceholder path (v0.5.38/39). Fixed in v0.5.40 but approach abandoned before testing. |
| 7 | **DrawIndexedDetour inject into `_compositeInputRtv` BEFORE calling Original** | Never correctly ran | v0.5.39 planned this but cascade was broken; v0.5.40 fixed cascade but v0.5.41+ moved away from this approach entirely before it could be tested. |
| 8 | **DrawIndexedDetour inject into BB (inject-first, then Original)** | Rect invisible | Composite blit (Original) overwrites rect. Order must be Original-first. *v0.5.42.* |
| 9 | **DrawDetour inject into BB (Original-first, then inject) with `useDepth=false`** | Rect covers EVERYTHING — 3D objects, characters, AND 2D UI. Worst state. | DrawDetour DOES fire after BB bind. But `useDepth=false` disables depth testing, so rect is drawn flat on top of the entire composited scene. Also fires AFTER all game rendering is done (composited 3D+HUD in BB) — there is no way to "insert" before HUD from this hook point. *v0.5.44 confirmed.* |
| 10 | **DrawIndexedDetour inject into BB when `_currentBbRtvPtr != 0` (Original-first)** | Rect entirely missing. | DrawIndexed calls fire BEFORE OMSetRenderTargets(backbuffer), so `_currentBbRtvPtr` is always 0 when DrawIndexedDetour runs. Condition never true. *v0.5.45.* |
| 11 | **DrawDetour inject with `useDepth=true` (naive — no dimension check)** | Rect entirely missing. | D3D11 silently renders nothing when DSV and RTV have different texture dimensions. If FFXIV's internal render resolution ≠ output resolution, `_trackedDsv` is the wrong size for the BB. No exception — just invisible. *v0.5.46.* |
| 10 | **DrawIndexedDetour + DrawDetour both injecting into BB** | Flickering | DrawIndexedDetour missing `_currentBbRtvPtr != 0` guard → NullReferenceException. *v0.5.41.* |
| 11 | **DrawDetour inject (Original-first) + DrawIndexedDetour composite inject (Original-first)** | Flickering | DrawIndexedDetour set `_frameInjectionDone=true` during post-processing (before BB bind), blocking DrawDetour's BB inject. *v0.5.43.* |
| 12 | **OMSetRenderTargets scene-pass inject — v0.5.57/v0.5.58: fire on FIRST MainSceneDSV+MainSceneRTV bind** | FAILED (rect invisible in v0.5.57; v0.5.58 reverted to dual-draw) | Fired too early — at the FIRST OMSetRenderTargets call for the scene pass, before FFXIV clears + draws the 3D scene. FFXIV's ClearDepthStencilView + 3D geometry draws immediately overwrote our inject. Also: v0.5.57 set `_frameInjectionDone=true` before BB fallback could run → no rect at all. v0.5.58 removed that flag so BB inject still fired (rect visible, HUD still behind). Root cause: need `RendererServiceAlt` pattern — inject AFTER scene draws, not before. |
| 13 | **OMSetRenderTargets scene-pass inject — `RendererServiceAlt` pattern into R16G16B16A16_Float** | FAILED (v0.5.59) | `_mainSceneRtvPtr` was set to the LAST full-res non-BGRA8 RTV paired with MainDSV = R16G16B16A16_Float. Scene inject fired correctly (log confirmed). But R16G16B16A16_Float is a compositing/accumulation buffer — a 71973-index DrawIndexed in the no-DSV phase completely overwrites it every frame. Rect drew into R16G16B16A16_Float, then was immediately erased. `_frameInjectionDone=true` blocked BB fallback → rect invisible. Root cause: R16G16B16A16_Float is NOT a stable final scene surface. |
| 14 | **OMSetRenderTargets scene-pass inject — `RendererServiceAlt` into BGRA8 (first full-res RTV with MainDSV)** | FAILED (v0.5.60) | Injected into BGRA8 at the DSV-phase exit. But the no-DSV DrawIndexed phase fires 71973 (and other calls) directly to BGRA8, completely overwriting our rect. Log confirmed: `idx=71973 rt=0x259B6254660` fires in no-DSV phase after our inject. Additionally `_frameInjectionDone=true` blocked BB fallback → rect invisible. Fix: removed `_frameInjectionDone=true` from scene inject (v0.5.61) — BB inject restored. Core problem: BGRA8 is rewritten in the no-DSV phase regardless of when we inject in the DSV phase. No injection point in the DSV phase survives into the final frame. |
| 15 | **OMSetRenderTargets scene-pass inject — RendererService into R16G16B16A16_Float with DepthWriteMask.Zero** | FAILED (v0.5.62) | Two bugs combined: (a) `_mainSceneRtvPtr` filtered `notBgra` so it captured R16G16B16A16_Float instead of BGRA8 — the wrong target per analysis (R16 gets score 0; BGRA8 gets +500). (b) `_dsReverseZ` uses `DepthWriteMask.Zero` — depth not written, so FFXIV's subsequent geometry always passes the depth test against cleared depth=0 and overwrites our rect entirely. Both root causes confirmed by deeper analysis. Fix for v0.5.63: target BGRA8 specifically + use `_dsReverseZWrite` (DepthWriteMask.All, GreaterEqual). |
| 16 | **OMSetRenderTargets scene-pass inject into BGRA8 — fires on `currentSceneRendered && !_sceneDrawnThisFrame`** | FAILED (v0.5.63): rect invisible. Fixed `_frameInjectionDone` → v0.5.64: rect visible but HUD still behind. | v0.5.63: ClearRenderTargetView(BGRA8) fires immediately after OMSetRenderTargets before ANY geometry draws, wiping our inject. Also `_frameInjectionDone=true` blocked BB fallback → completely invisible. v0.5.64: removed `_frameInjectionDone=true` from scene inject so BB-path DrawDetour still fired → rect visible again. But DrawDetour (vtable[13]) fires on the FIRST `Draw()` call after BB bind, which comes AFTER all `DrawIndexed` (vtable[12]) HUD calls. FFXIV uses DrawIndexed for both the composite blit AND all 2D HUD elements. Result: HUD already baked into BB before we inject → HUD always behind rect. Fix for v0.5.65: move injection to DrawIndexedDetour — first DrawIndexed with BB bound is the composite blit; inject after it fires, before any HUD DrawIndexed. |
| 17 | **DrawIndexedDetour BB inject (v0.5.65) — first DrawIndexed with `_currentBbRtvPtr != 0`** | FAILED: HUD still in front of rect (same as v0.5.64). | The composite DrawIndexed on BB fires AFTER the complete HUD rendering pipeline finishes. Despite our assumption, all HUD elements are fully composited into the BB before the first DrawIndexed on BB fires. DrawIndexed seq diagnostics confirm: `_inUiPass` DrawIndexed calls (#1-100+) all fire to intermediates (0x...C2EFE0, EB60, E860, ECE0) — NOT to the BB. The BB composite DrawIndexed reads from these pre-composited intermediates (which already contain HUD). Our inject after it = on top of HUD. Fix for v0.5.66: detect idx=71973 (the full-screen scene composite), inject AFTER it fires (before subsequent HUD DrawIndexed calls to the same RT). |
| 18 | **71973 scene-layer inject (v0.5.66) — CATASTROPHIC FAILURE (white hellscape + rainbow rect)** | REVERTED in v0.5.67. | The 71973 DrawIndexed fires during `_inUiPass` and writes to an intermediate surface (0x259B6C2E860) that uses the **alpha channel for compositing**. Our `ExecuteInlineDraw` wrote alpha=1.0 (opaque) for rect pixels. FFXIV's compositor reads alpha=1 as "no background" → entire scene background became pure white. Rect showed as rainbow gradient (HDR float surface format, our LDR normalized values look wrong on R16G16B16A16_Float target). v0.5.67 emergency revert removed 71973 inject, cleaned up `_sceneLayerInjectDone` field. |
| 19 | **RendererServiceAlt + R16G16B16A16_Float (v0.5.68) — PARTIAL SUCCESS** | HUD now draws in front. Rect is massively bright/glowing. | R16G16B16A16_Float is FFXIV's HDR accumulation buffer (linear light, pre-tone-mapping, pre-bloom). Our shader outputs sRGB-range values (0–1) which in linear HDR space are very bright. FFXIV's auto-exposure (eye adaptation) amplifies these further in dark indoor scenes. FFXIV's bloom post-process also fires on values above its threshold. Result: rect appears as overexposed white with colorful glow edges. Fix: add `HdrScale` multiplier to cbuffer + PS, default ~0.18, so output values are in the correct HDR linear range before tone-mapping. |
| 20 | **OMSetRT post-bloom inject — inject into prevNoDsvPtr (last no-DSV surface before BB bind) with depth (v0.5.88/v0.5.89)** | FAILED: rect draws in front of HUD. | `OMGetRenderTargets` at BB-bind time returns null (FFXIV unbinds R16 as RT to use as SRV for composite). Fallback to `prevNoDsvPtr` (last no-DSV RTV seen before BB). Even with `CheckDepthCompatibility` + scene DSV, the rect ends up in front of HUD in both `useDepth=false` and `useDepth=true` variants. Root cause: any surface injected at BB-bind time or later is at BB level — ordering relative to HUD is not guaranteed by this approach. The scene inject (RendererServiceAlt into R16 at scene-end) is the ONLY mechanism that reliably puts HUD in front, because R16 is pre-BB and every subsequent pass (tonemap → composite → HUD) layers on top. |
| 21 | **Post-bloom R16 inject — inject into R16 triggered by first LDR full-res surface bind after scene (v0.5.90)** | FAILED: rect invisible. | At first LDR (tonemap output) bind after scene detection, re-bind R16 + scene DSV, draw rect, restore. Theory: bloom has already read R16 at this point → no bloom amplification. Reality: R16 (`_mainSceneRtvPtr`) is subsequently overwritten by a DrawIndexed in the no-DSV phase (same as entry #13, v0.5.59 failure). `_frameInjectionDone=true` blocked all other paths. **This is a rediscovery of entry #13.** DO NOT ATTEMPT this approach again under any form — "inject into R16 after bloom" always fails because R16 is overwritten in the no-DSV phase regardless of when we inject. |
| 22 | **Two-pass: depth-only scene inject + full-color LDR inject at BB-bind time (v0.5.95/v0.5.96)** | FAILED: rect in front of HUD. Reverted in v0.5.97. | **Premise:** Pass 1 (scene inject, `_depthOnlyBlendState` — ColorWriteEnable.None) writes depth to R16 without color → bloom pass sees nothing → no glow. Pass 2 (OMSetRT inject at BB-bind, `!_ldrInjectionDone && _sceneDrawnThisFrame`) fires on the pre-BB LDR BGRA8 surface, draws rect with full color using `_psLdr` (no sRGB conversion). **Root cause of failure:** The BGRA8 LDR surface at BB-bind time ALREADY contains HUD composited into it. This is the SAME failure as entry #20 — any inject at or after BB-bind time puts rect at BB level where HUD ordering is not guaranteed. **Secondary bug in v0.5.95 (GreaterEqual lesson):** `_dsReverseZ` uses `ComparisonFunction.Greater` (strict). Scene inject writes D_rect with GreaterEqual. At LDR inject time: `D_rect > D_rect = FALSE` → every rect pixel discarded → black rect. Fixed in v0.5.96 with `overrideDepthState: _dsReverseZWrite` (GreaterEqual), but the fundamental ordering problem remained. **What depth-only scene inject proved:** `_depthOnlyBlendState` (ColorWriteEnable.None) DOES correctly prevent bloom — no glow was visible in v0.5.95/v0.5.96 when the rect was visible at all. The depth-only inject as a standalone technique works; the failure was in the companion LDR inject step, not the depth-only step. **Key learnings:** (1) No injection point after the scene inject can put rect before HUD — HUD is composited before BB bind. (2) GreaterEqual is required for any inject that tests against its own written depth values (Greater fails on exact match). (3) The only mechanism that gives HUD-in-front is the scene inject into R16+MainDSV at scene-end. |

---

## Suspect "Resolved" UI Ordering Issues

**v0.5.26** logged "Map window now renders in front of rect" after fixing the DSV leak in
`ExecuteInlineDraw`. This was likely WRONG — the inject at that time was hitting an
intermediate post-processing surface (not the final backbuffer), so UI "appearing in front"
was coincidental or was only Dalamud-side ImGui windows (which always render after game UI).

**v0.5.24–v0.5.26** used the ClearRTV hook and claimed the backbuffer was being cleared and
re-injected. This whole chain was invalidated by v0.5.32 confirming FFXIV never ClearRTVs
the actual backbuffer. Those versions were injecting into intermediate surfaces and the
"working" state was an illusion.

---

## Untried Approaches (Ranked by Likelihood)

### 1. OMSetRenderTargets inject — at BB-bind moment, into the currently-bound intermediate RT
**NOT YET TRIED (v0.5.51 target).**

At the moment OMSetRenderTargets fires with the BB (identified via `_knownBackbufferRtvPtrs`),
the currently-bound RT is the LAST intermediate surface in the post-processing chain — the
final composited output that's about to be copied to BB.

**Key hypothesis**: If this intermediate contains the 3D scene (post-processing) but NOT the
HUD yet, injecting into it here means our rect gets baked into the 3D scene. Then FFXIV's
Draw calls copy intermediate→BB and add HUD on top. HUD ends up in front.

**Key question**: Is HUD in the intermediate (before BB bind) or added by Draw calls after?
We can answer this empirically: if this approach puts HUD in front → HUD was added after
(hypothesis correct). If HUD is still behind → HUD was already baked into intermediate.

**Research basis**: Multiple reference plugins inject from inside the OMSetRenderTargets detour.
Multiple sources confirm this fires "after scene, before UI." REST
(ReshadeEffectShaderToggler-FFXIV_UIONLY) similarly fires at the scene→UI transition.

**Also**: FFXIV writes UI presence into the alpha channel of the backbuffer (unique to FFXIV).
This could be used as a fallback to mask rect pixels where alpha encodes UI — but requires
copying BB to a temp texture (D3D11 can't read+write same resource in one pass).

**Implementation**: In `OMSetRenderTargetsDetour`, when `_knownBackbufferRtvPtrs.Contains(rtvPtr)`:
1. `OMGetRenderTargets(1u, intermediateRtvArr, out intermediateDsv)` — get currently-bound RT
2. If intermediate != null: `ExecuteInlineDraw(intermediate, CheckDepthCompatibility(intermediate))`
3. Set `_frameInjectionDone = true` — blocks DrawDetour fallback
4. Call Original — BB gets bound, intermediate stays in GPU memory with rect baked in
5. FFXIV Draw calls copy intermediate→BB and add HUD on top

**Re-entrancy note**: ExecuteInlineDraw calls `_context.OMSetRenderTargets` internally, which
fires OMSetRenderTargetsDetour re-entrantly. Re-entrant calls: `_frameInjectionDone = true`
and non-BB RT → no double-injection. Original in finally uses trampoline (bypasses hook).

**To try:** Implement in v0.5.51.

### 2. DrawIndexedDetour: Original-first injection into BB when `_currentBbRtvPtr != 0`
**NOW TRYING in v0.5.65.**

The composite blit IS a DrawIndexed. v0.5.64 diagnostics confirmed call order:
`OMSetRT(BB)` → `DrawIndexed[composite]` → `DrawIndexed[HUD...]` → `Draw[...]`

- DrawIndexedDetour fires on composite blit: call Original (blit runs) → inject rect → set `_frameInjectionDone=true`
- All subsequent DrawIndexed (HUD) fire normally (Original via `finally`, no inject)
- Result: rect appears after composite blit (3D scene), HUD draws on top ✓

Note: v0.5.45 attempted "DrawIndexedDetour inject into BB when `_currentBbRtvPtr != 0`" and
found condition was always false. v0.5.64 diagnostics disproved that assumption — `_currentBbRtvPtr`
IS non-zero when DrawIndexed fires (OMSetRT sets it, then DrawIndexed follows on same BB).
The v0.5.45 failure was likely a tracking bug, not a fundamental pipeline ordering issue.

### 2. Composite input injection (SRV[0]) in DrawIndexedDetour BEFORE Original
**IN PROGRESS (v0.5.49).** v0.5.39 planned this but cascade was broken; v0.5.40+ abandoned it.
Now attempting properly.

The composite input texture (SRV[0]) is the 3D scene (matchRtv=0x0 = produced via DSV pass,
not tracked via `_rtvToTexture`). If we inject into it BEFORE the composite reads it,
our rect becomes part of what the composite blits to the BB. The composite then writes
3D+rect to BB. 2D HUD draws after on top.

`_compositeInputRtv` is ALREADY BEING CREATED in the current DrawIndexedDetour bookkeeping
code. It just isn't being used.

**D3D11 SRV/RTV hazard:** When we bind `_compositeInputRtv` (same texture) as RT while
it's bound as SRV[0], D3D11 auto-unbinds it from the SRV slot. After our inject we must
explicitly restore: (1) original RT so Original writes to its intended intermediate surface,
(2) SRV[0]=compositeInputTex so the composite DrawIndexed can actually read it.

**Depth:** `_trackedDsv` is frozen from the 3D pass (not updated during `_inUiPass`).
Both compositeInputTex and the DSV were created for the same 3D render pass → same
dimensions → `CheckDepthCompatibility` returns true → depth testing works.

**v0.5.49 attempt:** DrawIndexedDetour: when `_compositeInputRtv != null && !_frameInjectionDone`:
1. `PSGetShaderResources(0, savedSrvArr)` — save SRV[0]
2. `OMGetRenderTargets(savedRtvArr, out savedDsv)` — save current RT
3. `ExecuteInlineDraw(_compositeInputRtv, useDepth)` — inject into 3D scene texture
4. `OMSetRenderTargets(savedRtvArr, savedDsv)` — restore original RT
5. `PSSetShaderResources(0, savedSrvArr)` — re-bind SRV[0] (safe now, not bound as RTV)
6. `Original(...)` — composite blit runs with rect baked into SRV[0], writes to intermediateRT
DrawDetour: blocked by `_frameInjectionDone = true` (set in DrawIndexedDetour).

**Outcome: FAILED. Rect disappeared entirely.**

Root cause: `LogCompositeInputs` captures SRV[0] on the FIRST eligible DrawIndexed during
`_inUiPass`. This is NOT the final composite — it's early post-processing (TAA/tone-mapping/
DOF). FFXIV's post-processing chain has many DrawIndexed passes. The actual composite that
writes to the final pre-BB surface is a LATER DrawIndexed. By modifying the raw 3D scene
texture at an early step, we only affect the input to the first post-processing pass. The
output of that pass (and all subsequent passes) is a different intermediate that we never
touch. Additionally, `_frameInjectionDone = true` disabled DrawDetour (the working baseline),
so the frame showed nothing. Reverted in v0.5.50.

**Note on reference plugins:** No known reference plugin currently has UI rendering in front
of its world-space rects either — they share the same problem. "Confirmed working" applies
only to the rect being visible in 3D space — not the UI layering.

**What's needed to do this correctly:**
- Know which specific DrawIndexed call in the post-processing chain is the TRUE final composite
  (the one that writes to the surface that immediately precedes the BB bind)
- OR inject at a point in the pipeline that is definitely after ALL post-processing but before
  the BB bind (if such a point exists)
- v0.5.50 adds DrawIndexed sequence diagnostics: logs every DrawIndexed during _inUiPass
  for the first 3 frames (index count, current RT, SRV[0] texture ptr, whether SRV[0] is
  the captured compositeInputTex). This will identify the call order and which call is last.

### 3. Scene-transition timing — hook Present + OMSetRenderTargets, draw at scene transition
**NOT YET TRIED.** Hook both functions and fire Draw() when:
- OMSetRenderTargets is called with main scene RTV+DSV (matching swapchain dimensions)
- AND `LastPresentIndex != PresentIndex` (new frame has started)

Bind the backbuffer RTV + main DSV and draw there. The claim is this fires
"after scene, before UI." The underlying mechanism: track when the main scene DSV
stops being bound (transition to post-processing or 2D), and draw at that exact point.

**Risk:** We may have the same problem (rect covers UI) if FFXIV's 2D UI is already
baked into what the composite reads. But this approach is reportedly working in
other implementations.

### 4. Add draw-call diagnostics after BB bind
**Not done yet.** Log EVERY Draw + DrawIndexed call after `_currentBbRtvPtr != 0` fires
(vertex count, context, call order) until Present. This would definitively answer:
- Are there DrawIndexed calls after the composite blit?
- Are there Draw calls?
- How many total?
Without this data, approaches #1 and #9 (current) are being tried blind.

### 5. Hook `IDXGISwapChain::Present` (vtable[8])
Fire the inject on Present instead of on a Draw call. At Present time:
- BB contains the fully composited frame (3D + HUD)
- We draw on top of everything (same as ImGui path — rect covers UI)
This does NOT solve the UI layering problem but could confirm the rect itself renders.

### 6. Track the last non-null DSV from the 3D pass
For composite input injection (#2 above) to support depth testing, we need the 3D scene
DSV. Currently `_trackedDsv` is captured during DSV-bound passes, but we only use it in
`ExecuteInlineDraw` with `useDepth=true`. This infrastructure already mostly exists — just
need to confirm `_trackedDsv` is still valid at composite DrawIndexedDetour time.

---

## Checkpoint: v0.5.48 — Confirmed Working Baseline

**DO NOT REGRESS FROM THIS STATE.**

- Rect visible in world space. 3D objects/characters correctly occlude it (depth testing via `_trackedDsv` + `CheckDepthCompatibility()`).
- 2D HUD draws BEHIND rect (not ideal — remaining problem), but all other behavior correct.
- Video loop flash fixed via `_transitioning` flag in `VideoPlayer.cs`.
- Placement tab added to `MainWindow.cs`.

If any new approach makes the rect invisible or breaks depth testing, revert to this baseline:
- `DrawDetour`: Original-first, inject into BB, `CheckDepthCompatibility(bbRtv)` to decide depth.
- `DrawIndexedDetour`: bookkeeping only (`LogCompositeInputs`, `_compositeInputRtv` creation). No injection.
- `_trackedDsv` frozen at `_inUiPass` start (`!_inUiPass` guard in OMSetRenderTargetsDetour).

---

## ⚠️ NEVER AGAIN — Lessons From v0.5.44–v0.5.46

**DO NOT disable depth testing (`useDepth=false`) when injecting into the backbuffer.**
Using `useDepth=false` means the rect is drawn as a flat 2D overlay on top of EVERYTHING —
all 3D geometry, all characters, all UI. This is strictly worse than the ImGui fallback.
Any BB inject MUST use depth testing to preserve 3D depth relationships.

**DO NOT use `useDepth=true` without checking DSV/RTV dimension compatibility first.**
D3D11 silently renders NOTHING when the DSV and RTV have different texture dimensions.
No exception is thrown — the rect just disappears. Always use `CheckDepthCompatibility()`
before calling `ExecuteInlineDraw`. If dimensions don't match, fall back to `useDepth=false`
(at least the rect is visible) and log the mismatch.

**DO NOT update `_trackedDsv` during `_inUiPass`.**
Post-processing may bind DSVs with different dimensions, overwriting the valid main-scene DSV.
The `!_inUiPass` guard in OMSetRenderTargetsDetour prevents this. If removed, depth breaks.

**DrawIndexed calls fire BEFORE OMSetRenderTargets(backbuffer).**
`_currentBbRtvPtr` is 0 when DrawIndexedDetour runs. Any injection in DrawIndexedDetour gated
on `_currentBbRtvPtr != 0` will NEVER fire. Injection belongs in DrawDetour (fires after BB bind).

---

## Current State (v0.5.47 — confirmed working baseline)

DrawIndexedDetour: bookkeeping only (LogCompositeInputs, creates `_compositeInputRtv`).
DrawDetour: Original-first, inject into BB. Uses `CheckDepthCompatibility()` to decide
whether to bind `_trackedDsv`. Falls back to no-depth if DSV/RTV sizes mismatch.
`_trackedDsv` is frozen at start of `_inUiPass` — post-processing can't overwrite it.

**Confirmed (user):** Rect visible. 3D objects/characters correctly occlude the rect.
2D HUD (chat/hotbar/map) draws IN FRONT is still not achieved — rect is on top of 2D UI.

**Remaining problem:** 2D HUD draws on top of the rect. DrawDetour fires AFTER all HUD is
composited into the BB. No injection point between composite blit and HUD draw has been found.

**Next target (v0.5.57):** Scene-pass inject (Approach #12).
Identify `_mainSceneDsvPtr` (first full-res DSV) + `_mainSceneRtvPtr` (last full-res R8G8B8A8_UNorm
RTV bound alongside it). Inject into those targets during the 3D scene render pass. HUD renders
after → should appear in front. DrawDetour kept as fallback if this fails.

---

## Crash: A-B Loop Race Condition on Play() (Resolved 2026-03-17)

**Symptom:** Game crashed immediately when hitting Play while a video was already playing with an A-B loop active.

**Root Cause:**
`DisplayCallback` (LibVLC decode thread) fires `Task.Run(() => _player.Position = _loopA)` every frame when the playhead reaches loop point B. This task sits in the thread pool queue. When the user hits Play, the main thread calls `Stop()` → `_player.Stop()` → `StartPlayback()` (reconfigures the player for the new video). If the A-B seek task fires *during* this reconfiguration — while `_player.Media` is being reassigned and `_player.Play()` is starting — concurrent native LibVLC calls (`libvlc_media_player_set_position` + `libvlc_media_player_play`) race in native code → CLR fatal error / crash.

Secondary issue: `DisplayCallback` had no try/catch. Any exception from it propagates through native LibVLC → unhandled exception → CLR fatal error.

**Fix Applied (2026-03-17):**
- `DisplayCallback`: Entire body wrapped in try/catch. Any exception is logged and suppressed.
- A-B loop `Task.Run`: Captures `_playVersion` at dispatch time. The seek only fires if `_playVersion` hasn't changed (i.e., no new `Play()` call happened between dispatch and execution). If Play() was called, the stale seek is discarded.
- `LockCallback`: Wrapped in try/catch with `_vlcWriting = false` in catch to avoid leaving the spin-wait in `Stop()` stuck forever.

---

## Bug: OMSetRenderTargetsDetour NullRef Spam (Resolved 2026-03-17)

**Symptom:** `NullReferenceException` logged from `OMSetRenderTargetsDetour` ~120 times/second (every frame). Visible in dalamud.log as constant warning spam. Caused render pipeline instability during the 2026-03-17 crash session.

**Root Cause:**
`_context!.OMGetRenderTargets(1u, intermediateRtvArr, out var intermediateDsv)` — when the currently-bound RT has no depth stencil (normal for post-processing surfaces), Vortice returns `null` for `intermediateDsv`. The immediately following `intermediateDsv.Dispose()` (no null check) threw `NullReferenceException` on every frame. The outer try/catch caught it and logged it, preventing a game crash, but generating constant spam. Same pattern existed in a second `OMGetRenderTargets` call in the `DrawIndexedDetour` path.

**Fix Applied (2026-03-17):**
Both `intermediateDsv.Dispose()` → `intermediateDsv?.Dispose()` and `dsv.Dispose()` → `dsv?.Dispose()`.

---

## Non-Rendering Issues (Resolved)

| Version | Issue | File |
|---------|-------|------|
| v0.5.18–v0.5.20 | Game crashes, COM refcount, managed exceptions in detours | inline above |
| v0.5.18 | Video/browser players: YouTube, Reddit, WebView2 bugs | `Phase3-Video-Playback-Notes.md` |
| Phase 1/2 | UV rendering, depth testing, image display | `Phase2-D3D-Rendering-Notes.md` |

---

## Crash: CLR Fatal Internal Error 0x80131506 (Resolved 2026-03-17)

**Symptom:** Game crash at 03:16:34 on 2026-03-17. `output.log` ends with `"Stopping addons..."`.
Crash dump: `dalamud_appcrash_20260317_031634_222_55932.dmp`. Exit code `0x80131506`
(ExecutionEngineException — fatal CLR internal error, unrecoverable).

**Root Cause Identified:**
`VideoPlayer` previously called `Core.Initialize()` + `new LibVLC()` + `new MediaPlayer()` in its
**constructor**, which ran the moment FFXIV-TV was enabled — regardless of whether any video was
ever played. LibVLC responds to `Core.Initialize()` by scanning the plugins directory and loading
every codec/protocol/filter DLL it finds (~200 native DLLs). Loading ~200 native DLLs into the
managed CLR process dramatically increases memory pressure and native/managed interop surface area.
The CLR fatal error (0x80131506) is consistent with memory corruption or a GC/JIT failure
triggered by this native code load.

**Contributing factor:** FFXIV-TV is the only dev plugin that loads this many native DLLs.
VFXEditor loads FreeImage + nvtt (~3 DLLs). WebView2 is lazy. Nothing else in the 82-plugin
stack comes close to LibVLC's footprint.

**Fix Applied (2026-03-17):**
`VideoPlayer.EnsureVlcInitialized()` — lazy initialization. LibVLC, MediaPlayer, and all native
VLC plugin DLLs are now deferred until the **first `Play()` call**. Plugin startup, image mode,
browser mode, and sessions that never play video incur zero VLC DLL overhead.

**What changed in `VideoPlayer.cs`:**
- `_libVlc` and `_player` changed from `readonly` fields to nullable fields (`LibVLC?`, `MediaPlayer?`)
- Constructor no longer calls `Core.Initialize()`, `new LibVLC()`, or `new MediaPlayer()`
- New `private void EnsureVlcInitialized()` called at the top of `Play()` only
- Added `_pendingVolume` / `_pendingMuted` backing fields — Volume/Mute set before first Play
  are stored and applied when VLC actually initializes
- All `_player` accesses in properties/methods are null-safe:
  `IsPlaying` → `_player?.IsPlaying ?? false`
  `IsPaused` → `_player != null && _player.State == VLCState.Paused`
  `HasTexture` → `_srv != null && _player != null && ...`
  `TimeMs`/`LengthMs` → `_player?.Time ?? -1` / `_player?.Length ?? -1`
  `Seek`, `TogglePause`, `Stop`, `Dispose` — null-guarded before any `_player` access

**Functionality unchanged:**
- Image mode, browser mode: unaffected (never used `_player`)
- Volume/Mute persist: stored in `_pendingVolume`/`_pendingMuted`, applied at init
- All video playback features: identical after first `Play()` call
- Network sync (SyncCoordinator, SyncClient): all pass-throughs return safe defaults when VLC not yet init'd
