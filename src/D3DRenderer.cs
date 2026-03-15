using System;
using System.IO;
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
using SDDrawing = System.Drawing;
using SDImaging = System.Drawing.Imaging;

namespace FFXIVTv;

/// <summary>
/// Phase 2 renderer: D3D11 world-space quad injected into ImGui's render pipeline.
///
/// UV approach: All interpolated TEXCOORD semantics are silently zeroed in this D3D context
/// (TEXCOORD0, TEXCOORD1, all confirmed broken across rounds 3–15). SV_VertexID is always 0.
/// The only working per-pixel data is SV_POSITION (rasterizer system value, not interpolated).
///
/// Solution: PS reads SV_POSITION (screen pixels) and computes UV via bilinear inverse mapping
/// from the quad's four screen-space corner positions, passed via a constant buffer (b1).
/// Non-perspective-correct (screen-space bilinear), acceptable for near-head-on viewing.
///
/// Reversed-Z note: FFXIV uses near=1.0, far=0.0.
/// </summary>
public sealed unsafe class D3DRenderer : IDisposable
{
    private ID3D11Device?        _device;
    private ID3D11DeviceContext? _context;

    private ID3D11Buffer?        _vb;
    private ID3D11Buffer?        _cb;         // VS b0: ViewProj matrix
    private ID3D11Buffer?        _cbCorners;  // PS b1: screen-space quad corner positions
    private ID3D11VertexShader?  _vs;
    private ID3D11PixelShader?   _ps;
    private ID3D11InputLayout?   _inputLayout;
    private ID3D11BlendState?        _blendState;
    private ID3D11RasterizerState?   _rasterizer;
    private ID3D11DepthStencilState? _dsNoDepth;
    private ID3D11DepthStencilState? _dsReverseZ;
    private ID3D11SamplerState?      _sampler;

    // Saved at Draw() time (before ImGui swaps render targets).
    private ID3D11DepthStencilView? _savedDsv;

    // Own texture loaded from the image file.
    private ID3D11ShaderResourceView? _imageSrv;
    private string                    _loadedImagePath = string.Empty;

    private bool _initialized;
    public bool IsAvailable => _initialized;
    public bool HasTexture  => _imageSrv != null;

    // Set each frame Draw() is called; cleared after the callback fires.
    private bool _drawPending;

    // World corners and ViewProj stored in Draw(), used in the callback to compute screen corners.
    private Vector3  _wTL, _wTR, _wBL, _wBR;
    private Matrix4x4 _storedViewProj;


    private readonly ImDrawCallback _renderCallback;

