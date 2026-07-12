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
using CSControl = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;
using CSRtm = FFXIVClientStructs.FFXIV.Client.Graphics.Render.RenderTargetManager;

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
    // Must be 16-byte aligned. Total = 208 B.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct CbData
    {
        public Matrix4x4 ViewProj;         // 64 B — camera view * projection
        public Matrix4x4 ScreenTransform;  // 64 B — TRS for the TV quad
        public Vector4   ViewportSize;     // xy=w,h  zw=1/w,1/h
        public Vector4   Tint;             // rgba
        public Vector4   Options;          // x=brightness y=gamma z=contrast w=depthEnable
        public Vector4   Options2;         // x=hasVideo y=hasDepth z=0 w=0
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
        if (_bbCopySrv == null || _gameGui == null) return;

        var dl    = ImGui.GetBackgroundDrawList();
        var texId = new ImTextureID(_bbCopySrv.NativePointer);
        int restored = 0;

        foreach (var name in NativeUiAddonNames)
        {
            var wrap = _gameGui.GetAddonByName(name);
            nint ptr = (nint)wrap;
            if (ptr == nint.Zero) continue;
            var addon = (AtkUnitBase*)ptr;
            if (addon == null || !addon->IsVisible) continue;

            short ax    = addon->X;
            short ay    = addon->Y;
            float scale = addon->Scale;
            int aw = (int)(addon->RootNode == null ? 0 : addon->RootNode->Width  * scale);
            int ah = (int)(addon->RootNode == null ? 0 : addon->RootNode->Height * scale);
            if (aw <= 0 || ah <= 0) continue;

            int x0 = Math.Max((int)ax, 0);
            int y0 = Math.Max((int)ay, 0);
            int x1 = Math.Min((int)ax + aw, vpW);
            int y1 = Math.Min((int)ay + ah, vpH);
            if (x1 <= x0 || y1 <= y0) continue;

            var pMin = new Vector2(x0, y0);
            var pMax = new Vector2(x1, y1);
            var uvMin = new Vector2((float)x0 / vpW, (float)y0 / vpH);
            var uvMax = new Vector2((float)x1 / vpW, (float)y1 / vpH);

            // Use AddImageQuad with matching corners — AddImage takes ImTextureID
            // in Dalamud's bindings but the quad variant is what other renderers use
            // and it accepts our nint-wrapped ImTextureID cleanly.
            dl.AddImageQuad(texId,
                pMin, new Vector2(x1, y0), pMax, new Vector2(x0, y1),
                uvMin, new Vector2(uvMax.X, uvMin.Y), uvMax, new Vector2(uvMin.X, uvMax.Y),
                0xFFFFFFFF);
            restored++;
        }
        UiRestoreAddonCount = restored;
    }

    // Native FFXIV addons whose screen rects we restore on top of the video.
    // Same list StatusApi.BuildHud uses — visible HUD elements only. Windows the
    // user opens (Inventory, Character, Map, etc.) are handled by their own entries.
    private static readonly string[] NativeUiAddonNames = {
        "_HotBar", "_HotBar1", "_HotBar2", "_HotBar3", "_HotBar4",
        "_HotBar5", "_HotBar6", "_HotBar7", "_HotBar8", "_HotBar9",
        "_NaviMap", "_ParameterWidget", "_PartyList",
        "_TargetInfo", "_FocusTargetInfo", "_TargetInfoMainTarget", "_TargetInfoBuffDebuff", "_TargetInfoCastBar",
        "_ChatLog", "_ChatLogPanel_0", "_ChatLogPanel_1", "_ChatLogPanel_2", "_ChatLogPanel_3",
        "_ExpBar", "_LimitBreak", "_ScenarioTree",
        "_ActionBar", "_ActionBar01", "_ActionBar02", "_ActionBar03",
        "_ActionBar04", "_ActionBar05", "_ActionBar06", "_ActionBar07", "_ActionBar08", "_ActionBar09",
        "_ActionCross", "_ActionDoubleCrossL", "_ActionDoubleCrossR", "_ActionContents",
        "_StatusCustom0", "_StatusCustom1", "_StatusCustom2", "_StatusCustom3",
        "_Status", "_StatusEnhancements", "_StatusEnfeeblements", "_StatusEnfeeblementsOther", "_StatusEnhancementsOther", "_StatusOthers",
        "_ToDoList", "_MJI",
        "Inventory", "InventoryLarge", "InventoryExpansion", "InventoryGrid", "InventoryGridCrystal",
        "InventoryRetainer", "InventoryRetainerLarge",
        "Character", "CharacterInspect", "CharacterStatus",
        "Map", "AreaMap", "GatheringMasterpiece", "GatheringPointBase",
        "RecipeNote", "RecipeTree", "Synthesis", "Gathering", "Fishing", "FishingNotes",
        "SelectYesno", "SelectString", "SelectIconString", "Talk", "TalkSubtitle",
        "SystemMenu", "ConfigCharacter", "ConfigSystem", "ConfigKeybind", "ConfigLog",
        "Buddy", "PvpProfile", "PvPTeam", "PvPTeamSetup",
        "MountNoteBook", "MinionNoteBook", "OrnamentNoteBook",
        "FateProgress", "ContentsInfo", "ContentsFinder", "ContentsFinderSetting",
        "ArmouryBoard", "GearSetList", "Currency", "MoneyString",
        "Journal", "JournalDetail", "JournalResult",
        "MonsterNote", "MonsterNoteDetail",
        "Shop", "ShopExchangeCurrency", "ShopExchangeItem", "InclusionShop",
        "Trade", "Retainer", "RetainerList", "RetainerSell", "RetainerTaskAsk",
        "BannerEditor", "BannerList", "BannerPreview",
        "AreaTitleBanner", "InstanceTitleBanner", "EnemyMouseOverIcon",
    };

    private void UpdateCbuffer(Configuration config, ScreenDefinition screen,
        int vpW, int vpH, bool hasVideo, bool hasDepth)
    {
        // Same source D3DRenderer uses. No M44 fix / inversion — the vertex-based
        // path only needs the forward transform. This matches the working D3DRenderer
        // convention at D3DRenderer.cs:520-522.
        var viewProj        = CSControl.Instance()->ViewProjectionMatrix;
        var screenTransform = screen.ComputeScreenTransform();

        var cb = new CbData
        {
            ViewProj        = viewProj,
            ScreenTransform = screenTransform,
            ViewportSize    = new Vector4(vpW, vpH, 1f / vpW, 1f / vpH),
            Tint            = new Vector4(config.TintR, config.TintG, config.TintB, config.TintA),
            Options         = new Vector4(config.Brightness, config.Gamma, config.Contrast, hasDepth ? 1f : 0f),
            Options2        = new Vector4(hasVideo ? 1f : 0f, hasDepth ? 1f : 0f, 0f, 0f),
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

        // Six vertices, generated in the VS from SV_VertexID (no VB / IB / IL needed).
        // Rasterizer only fills pixels covered by the TV quad — outside stays cleared.
        ctx.Draw(6, 0);

        // Unbind our outputs so Dalamud's ImGui pass isn't confused by leftover
        // RTV / SRV bindings. Dalamud's ImGui backend restores full state on its
        // own Present, but being a good citizen is cheap.
        ctx.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>(), null);
        ctx.PSSetShaderResource(0, (ID3D11ShaderResourceView?)null);
        ctx.PSSetShaderResource(1, (ID3D11ShaderResourceView?)null);
    }

    // ── HLSL ─────────────────────────────────────────────────────────────────
    // Vertex-based quad (D3DRenderer-style) + 5x5 Gaussian PCF depth compare (XMP-style).
    //
    // The vertex path (mul(local, ScreenTransform) → mul(world, ViewProj)) is identical
    // to the shipping D3DRenderer convention (D3DRenderer.cs:519-524). Only pixels the
    // rasterizer covers hit the PS — everywhere else the RTV keeps the transparent
    // clear color, so ImGui blits the game through unchanged.
    //
    // Ray-plane / InvViewProj was tried first (XMP-clone) but produced a fullscreen
    // artifact because inverting Control->ViewProjectionMatrix with M44=0 required a
    // hack (M44:=1) that broke the resulting inverse. Vertex path avoids the inversion
    // entirely.
    private const string ShaderCode = @"
