using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using CSDevice = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device;
using CSRtm = FFXIVClientStructs.FFXIV.Client.Graphics.Render.RenderTargetManager;
using CSAtkMgr = FFXIVClientStructs.FFXIV.Client.UI.RaptureAtkUnitManager;
using CSSceneCameraMgr = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager;

namespace FFXIVTv;

/// <summary>
/// XMP-style renderer: zero game render hooks. Runs entirely at UiBuilder.Draw time.
///
/// Every frame:
///   1. CopyResource the game's depth-stencil into a plugin-owned texture (SRV bind).
///   2. Fullscreen-triangle draw into a plugin-owned offscreen RTV.
///        VS emits a screen-covering triangle from SV_VertexID.
///        PS reconstructs a world-space camera ray per pixel from InvViewProj,
///        intersects with the TV plane (3 world corners TL/TR/BL), samples the video
///        texture, and does a 5x5 Gaussian PCF depth compare against the depth SRV.
///   3. Blits the offscreen SRV to the background draw list via ImGui.AddImage.
///
/// Portability contract:
///   - No vtable hooks. No pattern-matched inject moment. No colorspace guesses.
///   - Only uses stable Device / RenderTargetManager struct offsets.
///   - Plugin owns every resource involved in the final blit — the output surface
///     is under our control end-to-end, so nothing about the game's tonemap /
///     bloom / HDR pipeline can distort the result.
///
/// Trade-off vs the CF-DI D3DRenderer hook path: two full-screen CopyResources per
/// frame (~66 MB at 4K, ~4 GB/s at 60fps), fill-rate covers whole screen even when
/// the TV is tiny. In exchange: renders identically on every user's machine.
/// </summary>
public sealed unsafe class CopyBlitRenderer : IDisposable
{
    private ID3D11Device?        _device;
    private ID3D11DeviceContext? _context;

    // ── Depth capture ─────────────────────────────────────────────────────────
    private ID3D11Texture2D?          _depthCopyTex;
    private ID3D11ShaderResourceView? _depthCopySrv;
    private uint  _depthCopyW;
    private uint  _depthCopyH;
    private Format _depthCopyFormat = Format.Unknown;

    // ── Back-buffer capture (for UI restore) ─────────────────────────────────
    // Snapshot the game's current back buffer BEFORE we draw our video. After the
    // video blit, we sample this back for each visible native FFXIV addon rect so
    // the game HUD renders on top of the TV rather than being covered by it.
    // Direct port of XMP's UILayerCapture pattern.
    private ID3D11Texture2D?          _bbCopyTex;
    private ID3D11ShaderResourceView? _bbCopySrv;
    private uint   _bbCopyW;
    private uint   _bbCopyH;
    private Format _bbCopyFormat = Format.Unknown;
    public int  BackBufferCaptureCount { get; private set; }
    public int  UiRestoreAddonCount    { get; private set; }

    // IGameGui is used to enumerate visible native ATK addons for the UI restore.
    private IGameGui? _gameGui;
    public void SetGameGui(IGameGui gui) => _gameGui = gui;

    // ── Offscreen composite target ────────────────────────────────────────────
    private ID3D11Texture2D?          _offscreenTex;
    private ID3D11RenderTargetView?   _offscreenRtv;
    private ID3D11ShaderResourceView? _offscreenSrv;
    private uint _offscreenW;
    private uint _offscreenH;

    // ── Shaders + state ───────────────────────────────────────────────────────
    private ID3D11VertexShader?      _vs;
    private ID3D11PixelShader?       _ps;
    private ID3D11Buffer?            _cbuffer;
    private ID3D11SamplerState?      _videoSampler;
    private ID3D11SamplerState?      _depthSampler;
    private ID3D11BlendState?        _blendPremul;
    private ID3D11DepthStencilState? _dsNoDepth;
    private ID3D11RasterizerState?   _raster;

    // ── State ────────────────────────────────────────────────────────────────
    private bool _initialized;
    private bool _shadersReady;
    public bool IsAvailable => _initialized && _shadersReady;

    public int  FrameCount        { get; private set; }
    public int  DepthCaptureCount { get; private set; }
    public int  BlitCount         { get; private set; }
    public string LastError       { get; private set; } = string.Empty;
    public string LastDepthFmt    { get; private set; } = "none";
    public nint LastDepthTexPtr   { get; private set; }
    public (int W, int H) LastViewport { get; private set; }

    private VideoPlayer?   _videoPlayer;
    public void SetVideoPlayer(VideoPlayer? vp) => _videoPlayer = vp;

