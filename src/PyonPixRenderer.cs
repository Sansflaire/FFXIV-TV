using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using CSDevice = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device;
using CSControl = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;

namespace FFXIVTv;

/// <summary>
/// Port of priprii/PyonPix's RendererService rendering pipeline. The reason to
/// port this specific approach (rather than the FFXIV-TV D3DRenderer hook path
/// or the XMP-style CopyBlit) is that PyonPix is the only one of the three
/// architectures we've studied that actually renders correctly on OTHER
/// players' machines.
///
/// Pipeline:
///   1. Hook ID3D11DeviceContext::OMSetRenderTargets (vtable slot 33) via
///      HookFromAddress. State-setting hook — fires dozens of times per frame
///      as the game rebinds RTVs. NOT a signature scan; the vtable slot is
///      ABI-stable across game patches.
///   2. Hook IDXGISwapChain::Present (vtable slot 8) as a frame counter only.
///   3. Every OMSetRenderTargets call, record (RTV, DSV) pairs, count binds,
///      cache RTVs that match device W/H and a color-buffer format.
///   4. Score each cached RTV (rebind frequency + format preference + recency).
///      The main-scene RTV gets rebound the most on any hardware config, so
///      the scoring converges to it in ~1 frame on every peer's machine.
///      This is why PyonPix works on peers where D3DRenderer's pattern-match
///      and CopyBlit's ImGui blit both fail.
///   5. When the current bind matches (TargetRTV, cached DSV), fire a native
///      ctx.Draw(6, 0) into that same game-owned RTV using the game's live
///      DSV — the TV pixel goes through the game's tonemap automatically,
///      inherits the game's depth buffer, and the game UI draws over the TV
///      because it's the same render target.
///   6. Save + restore all pipeline state so the game continues untouched.
///
/// Reference: PyonPix/Services/Game/RendererService.cs (897 lines) — cached at
/// ref-pyonpix/RendererService.cs in this repo.
///
/// Differences from PyonPix:
///   - SharpDX -> Vortice (functionally identical, different namespace)
///   - HLSL: we write our own since their .cso bytecode isn't in the repo. A
///     simple 6-vert quad instead of PyonPix's 36-vert cube shell (no borders /
///     back faces for now — that's aesthetics, not the peer-portability property)
///   - Matrices uploaded row-major with row_major HLSL keyword (D3DRenderer's
///     convention). PyonPix transposes CPU-side and uses column-major HLSL. Both
///     work; row_major matches the rest of this codebase.
///   - Single TV screen per plugin (vs PyonPix's per-Pix Renderers dict)
/// </summary>
public sealed unsafe class PyonPixRenderer : IDisposable
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

    // NO Present hook — v0.5.224 crashed here with an AccessViolation because
    // Dalamud already hooks IDXGISwapChain::Present (slot 8) and Reloaded.Hooks'
    // FunctionPatcher can't safely relocate the existing hook trampoline. The
    // AccessViolation kills the game process; see dalamud_appcrash_20260711.log.
    // Instead, IncrementFrameCounter() is called from Plugin.OnDraw once per
    // frame — same functional behavior for RTV scoring without the conflict.

    private readonly IGameInteropProvider _interop;

    // ── Frame + scene tracking ────────────────────────────────────────────────
    private ulong _presentIndex;
    private ulong _lastPresentIndex;
    private ulong _lastRescoredIndex;
    private bool  _sceneRendered;

    // Re-entrancy guard: our own ctx.Draw + state restore inside OMSetRtDetour
    // triggers additional OMSetRenderTargets calls (the state restore).
    // Without this the detour recurses into itself endlessly.
    [ThreadStatic] private static bool _inDetour;

    // Cross-thread deadlock guard: incremented at detour entry, decremented at
    // exit. Dispose() spin-waits on this hitting zero before calling
    // _omSetRtHook.Disable() — closes the race window where a hot-reload
    // teardown lands mid-detour (that race froze the game in v0.5.235;
    // see BROKEN.md's v0.5.235 entry for the full timeline).
    private long _detourInFlight;

    // ── Resource caches ──────────────────────────────────────────────────────
    private sealed class RtvItem
    {
        public required ID3D11RenderTargetView Rtv;
        public int    Width;
        public int    Height;
        public Format Format;
        public bool   IsBound;    // true if the underlying texture has NO ShaderResource bind flag
        public ulong  Calls;
        public ulong  LastPresent;
        public nint   TexturePtr; // native ptr of the underlying ID3D11Texture2D — used
                                  // to match against SwapChain->BackBuffer for definitive
                                  // "am I the currently-active swap chain buffer" check
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

    // ── D3D11 state resources (shared across draws) ──────────────────────────
    private ID3D11VertexShader?      _vs;
    private ID3D11PixelShader?       _ps;
    private ID3D11Buffer?            _cbuffer;
    private ID3D11SamplerState?      _sampler;
    private ID3D11BlendState?        _blend;
    private ID3D11DepthStencilState? _depthReverseZ;    // GreaterEqual + write
    private ID3D11DepthStencilState? _depthReverseZReadOnly;
    private ID3D11DepthStencilState? _dsNoDepth;        // no depth test at all — for debugging
    private ID3D11RasterizerState?   _rasterState;

    // ── Data plumbed in from Plugin.cs each frame ───────────────────────────
    private VideoPlayer?      _videoPlayer;
    private ScreenDefinition? _screen;
    private Configuration?    _config;
    public void SetVideoPlayer(VideoPlayer? vp) => _videoPlayer = vp;
    public void SetScreen(ScreenDefinition? s) => _screen = s;
    public void SetConfig(Configuration? c)    => _config = c;
    public ID3D11Device? Device                => _device;

    /// <summary>
    /// Diagnostic: when true, the shader outputs opaque solid red for every
    /// pixel that survives the vertex-quad rasterization, with NO depth test
    /// and NO blending. If a red rectangle appears at the TV's world position,
    /// pixel output is landing on a visible surface and only the shader math
    /// is wrong. If nothing appears, we're writing to an invisible / overwritten
    /// surface — need to pick a different RTV.
    /// </summary>
    public bool DebugRed { get; set; }

    /// <summary>Diagnostic: when true, skip the depth-stencil test entirely.</summary>
    public bool DisableDepth { get; set; }

    // ── Diagnostics (surfaced via StatusApi) ─────────────────────────────────
    public bool  IsAvailable                  => _vs != null && _ps != null && _omSetRtHook != null;
    public int   DrawCount                    { get; private set; }
    public int   OmSetRtCount                 { get; private set; }
    public int   FrameCount                   { get; private set; }

    /// <summary>
    /// Called once per frame from Plugin.OnDraw. Replaces PyonPix's Present hook
    /// (which we can't install without crashing — Dalamud already hooks Present).
    /// </summary>
    public void IncrementFrameCounter()
    {
        _presentIndex++;
        FrameCount++;
    }
    public int   RtvCacheCount                => _rtvCache.Count;
    public int   DsvCacheCount                => _dsvCache.Count;
    public string LastError                   { get; private set; } = string.Empty;
    public string ActiveState                 { get; private set; } = "uninit";
    public nint  TargetRtvPtr                 => _targetRtv?.Rtv.NativePointer ?? 0;
    public string TargetRtvFormat             => _targetRtv?.Format.ToString() ?? "none";
    public (int W, int H) TargetRtvSize       => _targetRtv != null ? (_targetRtv.Width, _targetRtv.Height) : (0, 0);
    public double TargetRtvScore              { get; private set; }

    /// <summary>
    /// Enumerates every cached RTV with its current score. Used by StatusApi to
    /// diagnose which surface the scoring picks and what alternatives exist.
    /// </summary>
    public IEnumerable<(nint ptr, Format fmt, int w, int h, ulong calls, ulong lastPresent, bool isBound, double score)>
        EnumerateCachedRtvs()
    {
        foreach (var kvp in _rtvCache)
        {
            var r = kvp.Value;
            double s = ComputeScore(r);
            yield return (kvp.Key, r.Format, r.Width, r.Height, r.Calls, r.LastPresent, r.IsBound, s);
        }
    }

    private double ComputeScore(RtvItem r)
    {
        double v = 0.0;
        v += Math.Sqrt(Math.Max(0, r.Calls)) * 10;
        if (r.LastPresent == _presentIndex) v += 300;
        else v += Math.Max(0, 100 - (int)(_presentIndex - r.LastPresent));

        // Default PyonPix format weights. Their upstream ships with these and
        // renders correctly for peers, so start from parity here rather than
        // guessing which surface class matters. The IsBound=true bonus we
        // added in v0.5.232 was overshooting — it picked a buffer that
        // trails/accumulates because it isn't the currently-active back
        // buffer, causing the "smeared TV across the whole screen" issue.
        if      (_prefUnormFormats.Contains(r.Format))    v += 500;
        else if (_prefTypelessFormats.Contains(r.Format)) v += 550;
        else if (r.Format == Format.R16G16B16A16_Float)   v += 450;
        else                                              v += 100;

        return v;
    }

    // ── Constant buffer layout (mirrors HLSL cbuffer below) ─────────────────
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct CbData
    {
        public Matrix4x4 CameraView;       // 64 B — RenderCamera->ViewMatrix (M44 fixed to 1)
        public Matrix4x4 CameraProjection; // 64 B — RenderCamera->ProjectionMatrix (as combined ViewProj)
        public Matrix4x4 ScreenTransform;  // 64 B — TRS for the TV in world space
        public Vector4   Tint;             // 16 B
        public Vector4   Options;          // 16 B — x=hasVideo(0/1) y=reserved z=reserved w=reserved
        // Total 224 B (16-byte aligned)
    }

    public PyonPixRenderer(IGameInteropProvider interop)
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
            if (_gameDevice == null)
            {
                ActiveState = "no game device";
                return false;
            }

            nint ctxPtr = (nint)_gameDevice->D3D11DeviceContext;
            if (ctxPtr == 0) { ActiveState = "no context ptr"; return false; }
            Marshal.AddRef(ctxPtr);
            _context = new ID3D11DeviceContext(ctxPtr);
            _device  = _context.Device;

            CreateShaders();
            CreateState();
            CreateConstantBuffer();

            InstallHooks(ctxPtr);

            ActiveState = "ready";
            Plugin.Log.Info("[FFXIV-TV] PyonPixRenderer initialized (hooks armed).");
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            ActiveState = "init failed";
            Plugin.Log.Warning($"[FFXIV-TV] PyonPixRenderer init failed: {ex}");
            return false;
        }
    }

    private void CreateShaders()
    {
        var vsBytecode = Compiler.Compile(ShaderCode, "VS", "pyonpix_vs", "vs_5_0");
        _vs = _device!.CreateVertexShader(vsBytecode.Span);
        var psBytecode = Compiler.Compile(ShaderCode, "PS", "pyonpix_ps", "ps_5_0");
        _ps = _device.CreatePixelShader(psBytecode.Span);
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

        // Standard straight-alpha blend — video comes in premultiplied
        // by our shader's alpha field, matching NonPremultiplied.
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

        // Reversed-Z: FFXIV uses near=1, far=0. PyonPix flips LessEqual to
        // GreaterEqual for this convention (RendererService.cs:194).
        _depthReverseZ = _device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable    = true,
            DepthWriteMask = DepthWriteMask.All,
            DepthFunc      = ComparisonFunction.GreaterEqual,
            StencilEnable  = false,
        });
        _depthReverseZReadOnly = _device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable    = true,
            DepthWriteMask = DepthWriteMask.Zero,
            DepthFunc      = ComparisonFunction.GreaterEqual,
            StencilEnable  = false,
        });
        _dsNoDepth = _device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable   = false,
            StencilEnable = false,
        });

        // CullMode.None — quads don't need face culling, matches PyonPix's
        // "None" case at RendererService.cs:187.
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
            ByteWidth      = (uint)Marshal.SizeOf<CbData>(),
            Usage          = ResourceUsage.Default,
            BindFlags      = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.None,
        });
    }

    private void InstallHooks(nint ctxPtr)
    {
        // ID3D11DeviceContext vtable slot 33 = OMSetRenderTargets.
        // vtable = *(nint**)context — first field of a COM object is the vtable ptr.
        //
        // We do NOT hook Present. Dalamud already hooks IDXGISwapChain::Present
        // and Reloaded.Hooks' FunctionPatcher AVs when it tries to relocate the
        // existing hook trampoline. Frame counting is done via IncrementFrameCounter
        // called from Plugin.OnDraw instead.
        var ctxVtbl = *(nint**)ctxPtr;
        var omSetRtAddr = ctxVtbl[33];
        _omSetRtHook = _interop.HookFromAddress<OMSetRenderTargetsDelegate>(
            omSetRtAddr, OMSetRtDetour);
        _omSetRtHook.Enable();
    }

    // ── OMSetRenderTargets hook ─────────────────────────────────────────────
    // This is where all the work happens. Called dozens of times per frame as
    // the game rebinds RTVs. See PyonPix/RendererService.cs:397-527.
    private void OMSetRtDetour(nint pContext, uint numViews, nint* ppRtvArray, nint pDsv)
    {
        // Call Original FIRST so the game gets its normal bind.
        _omSetRtHook!.Original(pContext, numViews, ppRtvArray, pDsv);

        // Re-entrancy guard: our own state restore triggers more OMSetRT calls;
        // we must not process them as game binds.
        if (_inDetour) return;

        // Cross-thread guard: Dispose() spin-waits on this hitting zero, so a
        // hot-reload teardown never lands while our body is executing.
        Interlocked.Increment(ref _detourInFlight);
        _inDetour = true;
        try
        {
            OmSetRtCount++;

            if (_screen == null || !_screen.Visible) return;
            if (_gameDevice == null) return;

            // ── Resize / swap-chain rebuild detection ────────────────────────
            // Matches RendererService.cs:402-442. Any indication that the swap
            // chain or back buffer changed → wipe caches and start fresh.
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
            // Guard the deref before touching BackBuffer — an AV in native code
            // here would be uncatchable (same class of crash as the Present hook).
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

            // Rescore ONCE per frame, BEFORE the RTV loop, so isCurRtv uses a
            // stable target. Previously the rescore ran after each new-RTV
            // discovery (which is fine on cold-start) but ALSO ran after the
            // loop each frame, updating _targetRtv between iterations. Since
            // isCurRtv was captured mid-loop, it went stale, and Draw never
            // fired even though the target was being bound every frame.
            if (_lastRescoredIndex != _presentIndex && _rtvCache.Count > 0)
            {
                _lastRescoredIndex = _presentIndex;
                var best = SelectBestRtv();
                if (best != null) _targetRtv = best;
            }

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

            // Fire the actual draw when the game binds our target RTV. Do NOT
            // require a DSV — swap-chain back buffers (which is what our new
            // scoring favors) are typically bound with no DSV during UI /
            // composite passes. Draw() picks a DSV from cache if it needs one,
            // or draws with null DSV + depth disabled.
            _sceneRendered = isCurRtv;
            if (_sceneRendered && _lastPresentIndex != _presentIndex)
            {
                _lastPresentIndex = _presentIndex;
                try
                {
                    Draw(curDsvPtr);
                    DrawCount++;

                    // Restore the game's own RTV binding. My Draw set OMSetRT to
                    // (myTarget, myDsv). Without this restore, subsequent game
                    // draws in this pass would land in MY target RTV, corrupting
                    // whatever intermediate surface the game was actually
                    // rendering to (this was the "screen looks awful" bug in
                    // v0.5.230 with debug shader).
                    //
                    // v0.5.237: previously called `_omSetRtHook.Original(...)` here,
                    // but that call races with Reloaded.Hooks' Disable during a
                    // hot-reload — if teardown happens mid-Original, the render
                    // thread jumps to freed trampoline memory and deadlocks the
                    // game (killed Trist's session overnight in v0.5.235; see
                    // BROKEN.md). Use ctx.OMSetRenderTargets instead: same net
                    // effect (rebind game's RTVs) but re-enters our own detour
                    // via the normal vtable path, which the `_inDetour` guard
                    // handles cleanly. Matches PyonPix's own restore pattern
                    // (RendererService.cs:724).
                    var restoreRtvs = new ID3D11RenderTargetView[numViews];
                    for (uint i = 0; i < numViews; i++)
                    {
                        if (ppRtvArray[i] != 0)
                        {
                            Marshal.AddRef(ppRtvArray[i]);
                            restoreRtvs[i] = new ID3D11RenderTargetView(ppRtvArray[i]);
                        }
                    }
                    ID3D11DepthStencilView? restoreDsv = null;
                    if (pDsv != 0)
                    {
                        Marshal.AddRef(pDsv);
                        restoreDsv = new ID3D11DepthStencilView(pDsv);
                    }
                    try
                    {
                        _context!.OMSetRenderTargets(restoreRtvs, restoreDsv);
                    }
                    finally
                    {
                        for (int i = 0; i < restoreRtvs.Length; i++) restoreRtvs[i]?.Dispose();
                        restoreDsv?.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    Plugin.Log.Warning($"[FFXIV-TV] PyonPix Draw failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            // NEVER crash the game — swallow and log.
            LastError = ex.Message;
            Plugin.Log.Warning($"[FFXIV-TV] PyonPix detour: {ex.GetType().Name}: {ex.Message}");
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

        // Fetch the underlying Texture2D description.
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
            // PyonPix filter (RendererService.cs:266-269): must have SR bind + typeless depth
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

            var item = new DsvItem
            {
                Dsv    = dsv,
                Format = desc.Format,
                Width  = (int)desc.Width,
                Height = (int)desc.Height,
            };
            _dsvCache[dsvPtr] = item;
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
        Format.R8G8B8A8_UNorm,
        Format.R8G8B8A8_Typeless,
        Format.B8G8R8A8_UNorm,
        Format.B8G8R8A8_Typeless,
        Format.R16G16B16A16_UNorm,
        Format.R16G16B16A16_Typeless,
        Format.R16G16B16A16_Float,
    };

    private static readonly HashSet<Format> _prefUnormFormats = new()
    {
        Format.R8G8B8A8_UNorm, Format.B8G8R8A8_UNorm,
    };
    private static readonly HashSet<Format> _prefTypelessFormats = new()
    {
        Format.R8G8B8A8_Typeless, Format.B8G8R8A8_Typeless,
    };

    // ── Heuristic scoring — the key architectural piece for peer portability ──
    // Direct port of RendererService.cs:334-381. Every RTV in the cache is scored;
    // the winner becomes _targetRtv. The main-scene RTV always wins on any peer's
    // hardware because it's the one the game rebinds the most.
    private RtvItem? SelectBestRtv()
    {
        if (_rtvCache.Count == 0) return null;

        // v0.5.238: definitive back-buffer identification. SwapChain->BackBuffer
        // is the game's texture ptr for the buffer that's ABOUT to be Presented
        // this frame. If any cached RTV is a view onto that same texture, THAT
        // is where our pixels will actually show up on screen. Skip the heuristic
        // in that case — the game itself tells us the answer.
        nint currentBackBufferTex = 0;
        try
        {
            if (_gameDevice != null && _gameDevice->SwapChain != null &&
                _gameDevice->SwapChain->BackBuffer != null)
            {
                currentBackBufferTex = (nint)_gameDevice->SwapChain->BackBuffer->D3D11Texture2D;
            }
        }
        catch { /* SwapChain->BackBuffer null between frames — heuristic fallback */ }

        if (currentBackBufferTex != 0)
        {
            foreach (var r in _rtvCache.Values)
            {
                if (r.TexturePtr == currentBackBufferTex)
                {
                    TargetRtvScore = 999999.0;   // sentinel — this one is guaranteed correct
                    return r;
                }
            }
        }

        RtvItem? best      = null;
        double   bestScore = double.MinValue;
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
        _presentIndex     = 0;
        _lastPresentIndex = 0;
        _sceneRendered    = false;
        _targetRtv        = null;
        TargetRtvScore    = 0;

        foreach (var r in _rtvCache.Values) r.Rtv.Dispose();
        _rtvCache.Clear();
        _rtvPtrs.Clear();

        foreach (var d in _dsvCache.Values) d.Dsv.Dispose();
        _dsvCache.Clear();
        _dsvPtrs.Clear();

        _pairCounts.Clear();
    }

    // ── Draw ─────────────────────────────────────────────────────────────────
    // Issues the actual draw call into the game's own RTV using the game's live
    // DSV. All state is saved before and restored after so the game continues
    // its own rendering pipeline as if we weren't there.
    private void Draw(nint curDsvPtr)
    {
        if (_context == null || _device == null) return;
        if (_targetRtv == null) return;
        if (_screen == null || !_screen.Visible) return;

        // Upload the latest video frame while we're on the render thread.
        _videoPlayer?.UploadFrame(_context);
        var videoSrv = _videoPlayer?.FrameSrv;   // may be null when nothing is playing

        // NOTE: we no longer early-return when there's no video — the shader
        // renders a placeholder gradient in that case, matching D3DRenderer's
        // "gradient" activeSrvSource behavior. That way the TV rectangle is
        // visible in-world regardless of playback state.

        // Pick a DSV to use. If the game currently has one bound and it's in
        // our cache, use it (matches the current pipeline state). Otherwise use
        // any cached DSV, or null. When there's no DSV, depth test is forced off.
        _dsvCache.TryGetValue(curDsvPtr, out var dsvItem);
        ID3D11DepthStencilView? drawDsv = dsvItem?.Dsv;
        if (drawDsv == null && _dsvCache.Count > 0)
        {
            foreach (var d in _dsvCache.Values) { drawDsv = d.Dsv; break; }
        }
        bool haveDepth = drawDsv != null;

        // ── Save state ─────────────────────────────────────────────────────
        // Uses the same array-based Vortice pattern as D3DRenderer.SaveState
        // (D3DRenderer.cs:3019-3037). Vortice does not expose single-slot Get*
        // accessors; must go through the Get*s(slot, array) overloads.
        var ctx = _context;
        var prevVps  = ctx.RSGetViewports<Viewport>().ToArray();
        var prevRs   = ctx.RSGetState();
        var prevBlend = ctx.OMGetBlendState(out var prevBlendFactor, out uint prevSampleMask);
        ctx.OMGetDepthStencilState(out var prevDs, out uint prevStencilRef);
        var prevVs   = ctx.VSGetShader();
        var prevPs   = ctx.PSGetShader();
        var prevTopo = ctx.IAGetPrimitiveTopology();
        var prevIL   = ctx.IAGetInputLayout();

        var vsCbs  = new ID3D11Buffer[1];             ctx.VSGetConstantBuffers(0, vsCbs);
        var psCbs  = new ID3D11Buffer[1];             ctx.PSGetConstantBuffers(0, psCbs);
        var psSrvs = new ID3D11ShaderResourceView[1]; ctx.PSGetShaderResources(0, psSrvs);
        var psSamp = new ID3D11SamplerState[1];       ctx.PSGetSamplers(0, psSamp);

        try
        {
            // Update constant buffer.
            var viewProj = _screen.ComputeScreenTransform(); // reused var
            var camView  = CameraService_GetViewMatrix();
            var camProj  = CameraService_GetProjectionMatrix();
            var screenTransform = _screen.ComputeScreenTransform();

            var cb = new CbData
            {
                CameraView       = camView,
                CameraProjection = camProj,
                ScreenTransform  = screenTransform,
                Tint             = _config == null
                    ? new Vector4(1f, 1f, 1f, 1f)
                    : new Vector4(_config.TintR, _config.TintG, _config.TintB, _config.TintA),
                Options          = new Vector4(videoSrv != null ? 1f : 0f, DebugRed ? 1f : 0f, 0f, 0f),
            };
            ctx.UpdateSubresource(cb, _cbuffer!);

            // Bind: chosen DSV (may be null) + our target RTV.
            ctx.OMSetRenderTargets(new[] { _targetRtv.Rtv }, drawDsv);
            ctx.RSSetViewport(new Viewport(0, 0, _targetRtv.Width, _targetRtv.Height, 0, 1));
            ctx.RSSetState(_rasterState);
            ctx.OMSetBlendState(_blend, new Color4(0, 0, 0, 0), 0xFFFFFFFF);
            // Depth state — if we have no DSV, no depth test is possible.
            // Otherwise reverse-Z read-only, unless DisableDepth toggle overrides.
            ctx.OMSetDepthStencilState(
                (!haveDepth || DisableDepth) ? _dsNoDepth : _depthReverseZReadOnly);

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

            // Six vertices — VS emits a unit-quad from SV_VertexID (same as
            // D3DRenderer.cs:506-513). PyonPix uses 36 for a cube shell; we don't
            // need borders/back for now.
            ctx.Draw(6, 0);
        }
        finally
        {
            // ── Restore state ──────────────────────────────────────────────
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

    // Local camera helpers — mirror PyonPix's CameraService but read via
    // Control.Instance() for the combined ViewProj. If we later want per-matrix
    // reads (RenderCamera->ViewMatrix / ProjectionMatrix) we can add them here.
    private static Matrix4x4 CameraService_GetViewMatrix()
    {
        // Return identity — the ScreenTransform × ViewProj chain in our shader
        // uses the combined ViewProj from Control (see below), so a separate
        // view matrix isn't needed. Kept as a helper so future refactors can
        // swap in RenderCamera->ViewMatrix without changing the caller.
        return Matrix4x4.Identity;
    }

    private static Matrix4x4 CameraService_GetProjectionMatrix()
    {
        // Combined ViewProj from the same source D3DRenderer uses. Simpler than
        // separate view+proj and known to work in this codebase.
        var vp = CSControl.Instance()->ViewProjectionMatrix;
        return vp;
    }

    // ── HLSL ─────────────────────────────────────────────────────────────────
    // 6-vertex unit quad from SV_VertexID. Same convention as D3DRenderer.cs:506
    // and CopyBlitRenderer.cs's vertex path. row_major matrices (uploaded raw
    // from .NET Matrix4x4). Sample the video texture, apply tint, return the
    // color with full alpha.
    //
    // Note: CameraView is unused in this simpler port — we pack the combined
    // ViewProj into CameraProjection. Kept in the cbuffer for future
    // "read view + proj separately from RenderCamera" refactors.
    private const string ShaderCode = @"
cbuffer CbParams : register(b0)
{
    row_major float4x4 CameraView;       // reserved for future use
    row_major float4x4 CameraProjection; // ViewProj (combined) for now
    row_major float4x4 ScreenTransform;  // TRS in world space
    float4 Tint;
    float4 Options;                      // x=hasVideo(0/1)
};

Texture2D    tex  : register(t0);
SamplerState samp : register(s0);

static const float3 kPos[6] = {
    float3(-0.5,  0.5, 0.0),  // TL
    float3( 0.5,  0.5, 0.0),  // TR
    float3(-0.5, -0.5, 0.0),  // BL
    float3( 0.5,  0.5, 0.0),  // TR
    float3( 0.5, -0.5, 0.0),  // BR
    float3(-0.5, -0.5, 0.0),  // BL
};
static const float2 kUV[6] = {
    float2(0, 0), float2(1, 0), float2(0, 1),
    float2(1, 0), float2(1, 1), float2(0, 1),
};

struct VS_OUT
{
    float4 pos : SV_Position;
    float2 uv  : TEXCOORD0;
};

VS_OUT VS(uint id : SV_VertexID)
{
    float4 world = mul(float4(kPos[id], 1.0), ScreenTransform);
    VS_OUT o;
    o.pos = mul(world, CameraProjection);
    o.uv  = kUV[id];
    return o;
}

float4 PS(VS_OUT input) : SV_Target
{
    // Diagnostic: solid red when DebugRed toggle is on. If a red rectangle
    // shows at the TV's world-space location, pipeline is fine, only shader
    // math is wrong. If nothing appears, we're writing to an invisible surface.
    if (Options.y > 0.5f) return float4(1.0f, 0.0f, 0.0f, 1.0f);

    float4 c;
    if (Options.x > 0.5f)
    {
        // Video mode — sample the bound video SRV.
        c = tex.Sample(samp, input.uv);
    }
    else
    {
        // No video — render a placeholder gradient so the TV rectangle is
        // visible in-world (matches D3DRenderer's activeSrvSource='gradient').
        c = float4(0.05f + 0.5f * input.uv.x,
                   0.05f + 0.5f * input.uv.y,
                   0.15f,
                   1.0f);
    }
    c *= Tint;
    return c;
}
";

    // ── Dispose ──────────────────────────────────────────────────────────────
    public void Dispose()
    {
        // v0.5.237: spin-wait for any in-flight detour to complete before
        // tearing down the hook. If we Disable/Dispose while a detour is
        // executing, the render thread jumps to freed trampoline memory and
        // the game deadlocks. Cap the wait at 2 seconds so Dispose can't
        // itself hang if something goes very wrong.
        if (_omSetRtHook != null)
        {
            var deadline = System.Environment.TickCount64 + 2000;
            while (Interlocked.Read(ref _detourInFlight) > 0
                && System.Environment.TickCount64 < deadline)
            {
                System.Threading.Thread.Sleep(1);
            }
            _omSetRtHook.Disable();
            _omSetRtHook.Dispose();
            _omSetRtHook = null;
        }

        foreach (var r in _rtvCache.Values) r.Rtv.Dispose();
        _rtvCache.Clear();
        foreach (var d in _dsvCache.Values) d.Dsv.Dispose();
        _dsvCache.Clear();

        _vs?.Dispose();       _vs = null;
        _ps?.Dispose();       _ps = null;
        _cbuffer?.Dispose();  _cbuffer = null;
        _sampler?.Dispose();  _sampler = null;
        _blend?.Dispose();    _blend = null;
        _depthReverseZ?.Dispose();          _depthReverseZ = null;
        _depthReverseZReadOnly?.Dispose();  _depthReverseZReadOnly = null;
        _dsNoDepth?.Dispose();              _dsNoDepth = null;
        _rasterState?.Dispose();            _rasterState = null;

        _context?.Dispose();  _context = null;
        _device = null;

        ActiveState = "disposed";
    }
}
