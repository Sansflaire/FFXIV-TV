using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace FFXIVTv;

/// <summary>
/// Phase 2 renderer: D3D11 world-space quad injected into ImGui's render pipeline.
///
/// During UiBuilder.Draw we update the vertex/constant buffers and register an
/// ImGui draw callback on the background draw list.  The callback fires inside
/// ImGui_ImplDX11_RenderDrawData, at which point the correct RTV and viewport
/// are already bound by the ImGui DX11 backend — so our DrawIndexed call lands
/// on the real presentation surface.
///
/// Reversed-Z note: FFXIV uses near=1.0, far=0.0.
/// Depth comparison is GREATER: pass if our pixel depth > stored (closer wins).
/// </summary>
public sealed unsafe class D3DRenderer : IDisposable
{
    private ID3D11Device?        _device;
    private ID3D11DeviceContext? _context;

    private ID3D11Buffer?        _vb;
    private ID3D11Buffer?        _ib;
    private ID3D11Buffer?        _cb;
    private ID3D11VertexShader?  _vs;
    private ID3D11PixelShader?   _ps;
    private ID3D11InputLayout?   _inputLayout;
    private ID3D11BlendState?        _blendState;
    private ID3D11RasterizerState?   _rasterizer;
    private ID3D11DepthStencilState? _dsNoDepth;
    private ID3D11SamplerState?      _sampler;

    private bool _initialized;
    public bool IsAvailable => _initialized;

    // Pending texture handle — set in Draw(), consumed in the ImGui callback.
    private ulong _pendingTextureHandle;

    // Managed delegate kept alive for the lifetime of this renderer.
    private readonly ImDrawCallback _renderCallback;

    // ── Vertex layout ─────────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenVertex { public Vector3 Position; public Vector2 UV; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CbPerFrame { public Matrix4x4 ViewProj; }

    // ── HLSL ─────────────────────────────────────────────────────────────────
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

    // ── Constructor ───────────────────────────────────────────────────────────
    public D3DRenderer()
    {
        _renderCallback = new ImDrawCallback(ExecuteDrawCallback);
    }

    // ── Init ──────────────────────────────────────────────────────────────────
    public bool TryInitialize()
    {
        if (_initialized) return true;

        var kernelDevice = Device.Instance();
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
        _context = _device.ImmediateContext;

        Plugin.Log.Info($"[FFXIV-TV] D3DRenderer: device=0x{devicePtr:X}");

        try
        {
            CreateResources();
            _initialized = true;
            Plugin.Log.Info("[FFXIV-TV] D3DRenderer initialized — Phase 2 active.");
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
        _vb = _device!.CreateBuffer(
            (uint)(sizeof(ScreenVertex) * 4),
            BindFlags.VertexBuffer,
            ResourceUsage.Dynamic,
            CpuAccessFlags.Write);

        uint[] indices = { 0, 1, 2, 0, 2, 3 };
        _ib = _device.CreateBuffer<uint>(indices, BindFlags.IndexBuffer);

        _cb = _device.CreateConstantBuffer<CbPerFrame>();

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

        _blendState = _device.CreateBlendState(BlendDescription.NonPremultiplied);

        _rasterizer = _device.CreateRasterizerState(new RasterizerDescription
        {
            CullMode        = CullMode.None,
            FillMode        = FillMode.Solid,
            DepthClipEnable = true,
        });

        // No depth testing — ImGui backend doesn't bind a DSV.
        _dsNoDepth = _device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable   = false,
            StencilEnable = false,
        });

        _sampler = _device.CreateSamplerState(SamplerDescription.LinearClamp);
    }

    // ── Per-frame draw ────────────────────────────────────────────────────────
    /// <summary>
    /// Called from UiBuilder.Draw.  Updates GPU buffers and registers an ImGui
    /// callback that will execute the actual DrawIndexed at submission time.
    /// </summary>
    public void Draw(ScreenDefinition screen, ulong textureHandle)
    {
        if (!_initialized || _context == null) return;
        if (textureHandle == 0) return;

        var ctrl = Control.Instance();
        if (ctrl == null) return;

        Matrix4x4 viewProj = ctrl->ViewProjectionMatrix;
        viewProj.M44 = 1f;

        var (wTL, wTR, wBR, wBL) = screen.GetWorldCorners();
        UpdateVB(wTL, wTR, wBR, wBL);
        UpdateCB(viewProj);

        _pendingTextureHandle = textureHandle;

        // Register callback on background draw list.
        // It fires inside ImGui_ImplDX11_RenderDrawData with correct RTV + viewport.
        ImGui.GetBackgroundDrawList().AddCallback(_renderCallback, null);
    }

    // ── ImGui render callback ─────────────────────────────────────────────────
    private void ExecuteDrawCallback(ImDrawList* parentList, ImDrawCmd* cmd)
    {
        if (_pendingTextureHandle == 0 || _context == null) return;

        var saved = SaveState();
        try
        {
            SetState((nint)(long)_pendingTextureHandle, _dsNoDepth!);
            _context.DrawIndexed(6, 0, 0);
        }
        finally
        {
            RestoreState(saved);
            _pendingTextureHandle = 0;
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
        var srv = new ID3D11ShaderResourceView(srvPtr);
        srv.AddRef();
        _context.PSSetShaderResource(0, srv);
        srv.Dispose();
        _context.PSSetSampler(0, _sampler!);

        _context.RSSetState(_rasterizer!);
        _context.OMSetBlendState(_blendState!);
        _context.OMSetDepthStencilState(depthState, 0);
    }

    // ── State save / restore ──────────────────────────────────────────────────
    private struct SavedState
    {
        public ID3D11InputLayout?         InputLayout;
        public ID3D11Buffer?              VB;
        public uint                       VBStride;
        public uint                       VBOffset;
        public ID3D11Buffer?              IB;
        public Format                     IBFormat;
        public uint                       IBOffset;
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

        var vbs      = new ID3D11Buffer[1];
        var strides  = new uint[1];
        var offsets  = new uint[1];
        _context.IAGetVertexBuffers(0, 1, vbs, strides, offsets);
        s.VB       = vbs[0];
        s.VBStride = strides[0];
        s.VBOffset = offsets[0];

        _context.IAGetIndexBuffer(out s.IB, out s.IBFormat, out s.IBOffset);

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
        _context.IASetVertexBuffer(0, s.VB, s.VBStride, s.VBOffset);
        _context.IASetIndexBuffer(s.IB, s.IBFormat, s.IBOffset);
        _context.VSSetShader(s.VS);
        _context.VSSetConstantBuffer(0, s.VSCb);
        _context.PSSetShader(s.PS);
        if (s.PSSrv != null) _context.PSSetShaderResource(0, s.PSSrv);
        _context.PSSetSampler(0, s.PSSampler);
        _context.RSSetState(s.RS);
        _context.OMSetBlendState(s.Blend);
        _context.OMSetDepthStencilState(s.DSS, s.StencilRef);

        s.InputLayout?.Dispose();
        s.VB?.Dispose();
        s.IB?.Dispose();
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
        _context = null;
        _device?.Dispose();
        _device      = null;
        _initialized = false;
    }

    private void DisposeResources()
    {
        _sampler?.Dispose();      _sampler      = null;
        _dsNoDepth?.Dispose();    _dsNoDepth    = null;
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