    // ── Vertex layout ─────────────────────────────────────────────────────────
    // TriangleStrip order: 0=TL  1=TR  2=BL  3=BR
    // UV column in VB is unused by shaders (UV comes from PS cbuffer computation),
    // but kept so the input layout stride remains correct.
    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenVertex { public Vector3 Position; public Vector2 UV; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CbPerFrame { public Matrix4x4 ViewProj; }

    // PS cbuffer: screen-space pixel positions of the four quad corners.
    // .xy = screen pixels, .zw unused (padding for 16-byte alignment).
    [StructLayout(LayoutKind.Sequential)]
    private struct CbCorners
    {
        public Vector4 TL, TR, BL, BR;
    }

    // ── HLSL ─────────────────────────────────────────────────────────────────
    // VS: transforms world positions to clip space. No TEXCOORD output —
    // PS computes UV from SV_POSITION instead (all interpolated semantics are broken).
    private const string VS_SRC = @"
cbuffer CbPerFrame : register(b0) { row_major float4x4 ViewProj; };
struct VSIn { float3 pos : POSITION; float2 uv : TEXCOORD; };
float4 main(VSIn v) : SV_POSITION {
    return mul(float4(v.pos, 1.0f), ViewProj);
}";

    // PS: computes UV via bilinear inverse from screen-space corner positions.
    // SV_POSITION is a rasterizer system value and is always correctly populated.
    // Non-perspective-correct (screen-space bilinear). Acceptable for near-head-on viewing.
    // Corner positions (screen pixels) passed via cbuffer b1, updated each callback from ViewProj.
    private const string PS_SRC = @"
cbuffer CbCorners : register(b1) { float4 TL, TR, BL, BR; };
Texture2D    tex  : register(t0);
SamplerState samp : register(s0);

float2 bilinear_uv(float2 p, float2 p00, float2 p10, float2 p01, float2 p11)
{
    float2 e = p10 - p00;
    float2 f = p01 - p00;
    float2 g = p00 - p10 - p01 + p11;
    float2 h = p  - p00;

    // Quadratic in v: Av*v^2 + Bv*v + Cv = 0
    // For linear case (Av~0): Bv*v + Cv = 0  =>  v = -Cv/Bv
    float Av = g.x*f.y - g.y*f.x;
    float Bv = e.x*f.y - e.y*f.x + h.x*g.y - h.y*g.x;
    float Cv = h.x*e.y - h.y*e.x;

    float v;
    if (abs(Av) < 1e-4) {
        v = -Cv / (Bv + 1e-10);   // R17 fix: was Cv/Bv (wrong sign)
    } else {
        float disc = max(0.0, Bv*Bv - 4.0*Av*Cv);
        float sq   = sqrt(disc);
        float v1   = (-Bv + sq) / (2.0*Av);
        float v2   = (-Bv - sq) / (2.0*Av);
        v = (v1 >= -0.01 && v1 <= 1.01) ? v1 : v2;
    }

    float2 numer = h   - f*v;
    float2 denom = e + g*v;
    float u = (abs(denom.x) >= abs(denom.y)) ? numer.x/denom.x : numer.y/denom.y;

    return saturate(float2(u, v));
}

float4 main(float4 pos : SV_POSITION) : SV_TARGET {
    float2 uv = bilinear_uv(pos.xy, TL.xy, TR.xy, BL.xy, BR.xy);
    return tex.Sample(samp, uv);
}";

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

        _cb        = _device.CreateConstantBuffer<CbPerFrame>();
        _cbCorners = _device.CreateConstantBuffer<CbCorners>();

        ReadOnlyMemory<byte> vsBlob = Compiler.Compile(VS_SRC, "main", "screen_vs", "vs_5_0");
        ReadOnlyMemory<byte> psBlob = Compiler.Compile(PS_SRC, "main", "screen_ps", "ps_5_0");

        Plugin.Log.Info($"[FFXIV-TV] Shader blobs: VS={vsBlob.Length}B PS={psBlob.Length}B");

        _vs = _device.CreateVertexShader(vsBlob.Span);
        _ps = _device.CreatePixelShader(psBlob.Span);

        InputElementDescription[] layout =
        {
            new("POSITION", 0, Format.R32G32B32_Float, 0,  0, InputClassification.PerVertexData, 0),
            new("TEXCOORD", 0, Format.R32G32_Float,    0, 12, InputClassification.PerVertexData, 0),
        };
        _inputLayout = _device.CreateInputLayout(layout, vsBlob.Span);

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

        _sampler = _device.CreateSamplerState(SamplerDescription.LinearClamp);
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
        if (!_initialized || _context == null || _imageSrv == null) return;

        var ctrl = Control.Instance();
        if (ctrl == null) return;

        Matrix4x4 viewProj = ctrl->ViewProjectionMatrix;

        _savedDsv?.Dispose();
        _savedDsv = null;
        var rtvCapture = new ID3D11RenderTargetView[1];
        _context.OMGetRenderTargets(1, rtvCapture, out _savedDsv);
        rtvCapture[0]?.Dispose();

        var (wTL, wTR, wBR, wBL) = screen.GetWorldCorners();
        // Store corners and ViewProj for use in callback (to compute screen-space positions).
        _wTL = wTL; _wTR = wTR; _wBL = wBL; _wBR = wBR;
        _storedViewProj = viewProj;

        UpdateVB(wTL, wTR, wBL, wBR);
        UpdateCB(viewProj);

        _drawPending = true;
        ImGui.GetBackgroundDrawList().AddCallback(_renderCallback, null);
    }

