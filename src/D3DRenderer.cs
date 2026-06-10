using System;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using SDDrawing = System.Drawing;
using SDImaging = System.Drawing.Imaging;

namespace FFXIVTv;

/// <summary>
/// Phase 2 renderer: D3D11 world-space quad injected into FFXIV's render pipeline.
///
/// Shader architecture:
///   - No vertex buffer. VS generates a flat quad from SV_VertexID (6 vertices, TriangleList).
///   - ScreenTransform TRS matrix in cbuffer — enables full Yaw/Pitch/Roll orientation.
///   - UV generated in VS from vertex index → correctly interpolated through rasterizer.
///   - Single cbuffer (b0) contains ViewProj + ScreenTransform + post-processing params.
///   - PS: standard tex.Sample(uv) + brightness/gamma/contrast/tint pipeline.
///
/// Reversed-Z note: FFXIV uses near=1.0, far=0.0.
/// </summary>
public sealed unsafe class D3DRenderer : IDisposable
{
    private ID3D11Device?        _device;
    private ID3D11DeviceContext? _context;

    public ID3D11Device? Device => _device;

    private VideoPlayer?  _videoPlayer;
    public void SetVideoPlayer(VideoPlayer? vp) { _videoPlayer = vp; }

    private BrowserPlayer? _browserPlayer;
    public void SetBrowserPlayer(BrowserPlayer? bp) { _browserPlayer = bp; }

    // Single merged cbuffer replaces the old b0+b1+b2 split.
    private ID3D11Buffer? _cbParams;

    /// <summary>Brightness multiplier applied to every pixel. 1.0 = original.</summary>
    public float Brightness { get; set; } = 1.0f;
    /// <summary>Gamma power curve. 1.0 = no change. >1 = darker midtones. Range 0.1–3.0.</summary>
    public float Gamma { get; set; } = 1.0f;
    /// <summary>Contrast around 0.5 midpoint. 1.0 = no change. >1 = more contrast. Range 0.0–3.0.</summary>
    public float Contrast { get; set; } = 1.0f;
    /// <summary>RGBA tint multiplier. (1,1,1,1) = no change. A &lt; 1 makes screen transparent.</summary>
    public Vector4 Tint { get; set; } = Vector4.One;
    /// <summary>Brightness limit for the LDR inject output. 1.0 = no limit (full brightness).
    /// 0 = disabled (no cap). Range 0.0–1.0. FFXIV bloom threshold is empirically ~0.3–0.5;
    /// keep below threshold to eliminate glow. At 0.35 bright pixels are clamped proportionally.</summary>
    public float BloomCap { get; set; } = 0.35f;

    private ID3D11VertexShader?      _vs;
    private ID3D11PixelShader?       _ps;
    private ID3D11BlendState?        _blendState;
    private ID3D11BlendState?        _depthOnlyBlendState;  // no color writes — scene inject writes depth only
    private ID3D11BlendState?        _blendStateInvDestAlpha; // TV fills alpha=0 areas only (behind HUD if HUD=alpha1, scene=alpha0)
    private ID3D11Texture2D?         _alphaStagingTex;      // 1x1 BGRA8 staging for LDR alpha readback
    private bool                     _alphaReadbackDone;
    private ID3D11PixelShader?       _psLdr; // LDR inject shader: no sRGB conversion, no bloom cap
    // Diagnostic "solid red" pixel shader — when DebugShaderRed=true, _psLdr is swapped
    // for this in the inject path so we can verify the draw path is reaching the BB at all.
    private ID3D11PixelShader?       _psLdrDebugRed;
    /// <summary>
    /// When true, the inject path swaps <c>_psLdr</c> for <c>_psLdrDebugRed</c> so the TV
    /// renders as solid red. Defaults OFF — hot path is unchanged when false.
    /// </summary>
    public bool DebugShaderRed { get; set; } = false;
    private ID3D11RasterizerState?   _rasterizer;
    private ID3D11DepthStencilState? _dsNoDepth;
    private ID3D11DepthStencilState? _dsReverseZ;       // DepthWriteMask.Zero — BB inject (nothing renders after)
    private ID3D11DepthStencilState? _dsReverseZWrite;  // DepthWriteMask.All  — scene inject (FFXIV geometry must depth-test against our rect)
    private ID3D11SamplerState?      _sampler;

    // ── Vtable hook delegates ─────────────────────────────────────────────────
    // FFXIV's ImmediateContext uses STANDARD absolute D3D11 vtable indices.
    // (IUnknown[0-2] + ID3D11DeviceChild[3-6] + ID3D11DeviceContext[7+])
    // Confirmed working: OMSetRenderTargets=33, ClearRTV=50.

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void OMSetRenderTargetsDelegate(
        nint pContext, uint numViews, nint* ppRTVs, nint pDSV);

    // ClearRenderTargetView (vtable[50]): kept for potential future use.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void ClearRenderTargetViewDelegate(
        nint pContext, nint pRTV, float* pColorRGBA);

    // DrawIndexed (vtable[12]) + Draw (vtable[13]): injection point (v0.5.36+).
    // FFXIV does NOT rebind the backbuffer between the 3D composite blit and
    // the 2D UI draws — only one OMSetRenderTargets on the backbuffer per frame.
    // We inject AFTER the first Draw/DrawIndexed call on the backbuffer (composite),
    // so subsequent 2D UI draw calls (chat, hotbar, map) render on top of our rect.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void DrawIndexedDelegate(
        nint pContext, uint indexCount, uint startIndexLocation, int baseVertexLocation);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void DrawDelegate(
        nint pContext, uint vertexCount, uint startVertexLocation);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void DrawIndexedInstancedDelegate(
        nint pContext, uint indexCountPerInstance, uint instanceCount,
        uint startIndexLocation, int baseVertexLocation, uint startInstanceLocation);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void DrawInstancedDelegate(
        nint pContext, uint vertexCountPerInstance, uint instanceCount,
        uint startVertexLocation, uint startInstanceLocation);

    // CopyResource (vtable[47]): diagnostic only — FFXIV doesn't appear to use it for LDR fill.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void CopyResourceDelegate(nint pContext, nint pDstResource, nint pSrcResource);

    // Dispatch (vtable[41]): compute shader dispatch — FFXIV may use this for the tonemap on ~37% of frames.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void DispatchDelegate(nint pContext, uint threadGroupCountX, uint threadGroupCountY, uint threadGroupCountZ);

    // DrawIndexedInstancedIndirect (vtable[39]) / DrawInstancedIndirect (vtable[40]):
    // indirect draw variants — potentially used for tonemap blit on miss frames.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void DrawIndexedInstancedIndirectDelegate(nint pContext, nint pBufferForArgs, uint alignedByteOffsetForArgs);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void DrawInstancedIndirectDelegate(nint pContext, nint pBufferForArgs, uint alignedByteOffsetForArgs);

    // DispatchIndirect (vtable[42]): GPU-driven compute dispatch — reads thread group counts
    // from a buffer. Suspected cause of the ~77% miss frames where no other hook fires.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void DispatchIndirectDelegate(nint pContext, nint pBufferForArgs, uint alignedByteOffsetForArgs);

    private Hook<OMSetRenderTargetsDelegate>?               _omSetRTHook;
    private Hook<ClearRenderTargetViewDelegate>?            _clearRtvHook;
    private Hook<DrawIndexedDelegate>?                      _drawIndexedHook;
    private Hook<DrawDelegate>?                             _drawHook;
    private Hook<DrawIndexedInstancedDelegate>?             _drawIndexedInstancedHook;
    private Hook<DrawInstancedDelegate>?                    _drawInstancedHook;
    private Hook<CopyResourceDelegate>?                     _copyResourceHook;
    private Hook<DispatchDelegate>?                         _dispatchHook;
    private Hook<DrawIndexedInstancedIndirectDelegate>?     _drawIndexedInstancedIndirectHook;
    private Hook<DrawInstancedIndirectDelegate>?            _drawInstancedIndirectHook;
    private Hook<DispatchIndirectDelegate>?                 _dispatchIndirectHook;

    // Re-entrancy guard: prevents ExecuteInlineDraw's internal D3D calls from recursing
    // back into our hook logic. Any detour call that sees _inHookDetour=true immediately
    // calls Original and returns, avoiding nested state mutation or exception leaks.
    [ThreadStatic] private static bool _inHookDetour;

    private ID3D11DepthStencilView? _trackedDsv;
    // Pointer-based cache for CheckDepthCompatibility.
    // _depthCompatible is only valid when both cached ptrs match the current call.
    // This prevents re-doing COM queries + log spam when _trackedDsv is recreated each frame.
    private nint _depthCompatCachedDsvPtr = 0;
    private nint _depthCompatCachedRtvPtr = 0;
    private nint                    _contextPtr;
    private nint _rendererSingletonAddr = 0;  // address of global renderer ptr, resolved via SigScanner at init
    private nint _cachedLiveRtv         = 0;  // GetLiveBackbufferRtv() result cached per frame in PrepareHooks
    private bool _dsvLoggedOnce;
    private int  _cbkFrameCount;

    // 3D→2D transition detection. OMSetRenderTargets tracks whether the previous call
    // bound a DSV (3D scene pass). When it switches to no-DSV (2D UI pass), _inUiPass is
    // set to true so the ClearRTV inject knows it's in the right phase.
    private bool _prevCallHadDsv   = false;
    private nint _prevDsvPtr       = 0;     // DSV ptr from the previous OMSetRT call (0 = no DSV)
    private bool _inUiPass         = false;
    // Set by ClearRTV inject to prevent double-injection in the same frame.
    // Reset by Draw()/DrawBlack() at ImGui time.
    private volatile bool _frameInjectionDone = false;
    // Set when omsetrt-ldr fires (possibly early on NVIDIA before tonemap).
    // Decoupled from _frameInjectionDone so CF-DI/Dispatch can still fire afterward.
    private bool _omSetRtLdrFiredThisFrame = false;

    // Backbuffer identification.
    // We learn the backbuffer texture ptr from _lastNoDsvRtvPtr (the LAST no-DSV
    // OMSetRenderTargets each frame, which is Dalamud's ImGui BB bind) via PrepareHooks.
    // We match by TEXTURE pointer (GetResource) rather than RTV pointer, because FFXIV
    // and Dalamud may create separate RTV COM objects for the same underlying BB texture.
    private readonly System.Collections.Generic.HashSet<nint> _knownBackbufferTexturePtrs = new();
    private readonly System.Collections.Generic.HashSet<nint> _knownBackbufferRtvPtrs     = new();
    // LDR RTV ptrs confirmed to be the post-tonemap composite intermediate.
    // Populated from CF-DI successes + BB-fallback prevNoDsvPtr/OMGetRT.
    // FFXIV rotates the LDR surface across swapchain frames (A→B→A→B...),
    // so we accumulate all seen ptrs so CF-DI fires on either one.
    private readonly System.Collections.Generic.HashSet<nint> _knownLdrRtvPtrs            = new();
    private readonly System.Collections.Generic.HashSet<nint> _checkedRtvPtrs             = new();
    private int _diagOmsetNodsv = 0;
    // Counts how many times the known backbuffer RTV is bound during _inUiPass each frame.
    // Confirmed (v0.5.35 diagnostics): only 1 bind per frame. The 1st (and only) bind is
    // the 3D→backbuffer composite. 2D UI draws follow on the SAME bind without rebinding.
    private int _bbBindCountThisUiPass = 0;
    // All intermediate (non-backbuffer) RTV ptrs seen via OMSetRenderTargets during _inUiPass.
    // Populated lazily across frames (never reset).
    private readonly System.Collections.Generic.HashSet<nint> _inUiPassRtvPtrs = new();
    // Maps intermediate RTV ptr → underlying texture ptr (for cross-referencing composite SRVs).
    // Populated alongside _inUiPassRtvPtrs when GetResource() is called for bb-check.
    private readonly System.Collections.Generic.Dictionary<nint, nint> _rtvToTexture = new();
    // Raw ptr of the backbuffer RTV currently bound (non-zero after first bb bind during _inUiPass).
    private nint _currentBbRtvPtr = 0;
    // Total bb-bind log entries emitted (prevents per-frame spam after pattern is confirmed).
    private int _bbBindLogCount = 0;
    // Most recently cleared intermediate RT during _inUiPass (updated each matching ClearRTV call).
    // This is the injection target: we draw into it BEFORE the composite DrawIndexed reads it.
    // "Last cleared" heuristic: post-processing surfaces are cleared early; the SDR/HUD RT is
    // cleared late (just before content is drawn to it), so _lastClearedUiPassRtvPtr converges
    // to the final composited surface — the correct injection point.
    private nint _lastClearedUiPassRtvPtr = 0;
    // One-shot: logs the first call to PrepareHooks so we know it's running.
    private bool _prepareHooksLoggedOnce = false;
    // ── Scene-pass inject (RendererServiceAlt approach) ──────────────
    // Fire inject when FFXIV transitions AWAY from the 3D scene pass.
    // At that moment all 3D geometry is drawn; depth buffer is valid.
    // We manually re-bind MainSceneDSV+MainSceneRTV and draw our rect.
    // Post-processing + HUD render after → naturally appear in front of rect.
    //
    // MainSceneDSV  = first full-resolution (device w×h) DSV seen before _inUiPass.
    // MainSceneRTV  = first full-res BGRA8 (B8G8R8A8_UNorm) RTV seen alongside MainSceneDSV.
    //                 Analysis: B8G8R8A8_UNorm scores +500; R16G16B16A16_Float scores 0.
    //                 Reset every frame (AutoSetRTV pattern) so it re-latches on first BGRA8 bind.
    // _prevSceneRendered: true when the PREVIOUS OMSetRenderTargets call bound MainDSV+MainRTV.
    //                 Inject fires when _prevSceneRendered=true && currentSceneRendered=false
    //                 (i.e., FFXIV just left the scene pass). RendererServiceAlt pattern.
    private nint _mainSceneDsvPtr        = 0;  // set once, stable
    private nint _mainSceneRtvPtr        = 0;  // updated each frame (reset to 0 in PrepareHooks)
    private bool _mainSceneRtvEverSeen   = false; // latched true on first valid R16 identification; never reset
    private bool _sceneDrawnThisFrame    = false;
    private bool _prevSceneRendered      = false;  // RendererServiceAlt: previous-call scene state
    private bool _omSetRtSceneInjectActive = false;  // re-entrancy guard for scene inject
    private int  _sceneInjectCount       = 0;
    // Post-tonemap inject: counter + cache for the first LDR full-res surface after bloom.
    private int  _postTonemapInjectCount = 0;
    // Per-path counters — exposed via StatusApi to diagnose CF-DI vs OMSetRT split.
    private int  _cfDiCount          = 0;
    private int  _cfDrawCount        = 0;
    private int  _omSetRtCount       = 0;
    private int  _diBbCount          = 0;
    private int  _clearRtvInjectCount = 0;
    private int  _clearRtvCallCount       = 0;  // any ClearRTV on our context
    private int  _clearRtvSceneDrawnCount = 0;  // _sceneDrawnThisFrame was true at ClearRTV time
    private int  _clearRtvLdrCount        = 0;  // IsLdrFullRes was true at ClearRTV time
    // Per-frame: tracks how many CF-Draw HUD draws have been skipped this frame.
    private int  _cfDrawHudSkipCount = 0;
    // Per-frame: whether a Dispatch fired while LDR was the current RTV (compute-tonemap signal).
    private bool _ldrFilledByNonDraw = false;
    // Per-frame: how many Dispatches have been skipped for CF-Dispatch inject this frame.
    private int  _cfDispatchSkipCount = 0;
    // Miss diagnostic: how many DrawIndexed calls hit the right phase but _currentNoDsvRtvPtr was 0.
    private int  _cfDiMissNullPtr       = 0;
    // Miss diagnostic: how many hit right phase, non-null ptr, but cfValid=false (wrong surface type).
    private int  _cfDiMissNotLdr        = 0;
    // Miss diagnostic: how many hit right phase, cfValid=true, but targetMatch failed (ptr mismatch).
    private int  _cfDiMissTargetMismatch = 0;
    // OMSetRT inject diagnostics: WHY CF-DI missed on those frames.
    private int  _omSetRtMissSceneNotDrawn = 0;  // _sceneDrawnThisFrame=false when OMSetRT fires
    private int  _omSetRtMissInUiPassFalse = 0;  // _inUiPass=false when OMSetRT fires (shouldn't happen — we're inside if(_inUiPass))
    private int  _omSetRtMissDrawCall      = 0;  // scene drawn + inUiPass but no draw call matched LDR
    // OMSetRT-LDR inject: fires when FFXIV binds the LDR surface post-scene-end for HUD draws.
    private int  _omSetRtLdrCount          = 0;
    // Whether the last omsetrt's intermediate surface was the backbuffer (NVIDIA: BB already bound).
    private bool _lastInjectWasBackbuffer  = false;
    // Count of omsetrt frames where intermediate was BB (skipped, let BB-inject handle).
    private int  _omSetRtSkippedBbCount   = 0;
    // CopyResource diagnostics.
    private int  _copyResourceTotal        = 0;  // total CopyResource calls seen on our context
    private int  _copyResourceLdrMatch     = 0;  // calls where pDst == _ldrTexPtr
    private int  _cfCopyCount             = 0;  // successful cf-copy injects
    // Dispatch diagnostics.
    private int  _dispatchInWindow         = 0;  // Dispatch calls during inject window (inUiPass+sceneDrawn+preBC+!done)
    private int  _dispatchNoUiPass         = 0;  // Dispatch calls post-scene but _inUiPass=false (blocked by missing transition detection)
    private int  _cfDispatchCount          = 0;  // successful cf-dispatch injects
    private int  _cfDispatchIndirectCount  = 0;  // successful cf-dispatchindirect injects
    private int  _dispatchIndirectInWindow = 0;  // dispatchindirect calls seen in inject window
    // Cache: rtvPtr → isValidPostBloom (full-res + not R16 + not BB). Avoids repeated COM queries.
    private readonly System.Collections.Generic.Dictionary<nint, bool> _postBloomRtvCache = new();
    // Cache: rtvPtr → isSceneRtv (full-res + R16). Avoids repeated COM queries.
    private readonly System.Collections.Generic.Dictionary<nint, bool> _sceneRtvCache = new();
    private int _sceneRtvLogCount = 0;  // throttle per-ptr identification logs
    // Total PrepareHooks calls — used for periodic heartbeat log.
    private int _prepareHooksCallCount = 0;
    // Whether GetLiveBackbufferRtv() has been cross-validated against learned BB textures.
    // Until validated, _cachedLiveRtv is not trusted (NVIDIA-specific renderer init may produce garbage).
    private bool _cachedLiveRtvValidated = false;
    // Last no-DSV RTV seen this frame.
    // The Dalamud ImGui backbuffer bind is the LAST no-DSV OMSetRenderTargets each frame,
    // so _lastNoDsvRtvPtr at the END of frame N is the swapchain backbuffer RTV.
    // We check it at the START of frame N+1 (in PrepareHooks) to learn the bb texture.
    // This replaces the flawed _pendingLearnBackbuffer heuristic which fires too early
    // and captures FFXIV post-processing surfaces instead of the real swapchain bb.
    private nint _lastNoDsvRtvPtr = 0;
    // Current RTV bound without a DSV — updated live by OMSetRenderTargetsDetour.
    // Set to 0 when a DSV-bound call is seen. Used by draw detours to check the
    // active surface without an extra OMGetRenderTargets COM call.
    private nint _currentNoDsvRtvPtr = 0;
    // Cross-frame inject: track the LAST valid LDR full-res RTV seen during draw calls
    // each frame. PrepareHooks promotes it to _targetInjectRtvPtr at frame start.
    // The draw detours inject when they see _targetInjectRtvPtr as the current RTV.
    // This bypasses the unreliable _inUiPass heuristic entirely.
    private nint _lastSeenValidRtvPtr  = 0;
    private nint _targetInjectRtvPtr   = 0;
    // Underlying texture ptr for _targetInjectRtvPtr — learned when CF-DI first fires.
    // Used by CopyResourceDetour to identify when FFXIV copies the tonemap result to LDR.
    private nint _ldrTexPtr            = 0;
    // Cached result of DSV vs backbuffer dimension check.
    // null = not yet checked; true = compatible (use depth); false = mismatch (no depth).
    private bool? _depthCompatible = null;

    // 1×1 black RGBA texture — safety fallback.
    private static readonly byte[] _blackPixelData = { 0, 0, 0, 255 };
    private ID3D11ShaderResourceView? _blackSrv;

    // 2×2 dynamic texture for the idle gradient screensaver.
    private ID3D11Texture2D?          _gradientTex;
    private ID3D11ShaderResourceView? _gradientSrv;
    private float                     _gradientTime = 0f;

    private static readonly float[] _gradientPhaseOffsets = { 0.0f, 0.25f, 0.5f, 0.75f };
    internal static float GradientSpeed = 0.018f;  // hue cycles per second
    // s=0.90, v=0.25: empirically confirmed to avoid bloom center-wash in FFXIV indoor scenes.
    // Center blend ≈ v*(1–s/2) ≈ 0.137 which is capped by BloomCap=0.10 → no white center.
    // Higher v or lower s risks center blooming to white at typical indoor bloom thresholds.
    internal static float GradientS     = 0.90f;   // saturation 0–1
    internal static float GradientV     = 0.25f;   // value/brightness 0–1

    // Own texture loaded from the image file.
    private ID3D11ShaderResourceView? _imageSrv;
    private string                    _loadedImagePath = string.Empty;

    // ── Diagnostic fields (v0.5.120 investigation: why ldrInjectCount=0) ─────
    // Capture pre-reset state each PrepareHooks so we can log what happened last frame.
    private nint _diagPrevBbRtv     = 0;    // _currentBbRtvPtr at end of previous frame
    private bool _diagPrevFrameInj  = false; // _frameInjectionDone at end of previous frame
    private nint _diagPrevLastSeen  = 0;    // _lastSeenValidRtvPtr at end of previous frame (= _targetInjectRtvPtr this frame)
    private int  _diagFrameCount    = 0;    // total PrepareHooks calls (resets logging at 5 frames)
    private int  _diagDiCount       = 0;    // DrawIndexed calls logged this frame
    private int  _diagDrawCount     = 0;    // Draw calls logged this frame

    // ── Inject-path diagnostics (v0.5.124+) — exposed via /inject API ─────────
    // Toggle flags: settable via /set/omsetrtenable and /set/bbdrawskip.
    // ── Inject-path toggles — all default to their working-state values ──────
    internal static bool CfDiEnabled          = true;  // CF-DI: first DrawIndexed on LDR in UI pass (after tonemap, before HUD) — primary inject path
    internal static bool CfDrawEnabled        = true;  // CF-Draw: fires on first Draw to LDR in UI pass; preInject puts TV before HUD (NVIDIA DLSS path)
    internal static bool ClearRtvInjectEnabled = false; // ClearRTV inject: FFXIV never calls ClearRTV on LDR (confirmed) — disabled
    internal static bool OmSetRtLdrEnabled    = true;  // OMSetRT-LDR: second LDR bind (after tonemap) → inject before HUD
    internal static bool OmSetRtInjectEnabled = true;  // omsetrt BB-bind: inject into intermediate at BB-bind time (compute-tonemap fallback)
    internal static bool AlphaBlendInject     = true;  // omsetrt: use InvDestAlpha blend — TV fills LDR alpha=0 areas (behind HUD if HUD=alpha1)
    internal static bool CfDispatchEnabled    = false; // CF-Dispatch: DISABLED — fires on wrong compute (pre-tonemap), overwrites rect
    internal static int  CfDispatchSkip       = 0;    // skip first N Dispatches in inject window before CF-Dispatch fires (sweep to find tonemap compute)
    internal static bool CfDrawPreInject      = true;  // inject BEFORE Original in CF-Draw when _ldrFilledByNonDraw (DLSS/compute filled LDR → first Draw is HUD)
    internal static int  CfDrawHudSkip        = 0;    // skip first N Draw calls to LDR before CF-Draw fires (skip HUD draws to probe depth)
    internal static int  BbDrawSkip           = 0;    // skip N draws on BB before injecting (sweep to find correct timing)
    internal static bool LdrLog               = false; // one-frame verbose log of every no-DSV OMSetRT + IsLdrFullRes result

    // ── Frame-trace capture ──────────────────────────────────────────────────
    // Set TraceFramesRemaining = N via /fftv trace N or the Debug-tab button to log every
    // hook call for N frames. Each entry emits "[FFTV-TRACE #NNNNN] ..." at Info level.
    internal static int TraceFramesRemaining = 0;
    internal static int TraceSequence        = 0; // monotonic sequence within capture
    internal static void TraceLog(string line)
    {
        if (TraceFramesRemaining <= 0) return;
        int seq = System.Threading.Interlocked.Increment(ref TraceSequence);
        Plugin.Log.Info($"[FFTV-TRACE #{seq:D5}] {line}");
    }

    // Per-frame state — captured at end of each frame by PrepareHooks.
    private string _lastInjectPath      = "none";   // which inject path fired ("omsetrt","omsetrt-fallback","cf-di","cf-draw","di-bb","draw-bb","none")
    private nint   _lastInjectRtvPtr    = 0;        // RTV pointer injected into
    private bool   _lastFallbackUsed    = false;    // true when prevNoDsvPtr was used (OMGetRenderTargets returned null)
    private string _lastInjectFmt       = "unknown";// DXGI format of inject surface
    private int    _lastInjectW         = 0;        // dimensions of inject surface
    private int    _lastInjectH         = 0;
    private nint   _lastIntermediateGot = 0;        // raw ptr OMGetRenderTargets returned at BB-bind (0 = null)
    private nint   _lastPrevNoDsvPtr    = 0;        // prevNoDsvPtr captured at BB-bind time
    private int    _bbDrawCount         = 0;        // Draw/DrawIndexed calls seen on BB this frame (not counting injected one)
    private int    _diagPrevBbDrawCount = 0;        // previous frame's _bbDrawCount (safe for background thread)

    private bool _initialized;
    public bool IsAvailable => _initialized;
    public bool HasTexture  => _imageSrv != null || (_videoPlayer?.HasTexture == true) || (_browserPlayer?.HasTexture == true);

    // ── Diagnostic properties (read by StatusApi on background thread) ────────
    public int  SceneInjectCount     => _sceneInjectCount;
    public int  LdrInjectCount       => _postTonemapInjectCount;
    public int  CfDiCount            => _cfDiCount;
    public int  CfDrawCount          => _cfDrawCount;
    public bool LdrFilledByNonDraw   => _ldrFilledByNonDraw;
    public int  OmSetRtCount         => _omSetRtCount;
    public int  DiBbCount            => _diBbCount;
    public int  ClearRtvInjectCount      => _clearRtvInjectCount;
    public int  ClearRtvCallCount        => _clearRtvCallCount;
    public int  ClearRtvSceneDrawnCount  => _clearRtvSceneDrawnCount;
    public int  ClearRtvLdrCount         => _clearRtvLdrCount;
    public int  CfDiMissNullPtr          => _cfDiMissNullPtr;
    public int  CfDiMissNotLdr           => _cfDiMissNotLdr;
    public int  CfDiMissTargetMismatch   => _cfDiMissTargetMismatch;
    public int  OmSetRtMissSceneNotDrawn => _omSetRtMissSceneNotDrawn;
    public int  OmSetRtMissInUiPassFalse => _omSetRtMissInUiPassFalse;
    public int  OmSetRtMissDrawCall      => _omSetRtMissDrawCall;
    public int  OmSetRtLdrCount          => _omSetRtLdrCount;
    public bool LastInjectWasBackbuffer  => _lastInjectWasBackbuffer;
    public int  OmSetRtSkippedBbCount   => _omSetRtSkippedBbCount;
    public int  CopyResourceTotal        => _copyResourceTotal;
    public int  CopyResourceLdrMatch     => _copyResourceLdrMatch;
    public int  CfCopyCount             => _cfCopyCount;
    public long LdrTexPtr               => (long)_ldrTexPtr;
    public int  DispatchInWindow         => _dispatchInWindow;
    public int  DispatchNoUiPass         => _dispatchNoUiPass;
    public int  CfDispatchCount              => _cfDispatchCount;
    public int  CfDispatchIndirectCount      => _cfDispatchIndirectCount;
    public int  DispatchIndirectInWindow     => _dispatchIndirectInWindow;
    // _mainSceneDsvPtr is never reset — stable once set.
    public bool MainSceneDsvSet      => _mainSceneDsvPtr != 0;
    // _mainSceneRtvPtr is reset to 0 every frame in PrepareHooks — reading it from a
    // background thread almost always sees 0. Use MainSceneRtvEverSeen instead.
    public bool MainSceneRtvEverSeen => _mainSceneRtvEverSeen;
    public bool ActiveSrvNonNull     => _activeSrv != null;
    // Inject-path diagnostics.
    public string LastInjectPath      => _lastInjectPath;
    public long   LastInjectRtvPtr    => (long)_lastInjectRtvPtr;
    public bool   LastFallbackUsed    => _lastFallbackUsed;
    public string LastInjectFmt       => _lastInjectFmt;
    public int    LastInjectW         => _lastInjectW;
    public int    LastInjectH         => _lastInjectH;
    public long   LastIntermediateGot => (long)_lastIntermediateGot;
    public long   LastPrevNoDsvPtr    => (long)_lastPrevNoDsvPtr;
    public int    BbDrawCount         => _diagPrevBbDrawCount;
    public int    BbRtvCount          => _knownBackbufferRtvPtrs.Count;
    public int    BbTexCount          => _knownBackbufferTexturePtrs.Count;
    public long   TargetInjectPtr     => (long)_targetInjectRtvPtr;
    public long   LastSeenValidPtr    => (long)_diagPrevLastSeen;  // _lastSeenValidRtvPtr from end of previous frame
    public string ActiveSrvSource
    {
        get
        {
            if (_activeSrv == null)             return "null";
            if (_activeSrv == _gradientSrv)     return "gradient";
            if (_activeSrv == _blackSrv)        return "black";
            if (_activeSrv == _imageSrv)        return "image";
            if (_activeSrv == _videoPlayer?.FrameSrv)  return "video";
            if (_activeSrv == _browserPlayer?.FrameSrv) return "browser";
            return "unknown";
        }
    }
    public bool BackbufferLearned    => _knownBackbufferTexturePtrs.Count > 0;
    public int  CbkFrameCount        => _cbkFrameCount;  // increments only after Context.Draw(6,0) fires

    // Stored each PrepareHooks call — safe to read from background thread (reference copy).
    public Matrix4x4      StoredViewProj => _storedViewProj;
    public ScreenDefinition? StoredScreen => _storedScreen;

    /// <summary>Returns the FFXIV render target resolution (from KernelDevice). Safe from any thread.</summary>
    public (int W, int H) DeviceResolution
    {
        get
        {
            var kdev = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance();
            return kdev != null ? ((int)kdev->Width, (int)kdev->Height) : (0, 0);
        }
    }

    private ID3D11ShaderResourceView? _activeSrv;

    // Stored each Draw() call for use in the ClearRTV inject (one frame stale — intentional).
    private Matrix4x4        _storedViewProj;
    private ScreenDefinition? _storedScreen;

    // ── Cbuffer layout ────────────────────────────────────────────────────────
    // Single 160-byte cbuffer at register b0, bound to both VS and PS.
    [StructLayout(LayoutKind.Sequential)]
    private struct CbParams
    {
        public Matrix4x4 ViewProj;        // 64 bytes — camera view * projection
        public Matrix4x4 ScreenTransform; // 64 bytes — TRS for the screen in world space
        public float     Brightness;      // 4 — linear exposure multiplier
        public float     Gamma;           // 4 — power curve (1/Gamma applied to rgb)
        public float     Contrast;        // 4 — contrast around 0.5 midpoint
        public float     BloomCap;        // 4 — maximum linear component value; pixels above this are proportionally clamped to prevent bloom
        public Vector4   Tint;            // 16 — rgba multiplier (1,1,1,1 = no change)
    }

    // ── HLSL ─────────────────────────────────────────────────────────────────

    // CBUFFER_DEF is the shared cbuffer declaration used in both VS and PS.
    private const string CBUFFER_DEF = @"
