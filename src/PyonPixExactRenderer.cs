using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using CSDevice     = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device;
using CameraMgr    = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager;
using CSControl    = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;

namespace FFXIVTv;

/// <summary>
/// Highest-fidelity replica of priprii/PyonPix's RendererService. Every design
/// point that could conceivably diverge from upstream is copied verbatim:
///
///   • Shaders: loaded from embedded PyonPix .cso bytecode (vsmain / psmain
///     extracted from installedPlugins/PyonPix/1.1.0.1/PyonPix.dll). We DO
///     NOT compile our own HLSL — the whole point of this renderer is that
///     the shader math is theirs, byte-for-byte.
///   • Constant buffer: 288-byte ShaderParams struct with CameraView,
///     CameraProjection, ScreenTransform, ScreenTint, EdgeColour, BackColour,
///     BorderColour, BorderWidthH/V, BorderMode, BorderFeather, EdgeFeather,
///     DepthOffset + 2 float pad. Matches ref-pyonpix/ShaderParams.cs
///     exactly (same field order + same padding).
///   • Matrices: uploaded TRANSPOSED (PyonPix's HLSL is column-major default).
///     CameraView comes from SceneCamera->ViewMatrix (with M44 forced to 1),
///     CameraProjection from RenderCamera->ProjectionMatrix — SEPARATE
///     matrices, not a combined ViewProj (mirrors PyonPix's CameraService).
///   • Geometry: no vertex buffer, no input layout. ctx.Draw(36, 0). The
///     compiled shader constructs a cube-shell from SV_VertexID internally.
///   • RTV/DSV selection + resize handling + hook wiring: same as
///     PyonPixRenderer (this class copies that infra; both share the same
///     peer-portability property of converging on the correct main-scene RTV
///     via rebind-frequency scoring on any hardware config).
///
/// Coexists with PyonPixRenderer. Selected via
///   /set/rendermode?v=pyonpixexact
/// so we can A/B the from-scratch shader against the real bytecode without
/// touching the existing PyonPixRenderer code path.
/// </summary>
public sealed unsafe class PyonPixExactRenderer : IDisposable
{
    // ── Game device ───────────────────────────────────────────────────────────
    private ID3D11Device?        _device;
    private ID3D11DeviceContext? _context;
    private CSDevice*            _gameDevice;

    // ── Hook state ────────────────────────────────────────────────────────────
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void OMSetRenderTargetsDelegate(
        nint pContext, uint numViews, nint* ppRtvArray, nint pDsv);

    private Hook<OMSetRenderTargetsDelegate>? _omSetRtHook;

    private readonly IGameInteropProvider _interop;

    // ── Frame + scene tracking ────────────────────────────────────────────────
    private ulong _presentIndex;
    private ulong _lastPresentIndex;
    private ulong _lastRescoredIndex;

    [ThreadStatic] private static bool _inDetour;
    private long _detourInFlight;

    // ── Resource caches (RTV + DSV + pair counts) ────────────────────────────
    private sealed class RtvItem
    {
        public required ID3D11RenderTargetView Rtv;
        public int    Width;
        public int    Height;
        public Format Format;
        public bool   IsBound;
        public ulong  Calls;
        public ulong  LastPresent;
        public nint   TexturePtr;
    }

    private sealed class DsvItem
    {
        public required ID3D11DepthStencilView Dsv;
        public Format Format;
        public int    Width;
        public int    Height;
    }

    private readonly Dictionary<(nint rtv, nint dsv), ulong> _pairCounts = new();
    private readonly Dictionary<nint, RtvItem> _rtvCache = new();
    private readonly Dictionary<nint, DsvItem> _dsvCache = new();
    private readonly List<nint>                _rtvPtrs  = new();
    private readonly List<nint>                _dsvPtrs  = new();
    private RtvItem?                           _targetRtv;

    // ── Resize / swap-chain detection ────────────────────────────────────────
    private bool _resizeInProgress;
    private nint _lastSwapChainPtr;
    private nint _lastBackBufferPtr;

    // ── D3D11 state resources ────────────────────────────────────────────────
    private ID3D11VertexShader?      _vs;
    private ID3D11PixelShader?       _ps;
    private ID3D11Buffer?            _cbuffer;
    private ID3D11SamplerState?      _sampler;
    private ID3D11BlendState?        _blend;
    private ID3D11BlendState?        _blendOff;   // BlendEnable=false — direct RGBA write
    private ID3D11DepthStencilState? _depthState;
    private ID3D11DepthStencilState? _dsNoDepth;
    private ID3D11RasterizerState?   _rasterState;