    // Public accessor for wiring VideoPlayer.SetDevice on first frame.
    public ID3D11Device? Device => _device;

    // Constant buffer layout — mirrors the HLSL cbuffer below.
    // XMP-style: camera basis + FoV + world corners so the PS can do ray-plane
    // intersection per pixel (fullscreen triangle path).
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct CbData
    {
        public Matrix4x4 ViewProj;       // 64 B — used only for the depth-compare hit-depth calc
        public Vector4   CameraPos;      // 16 B — xyz = camera origin in world space
        public Vector4   CameraRight;    // 16 B — camera basis
        public Vector4   CameraUp;       // 16 B
        public Vector4   CameraForward;  // 16 B
        public Vector4   CornerTL;       // 16 B — TV plane world corners
        public Vector4   CornerTR;       // 16 B
        public Vector4   CornerBL;       // 16 B
        public Vector4   FovAspect;      // 16 B — x=fovY (radians), y=aspect, z=nearPlane, w=farPlane
        public Vector4   ViewportSize;   // 16 B — xy=w,h  zw=1/w,1/h
        public Vector4   Tint;           // 16 B
        public Vector4   Options;        // 16 B — x=brightness y=gamma z=contrast w=depthEnable
        public Vector4   Options2;       // 16 B — x=hasVideo y=hasDepth
        // Total = 64 + 12*16 = 256 B
    }

    public CopyBlitRenderer() { }

    // ── Init ─────────────────────────────────────────────────────────────────
    public bool TryInitialize()
    {
        if (_initialized) return _shadersReady;
        try
        {
            var kernelDevice = CSDevice.Instance();
            if (kernelDevice == null) return false;
            nint ctxPtr = (nint)kernelDevice->D3D11DeviceContext;
            if (ctxPtr == 0) return false;
            Marshal.AddRef(ctxPtr);
            _context = new ID3D11DeviceContext(ctxPtr);
            _device  = _context.Device;

            CreateShaders();
            CreateState();
            CreateConstantBuffer();

            _initialized  = true;
            _shadersReady = _vs != null && _ps != null;
            Plugin.Log.Info(
                $"[FFXIV-TV] CopyBlitRenderer init: shadersReady={_shadersReady}");
            return _shadersReady;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Plugin.Log.Warning($"[FFXIV-TV] CopyBlitRenderer init failed: {ex.Message}");
            _initialized = false;
            return false;
        }
    }

    private void CreateShaders()
    {
        var vsBytecode = Compiler.Compile(ShaderCode, "VS", "copyblit_vs", "vs_5_0");
        _vs = _device!.CreateVertexShader(vsBytecode.Span);
        var psBytecode = Compiler.Compile(ShaderCode, "PS", "copyblit_ps", "ps_5_0");
        _ps = _device!.CreatePixelShader(psBytecode.Span);
    }

    private void CreateState()
    {
        _videoSampler = _device!.CreateSamplerState(new SamplerDescription
        {
            Filter         = Filter.MinMagMipLinear,
            AddressU       = TextureAddressMode.Clamp,
            AddressV       = TextureAddressMode.Clamp,
            AddressW       = TextureAddressMode.Clamp,
            MipLODBias     = 0,
            MaxAnisotropy  = 1,
            ComparisonFunc = ComparisonFunction.Never,
            MinLOD         = 0,
            MaxLOD         = float.MaxValue,
        });

        _depthSampler = _device.CreateSamplerState(new SamplerDescription
        {
            Filter         = Filter.MinMagMipPoint,
            AddressU       = TextureAddressMode.Clamp,
            AddressV       = TextureAddressMode.Clamp,
            AddressW       = TextureAddressMode.Clamp,
            MipLODBias     = 0,
            MaxAnisotropy  = 1,
            ComparisonFunc = ComparisonFunction.Never,
            MinLOD         = 0,
            MaxLOD         = float.MaxValue,
        });

        // Premultiplied-alpha style blend so per-pixel PS-emitted alpha controls the
        // transparent regions of the offscreen composite (the "not on the TV plane" pixels).
        // We render into an initially-cleared-to-transparent RTV.
        var blendDesc = new BlendDescription();
        blendDesc.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable           = true,
            SourceBlend           = Blend.One,
            DestinationBlend      = Blend.InverseSourceAlpha,
            BlendOperation        = BlendOperation.Add,
            SourceBlendAlpha      = Blend.One,
            DestinationBlendAlpha = Blend.InverseSourceAlpha,
            BlendOperationAlpha   = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All,
        };
        _blendPremul = _device.CreateBlendState(blendDesc);