cbuffer CbParams : register(b0) {
    row_major float4x4 ViewProj;
    row_major float4x4 ScreenTransform;
    float Brightness; float Gamma; float Contrast; float BloomCap;
    float4 Tint;
};";

    // VS: generates a flat quad from SV_VertexID.
    // Local-space positions form a unit quad in the XY plane.
    // ScreenTransform (TRS) maps local → world; ViewProj maps world → clip.
    // UV is generated per vertex and interpolates correctly through the rasterizer.
    private const string VS_SRC = CBUFFER_DEF + @"
static const float3 kPos[6] = {
    float3(-0.5f,  0.5f, 0.0f),  // TL
    float3( 0.5f,  0.5f, 0.0f),  // TR
    float3(-0.5f, -0.5f, 0.0f),  // BL
    float3( 0.5f,  0.5f, 0.0f),  // TR
    float3( 0.5f, -0.5f, 0.0f),  // BR
    float3(-0.5f, -0.5f, 0.0f),  // BL
};
static const float2 kUV[6] = {
    float2(0.0f, 0.0f), float2(1.0f, 0.0f), float2(0.0f, 1.0f),
    float2(1.0f, 0.0f), float2(1.0f, 1.0f), float2(0.0f, 1.0f),
};
struct VSOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
VSOut main(uint id : SV_VertexID) {
    float4 world = mul(float4(kPos[id], 1.0f), ScreenTransform);
    VSOut o;
    o.pos = mul(world, ViewProj);
    o.uv  = kUV[id];
    return o;
}";

    // PS: sample → sRGB-to-linear → brightness/contrast/gamma → bloom cap → tint.
    //
    // WHY sRGB-to-linear: all source textures are B8G8R8A8_UNorm (non-sRGB flag), so
    // tex.Sample returns gamma-encoded sRGB [0,1]. We inject into R16G16B16A16_Float
    // which is a LINEAR HDR buffer. Writing sRGB values directly means mid-tones land
    // at their sRGB value (0.5) rather than the linearised value (0.214), which FFXIV's
    // ACES tonemapper maps back to roughly the right perceptual brightness — empirically
    // this looks correct. Applying pow(x,2.2) would darken mid-tones by ~4–5x and make
    // the image appear nearly black regardless of BloomCap setting. DO NOT add that conversion.
    //
    // WHY bloom cap: the scene inject fires PRE-BLOOM into R16. FFXIV's bloom pass reads
    // R16 and amplifies any pixel above its luminance threshold (empirically < 0.5).
    // We clamp the maximum component to BloomCap (cbuffer value, user-configurable)
    // so no pixel exceeds the threshold. BloomCap 0 = disabled. Default 0.35.
    private const string PS_SRC = CBUFFER_DEF + @"
Texture2D    tex  : register(t0);
SamplerState samp : register(s0);
struct VSOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
float4 main(VSOut input) : SV_TARGET {
    float4 color = tex.Sample(samp, input.uv);
    // No sRGB→linear conversion: sRGB values written directly to R16 look correct after
    // FFXIV's ACES tonemapper. Adding pow(x,2.2) here makes the image nearly black.
    color.rgb *= Brightness;
    color.rgb  = saturate((color.rgb - 0.5f) * Contrast + 0.5f);
    color.rgb  = pow(saturate(color.rgb), 1.0f / max(Gamma, 0.001f));
    color.rgb *= Tint.rgb;
    // Bloom cap: clamp proportionally so no component exceeds BloomCap.
    // BloomCap <= 0 means no cap -- skip clamping entirely.
    float maxComp = max(color.r, max(color.g, color.b));
    if (BloomCap > 0.001f && maxComp > BloomCap)
        color.rgb *= BloomCap / maxComp;
    return float4(color.rgb, color.a * Tint.a);
}";

    // LDR pixel shader: used for the post-tonemap LDR inject pass.
    // Source textures are B8G8R8A8_UNorm (sRGB-encoded). The LDR BGRA8 surface stores
    // display-ready values — writing sRGB values directly is correct, NO sRGB-to-linear
    // conversion needed. No bloom cap — this inject fires post-bloom.
    private const string PS_LDR_SRC = CBUFFER_DEF + @"
Texture2D    tex  : register(t0);
SamplerState samp : register(s0);
struct VSOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
float4 main(VSOut input) : SV_TARGET {
    float4 color = tex.Sample(samp, input.uv);
    color.rgb *= Brightness;
    color.rgb  = saturate((color.rgb - 0.5f) * Contrast + 0.5f);
    color.rgb  = pow(saturate(color.rgb), 1.0f / max(Gamma, 0.001f));
    color.rgb *= Tint.rgb;
    return float4(color.rgb, color.a * Tint.a);
}";

    // Diagnostic shader — solid red, no texture read. Used by DebugShaderRed toggle
    // to verify the inject draw path is reaching the target RTV.
    private const string PS_LDR_DEBUG_RED_SRC = @"
