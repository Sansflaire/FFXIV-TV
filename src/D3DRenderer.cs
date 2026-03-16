using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
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

    private ID3D11VertexShader?      _vs;
    private ID3D11PixelShader?       _ps;
    private ID3D11BlendState?        _blendState;
    private ID3D11RasterizerState?   _rasterizer;
    private ID3D11DepthStencilState? _dsNoDepth;
    private ID3D11DepthStencilState? _dsReverseZ;
    private ID3D11SamplerState?      _sampler;

    // DSV tracking via OMSetRenderTargets vtable hook.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void OMSetRenderTargetsDelegate(
        nint pContext, uint numViews, nint* ppRTVs, nint pDSV);

    // Injection via ClearRenderTargetView vtable hook.
    // FFXIV clears the backbuffer before drawing native 2D UI; we inject our draw right after
    // the clear so native UI renders on top of our TV (correct depth ordering).
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private unsafe delegate void ClearRenderTargetViewDelegate(
        nint pContext, nint pRTV, float* pColorRGBA);

    private Hook<OMSetRenderTargetsDelegate>?     _omSetRTHook;
    private Hook<ClearRenderTargetViewDelegate>?  _clearRtvHook;
    private ID3D11DepthStencilView?           _trackedDsv;
    private nint                              _contextPtr;
    private bool _dsvLoggedOnce;
    private int  _cbkFrameCount;

    // Backbuffer RTV pointers learned at ImGui-time via OMGetRenderTargets.
    // FFXIV may double- or triple-buffer its swapchain, so multiple RTVs rotate per frame.
    // The hook matches any pointer in this set to find the true 2D UI injection point.
    private readonly HashSet<nint> _knownBackbufferRtvPtrs = new();
    // Set when inline draw fires; reset by Draw(). Draw() skips the fallback if true.
    private volatile bool _frameInjectionDone = false;
    private volatile bool _cachedDrawReady = false;

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
    private bool _drawPending;

    // Stored each Draw() call for use in the render-thread callback.
    private Matrix4x4        _storedViewProj;
    private ScreenDefinition? _storedScreen;

    private readonly ImDrawCallback _renderCallback;

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
        public float     _pad0;           // 4 — 16-byte alignment
        public Vector4   Tint;            // 16 — rgba multiplier (1,1,1,1 = no change)
    }

    // ── HLSL ─────────────────────────────────────────────────────────────────

    // CBUFFER_DEF is the shared cbuffer declaration used in both VS and PS.
    private const string CBUFFER_DEF = @"