    // ── ImGui render callback ─────────────────────────────────────────────────
    private void ExecuteDrawCallback(ImDrawList* parentList, ImDrawCmd* cmd)
    {
        if (!_drawPending || _imageSrv == null || _context == null) return;

        var rtvBuf = new ID3D11RenderTargetView[1];
        _context.OMGetRenderTargets(1, rtvBuf, out var currentDsv);
        var rtv = rtvBuf[0];

        var effectiveDsv = _savedDsv ?? currentDsv;
        var depthState   = effectiveDsv != null ? _dsReverseZ! : _dsNoDepth!;

        if (rtv != null)
            _context.OMSetRenderTargets(new[] { rtv }, effectiveDsv);

        // Compute screen-space corner positions from world corners + ViewProj + current viewport.
        // Used by PS to compute UV via bilinear inverse (all interpolated semantics are broken).
        UpdateCbCorners();

        var saved = SaveState();
        try
        {
            SetState(_imageSrv, depthState);
            _context.Draw(4, 0);
        }
        finally
        {
            RestoreState(saved);
            if (rtv != null)
                _context.OMSetRenderTargets(new[] { rtv }, null);

            rtv?.Dispose();
            currentDsv?.Dispose();
            _drawPending = false;
        }
    }

    // Projects one world corner to screen pixels using the stored ViewProj + current viewport.
    private Vector4 WorldToScreenPixels(Vector3 world, Viewport vp)
    {
        var clip = Vector4.Transform(new Vector4(world, 1f), _storedViewProj);
        if (MathF.Abs(clip.W) < 1e-6f) clip.W = 1e-6f;  // guard against degenerate W
        float ndcX =  clip.X / clip.W;
        float ndcY =  clip.Y / clip.W;
        float sx = (ndcX + 1f) * 0.5f * vp.Width  + vp.X;
        float sy = (1f - ndcY) * 0.5f * vp.Height + vp.Y;
        return new Vector4(sx, sy, 0f, 0f);
    }

    private void UpdateCbCorners()
    {
        var vpArr = new Viewport[1];
        uint vpCount = 1;
        _context!.RSGetViewports(ref vpCount, vpArr);
        var vp = vpArr[0];

        var tl = WorldToScreenPixels(_wTL, vp);
        var tr = WorldToScreenPixels(_wTR, vp);
        var bl = WorldToScreenPixels(_wBL, vp);
        var br = WorldToScreenPixels(_wBR, vp);

        var mapped = _context.Map(_cbCorners!, MapMode.WriteDiscard);
        mapped.AsSpan<CbCorners>(1)[0] = new CbCorners { TL = tl, TR = tr, BL = bl, BR = br };
        _context.Unmap(_cbCorners!);
    }

    private void UpdateVB(Vector3 tl, Vector3 tr, Vector3 bl, Vector3 br)
    {
        var mapped = _context!.Map(_vb!, MapMode.WriteDiscard);
        var f = mapped.AsSpan<float>(20);
        f[ 0]=tl.X; f[ 1]=tl.Y; f[ 2]=tl.Z; f[ 3]=0f; f[ 4]=0f;
        f[ 5]=tr.X; f[ 6]=tr.Y; f[ 7]=tr.Z; f[ 8]=1f; f[ 9]=0f;
        f[10]=bl.X; f[11]=bl.Y; f[12]=bl.Z; f[13]=0f; f[14]=1f;
        f[15]=br.X; f[16]=br.Y; f[17]=br.Z; f[18]=1f; f[19]=1f;
        _context.Unmap(_vb!);
    }

    private void UpdateCB(Matrix4x4 viewProj)
    {
        var mapped = _context!.Map(_cb!, MapMode.WriteDiscard);
        mapped.AsSpan<CbPerFrame>(1)[0] = new CbPerFrame { ViewProj = viewProj };
        _context.Unmap(_cb!);
    }