    // ── Data plumbed in from Plugin.cs each frame ───────────────────────────
    private VideoPlayer?      _videoPlayer;
    private ScreenDefinition? _screen;
    private Configuration?    _config;
    public void SetVideoPlayer(VideoPlayer? vp) => _videoPlayer = vp;
    public void SetScreen(ScreenDefinition? s)  => _screen = s;
    public void SetConfig(Configuration? c)     => _config = c;
    public ID3D11Device? Device                 => _device;

    /// <summary>Diagnostic: skip depth test entirely (useful to verify pixel path).</summary>
    public bool DisableDepth { get; set; }

    /// <summary>When true (DEFAULT), blend is disabled — RGBA writes go directly
    /// to the target, ignoring the video texture's alpha channel. This is required
    /// because LibVLC writes video frames as BGRA with alpha=0 (undefined pad
    /// channel), so SrcAlpha/InvSrcAlpha blending would multiply everything by 0
    /// and produce an invisible TV. PyonPix's own reference plugin uses a browser
    /// (CEF) texture that DOES write alpha=1, so their default blend works there.
    /// If a future video pipeline writes real alpha, flip this to false via
    /// /set/pyonpixexact/disableblend?v=false to get proper alpha compositing.</summary>
    public bool DisableBlend { get; set; } = true;

    /// <summary>Diagnostic: force the second-pass target override to pick by
    /// insertion index instead of PyonPix's reverse-walk. -1 = default (PyonPix's
    /// reverse walk). 0..N-1 = pick RTV at _rtvPtrs[N] directly. Lets us walk
    /// through every candidate surface live to find one where the TV shows.</summary>
    public int TargetOverrideIndex { get; set; } = -1;

    /// <summary>When true (default, PyonPix's semantics), Draw only fires when
    /// the target RTV is bound WITH a cached R24G8_Typeless DSV — a main-scene
    /// pass. When false, Draw fires on ANY bind of the target RTV — needed to
    /// hit the SwapChain back buffer target which is bound WITHOUT a DSV during
    /// composite/UI passes (per decompile RenderTargetManager.SwapChainBackBuffer
    /// @ +1392 = final compose target).</summary>
    public bool RequireDsv { get; set; } = true;

    // ── Diagnostics (surfaced via StatusApi) ─────────────────────────────────
    public bool  IsAvailable       => _vs != null && _ps != null && _omSetRtHook != null;
    public int   DrawCount         { get; private set; }
    public int   OmSetRtCount      { get; private set; }
    public int   FrameCount        { get; private set; }
    public int   RtvCacheCount     => _rtvCache.Count;
    public int   DsvCacheCount     => _dsvCache.Count;
    public string LastError        { get; private set; } = string.Empty;
    public string ActiveState      { get; private set; } = "uninit";
    public nint  TargetRtvPtr      => _targetRtv?.Rtv.NativePointer ?? 0;
    public string TargetRtvFormat  => _targetRtv?.Format.ToString() ?? "none";
    public (int W, int H) TargetRtvSize => _targetRtv != null ? (_targetRtv.Width, _targetRtv.Height) : (0, 0);
    public double TargetRtvScore   { get; private set; }

    public IEnumerable<(int insertIndex, nint ptr, Format fmt, int w, int h, ulong calls, ulong lastPresent, bool isBound, double score)>
        EnumerateCachedRtvs()
    {
        for (int i = 0; i < _rtvPtrs.Count; i++)
        {
            nint ptr = _rtvPtrs[i];
            if (!_rtvCache.TryGetValue(ptr, out var r)) continue;
            yield return (i, ptr, r.Format, r.Width, r.Height, r.Calls, r.LastPresent, r.IsBound, ComputeScore(r));
        }
    }

    /// <summary>Called once per frame from Plugin.OnDraw (Present hook is forbidden — Dalamud owns it).</summary>
    public void IncrementFrameCounter()
    {
        _presentIndex++;
        FrameCount++;
    }