cbuffer Constants : register(b0)
{
    row_major float4x4 ViewProj;
    row_major float4x4 ScreenTransform;
    float4   ViewportSize;   // xy = w,h    zw = 1/w, 1/h
    float4   Tint;
    float4   Options;        // x=brightness  y=gamma  z=contrast  w=depthEnable(0/1)
    float4   Options2;       // x=hasVideo    y=hasDepth
};

Texture2D    VideoTexture : register(t0);
Texture2D    DepthTexture : register(t1);
SamplerState VideoSampler : register(s0);
SamplerState DepthSampler : register(s1);

// Two triangles covering a unit quad in local XY. Same layout as D3DRenderer.cs:506-513.
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

struct VS_OUT
{
    float4 pos      : SV_Position;
    float2 uv       : TEXCOORD0;
    float3 worldPos : TEXCOORD1;
};

VS_OUT VS(uint id : SV_VertexID)
{
    float4 world = mul(float4(kPos[id], 1.0f), ScreenTransform);
    VS_OUT o;
    o.pos      = mul(world, ViewProj);
    o.uv       = kUV[id];
    o.worldPos = world.xyz;
    return o;
}

float4 PS(VS_OUT input) : SV_Target
{
    // ─ Sample video / placeholder ────────────────────────────────────────
    float4 videoColor;
    if (Options2.x > 0.5)
    {
        videoColor = VideoTexture.Sample(VideoSampler, input.uv);
    }
    else
    {
        // Placeholder gradient — visible when no video is loaded.
        videoColor = float4(0.05 + 0.5 * input.uv.x,
                            0.05 + 0.5 * input.uv.y,
                            0.15,
                            1.0);
    }

    // ─ Depth test (5x5 Gaussian PCF against the captured game depth) ─────
    float occlusion = 0.0;
    if (Options.w > 0.5 && Options2.y > 0.5)
    {
        float4 hitClip = mul(float4(input.worldPos, 1.0f), ViewProj);
        if (hitClip.w > 0.0)
        {
            float hitDepth    = hitClip.z / hitClip.w;
            float occCount    = 0.0;
            float totalWeight = 0.0;
            float2 texel      = ViewportSize.zw;
            [unroll] for (int dy = -2; dy <= 2; dy++)
            {
                [unroll] for (int dx = -2; dx <= 2; dx++)
                {
                    float2 sampleUV = (input.pos.xy + float2(dx * 1.5f, dy * 1.5f)) * texel;
                    float  sceneDepth = DepthTexture.SampleLevel(DepthSampler, sampleUV, 0).r;
                    float  weight     = exp(-0.3f * (dx * dx + dy * dy));
                    // Reverse-Z: closer to camera = higher value.
                    // sceneDepth > hitDepth → scene sample is CLOSER → hit is occluded.
                    if (sceneDepth > hitDepth + 0.0001f) occCount += weight;
                    totalWeight += weight;
                }
            }
            occlusion = smoothstep(0.55f, 0.95f, occCount / totalWeight);
        }
    }
    if (occlusion >= 0.999f) discard;

    // ─ Post-processing ───────────────────────────────────────────────────
    videoColor.rgb *= Options.x;                                            // brightness
    videoColor.rgb = pow(saturate(videoColor.rgb), max(Options.y, 0.01f));  // gamma
    videoColor.rgb = saturate((videoColor.rgb - 0.5f) * Options.z + 0.5f);  // contrast
    videoColor    *= Tint;

    videoColor.a *= (1.0f - occlusion);

    // Premultiply so the offscreen->ImGui blit blends correctly.
    videoColor.rgb *= videoColor.a;
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
