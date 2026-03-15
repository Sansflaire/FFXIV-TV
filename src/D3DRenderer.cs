using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace FFXIVTv;

/// <summary>
/// Phase 2 renderer: D3D11 world-space quad with depth testing.
///
/// Draws a textured quad in world space using the game's D3D11 device.
/// The game's reversed-Z depth buffer is used so characters occlude the screen correctly.
///
/// Reversed-Z note: FFXIV uses near=1.0, far=0.0.
/// Depth comparison is GREATER: pass if our pixel depth > stored (closer to camera wins).
///
/// Reference: PunishXIV/Splatoon — VbmCamera.cs + ViewMatrix.M44 fix.
/// </summary>
public sealed unsafe class D3DRenderer : IDisposable
{
    // Borrowed from ImGui DX11 backend — AddRef on wrap, Release on Dispose is correct.
    private ID3D11Device?        _device;
    private ID3D11DeviceContext? _context;

    private ID3D11Buffer?        _vb;           // 4 verts, dynamic
    private ID3D11Buffer?        _ib;           // 6 indices, static
    private ID3D11Buffer?        _cb;           // ViewProj cbuffer
    private ID3D11VertexShader?  _vs;
    private ID3D11PixelShader?   _ps;
    private ID3D11InputLayout?   _inputLayout;
    private ID3D11BlendState?        _blendState;
    private ID3D11RasterizerState?   _rasterizer;
    private ID3D11DepthStencilState? _dsWithDepth;
    private ID3D11DepthStencilState? _dsNoDepth;
    private ID3D11SamplerState?      _sampler;

    private bool _initialized;
    public bool IsAvailable => _initialized;

    // ── Vertex layout ─────────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenVertex { public Vector3 Position; public Vector2 UV; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CbPerFrame { public Matrix4x4 ViewProj; }

    // ── HLSL — row_major matches System.Numerics.Matrix4x4 (Splatoon-confirmed) ─
    private const string VS_SRC = @"
cbuffer CbPerFrame : register(b0) { row_major float4x4 ViewProj; };
struct VSIn  { float3 pos : POSITION; float2 uv : TEXCOORD; };
struct VSOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD; };
VSOut main(VSIn v) {
    VSOut o;
    o.pos = mul(float4(v.pos, 1.0f), ViewProj);
    o.uv  = v.uv;
    return o;
}";

    private const string PS_SRC = @"