        _dsNoDepth = _device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable   = false,
            StencilEnable = false,
        });

        _raster = _device.CreateRasterizerState(new RasterizerDescription
        {
            CullMode              = CullMode.None,
            FillMode              = FillMode.Solid,
            FrontCounterClockwise = false,
            DepthBias             = 0,
            DepthBiasClamp        = 0,
            SlopeScaledDepthBias  = 0,
            DepthClipEnable       = false,
            ScissorEnable         = false,
            MultisampleEnable     = false,
            AntialiasedLineEnable = false,
        });
    }

    private void CreateConstantBuffer()
    {
        _cbuffer = _device!.CreateBuffer(new BufferDescription
        {
            ByteWidth      = (uint)Marshal.SizeOf<CbData>(),
            Usage          = ResourceUsage.Dynamic,
            BindFlags      = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
        });
    }

    // ── Frame ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Draws one frame of the XMP-style pipeline. Called from Plugin.OnDraw.
    /// Content selection: uses VideoPlayer.FrameSrv when a video is playing, otherwise
    /// falls back to the placeholder gradient in the shader.
    /// </summary>
    public void Draw(Configuration config)
    {
        FrameCount++;
        if (!IsAvailable || _device == null || _context == null) return;

        var screen = config.Screen;
        if (!screen.Visible) return;

        // 1. Determine viewport size from ImGui's swapchain surface.
        var io = ImGui.GetIO();
        int vpW = (int)io.DisplaySize.X;
        int vpH = (int)io.DisplaySize.Y;
        if (vpW <= 0 || vpH <= 0) return;
        LastViewport = (vpW, vpH);

        // 2. Upload the latest decoded video frame (if any).
        //    UploadFrame is a no-op unless VideoPlayer has a dirty frame ready.
        _videoPlayer?.UploadFrame(_context);
        nint videoSrvPtr = _videoPlayer?.FrameSrv?.NativePointer ?? nint.Zero;

        // SAFETY: if there's no video texture, do NOT run the pipeline at all.
        // The shader also discards when Options2.x < 0.5, but skipping here means
        // we don't even blit the offscreen — nothing appears on the user's screen.
        if (videoSrvPtr == nint.Zero) return;

        // 3. Ensure offscreen composite RTV matches viewport size.
        if (!EnsureOffscreen((uint)vpW, (uint)vpH)) return;

        // 4. Snapshot the current back buffer BEFORE we draw anything. The back buffer
        //    already contains the composed game scene + native FFXIV UI. We'll sample
        //    from this after the video blit to restore UI regions on top.
        bool hasBb = TryCaptureBackBuffer();
        if (hasBb) BackBufferCaptureCount++;

        // 5. Capture the game's depth buffer via CopyResource.
        //    (Silent no-depth fallback if capture fails — shader gates on Options2.y.)
        bool hasDepth = TryCaptureGameDepth();
        if (hasDepth) DepthCaptureCount++;

        // 6. Update constant buffer.
        UpdateCbuffer(config, screen, vpW, vpH, videoSrvPtr != 0, hasDepth);

        // 7. Bind + issue the vertex-quad draw.
        RunComposite(videoSrvPtr, hasDepth, vpW, vpH);

        // 8. Blit the offscreen SRV to the ImGui background draw list.
        //    AddImageQuad is used instead of AddImage because Dalamud.Bindings.ImGui's
        //    AddImage takes an ImTextureID (not nint), while AddImageQuad accepts nint
        //    directly. Identity-mapping the four corners to the viewport rect yields the
        //    same 1:1 blit.
        if (_offscreenSrv != null)
        {
            BlitCount++;
            var dl = ImGui.GetBackgroundDrawList();
            var texId = new ImTextureID(_offscreenSrv.NativePointer);
            var tl = new Vector2(0f, 0f);
            var tr = new Vector2(vpW, 0f);
            var br = new Vector2(vpW, vpH);
            var bl = new Vector2(0f, vpH);
            var uv0 = new Vector2(0f, 0f);
            var uv1 = new Vector2(1f, 0f);
            var uv2 = new Vector2(1f, 1f);
            var uv3 = new Vector2(0f, 1f);
            dl.AddImageQuad(texId, tl, tr, br, bl, uv0, uv1, uv2, uv3, 0xFFFFFFFF);
        }

        // 9. Restore native FFXIV UI addons on top of the video. Each visible addon's
        //    screen-space rect is sampled from the pre-video back-buffer snapshot and
        //    blitted back via a normal ImGui.AddImage — the addon pixels end up in
        //    front of the video, matching XMP's UILayerCapture pattern.
        if (hasBb) RestoreUiAddons(vpW, vpH);
    }

    private bool EnsureOffscreen(uint w, uint h)
    {
        if (_offscreenTex != null && _offscreenW == w && _offscreenH == h) return true;

        _offscreenSrv?.Dispose(); _offscreenSrv = null;
        _offscreenRtv?.Dispose(); _offscreenRtv = null;
        _offscreenTex?.Dispose(); _offscreenTex = null;

        try
        {
            var desc = new Texture2DDescription
            {
                Width             = w,
                Height            = h,
                MipLevels         = 1,
                ArraySize         = 1,
                Format            = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage             = ResourceUsage.Default,
                BindFlags         = BindFlags.RenderTarget | BindFlags.ShaderResource,
                CPUAccessFlags    = CpuAccessFlags.None,
                MiscFlags         = ResourceOptionFlags.None,
            };
            _offscreenTex = _device!.CreateTexture2D(desc);
            _offscreenRtv = _device.CreateRenderTargetView(_offscreenTex);
            _offscreenSrv = _device.CreateShaderResourceView(_offscreenTex);
            _offscreenW   = w;
            _offscreenH   = h;
            Plugin.Log.Info($"[FFXIV-TV] CopyBlit: offscreen RTV {w}x{h}");
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"Offscreen create failed: {ex.Message}";
            Plugin.Log.Warning($"[FFXIV-TV] CopyBlit: offscreen create failed: {ex.Message}");
            return false;
        }
    }

    private bool TryCaptureGameDepth()
    {
        try
        {
            var rtm = CSRtm.Instance();
            if (rtm == null) return false;
            var depthTex = rtm->DepthStencil;
            if (depthTex == null) return false;

            nint srcTexPtr = (nint)depthTex->D3D11Texture2D;
            if (srcTexPtr == 0) return false;

            LastDepthTexPtr = srcTexPtr;

            Marshal.AddRef(srcTexPtr);
            using var srcTex2D = new ID3D11Texture2D(srcTexPtr);
            var srcDesc = srcTex2D.Description;

            // Recreate copy texture if size or format changed.
            if (_depthCopyTex == null || _depthCopyW != srcDesc.Width ||
                _depthCopyH != srcDesc.Height || _depthCopyFormat != srcDesc.Format)
            {
                _depthCopySrv?.Dispose(); _depthCopySrv = null;
                _depthCopyTex?.Dispose(); _depthCopyTex = null;

                var copyDesc = new Texture2DDescription
                {
                    Width             = srcDesc.Width,
                    Height            = srcDesc.Height,
                    MipLevels         = 1,
                    ArraySize         = 1,
                    // Preserve the typeless format so both DSV and SRV can be created.
                    Format            = srcDesc.Format,
                    SampleDescription = srcDesc.SampleDescription,
                    Usage             = ResourceUsage.Default,
                    BindFlags         = BindFlags.DepthStencil | BindFlags.ShaderResource,
                    CPUAccessFlags    = CpuAccessFlags.None,
                    MiscFlags         = ResourceOptionFlags.None,
                };
                _depthCopyTex = _device!.CreateTexture2D(copyDesc);

                // Pick an SRV-compatible view format for the depth channel.
                Format srvFormat = srcDesc.Format switch
                {
                    Format.R24G8_Typeless    => Format.R24_UNorm_X8_Typeless,
                    Format.D24_UNorm_S8_UInt => Format.R24_UNorm_X8_Typeless,
                    Format.R32_Typeless      => Format.R32_Float,
                    Format.D32_Float         => Format.R32_Float,
                    Format.R32G8X24_Typeless => Format.R32_Float_X8X24_Typeless,
                    _                        => srcDesc.Format,
                };

                _depthCopySrv = _device!.CreateShaderResourceView(_depthCopyTex,
                    new ShaderResourceViewDescription
                    {
                        Format        = srvFormat,
                        ViewDimension = ShaderResourceViewDimension.Texture2D,
                        Texture2D = new Texture2DShaderResourceView
                        {
                            MipLevels       = 1,
                            MostDetailedMip = 0,
                        },
                    });

                _depthCopyW      = srcDesc.Width;
                _depthCopyH      = srcDesc.Height;
                _depthCopyFormat = srcDesc.Format;
                LastDepthFmt     = srcDesc.Format.ToString();
                Plugin.Log.Info($"[FFXIV-TV] CopyBlit: depth copy {srcDesc.Width}x{srcDesc.Height} fmt={srcDesc.Format} srvFmt={srvFormat}");
            }

            // CopyResource — same size/format guaranteed by the check above.
            _context!.CopyResource(_depthCopyTex, srcTex2D);
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"Depth capture failed: {ex.Message}";
            // Don't spam the log — capture failures are per-frame.
            return false;
        }
    }

    private bool TryCaptureBackBuffer()
    {
        try
        {
            var device = CSDevice.Instance();
            if (device == null) return false;
            var sc = device->SwapChain;
            if (sc == null) return false;
            var bbTex = sc->BackBuffer;
            if (bbTex == null) return false;
            nint srcPtr = (nint)bbTex->D3D11Texture2D;
            if (srcPtr == 0) return false;

            Marshal.AddRef(srcPtr);
            using var srcTex2D = new ID3D11Texture2D(srcPtr);
            var srcDesc = srcTex2D.Description;

            if (_bbCopyTex == null || _bbCopyW != srcDesc.Width ||
                _bbCopyH != srcDesc.Height || _bbCopyFormat != srcDesc.Format)
            {
                _bbCopySrv?.Dispose(); _bbCopySrv = null;
                _bbCopyTex?.Dispose(); _bbCopyTex = null;

                var copyDesc = new Texture2DDescription
                {
                    Width             = srcDesc.Width,
                    Height            = srcDesc.Height,
                    MipLevels         = 1,
                    ArraySize         = 1,
                    Format            = srcDesc.Format,
                    SampleDescription = srcDesc.SampleDescription,
                    Usage             = ResourceUsage.Default,
                    BindFlags         = BindFlags.ShaderResource,
                    CPUAccessFlags    = CpuAccessFlags.None,
                    MiscFlags         = ResourceOptionFlags.None,
                };
                _bbCopyTex    = _device!.CreateTexture2D(copyDesc);
                _bbCopySrv    = _device.CreateShaderResourceView(_bbCopyTex);
                _bbCopyW      = srcDesc.Width;
                _bbCopyH      = srcDesc.Height;
                _bbCopyFormat = srcDesc.Format;
                Plugin.Log.Info($"[FFXIV-TV] CopyBlit: bb copy {srcDesc.Width}x{srcDesc.Height} fmt={srcDesc.Format}");
            }

            _context!.CopyResource(_bbCopyTex, srcTex2D);
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"BB capture failed: {ex.Message}";
            return false;
        }
    }

    private unsafe void RestoreUiAddons(int vpW, int vpH)
    {
        if (_bbCopySrv == null) return;

        var mgr = CSAtkMgr.Instance();
        if (mgr == null) return;

        var dl    = ImGui.GetBackgroundDrawList();
        var texId = new ImTextureID(_bbCopySrv.NativePointer);
        int restored = 0;

        // Enumerate every currently loaded ATK unit. AllLoadedUnitsList holds up
        // to 256 pointers with a Count field; iterating this list catches every
        // addon (HotBars, ChatLog + panels, Inventory, Map, Journal, etc.) without
        // us maintaining a name list that goes stale each patch.
        ref var list = ref mgr->AllLoadedUnitsList;
        int count    = list.Count;
        for (int i = 0; i < count; i++)
        {
            var addon = list.Entries[i].Value;
            if (addon == null || !addon->IsVisible) continue;
            var root = addon->RootNode;
            if (root == null) continue;

            float scale = addon->Scale;
            if (scale <= 0f) scale = 1f;

            int aw = (int)MathF.Ceiling(root->Width  * scale);
            int ah = (int)MathF.Ceiling(root->Height * scale);
            if (aw <= 0 || ah <= 0) continue;

            // Pad 1px each side so anti-aliased edges of the addon are covered.
            int x0 = Math.Max((int)addon->X - 1, 0);
            int y0 = Math.Max((int)addon->Y - 1, 0);
            int x1 = Math.Min((int)addon->X + aw + 1, vpW);
            int y1 = Math.Min((int)addon->Y + ah + 1, vpH);
            if (x1 <= x0 || y1 <= y0) continue;

            var pMin  = new Vector2(x0, y0);
            var pMax  = new Vector2(x1, y1);
            var uvMin = new Vector2((float)x0 / vpW, (float)y0 / vpH);
            var uvMax = new Vector2((float)x1 / vpW, (float)y1 / vpH);

            dl.AddImageQuad(texId,
                pMin, new Vector2(x1, y0), pMax, new Vector2(x0, y1),
                uvMin, new Vector2(uvMax.X, uvMin.Y), uvMax, new Vector2(uvMin.X, uvMax.Y),
                0xFFFFFFFF);
            restored++;
        }
        UiRestoreAddonCount = restored;
    }

    private void UpdateCbuffer(Configuration config, ScreenDefinition screen,
        int vpW, int vpH, bool hasVideo, bool hasDepth)
    {
        // Read camera state directly from Render.Camera — this is exactly what XMP
        // does (Plugin.cs:2925-2978 in their repo). No matrix inversion is required
        // for the ray reconstruction because we have the basis vectors + FoV as
        // discrete fields on the camera struct.
        Matrix4x4 viewProj  = Matrix4x4.Identity;
        Vector3 camPos      = Vector3.Zero;
        Vector3 camRight    = Vector3.UnitX;
        Vector3 camUp       = Vector3.UnitY;
        Vector3 camForward  = -Vector3.UnitZ;
        float   fovY        = MathF.PI * 0.25f;
        float   aspect      = (float)vpW / Math.Max(1, vpH);
        float   nearPlane   = 0.1f;
        float   farPlane    = 1000f;

        var sceneMgr = CSSceneCameraMgr.Instance();
        if (sceneMgr != null)
        {
            var sceneCam = sceneMgr->CurrentCamera;
            if (sceneCam != null)
            {
                var rc = sceneCam->RenderCamera;
                if (rc != null)
                {
                    var view = rc->ViewMatrix;
                    var proj = rc->ProjectionMatrix;
                    viewProj  = view * proj;
                    camPos    = rc->Origin;
                    fovY      = rc->FoV;
                    aspect    = rc->AspectRatio;
                    nearPlane = rc->NearPlane;
                    farPlane  = rc->FarPlane;

                    // XMP's approach: derive camera basis from the inverse view matrix.
                    // Rows [0], [1], [2] of invView are camera-space X, Y, Z axes in world.
                    if (Matrix4x4.Invert(view, out var invView))
                    {
                        camRight   = new Vector3(invView.M11, invView.M12, invView.M13);
                        camUp      = new Vector3(invView.M21, invView.M22, invView.M23);
                        camForward = new Vector3(invView.M31, invView.M32, invView.M33);
                    }
                }
            }
        }

        // TV plane world corners from the full TRS matrix — honors yaw/pitch/roll.
        var st = screen.ComputeScreenTransform();
        var tl = Vector3.Transform(new Vector3(-0.5f,  0.5f, 0f), st);
        var tr = Vector3.Transform(new Vector3( 0.5f,  0.5f, 0f), st);
        var bl = Vector3.Transform(new Vector3(-0.5f, -0.5f, 0f), st);

        var cb = new CbData
        {
            ViewProj      = viewProj,
            CameraPos     = new Vector4(camPos,     0f),
            CameraRight   = new Vector4(camRight,   0f),
            CameraUp      = new Vector4(camUp,      0f),
            CameraForward = new Vector4(camForward, 0f),
            CornerTL      = new Vector4(tl, 0f),
            CornerTR      = new Vector4(tr, 0f),
            CornerBL      = new Vector4(bl, 0f),
            FovAspect     = new Vector4(fovY, aspect, nearPlane, farPlane),
            ViewportSize  = new Vector4(vpW, vpH, 1f / vpW, 1f / vpH),
            Tint          = new Vector4(config.TintR, config.TintG, config.TintB, config.TintA),
            Options       = new Vector4(config.Brightness, config.Gamma, config.Contrast, hasDepth ? 1f : 0f),
            Options2      = new Vector4(hasVideo ? 1f : 0f, hasDepth ? 1f : 0f, 0f, 0f),
        };

        var mapped = _context!.Map(_cbuffer!, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            Marshal.StructureToPtr(cb, mapped.DataPointer, false);
        }
        finally { _context.Unmap(_cbuffer!, 0); }
    }

    private void RunComposite(nint videoSrvPtr, bool hasDepth, int vpW, int vpH)
    {
        var ctx = _context!;

        // Clear the offscreen surface to transparent every frame — we blit it as
        // an image, so anywhere the shader discards or emits alpha=0 must be see-through.
        var clearColor = new Color4(0f, 0f, 0f, 0f);
        ctx.ClearRenderTargetView(_offscreenRtv!, clearColor);

        // Rasterizer / OM state.
        ctx.RSSetViewport(0, 0, vpW, vpH, 0, 1);
        ctx.RSSetState(_raster);
        ctx.OMSetRenderTargets(new[] { _offscreenRtv! }, null);
        ctx.OMSetDepthStencilState(_dsNoDepth);
        ctx.OMSetBlendState(_blendPremul, new Color4(0, 0, 0, 0), 0xFFFFFFFF);

        // Fullscreen triangle — no IB, no VB, no IL.
        ctx.IASetInputLayout(null);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.IASetVertexBuffer(0, null, 0, 0);
        ctx.IASetIndexBuffer(null, Format.Unknown, 0);

        // Shaders + resources.
        ctx.VSSetShader(_vs);
        ctx.VSSetConstantBuffer(0, _cbuffer);

        ctx.PSSetShader(_ps);
        ctx.PSSetConstantBuffer(0, _cbuffer);
        ctx.PSSetSampler(0, _videoSampler);
        ctx.PSSetSampler(1, _depthSampler);

        // t0 = video texture (AddRef so we don't take ownership of the caller's SRV).
        if (videoSrvPtr != 0)
        {
            Marshal.AddRef(videoSrvPtr);
            using var videoSrv = new ID3D11ShaderResourceView(videoSrvPtr);
            ctx.PSSetShaderResource(0, videoSrv);
        }
        else
        {
            ctx.PSSetShaderResource(0, (ID3D11ShaderResourceView?)null);
        }

        // t1 = depth SRV (or null if capture failed).
        ctx.PSSetShaderResource(1, hasDepth ? _depthCopySrv : null);

        // Three vertices — SV_VertexID emits a screen-covering triangle. The PS
        // decides per-pixel whether it lies on the TV plane. Exactly XMP's pattern
        // (DepthTestedRenderer.cs:1420-1453).
        ctx.Draw(3, 0);

        // Unbind our outputs so Dalamud's ImGui pass isn't confused by leftover
        // RTV / SRV bindings. Dalamud's ImGui backend restores full state on its
        // own Present, but being a good citizen is cheap.
        ctx.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>(), null);
        ctx.PSSetShaderResource(0, (ID3D11ShaderResourceView?)null);
        ctx.PSSetShaderResource(1, (ID3D11ShaderResourceView?)null);
    }

    // ── HLSL ─────────────────────────────────────────────────────────────────
    // XivMediaPlayer's rendering: fullscreen triangle + per-pixel ray-plane
    // intersection using camera basis + FoV read from Render.Camera. Direct port
    // of XMP's DepthTestedRenderer (DepthTestedRenderer.cs:258-352 in their repo).
    // NO ViewProj inversion — the ray is built straight from CameraPos + basis + FoV.
    private const string ShaderCode = @"