    // ── PyonPix's 288-byte ShaderParams cbuffer, byte-for-byte ──────────────
    // Field order and padding match ref-pyonpix/ShaderParams.cs. HLSL cbuffer
    // packing rules produce the same memory layout so the compiled shader
    // reads each field at the expected offset.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ShaderParams
    {
        public Matrix4x4 CameraView;         // offset   0
        public Matrix4x4 CameraProjection;   // offset  64
        public Matrix4x4 ScreenTransform;    // offset 128
        public Vector4   ScreenTint;         // offset 192
        public Vector4   EdgeColour;         // offset 208
        public Vector4   BackColour;         // offset 224
        public Vector4   BorderColour;       // offset 240
        public float     BorderWidthH;       // offset 256
        public float     BorderWidthV;       // offset 260
        public int       BorderMode;         // offset 264
        public float     BorderFeather;      // offset 268
        public float     EdgeFeather;        // offset 272
        public float     DepthOffset;        // offset 276
        public float     _pad1;              // offset 280
        public float     _pad2;              // offset 284
        // total 288 bytes
    }

    public PyonPixExactRenderer(IGameInteropProvider interop)
    {
        _interop = interop;
    }

    // ── Init ─────────────────────────────────────────────────────────────────
    public bool TryInitialize()
    {
        if (IsAvailable) return true;
        try
        {
            _gameDevice = CSDevice.Instance();
            if (_gameDevice == null) { ActiveState = "no game device"; return false; }

            nint ctxPtr = (nint)_gameDevice->D3D11DeviceContext;
            if (ctxPtr == 0) { ActiveState = "no context ptr"; return false; }
            Marshal.AddRef(ctxPtr);
            _context = new ID3D11DeviceContext(ctxPtr);
            _device  = _context.Device;

            LoadEmbeddedShaders();
            CreateState();
            CreateConstantBuffer();
            InstallHooks(ctxPtr);

            ActiveState = "ready";
            Plugin.Log.Info("[FFXIV-TV] PyonPixExactRenderer initialized (real PyonPix bytecode loaded).");
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            ActiveState = "init failed";
            Plugin.Log.Warning($"[FFXIV-TV] PyonPixExactRenderer init failed: {ex}");
            return false;
        }
    }

    private void LoadEmbeddedShaders()
    {
        var vs = ReadEmbedded("FFXIVTv.Shaders.vsmain.cso");
        var ps = ReadEmbedded("FFXIVTv.Shaders.psmain.cso");
        _vs = _device!.CreateVertexShader(vs);
        _ps = _device.CreatePixelShader(ps);
    }

    private static byte[] ReadEmbedded(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var s = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource missing: {name}");
        var buf = new byte[s.Length];
        int off = 0;
        while (off < buf.Length)
        {
            int n = s.Read(buf, off, buf.Length - off);
            if (n == 0) break;
            off += n;
        }
        return buf;
    }

    private void CreateState()
    {
        _sampler = _device!.CreateSamplerState(new SamplerDescription
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

        // Standard alpha blend — same shape as PyonPix's default global props.
        var blendDesc = new BlendDescription();
        blendDesc.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable           = true,
            SourceBlend           = Blend.SourceAlpha,
            DestinationBlend      = Blend.InverseSourceAlpha,
            BlendOperation        = BlendOperation.Add,
            SourceBlendAlpha      = Blend.One,
            DestinationBlendAlpha = Blend.InverseSourceAlpha,
            BlendOperationAlpha   = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All,
        };
        _blend = _device.CreateBlendState(blendDesc);

        // Diagnostic: blend disabled — RGBA writes go straight to target, ignoring
        // whatever alpha the video texture reports. Used to isolate whether "front
        // invisible" is caused by video alpha=0 (blend rejects) vs some other issue.
        var blendOffDesc = new BlendDescription();
        blendOffDesc.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable           = false,
            SourceBlend           = Blend.One,
            DestinationBlend      = Blend.Zero,
            BlendOperation        = BlendOperation.Add,
            SourceBlendAlpha      = Blend.One,
            DestinationBlendAlpha = Blend.Zero,
            BlendOperationAlpha   = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All,
        };
        _blendOff = _device.CreateBlendState(blendOffDesc);

