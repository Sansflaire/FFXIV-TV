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
/// Shader architecture (PyonPix-inspired):
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
    /// <summary>Output scale multiplier applied before writing to the render target.
    /// Injecting into the post-tonemap LDR intermediate (sRGB surface): 1.0 = correct.
    /// Only needs adjustment if colors look wrong; range 0.01–1.0.</summary>
    public float HdrScale { get; set; } = 1.0f;

    private ID3D11VertexShader?      _vs;
    private ID3D11PixelShader?       _ps;
    private ID3D11BlendState?        _blendState;
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

    private Hook<OMSetRenderTargetsDelegate>?      _omSetRTHook;
    private Hook<ClearRenderTargetViewDelegate>?   _clearRtvHook;
    private Hook<DrawIndexedDelegate>?             _drawIndexedHook;
    private Hook<DrawDelegate>?                    _drawHook;

    // Re-entrancy guard: prevents ExecuteInlineDraw's internal D3D calls from recursing
    // back into our hook logic. Any detour call that sees _inHookDetour=true immediately
    // calls Original and returns, avoiding nested state mutation or exception leaks.
    [ThreadStatic] private static bool _inHookDetour;

    private ID3D11DepthStencilView? _trackedDsv;
    private nint                    _contextPtr;
    private bool _dsvLoggedOnce;
    private int  _cbkFrameCount;

    // 3D→2D transition detection. OMSetRenderTargets tracks whether the previous call
    // bound a DSV (3D scene pass). When it switches to no-DSV (2D UI pass), _inUiPass is
    // set to true so the ClearRTV inject knows it's in the right phase.
    private bool _prevCallHadDsv   = false;
    private bool _inUiPass         = false;
    // Set by ClearRTV inject to prevent double-injection in the same frame.
    // Reset by Draw()/DrawBlack() at ImGui time.
    private volatile bool _frameInjectionDone = false;

    // Backbuffer identification.
    // OMGetRenderTargets at Draw() time returns null because Draw() fires during ImGui
    // command-collection phase — Dalamud hasn't bound the backbuffer yet (that happens
    // in ImGui_ImplDX11_RenderDrawData, which runs AFTER all Draw() callbacks).
    // We learn the backbuffer texture ptr from that first no-DSV OMSetRenderTargets call
    // after Draw() returns (_pendingLearnBackbuffer). We then match by TEXTURE pointer
    // (GetResource) rather than RTV pointer, because FFXIV and Dalamud may create separate
    // RTV COM objects for the same underlying backbuffer texture.
    private readonly System.Collections.Generic.HashSet<nint> _knownBackbufferTexturePtrs = new();
    private readonly System.Collections.Generic.HashSet<nint> _knownBackbufferRtvPtrs     = new();
    private readonly System.Collections.Generic.HashSet<nint> _checkedRtvPtrs             = new();
    private volatile bool _pendingLearnBackbuffer = false;
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
    // Set to true once the composite SRV inputs have been logged (one-shot diagnostic).
    private bool _compositeInputsLogged = false;
    // Texture ptr of the composite input surface (SRV[0] at composite DrawIndexed time).
    // Stored by LogCompositeInputs; used to create _compositeInputRtv for direct injection.
    private nint _compositeInputTexPtr = 0;
    // Cached RTV created from _compositeInputTexPtr. We inject our rect into this surface
    // BEFORE the composite DrawIndexed reads it as an SRV and blits it to the backbuffer.
    private ID3D11RenderTargetView? _compositeInputRtv = null;
    // Diagnostic counter for DrawDetour injection logging (throttled).
    private int _drawDrvEligCount = 0;
    // Diagnostic counter for OMSetRenderTargets injection logging (throttled).
    private int _omSetRtInjectCount = 0;
    // One-shot: logs the first call to PrepareHooks so we know it's running.
    private bool _prepareHooksLoggedOnce = false;
    // Counts how many times DrawDetour fired on the bb but injection was blocked.
    private int _drawBlockedLogCount = 0;
    // Counts how many times OMSetRT detected bb but intermediateRTV was null.
    private int _omsetNullIntermCount = 0;
    // Counts how many times OMSetRT detected bb but conditions other than intermediate blocked.
    private int _omsetBlockedLogCount = 0;
    // Total DrawIndexed seq entries emitted (prevents infinite spam when _cbkFrameCount stays 0).
    private int _drawSeqLogTotal = 0;
    // ── Scene-pass inject (PyonPix RendererServiceAlt approach) ──────────────
    // Fire inject when FFXIV transitions AWAY from the 3D scene pass.
    // At that moment all 3D geometry is drawn; depth buffer is valid.
    // We manually re-bind MainSceneDSV+MainSceneRTV and draw our rect.
    // Post-processing + HUD render after → naturally appear in front of rect.
    //
    // MainSceneDSV  = first full-resolution (device w×h) DSV seen before _inUiPass.
    // MainSceneRTV  = first full-res BGRA8 (B8G8R8A8_UNorm) RTV seen alongside MainSceneDSV.
    //                 PyonPix analysis: B8G8R8A8_UNorm scores +500; R16G16B16A16_Float scores 0.
    //                 Reset every frame (PyonPix AutoSetRTV equivalent) so it re-latches on first BGRA8 bind.
    // _prevSceneRendered: true when the PREVIOUS OMSetRenderTargets call bound MainDSV+MainRTV.
    //                 Inject fires when _prevSceneRendered=true && currentSceneRendered=false
    //                 (i.e., FFXIV just left the scene pass). RendererServiceAlt pattern.
    private nint _mainSceneDsvPtr        = 0;  // set once, stable
    private nint _mainSceneRtvPtr        = 0;  // updated each frame
    private bool _sceneDrawnThisFrame    = false;
    private bool _prevSceneRendered      = false;  // RendererServiceAlt: previous-call scene state
    private bool _omSetRtSceneInjectActive = false;  // re-entrancy guard for scene inject
    private int  _sceneInjectCount       = 0;
    // Post-tonemap inject: counter + cache for the first LDR full-res surface after bloom.
    private int  _postTonemapInjectCount = 0;
    // Cache: rtvPtr → isValidPostBloom (full-res + not R16 + not BB). Avoids repeated COM queries.
    private readonly System.Collections.Generic.Dictionary<nint, bool> _postBloomRtvCache = new();
    // Cache: rtvPtr → isSceneRtv (full-res + R16). Avoids repeated COM queries.
    private readonly System.Collections.Generic.Dictionary<nint, bool> _sceneRtvCache = new();
    private int _sceneRtvLogCount = 0;  // throttle per-ptr identification logs
    // Total PrepareHooks calls — used for periodic heartbeat log.
    private int _prepareHooksCallCount = 0;
    // Throttle for _pendingLearnBackbuffer-consumed-but-skipped log.
    private int _bbLearnSkipLogCount = 0;
    // Last no-DSV RTV seen this frame.
    // The Dalamud ImGui backbuffer bind is the LAST no-DSV OMSetRenderTargets each frame,
    // so _lastNoDsvRtvPtr at the END of frame N is the swapchain backbuffer RTV.
    // We check it at the START of frame N+1 (in PrepareHooks) to learn the bb texture.
    // This replaces the flawed _pendingLearnBackbuffer heuristic which fires too early
    // and captures FFXIV post-processing surfaces instead of the real swapchain bb.
    private nint _lastNoDsvRtvPtr = 0;
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
    private const float GradientSpeed = 0.018f;

    // Own texture loaded from the image file.
    private ID3D11ShaderResourceView? _imageSrv;
    private string                    _loadedImagePath = string.Empty;

    private bool _initialized;
    public bool IsAvailable => _initialized;
    public bool HasTexture  => _imageSrv != null || (_videoPlayer?.HasTexture == true) || (_browserPlayer?.HasTexture == true);

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
        public float     HdrScale;        // 4 — HDR output scale: maps sRGB [0,1] into linear HDR range before tone-mapping
        public Vector4   Tint;            // 16 — rgba multiplier (1,1,1,1 = no change)
    }

    // ── HLSL ─────────────────────────────────────────────────────────────────

    // CBUFFER_DEF is the shared cbuffer declaration used in both VS and PS.
    private const string CBUFFER_DEF = @"