Texture2D    tex  : register(t0);
SamplerState samp : register(s0);
float4 main(float2 uv : TEXCOORD) : SV_TARGET { return tex.Sample(samp, uv); }";

    // ── Init ──────────────────────────────────────────────────────────────────
    public bool TryInitialize()
    {
        if (_initialized) return true;

        // ImGui_ImplDX11_Data layout (stable since ImGui 1.80):
        //   offset 0 = ID3D11Device*
        //   offset 8 = ID3D11DeviceContext*
        var io = ImGui.GetIO();
        void* brd = io.BackendRendererUserData;
        if (brd == null)
        {
            Plugin.Log.Debug("[FFXIV-TV] D3DRenderer: ImGui DX11 backend data not ready yet.");
            return false;
        }

        nint* bd = (nint*)brd;
        if (bd[0] == 0 || bd[1] == 0)
        {
            Plugin.Log.Warning("[FFXIV-TV] D3DRenderer: null device or context in backend data.");
            return false;
        }

        // Vortice constructor does NOT AddRef — but the Get* call that fills the backend
        // data already AddRef'd, so these pointers are valid borrowed references.
        // We AddRef here so our Dispose (Release) is safe.
        _device  = new ID3D11Device(bd[0]);
        _device.AddRef();
        _context = new ID3D11DeviceContext(bd[1]);
        _context.AddRef();

        try
        {
            CreateResources();
            _initialized = true;
            Plugin.Log.Info("[FFXIV-TV] D3DRenderer initialized — Phase 2 depth mode active.");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[FFXIV-TV] D3DRenderer init failed: {ex.Message}");
            DisposeResources();
            return false;
        }
    }

    private void CreateResources()
    {
        // Vertex buffer: 4 verts, written each frame.
        _vb = _device!.CreateBuffer(
            (uint)(sizeof(ScreenVertex) * 4),
            BindFlags.VertexBuffer,
            ResourceUsage.Dynamic,
            CpuAccessFlags.Write);

        // Index buffer: TL→TR→BR, TL→BR→BL (static).
        uint[] indices = { 0, 1, 2, 0, 2, 3 };
        _ib = _device.CreateBuffer<uint>(indices, BindFlags.IndexBuffer);

        // Constant buffer (ViewProj, 64 bytes). CreateConstantBuffer<T> aligns and sets Dynamic+Write.
        _cb = _device.CreateConstantBuffer<CbPerFrame>();

        // Compile shaders at runtime.
        ReadOnlyMemory<byte> vsBlob = Compiler.Compile(VS_SRC, "main", "screen_vs", "vs_5_0");
        ReadOnlyMemory<byte> psBlob = Compiler.Compile(PS_SRC, "main", "screen_ps", "ps_5_0");

        _vs = _device.CreateVertexShader(vsBlob.Span);
        _ps = _device.CreatePixelShader(psBlob.Span);

        InputElementDescription[] layout =
        {
            new("POSITION", 0, Format.R32G32B32_Float, 0,  0, InputClassification.PerVertexData, 0),
            new("TEXCOORD", 0, Format.R32G32_Float,    12, 0, InputClassification.PerVertexData, 0),
        };
        _inputLayout = _device.CreateInputLayout(layout, vsBlob.Span);

        // Blend: standard non-premultiplied alpha.
        _blendState = _device.CreateBlendState(BlendDescription.NonPremultiplied);

        // Rasterizer: no culling (two-sided).
        _rasterizer = _device.CreateRasterizerState(new RasterizerDescription
        {
            CullMode        = CullMode.None,
            FillMode        = FillMode.Solid,
            DepthClipEnable = true,
        });

        // Depth — reversed-Z: near=1, far=0.  GREATER = closer to camera wins.
        _dsWithDepth = _device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable    = true,
            DepthWriteMask = DepthWriteMask.Zero,         // read-only, don't pollute scene depth
            DepthFunc      = ComparisonFunction.Greater,  // reversed-Z
            StencilEnable  = false,
        });

        // Fallback when no depth buffer is available at Present time.
        _dsNoDepth = _device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable   = false,
            StencilEnable = false,
        });

        _sampler = _device.CreateSamplerState(SamplerDescription.LinearClamp);
    }

    // ── Per-frame draw ────────────────────────────────────────────────────────
    /// <summary>
    /// Draws the screen in world space with D3D11 depth testing.
    /// textureHandle is IDalamudTextureWrap.Handle.Handle (ulong, = ID3D11ShaderResourceView*).
    /// </summary>
    public void Draw(ScreenDefinition screen, ulong textureHandle)
    {
        if (!_initialized || _context == null) return;
        if (textureHandle == 0) return;

        // ViewProj — same source as IGameGui.WorldToScreen.
        // M44 is uninitialized in game memory (Splatoon-confirmed bug) — always set to 1.
        var ctrl = Control.Instance();
        if (ctrl == null) return;

        Matrix4x4 viewProj = ctrl->ViewProjectionMatrix;
        viewProj.M44 = 1f;

        var (wTL, wTR, wBR, wBL) = screen.GetWorldCorners();
        UpdateVB(wTL, wTR, wBR, wBL);
        UpdateCB(viewProj);

        // Check if the game's depth buffer is still bound at Present time.
        var rtvArray = new ID3D11RenderTargetView[1];
        _context.OMGetRenderTargets(1, rtvArray, out ID3D11DepthStencilView? dsv);
        bool hasDepth = dsv != null;

        SavedState saved = SaveState();
        try
        {
            SetState((nint)(long)textureHandle, hasDepth ? _dsWithDepth! : _dsNoDepth!);
            _context.DrawIndexed(6, 0, 0);
        }
        finally
        {
            RestoreState(saved);
            dsv?.Dispose();
            foreach (var rtv in rtvArray) rtv?.Dispose();
        }
    }

    private void UpdateVB(Vector3 tl, Vector3 tr, Vector3 br, Vector3 bl)
    {
        var mapped = _context!.Map(_vb!, MapMode.WriteDiscard);
        var verts  = mapped.AsSpan<ScreenVertex>(4);
        verts[0] = new ScreenVertex { Position = tl, UV = new(0, 0) };
        verts[1] = new ScreenVertex { Position = tr, UV = new(1, 0) };
        verts[2] = new ScreenVertex { Position = br, UV = new(1, 1) };
        verts[3] = new ScreenVertex { Position = bl, UV = new(0, 1) };
        _context.Unmap(_vb!);
    }

    private void UpdateCB(Matrix4x4 viewProj)
    {
        var mapped = _context!.Map(_cb!, MapMode.WriteDiscard);
        mapped.AsSpan<CbPerFrame>(1)[0] = new CbPerFrame { ViewProj = viewProj };
        _context.Unmap(_cb!);
    }

    private void SetState(nint srvPtr, ID3D11DepthStencilState depthState)
    {
        _context!.IASetVertexBuffer(0, _vb!, (uint)sizeof(ScreenVertex));
        _context.IASetIndexBuffer(_ib!, Format.R32_UInt, 0);
        _context.IASetInputLayout(_inputLayout!);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

        _context.VSSetShader(_vs!);
        _context.VSSetConstantBuffer(0, _cb!);

        _context.PSSetShader(_ps!);
        // SRV is borrowed from Dalamud's texture system.
        // AddRef so our Dispose doesn't destroy it.
        var srv = new ID3D11ShaderResourceView(srvPtr);
        srv.AddRef();
        _context.PSSetShaderResource(0, srv);
        srv.Dispose(); // releases our extra AddRef; Dalamud still holds original ref
        _context.PSSetSampler(0, _sampler!);

        _context.RSSetState(_rasterizer!);
        _context.OMSetBlendState(_blendState!);
        _context.OMSetDepthStencilState(depthState, 0);
    }

    // ── Minimal state save / restore ─────────────────────────────────────────
    // Saves only the pipeline stages we actually modify so we don't break ImGui.

    private struct SavedState
    {
        public ID3D11InputLayout?         InputLayout;
        public ID3D11VertexShader?        VS;
        public ID3D11PixelShader?         PS;
        public ID3D11Buffer?              VSCb;
        public ID3D11ShaderResourceView?  PSSrv;
        public ID3D11SamplerState?        PSSampler;
        public ID3D11RasterizerState?     RS;
        public ID3D11BlendState?          Blend;
        public ID3D11DepthStencilState?   DSS;
        public uint                       StencilRef;
        public PrimitiveTopology          Topology;
    }

    private SavedState SaveState()
    {
        var s = new SavedState();
        s.Topology    = _context!.IAGetPrimitiveTopology();
        s.InputLayout = _context.IAGetInputLayout();
        s.VS          = _context.VSGetShader();
        var vsCbs     = new ID3D11Buffer[1]; _context.VSGetConstantBuffers(0, vsCbs); s.VSCb = vsCbs[0];
        s.PS          = _context.PSGetShader();
        var srvs      = new ID3D11ShaderResourceView[1]; _context.PSGetShaderResources(0, srvs); s.PSSrv = srvs[0];
        var samplers  = new ID3D11SamplerState[1]; _context.PSGetSamplers(0, samplers); s.PSSampler = samplers[0];
        s.RS          = _context.RSGetState();
        s.Blend       = _context.OMGetBlendState(out _, out _);
        _context.OMGetDepthStencilState(out s.DSS, out s.StencilRef);
        return s;
    }

    private void RestoreState(SavedState s)
    {
        _context!.IASetPrimitiveTopology(s.Topology);
        _context.IASetInputLayout(s.InputLayout);
        _context.VSSetShader(s.VS);
        _context.VSSetConstantBuffer(0, s.VSCb);
        _context.PSSetShader(s.PS);
        _context.PSSetShaderResource(0, s.PSSrv);
        _context.PSSetSampler(0, s.PSSampler);
        _context.RSSetState(s.RS);
        _context.OMSetBlendState(s.Blend);
        _context.OMSetDepthStencilState(s.DSS, s.StencilRef);

        s.InputLayout?.Dispose();
        s.VS?.Dispose();
        s.VSCb?.Dispose();
        s.PS?.Dispose();
        s.PSSrv?.Dispose();
        s.PSSampler?.Dispose();
        s.RS?.Dispose();
        s.Blend?.Dispose();
        s.DSS?.Dispose();
    }

    // ── Dispose ───────────────────────────────────────────────────────────────
    public void Dispose()
    {
        DisposeResources();
        _context?.Dispose();
        _device?.Dispose();
        _context      = null;
        _device       = null;
        _initialized  = false;
    }

    private void DisposeResources()
    {
        _sampler?.Dispose();      _sampler      = null;
        _dsNoDepth?.Dispose();    _dsNoDepth    = null;
        _dsWithDepth?.Dispose();  _dsWithDepth  = null;
        _rasterizer?.Dispose();   _rasterizer   = null;
        _blendState?.Dispose();   _blendState   = null;
        _inputLayout?.Dispose();  _inputLayout  = null;
        _ps?.Dispose();           _ps           = null;
        _vs?.Dispose();           _vs           = null;
        _cb?.Dispose();           _cb           = null;
        _ib?.Dispose();           _ib           = null;
        _vb?.Dispose();           _vb           = null;
    }
}