cbuffer CbParams : register(b0) {
    row_major float4x4 ViewProj;
    row_major float4x4 ScreenTransform;
    float Brightness; float Gamma; float Contrast; float _pad0;
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
    return float4(color.rgb * Tint.rgb, color.a * Tint.a);
}";

    private readonly IGameInteropProvider _interop;

    // ── Constructor ───────────────────────────────────────────────────────────
    public D3DRenderer(IGameInteropProvider interop)
    {
        _interop        = interop;
        _renderCallback = new ImDrawCallback(ExecuteDrawCallback);
    }

    // ── Init ──────────────────────────────────────────────────────────────────
    public bool TryInitialize()
    {
        if (_initialized) return true;

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

        // vtable[33] = OMSetRenderTargets — for DSV tracking and backbuffer detection.
        _omSetRTHook = _interop.HookFromAddress<OMSetRenderTargetsDelegate>(
            vtable[33], OMSetRenderTargetsDetour);
        _omSetRTHook.Enable();

        // vtable[50] = ClearRenderTargetView — injection point.
        // FFXIV clears the backbuffer before 2D UI; we draw immediately after the clear
        // so native chat/hotbar/map renders on top of our TV.
        _clearRtvHook = _interop.HookFromAddress<ClearRenderTargetViewDelegate>(
            vtable[50], ClearRenderTargetViewDetour);
        _clearRtvHook.Enable();

        Plugin.Log.Info("[FFXIV-TV] OMSetRenderTargets + ClearRenderTargetView hooks installed.");
    }

    private void OMSetRenderTargetsDetour(nint pCtx, uint numViews, nint* ppRTVs, nint pDSV)
    {
        try
        {
            if (pCtx == _contextPtr)
            {
                // Track the main-scene DSV for depth testing in inline draws.
                // Only capture when both RTV and DSV are bound (main 3D scene pass).
                // Shadow passes (numViews=0, pDSV=shadowDSV) are excluded by the RTV check.
                bool thisCallHasDsv = pDSV != 0 && numViews > 0 && ppRTVs != null && ppRTVs[0] != 0;
                if (thisCallHasDsv)
                {
                    _trackedDsv?.Dispose();
                    _trackedDsv = new ID3D11DepthStencilView(pDSV);
                    _trackedDsv.AddRef();
                    if (!_dsvLoggedOnce)
                    {
                        _dsvLoggedOnce = true;
                        Plugin.Log.Info($"[FFXIV-TV] main-scene DSV captured: 0x{pDSV:X}");
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
            _omSetRTHook?.Original(pCtx, numViews, ppRTVs, pDSV);
        }
    }

    private unsafe void ClearRenderTargetViewDetour(nint pCtx, nint pRTV, float* pColorRGBA)
    {
        // Call Original FIRST — we draw on top of the clear, not before it.
        // The clear wipes the backbuffer; our TV draw fires immediately after so
        // native 2D UI (chat/hotbar/map) then renders on top of our TV.
        _clearRtvHook?.Original(pCtx, pRTV, pColorRGBA);
        try
        {
            if (pCtx == _contextPtr
                && _knownBackbufferRtvPtrs.Contains(pRTV)
                && !_frameInjectionDone
                && _initialized && _activeSrv != null && _storedScreen != null
                && _dsReverseZ != null && _dsNoDepth != null && _cbParams != null)
            {
                _frameInjectionDone = true;
                _drawPending     = false;
                _cachedDrawReady = false;
                var inlineRtv = new ID3D11RenderTargetView(pRTV);
                inlineRtv.AddRef();
                try { ExecuteInlineDraw(inlineRtv); }
                catch (Exception ex) { Plugin.Log.Warning($"[FFXIV-TV] clear-hook draw failed: {ex.Message}"); }
                finally { inlineRtv.Dispose(); }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[FFXIV-TV] ClearRenderTargetViewDetour exception: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void ExecuteInlineDraw(ID3D11RenderTargetView rtv)
    {
        if (_context == null || _activeSrv == null || _storedScreen == null) return;

        var depthState = _trackedDsv != null ? _dsReverseZ! : _dsNoDepth!;

        UpdateCbParams();

        var saved = SaveState();
        try
        {
            _context.OMSetRenderTargets(new[] { rtv }, _trackedDsv);
            SetState(_activeSrv, depthState);
            _context.Draw(6, 0);
        }
        finally
        {
            RestoreState(saved);
            _drawPending = false;
            // rtv lifetime is managed by the caller (OMSetRenderTargetsDetour) — do NOT Dispose here.
        }

        _cbkFrameCount++;
        if (_cbkFrameCount <= 3 || _cbkFrameCount % 300 == 0)
            Plugin.Log.Info($"[FFXIV-TV] inline draw frame={_cbkFrameCount} dsv={(_trackedDsv != null ? "yes" : "no")}");
    }

    private void CreateResources()
    {
        _cbParams = _device!.CreateConstantBuffer<CbParams>();

        ReadOnlyMemory<byte> vsBlob = Compiler.Compile(VS_SRC, "main", "screen_vs", "vs_5_0");
        ReadOnlyMemory<byte> psBlob = Compiler.Compile(PS_SRC, "main", "screen_ps", "ps_5_0");

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
            DepthWriteMask = DepthWriteMask.Zero,
            DepthFunc      = ComparisonFunction.Greater,
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
        if (!_initialized || _context == null || _activeSrv == null) return;

        var ctrl = Control.Instance();
        if (ctrl == null) return;

        _storedViewProj = ctrl->ViewProjectionMatrix;
        _storedScreen   = screen;

        _cachedDrawReady = true;

        // Read and reset the per-frame injection flag. If the inline draw fired at the
        // 3D→2D transition earlier this frame, skip the fallback callback — it would draw
        // AFTER native UI and cover chat/hotbar/map. Reset here so next frame starts clean.
        bool inlineFiredThisFrame = _frameInjectionDone;
        _frameInjectionDone = false;

        if (!inlineFiredThisFrame)
        {
            _drawPending = true;
            ImGui.GetBackgroundDrawList().AddCallback(_renderCallback, null);
        }
    }

    // Draws the idle gradient screensaver quad (depth-tested).
    public void DrawBlack(ScreenDefinition screen)
    {
        if (!_initialized || _context == null) return;
        var srv = _gradientSrv ?? _blackSrv;
        if (srv == null) return;

        var ctrl = Control.Instance();
        if (ctrl == null) return;

        _storedViewProj = ctrl->ViewProjectionMatrix;
        _storedScreen   = screen;

        UpdateGradientTexture();

        _activeSrv       = srv;
        _cachedDrawReady = true;

        bool inlineFiredThisFrame = _frameInjectionDone;
        _frameInjectionDone = false;

        if (!inlineFiredThisFrame)
        {
            _drawPending = true;
            ImGui.GetBackgroundDrawList().AddCallback(_renderCallback, null);
        }
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

    // ── ImGui render callback (fallback) ─────────────────────────────────────
    private void ExecuteDrawCallback(ImDrawList* parentList, ImDrawCmd* cmd)
    {
        if (!_drawPending || _activeSrv == null || _context == null || _storedScreen == null) return;
        _drawPending     = false;
        _cachedDrawReady = false;

        var depthState = _trackedDsv != null ? _dsReverseZ! : _dsNoDepth!;

        var rtvBuf = new ID3D11RenderTargetView[1];
        _context.OMGetRenderTargets(1, rtvBuf, out var currentDsv);
        var rtv = rtvBuf[0];

        // Learn the backbuffer RTV pointer. FFXIV may use multiple backbuffers (double/triple
        // buffering), so we collect all pointers seen at ImGui-time into a HashSet. The
        // ClearRTV hook matches any of these to find the true 2D UI injection point.
        if (rtv != null)
            _knownBackbufferRtvPtrs.Add(rtv.NativePointer);

        if (rtv != null)
            _context.OMSetRenderTargets(new[] { rtv }, _trackedDsv);

        UpdateCbParams();
        var saved = SaveState();
        try
        {
            SetState(_activeSrv, depthState);
            _context.Draw(6, 0);
        }
        finally
        {
            RestoreState(saved);
            if (rtv != null)
                _context.OMSetRenderTargets(new[] { rtv }, null);
            rtv?.Dispose();
            currentDsv?.Dispose();
        }
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
        DisposeResources();
        _context = null;
        _device?.Dispose();
        _device      = null;
        _initialized = false;
    }

    private void DisposeResources()
    {
        _omSetRTHook?.Disable();
        _omSetRTHook?.Dispose();   _omSetRTHook   = null;
        _clearRtvHook?.Disable();
        _clearRtvHook?.Dispose();  _clearRtvHook  = null;
        _trackedDsv?.Dispose();   _trackedDsv   = null;
        _activeSrv  = null;
        _gradientSrv?.Dispose();  _gradientSrv  = null;
        _gradientTex?.Dispose();  _gradientTex  = null;
        _blackSrv?.Dispose();     _blackSrv     = null;
        _imageSrv?.Dispose();     _imageSrv     = null;
        _loadedImagePath = string.Empty;
        _sampler?.Dispose();      _sampler      = null;
        _dsReverseZ?.Dispose();   _dsReverseZ   = null;
        _dsNoDepth?.Dispose();    _dsNoDepth    = null;
        _rasterizer?.Dispose();   _rasterizer   = null;
        _blendState?.Dispose();   _blendState   = null;
        _ps?.Dispose();           _ps           = null;
        _vs?.Dispose();           _vs           = null;
        _cbParams?.Dispose();     _cbParams     = null;
    }
}