cbuffer CbParams : register(b0) {
    row_major float4x4 ViewProj;
    row_major float4x4 ScreenTransform;
    float Brightness; float Gamma; float Contrast; float HdrScale;
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

    // PS: standard texture sample + brightness → contrast → gamma → tint pipeline.
    private const string PS_SRC = CBUFFER_DEF + @"
Texture2D    tex  : register(t0);
SamplerState samp : register(s0);
struct VSOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
float4 main(VSOut input) : SV_TARGET {
    float4 color = tex.Sample(samp, input.uv);
    color.rgb *= Brightness;
    color.rgb  = saturate((color.rgb - 0.5f) * Contrast + 0.5f);
    color.rgb  = pow(saturate(color.rgb), 1.0f / max(Gamma, 0.001f));
    return float4(color.rgb * Tint.rgb * HdrScale, color.a * Tint.a);
}";

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

        Plugin.Log.Info("[FFXIV-TV] OMSetRenderTargets + ClearRTV + DrawIndexed + Draw hooks installed.");
    }

    private void OMSetRenderTargetsDetour(nint pCtx, uint numViews, nint* ppRTVs, nint pDSV)
    {
        if (_inHookDetour) { _omSetRTHook?.Original(pCtx, numViews, ppRTVs, pDSV); return; }
        _inHookDetour = true;
        try
        {
            if (pCtx == _contextPtr)
            {
                // Only consider calls that bind at least one RTV (ignore depth-only shadow passes).
                bool hasRtvs = numViews > 0 && ppRTVs != null && ppRTVs[0] != 0;
                if (hasRtvs)
                {
                    bool hasDsv = pDSV != 0;

                    // Track the main-scene DSV for depth testing during our inject.
                    // Only update BEFORE _inUiPass — once we enter post-processing/2D, freeze it.
                    // Skip update during scene inject (re-entrant call) to avoid disposing the
                    // DSV wrapper we are currently using inside ExecuteInlineDraw.
                    if (hasDsv && !_inUiPass && !_omSetRtSceneInjectActive)
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
                        // ── Scene-pass identification (PyonPix approach) ──────────────────
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
                                            // PyonPix RendererServiceAlt targets R16G16B16A16_Float —
                                            // FFXIV's HDR accumulation buffer (MainRTV).
                                            // The 71973 DrawIndexed READS from R16 as SRV and writes to a
                                            // different intermediate → our inject into R16 survives into
                                            // the post-processing chain. BGRA8 fails because FFXIV clears
                                            // it immediately after binding AND 71973 overwrites it.
                                            bool isR16 = d.Format == Format.R16G16B16A16_Float;
                                            isSceneRtv = fullRes && isR16;
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
                                _mainSceneRtvPtr = rtvPtr3d;  // always update → converges to LAST qualifying R16 each frame (PyonPix AutoSetRTV)
                        }
                    }

                    // ── RendererServiceAlt inject: fire when game LEAVES MainDSV+MainRTV ──────
                    // PyonPix RendererServiceAlt pattern: check _prevSceneRendered BEFORE
                    // updating it. Fires on the OMSetRenderTargets call that transitions AWAY
                    // from MainDSV+R16G16B16A16_Float (i.e., after 3D scene is fully drawn).
                    // At that moment we re-bind R16+MainDSV, draw rect, restore state.
                    // Post-processing reads R16 (with our rect baked in) → composites to
                    // intermediates → BB. HUD draws after all of this → naturally in front.
                    bool currentSceneRendered = hasDsv && !_inUiPass
                        && _mainSceneDsvPtr != 0 && _mainSceneRtvPtr != 0
                        && pDSV == _mainSceneDsvPtr && ppRTVs[0] == _mainSceneRtvPtr;

                    // ── RendererServiceAlt: detect when 3D scene is complete ──────────────
                    // We DON'T inject into R16G16B16A16_Float here — that causes FFXIV bloom
                    // and auto-exposure to overexpose the rect. Instead, mark the scene done
                    // so DrawIndexedDetour can inject into the post-tonemap LDR intermediate
                    // (clean sRGB, no bloom). HUD draws on that surface after our inject → in front.
                    if (_prevSceneRendered && !currentSceneRendered && !_sceneDrawnThisFrame
                        && !_omSetRtSceneInjectActive)
                    {
                        _sceneDrawnThisFrame = true;
                        _sceneInjectCount++;
                        if (_sceneInjectCount <= 5 || _sceneInjectCount % 300 == 0)
                            Plugin.Log.Info($"[FFXIV-TV] Scene complete #{_sceneInjectCount} → awaiting post-tonemap surface in DrawIndexedDetour");
                    }

                    // Update _prevSceneRendered for next call (skip during re-entrant inject call).
                    if (!_omSetRtSceneInjectActive)
                        _prevSceneRendered = currentSceneRendered;

                    // Detect 3D→2D transition: first RTV-binding no-DSV call after a DSV call.
                    if (!hasDsv && _prevCallHadDsv)
                        _inUiPass = true;

                    _prevCallHadDsv = hasDsv;

                    if (!hasDsv)
                    {
                        nint rtvPtr = ppRTVs[0];

                        // Track the last no-DSV RTV seen this frame.
                        // The Dalamud ImGui bb bind is the LAST one — PrepareHooks on
                        // the next frame uses this to learn the real swapchain backbuffer.
                        _lastNoDsvRtvPtr = rtvPtr;

                        // STEP A: Learn backbuffer texture ptr from Dalamud's ImGui bind.
                        // _pendingLearnBackbuffer is set by Draw()/DrawBlack(). The first
                        // no-DSV OMSetRenderTargets after Draw() returns is
                        // ImGui_ImplDX11_RenderDrawData binding the swapchain backbuffer.
                        if (_pendingLearnBackbuffer)
                        {
                            _pendingLearnBackbuffer = false;
                            if (!_checkedRtvPtrs.Contains(rtvPtr))
                            {
                                _checkedRtvPtrs.Add(rtvPtr);
                                try
                                {
                                    // Wrap as ID3D11View to access Resource property
                                    // (Vortice does not expose base-class members on derived types).
                                    var lv = new ID3D11View(rtvPtr);
                                    lv.AddRef();
                                    try
                                    {
                                        using var lres = lv.Resource;
                                        nint texPtr = lres.NativePointer;
                                        if (_knownBackbufferTexturePtrs.Add(texPtr))
                                            Plugin.Log.Info($"[FFXIV-TV] bb-tex learned (ImGui bind): rtv=0x{rtvPtr:X} tex=0x{texPtr:X}");
                                    }
                                    finally { lv.Dispose(); }
                                }
                                catch (Exception ex) { Plugin.Log.Warning($"[FFXIV-TV] learn bb-tex failed: {ex.Message}"); }
                            }
                            else if (_bbLearnSkipLogCount < 5)
                            {
                                // _pendingLearnBackbuffer fired but this RTV was already checked —
                                // the bb-learn opportunity was consumed by a non-bb surface. If this
                                // keeps happening, _knownBackbufferTexturePtrs stays empty and
                                // injection can never fire.
                                _bbLearnSkipLogCount++;
                                Plugin.Log.Warning($"[FFXIV-TV] bb-learn SKIP #{_bbLearnSkipLogCount}: rtv=0x{rtvPtr:X} already checked — bb tex NOT updated. bbTexCount={_knownBackbufferTexturePtrs.Count}");
                            }
                        }

                        // STEP B: During inUiPass, identify and inject on the backbuffer bind.
                        if (_inUiPass)
                        {
                            // Track all intermediate (non-backbuffer) RTV ptrs seen during _inUiPass.
                            // Used by ClearRTV hook to find the 2D HUD RT (cleared+drawn-to surface).
                            if (!_knownBackbufferRtvPtrs.Contains(rtvPtr))
                                _inUiPassRtvPtrs.Add(rtvPtr);

                            // Check if this RTV is backed by a known backbuffer texture.
                            if (!_knownBackbufferRtvPtrs.Contains(rtvPtr)
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

                            // When the known backbuffer is bound, inject into the currently-bound
                            // intermediate RT BEFORE Original fires.
                            // Hypothesis: intermediate has 3D (post-processed) but not HUD yet.
                            // After our inject, Original binds BB. FFXIV Draw calls copy intermediate
                            // (now with rect) → BB, then add HUD on top → HUD renders in front.
                            if (_knownBackbufferRtvPtrs.Contains(rtvPtr))
                            {
                                _bbBindCountThisUiPass++;
                                _currentBbRtvPtr = rtvPtr;
                                if (_bbBindLogCount < 3)
                                {
                                    _bbBindLogCount++;
                                    Plugin.Log.Info($"[FFXIV-TV] bb bind #{_bbBindCountThisUiPass} rtv=0x{rtvPtr:X}");
                                }

                                // Approach: inject into intermediate RT at BB-bind moment.
                                // ExecuteInlineDraw will re-enter OMSetRenderTargetsDetour when it
                                // sets/restores RTs — those re-entrant calls are harmless (_frameInjectionDone).
                                if (!_frameInjectionDone && _initialized && _storedScreen != null
                                    && _dsReverseZ != null && _dsNoDepth != null && _cbParams != null)
                                {
                                    var intermediateRtvArr = new ID3D11RenderTargetView[1];
                                    _context!.OMGetRenderTargets(1u, intermediateRtvArr, out var intermediateDsv);
                                    if (intermediateRtvArr[0] != null)
                                    {
                                        _frameInjectionDone = true;
                                        _omSetRtInjectCount++;
                                        if (_omSetRtInjectCount <= 5 || _omSetRtInjectCount % 300 == 0)
                                            Plugin.Log.Info($"[FFXIV-TV] OMSetRT inject #{_omSetRtInjectCount} into intermediate=0x{intermediateRtvArr[0].NativePointer:X}");
                                        bool useDepth = CheckDepthCompatibility(intermediateRtvArr[0]);
                                        try { ExecuteInlineDraw(intermediateRtvArr[0], useDepth); }
                                        catch (Exception ex) { Plugin.Log.Warning($"[FFXIV-TV] OMSetRT inject failed: {ex.Message}"); }
                                    }
                                    else if (_omsetNullIntermCount < 5)
                                    {
                                        _omsetNullIntermCount++;
                                        Plugin.Log.Warning($"[FFXIV-TV] OMSetRT bb=0x{rtvPtr:X} but intermediate RTV is null (no RT bound before BB bind?)");
                                    }
                                    intermediateRtvArr[0]?.Dispose();
                                    intermediateDsv?.Dispose();
                                }
                                else if (!_frameInjectionDone && _omsetBlockedLogCount < 5)
                                {
                                    _omsetBlockedLogCount++;
                                    Plugin.Log.Warning($"[FFXIV-TV] OMSetRT bb=0x{rtvPtr:X} BLOCKED: init={_initialized} screen={_storedScreen != null} dsRZ={_dsReverseZ != null} dsND={_dsNoDepth != null} cb={_cbParams != null}");
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
            _omSetRTHook?.Original(pCtx, numViews, ppRTVs, pDSV);
        }
    }

    private void ClearRenderTargetViewDetour(nint pCtx, nint pRTV, float* pColorRGBA)
    {
        if (_inHookDetour) { _clearRtvHook?.Original(pCtx, pRTV, pColorRGBA); return; }
        _inHookDetour = true;
        // Track (don't inject here). Record the most recently cleared non-backbuffer RT during
        // _inUiPass. DrawIndexed/Draw detour uses this as a fallback injection target.
        // NOTE: the ClearRTV-cleared surface may have been bound WITH a DSV during the 3D pass
        // and therefore NOT appear in _inUiPassRtvPtrs. The v0.5.38 filter (_inUiPassRtvPtrs)
        // was too restrictive — it excluded valid surfaces. Filter only on !backbuffer.
        try
        {
            if (pCtx == _contextPtr && _inUiPass && !_knownBackbufferRtvPtrs.Contains(pRTV))
            {
                _lastClearedUiPassRtvPtr = pRTV;
                // Sequence log (first 3 frames only — helps identify the HUD RT in the log).
                if (_cbkFrameCount < 3)
                    Plugin.Log.Info($"[FFXIV-TV] ClearRTV seq: ptr=0x{pRTV:X} frame={_cbkFrameCount}");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[FFXIV-TV] ClearRenderTargetViewDetour exception: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _inHookDetour = false;
            _clearRtvHook?.Original(pCtx, pRTV, pColorRGBA);
        }
    }

    // DrawIndexedDetour: primary injection point.
    // BB is already bound when the composite-blit DrawIndexed fires, so _currentBbRtvPtr != 0.
    // On the first DrawIndexed with BB bound we call Original first (composite blit runs),
    // then inject our rect, then set _frameInjectionDone=true.  All subsequent DrawIndexed
    // calls (HUD) and Draw calls just call Original → HUD renders on top of the rect.
    private void DrawIndexedDetour(nint pCtx, uint indexCount, uint startIndex, int baseVertex)
    {
        if (_inHookDetour) { _drawIndexedHook?.Original(pCtx, indexCount, startIndex, baseVertex); return; }
        _inHookDetour = true;
        bool calledOriginal = false;
        try
        {
            // Post-tonemap inject: fires on the first full-res non-R16 non-BB DrawIndexed in
            // the no-DSV (post-processing) phase after the 3D scene is complete.
            // Strategy: call Original first (lets the tonemap DrawIndexed write its LDR output
            // to the intermediate RT), then inject our rect on top. HUD elements draw on the
            // same intermediate RT afterward → HUD naturally in front of our rect.
            // No depth testing: we're in a 2D post-processing pass with no DSV.
            if (pCtx == _contextPtr && _inUiPass && _sceneDrawnThisFrame && !_frameInjectionDone
                && _initialized && _storedScreen != null && _dsNoDepth != null && _cbParams != null)
            {
                var ptRtvArr = new ID3D11RenderTargetView[1];
                _context!.OMGetRenderTargets(1u, ptRtvArr, out var ptDsv);
                ptDsv?.Dispose();
                nint ptRtvPtr = ptRtvArr[0]?.NativePointer ?? 0;
                if (ptRtvPtr != 0 && !_knownBackbufferRtvPtrs.Contains(ptRtvPtr))
                {
                    if (!_postBloomRtvCache.TryGetValue(ptRtvPtr, out bool isValidTarget))
                    {
                        isValidTarget = false;
                        try
                        {
                            var kdev = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance();
                            using var ptRes = ptRtvArr[0]!.Resource;
                            var ptTex = new ID3D11Texture2D(ptRes.NativePointer);
                            ptTex.AddRef();
                            try
                            {
                                var ptDesc = ptTex.Description;
                                bool fullRes = kdev != null && ptDesc.Width == kdev->Width && ptDesc.Height == kdev->Height;
                                // Only accept 8bpc BGRA/RGBA — the tonemapped sRGB output.
                                // R11G11B10_Float, R16, R10G10B10A2 are HDR intermediates;
                                // drawing into them corrupts the whole-scene color pipeline.
                                bool isBgra8 = ptDesc.Format == Format.B8G8R8A8_UNorm
                                            || ptDesc.Format == Format.B8G8R8A8_UNorm_SRgb
                                            || ptDesc.Format == Format.R8G8B8A8_UNorm
                                            || ptDesc.Format == Format.R8G8B8A8_UNorm_SRgb;
                                isValidTarget = fullRes && isBgra8;
                                Plugin.Log.Info($"[FFXIV-TV] PostBloom candidate 0x{ptRtvPtr:X} {ptDesc.Width}x{ptDesc.Height} fmt={ptDesc.Format} fullRes={fullRes} isBgra8={isBgra8} valid={isValidTarget}");
                            }
                            finally { ptTex.Dispose(); }
                        }
                        catch (Exception ex) { Plugin.Log.Warning($"[FFXIV-TV] PostBloom check failed: {ex.Message}"); }
                        _postBloomRtvCache[ptRtvPtr] = isValidTarget;
                    }
                    if (isValidTarget)
                    {
                        _frameInjectionDone = true;
                        calledOriginal = true;
                        _drawIndexedHook?.Original(pCtx, indexCount, startIndex, baseVertex);
                        _postTonemapInjectCount++;
                        if (_postTonemapInjectCount <= 5 || _postTonemapInjectCount % 300 == 0)
                            Plugin.Log.Info($"[FFXIV-TV] PostTonemap inject #{_postTonemapInjectCount} idx={indexCount} rtv=0x{ptRtvPtr:X}");
                        try { ExecuteInlineDraw(ptRtvArr[0]!, useDepth: false); }
                        catch (Exception ex) { Plugin.Log.Warning($"[FFXIV-TV] PostTonemap inject failed: {ex.Message}"); }
                    }
                }
                ptRtvArr[0]?.Dispose();
            }

            // BB-path inject: fires on the first DrawIndexed after BB is bound (composite blit).
            if (pCtx == _contextPtr && _currentBbRtvPtr != 0
                && !_frameInjectionDone
                && _initialized && _storedScreen != null
                && _dsReverseZ != null && _dsNoDepth != null && _cbParams != null)
            {
                _frameInjectionDone = true;
                calledOriginal = true;
                _drawIndexedHook?.Original(pCtx, indexCount, startIndex, baseVertex);
                _drawDrvEligCount++;
                if (_drawDrvEligCount <= 5 || _drawDrvEligCount % 300 == 0)
                    Plugin.Log.Info($"[FFXIV-TV] DrawIndexed inject #{_drawDrvEligCount} into bb=0x{_currentBbRtvPtr:X}");
                var bbRtv = new ID3D11RenderTargetView(_currentBbRtvPtr);
                bbRtv.AddRef();
                try
                {
                    bool useDepth = CheckDepthCompatibility(bbRtv);
                    ExecuteInlineDraw(bbRtv, useDepth);
                }
                catch (Exception ex) { Plugin.Log.Warning($"[FFXIV-TV] DrawIndexed inject failed: {ex.Message}"); }
                finally { bbRtv.Dispose(); }
            }

            if (pCtx == _contextPtr && _inUiPass && _rtvToTexture.Count > 0)
            {
                if (!_compositeInputsLogged)
                    LogCompositeInputs();

                if (_compositeInputTexPtr != 0 && _compositeInputRtv == null && _device != null)
                {
                    try
                    {
                        var tex = new ID3D11Texture2D(_compositeInputTexPtr);
                        tex.AddRef();
                        try { _compositeInputRtv = _device.CreateRenderTargetView(tex, null); }
                        finally { tex.Dispose(); }
                        Plugin.Log.Info($"[FFXIV-TV] Created composite input RTV for tex=0x{_compositeInputTexPtr:X}");
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.Warning($"[FFXIV-TV] CreateRenderTargetView(compositeInput) failed: {ex.Message}");
                        _compositeInputTexPtr = 0;
                    }
                }

                // Diagnostic: log every DrawIndexed during _inUiPass for the first 100 entries total.
                // Shows call order, index counts, and which texture is in SRV[0] at each step.
                // Goal: identify which DrawIndexed is the true final composite (last one before BB bind).
                // Hard-capped at 100 total (not per-frame) so it never drowns out other log entries
                // even if _cbkFrameCount stays 0 (injection never fires).
                if (_drawSeqLogTotal < 100)
                {
                    try
                    {
                        var rtvArr = new ID3D11RenderTargetView[1];
                        _context!.OMGetRenderTargets(1u, rtvArr, out var dsv);
                        var srvArr = new ID3D11ShaderResourceView[1];
                        _context.PSGetShaderResources(0, srvArr);
                        nint rtvPtr = rtvArr[0]?.NativePointer ?? 0;
                        nint srvTex = 0;
                        if (srvArr[0] != null)
                        {
                            try { using var res = srvArr[0]!.Resource; srvTex = res.NativePointer; }
                            catch { /* ignore */ }
                        }
                        bool isCompositeSrv = srvTex == _compositeInputTexPtr && _compositeInputTexPtr != 0;
                        _drawSeqLogTotal++;
                        Plugin.Log.Info($"[FFXIV-TV] DrawIndexed seq #{_drawSeqLogTotal} frame={_cbkFrameCount} idx={indexCount} rt=0x{rtvPtr:X} srv0tex=0x{srvTex:X} isCompositeSrv={isCompositeSrv}");
                        rtvArr[0]?.Dispose();
                        dsv?.Dispose();
                        srvArr[0]?.Dispose();
                    }
                    catch (Exception ex) { Plugin.Log.Warning($"[FFXIV-TV] DrawIndexed diag failed: {ex.Message}"); }
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
                _drawIndexedHook?.Original(pCtx, indexCount, startIndex, baseVertex);
        }
    }

    // DrawDetour: injection point. Draw() calls fire after OMSetRenderTargets(backbuffer),
    // so _currentBbRtvPtr != 0 here. Call Original first (lets FFXIV's draw run), then inject
    // with useDepth=true so 3D geometry/characters correctly occlude our rect (reversed-Z DSV).
    private void DrawDetour(nint pCtx, uint vertexCount, uint startVertex)
    {
        if (_inHookDetour) { _drawHook?.Original(pCtx, vertexCount, startVertex); return; }
        _inHookDetour = true;
        bool calledOriginal = false;
        try
        {
            if (pCtx == _contextPtr && _inUiPass && _currentBbRtvPtr != 0
                && !_frameInjectionDone)
            {
                // Log which condition is blocking injection (throttled to first 5).
                if (!_initialized || _storedScreen == null || _dsReverseZ == null || _dsNoDepth == null || _cbParams == null)
                {
                    if (_drawBlockedLogCount < 5)
                    {
                        _drawBlockedLogCount++;
                        Plugin.Log.Warning($"[FFXIV-TV] DrawDetour bb=0x{_currentBbRtvPtr:X} BLOCKED: init={_initialized} screen={_storedScreen != null} dsRZ={_dsReverseZ != null} dsND={_dsNoDepth != null} cb={_cbParams != null}");
                    }
                }
            }

            if (pCtx == _contextPtr && _inUiPass && _currentBbRtvPtr != 0
                && !_frameInjectionDone
                && _initialized && _storedScreen != null
                && _dsReverseZ != null && _dsNoDepth != null && _cbParams != null)
            {
                _frameInjectionDone = true;
                calledOriginal = true;
                _drawHook?.Original(pCtx, vertexCount, startVertex);

                _drawDrvEligCount++;
                if (_drawDrvEligCount <= 5 || _drawDrvEligCount % 300 == 0)
                    Plugin.Log.Info($"[FFXIV-TV] Draw inject #{_drawDrvEligCount} into bb=0x{_currentBbRtvPtr:X} dsv={_trackedDsv != null}");

                var bbRtv = new ID3D11RenderTargetView(_currentBbRtvPtr);
                bbRtv.AddRef();
                try
                {
                    bool useDepth = CheckDepthCompatibility(bbRtv);
                    ExecuteInlineDraw(bbRtv, useDepth);
                }
                catch (Exception ex) { Plugin.Log.Warning($"[FFXIV-TV] Draw inject failed: {ex.Message}"); }
                finally { bbRtv.Dispose(); }
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
                _drawHook?.Original(pCtx, vertexCount, startVertex);
        }
    }

    // Check whether _trackedDsv can be safely paired with the given RTV (same texture dimensions).
    // D3D11 silently renders nothing if DSV and RTV sizes differ — no exception, no visible rect.
    // Result is cached in _depthCompatible; recomputed whenever _trackedDsv changes (new frame DSV).
    private bool CheckDepthCompatibility(ID3D11RenderTargetView bbRtv)
    {
        if (_trackedDsv == null) return false;
        if (_depthCompatible.HasValue) return _depthCompatible.Value;

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
            _depthCompatible = ok;
            Plugin.Log.Info($"[FFXIV-TV] Depth check: dsv={dsvW}x{dsvH} bb={bbW}x{bbH} compatible={ok}");
            return ok;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[FFXIV-TV] Depth check failed: {ex.Message}");
            _depthCompatible = false;
            return false;
        }
    }

    // useDepth=true: bind _trackedDsv (reversed-Z depth testing — correct for intermediate RTs).
    // useDepth=false: no DSV — safe for composite input texture which may differ in size from DSV.
    // restoreAfterDraw=true  (default, BB inject): after draw, unbind the DSV so subsequent
    //   FFXIV 2D UI draw calls aren't affected by the depth stencil we bound.
    // restoreAfterDraw=false: leave bound targets as-is (caller manages state).
    // overrideDepthState: when non-null, use this depth state instead of the default selection.
    //   Scene inject passes _dsReverseZWrite (DepthWriteMask.All) so FFXIV geometry depth-tests
    //   against our written depth values. BB inject uses default (_dsReverseZ, WriteZero).
    private void ExecuteInlineDraw(ID3D11RenderTargetView rtv, bool useDepth = true, bool restoreAfterDraw = true, ID3D11DepthStencilState? overrideDepthState = null)
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

        ID3D11DepthStencilView? dsv = useDepth ? _trackedDsv : null;
        var depthState = overrideDepthState ?? (dsv != null ? _dsReverseZ! : _dsNoDepth!);

        UpdateCbParams();

        var saved = SaveState();
        try
        {
            _context.OMSetRenderTargets(new[] { rtv }, dsv);
            SetState(srv, depthState);
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

    // Log the PS SRV inputs at composite DrawIndexed time; store SRV[0] texture ptr for injection.
    // _compositeInputsLogged is set to true ONLY on success — failed calls retry next frame.
    private void LogCompositeInputs()
    {
        try
        {
            var srvs = new ID3D11ShaderResourceView[8];
            _context!.PSGetShaderResources(0, srvs);
            for (int i = 0; i < srvs.Length; i++)
            {
                if (srvs[i] == null) continue;
                try
                {
                    using var res = srvs[i].Resource;
                    nint texPtr = res.NativePointer;
                    nint matchRtv = 0;
                    foreach (var kvp in _rtvToTexture)
                        if (kvp.Value == texPtr) { matchRtv = kvp.Key; break; }
                    Plugin.Log.Info($"[FFXIV-TV] composite SRV[{i}]: tex=0x{texPtr:X} matchRtv=0x{matchRtv:X}");
                    // Store SRV[0] as the composite input texture for direct RTV injection.
                    if (i == 0 && _compositeInputTexPtr == 0)
                        _compositeInputTexPtr = texPtr;
                }
                finally { srvs[i].Dispose(); }
            }
            _compositeInputsLogged = true; // mark done only after successful execution
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[FFXIV-TV] LogCompositeInputs failed: {ex.Message}");
            // _compositeInputsLogged stays false → retries next eligible frame
        }
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

        // No vertex buffer, no input layout — SV_VertexID drives the geometry.

        _blendState = _device.CreateBlendState(BlendDescription.NonPremultiplied);

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
    /// with no image loaded). Ensures _pendingLearnBackbuffer is set so the backbuffer-learning
    /// cascade fires, and resets per-frame injection flags so DrawIndexedDetour can fire.
    /// </summary>
    public void PrepareHooks(ScreenDefinition screen)
    {
        if (!_initialized) return;

        // Last-RTV bb identification: the Dalamud ImGui bind is the LAST no-DSV
        // OMSetRenderTargets in the frame. Check it now (start of next frame) to
        // learn the backbuffer texture. This is more reliable than _pendingLearnBackbuffer
        // which fires too early and captures FFXIV post-processing surfaces.
        if (_lastNoDsvRtvPtr != 0 && _knownBackbufferTexturePtrs.Count == 0)
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
                        _knownBackbufferRtvPtrs.Add(rtvPtr);
                        Plugin.Log.Info($"[FFXIV-TV] bb learned (last-RTV): rtv=0x{rtvPtr:X} tex=0x{texPtr:X}");
                    }
                }
                finally { v.Dispose(); }
            }
            catch (Exception ex) { Plugin.Log.Warning($"[FFXIV-TV] last-RTV bb-learn failed: {ex.Message}"); }
        }

        _lastNoDsvRtvPtr         = 0; // reset — will be updated to the last no-DSV RTV this frame
        _pendingLearnBackbuffer  = true;
        _inUiPass                = false;
        _frameInjectionDone      = false;
        _bbBindCountThisUiPass   = 0;
        _currentBbRtvPtr         = 0;
        _lastClearedUiPassRtvPtr = 0;
        _sceneDrawnThisFrame     = false;
        _prevSceneRendered       = false;
        // Reset MainSceneRTV each frame so it re-converges to the LAST qualifying RTV
        // (PyonPix AutoSetRTV equivalent — ensures we track pipeline changes across frames).
        _mainSceneRtvPtr         = 0;

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
        // Periodic heartbeat: frame 1, 60, 300, then every 600 (~10s at 60fps).
        if (_prepareHooksCallCount == 1 || _prepareHooksCallCount == 60 || _prepareHooksCallCount == 300
            || _prepareHooksCallCount % 600 == 0)
        {
            Plugin.Log.Info($"[FFXIV-TV] Heartbeat #{_prepareHooksCallCount}: " +
                $"bbTex={_knownBackbufferTexturePtrs.Count} bbRtv={_knownBackbufferRtvPtrs.Count} " +
                $"cbkFrames={_cbkFrameCount} storedScreen={_storedScreen != null} " +
                $"inUiPass={_inUiPass} currentBbRtv=0x{_currentBbRtvPtr:X} " +
                $"init={_initialized} dsRZ={_dsReverseZ != null} dsRZW={_dsReverseZWrite != null} dsND={_dsNoDepth != null} cb={_cbParams != null}");
        }
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

        // PrepareHooks handles all per-frame resets + backbuffer learning signal.
        // Called here so Draw() remains self-contained (Plugin.cs also calls PrepareHooks
        // unconditionally before the DrawPlaceholder/Draw branch, so this is a harmless
        // double-call — same values, idempotent).
        PrepareHooks(screen);
    }

    // Draws the idle gradient screensaver quad (depth-tested).
    public void DrawBlack(ScreenDefinition screen)
    {
        if (!_initialized || _context == null) return;
        var srv = _gradientSrv ?? _blackSrv;
        if (srv == null) return;

        UpdateGradientTexture();
        _activeSrv = srv;

        // PrepareHooks handles per-frame resets + backbuffer learning signal.
        PrepareHooks(screen);
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

        const float s = 0.40f, v = 0.72f;
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
            HdrScale        = HdrScale,
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
        _compositeInputRtv?.Dispose(); _compositeInputRtv  = null;
        _compositeInputTexPtr = 0;
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
        _drawHook?.Dispose();            _drawHook         = null;
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
        _ps?.Dispose();           _ps           = null;
        _vs?.Dispose();           _vs           = null;
        _cbParams?.Dispose();     _cbParams     = null;
    }
}