struct VSOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
float4 main(VSOut input) : SV_TARGET { return float4(1.0f, 0.0f, 0.0f, 1.0f); }";

    private readonly IGameInteropProvider _interop;

    // ── Constructor ───────────────────────────────────────────────────────────
    // Pre-compiled shader bytecode — compiled on a background thread at construction
    // to avoid a 300ms+ hitch on the render thread when TryInitialize() is first called.
    private System.Threading.Tasks.Task<(ReadOnlyMemory<byte> vs, ReadOnlyMemory<byte> ps)>? _shaderCompileTask;

    public D3DRenderer(IGameInteropProvider interop)
    {
        _interop = interop;
        // Kick off shader compilation immediately on a thread-pool thread.
        // TryInitialize will wait for completion (IsCompleted check) without blocking the render thread.
        _shaderCompileTask = System.Threading.Tasks.Task.Run(() =>
        {
            var vs = Compiler.Compile(VS_SRC, "main", "screen_vs", "vs_5_0");
            var ps = Compiler.Compile(PS_SRC, "main", "screen_ps", "ps_5_0");
            return (vs, ps);
        });
    }

    // ── Init ──────────────────────────────────────────────────────────────────
    public bool TryInitialize()
    {
        if (_initialized) return true;

        // Wait for background shader compilation before touching the D3D device.
        if (_shaderCompileTask != null && !_shaderCompileTask.IsCompleted)
        {
            Plugin.Log.Debug("[FFXIV-TV] D3DRenderer: shaders still compiling — deferring init.");
            return false;
        }

        var kernelDevice = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance();
        if (kernelDevice == null)
        {
            Plugin.Log.Debug("[FFXIV-TV] D3DRenderer: Kernel device not ready yet.");
            return false;
        }

        nint devicePtr = (nint)kernelDevice->D3D11Forwarder;
        if (devicePtr == 0)
        {
            Plugin.Log.Warning("[FFXIV-TV] D3DRenderer: null D3D11 device pointer.");
            return false;
        }

        _device = new ID3D11Device(devicePtr);
        _device.AddRef();
        _context    = _device.ImmediateContext;
        _contextPtr = _context.NativePointer;

        Plugin.Log.Info($"[FFXIV-TV] D3DRenderer: device=0x{devicePtr:X}");
        ResolveRendererSingleton();
        try
        {
            using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
            using var adapter    = dxgiDevice.GetAdapter();
            var desc = adapter.Description;
            Plugin.Log.Info($"[FFXIV-TV] GPU: {desc.Description} VRAM={desc.DedicatedVideoMemory / 1024 / 1024}MB");
        }
        catch (Exception ex) { Plugin.Log.Warning($"[FFXIV-TV] GPU info unavailable: {ex.Message}"); }

        try
        {
            CreateResources();
            InstallOMSetRTHook();
            _initialized = true;
            Plugin.Log.Info("[FFXIV-TV] D3DRenderer initialized — Phase 2 active (ScreenTransform shader).");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[FFXIV-TV] D3DRenderer init failed: {ex.Message}");
            DisposeResources();
            return false;
        }
    }

    private void InstallOMSetRTHook()
    {
        nint* vtable = *(nint**)_contextPtr;

        // vtable[33] = OMSetRenderTargets — DSV tracking + backbuffer identification.
        _omSetRTHook = _interop.HookFromAddress<OMSetRenderTargetsDelegate>(
            vtable[33], OMSetRenderTargetsDetour);
        _omSetRTHook.Enable();

        // vtable[50] = ClearRenderTargetView — kept, no active injection.
        _clearRtvHook = _interop.HookFromAddress<ClearRenderTargetViewDelegate>(
            vtable[50], ClearRenderTargetViewDetour);
        _clearRtvHook.Enable();

        // vtable[12] = DrawIndexed, vtable[13] = Draw — injection point (v0.5.36+).
        // FFXIV has only ONE backbuffer OMSetRenderTargets per frame (confirmed v0.5.35).
        // We inject AFTER the first draw call on the backbuffer (3D composite blit) so
        // all subsequent 2D UI draw calls render on top of our rect.
        _drawIndexedHook = _interop.HookFromAddress<DrawIndexedDelegate>(
            vtable[12], DrawIndexedDetour);
        _drawIndexedHook.Enable();

        _drawHook = _interop.HookFromAddress<DrawDelegate>(
            vtable[13], DrawDetour);
        _drawHook.Enable();

        // vtable[20] = DrawIndexedInstanced, vtable[21] = DrawInstanced — catch tonemap blits
        // that FFXIV issues via instanced draw calls (~30% of frames miss CF-DI/CF-Draw).
        _drawIndexedInstancedHook = _interop.HookFromAddress<DrawIndexedInstancedDelegate>(
            vtable[20], DrawIndexedInstancedDetour);
        _drawIndexedInstancedHook.Enable();

        _drawInstancedHook = _interop.HookFromAddress<DrawInstancedDelegate>(
            vtable[21], DrawInstancedDetour);
        _drawInstancedHook.Enable();

        // vtable[47] = CopyResource — diagnostic hook.
        _copyResourceHook = _interop.HookFromAddress<CopyResourceDelegate>(
            vtable[47], CopyResourceDetour);
        _copyResourceHook.Enable();

        // vtable[41] = Dispatch — compute shader; FFXIV may use compute tonemap on ~37% of frames.
        _dispatchHook = _interop.HookFromAddress<DispatchDelegate>(
            vtable[41], DispatchDetour);
        _dispatchHook.Enable();

        // vtable[39] = DrawIndexedInstancedIndirect, vtable[40] = DrawInstancedIndirect.
        _drawIndexedInstancedIndirectHook = _interop.HookFromAddress<DrawIndexedInstancedIndirectDelegate>(
            vtable[39], DrawIndexedInstancedIndirectDetour);
        _drawIndexedInstancedIndirectHook.Enable();

        _drawInstancedIndirectHook = _interop.HookFromAddress<DrawInstancedIndirectDelegate>(
            vtable[40], DrawInstancedIndirectDetour);
        _drawInstancedIndirectHook.Enable();

        // vtable[42] = DispatchIndirect — GPU-driven compute; suspected ~77% miss path.
        _dispatchIndirectHook = _interop.HookFromAddress<DispatchIndirectDelegate>(
            vtable[42], DispatchIndirectDetour);
        _dispatchIndirectHook.Enable();

        Plugin.Log.Info("[FFXIV-TV] OMSetRenderTargets + ClearRTV + Draw* + Dispatch + DispatchIndirect + CopyResource hooks installed.");
    }

    /// <summary>
    /// Resolves the FFXIV renderer singleton global pointer at plugin init time.
    /// Pattern: MOV RCX, [DAT_1427F1A80] — first instruction of the render-submit function.
    /// </summary>
    private unsafe void ResolveRendererSingleton()
    {
        try
        {
            nint instrAddr = Plugin.SigScanner.ScanText("48 8B 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 85 C0");
            if (instrAddr == 0) { Plugin.Log.Warning("[FFXIV-TV] Renderer singleton: sig not found"); return; }
            int rel32 = *(int*)(instrAddr + 3);
            _rendererSingletonAddr = instrAddr + 7 + rel32;
            nint rendererTest = *(nint*)_rendererSingletonAddr;
            Plugin.Log.Info($"[FFXIV-TV] Renderer singleton resolved: ptr=0x{_rendererSingletonAddr:X} renderer=0x{rendererTest:X}");
        }
        catch (Exception ex) { Plugin.Log.Warning($"[FFXIV-TV] Renderer singleton resolve failed: {ex.Message}"); }
    }

    /// <summary>
    /// Returns the current frame's active backbuffer RTV pointer by walking the renderer singleton.
    /// renderer→+0x70→scWrapper→+0x60→rtDesc→+0x68→bbRtv (all confirmed offsets for patch 7.4.0).
    /// Returns 0 if singleton not resolved or any ptr in the chain is null.
    /// </summary>
    private unsafe nint GetLiveBackbufferRtv()
    {
        try
        {
            if (_rendererSingletonAddr == 0) return 0;
            var renderer  = *(nint*)_rendererSingletonAddr;
            if (renderer == 0) return 0;
            var scWrapper = *(nint*)(renderer + 0x70);
            if (scWrapper == 0) return 0;
            var rtDesc    = *(nint*)(scWrapper + 0x60);
            if (rtDesc == 0) return 0;
            return *(nint*)(rtDesc + 0x68);
        }
        catch { return 0; }
    }

    /// <summary>
    /// Returns true if rtvPtr is the current backbuffer — either from the live singleton read
    /// (_cachedLiveRtv, primary) or from the learned RTV set (fallback for when singleton is 0).
    /// </summary>
    private bool IsBackbuffer(nint rtvPtr) =>
        rtvPtr != 0 && ((_cachedLiveRtv != 0 && rtvPtr == _cachedLiveRtv) || _knownBackbufferRtvPtrs.Contains(rtvPtr));

    private void OMSetRenderTargetsDetour(nint pCtx, uint numViews, nint* ppRTVs, nint pDSV)
    {
        if (_inHookDetour) { try { _omSetRTHook?.Original(pCtx, numViews, ppRTVs, pDSV); } catch { } return; }
        _inHookDetour = true;
        bool calledOriginal = false;
        try
        {
            if (pCtx == _contextPtr)
            {
                // Snapshot before any state mutation inside this call.
                // Used by OMSetRT-LDR inject to distinguish "scene-end OMSetRT" (false) from
                // "post-scene-end LDR bind for HUD draws" (true).
                bool sceneWasDrawnBeforeThisCall = _sceneDrawnThisFrame;
                if (TraceFramesRemaining > 0)
                {
                    string rtvs = "null";
                    if (ppRTVs != null && numViews > 0)
                    {
                        var sb = new System.Text.StringBuilder();
                        for (uint i = 0; i < numViews; i++) { if (i > 0) sb.Append(","); sb.Append($"0x{ppRTVs[i]:X}"); }
                        rtvs = sb.ToString();
                    }
                    TraceLog($"OMSetRT numViews={numViews} rtvs=[{rtvs}] dsv=0x{pDSV:X} frame={_cbkFrameCount} sceneDrawn={_sceneDrawnThisFrame} inUiPass={_inUiPass}");
                }
                // Only consider calls that bind at least one RTV (ignore depth-only shadow passes).
                bool hasRtvs = numViews > 0 && ppRTVs != null && ppRTVs[0] != 0;
                if (hasRtvs)
                {
                    bool hasDsv = pDSV != 0;

                    // Track the main-scene DSV for depth testing during our inject.
                    // Only update BEFORE _inUiPass — once we enter post-processing/2D, freeze it.
                    // Skip update during scene inject (re-entrant call) to avoid disposing the
                    // DSV wrapper we are currently using inside ExecuteInlineDraw.
                    // Lock _trackedDsv to MainDSV once scene inject has run (_sceneDrawnThisFrame=true).
                    // Post-processing may bind smaller/different DSVs; we must NOT overwrite the
                    // MainDSV pointer that the LDR inject (Pass 2) will use for depth testing.
                    if (hasDsv && !_inUiPass && !_omSetRtSceneInjectActive && !_sceneDrawnThisFrame)
                    {
                        _trackedDsv?.Dispose();
                        _trackedDsv = new ID3D11DepthStencilView(pDSV);
                        _trackedDsv.AddRef();
                        _depthCompatible = null; // reset check when DSV changes
                        if (!_dsvLoggedOnce)
                        {
                            _dsvLoggedOnce = true;
                            Plugin.Log.Info($"[FFXIV-TV] main-scene DSV captured: 0x{pDSV:X}");
                        }
                    }

                    if (hasDsv && !_inUiPass)
                    {
                        // ── Scene-pass identification ──────────────────
                        // Step 1: identify MainSceneDSV (one-time).
                        var kdev = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance();
                        if (kdev != null && _mainSceneDsvPtr == 0)
                        {
                            try
                            {
                                var dsvView = new ID3D11DepthStencilView(pDSV);
                                dsvView.AddRef();
                                try
                                {
                                    using var dsvRes = dsvView.Resource;
                                    var dsvTex = new ID3D11Texture2D(dsvRes.NativePointer);
                                    dsvTex.AddRef();
                                    try
                                    {
                                        var d = dsvTex.Description;
                                        if (d.Width == kdev->Width && d.Height == kdev->Height)
                                        {
                                            _mainSceneDsvPtr = pDSV;
                                            Plugin.Log.Info($"[FFXIV-TV] MainSceneDSV: 0x{pDSV:X} {d.Width}x{d.Height}");
                                        }
                                    }
                                    finally { dsvTex.Dispose(); }
                                }
                                finally { dsvView.Dispose(); }
                            }
                            catch (Exception ex) { Plugin.Log.Warning($"[FFXIV-TV] MainSceneDSV check: {ex.Message}"); }
                        }

                        // Step 2: update MainSceneRTV when paired with MainSceneDSV.
                        // Uses _sceneRtvCache to avoid re-querying known pointers.
                        if (kdev != null && _mainSceneDsvPtr != 0 && pDSV == _mainSceneDsvPtr)
                        {
                            nint rtvPtr3d = ppRTVs[0];
                            if (!_sceneRtvCache.TryGetValue(rtvPtr3d, out bool isSceneRtv))
                            {
                                isSceneRtv = false;
                                try
                                {
                                    var rv = new ID3D11RenderTargetView(rtvPtr3d);
                                    rv.AddRef();
                                    try
                                    {
                                        using var rtvRes = rv.Resource;
                                        var rtvTex = new ID3D11Texture2D(rtvRes.NativePointer);
                                        rtvTex.AddRef();
                                        try
                                        {
                                            var d = rtvTex.Description;
                                            bool fullRes = d.Width == kdev->Width && d.Height == kdev->Height;
                                            // RendererServiceAlt pattern targets R16G16B16A16_Float —
                                            // FFXIV's HDR accumulation buffer (MainRTV).
                                            // The 71973 DrawIndexed READS from R16 as SRV and writes to a
                                            // different intermediate → our inject into R16 survives into
                                            // the post-processing chain. BGRA8 fails because FFXIV clears
                                            // it immediately after binding AND 71973 overwrites it.
                                            bool isR16 = d.Format == Format.R16G16B16A16_Float;
                                            // Don't require fullRes: with DLSS the R16 HDR buffer
                                            // renders at sub-native resolution but still uses the
                                            // full-res mainSceneDSV. Requiring fullRes would
                                            // prevent _mainSceneRtvPtr from being set on NVIDIA
                                            // with DLSS, blocking the BGRA8 _inUiPass fallback.
                                            isSceneRtv = isR16;
                                            if (_sceneRtvLogCount < 15)
                                            {
                                                _sceneRtvLogCount++;
                                                Plugin.Log.Info($"[FFXIV-TV] SceneRTV candidate 0x{rtvPtr3d:X} {d.Width}x{d.Height} fmt={d.Format} fullRes={fullRes} isR16={isR16} → isScene={isSceneRtv}");
                                            }
                                        }
                                        finally { rtvTex.Dispose(); }
                                    }
                                    finally { rv.Dispose(); }
                                }
                                catch (Exception ex) { Plugin.Log.Warning($"[FFXIV-TV] SceneRTV check 0x{rtvPtr3d:X}: {ex.Message}"); }
                                _sceneRtvCache[rtvPtr3d] = isSceneRtv;
                            }
                            if (isSceneRtv)
                            {
                                _mainSceneRtvPtr     = rtvPtr3d;  // always update → converges to LAST qualifying R16 each frame (AutoSetRTV pattern)
                                _mainSceneRtvEverSeen = true;      // latched for diagnostic — never reset
                            }
                        }
                    }

                    // Detect 3D→2D transition: first RTV-binding no-DSV call after the main scene DSV.
                    // Guard: _prevDsvPtr == _mainSceneDsvPtr ensures we only trigger on the real scene-end
                    // transition, not on shadow map / reflection passes that also do DSV→no-DSV.
                    // Fallback: if _mainSceneDsvPtr not yet identified, accept any DSV→no-DSV (first frame).
                    bool isMainSceneTransition = _prevCallHadDsv
                        && (_mainSceneDsvPtr == 0 || _prevDsvPtr == _mainSceneDsvPtr);
                    if (!hasDsv && isMainSceneTransition)
                    {
                        _inUiPass = true;
                        if (!_sceneDrawnThisFrame)
                        {
                            _sceneDrawnThisFrame = true;
                            if (_mainSceneDsvPtr != 0)
                            {
                                _trackedDsv?.Dispose();
                                _trackedDsv = new ID3D11DepthStencilView(_mainSceneDsvPtr);
                                _trackedDsv.AddRef();
                                _depthCompatible = null;
                            }
                        }
                    }
                    // BGRA8 fallback: set _inUiPass (and _sceneDrawnThisFrame if needed) when a
                    // full-res BGRA8 surface is bound AND we've seen the main scene RTV this frame.
                    // This fixes the ~77% miss rate caused by intermediate DSV-bound passes between
                    // Stage 1 and Stage 2 that corrupt _prevDsvPtr and break isMainSceneTransition.
                    // _mainSceneRtvPtr is reset to 0 in PrepareHooks and set when the R16 HDR scene
                    // RTV is bound with mainSceneDSV — guarantees Stage 1 is in progress/complete.
                    // BGRA8 never appears during Stage 1 (FFXIV uses R16 for HDR), so any BGRA8 bind
                    // after _mainSceneRtvPtr is set is definitively post-scene.
                    if (!_inUiPass && hasRtvs && !hasDsv && _mainSceneRtvPtr != 0)
                    {
                        nint candidatePtr = ppRTVs[0];
                        if (candidatePtr != 0)
                        {
                            if (!_postBloomRtvCache.TryGetValue(candidatePtr, out bool isLdr))
                            {
                                isLdr = IsLdrFullRes(candidatePtr);
                                _postBloomRtvCache[candidatePtr] = isLdr;
                            }
                            if (isLdr)
                            {
                                _inUiPass = true;
                                if (!_sceneDrawnThisFrame)
                                {
                                    _sceneDrawnThisFrame = true;
                                    if (_mainSceneDsvPtr != 0)
                                    {
                                        _trackedDsv?.Dispose();
                                        _trackedDsv = new ID3D11DepthStencilView(_mainSceneDsvPtr);
                                        _trackedDsv.AddRef();
                                        _depthCompatible = null;
                                    }
                                }
                            }
                        }
                    }

                    _prevCallHadDsv = hasDsv;
                    if (hasDsv) _prevDsvPtr = pDSV;
                    else        _prevDsvPtr = 0;

                    // Track current no-DSV RTV (cleared when DSV is bound).
                    _currentNoDsvRtvPtr = hasDsv ? 0 : (hasRtvs ? ppRTVs[0] : 0);

                    if (!hasDsv)
                    {
                        nint rtvPtr = ppRTVs[0];

                        // Save the previous no-DSV RTV BEFORE overwriting.
                        // At BB-bind time this holds the last post-processing surface
                        // (post-bloom R16 or similar) — used as OMSetRT inject fallback
                        // when FFXIV unbinds R16 as RT (to use as SRV) before binding BB,
                        // which causes OMGetRenderTargets to return null.
                        nint prevNoDsvPtr = _lastNoDsvRtvPtr;

                        // Track the last no-DSV RTV seen this frame.
                        // The Dalamud ImGui bb bind is the LAST one — PrepareHooks on
                        // the next frame uses this to learn the real swapchain backbuffer.
                        _lastNoDsvRtvPtr = rtvPtr;

                        // STEP B: During inUiPass, identify and inject on the backbuffer bind.
                        // Note: backbuffer texture learning happens via _lastNoDsvRtvPtr in
                        // PrepareHooks (the LAST no-DSV RTV each frame = Dalamud's ImGui BB bind).
                        // STEP A (_pendingLearnBackbuffer) was removed — it fired on the FIRST
                        // no-DSV call which on NVIDIA is an HBAO+/SSAO surface, not the BB.
                        if (_inUiPass)
                        {
                            // Track all intermediate (non-backbuffer) RTV ptrs seen during _inUiPass.
                            // Used by ClearRTV hook to find the 2D HUD RT (cleared+drawn-to surface).
                            if (!IsBackbuffer(rtvPtr))
                                _inUiPassRtvPtrs.Add(rtvPtr);

                            // Check if this RTV is backed by a known backbuffer texture.
                            if (!IsBackbuffer(rtvPtr)
                                && !_checkedRtvPtrs.Contains(rtvPtr)
                                && _knownBackbufferTexturePtrs.Count > 0)
                            {
                                _checkedRtvPtrs.Add(rtvPtr);
                                try
                                {
                                    var cv = new ID3D11View(rtvPtr);
                                    cv.AddRef();
                                    try
                                    {
                                        using var cres = cv.Resource;
                                        nint texPtr = cres.NativePointer;
                                        bool isBB = _knownBackbufferTexturePtrs.Contains(texPtr);
                                        if (_diagOmsetNodsv < 20)
                                        {
                                            _diagOmsetNodsv++;
                                            Plugin.Log.Info($"[FFXIV-TV] OMSetRT inUiPass rtv=0x{rtvPtr:X} tex=0x{texPtr:X} isBB={isBB}");
                                        }
                                        if (isBB)
                                        {
                                            _knownBackbufferRtvPtrs.Add(rtvPtr);
                                        }
                                        else
                                        {
                                            // Store texture ptr for cross-referencing composite SRV inputs later.
                                            _rtvToTexture[rtvPtr] = texPtr;
                                        }
                                    }
                                    finally { cv.Dispose(); }
                                }
                                catch (Exception ex) { Plugin.Log.Warning($"[FFXIV-TV] check bb-rtv failed: {ex.Message}"); }
                            }

                            // CF inject learning: runs AFTER STEP B so BB is classified before we check it.
                            // Guard: _knownBackbufferTexturePtrs.Count > 0 prevents learning on early frames
                            // before BB is known — those frames would capture the BB ptr as "valid LDR"
                            // (BB is BGRA8 full-res, passes IsLdrFullRes), corrupting _targetInjectRtvPtr.
                            bool inBbSet   = IsBackbuffer(rtvPtr);
                            bool bbKnown   = _cachedLiveRtv != 0 || _knownBackbufferTexturePtrs.Count > 0;
                            if (!inBbSet && bbKnown)
                            {
                                if (!_postBloomRtvCache.TryGetValue(rtvPtr, out bool lrnValid))
                                {
                                    lrnValid = IsLdrFullRes(rtvPtr);
                                    _postBloomRtvCache[rtvPtr] = lrnValid;
                                }
                                if (LdrLog)
                                    Plugin.Log.Info($"[FFXIV-TV] LdrLog OMSetRT: ptr=0x{rtvPtr:X} inBb={inBbSet} isLdr={_postBloomRtvCache[rtvPtr]} lastSeen=0x{_lastSeenValidRtvPtr:X}");
                                if (lrnValid) _lastSeenValidRtvPtr = rtvPtr;
                            }
                            else if (LdrLog)
                                Plugin.Log.Info($"[FFXIV-TV] LdrLog OMSetRT SKIP: ptr=0x{rtvPtr:X} inBb={inBbSet} bbKnown={bbKnown}");

                            // BB-bind definitive learn: when FFXIV binds the backbuffer, the surface
                            // that was bound IMMEDIATELY before it is the final LDR composite target.
                            // This is more reliable than the CF-DI-learned pointer on the host, and
                            // fixes clients where a different BGRA8 intermediate (pre-tonemap scene
                            // buffer or shadow/AO surface) appears before the correct LDR in the
                            // DrawIndexed sequence, causing video to render onto characters instead
                            // of the screen rect. Force-override _lastSeenValidRtvPtr here so
                            // PrepareHooks promotes the correct pointer to _targetInjectRtvPtr.
                            if (inBbSet && prevNoDsvPtr != 0 && !IsBackbuffer(prevNoDsvPtr))
                            {
                                if (!_postBloomRtvCache.TryGetValue(prevNoDsvPtr, out bool prevLdr))
                                {
                                    prevLdr = IsLdrFullRes(prevNoDsvPtr);
                                    _postBloomRtvCache[prevNoDsvPtr] = prevLdr;
                                }
                                if (prevLdr)
                                    _lastSeenValidRtvPtr = prevNoDsvPtr;
                            }

                            // OMSetRT-LDR inject: fires on the second LDR bind per frame (re-bind for HUD).
                            // Guard: sceneWasDrawnBeforeThisCall + prevNoDsvPtr==rtvPtr ensures this is the
                            // HUD-pass re-bind (tonemap already filled LDR), not the initial tonemap setup bind.
                            // On NVIDIA/DLSS frames where CF-Draw fires first (compute-tonemap path), this
                            // is blocked by _omSetRtLdrFiredThisFrame and does not fire redundantly.
                            if (OmSetRtLdrEnabled
                                && sceneWasDrawnBeforeThisCall && !inBbSet && !_frameInjectionDone && !_omSetRtLdrFiredThisFrame
                                && prevNoDsvPtr == rtvPtr
                                && rtvPtr == _targetInjectRtvPtr && _targetInjectRtvPtr != 0
                                && _initialized && _storedScreen != null && _psLdr != null
                                && _dsReverseZWrite != null && _dsNoDepth != null && _cbParams != null)
                            {
                                calledOriginal = true;
                                _omSetRTHook?.Original(pCtx, numViews, ppRTVs, pDSV); // bind LDR first
                                _omSetRtLdrFiredThisFrame = true; // prevent re-firing; CF-Draw/CF-DI can still fire
                                _lastSeenValidRtvPtr = rtvPtr;
                                _postTonemapInjectCount++;
                                _omSetRtLdrCount++;
                                _lastInjectPath   = "omsetrt-ldr";
                                _lastInjectRtvPtr = rtvPtr;
                                var (ldrFmt, ldrW, ldrH) = GetRtvInfo(rtvPtr);
                                _lastInjectFmt = ldrFmt; _lastInjectW = ldrW; _lastInjectH = ldrH;
                                if (_postTonemapInjectCount <= 5 || _postTonemapInjectCount % 300 == 0)
                                    Plugin.Log.Info($"[FFXIV-TV] OMSetRT-LDR inject #{_postTonemapInjectCount} rtv=0x{rtvPtr:X}");
                                // When AlphaBlendInject: skip draw here — OMSetRT-LDR fires but its
                                // draws are overwritten by later passes before BB-bind. Drawing here
                                // with any blend mode corrupts LDR alpha, breaking BB-bind's InvDestAlpha.
                                // BB-bind is the sole path whose draws survive to the final image.
                                if (!AlphaBlendInject)
                                {
                                    var ldrRtv2 = new ID3D11RenderTargetView(rtvPtr);
                                    ldrRtv2.AddRef();
                                    try { ExecuteInlineDraw(ldrRtv2, useDepth: false, restoreAfterDraw: true,
                                                           useLdrShader: true); }
                                    catch (Exception ex) { Plugin.Log.Warning($"[FFXIV-TV] OMSetRT-LDR inject failed: {ex.Message}"); }
                                    finally { ldrRtv2.Dispose(); }
                                }
                            }

                            if (inBbSet)
                            {
                                _bbBindCountThisUiPass++;
                                _currentBbRtvPtr = rtvPtr;

                                // v0.5.69 intermediate inject (restored in v0.5.123):
                                // At BB-bind time, the previously-bound RT is the final post-bloom LDR
                                // surface (BGRA8). It does NOT contain HUD — HUD draws on the BB AFTER
                                // FFXIV's composite blit (intermediate → BB).
                                // By injecting into the intermediate BEFORE calling Original():
                                //   1. Our rect is baked into the intermediate.
                                //   2. Original() executes: FFXIV binds BB.
                                //   3. FFXIV DrawIndexed/Draw: composites intermediate (with rect) → BB.
                                //   4. FFXIV HUD draws on BB after → HUD naturally in front of rect. ✓
                                // Fallback: if FFXIV unbound the intermediate (to use as SRV input)
                                // before the BB bind, OMGetRenderTargets returns null. We use
                                // prevNoDsvPtr (the last non-DSV surface before the BB bind) instead.
                                if (!_frameInjectionDone && OmSetRtInjectEnabled
                                    && _initialized && _storedScreen != null
                                    && _psLdr != null && _dsReverseZWrite != null
                                    && _dsNoDepth != null && _cbParams != null && _context != null)
                                {
                                    // Diagnose WHY CF-DI missed — will tell us the root cause of the 30% miss.
                                    if (!_sceneDrawnThisFrame)         _omSetRtMissSceneNotDrawn++;
                                    else if (!_inUiPass)               _omSetRtMissInUiPassFalse++;
                                    else                               _omSetRtMissDrawCall++;
                                    ID3D11RenderTargetView? intermediate = null;
                                    ID3D11DepthStencilView? tmpDsv = null;
                                    try
                                    {
                                        var arr = new ID3D11RenderTargetView[1];
                                        _context.OMGetRenderTargets(1u, arr, out tmpDsv);
                                        intermediate = arr[0]; // non-null = OMGetRenderTargets AddRef'd it
                                    }
                                    catch { }
                                    finally { tmpDsv?.Dispose(); }

                                    // Capture diagnostic info.
                                    _lastIntermediateGot = intermediate?.NativePointer ?? 0;
                                    _lastPrevNoDsvPtr    = prevNoDsvPtr;

                                    nint injectPtr = intermediate?.NativePointer ?? 0;
                                    // Fallback: FFXIV sometimes unbinds intermediate before BB bind.
                                    bool usedFallback = false;
                                    if (injectPtr == 0 && prevNoDsvPtr != 0)
                                    {
                                        injectPtr   = prevNoDsvPtr;
                                        usedFallback = true;
                                    }

                                    if (injectPtr != 0)
                                    {
                                        bool injectPtrIsBb = IsBackbuffer(injectPtr);
                                        _lastInjectWasBackbuffer = injectPtrIsBb;
                                        if (injectPtrIsBb)
                                        {
                                            // Intermediate IS the backbuffer: NVIDIA sometimes has BB already
                                            // bound before FFXIV's explicit BB-bind call. Drawing here would
                                            // be overwritten by FFXIV's composite DrawIndexed (LDR→BB).
                                            // Skip injection — let DrawIndexedDetour's BB-inject path fire.
                                            _omSetRtSkippedBbCount++;
                                            if (_omSetRtSkippedBbCount <= 3 || _omSetRtSkippedBbCount % 3000 == 0)
                                                Plugin.Log.Info($"[FFXIV-TV] OMSetRT inject: intermediate=BB (skip #{_omSetRtSkippedBbCount}), deferring to BB-inject");
                                            intermediate?.Dispose();
                                        }
                                        else
                                        {
                                        // Learn this LDR ptr — it may be the alternate swapchain buffer
                                        // that CF-DI misses due to ptr rotation. Adding it here lets
                                        // CF-DI fire on it directly in future frames, eliminating fallback.
                                        _knownLdrRtvPtrs.Add(injectPtr);
                                        _frameInjectionDone = true;
                                        _postTonemapInjectCount++;
                                        _omSetRtCount++;
                                        _lastInjectPath    = usedFallback ? "omsetrt-fallback" : "omsetrt";
                                        _lastInjectRtvPtr  = injectPtr;
                                        _lastFallbackUsed  = usedFallback;
                                        var (fmt, w, h)    = GetRtvInfo(injectPtr);
                                        _lastInjectFmt     = fmt;
                                        _lastInjectW       = w;
                                        _lastInjectH       = h;
                                        if (_postTonemapInjectCount <= 5 || _postTonemapInjectCount % 300 == 0)
                                            Plugin.Log.Info($"[FFXIV-TV] OMSetRT inject #{_postTonemapInjectCount} path={_lastInjectPath} rtv=0x{injectPtr:X} fmt={fmt} {w}x{h}");

                                        // Sample LDR alpha once to determine if HUD=alpha1/scene=alpha0 (guides AlphaBlendInject).
                                        if (!_alphaReadbackDone)
                                            ReadLdrAlphaSamples(injectPtr);

                                        // Use COM object from OMGetRenderTargets if available; otherwise wrap ptr.
                                        var injRtv = intermediate ?? new ID3D11RenderTargetView(injectPtr);
                                        if (intermediate == null) injRtv.AddRef();
                                        var blendOverride = AlphaBlendInject ? _blendStateInvDestAlpha : null;
                                        bool useDepthNow = true;
                                        var depthOverride = _dsReverseZ;
                                        try
                                        {
                                            ExecuteInlineDraw(injRtv, useDepth: useDepthNow, restoreAfterDraw: true,
                                                             overrideDepthState: depthOverride, useLdrShader: true,
                                                             overrideBlendState: blendOverride);
                                        }
                                        catch (Exception ex) { Plugin.Log.Warning($"[FFXIV-TV] OMSetRT inject failed: {ex.Message}"); }
                                        finally { injRtv.Dispose(); }
                                        } // end else (intermediate != BB)
                                    }
                                    else
                                    {
                                        // Nothing to inject into — BB inject fallback will fire in DrawDetour.
                                        _lastIntermediateGot = 0;
                                        intermediate?.Dispose();
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[FFXIV-TV] OMSetRenderTargetsDetour exception: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _inHookDetour = false;
            if (!calledOriginal)
                try { _omSetRTHook?.Original(pCtx, numViews, ppRTVs, pDSV); } catch { }
        }
    }

    private void ClearRenderTargetViewDetour(nint pCtx, nint pRTV, float* pColorRGBA)
    {
        if (_inHookDetour) { try { _clearRtvHook?.Original(pCtx, pRTV, pColorRGBA); } catch { } return; }
        _inHookDetour = true;
        bool calledOriginal = false;
        try
        {
            if (TraceFramesRemaining > 0 && pCtx == _contextPtr)
            {
                float tr = pColorRGBA != null ? pColorRGBA[0] : 0; float tg = pColorRGBA != null ? pColorRGBA[1] : 0;
                float tb = pColorRGBA != null ? pColorRGBA[2] : 0; float ta = pColorRGBA != null ? pColorRGBA[3] : 0;
                TraceLog($"ClearRTV rtv=0x{pRTV:X} color=({tr:F2},{tg:F2},{tb:F2},{ta:F2}) isBB={IsBackbuffer(pRTV)}");
            }
            if (pCtx == _contextPtr && !IsBackbuffer(pRTV))
            {
                _clearRtvCallCount++;
                if (_sceneDrawnThisFrame) _clearRtvSceneDrawnCount++;
                if (_inUiPass) _lastClearedUiPassRtvPtr = pRTV;
                if (_cbkFrameCount < 3)
                    Plugin.Log.Info($"[FFXIV-TV] ClearRTV seq: ptr=0x{pRTV:X} inUiPass={_inUiPass} sceneDrawn={_sceneDrawnThisFrame} frame={_cbkFrameCount}");

                // ClearRTV-inject: fires after post-processing is complete and LDR is cleared
                // for the HUD pass. We draw the rect onto the freshly-cleared LDR surface BEFORE
                // any HUD elements draw. FFXIV HUD then draws on LDR on top of us → HUD in front.
                // The composite blit (LDR→BB) carries both scene + rect + HUD to BB. ✓
                // Guard: _sceneDrawnThisFrame (not _inUiPass) — ClearRTV may fire before the
                // DSV→no-DSV transition is detected by OMSetRenderTargets.
                if (_sceneDrawnThisFrame && ClearRtvInjectEnabled && !_frameInjectionDone
                    && _initialized && _storedScreen != null && _psLdr != null
                    && _dsReverseZ != null && _dsNoDepth != null && _cbParams != null)
                {
                    if (!_postBloomRtvCache.TryGetValue(pRTV, out bool isLdr))
                    {
                        isLdr = IsLdrFullRes(pRTV);
                        _postBloomRtvCache[pRTV] = isLdr;
                    }
                    if (isLdr) _clearRtvLdrCount++;
                    if (isLdr)
                    {
                        calledOriginal = true;
                        _clearRtvHook?.Original(pCtx, pRTV, pColorRGBA); // clear LDR first
                        _frameInjectionDone = true;
                        _postTonemapInjectCount++;
                        _clearRtvInjectCount++;
                        _lastInjectPath   = "clearrtv";
                        _lastInjectRtvPtr = pRTV;
                        var (fmt, w, h) = GetRtvInfo(pRTV);
                        _lastInjectFmt = fmt; _lastInjectW = w; _lastInjectH = h;
                        if (_clearRtvInjectCount <= 5 || _clearRtvInjectCount % 300 == 0)
                            Plugin.Log.Info($"[FFXIV-TV] ClearRTV inject #{_clearRtvInjectCount} rtv=0x{pRTV:X} {w}x{h}");
                        var ldrRtv = new ID3D11RenderTargetView(pRTV);
                        ldrRtv.AddRef();
                        try
                        {
                            ExecuteInlineDraw(ldrRtv, useDepth: true, restoreAfterDraw: true,
                                             overrideDepthState: _dsReverseZ, useLdrShader: true);
                        }
                        catch (Exception ex) { Plugin.Log.Warning($"[FFXIV-TV] ClearRTV inject failed: {ex.Message}"); }
                        finally { ldrRtv.Dispose(); }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[FFXIV-TV] ClearRenderTargetViewDetour exception: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _inHookDetour = false;
            if (!calledOriginal)
                try { _clearRtvHook?.Original(pCtx, pRTV, pColorRGBA); } catch { }
        }
    }

    // Returns the DXGI format name and pixel dimensions of an RTV's backing texture.
    // Used for inject diagnostics — never throws.
    private (string fmt, int w, int h) GetRtvInfo(nint rtvPtr)
    {
        if (rtvPtr == 0) return ("null", 0, 0);
        try
        {
            var rv = new ID3D11RenderTargetView(rtvPtr);
            rv.AddRef();
            try
            {
                using var res = rv.Resource;
                var tex = new ID3D11Texture2D(res.NativePointer);
                tex.AddRef();
                try { var d = tex.Description; return (d.Format.ToString(), (int)d.Width, (int)d.Height); }
                finally { tex.Dispose(); }
            }
            finally { rv.Dispose(); }
        }
        catch { return ("error", 0, 0); }
    }

    // Returns true if rtvPtr is a full-resolution LDR surface (not R16/R32/R11 HDR).
    // Called at most once per unique rtvPtr per frame (result cached in _postBloomRtvCache).
    private bool IsLdrFullRes(nint rtvPtr)
    {
        var kdev = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance();
        if (kdev == null) return false;
        try
        {
            var rv = new ID3D11RenderTargetView(rtvPtr);
            rv.AddRef();
            try
            {
                using var res = rv.Resource;
                var tex = new ID3D11Texture2D(res.NativePointer);
                tex.AddRef();
                try
                {
                    var d = tex.Description;
                    bool fullRes = d.Width == kdev->Width && d.Height == kdev->Height;
                    // Only BGRA8 formats are valid LDR composite targets.
                    // Negative exclusion was too broad (passed R8_UNorm, R16_UNorm, etc.).
                    bool isLdr   = d.Format == Format.B8G8R8A8_UNorm
                                || d.Format == Format.B8G8R8A8_UNorm_SRgb
                                || d.Format == Format.R8G8B8A8_UNorm
                                || d.Format == Format.R8G8B8A8_UNorm_SRgb
                                // NVIDIA: HDR/RTX HDR/Smooth Motion may upgrade the swapchain
                                // to 10-bit. The internal LDR composite surface follows suit.
                                || d.Format == Format.R10G10B10A2_UNorm;
                    return fullRes && isLdr;
                }
                finally { tex.Dispose(); }
            }
            finally { rv.Dispose(); }
        }
        catch { return false; }
    }

    // Cross-frame inject: called after every Draw/DrawIndexed on our context.
    // Checks _currentNoDsvRtvPtr (live-tracked from OMSetRenderTargets — no COM call needed).
    // Tracks _lastSeenValidRtvPtr for next-frame target learning.
    // Injects when the current RTV matches _targetInjectRtvPtr (learned from last frame).
    private void TryCrossFrameInject(nint pCtx)
    {
        if (pCtx != _contextPtr || _frameInjectionDone) return;
        if (!_initialized || _storedScreen == null || _dsNoDepth == null || _cbParams == null) return;

        nint rtvPtr = _currentNoDsvRtvPtr;
        if (rtvPtr == 0) return;

        // Cache the LDR full-res check per unique RTV ptr (cleared each frame by PrepareHooks).
        if (!_postBloomRtvCache.TryGetValue(rtvPtr, out bool isValid))
        {
            isValid = IsLdrFullRes(rtvPtr);
            _postBloomRtvCache[rtvPtr] = isValid;
        }
        if (!isValid) return;

        // Always update the learning field — captures the LAST valid LDR surface this frame.
        _lastSeenValidRtvPtr = rtvPtr;

        // Only inject when this frame's learned target is matched.
        // On frame 1 _targetInjectRtvPtr == 0 → no inject; frame 2+ → targets previous frame's last LDR RTV.
        if (rtvPtr != _targetInjectRtvPtr) return;

        _frameInjectionDone = true;
        _postTonemapInjectCount++;
        if (_postTonemapInjectCount <= 5 || _postTonemapInjectCount % 300 == 0)
            Plugin.Log.Info($"[FFXIV-TV] CF inject #{_postTonemapInjectCount} rtv=0x{rtvPtr:X}");

        var rtv = new ID3D11RenderTargetView(rtvPtr);
        rtv.AddRef();
        try { ExecuteInlineDraw(rtv, useDepth: false); }
        catch (Exception ex) { Plugin.Log.Warning($"[FFXIV-TV] CF inject failed: {ex.Message}"); }
        finally { rtv.Dispose(); }
    }

    // DrawIndexedDetour: fallback injection point (v0.5.69 pattern).
    // Priority: OMSetRT inject fires first (pre-BB-bind, into intermediate). If that
    // fires, _frameInjectionDone=true blocks this detour entirely.
    // If OMSetRT got a null intermediate (nothing was bound before BB), THIS fires instead:
    //   - First DrawIndexed after BB bind = the composite blit (3D scene → BB).
    //   - Original-first: composite blit runs, then inject rect into BB.
    //   - All subsequent DrawIndexed (HUD) call Original → HUD draws on top of rect → HUD in front. ✓
    private void DrawIndexedDetour(nint pCtx, uint indexCount, uint startIndex, int baseVertex)
    {
        if (_inHookDetour) { try { _drawIndexedHook?.Original(pCtx, indexCount, startIndex, baseVertex); } catch { } return; }
        _inHookDetour = true;
        bool calledOriginal = false;
        try
        {
            if (TraceFramesRemaining > 0 && pCtx == _contextPtr)
                TraceLog($"DrawIndexed idx={indexCount} start={startIndex} base={baseVertex} bbRtv=0x{_currentBbRtvPtr:X} noDsvRtv=0x{_currentNoDsvRtvPtr:X} frameInj={_frameInjectionDone} bbDrawCount={_bbDrawCount}");
            // Condition dump for first 5 frames × first 10 DrawIndexed calls per frame.
            if (pCtx == _contextPtr && _diagFrameCount <= 5 && _diagDiCount < 10)
            {
                _diagDiCount++;
                Plugin.Log.Info($"[FFXIV-TV] DI#{_diagDiCount}@f{_diagFrameCount}: " +
                    $"frameInj={_frameInjectionDone} bbRtv=0x{_currentBbRtvPtr:X} " +
                    $"sceneDrawn={_sceneDrawnThisFrame} inUiPass={_inUiPass} " +
                    $"noDsvRtv=0x{_currentNoDsvRtvPtr:X} targetRtv=0x{_targetInjectRtvPtr:X} " +
                    $"psLdr={_psLdr != null}");
            }

            // CF-DrawIndexed inject: fires on the first draw to the known LDR full-res surface
            // (BGRA8 tonemap blit) in the UI pass, before BB is bound.
            // Original runs first (tonemap blit executes), then we draw the rect on top.
            // At this point we are POST-BLOOM → no FFXIV bloom amplification of our content. ✓
            // HUD draws come after → HUD is on top. ✓  Depth from frozen _trackedDsv → correct occlusion. ✓
            // CF-DrawIndexed inject: fires on the first DrawIndexed to the known LDR surface,
            // which is the tonemap blit (post-bloom, pre-HUD). Original runs first (tonemap completes),
            // then we draw rect onto the LDR surface. HUD draws into LDR after → HUD in front. ✓
            // _targetInjectRtvPtr is set in PrepareHooks from last frame's _lastSeenValidRtvPtr.
            // Learning (OMSetRenderTargets, post-STEP-B) guarantees it's the LDR surface, not the BB.
            // CF-DrawIndexed: post-scene, pre-BB-bind, first qualifying BGRA8 DrawIndexed.
            // Requires _inUiPass — without it, pre-bloom BGRA8 surfaces (SMAA/TAA intermediates
            // etc.) are misidentified as LDR and cause white flash. _inUiPass is now set explicitly
            // in OMSetRT when the LDR surface is first detected (fixing the old 35% miss rate).
            if (CfDiEnabled
                && pCtx == _contextPtr && _sceneDrawnThisFrame && !_frameInjectionDone
                && _inUiPass && _currentBbRtvPtr == 0
                && _initialized && _storedScreen != null && _psLdr != null
                && _dsReverseZWrite != null && _dsNoDepth != null && _cbParams != null)
            {
                // Fast path: tracked pointer. If 0 (FFXIV bound BGRA8+DSV → hasDsv reset it),
                // fall back to OMGetRenderTargets. Safe here because _inUiPass guarantees post-scene.
                nint rtvPtr = _currentNoDsvRtvPtr;
                if (rtvPtr == 0 && _context != null)
                {
                    var arr2 = new ID3D11RenderTargetView[1];
                    ID3D11DepthStencilView? tmpDsv2 = null;
                    try
                    {
                        _context.OMGetRenderTargets(1u, arr2, out tmpDsv2);
                        rtvPtr = arr2[0]?.NativePointer ?? 0;
                    }
                    catch { }
                    finally { arr2[0]?.Dispose(); tmpDsv2?.Dispose(); }
                }
                if (!_postBloomRtvCache.TryGetValue(rtvPtr, out bool cfValid))
                {
                    cfValid = rtvPtr != 0 && IsLdrFullRes(rtvPtr);
                    if (rtvPtr != 0) _postBloomRtvCache[rtvPtr] = cfValid;
                }
                // Require rtvPtr matches _targetInjectRtvPtr (the stable LDR surface learned by OMSetRT).
                // This prevents CF-DI from firing on earlier BGRA8 intermediates (pre-tonemap,
                // shadow/SMAA surfaces, etc.) that pass IsLdrFullRes but don't reach the screen.
                // Fall back to any valid LDR on the first few frames before _targetInjectRtvPtr is learned.
                bool targetMatch = _targetInjectRtvPtr == 0 || rtvPtr == _targetInjectRtvPtr;
                if (rtvPtr != 0 && cfValid && !IsBackbuffer(rtvPtr) && targetMatch)
                {
                    _lastSeenValidRtvPtr = rtvPtr;
                    _knownLdrRtvPtrs.Add(rtvPtr);   // accumulate LDR ptr for multi-buffer rotation
                    // Learn the underlying LDR texture ptr for CopyResourceDetour (first time only).
                    if (_ldrTexPtr == 0)
                    {
                        try
                        {
                            var rv2 = new ID3D11RenderTargetView(rtvPtr);
                            rv2.AddRef();
                            try { _ldrTexPtr = rv2.Resource.NativePointer; }
                            finally { rv2.Dispose(); }
                            Plugin.Log.Info($"[FFXIV-TV] CF-DI: learned _ldrTexPtr=0x{_ldrTexPtr:X} from rtv=0x{rtvPtr:X}");
                        }
                        catch { }
                    }
                    // AlphaBlendInject: BB-bind handles the inject with InvDestAlpha blend at the
                    // point where LDR alpha is correct (scene=0, HUD=153). Skip draw here to avoid
                    // NonPremultiplied contamination and to allow BB-bind to fire this frame.
                    // Original still runs via the finally block (calledOriginal stays false).
                    if (!AlphaBlendInject)
                    {
                        _frameInjectionDone = true;
                        _postTonemapInjectCount++;
                        _cfDiCount++;
                        _lastInjectPath   = "cf-di";
                        _lastInjectRtvPtr = rtvPtr;
                        var (cfmt, cw, ch) = GetRtvInfo(rtvPtr);
                        _lastInjectFmt = cfmt; _lastInjectW = cw; _lastInjectH = ch;
                        if (_postTonemapInjectCount <= 5 || _postTonemapInjectCount % 300 == 0)
                            Plugin.Log.Info($"[FFXIV-TV] CF-DI inject #{_postTonemapInjectCount} rtv=0x{rtvPtr:X}");
                        calledOriginal = true;
                        _drawIndexedHook?.Original(pCtx, indexCount, startIndex, baseVertex);
                        var ldrRtv = new ID3D11RenderTargetView(rtvPtr);
                        ldrRtv.AddRef();
                        try
                        {
                            ExecuteInlineDraw(ldrRtv, useDepth: true, restoreAfterDraw: true,
                                             overrideDepthState: _dsReverseZ, useLdrShader: true);
                        }
                        catch (Exception ex) { Plugin.Log.Warning($"[FFXIV-TV] CF-DI inject failed: {ex.Message}"); }
                        finally { ldrRtv.Dispose(); }
                    }
                }
                else
                {
                    // Inner-check fail diagnosis (tells us WHY CF-DI didn't fire on this draw).
                    if      (rtvPtr == 0)   _cfDiMissNullPtr++;
                    else if (!cfValid)      _cfDiMissNotLdr++;
                    // else: it's in the BB set (rare, not tracked separately)
                }
            }
            // BB inject: fires on the Nth DrawIndexed after FFXIV binds the backbuffer (N = BbDrawSkip+1).
            // Counting skipped draws lets us sweep through to find exactly which draw is the
            // scene composite vs HUD draws — useful for diagnosing HUD ordering.
            else if (pCtx == _contextPtr && _currentBbRtvPtr != 0
                && !_frameInjectionDone
                && _initialized && _storedScreen != null && _psLdr != null
                && _dsReverseZ != null && _dsNoDepth != null && _cbParams != null)
            {
                _bbDrawCount++;
                if (_bbDrawCount > BbDrawSkip)
                {
                    _frameInjectionDone = true;
                    calledOriginal = true;
                    _drawIndexedHook?.Original(pCtx, indexCount, startIndex, baseVertex);

                    _postTonemapInjectCount++;
                    _diBbCount++;
                    _lastInjectPath   = "di-bb";
                    _lastInjectRtvPtr = _currentBbRtvPtr;
                    var (bfmt, bw, bh) = GetRtvInfo(_currentBbRtvPtr);
                    _lastInjectFmt = bfmt; _lastInjectW = bw; _lastInjectH = bh;
                    if (_postTonemapInjectCount <= 5 || _postTonemapInjectCount % 300 == 0)
                        Plugin.Log.Info($"[FFXIV-TV] DI-BB inject #{_postTonemapInjectCount} bb=0x{_currentBbRtvPtr:X} drawN={_bbDrawCount} skip={BbDrawSkip}");

                    var bbRtv = new ID3D11RenderTargetView(_currentBbRtvPtr);
                    bbRtv.AddRef();
                    try
                    {
                        bool useDepth = CheckDepthCompatibility(bbRtv);
                        var depthOverride = (_sceneDrawnThisFrame && useDepth) ? _dsReverseZWrite : null;
                        ExecuteInlineDraw(bbRtv, useDepth, overrideDepthState: depthOverride, useLdrShader: true);
                    }
                    catch (Exception ex) { Plugin.Log.Warning($"[FFXIV-TV] DI-BB inject failed: {ex.Message}"); }
                    finally { bbRtv.Dispose(); }
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[FFXIV-TV] DrawIndexedDetour exception: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _inHookDetour = false;
            if (!calledOriginal)
                try { _drawIndexedHook?.Original(pCtx, indexCount, startIndex, baseVertex); } catch { }
        }
    }

    // DrawDetour: v0.5.69 BB inject (fallback / depth-correct path).
    // Fires after FFXIV binds the backbuffer (DrawDetour sees _currentBbRtvPtr != 0).
    // Calls Original first (FFXIV's draw runs), then injects with reversed-Z depth testing
    // so 3D geometry/characters correctly occlude the rect.
    private void DrawDetour(nint pCtx, uint vertexCount, uint startVertex)
    {
        if (_inHookDetour) { try { _drawHook?.Original(pCtx, vertexCount, startVertex); } catch { } return; }
        _inHookDetour = true;
        bool calledOriginal = false;
        try
        {
            if (TraceFramesRemaining > 0 && pCtx == _contextPtr)
                TraceLog($"Draw verts={vertexCount} start={startVertex} bbRtv=0x{_currentBbRtvPtr:X} noDsvRtv=0x{_currentNoDsvRtvPtr:X} frameInj={_frameInjectionDone}");
            // Condition dump for first 5 frames × first 10 Draw calls per frame.
            if (pCtx == _contextPtr && _diagFrameCount <= 5 && _diagDrawCount < 10)
            {
                _diagDrawCount++;
                Plugin.Log.Info($"[FFXIV-TV] Draw#{_diagDrawCount}@f{_diagFrameCount}: " +
                    $"frameInj={_frameInjectionDone} bbRtv=0x{_currentBbRtvPtr:X} " +
                    $"sceneDrawn={_sceneDrawnThisFrame} inUiPass={_inUiPass} " +
                    $"noDsvRtv=0x{_currentNoDsvRtvPtr:X} targetRtv=0x{_targetInjectRtvPtr:X}");
            }

            // CF-Draw inject: same logic as CF-DrawIndexed (see DrawIndexedDetour).
            if (CfDrawEnabled
                && pCtx == _contextPtr && _sceneDrawnThisFrame && !_frameInjectionDone
                && _inUiPass && _currentBbRtvPtr == 0
                && _initialized && _storedScreen != null && _psLdr != null
                && _dsReverseZWrite != null && _dsNoDepth != null && _cbParams != null)
            {
                nint rtvPtr = _currentNoDsvRtvPtr;
                if (rtvPtr == 0 && _context != null)
                {
                    var arr = new ID3D11RenderTargetView[1];
                    ID3D11DepthStencilView? tmpDsv = null;
                    try
                    {
                        _context.OMGetRenderTargets(1u, arr, out tmpDsv);
                        rtvPtr = arr[0]?.NativePointer ?? 0;
                    }
                    catch { }
                    finally { arr[0]?.Dispose(); tmpDsv?.Dispose(); }
                }
                if (!_postBloomRtvCache.TryGetValue(rtvPtr, out bool cfValid))
                {
                    cfValid = IsLdrFullRes(rtvPtr);
                    _postBloomRtvCache[rtvPtr] = cfValid;
                }
                // Same target matching as CF-DI: prevent firing on SMAA/TAA intermediates.
                bool targetMatch = _targetInjectRtvPtr == 0 || rtvPtr == _targetInjectRtvPtr;
                if (rtvPtr != 0 && cfValid && targetMatch && !IsBackbuffer(rtvPtr))
                {
                    // CfDrawHudSkip: skip the first N Draw calls to LDR (probe HUD draw ordering).
                    if (_cfDrawHudSkipCount < CfDrawHudSkip)
                    {
                        _cfDrawHudSkipCount++;
                        // don't inject — let this draw call run as-is
                    }
                    else
                    {
                    _lastSeenValidRtvPtr = rtvPtr;
                    // AlphaBlendInject: BB-bind handles inject. Skip draw here (Original via finally).
                    if (!AlphaBlendInject)
                    {
                        bool preInject = CfDrawPreInject && _ldrFilledByNonDraw;
                        _frameInjectionDone = true;
                        _postTonemapInjectCount++;
                        _cfDrawCount++;
                        _lastInjectPath   = "cf-draw";
                        _lastInjectRtvPtr = rtvPtr;
                        var (cfmt, cw, ch) = GetRtvInfo(rtvPtr);
                        _lastInjectFmt = cfmt; _lastInjectW = cw; _lastInjectH = ch;
                        if (_postTonemapInjectCount <= 5 || _postTonemapInjectCount % 300 == 0)
                            Plugin.Log.Info($"[FFXIV-TV] CF-Draw inject #{_postTonemapInjectCount} rtv=0x{rtvPtr:X} preInject={preInject} ldrFilledByNonDraw={_ldrFilledByNonDraw}");
                        if (!preInject)
                        {
                            calledOriginal = true;
                            _drawHook?.Original(pCtx, vertexCount, startVertex);
                        }
                        var ldrRtv = new ID3D11RenderTargetView(rtvPtr);
                        ldrRtv.AddRef();
                        try
                        {
                            ExecuteInlineDraw(ldrRtv, useDepth: false, restoreAfterDraw: true,
                                             overrideDepthState: null, useLdrShader: true);
                        }
                        catch (Exception ex) { Plugin.Log.Warning($"[FFXIV-TV] CF-Draw inject failed: {ex.Message}"); }
                        finally { ldrRtv.Dispose(); }
                        if (preInject)
                        {
                            calledOriginal = true;
                            _drawHook?.Original(pCtx, vertexCount, startVertex);
                        }
                    }
                    } // end CfDrawHudSkip else
                }
            }
            // Draw BB inject: fires on the Nth Draw after BB bind (N = BbDrawSkip+1).
            else if (pCtx == _contextPtr && _currentBbRtvPtr != 0
                && !_frameInjectionDone
                && _initialized && _storedScreen != null && _psLdr != null
                && _dsReverseZ != null && _dsNoDepth != null && _cbParams != null)
            {
                _bbDrawCount++;
                if (_bbDrawCount > BbDrawSkip)
                {
                    _frameInjectionDone = true;
                    calledOriginal = true;
                    _drawHook?.Original(pCtx, vertexCount, startVertex);

                    _postTonemapInjectCount++;
                    _lastInjectPath   = "draw-bb";
                    _lastInjectRtvPtr = _currentBbRtvPtr;
                    var (bfmt, bw, bh) = GetRtvInfo(_currentBbRtvPtr);
                    _lastInjectFmt = bfmt; _lastInjectW = bw; _lastInjectH = bh;
                    if (_postTonemapInjectCount <= 5 || _postTonemapInjectCount % 300 == 0)
                        Plugin.Log.Info($"[FFXIV-TV] Draw-BB inject #{_postTonemapInjectCount} bb=0x{_currentBbRtvPtr:X} drawN={_bbDrawCount} skip={BbDrawSkip}");

                    var bbRtv = new ID3D11RenderTargetView(_currentBbRtvPtr);
                    bbRtv.AddRef();
                    try
                    {
                        bool useDepth = CheckDepthCompatibility(bbRtv);
                        var depthOverride = (_sceneDrawnThisFrame && useDepth) ? _dsReverseZWrite : null;
                        ExecuteInlineDraw(bbRtv, useDepth, overrideDepthState: depthOverride, useLdrShader: true);
                    }
                    catch (Exception ex) { Plugin.Log.Warning($"[FFXIV-TV] Draw-BB inject failed: {ex.Message}"); }
                    finally { bbRtv.Dispose(); }
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[FFXIV-TV] DrawDetour exception: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _inHookDetour = false;
            if (!calledOriginal)
                try { _drawHook?.Original(pCtx, vertexCount, startVertex); } catch { }
        }
    }

    // DrawIndexedInstancedDetour / DrawInstancedDetour: same CF logic as CF-DI/CF-Draw.
    // Catches tonemap blits issued via instanced draw calls (~30% of frames on some hardware/states).
    private void DrawIndexedInstancedDetour(nint pCtx, uint indexCountPerInstance, uint instanceCount,
        uint startIndexLocation, int baseVertexLocation, uint startInstanceLocation)
    {
        if (_inHookDetour) { try { _drawIndexedInstancedHook?.Original(pCtx, indexCountPerInstance, instanceCount, startIndexLocation, baseVertexLocation, startInstanceLocation); } catch { } return; }
        _inHookDetour = true;
        bool calledOriginal = false;
        try
        {
            CfInjectShared(pCtx,
                callOriginal: () => { calledOriginal = true; _drawIndexedInstancedHook?.Original(pCtx, indexCountPerInstance, instanceCount, startIndexLocation, baseVertexLocation, startInstanceLocation); },
                pathName: "cf-dii");
        }
        catch (Exception ex) { Plugin.Log.Warning($"[FFXIV-TV] DrawIndexedInstancedDetour exception: {ex.Message}"); }
        finally
        {
            _inHookDetour = false;
            if (!calledOriginal)
                try { _drawIndexedInstancedHook?.Original(pCtx, indexCountPerInstance, instanceCount, startIndexLocation, baseVertexLocation, startInstanceLocation); } catch { }
        }
    }

    private void DrawInstancedDetour(nint pCtx, uint vertexCountPerInstance, uint instanceCount,
        uint startVertexLocation, uint startInstanceLocation)
    {
        if (_inHookDetour) { try { _drawInstancedHook?.Original(pCtx, vertexCountPerInstance, instanceCount, startVertexLocation, startInstanceLocation); } catch { } return; }
        _inHookDetour = true;
        bool calledOriginal = false;
        try
        {
            CfInjectShared(pCtx,
                callOriginal: () => { calledOriginal = true; _drawInstancedHook?.Original(pCtx, vertexCountPerInstance, instanceCount, startVertexLocation, startInstanceLocation); },
                pathName: "cf-di2");
        }
        catch (Exception ex) { Plugin.Log.Warning($"[FFXIV-TV] DrawInstancedDetour exception: {ex.Message}"); }
        finally
        {
            _inHookDetour = false;
            if (!calledOriginal)
                try { _drawInstancedHook?.Original(pCtx, vertexCountPerInstance, instanceCount, startVertexLocation, startInstanceLocation); } catch { }
        }
    }

    // CopyResourceDetour: handles frames where FFXIV fills the LDR intermediate via CopyResource
    // (vtable[47]) instead of a draw call (~37% of frames).  When pDst matches the known LDR
    // texture (_ldrTexPtr), call Original so the copy completes, then inject rect into _targetInjectRtvPtr.
    private void CopyResourceDetour(nint pCtx, nint pDst, nint pSrc)
    {
        if (_inHookDetour) { try { _copyResourceHook?.Original(pCtx, pDst, pSrc); } catch { } return; }
        _inHookDetour = true;
        bool calledOriginal = false;
        try
        {
            if (TraceFramesRemaining > 0 && pCtx == _contextPtr)
                TraceLog($"CopyResource dst=0x{pDst:X} src=0x{pSrc:X} ldrMatch={pDst == _ldrTexPtr}");
            if (pCtx == _contextPtr)
            {
                _copyResourceTotal++;
                if (_ldrTexPtr != 0 && pDst == _ldrTexPtr) _copyResourceLdrMatch++;
            }
            if (pCtx == _contextPtr && !_frameInjectionDone && _inUiPass && _sceneDrawnThisFrame
                && _currentBbRtvPtr == 0 && _ldrTexPtr != 0 && pDst == _ldrTexPtr
                && _targetInjectRtvPtr != 0
                && _initialized && _storedScreen != null && _psLdr != null
                && _dsReverseZWrite != null && _dsNoDepth != null && _cbParams != null)
            {
                calledOriginal = true;
                _copyResourceHook?.Original(pCtx, pDst, pSrc);
                _frameInjectionDone = true;
                _lastSeenValidRtvPtr = _targetInjectRtvPtr;
                _postTonemapInjectCount++;
                _cfDiCount++;
                _cfCopyCount++;
                _lastInjectPath   = "cf-copy";
                _lastInjectRtvPtr = _targetInjectRtvPtr;
                var (cfmt, cw, ch) = GetRtvInfo(_targetInjectRtvPtr);
                _lastInjectFmt = cfmt; _lastInjectW = cw; _lastInjectH = ch;
                if (_postTonemapInjectCount <= 5 || _postTonemapInjectCount % 300 == 0)
                    Plugin.Log.Info($"[FFXIV-TV] CF-Copy inject #{_postTonemapInjectCount} rtv=0x{_targetInjectRtvPtr:X}");
                var ldrRtv = new ID3D11RenderTargetView(_targetInjectRtvPtr);
                ldrRtv.AddRef();
                try
                {
                    ExecuteInlineDraw(ldrRtv, useDepth: false, restoreAfterDraw: true,
                                     overrideDepthState: null, useLdrShader: true);
                }
                catch (Exception ex) { Plugin.Log.Warning($"[FFXIV-TV] CopyResource inject failed: {ex.Message}"); }
                finally { ldrRtv.Dispose(); }
            }
        }
        catch (Exception ex) { Plugin.Log.Warning($"[FFXIV-TV] CopyResourceDetour exception: {ex.Message}"); }
        finally
        {
            _inHookDetour = false;
            if (!calledOriginal)
                try { _copyResourceHook?.Original(pCtx, pDst, pSrc); } catch { }
        }
    }

    // DrawIndexedInstancedIndirect / DrawInstancedIndirect: pass-through to CfInjectShared.
    private void DrawIndexedInstancedIndirectDetour(nint pCtx, nint pBufferForArgs, uint alignedByteOffsetForArgs)
    {
        if (_inHookDetour) { try { _drawIndexedInstancedIndirectHook?.Original(pCtx, pBufferForArgs, alignedByteOffsetForArgs); } catch { } return; }
        _inHookDetour = true;
        bool calledOriginal = false;
        try
        {
            CfInjectShared(pCtx,
                callOriginal: () => { calledOriginal = true; _drawIndexedInstancedIndirectHook?.Original(pCtx, pBufferForArgs, alignedByteOffsetForArgs); },
                pathName: "cf-diii");
        }
        catch (Exception ex) { Plugin.Log.Warning($"[FFXIV-TV] DrawIndexedInstancedIndirectDetour exception: {ex.Message}"); }
        finally
        {
            _inHookDetour = false;
            if (!calledOriginal)
                try { _drawIndexedInstancedIndirectHook?.Original(pCtx, pBufferForArgs, alignedByteOffsetForArgs); } catch { }
        }
    }

    private void DrawInstancedIndirectDetour(nint pCtx, nint pBufferForArgs, uint alignedByteOffsetForArgs)
    {
        if (_inHookDetour) { try { _drawInstancedIndirectHook?.Original(pCtx, pBufferForArgs, alignedByteOffsetForArgs); } catch { } return; }
        _inHookDetour = true;
        bool calledOriginal = false;
        try
        {
            CfInjectShared(pCtx,
                callOriginal: () => { calledOriginal = true; _drawInstancedIndirectHook?.Original(pCtx, pBufferForArgs, alignedByteOffsetForArgs); },
                pathName: "cf-dii");
        }
        catch (Exception ex) { Plugin.Log.Warning($"[FFXIV-TV] DrawInstancedIndirectDetour exception: {ex.Message}"); }
        finally
        {
            _inHookDetour = false;
            if (!calledOriginal)
                try { _drawInstancedIndirectHook?.Original(pCtx, pBufferForArgs, alignedByteOffsetForArgs); } catch { }
        }
    }

    // DispatchDetour: handles compute shader dispatches post-scene. FFXIV uses compute for
    // the tonemap blit on ~37% of frames (filling LDR as a UAV, not an RTV). Since _currentNoDsvRtvPtr
    // is 0 during compute, we inject into _targetInjectRtvPtr directly after the dispatch completes.
    private void DispatchDetour(nint pCtx, uint x, uint y, uint z)
    {
        if (_inHookDetour) { try { _dispatchHook?.Original(pCtx, x, y, z); } catch { } return; }
        _inHookDetour = true;
        bool calledOriginal = false;
        try
        {
            if (TraceFramesRemaining > 0 && pCtx == _contextPtr)
                TraceLog($"Dispatch tg=({x},{y},{z}) bbRtv=0x{_currentBbRtvPtr:X} noDsvRtv=0x{_currentNoDsvRtvPtr:X} inUiPass={_inUiPass} sceneDrawn={_sceneDrawnThisFrame}");
            // On compute-tonemap frames (~37%), the tonemap Dispatch fires directly after Stage 1
            // with NO intermediate no-DSV OMSetRT between them. _sceneDrawnThisFrame is never set
            // before the Dispatch arrives, so it falls outside the normal inject window.
            // Detect this: previous OMSetRT had the main scene DSV → we're coming out of Stage 1.
            if (pCtx == _contextPtr && !_sceneDrawnThisFrame && _prevCallHadDsv
                && _mainSceneDsvPtr != 0 && !_frameInjectionDone && _currentBbRtvPtr == 0)
            {
                _sceneDrawnThisFrame = true;
                _inUiPass = true;
            }

            if (pCtx == _contextPtr && _sceneDrawnThisFrame
                && !_frameInjectionDone && _currentBbRtvPtr == 0)
            {
                if (_inUiPass)
                {
                    _dispatchInWindow++;
                    // Detect compute-tonemap: Dispatch fires while LDR is the current RTV (filling it as UAV).
                    // CF-Draw uses this to decide inject-before vs inject-after Original.
                    if (_targetInjectRtvPtr != 0 && _currentNoDsvRtvPtr == _targetInjectRtvPtr)
                        _ldrFilledByNonDraw = true;
                }
                else
                {
                    // Post-scene Dispatch but _inUiPass not yet set — transition detection may be late.
                    _dispatchNoUiPass++;
                }
            }
            if (CfDispatchEnabled
                && pCtx == _contextPtr && _inUiPass && _sceneDrawnThisFrame
                && !_frameInjectionDone && _currentBbRtvPtr == 0 && _targetInjectRtvPtr != 0
                && _initialized && _storedScreen != null && _psLdr != null
                && _dsReverseZWrite != null && _dsNoDepth != null && _cbParams != null)
            {
                // CfDispatchSkip: skip the first N dispatches, inject on (N+1)th.
                if (_cfDispatchSkipCount < CfDispatchSkip)
                {
                    _cfDispatchSkipCount++;
                    calledOriginal = true;
                    _dispatchHook?.Original(pCtx, x, y, z);
                }
                else
                {
                calledOriginal = true;
                _dispatchHook?.Original(pCtx, x, y, z);
                _frameInjectionDone = true;
                _lastSeenValidRtvPtr = _targetInjectRtvPtr;
                _postTonemapInjectCount++;
                _cfDiCount++;
                _cfDispatchCount++;
                _lastInjectPath   = "cf-dispatch";
                _lastInjectRtvPtr = _targetInjectRtvPtr;
                var (cfmt, cw, ch) = GetRtvInfo(_targetInjectRtvPtr);
                _lastInjectFmt = cfmt; _lastInjectW = cw; _lastInjectH = ch;
                if (_postTonemapInjectCount <= 5 || _postTonemapInjectCount % 300 == 0)
                    Plugin.Log.Info($"[FFXIV-TV] CF-Dispatch inject #{_postTonemapInjectCount} rtv=0x{_targetInjectRtvPtr:X}");
                var ldrRtv = new ID3D11RenderTargetView(_targetInjectRtvPtr);
                ldrRtv.AddRef();
                try
                {
                    ExecuteInlineDraw(ldrRtv, useDepth: false, restoreAfterDraw: true,
                                     overrideDepthState: null, useLdrShader: true);
                }
                catch (Exception ex) { Plugin.Log.Warning($"[FFXIV-TV] Dispatch inject failed: {ex.Message}"); }
                finally { ldrRtv.Dispose(); }
                } // end else (inject path)
            }
        }
        catch (Exception ex) { Plugin.Log.Warning($"[FFXIV-TV] DispatchDetour exception: {ex.Message}"); }
        finally
        {
            _inHookDetour = false;
            if (!calledOriginal)
                try { _dispatchHook?.Original(pCtx, x, y, z); } catch { }
        }
    }

    // DispatchIndirectDetour: mirrors DispatchDetour for GPU-driven indirect compute dispatches.
    // vtable[42]. Suspected cause of the ~77% frames missed by all other CF hooks.
    private void DispatchIndirectDetour(nint pCtx, nint pBufferForArgs, uint alignedByteOffsetForArgs)
    {
        if (_inHookDetour) { try { _dispatchIndirectHook?.Original(pCtx, pBufferForArgs, alignedByteOffsetForArgs); } catch { } return; }
        _inHookDetour = true;
        bool calledOriginal = false;
        try
        {
            // Same early scene-detection as DispatchDetour: on compute-tonemap frames the
            // indirect dispatch may fire before any no-DSV OMSetRT sets _sceneDrawnThisFrame.
            if (pCtx == _contextPtr && !_sceneDrawnThisFrame && _prevCallHadDsv
                && _mainSceneDsvPtr != 0 && !_frameInjectionDone && _currentBbRtvPtr == 0)
            {
                _sceneDrawnThisFrame = true;
                _inUiPass = true;
            }

            if (pCtx == _contextPtr && _sceneDrawnThisFrame
                && !_frameInjectionDone && _currentBbRtvPtr == 0)
            {
                if (_inUiPass)
                {
                    _dispatchIndirectInWindow++;
                    if (_targetInjectRtvPtr != 0 && _currentNoDsvRtvPtr == _targetInjectRtvPtr)
                        _ldrFilledByNonDraw = true;
                }
            }
            if (CfDispatchEnabled
                && pCtx == _contextPtr && _inUiPass && _sceneDrawnThisFrame
                && !_frameInjectionDone && _currentBbRtvPtr == 0 && _targetInjectRtvPtr != 0
                && _initialized && _storedScreen != null && _psLdr != null
                && _dsReverseZWrite != null && _dsNoDepth != null && _cbParams != null)
            {
                calledOriginal = true;
                _dispatchIndirectHook?.Original(pCtx, pBufferForArgs, alignedByteOffsetForArgs);
                _frameInjectionDone = true;
                _lastSeenValidRtvPtr = _targetInjectRtvPtr;
                _postTonemapInjectCount++;
                _cfDiCount++;
                _cfDispatchIndirectCount++;
                _lastInjectPath   = "cf-dispatchindirect";
                _lastInjectRtvPtr = _targetInjectRtvPtr;
                var (cfmt, cw, ch) = GetRtvInfo(_targetInjectRtvPtr);
                _lastInjectFmt = cfmt; _lastInjectW = cw; _lastInjectH = ch;
                if (_postTonemapInjectCount <= 5 || _postTonemapInjectCount % 300 == 0)
                    Plugin.Log.Info($"[FFXIV-TV] CF-DispatchIndirect inject #{_postTonemapInjectCount} rtv=0x{_targetInjectRtvPtr:X}");
                var ldrRtv = new ID3D11RenderTargetView(_targetInjectRtvPtr);
                ldrRtv.AddRef();
                try
                {
                    ExecuteInlineDraw(ldrRtv, useDepth: false, restoreAfterDraw: true,
                                     overrideDepthState: null, useLdrShader: true);
                }
                catch (Exception ex) { Plugin.Log.Warning($"[FFXIV-TV] DispatchIndirect inject failed: {ex.Message}"); }
                finally { ldrRtv.Dispose(); }
            }
        }
        catch (Exception ex) { Plugin.Log.Warning($"[FFXIV-TV] DispatchIndirectDetour exception: {ex.Message}"); }
        finally
        {
            _inHookDetour = false;
            if (!calledOriginal)
                try { _dispatchIndirectHook?.Original(pCtx, pBufferForArgs, alignedByteOffsetForArgs); } catch { }
        }
    }

    // Shared CF inject logic used by DrawIndexed, Draw, DrawIndexedInstanced, DrawInstanced.
    private void CfInjectShared(nint pCtx, Action callOriginal, string pathName)
    {
        if (pCtx != _contextPtr || !_sceneDrawnThisFrame || _frameInjectionDone
            || _currentBbRtvPtr != 0 || !_inUiPass) return;
        if (!_initialized || _storedScreen == null || _psLdr == null
            || _dsReverseZWrite == null || _dsNoDepth == null || _cbParams == null) return;

        nint rtvPtr = _currentNoDsvRtvPtr;
        if (rtvPtr == 0 && _context != null)
        {
            var arr = new ID3D11RenderTargetView[1];
            ID3D11DepthStencilView? tmpDsv = null;
            try { _context.OMGetRenderTargets(1u, arr, out tmpDsv); rtvPtr = arr[0]?.NativePointer ?? 0; }
            catch { }
            finally { arr[0]?.Dispose(); tmpDsv?.Dispose(); }
        }
        if (rtvPtr == 0) return;

        if (!_postBloomRtvCache.TryGetValue(rtvPtr, out bool cfValid))
        {
            cfValid = IsLdrFullRes(rtvPtr);
            _postBloomRtvCache[rtvPtr] = cfValid;
        }
        bool targetMatch = _knownLdrRtvPtrs.Count == 0 || _knownLdrRtvPtrs.Contains(rtvPtr);
        if (!cfValid || !targetMatch || IsBackbuffer(rtvPtr)) return;

        _lastSeenValidRtvPtr = rtvPtr;
        _knownLdrRtvPtrs.Add(rtvPtr);
        callOriginal();
        _frameInjectionDone = true;
        _postTonemapInjectCount++;
        _cfDiCount++;
        _lastInjectPath   = pathName;
        _lastInjectRtvPtr = rtvPtr;
        var (fmt, w, h) = GetRtvInfo(rtvPtr);
        _lastInjectFmt = fmt; _lastInjectW = w; _lastInjectH = h;
        if (_postTonemapInjectCount <= 5 || _postTonemapInjectCount % 300 == 0)
            Plugin.Log.Info($"[FFXIV-TV] {pathName} inject #{_postTonemapInjectCount} rtv=0x{rtvPtr:X}");
        var ldrRtv = new ID3D11RenderTargetView(rtvPtr);
        ldrRtv.AddRef();
        try
        {
            ExecuteInlineDraw(ldrRtv, useDepth: false, restoreAfterDraw: true,
                             overrideDepthState: null, useLdrShader: true);
        }
        catch (Exception ex) { Plugin.Log.Warning($"[FFXIV-TV] {pathName} inject failed: {ex.Message}"); }
        finally { ldrRtv.Dispose(); }
    }

    // Check whether _trackedDsv can be safely paired with the given RTV (same texture dimensions).
    // D3D11 silently renders nothing if DSV and RTV sizes differ — no exception, no visible rect.
    // Cached by (dsvPtr, rtvPtr) pair to avoid per-frame COM queries and log spam.
    // (_depthCompatible alone wasn't enough because _trackedDsv is recreated each frame from
    //  the same pointer, invalidating the cache even when nothing changed.)
    private bool CheckDepthCompatibility(ID3D11RenderTargetView bbRtv)
    {
        if (_trackedDsv == null) return false;
        nint dsvPtr = _trackedDsv.NativePointer;
        nint rtvPtr = bbRtv.NativePointer;
        if (_depthCompatible.HasValue
            && _depthCompatCachedDsvPtr == dsvPtr
            && _depthCompatCachedRtvPtr == rtvPtr)
            return _depthCompatible.Value;

        try
        {
            uint dsvW, dsvH, bbW, bbH;

            using var dsvRes = _trackedDsv.Resource;
            var dsvTex = new ID3D11Texture2D(dsvRes.NativePointer);
            dsvTex.AddRef();
            try { var d = dsvTex.Description; dsvW = d.Width; dsvH = d.Height; }
            finally { dsvTex.Dispose(); }

            using var bbRes = bbRtv.Resource;
            var bbTex = new ID3D11Texture2D(bbRes.NativePointer);
            bbTex.AddRef();
            try { var d = bbTex.Description; bbW = d.Width; bbH = d.Height; }
            finally { bbTex.Dispose(); }

            bool ok = (dsvW == bbW && dsvH == bbH);
            _depthCompatible           = ok;
            _depthCompatCachedDsvPtr   = dsvPtr;
            _depthCompatCachedRtvPtr   = rtvPtr;
            // Debug only — this is called per-inject and logs once per unique (dsv,rtv) pair.
            Plugin.Log.Debug($"[FFXIV-TV] Depth check: dsv={dsvW}x{dsvH} rtv={bbW}x{bbH} compatible={ok}");
            return ok;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[FFXIV-TV] Depth check failed: {ex.Message}");
            _depthCompatible           = false;
            _depthCompatCachedDsvPtr   = dsvPtr;
            _depthCompatCachedRtvPtr   = rtvPtr;
            return false;
        }
    }

    // useDepth=true: bind _trackedDsv (reversed-Z depth testing — correct for intermediate RTs).
    // useDepth=false: no DSV — safe for composite input texture which may differ in size from DSV.
    // restoreAfterDraw=true  (default, BB inject): after draw, unbind the DSV so subsequent
    //   FFXIV 2D UI draw calls aren't affected by the depth stencil we bound.
    // restoreAfterDraw=false: leave bound targets as-is (caller manages state).
    // Samples 3 pixels from the LDR surface and logs their RGBA bytes to diagnose alpha channel content.
    // Called once per plugin session (guarded by _alphaReadbackDone) so the GPU stall is acceptable.
    private void ReadLdrAlphaSamples(nint rtvPtr)
    {
        if (_alphaStagingTex == null || _context == null || rtvPtr == 0) return;
        try
        {
            var rv = new ID3D11RenderTargetView(rtvPtr);
            rv.AddRef();
            ID3D11Texture2D? ldrTex = null;
            int texW = 0, texH = 0;
            try
            {
                using var res = rv.Resource;
                ldrTex = new ID3D11Texture2D(res.NativePointer);
                ldrTex.AddRef();
                var d = ldrTex.Description;
                texW = (int)d.Width; texH = (int)d.Height;
            }
            finally { rv.Dispose(); }

            if (ldrTex == null || texW == 0 || texH == 0) return;
            try
            {
                (int px, int py, string lbl)[] pts =
                {
                    (texW / 2, texH / 2, "ctr"),
                    (100, Math.Max(0, texH - 80), "hbar"),
                    (Math.Max(0, texW - 80), 40, "tr"),
                };
                var sb = new System.Text.StringBuilder("[FFXIV-TV] LdrAlpha samples:");
                foreach (var (px, py, lbl) in pts)
                {
                    if (px >= texW || py >= texH) continue;
                    _context.CopySubresourceRegion(_alphaStagingTex, 0, 0, 0, 0, ldrTex, 0,
                        new Box(px, py, 0, px + 1, py + 1, 1));
                    _context.Map(_alphaStagingTex, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped);
                    byte b, g, r, a;
                    unsafe { byte* p = (byte*)mapped.DataPointer; b = p[0]; g = p[1]; r = p[2]; a = p[3]; }
                    _context.Unmap(_alphaStagingTex, 0);
                    sb.Append($" {lbl}({px},{py})=R{r}G{g}B{b}A{a}");
                }
                Plugin.Log.Info(sb.ToString());
                _alphaReadbackDone = true;
            }
            finally { ldrTex.Dispose(); }
        }
        catch (Exception ex) { Plugin.Log.Warning($"[FFXIV-TV] LdrAlpha readback: {ex.Message}"); }
    }

    // overrideDepthState: when non-null, use this depth state instead of the default selection.
    //   Scene inject passes _dsReverseZWrite (DepthWriteMask.All) so FFXIV geometry depth-tests
    //   against our written depth values. BB inject uses default (_dsReverseZ, WriteZero).
    private void ExecuteInlineDraw(ID3D11RenderTargetView rtv, bool useDepth = true, bool restoreAfterDraw = true, ID3D11DepthStencilState? overrideDepthState = null, ID3D11BlendState? overrideBlendState = null, bool useLdrShader = false)
    {
        if (_context == null || _storedScreen == null) return;
        // Use active SRV when available; fall back to 1x1 black when _activeSrv is null
        // (e.g. video stopped, no image loaded) so the rect shape is still visible.
        var srv = _activeSrv ?? _blackSrv;
        if (srv == null)
        {
            Plugin.Log.Warning("[FFXIV-TV] ExecuteInlineDraw: no SRV (activeSrv=null, blackSrv=null) — draw skipped");
            return;
        }

        // Guard: if _trackedDsv is incompatible with the target RTV (different texture dimensions),
        // D3D11 silently renders nothing. Fall back to no-depth in that case — at least shows the rect.
        ID3D11DepthStencilView? dsv = (useDepth && CheckDepthCompatibility(rtv)) ? _trackedDsv : null;
        var depthState = overrideDepthState ?? (dsv != null ? _dsReverseZ! : _dsNoDepth!);

        UpdateCbParams();

        var saved = SaveState();
        try
        {
            _context.OMSetRenderTargets(new[] { rtv }, dsv);
            // Explicitly set viewport to match the target surface.
            // When firing after a compute dispatch or bloom downsample pass, the rasterizer
            // viewport may be set to a smaller resolution (e.g. 1280x720, 1024x1024) left over
            // from the last rasterization call. Without this, the rect projects to the wrong
            // screen position and clips to the wrong area of the LDR surface.
            var (_, rtvW, rtvH) = GetRtvInfo(rtv.NativePointer);
            if (rtvW > 0 && rtvH > 0)
                _context.RSSetViewports(new[] { new Viewport(0, 0, rtvW, rtvH, 0f, 1f) });
            SetState(srv, depthState);
            if (overrideBlendState != null)
                _context.OMSetBlendState(overrideBlendState);
            if (useLdrShader && _psLdr != null)
                _context.PSSetShader(DebugShaderRed && _psLdrDebugRed != null ? _psLdrDebugRed : _psLdr);
            _context.Draw(6, 0);
        }
        finally
        {
            RestoreState(saved);
            if (restoreAfterDraw)
            {
                // BB inject: unset DSV so FFXIV's subsequent 2D UI draw calls aren't depth-tested.
                _context.OMSetRenderTargets(new[] { rtv }, (ID3D11DepthStencilView?)null);
            }
            // When restoreAfterDraw=false (scene-pass inject), leave (rtv, mainDSV) bound.
            // rtv lifetime is managed by the caller — do NOT Dispose here.
        }

        _cbkFrameCount++;
        if (_cbkFrameCount <= 3 || _cbkFrameCount % 300 == 0)
            Plugin.Log.Info($"[FFXIV-TV] inline draw frame={_cbkFrameCount} dsv={dsv != null} restoreAfter={restoreAfterDraw}");
    }


    private void CreateResources()
    {
        _cbParams = _device!.CreateConstantBuffer<CbParams>();

        // Retrieve blobs from the pre-compiled task (guaranteed complete by TryInitialize check).
        var (vsBlob, psBlob) = _shaderCompileTask!.Result;
        _shaderCompileTask = null; // allow GC of the task
        Plugin.Log.Info($"[FFXIV-TV] Shader blobs: VS={vsBlob.Length}B PS={psBlob.Length}B");

        _vs = _device.CreateVertexShader(vsBlob.Span);
        _ps = _device.CreatePixelShader(psBlob.Span);
        var psLdrBlob = Compiler.Compile(PS_LDR_SRC, "main", "screen_ps_ldr", "ps_5_0");
        Plugin.Log.Info($"[FFXIV-TV] LDR shader blob: {psLdrBlob.Length}B");
        _psLdr = _device.CreatePixelShader(psLdrBlob.Span);
        var psLdrDebugRedBlob = Compiler.Compile(PS_LDR_DEBUG_RED_SRC, "main", "screen_ps_ldr_debug_red", "ps_5_0");
        Plugin.Log.Info($"[FFXIV-TV] LDR debug-red shader blob: {psLdrDebugRedBlob.Length}B");
        _psLdrDebugRed = _device.CreatePixelShader(psLdrDebugRedBlob.Span);

        // No vertex buffer, no input layout — SV_VertexID drives the geometry.

        _blendState = _device.CreateBlendState(BlendDescription.NonPremultiplied);

        // Depth-only blend: disables color writes so scene inject writes depth without
        // contributing luminance to R16 — prevents FFXIV bloom from processing our rect.
        var dob = new BlendDescription();
        dob.RenderTarget[0] = new RenderTargetBlendDescription { RenderTargetWriteMask = ColorWriteEnable.None };
        _depthOnlyBlendState = _device.CreateBlendState(dob);

        // InvDestAlpha blend: out = TV*(1-destA) + dest*destA
        // If HUD pixels have alpha=1 and scene has alpha=0 in LDR, TV fills only scene areas.
        var iaDesc = new BlendDescription();
        iaDesc.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable           = true,
            SourceBlend           = Blend.InverseDestinationAlpha,
            DestinationBlend      = Blend.DestinationAlpha,
            BlendOperation        = BlendOperation.Add,
            SourceBlendAlpha      = Blend.Zero,
            DestinationBlendAlpha = Blend.One,
            BlendOperationAlpha   = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All,
        };
        _blendStateInvDestAlpha = _device.CreateBlendState(iaDesc);

        // 1x1 staging texture for LDR pixel alpha readback diagnostic.
        _alphaStagingTex = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = 1, Height = 1, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
        });

        _rasterizer = _device.CreateRasterizerState(new RasterizerDescription
        {
            CullMode        = CullMode.None,
            FillMode        = FillMode.Solid,
            DepthClipEnable = true,
        });

        _dsNoDepth = _device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable   = false,
            StencilEnable = false,
        });

        _dsReverseZ = _device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable    = true,
            DepthWriteMask = DepthWriteMask.Zero,   // don't write depth — BB inject (nothing renders after)
            DepthFunc      = ComparisonFunction.Greater,
            StencilEnable  = false,
        });

        // Scene inject: write depth so FFXIV geometry depth-tests against our rect.
        // GreaterEqual matches FFXIV's reversed-Z convention (near=1, far=0; "pass if closer").
        _dsReverseZWrite = _device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable    = true,
            DepthWriteMask = DepthWriteMask.All,    // write depth — scene inject (FFXIV geometry renders after)
            DepthFunc      = ComparisonFunction.GreaterEqual,
            StencilEnable  = false,
        });

        var samplerDesc = new SamplerDescription(
            Filter.Anisotropic,
            TextureAddressMode.Clamp,
            TextureAddressMode.Clamp,
            TextureAddressMode.Clamp,
            0f, 16, ComparisonFunction.Never,
            new Color4(0f), 0f, float.MaxValue);
        _sampler = _device.CreateSamplerState(samplerDesc);

        // 1×1 black texture fallback.
        fixed (byte* p = _blackPixelData)
        {
            var blackTexDesc = new Texture2DDescription
            {
                Width             = 1,
                Height            = 1,
                MipLevels         = 1,
                ArraySize         = 1,
                Format            = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage             = ResourceUsage.Immutable,
                BindFlags         = BindFlags.ShaderResource,
            };
            var subData = new SubresourceData { DataPointer = (nint)p, RowPitch = 4 };
            using var blackTex = _device.CreateTexture2D(blackTexDesc, new[] { subData });
            _blackSrv = _device.CreateShaderResourceView(blackTex);
        }

        // 2×2 dynamic gradient texture for idle screensaver.
        var gradDesc = new Texture2DDescription
        {
            Width             = 2,
            Height            = 2,
            MipLevels         = 1,
            ArraySize         = 1,
            Format            = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage             = ResourceUsage.Dynamic,
            BindFlags         = BindFlags.ShaderResource,
            CPUAccessFlags    = CpuAccessFlags.Write,
        };
        _gradientTex = _device.CreateTexture2D(gradDesc);
        _gradientSrv = _device.CreateShaderResourceView(_gradientTex);
    }

    // ── Texture management ────────────────────────────────────────────────────
    public void SetImagePath(string path)
    {
        if (path == _loadedImagePath) return;
        _loadedImagePath = path;
        _imageSrv?.Dispose();
        _imageSrv = null;
        if (!_initialized || string.IsNullOrWhiteSpace(path)) return;
        _imageSrv = LoadTexture(path);
    }

    private ID3D11ShaderResourceView? LoadTexture(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var bmp = new SDDrawing.Bitmap(path);
            using var bmp32 = bmp.Clone(
                new SDDrawing.Rectangle(0, 0, bmp.Width, bmp.Height),
                SDImaging.PixelFormat.Format32bppArgb);

            int w = bmp32.Width, h = bmp32.Height;
            var bd = bmp32.LockBits(
                new SDDrawing.Rectangle(0, 0, w, h),
                SDImaging.ImageLockMode.ReadOnly,
                SDImaging.PixelFormat.Format32bppArgb);
            try
            {
                var desc = new Texture2DDescription
                {
                    Width             = (uint)w,
                    Height            = (uint)h,
                    MipLevels         = 1,
                    ArraySize         = 1,
                    Format            = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage             = ResourceUsage.Immutable,
                    BindFlags         = BindFlags.ShaderResource,
                };
                var subData = new SubresourceData { DataPointer = bd.Scan0, RowPitch = (uint)bd.Stride };
                using var tex = _device!.CreateTexture2D(desc, new[] { subData });
                var srv = _device.CreateShaderResourceView(tex);
                Plugin.Log.Info($"[FFXIV-TV] Loaded texture {w}x{h} from '{path}'");
                return srv;
            }
            finally { bmp32.UnlockBits(bd); }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[FFXIV-TV] D3DRenderer: failed to load '{path}': {ex.Message}");
            return null;
        }
    }

    // ── Per-frame draw ────────────────────────────────────────────────────────

    /// <summary>
    /// Primes hook state for the current frame. MUST be called every frame when D3DRenderer
    /// is available — even when Draw() is not called (e.g. DrawPlaceholder path in Image mode
    /// with no image loaded). Resets per-frame injection flags so DrawIndexedDetour can fire
    /// and advances backbuffer texture learning via _lastNoDsvRtvPtr.
    /// </summary>
    public void PrepareHooks(ScreenDefinition screen)
    {
        if (!_initialized) return;

        // Last-RTV bb identification: FFXIV's BB bind is the LAST no-DSV OMSetRenderTargets
        // before Present. Check it every frame to accumulate ALL swapchain buffer texture ptrs
        // (DXGI rotates between 2-3 buffers each frame; Count==0 guard was too restrictive and
        // prevented learning the second/third buffer, keeping _currentBbRtvPtr stuck at 0).
        // Accumulate BB textures every frame (DXGI rotates between 2-3 swapchain buffers).
        // IMPORTANT: only add TEXTURE here, NOT the RTV ptr.
        // The RTV→"is BB" matching must happen in STEP B (inside _inUiPass) so the
        // OMSetRT inject only fires when FFXIV binds the BB for the composite — BEFORE HUD draws.
        // Adding the RTV ptr directly here would cause inject to fire if that ptr is ever bound
        // after HUD, putting the rect on top of the HUD instead of behind it.
        // When a new texture is learned, clear _checkedRtvPtrs so STEP B re-evaluates RTVs
        // that were previously rejected (they may match the newly-learned swapchain texture).
        if (_lastNoDsvRtvPtr != 0)
        {
            nint rtvPtr = _lastNoDsvRtvPtr;
            try
            {
                var v = new ID3D11View(rtvPtr);
                v.AddRef();
                try
                {
                    using var res = v.Resource;
                    nint texPtr = res.NativePointer;
                    if (_knownBackbufferTexturePtrs.Add(texPtr))
                    {
                        Plugin.Log.Info($"[FFXIV-TV] bb-tex learned (last-RTV): rtv=0x{rtvPtr:X} tex=0x{texPtr:X}");
                        // New BB texture learned — clear checked cache so STEP B re-evaluates
                        // all previously-seen RTVs (some may now match this new texture).
                        _checkedRtvPtrs.Clear();
                    }
                }
                finally { v.Dispose(); }
            }
            catch (Exception ex) { Plugin.Log.Warning($"[FFXIV-TV] last-RTV bb-learn failed: {ex.Message}"); }
        }

        // Capture end-of-frame diagnostic state BEFORE wiping fields.
        _diagPrevBbRtv      = _currentBbRtvPtr;
        _diagPrevFrameInj   = _frameInjectionDone;
        _diagPrevLastSeen   = _lastSeenValidRtvPtr; // this becomes _targetInjectRtvPtr below
        _diagPrevBbDrawCount = _bbDrawCount;
        if (TraceFramesRemaining > 0)
        {
            TraceLog($"===== FRAME BOUNDARY (prev frame had {_bbDrawCount} BB draws) =====");
            TraceFramesRemaining--;
            if (TraceFramesRemaining == 0)
                Plugin.Log.Info("[FFTV-TRACE] Capture complete.");
        }
        _diagDiCount        = 0;
        _diagDrawCount      = 0;
        _bbDrawCount        = 0;
        _diagFrameCount++;

        // Clear per-frame RTV check cache so DXGI swapchain buffer rotation is handled.
        // Each of 2-3 swapchain buffers has a unique RTV ptr. Without this clear, a buffer
        // that was checked before BB was known gets permanently stuck in _checkedRtvPtrs as
        // "not BB", blocking STEP B from ever identifying it. Clearing every frame gives STEP B
        // a fresh chance to evaluate each buffer against the now-known BB texture set.
        // Cost: ~20 GetResource() COM calls per frame (one per unique RTV) — acceptable.
        _checkedRtvPtrs.Clear();

        _lastNoDsvRtvPtr         = 0; // reset — will be updated to the last no-DSV RTV this frame
        // Validate GetLiveBackbufferRtv() against learned BB textures before trusting it.
        // On NVIDIA, the DLSS/HBAO+ renderer init path may produce a wrong non-zero value
        // that corrupts IsBackbuffer() for all RTVs and causes injection into the wrong surface.
        nint rawLiveRtv = GetLiveBackbufferRtv();
        if (_cachedLiveRtvValidated)
        {
            _cachedLiveRtv = rawLiveRtv;
        }
        else if (rawLiveRtv != 0 && _knownBackbufferTexturePtrs.Count > 0)
        {
            // One-time validation: confirm the singleton ptr is backed by a known BB texture.
            try
            {
                var v = new ID3D11View(rawLiveRtv);
                v.AddRef();
                nint texPtr = 0;
                try { using var res = v.Resource; texPtr = res.NativePointer; }
                finally { v.Dispose(); }
                if (_knownBackbufferTexturePtrs.Contains(texPtr))
                {
                    _cachedLiveRtvValidated = true;
                    _cachedLiveRtv = rawLiveRtv;
                    Plugin.Log.Info($"[FFXIV-TV] GetLiveBackbufferRtv validated: ptr=0x{rawLiveRtv:X}");
                }
                else
                {
                    Plugin.Log.Warning($"[FFXIV-TV] GetLiveBackbufferRtv INVALID (NVIDIA-specific garbage?): ptr=0x{rawLiveRtv:X} tex=0x{texPtr:X} not in known BB set — disabling singleton path");
                    _rendererSingletonAddr = 0; // prevent future garbage
                    _cachedLiveRtv = 0;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"[FFXIV-TV] GetLiveBackbufferRtv validation exception: {ex.Message} — disabling singleton path");
                _rendererSingletonAddr = 0;
                _cachedLiveRtv = 0;
            }
        }
        else
        {
            _cachedLiveRtv = 0; // not yet validated — don't trust it yet
        }
        _inUiPass                = false;
        _frameInjectionDone      = false;
        _omSetRtLdrFiredThisFrame = false;
        _bbBindCountThisUiPass   = 0;
        _cfDrawHudSkipCount      = 0;
        _ldrFilledByNonDraw      = false;
        _cfDispatchSkipCount     = 0;
        _dispatchIndirectInWindow = 0;
        _currentBbRtvPtr         = 0;
        _lastClearedUiPassRtvPtr = 0;
        _sceneDrawnThisFrame     = false;
        _prevSceneRendered       = false;
        _currentNoDsvRtvPtr      = 0;
        _postBloomRtvCache.Clear(); // refresh per-frame — handles RTV pointer reuse after territory change
        // Reset MainSceneRTV each frame so it re-converges to the LAST qualifying RTV
        // (AutoSetRTV pattern — ensures we track pipeline changes across frames).
        _mainSceneRtvPtr         = 0;
        // Cross-frame inject: promote last frame's final valid LDR surface as this frame's target.
        _targetInjectRtvPtr  = _lastSeenValidRtvPtr;
        _lastSeenValidRtvPtr = 0;

        var ctrl = Control.Instance();
        if (ctrl == null) return;

        _storedViewProj = ctrl->ViewProjectionMatrix;
        _storedScreen   = screen;

        if (!_prepareHooksLoggedOnce)
        {
            _prepareHooksLoggedOnce = true;
            Plugin.Log.Info($"[FFXIV-TV] PrepareHooks: first call. screen.Visible={screen.Visible} bbTexCount={_knownBackbufferTexturePtrs.Count} bbRtvCount={_knownBackbufferRtvPtrs.Count}");
        }

        _prepareHooksCallCount++;
        // Periodic heartbeat: frame 1, 2, 3, 4, 5, 60, 300, then every 600 (~10s at 60fps).
        // First 5 frames logged individually to capture the inject-condition state at startup.
        // Auto-enable LdrLog for first 5 frames to capture the bind sequence on first run.
        if (_prepareHooksCallCount <= 5) LdrLog = true;
        else if (_prepareHooksCallCount == 6) LdrLog = false;

        if (_prepareHooksCallCount <= 5 || _prepareHooksCallCount == 60 || _prepareHooksCallCount == 300
            || _prepareHooksCallCount % 600 == 0)
        {
            Plugin.Log.Info($"[FFXIV-TV] Heartbeat #{_prepareHooksCallCount}: " +
                $"diInjects={_postTonemapInjectCount} sceneInjects={_sceneInjectCount} " +
                $"cfDi={_cfDiCount} cfDraw={_cfDrawCount} omsetrtLdr={_omSetRtLdrCount} diBb={_diBbCount} omsetrt={_omSetRtCount} " +
                $"lastPath={_lastInjectPath} ldrNonDraw={_ldrFilledByNonDraw} " +
                $"cfDiMiss(null={_cfDiMissNullPtr} notLdr={_cfDiMissNotLdr} tgt={_cfDiMissTargetMismatch}) " +
                $"bbTexCount={_knownBackbufferTexturePtrs.Count} bbRtvCount={_knownBackbufferRtvPtrs.Count} " +
                $"prevBbRtv=0x{_diagPrevBbRtv:X} prevFrameInj={_diagPrevFrameInj} " +
                $"targetRtv=0x{_targetInjectRtvPtr:X} prevLastSeen=0x{_diagPrevLastSeen:X} " +
                $"psLdr={_psLdr != null} storedScreen={_storedScreen != null} init={_initialized}");
        }
    }

    // ── Diagnostic output ────────────────────────────────────────────────────

    /// <summary>
    /// Writes a comprehensive system-info block to dalamud.log.
    /// Safe to call at any time — reads live state only, no mutation.
    /// </summary>
    public void LogSysInfo()
    {
        var log = Plugin.Log;
        var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
        log.Info("[FFTV-SYSINFO] ══════════════════════════════════════");
        log.Info($"[FFTV-SYSINFO] FFXIV-TV v{ver}");

        // OS + runtime
        log.Info($"[FFTV-SYSINFO] OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        log.Info($"[FFTV-SYSINFO] OS version: {Environment.OSVersion}");
        log.Info($"[FFTV-SYSINFO] .NET: {Environment.Version}");
        log.Info($"[FFTV-SYSINFO] 64-bit OS={Environment.Is64BitOperatingSystem} Process={Environment.Is64BitProcess}");
        log.Info($"[FFTV-SYSINFO] Machine: {Environment.MachineName}");
        log.Info($"[FFTV-SYSINFO] ProcessorCount: {Environment.ProcessorCount}");

        // CPU name from registry
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var cpuName = key?.GetValue("ProcessorNameString")?.ToString() ?? "unknown";
            var mhz     = key?.GetValue("~MHz")?.ToString() ?? "?";
            log.Info($"[FFTV-SYSINFO] CPU: {cpuName.Trim()} @ {mhz} MHz");
        }
        catch (Exception ex) { log.Warning($"[FFTV-SYSINFO] CPU registry: {ex.Message}"); }

        // GPU + displays via DXGI
        if (_device != null)
        {
            try
            {
                using var dxgiDev = _device.QueryInterface<IDXGIDevice>();
                using var adapter = dxgiDev.GetAdapter();
                var d = adapter.Description;
                log.Info($"[FFTV-SYSINFO] GPU: {d.Description}");
                log.Info($"[FFTV-SYSINFO] GPU VendorId=0x{d.VendorId:X4} DeviceId=0x{d.DeviceId:X4} Revision={d.Revision}");
                log.Info($"[FFTV-SYSINFO] GPU VRAM dedicated={d.DedicatedVideoMemory / 1024 / 1024}MB shared={d.SharedSystemMemory / 1024 / 1024}MB sys={d.DedicatedSystemMemory / 1024 / 1024}MB");
                log.Info($"[FFTV-SYSINFO] D3D FeatureLevel: {_device.FeatureLevel}");
                log.Info($"[FFTV-SYSINFO] AdapterLuid=0x{d.Luid:X16}");
                // Driver version via CheckInterfaceSupport — returns LARGE_INTEGER in AA.BB.CCCCC.DDDDD form.
                try
                {
                    adapter.CheckInterfaceSupport<Vortice.DXGI.IDXGIDevice>(out long raw);
                    if (raw != 0)
                    {
                        int a = (int)((raw >> 48) & 0xFFFF), b = (int)((raw >> 32) & 0xFFFF),
                            c = (int)((raw >> 16) & 0xFFFF), dd = (int)(raw & 0xFFFF);
                        log.Info($"[FFTV-SYSINFO] UMD driver version: {a}.{b}.{c}.{dd}");
                    }
                }
                catch (Exception ex) { log.Warning($"[FFTV-SYSINFO] Driver version: {ex.Message}"); }
                try
                {
                    var kdev = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance();
                    if (kdev != null)
                        log.Info($"[FFTV-SYSINFO] FFXIV Device: Width={kdev->Width} Height={kdev->Height}");
                }
                catch (Exception ex) { log.Warning($"[FFTV-SYSINFO] FFXIV kdev: {ex.Message}"); }

                uint outIdx = 0;
                while (adapter.EnumOutputs(outIdx, out IDXGIOutput? output).Success && output != null)
                {
                    using (output)
                    {
                        var od = output.Description;
                        var r  = od.DesktopCoordinates;
                        log.Info($"[FFTV-SYSINFO] Display[{outIdx}]: {od.DeviceName} " +
                                 $"{r.Right - r.Left}x{r.Bottom - r.Top} @({r.Left},{r.Top}) " +
                                 $"attached={od.AttachedToDesktop} rotation={od.Rotation}");
                    }
                    outIdx++;
                }
            }
            catch (Exception ex) { log.Warning($"[FFTV-SYSINFO] DXGI: {ex.Message}"); }
        }
        else
        {
            log.Warning("[FFTV-SYSINFO] D3D device not initialized yet.");
        }

        // Inject flags
        log.Info($"[FFTV-SYSINFO] Flags: AlphaBlendInject={AlphaBlendInject} CfDiEnabled={CfDiEnabled} CfDrawEnabled={CfDrawEnabled}");
        log.Info($"[FFTV-SYSINFO] Flags: OmSetRtLdrEnabled={OmSetRtLdrEnabled} OmSetRtInjectEnabled={OmSetRtInjectEnabled}");
        log.Info($"[FFTV-SYSINFO] Flags: CfDrawPreInject={CfDrawPreInject} BbDrawSkip={BbDrawSkip} LdrLog={LdrLog}");

        // Inject counters (lifetime totals)
        log.Info($"[FFTV-SYSINFO] Counters: ldrInject={LdrInjectCount} cfDi={CfDiCount} cfDraw={CfDrawCount} omsetrtLdr={OmSetRtLdrCount} diBb={DiBbCount}");
        log.Info($"[FFTV-SYSINFO] Counters: cfDiMissNotLdr={CfDiMissNotLdr} cfDiMissNullPtr={CfDiMissNullPtr} cfDiMissTargetMismatch={CfDiMissTargetMismatch}");
        log.Info($"[FFTV-SYSINFO] Counters: omsetrtMissSceneNotDrawn={OmSetRtMissSceneNotDrawn} omsetrtMissInUiPassFalse={OmSetRtMissInUiPassFalse} omsetrtMissDrawCall={OmSetRtMissDrawCall}");
        log.Info($"[FFTV-SYSINFO] Counters: cfDispatch={CfDispatchCount} dispatchInWindow={DispatchInWindow} dispatchNoUiPass={DispatchNoUiPass} bbSkipped={OmSetRtSkippedBbCount}");
        log.Info($"[FFTV-SYSINFO] BB draws per frame: injectFiredAtDraw={BbDrawCount} BbDrawSkip={BbDrawSkip}");
        log.Info($"[FFTV-SYSINFO] LastPath={LastInjectPath} lastRtv=0x{LastInjectRtvPtr:X} wasBB={LastInjectWasBackbuffer} fallback={LastFallbackUsed}");
        log.Info($"[FFTV-SYSINFO] LastInjectFmt={LastInjectFmt} {LastInjectW}x{LastInjectH} cbkFrames={CbkFrameCount} bbLearned={BackbufferLearned}");

        // Content/SRV state
        log.Info($"[FFTV-SYSINFO] ActiveSrvSource={ActiveSrvSource} ActiveSrvNonNull={ActiveSrvNonNull} HasTexture={HasTexture}");
        log.Info($"[FFTV-SYSINFO] VideoPlayer: hasTexture={_videoPlayer?.HasTexture} frameSrvNonNull={_videoPlayer?.FrameSrv != null}");
        log.Info($"[FFTV-SYSINFO] BrowserPlayer: hasTexture={_browserPlayer?.HasTexture} frameSrvNonNull={_browserPlayer?.FrameSrv != null}");
        log.Info($"[FFTV-SYSINFO] DebugShaderRed={DebugShaderRed} (forces TV to render solid red when true)");

        // Screen state
        var sc = StoredScreen;
        if (sc != null)
            log.Info($"[FFTV-SYSINFO] Screen: center=({sc.Center.X:F2},{sc.Center.Y:F2},{sc.Center.Z:F2}) yaw={sc.YawDegrees:F1} w={sc.Width:F1} h={sc.Height:F1} vis={sc.Visible}");
        else
            log.Info("[FFTV-SYSINFO] Screen: not set");

        var (rW, rH) = DeviceResolution;
        log.Info($"[FFTV-SYSINFO] DeviceResolution: {rW}x{rH}");
        log.Info($"[FFTV-SYSINFO] DSV set={MainSceneDsvSet} bbTex={BbTexCount} bbRtv={BbRtvCount}");
        log.Info($"[FFTV-SYSINFO] LdrTexPtr=0x{LdrTexPtr:X} LastPath={LastInjectPath}");
        log.Info("[FFTV-SYSINFO] ══════════════════════════════════════");
        log.Info("[FFTV-SYSINFO] Share your dalamud.log with the developer.");
    }

    // ── Diagnostic report file (one-click export from Debug tab) ─────────────
    /// <summary>
    /// Absolute path of the diagnostic report file. Writes via AppendDiagnosticReport accumulate here;
    /// the Debug-tab "Show log file" button opens its folder selected.
    /// </summary>
    public string DiagnosticReportPath =>
        System.IO.Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "ffxiv-tv-debug.log");

    /// <summary>
    /// Appends a self-contained snapshot of current state to the diagnostic log file.
    /// Returns the path written to.
    /// </summary>
    public string AppendDiagnosticReport(string? testLabel = null)
    {
        var path = DiagnosticReportPath;
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var sb = new System.Text.StringBuilder();
        void W(string s) => sb.AppendLine(s);

        var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
        W(string.Empty);
        W(new string('=', 70));
        W($"SNAPSHOT — {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}   FFXIV-TV v{ver}" + (testLabel == null ? "" : $"   [{testLabel}]"));
        W(new string('=', 70));

        W("## System");
        W($"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        W($"OS version: {Environment.OSVersion}");
        W($".NET: {Environment.Version}");
        W($"64-bit OS={Environment.Is64BitOperatingSystem} Process={Environment.Is64BitProcess}");
        W($"Machine: {Environment.MachineName}");
        W($"ProcessorCount: {Environment.ProcessorCount}");
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            W($"CPU: {key?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? "unknown"} @ {key?.GetValue("~MHz")?.ToString() ?? "?"} MHz");
        }
        catch (Exception ex) { W($"CPU: <err: {ex.Message}>"); }

        W(string.Empty); W("## Graphics");
        if (_device != null)
        {
            try
            {
                using var dxgiDev = _device.QueryInterface<IDXGIDevice>();
                using var adapter = dxgiDev.GetAdapter();
                var d = adapter.Description;
                W($"GPU: {d.Description}");
                W($"VendorId=0x{d.VendorId:X4} DeviceId=0x{d.DeviceId:X4} Revision={d.Revision}");
                W($"VRAM dedicated={d.DedicatedVideoMemory / 1024 / 1024}MB shared={d.SharedSystemMemory / 1024 / 1024}MB sys={d.DedicatedSystemMemory / 1024 / 1024}MB");
                W($"Adapter LUID: 0x{d.Luid:X16}");
                W($"D3D FeatureLevel: {_device.FeatureLevel}");
                try
                {
                    adapter.CheckInterfaceSupport<Vortice.DXGI.IDXGIDevice>(out long raw);
                    if (raw != 0)
                    {
                        int a = (int)((raw >> 48) & 0xFFFF), b = (int)((raw >> 32) & 0xFFFF),
                            cc = (int)((raw >> 16) & 0xFFFF), dd = (int)(raw & 0xFFFF);
                        W($"UMD driver version: {a}.{b}.{cc}.{dd}");
                    }
                }
                catch (Exception ex) { W($"Driver version: <err: {ex.Message}>"); }
                uint outIdx = 0;
                while (adapter.EnumOutputs(outIdx, out IDXGIOutput? output).Success && output != null)
                {
                    using (output)
                    {
                        var od = output.Description;
                        var r = od.DesktopCoordinates;
                        W($"Display[{outIdx}]: {od.DeviceName} {r.Right - r.Left}x{r.Bottom - r.Top} @({r.Left},{r.Top}) attached={od.AttachedToDesktop} rotation={od.Rotation}");
                    }
                    outIdx++;
                }
            }
            catch (Exception ex) { W($"DXGI: <err: {ex.Message}>"); }
        }
        else W("D3D device not initialized.");

        W(string.Empty); W("## FFXIV");
        try
        {
            var kdev = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance();
            if (kdev != null)
                W($"FFXIV Device resolution: {kdev->Width}x{kdev->Height}");
        }
        catch (Exception ex) { W($"FFXIV kdev: <err: {ex.Message}>"); }
        try
        {
            // Scan FFXIV game directory for wrapper DLLs (ReShade/GShade/XIVAlexander leftovers).
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            var gameDir = !string.IsNullOrEmpty(exePath) ? System.IO.Path.GetDirectoryName(exePath) : null;
            if (!string.IsNullOrEmpty(gameDir) && System.IO.Directory.Exists(gameDir))
            {
                var candidates = new[] { "dxgi.dll", "d3d11.dll", "dinput8.dll", "nvngx_dlss.dll", "nvngx_dlaa.dll", "GShade.dll", "GShade32.dll", "ReShade.ini", "GShade.ini" };
                var foundList = new System.Collections.Generic.List<string>();
                foreach (var c in candidates)
                    if (System.IO.File.Exists(System.IO.Path.Combine(gameDir, c))) foundList.Add(c);
                var found = foundList.ToArray();
                W($"Game dir: {gameDir}");
                W($"Wrapper DLLs present: {(found.Length == 0 ? "none" : string.Join(", ", found))}");
            }
        }
        catch (Exception ex) { W($"Wrapper scan: <err: {ex.Message}>"); }

        W(string.Empty); W("## Plugin config");
        var cfg = Plugin.PluginInterface.GetPluginConfig() as Configuration;
        if (cfg != null)
        {
            W($"AlphaMode: {cfg.AlphaMode} (INERT — not yet consulted by renderer)");
            W($"DepthMode: {cfg.DepthMode} (INERT — not yet consulted by renderer)");
            W($"ActiveMode: {cfg.ActiveMode}");
            W($"Screen.Visible: {cfg.Screen.Visible} Center: ({cfg.Screen.Center.X:F2},{cfg.Screen.Center.Y:F2},{cfg.Screen.Center.Z:F2}) w={cfg.Screen.Width:F2} h={cfg.Screen.Height:F2}");
            W($"Brightness: {cfg.Brightness} Gamma: {cfg.Gamma} Contrast: {cfg.Contrast} BloomCap: {cfg.BloomCap}");
        }

        W(string.Empty); W("## Inject state");
        W($"Flags: AlphaBlendInject={AlphaBlendInject} CfDiEnabled={CfDiEnabled} CfDrawEnabled={CfDrawEnabled} OmSetRtLdrEnabled={OmSetRtLdrEnabled} OmSetRtInjectEnabled={OmSetRtInjectEnabled}");
        W($"Flags: BbDrawSkip={BbDrawSkip} DebugShaderRed={DebugShaderRed}");
        W($"Counters: ldrInject={LdrInjectCount} cfDi={CfDiCount} cfDraw={CfDrawCount} omsetrtLdr={OmSetRtLdrCount} diBb={DiBbCount} omsetrt={OmSetRtCount}");
        W($"Miss counters: cfDiMiss(null={CfDiMissNullPtr} notLdr={CfDiMissNotLdr} tgt={CfDiMissTargetMismatch}) omsetrtMiss(sceneNotDrawn={OmSetRtMissSceneNotDrawn} inUiPassFalse={OmSetRtMissInUiPassFalse} drawCall={OmSetRtMissDrawCall})");
        W($"BB draws prev frame: {BbDrawCount}");
        W($"LastPath: {LastInjectPath}  lastRtv: 0x{LastInjectRtvPtr:X}  wasBB: {LastInjectWasBackbuffer}  fallback: {LastFallbackUsed}");
        W($"LastInjectFmt: {LastInjectFmt} {LastInjectW}x{LastInjectH}  cbkFrames: {CbkFrameCount}  bbLearned: {BackbufferLearned}");
        W($"LdrTexPtr: 0x{LdrTexPtr:X}  DSV set: {MainSceneDsvSet}  bbTex: {BbTexCount}  bbRtv: {BbRtvCount}");
        W($"ActiveSrvSource: {ActiveSrvSource}  HasTexture: {HasTexture}");
        W($"VideoPlayer.HasTexture: {_videoPlayer?.HasTexture}  BrowserPlayer.HasTexture: {_browserPlayer?.HasTexture}");

        W(string.Empty);
        W(new string('=', 70));
        W("For additional diagnostics: run /fftv trace 3 in-game before exporting again —");
        W("that writes a full per-hook pipeline trace into dalamud.log, which this");
        W("report does NOT embed. Pair this file WITH dalamud.log when sharing.");

        System.IO.File.AppendAllText(path, sb.ToString());
        Plugin.Log.Info($"[FFXIV-TV] Diagnostic snapshot appended to: {path}");
        return path;
    }

    /// <summary>Deletes the diagnostic report file if it exists.</summary>
    public void ClearDiagnosticReport()
    {
        var path = DiagnosticReportPath;
        try
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            Plugin.Log.Info($"[FFXIV-TV] Diagnostic report cleared: {path}");
        }
        catch (Exception ex) { Plugin.Log.Warning($"[FFXIV-TV] ClearDiagnosticReport: {ex.Message}"); }
    }

    /// <summary>Re-arms the one-shot alpha readback so the next eligible frame re-samples.</summary>
    public void ResetAlphaDetection()
    {
        _alphaReadbackDone = false;
        Plugin.Log.Info("[FFXIV-TV] Alpha detection reset — will re-sample on next eligible frame.");
    }

    public void Draw(ScreenDefinition screen)
    {
        if (_videoPlayer != null && _context != null)
            _videoPlayer.UploadFrame(_context);
        if (_browserPlayer != null && _context != null)
            _browserPlayer.UploadFrame(_context);

        _activeSrv = (_browserPlayer?.HasTexture == true)
            ? _browserPlayer.FrameSrv
            : (_videoPlayer?.HasTexture == true)
                ? _videoPlayer.FrameSrv
                : _imageSrv;

        // NOTE: PrepareHooks is NOT called here — Plugin.cs calls it unconditionally
        // before the Draw/DrawBlack branch. Calling it a second time resets
        // _lastSeenValidRtvPtr to 0 before it can be promoted to _targetInjectRtvPtr,
        // which permanently breaks CF injection (targetInjectPtr stays 0x0 forever).
    }

    // Draws the idle gradient screensaver quad (depth-tested).
    public void DrawBlack(ScreenDefinition screen)
    {
        if (!_initialized || _context == null) return;
        var srv = _gradientSrv ?? _blackSrv;
        if (srv == null) return;

        UpdateGradientTexture();
        _activeSrv = srv;
        // NOTE: PrepareHooks is NOT called here — same reason as Draw() above.
    }

    // ── Gradient screensaver ──────────────────────────────────────────────────
    private void UpdateGradientTexture()
    {
        if (_gradientTex == null || _context == null) return;

        _gradientTime += 1f / 60f;

        _context.Map(_gradientTex!, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None, out var mapped);
        byte* r0 = (byte*)mapped.DataPointer;
        byte* r1 = r0 + mapped.RowPitch;

        float t = _gradientTime * GradientSpeed;
        WriteHsvPixel(r0 + 0, t + _gradientPhaseOffsets[0]); // TL
        WriteHsvPixel(r0 + 4, t + _gradientPhaseOffsets[1]); // TR
        WriteHsvPixel(r1 + 0, t + _gradientPhaseOffsets[2]); // BL
        WriteHsvPixel(r1 + 4, t + _gradientPhaseOffsets[3]); // BR

        _context.Unmap(_gradientTex!, 0);
    }

    private static void WriteHsvPixel(byte* dst, float hue)
    {
        hue = hue - MathF.Floor(hue);

        float s = GradientS, v = GradientV;
        float c = v * s;
        float x = c * (1f - MathF.Abs(hue * 6f % 2f - 1f));
        float m = v - c;
        float r, g, b;
        switch ((int)(hue * 6f) % 6)
        {
            case 0:  r = c; g = x; b = 0; break;
            case 1:  r = x; g = c; b = 0; break;
            case 2:  r = 0; g = c; b = x; break;
            case 3:  r = 0; g = x; b = c; break;
            case 4:  r = x; g = 0; b = c; break;
            default: r = c; g = 0; b = x; break;
        }
        dst[0] = (byte)((b + m) * 255f);
        dst[1] = (byte)((g + m) * 255f);
        dst[2] = (byte)((r + m) * 255f);
        dst[3] = 255;
    }

    // ── Cbuffer update ────────────────────────────────────────────────────────
    // Builds the ScreenTransform TRS matrix from the stored ScreenDefinition
    // and uploads the full CbParams struct to the GPU.
    private void UpdateCbParams()
    {
        if (_cbParams == null || _context == null || _storedScreen == null) return;

        var transform = _storedScreen.ComputeScreenTransform();

        var mapped = _context.Map(_cbParams, MapMode.WriteDiscard);
        mapped.AsSpan<CbParams>(1)[0] = new CbParams
        {
            ViewProj        = _storedViewProj,
            ScreenTransform = transform,
            Brightness      = Brightness,
            Gamma           = Gamma,
            Contrast        = Contrast,
            BloomCap        = BloomCap,
            Tint            = Tint,
        };
        _context.Unmap(_cbParams);
    }

    // ── Pipeline state ────────────────────────────────────────────────────────
    private void SetState(ID3D11ShaderResourceView srv, ID3D11DepthStencilState depthState)
    {
        // No vertex buffer or input layout — SV_VertexID drives geometry.
        _context!.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

        _context.VSSetShader(_vs!);
        _context.GSSetShader(null);
        _context.HSSetShader(null);
        _context.DSSetShader(null);
        _context.VSSetConstantBuffer(0, _cbParams!);

        _context.PSSetShader(_ps!);
        _context.PSSetConstantBuffer(0, _cbParams!);
        _context.PSSetShaderResource(0, srv);
        _context.PSSetSampler(0, _sampler!);

        _context.RSSetState(_rasterizer!);
        _context.OMSetBlendState(_blendState!);
        _context.OMSetDepthStencilState(depthState, 0);
    }

    // ── State save / restore ──────────────────────────────────────────────────
    // We only save/restore what we actually change in SetState.
    // VB, IB, and InputLayout are deliberately NOT touched (SV_VertexID ignores them).
    private struct SavedState
    {
        public PrimitiveTopology          Topology;
        public ID3D11VertexShader?        VS;
        public ID3D11GeometryShader?      GS;
        public ID3D11HullShader?          HS;
        public ID3D11DomainShader?        DS;
        public ID3D11Buffer?              VSCb0;
        public ID3D11PixelShader?         PS;
        public ID3D11Buffer?              PSCb0;
        public ID3D11ShaderResourceView?  PSSrv0;
        public ID3D11SamplerState?        PSSampler0;
        public ID3D11RasterizerState?     RS;
        public ID3D11BlendState?          Blend;
        public ID3D11DepthStencilState?   DSS;
        public uint                       StencilRef;
        public Viewport[]?                Viewports;
    }

    private SavedState SaveState()
    {
        var s = new SavedState();
        s.Topology = _context!.IAGetPrimitiveTopology();
        s.VS = _context.VSGetShader();
        s.GS = _context.GSGetShader();
        s.HS = _context.HSGetShader();
        s.DS = _context.DSGetShader();
        var vsCbs = new ID3D11Buffer[1]; _context.VSGetConstantBuffers(0, vsCbs); s.VSCb0 = vsCbs[0];
        s.PS = _context.PSGetShader();
        var psCbs = new ID3D11Buffer[1]; _context.PSGetConstantBuffers(0, psCbs); s.PSCb0 = psCbs[0];
        var srvs     = new ID3D11ShaderResourceView[1]; _context.PSGetShaderResources(0, srvs); s.PSSrv0     = srvs[0];
        var samplers = new ID3D11SamplerState[1];        _context.PSGetSamplers(0, samplers);   s.PSSampler0 = samplers[0];
        s.RS    = _context.RSGetState();
        s.Blend = _context.OMGetBlendState(out _, out _);
        _context.OMGetDepthStencilState(out s.DSS, out s.StencilRef);
        s.Viewports = _context.RSGetViewports<Viewport>().ToArray();
        return s;
    }

    private void RestoreState(SavedState s)
    {
        _context!.IASetPrimitiveTopology(s.Topology);
        _context.VSSetShader(s.VS);
        _context.GSSetShader(s.GS);
        _context.HSSetShader(s.HS);
        _context.DSSetShader(s.DS);
        _context.VSSetConstantBuffer(0, s.VSCb0);
        _context.PSSetShader(s.PS);
        _context.PSSetConstantBuffer(0, s.PSCb0);
        if (s.PSSrv0 != null) _context.PSSetShaderResource(0, s.PSSrv0);
        _context.PSSetSampler(0, s.PSSampler0);
        _context.RSSetState(s.RS);
        _context.OMSetBlendState(s.Blend);
        _context.OMSetDepthStencilState(s.DSS, s.StencilRef);
        if (s.Viewports != null && s.Viewports.Length > 0) _context.RSSetViewports(s.Viewports);

        s.VS?.Dispose();
        s.GS?.Dispose();
        s.HS?.Dispose();
        s.DS?.Dispose();
        s.VSCb0?.Dispose();
        s.PS?.Dispose();
        s.PSCb0?.Dispose();
        s.PSSrv0?.Dispose();
        s.PSSampler0?.Dispose();
        s.RS?.Dispose();
        s.Blend?.Dispose();
        s.DSS?.Dispose();
    }

    // ── Dispose ───────────────────────────────────────────────────────────────
    public void Dispose()
    {
        _shaderCompileTask = null; // let task finish naturally; we just drop the reference
        DisposeResources();
        _context = null;
        _device?.Dispose();
        _device      = null;
        _initialized = false;
    }

    private void DisposeResources()
    {
        _mainSceneDsvPtr     = 0;
        _mainSceneRtvPtr     = 0;
        _sceneRtvCache.Clear();
        _omSetRTHook?.Disable();
        _omSetRTHook?.Dispose();         _omSetRTHook      = null;
        _clearRtvHook?.Disable();
        _clearRtvHook?.Dispose();        _clearRtvHook     = null;
        _drawIndexedHook?.Disable();
        _drawIndexedHook?.Dispose();     _drawIndexedHook  = null;
        _drawHook?.Disable();
        _drawHook?.Dispose();                        _drawHook                    = null;
        _drawIndexedInstancedHook?.Disable();
        _drawIndexedInstancedHook?.Dispose();        _drawIndexedInstancedHook    = null;
        _drawInstancedHook?.Disable();
        _drawInstancedHook?.Dispose();               _drawInstancedHook           = null;
        _copyResourceHook?.Disable();
        _copyResourceHook?.Dispose();                _copyResourceHook                    = null;
        _dispatchHook?.Disable();
        _dispatchHook?.Dispose();                    _dispatchHook                        = null;
        _drawIndexedInstancedIndirectHook?.Disable();
        _drawIndexedInstancedIndirectHook?.Dispose(); _drawIndexedInstancedIndirectHook   = null;
        _drawInstancedIndirectHook?.Disable();
        _drawInstancedIndirectHook?.Dispose();       _drawInstancedIndirectHook           = null;
        _dispatchIndirectHook?.Disable();
        _dispatchIndirectHook?.Dispose();            _dispatchIndirectHook                = null;
        _ldrTexPtr = 0;
        _trackedDsv?.Dispose();   _trackedDsv   = null;
        _activeSrv  = null;
        _gradientSrv?.Dispose();  _gradientSrv  = null;
        _gradientTex?.Dispose();  _gradientTex  = null;
        _blackSrv?.Dispose();     _blackSrv     = null;
        _imageSrv?.Dispose();     _imageSrv     = null;
        _loadedImagePath = string.Empty;
        _sampler?.Dispose();      _sampler      = null;
        _dsReverseZWrite?.Dispose(); _dsReverseZWrite = null;
        _dsReverseZ?.Dispose();   _dsReverseZ   = null;
        _dsNoDepth?.Dispose();    _dsNoDepth    = null;
        _rasterizer?.Dispose();   _rasterizer   = null;
        _blendState?.Dispose();   _blendState   = null;
        _depthOnlyBlendState?.Dispose(); _depthOnlyBlendState = null;
        _ps?.Dispose();           _ps           = null;
        _psLdr?.Dispose();        _psLdr        = null;
        _psLdrDebugRed?.Dispose(); _psLdrDebugRed = null;
        _vs?.Dispose();           _vs           = null;
        _cbParams?.Dispose();     _cbParams     = null;
    }
}