        // Reversed-Z (FFXIV): PyonPix flips LessEqual → GreaterEqual.
        _depthState = _device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable    = true,
            DepthWriteMask = DepthWriteMask.All,
            DepthFunc      = ComparisonFunction.GreaterEqual,
            StencilEnable  = false,
        });
        _dsNoDepth = _device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable   = false,
            StencilEnable = false,
        });

        // CullMode.None — safe default when we don't know the exact winding of
        // the cube-shell that PyonPix's vsmain emits from SV_VertexID.
        _rasterState = _device.CreateRasterizerState(new RasterizerDescription
        {
            CullMode              = CullMode.None,
            FillMode              = FillMode.Solid,
            FrontCounterClockwise = false,
            DepthClipEnable       = false,
            ScissorEnable         = false,
        });
    }

    private void CreateConstantBuffer()
    {
        _cbuffer = _device!.CreateBuffer(new BufferDescription
        {
            ByteWidth      = (uint)Marshal.SizeOf<ShaderParams>(),
            Usage          = ResourceUsage.Default,
            BindFlags      = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.None,
        });
    }

    private void InstallHooks(nint ctxPtr)
    {
        var ctxVtbl = *(nint**)ctxPtr;
        var omSetRtAddr = ctxVtbl[33];
        _omSetRtHook = _interop.HookFromAddress<OMSetRenderTargetsDelegate>(
            omSetRtAddr, OMSetRtDetour);
        _omSetRtHook.Enable();
    }

    // ── OMSetRenderTargets detour ────────────────────────────────────────────
    private void OMSetRtDetour(nint pContext, uint numViews, nint* ppRtvArray, nint pDsv)
    {
        _omSetRtHook!.Original(pContext, numViews, ppRtvArray, pDsv);

        if (_inDetour) return;

        Interlocked.Increment(ref _detourInFlight);
        _inDetour = true;
        try
        {
            OmSetRtCount++;

            // Honor a pending cache reset from Plugin.OnDraw (mode switch).
            // Safe here — we're on the render thread and hold _inDetour.
            if (Interlocked.Exchange(ref _resetPending, 0) == 1)
                ClearViews();

            if (_screen == null || !_screen.Visible) return;
            if (_gameDevice == null) return;

            if (_resizeInProgress)
            {
                if (numViews > 0 && pDsv != 0)
                {
                    ClearViews();
                    _resizeInProgress = false;
                }
                return;
            }

            if (_lastSwapChainPtr != (nint)_gameDevice->SwapChain)
            {
                _lastSwapChainPtr = (nint)_gameDevice->SwapChain;
                _resizeInProgress = true;
                return;
            }
            if (_gameDevice->SwapChain == null) return;
            nint backBufferPtr = (nint)_gameDevice->SwapChain->BackBuffer;
            if (_lastBackBufferPtr != backBufferPtr)
            {
                _lastBackBufferPtr = backBufferPtr;
                _resizeInProgress = true;
                return;
            }
            if (_targetRtv != null &&
                (_targetRtv.Width != _gameDevice->Width || _targetRtv.Height != _gameDevice->Height))
            {
                _resizeInProgress = true;
                return;
            }

            if (numViews == 0) return;

            nint curDsvPtr = pDsv;
            bool isCurRtv = false;

            for (int i = 0; i < numViews; i++)
            {
                nint rtvPtr = ppRtvArray[i];
                if (rtvPtr == 0) continue;

                if (curDsvPtr != 0)
                {
                    var key = (rtvPtr, curDsvPtr);
                    if (!_pairCounts.ContainsKey(key)) _pairCounts[key] = 0;
                    _pairCounts[key]++;
                }

                if (TryCreateRtvItem(rtvPtr))
                {
                    var selected = SelectBestRtv();
                    if (selected != null) _targetRtv = selected;
                }

                if (_rtvCache.TryGetValue(rtvPtr, out var rtv))
                {
                    rtv.LastPresent = _presentIndex;
                    rtv.Calls++;
                }

                if (_targetRtv?.Rtv.NativePointer == rtvPtr) isCurRtv = true;
            }

            if (curDsvPtr != 0) TryCreateDsvItem(curDsvPtr);

            // Target selection — priority order:
            //   1. If TargetOverrideIndex >= 0: force pick RTV at that _rtvPtrs index
            //      (diagnostic; lets us walk every candidate live via curl)
            //   2. Else PyonPix's second-pass reverse-walk (RendererService.cs:480-511):
            //      pick the LAST-INSERTED RTV that has a valid RTV/DSV pair count > 0.
            //      Prefers late-inserted post-process targets over early main-scene
            //      targets. Without this, Float RTV wins and bloom over-brightens
            //      our video pixels into starburst halos.
            if (_lastPresentIndex != _presentIndex && _rtvPtrs.Count > 0 && _pairCounts.Count > 0)
            {
                if (TargetOverrideIndex >= 0 && TargetOverrideIndex < _rtvPtrs.Count)
                {
                    nint forcedPtr = _rtvPtrs[TargetOverrideIndex];
                    if (_rtvCache.TryGetValue(forcedPtr, out var forced))
                    {
                        _targetRtv = forced;
                        TargetRtvScore = ComputeScore(forced);
                        for (int j = 0; j < numViews; j++)
                        {
                            if (ppRtvArray[j] == forcedPtr) { isCurRtv = true; break; }
                        }
                    }
                }
                else
                {
                    bool found = false;
                    var pairs = _pairCounts.OrderByDescending(x => x.Value).ToList();
                    for (int i = _rtvPtrs.Count - 1; i >= 0 && !found; i--)
                    {
                        nint rtvPtr = _rtvPtrs[i];
                        foreach (var pair in pairs)
                        {
                            if (pair.Key.rtv != rtvPtr || pair.Value == 0) continue;
                            if (!_rtvCache.TryGetValue(rtvPtr, out var rtv2)) continue;
                            _targetRtv = rtv2;
                            TargetRtvScore = ComputeScore(rtv2);
                            found = true;
                            for (int j = 0; j < numViews; j++)
                            {
                                if (ppRtvArray[j] == rtvPtr) { isCurRtv = true; break; }
                            }
                            break;
                        }
                    }
                }
            }

            // PyonPix's PreDraw semantics: fire ONLY when a scene pass rebinds
            // our target RTV — that is, target RTV + a cached R24G8_Typeless DSV
            // bound in the same call. UI / composite / tonemap passes with a
            // null or non-cached DSV are NOT valid draw points; if we fire on
            // those the game's next pass overwrites our pixels or we land on
            // top of finished HUD that then gets drawn over us. This mirrors
            // ref-pyonpix/RendererService.cs:513-515:
            //   SceneRendered = DSVCache.ContainsKey(curDSVPtr) && isCurRtv;
            bool sceneRendered = isCurRtv
                && (!RequireDsv || (curDsvPtr != 0 && _dsvCache.ContainsKey(curDsvPtr)));
            if (sceneRendered && _lastPresentIndex != _presentIndex)
            {
                _lastPresentIndex = _presentIndex;
                try
                {
                    Draw(curDsvPtr);
                    DrawCount++;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    Plugin.Log.Warning($"[FFXIV-TV] PyonPixExact Draw failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Plugin.Log.Warning($"[FFXIV-TV] PyonPixExact detour: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _inDetour = false;
            Interlocked.Decrement(ref _detourInFlight);
        }
    }

    // ── Cache management ────────────────────────────────────────────────────
    private bool TryCreateRtvItem(nint rtvPtr)
    {
        if (_rtvCache.ContainsKey(rtvPtr)) return false;

        Marshal.AddRef(rtvPtr);
        ID3D11RenderTargetView rtv;
        try { rtv = new ID3D11RenderTargetView(rtvPtr); }
        catch { Marshal.Release(rtvPtr); return false; }

        try
        {
            using var res = rtv.Resource;
            using var tex = res.QueryInterface<ID3D11Texture2D>();
            var desc = tex.Description;

            if (_gameDevice->Width != desc.Width || _gameDevice->Height != desc.Height)
            {
                rtv.Dispose();
                return false;
            }
            if (!_validRtvFormats.Contains(desc.Format))
            {
                rtv.Dispose();
                return false;
            }

            var item = new RtvItem
            {
                Rtv        = rtv,
                Width      = (int)desc.Width,
                Height     = (int)desc.Height,
                Format     = desc.Format,
                IsBound    = (desc.BindFlags & BindFlags.ShaderResource) == 0,
                TexturePtr = tex.NativePointer,
            };
            _rtvCache[rtvPtr] = item;
            _rtvPtrs.Add(rtvPtr);
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"CreateRtv: {ex.Message}";
            rtv.Dispose();
            return false;
        }
    }

    private bool TryCreateDsvItem(nint dsvPtr)
    {
        if (_dsvCache.ContainsKey(dsvPtr)) return false;

        Marshal.AddRef(dsvPtr);
        ID3D11DepthStencilView dsv;
        try { dsv = new ID3D11DepthStencilView(dsvPtr); }
        catch { Marshal.Release(dsvPtr); return false; }

        try
        {
            using var res = dsv.Resource;
            using var tex = res.QueryInterface<ID3D11Texture2D>();
            var desc = tex.Description;

            if (_gameDevice->Width != desc.Width || _gameDevice->Height != desc.Height)
            {
                dsv.Dispose();
                return false;
            }
            if ((desc.BindFlags & BindFlags.ShaderResource) == 0)
            {
                dsv.Dispose();
                return false;
            }
            if (desc.Format != Format.R24G8_Typeless)
            {
                dsv.Dispose();
                return false;
            }

            _dsvCache[dsvPtr] = new DsvItem
            {
                Dsv    = dsv,
                Format = desc.Format,
                Width  = (int)desc.Width,
                Height = (int)desc.Height,
            };
            _dsvPtrs.Add(dsvPtr);
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"CreateDsv: {ex.Message}";
            dsv.Dispose();
            return false;
        }
    }

    private static readonly HashSet<Format> _validRtvFormats = new()
    {
        Format.R8G8B8A8_UNorm,   Format.R8G8B8A8_Typeless,
        Format.B8G8R8A8_UNorm,   Format.B8G8R8A8_Typeless,
        Format.R16G16B16A16_UNorm, Format.R16G16B16A16_Typeless,
        Format.R16G16B16A16_Float,
    };
    private static readonly HashSet<Format> _prefUnorm = new()
    {
        Format.R8G8B8A8_UNorm, Format.B8G8R8A8_UNorm,
    };
    private static readonly HashSet<Format> _prefTypeless = new()
    {
        Format.R8G8B8A8_Typeless, Format.B8G8R8A8_Typeless,
    };

    private double ComputeScore(RtvItem r)
    {
        double v = 0.0;
        v += Math.Sqrt(Math.Max(0, r.Calls)) * 10;
        if (r.LastPresent == _presentIndex) v += 300;
        else v += Math.Max(0, 100 - (int)(_presentIndex - r.LastPresent));

        if      (_prefUnorm.Contains(r.Format))    v += 500;
        else if (_prefTypeless.Contains(r.Format)) v += 550;
        else if (r.Format == Format.R16G16B16A16_Float) v += 450;
        else                                       v += 100;
        return v;
    }

    private RtvItem? SelectBestRtv()
    {
        if (_rtvCache.Count == 0) return null;

        // Pure PyonPix heuristic scoring — no SwapChain->BackBuffer sentinel.
        // The sentinel picks the swap chain back buffer, but that surface is
        // bound during tonemap (which overwrites our pixels) or UI passes
        // (which have no DSV, so PyonPix's DSV-required Draw gate blocks us).
        // Scoring instead converges on the HDR intermediate that scene passes
        // rebind many times per frame with a real R24G8_Typeless DSV — draw
        // there, and tonemap converts the TV to LDR + copies to back buffer.
        RtvItem? best = null;
        double bestScore = double.MinValue;
        foreach (var r in _rtvCache.Values)
        {
            double v = ComputeScore(r);
            if (v > bestScore) { bestScore = v; best = r; }
        }
        TargetRtvScore = bestScore;
        return best;
    }

    private void ClearViews()
    {
        _presentIndex = 0;
        _lastPresentIndex = 0;
        _lastRescoredIndex = 0;
        _targetRtv = null;
        TargetRtvScore = 0;

        foreach (var r in _rtvCache.Values) r.Rtv.Dispose();
        _rtvCache.Clear();
        _rtvPtrs.Clear();

        foreach (var d in _dsvCache.Values) d.Dsv.Dispose();
        _dsvCache.Clear();
        _dsvPtrs.Clear();

        _pairCounts.Clear();
    }

    /// <summary>Requests the render-thread detour to clear RTV/DSV caches at
    /// the top of its next call. Set from Plugin.OnDraw (UI thread) when the
    /// user switches back to PyonPixExact from another mode — otherwise the
    /// caches accumulate stale state during the inactive period and the
    /// reverse-walk selector converges on a wrong surface. NOT a direct
    /// clear: touching _rtvCache from the UI thread races with the detour
    /// on the render thread (Dictionary iteration + Dispose = crash).</summary>
    private int _resetPending;
    public void ResetTargeting() => Interlocked.Exchange(ref _resetPending, 1);

    // ── Draw ─────────────────────────────────────────────────────────────────
    // Mirrors ref-pyonpix/RendererService.cs::Draw + DrawRenderer:
    //   1. Save current pipeline state.
    //   2. Bind our target RTV + a cached DSV (or null → no depth test).
    //   3. Upload the ShaderParams cbuffer with transposed matrices.
    //   4. Set null input layout, TriangleList topology, no vertex/index buf.
    //   5. Bind vsmain / psmain + video SRV + sampler + cbuffer on both stages.
    //   6. ctx.Draw(36, 0). The compiled shader synthesizes 36 vertices from
    //      SV_VertexID (cube shell: 6 faces × 2 tris × 3 verts).
    //   7. Restore pipeline state.
    private void Draw(nint curDsvPtr)
    {
        if (_context == null || _device == null) return;
        if (_targetRtv == null) return;
        if (_screen == null || !_screen.Visible) return;

        _videoPlayer?.UploadFrame(_context);
        var videoSrv = _videoPlayer?.FrameSrv;

        _dsvCache.TryGetValue(curDsvPtr, out var dsvItem);
        ID3D11DepthStencilView? drawDsv = dsvItem?.Dsv;
        if (drawDsv == null && _dsvCache.Count > 0)
        {
            foreach (var d in _dsvCache.Values) { drawDsv = d.Dsv; break; }
        }
        bool haveDepth = drawDsv != null;

        var ctx = _context;

        // ── Save state ────────────────────────────────────────────────────
        var prevVps    = ctx.RSGetViewports<Viewport>().ToArray();
        var prevRs     = ctx.RSGetState();
        var prevBlend  = ctx.OMGetBlendState(out var prevBlendFactor, out uint prevSampleMask);
        ctx.OMGetDepthStencilState(out var prevDs, out uint prevStencilRef);
        var prevVs     = ctx.VSGetShader();
        var prevPs     = ctx.PSGetShader();
        var prevTopo   = ctx.IAGetPrimitiveTopology();
        var prevIL     = ctx.IAGetInputLayout();

        var vsCbs  = new ID3D11Buffer[1];             ctx.VSGetConstantBuffers(0, vsCbs);
        var psCbs  = new ID3D11Buffer[1];             ctx.PSGetConstantBuffers(0, psCbs);
        var psSrvs = new ID3D11ShaderResourceView[1]; ctx.PSGetShaderResources(0, psSrvs);
        var psSamp = new ID3D11SamplerState[1];       ctx.PSGetSamplers(0, psSamp);

        try
        {
            // ── Camera matrices ───────────────────────────────────────────
            // FFXIV's RenderCamera->ProjectionMatrix has reverse-Z encoding
            // (M33/M43 signs, M44=0 style) that PyonPix's CameraService reads
            // raw. Passing that raw matrix here yielded a "white glowing mess
            // that breaks as camera turns" — vertices projecting to unstable
            // clip positions. Using the combined ViewProjectionMatrix from
            // Control.Instance() instead (same source the working PyonPix
            // mode uses). Identity for CameraView + ViewProj in CameraProjection
            // collapses to `world * ViewProj = clip` regardless of whether the
            // shader does mul(mul(w,V),P) or mul(w, mul(V,P)).
            var camView = Matrix4x4.Identity;
            var camProj = CSControl.Instance()->ViewProjectionMatrix;
            camView = Matrix4x4.Transpose(camView);
            camProj = Matrix4x4.Transpose(camProj);
            var screenXf = Matrix4x4.Transpose(_screen.ComputeScreenTransform());

            var tintV4 = _config == null
                ? new Vector4(1f, 1f, 1f, 1f)
                : new Vector4(_config.TintR, _config.TintG, _config.TintB, _config.TintA);

            // Border widths 0 → no frame decoration; the video texture fills
            // the front face of the cube. EdgeFeather 0 → no edge softening.
            var cb = new ShaderParams
            {
                CameraView       = camView,
                CameraProjection = camProj,
                ScreenTransform  = screenXf,
                ScreenTint       = tintV4,
                EdgeColour       = new Vector4(0f, 0f, 0f, 1f),
                BackColour       = new Vector4(0f, 0f, 0f, 1f),
                BorderColour     = new Vector4(0f, 0f, 0f, 1f),
                BorderWidthH     = 0f,
                BorderWidthV     = 0f,
                BorderMode       = 0,
                BorderFeather    = 0f,
                EdgeFeather      = 0f,
                DepthOffset      = 0f,
            };
            ctx.UpdateSubresource(cb, _cbuffer!);

            ctx.OMSetRenderTargets(new[] { _targetRtv.Rtv }, drawDsv);
            ctx.RSSetViewport(new Viewport(0, 0, _targetRtv.Width, _targetRtv.Height, 0, 1));
            ctx.RSSetState(_rasterState);
            ctx.OMSetBlendState(DisableBlend ? _blendOff : _blend,
                                new Color4(0, 0, 0, 0), 0xFFFFFFFF);
            ctx.OMSetDepthStencilState(
                (!haveDepth || DisableDepth) ? _dsNoDepth : _depthState);

            ctx.IASetInputLayout(null);
            ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            ctx.IASetVertexBuffer(0, null, 0, 0);
            ctx.IASetIndexBuffer(null, Format.Unknown, 0);

            ctx.VSSetShader(_vs);
            ctx.VSSetConstantBuffer(0, _cbuffer);
            ctx.PSSetShader(_ps);
            ctx.PSSetConstantBuffer(0, _cbuffer);
            ctx.PSSetSampler(0, _sampler);
            ctx.PSSetShaderResource(0, videoSrv);

            // 36 vertices — cube-shell synthesized by vsmain from SV_VertexID.
            ctx.Draw(36, 0);
        }
        finally
        {
            // ── Restore state ─────────────────────────────────────────────
            if (prevVps.Length > 0) ctx.RSSetViewports(prevVps);
            ctx.RSSetState(prevRs);
            prevRs?.Dispose();
            ctx.OMSetBlendState(prevBlend, prevBlendFactor, prevSampleMask);
            prevBlend?.Dispose();
            ctx.OMSetDepthStencilState(prevDs, prevStencilRef);
            prevDs?.Dispose();
            ctx.VSSetShader(prevVs);
            prevVs?.Dispose();
            ctx.PSSetShader(prevPs);
            prevPs?.Dispose();
            ctx.IASetInputLayout(prevIL);
            prevIL?.Dispose();
            ctx.IASetPrimitiveTopology(prevTopo);

            ctx.VSSetConstantBuffer(0, vsCbs[0]);
            vsCbs[0]?.Dispose();
            ctx.PSSetConstantBuffer(0, psCbs[0]);
            psCbs[0]?.Dispose();
            if (psSrvs[0] != null) ctx.PSSetShaderResource(0, psSrvs[0]);
            psSrvs[0]?.Dispose();
            ctx.PSSetSampler(0, psSamp[0]);
            psSamp[0]?.Dispose();
        }
    }

    // ── Camera matrix accessors — mirror ref-pyonpix/CameraService.cs ──────
    // SceneCamera->ViewMatrix has M44=0 sometimes; PyonPix forces it to 1.
    // ProjectionMatrix comes from RenderCamera (perspective projection).
    private static Matrix4x4 GetCameraView()
    {
        var mgr = CameraMgr.Instance();
        if (mgr == null) return Matrix4x4.Identity;
        var cam = mgr->GetActiveCamera();
        if (cam == null) return Matrix4x4.Identity;
        Matrix4x4 view = cam->CameraBase.SceneCamera.ViewMatrix;
        view.M44 = 1f;   // matches PyonPix's `view with { M44 = 1.0f }`
        return view;
    }

    private static Matrix4x4 GetCameraProjection()
    {
        var mgr = CameraMgr.Instance();
        if (mgr == null) return Matrix4x4.Identity;
        var cam = mgr->GetActiveCamera();
        if (cam == null) return Matrix4x4.Identity;
        var scene = cam->CameraBase.SceneCamera;
        var render = scene.RenderCamera;
        if (render == null) return Matrix4x4.Identity;
        return render->ProjectionMatrix;
    }

    // ── Dispose ──────────────────────────────────────────────────────────────
    public void Dispose()
    {
        if (_omSetRtHook != null)
        {
            var deadline = Environment.TickCount64 + 2000;
            while (Interlocked.Read(ref _detourInFlight) > 0 && Environment.TickCount64 < deadline)
                Thread.Sleep(1);
            _omSetRtHook.Disable();
            _omSetRtHook.Dispose();
            _omSetRtHook = null;
        }

        foreach (var r in _rtvCache.Values) r.Rtv.Dispose();
        _rtvCache.Clear();
        foreach (var d in _dsvCache.Values) d.Dsv.Dispose();
        _dsvCache.Clear();

        _vs?.Dispose();          _vs = null;
        _ps?.Dispose();          _ps = null;
        _cbuffer?.Dispose();     _cbuffer = null;
        _sampler?.Dispose();     _sampler = null;
        _blend?.Dispose();       _blend = null;
        _blendOff?.Dispose();    _blendOff = null;
        _depthState?.Dispose();  _depthState = null;
        _dsNoDepth?.Dispose();   _dsNoDepth = null;
        _rasterState?.Dispose(); _rasterState = null;

        _context?.Dispose();     _context = null;
        _device = null;

        ActiveState = "disposed";
    }
}