cbuffer Constants : register(b0)
{
    row_major float4x4 ViewProj;   // only for computing hit-depth in the depth compare
    float4   CameraPos;
    float4   CameraRight;
    float4   CameraUp;
    float4   CameraForward;
    float4   CornerTL;             // TV plane world corners
    float4   CornerTR;
    float4   CornerBL;
    float4   FovAspect;            // x=fovY(rad) y=aspect z=near w=far
    float4   ViewportSize;         // xy=w,h  zw=1/w,1/h
    float4   Tint;
    float4   Options;              // x=brightness y=gamma z=contrast w=depthEnable
    float4   Options2;             // x=hasVideo   y=hasDepth
};

Texture2D    VideoTexture : register(t0);
Texture2D    DepthTexture : register(t1);
SamplerState VideoSampler : register(s0);
SamplerState DepthSampler : register(s1);

struct VS_OUT
{
    float4 pos : SV_Position;
    float2 uv  : TEXCOORD0;
};

// Standard SV_VertexID → fullscreen triangle trick (same as XMP).
VS_OUT VS(uint id : SV_VertexID)
{
    VS_OUT o;
    o.uv  = float2((id << 1) & 2, id & 2);
    o.pos = float4(o.uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}

float4 PS(VS_OUT input) : SV_Target
{
    // ─ Reconstruct camera ray for this pixel (XMP's exact approach) ──────
    float2 ndc = input.pos.xy * ViewportSize.zw * float2(2, -2) + float2(-1, 1);
    float  fovDist = 1.0f / tan(FovAspect.x * 0.5f);
    float3 rayDir = normalize(ndc.x * FovAspect.y * CameraRight.xyz
                            + ndc.y                * CameraUp.xyz
                            - fovDist              * CameraForward.xyz);
    float3 rayOrigin = CameraPos.xyz;

    // ─ Ray vs TV plane ───────────────────────────────────────────────────
    float3 tvRight  = CornerTR.xyz - CornerTL.xyz;
    float3 tvDown   = CornerBL.xyz - CornerTL.xyz;
    float3 tvNormal = cross(tvDown, tvRight);
    float  nlen     = length(tvNormal);
    if (nlen < 1e-6f) discard;
    tvNormal /= nlen;

    float denom = dot(tvNormal, rayDir);
    if (abs(denom) < 1e-6f) discard;
    float t = dot(CornerTL.xyz - rayOrigin, tvNormal) / denom;
    if (t < 0.0f) discard;

    float3 hit = rayOrigin + rayDir * t;
    float3 rel = hit - CornerTL.xyz;

    float rightLenSq = dot(tvRight, tvRight);
    float downLenSq  = dot(tvDown,  tvDown);
    if (rightLenSq < 1e-8f || downLenSq < 1e-8f) discard;

    float u = dot(rel, tvRight) / rightLenSq;
    float v = dot(rel, tvDown)  / downLenSq;
    if (u < 0.0f || u > 1.0f || v < 0.0f || v > 1.0f) discard;

    // ─ Depth test (5x5 Gaussian PCF against captured game depth) ─────────
    float occlusion = 0.0f;
    if (Options.w > 0.5f && Options2.y > 0.5f)
    {
        float4 hitClip = mul(float4(hit, 1.0f), ViewProj);
        if (hitClip.w > 0.0f)
        {
            float hitDepth    = hitClip.z / hitClip.w;
            float occCount    = 0.0f;
            float totalWeight = 0.0f;
            float2 texel      = ViewportSize.zw;
            [unroll] for (int dy = -2; dy <= 2; dy++)
            {
                [unroll] for (int dx = -2; dx <= 2; dx++)
                {
                    float2 sampleUV = (input.pos.xy + float2(dx * 1.5f, dy * 1.5f)) * texel;
                    float  sceneDepth = DepthTexture.SampleLevel(DepthSampler, sampleUV, 0).r;
                    float  weight     = exp(-0.3f * (dx * dx + dy * dy));
                    // Reverse-Z: sceneDepth > hitDepth => scene closer => hit occluded.
                    if (sceneDepth > hitDepth + 0.0001f) occCount += weight;
                    totalWeight += weight;
                }
            }
            occlusion = smoothstep(0.55f, 0.95f, occCount / totalWeight);
        }
    }
    if (occlusion >= 0.999f) discard;

    // ─ Sample video (no placeholder — safety) ────────────────────────────
    // If there's no video texture, discard immediately. NEVER draw a full-screen
    // placeholder fill — a broken ray-plane math combined with a placeholder gradient
    // is what black/blue-covered the user's screen previously.
    if (Options2.x < 0.5f) discard;
    float4 videoColor = VideoTexture.Sample(VideoSampler, float2(u, v));

    // ─ Post-processing ───────────────────────────────────────────────────
    videoColor.rgb *= Options.x;
    videoColor.rgb = pow(saturate(videoColor.rgb), max(Options.y, 0.01f));
    videoColor.rgb = saturate((videoColor.rgb - 0.5f) * Options.z + 0.5f);
    videoColor    *= Tint;

    videoColor.a *= (1.0f - occlusion);
    videoColor.rgb *= videoColor.a;  // premultiply
    return videoColor;
}
";

    // ── Dispose ──────────────────────────────────────────────────────────────
    public void Dispose()
    {
        _offscreenSrv?.Dispose(); _offscreenSrv = null;
        _offscreenRtv?.Dispose(); _offscreenRtv = null;
        _offscreenTex?.Dispose(); _offscreenTex = null;

        _depthCopySrv?.Dispose(); _depthCopySrv = null;
        _depthCopyTex?.Dispose(); _depthCopyTex = null;

        _bbCopySrv?.Dispose(); _bbCopySrv = null;
        _bbCopyTex?.Dispose(); _bbCopyTex = null;

        _vs?.Dispose();           _vs = null;
        _ps?.Dispose();           _ps = null;
        _cbuffer?.Dispose();      _cbuffer = null;
        _videoSampler?.Dispose(); _videoSampler = null;
        _depthSampler?.Dispose(); _depthSampler = null;
        _blendPremul?.Dispose();  _blendPremul = null;
        _dsNoDepth?.Dispose();    _dsNoDepth = null;
        _raster?.Dispose();       _raster = null;

        _context?.Dispose();      _context = null;
        // NOTE: _device is context.Device; disposing _context releases it.
        _device = null;

        _initialized  = false;
        _shadersReady = false;
    }
}