    private void SetState(ID3D11ShaderResourceView srv, ID3D11DepthStencilState depthState)
    {
        _context!.IASetVertexBuffer(0, _vb!, (uint)sizeof(ScreenVertex));
        _context.IASetInputLayout(_inputLayout!);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);

        _context.VSSetShader(_vs!);
        _context.GSSetShader(null);
        _context.HSSetShader(null);
        _context.DSSetShader(null);
        _context.VSSetConstantBuffer(0, _cb!);

        _context.PSSetShader(_ps!);
        _context.PSSetConstantBuffer(1, _cbCorners!);
        _context.PSSetShaderResource(0, srv);
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
        public ID3D11GeometryShader?      GS;
        public ID3D11HullShader?          HS;
        public ID3D11DomainShader?        DS;
        public ID3D11PixelShader?         PS;
        public ID3D11Buffer?              VSCb;
        public ID3D11Buffer?              PSCb1;  // PS constant buffer slot 1 (we bind _cbCorners here)
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

        var vbs     = new ID3D11Buffer[1];
        var strides = new uint[1];
        var offsets = new uint[1];
        _context.IAGetVertexBuffers(0, 1, vbs, strides, offsets);
        s.VB       = vbs[0];
        s.VBStride = strides[0];
        s.VBOffset = offsets[0];

        _context.IAGetIndexBuffer(out s.IB, out s.IBFormat, out s.IBOffset);

        s.VS = _context.VSGetShader();
        s.GS = _context.GSGetShader();
        s.HS = _context.HSGetShader();
        s.DS = _context.DSGetShader();
        var vsCbs = new ID3D11Buffer[1]; _context.VSGetConstantBuffers(0, vsCbs); s.VSCb = vsCbs[0];
        s.PS = _context.PSGetShader();
        var psCbs1 = new ID3D11Buffer[1]; _context.PSGetConstantBuffers(1, psCbs1); s.PSCb1 = psCbs1[0];
        var srvs     = new ID3D11ShaderResourceView[1]; _context.PSGetShaderResources(0, srvs); s.PSSrv     = srvs[0];
        var samplers = new ID3D11SamplerState[1];        _context.PSGetSamplers(0, samplers);   s.PSSampler = samplers[0];
        s.RS    = _context.RSGetState();
        s.Blend = _context.OMGetBlendState(out _, out _);
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
        _context.GSSetShader(s.GS);
        _context.HSSetShader(s.HS);
        _context.DSSetShader(s.DS);
        _context.VSSetConstantBuffer(0, s.VSCb);
        _context.PSSetShader(s.PS);
        _context.PSSetConstantBuffer(1, s.PSCb1);
        if (s.PSSrv != null) _context.PSSetShaderResource(0, s.PSSrv);
        _context.PSSetSampler(0, s.PSSampler);
        _context.RSSetState(s.RS);
        _context.OMSetBlendState(s.Blend);
        _context.OMSetDepthStencilState(s.DSS, s.StencilRef);

        s.InputLayout?.Dispose();
        s.VB?.Dispose();
        s.IB?.Dispose();
        s.VS?.Dispose();
        s.GS?.Dispose();
        s.HS?.Dispose();
        s.DS?.Dispose();
        s.VSCb?.Dispose();
        s.PSCb1?.Dispose();
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
        _savedDsv?.Dispose();     _savedDsv     = null;
        _imageSrv?.Dispose();     _imageSrv     = null;
        _loadedImagePath = string.Empty;
        _sampler?.Dispose();      _sampler      = null;
        _dsReverseZ?.Dispose();   _dsReverseZ   = null;
        _dsNoDepth?.Dispose();    _dsNoDepth    = null;
        _rasterizer?.Dispose();   _rasterizer   = null;
        _blendState?.Dispose();   _blendState   = null;
        _inputLayout?.Dispose();  _inputLayout  = null;
        _ps?.Dispose();           _ps           = null;
        _vs?.Dispose();           _vs           = null;
        _cbCorners?.Dispose();    _cbCorners    = null;
        _cb?.Dispose();           _cb           = null;
        _vb?.Dispose();           _vb           = null;
    }
}
