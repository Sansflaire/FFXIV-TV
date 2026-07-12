using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Hooking;
using PyonPix.Config.Global.Properties;
using PyonPix.Services.Core;
using PyonPix.Shared.Structs.Pix;
using PyonPix.Shared.Structs.Renderer;
using PyonPix.Structs.Renderer;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using Buffer = SharpDX.Direct3D11.Buffer;
using Configuration = PyonPix.Config.Configuration;
using PixelShader = SharpDX.Direct3D11.PixelShader;
using VertexShader = SharpDX.Direct3D11.VertexShader;

namespace PyonPix.Services.Game;

public class RendererService(Configuration config, IServiceContext services) : BaseService(config, services) {
    private PixService PixService => Services.Get<PixService>();
    private DXService DXService => Services.Get<DXService>();
    private StateService StateService => Services.Get<StateService>();
    private BrowserService BrowserService => Services.Get<BrowserService>();
    private LightService LightService => Services.Get<LightService>();

    private unsafe delegate void OMSetRenderTargetsDelegate(void* context, uint numViews, void** rtvArray, void* depthStencilView);
    private Hook<OMSetRenderTargetsDelegate>? HookOMSetRenderTargets;

    private unsafe delegate int PresentDelegate(void* swapChain, uint syncInterval, uint flags);
    private Hook<PresentDelegate>? HookPresent;

    private readonly unsafe FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device* Device = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance();

    // refs
    // https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-omsetrendertargets
    // https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-drawindexed
    // https://xosh.org/id3d10device-vtable/

    // todo: use https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/Graphics/Render/RenderTargetManager.cs

    private ulong PresentIndex;
    private ulong LastPresentIndex;
    private bool SceneRendered;

    public readonly Dictionary<(nint rtv, nint dsv), ulong> PairCounts = new();
    public readonly Dictionary<nint, RTVItem> RTVCache = new();
    public readonly Dictionary<nint, DSVItem> DSVCache = new();
    public List<nint> DSVPtrs = new();
    public RTVItem? TargetRTV;

    private BlendState BlendS = null!;

    private VertexShader VS = null!;
    private PixelShader PS = null!;
    private SamplerState Sampler = null!;

    private Buffer ShaderParams = null!;

    private Texture2D AvgTexture = null!;
    private RenderTargetView AvgRTV = null!;
    private Texture2D AvgStaging = null!;
    private VertexShader AvgVS = null!;
    private PixelShader AvgPS = null!;

    private readonly Dictionary<string, Renderer> Renderers = [];

    public override async Task Initialize() {
        if(DXService.D3D11Device == null) return;

        VS = await DXService.LoadShader<VertexShader>("vsmain");
        PS = await DXService.LoadShader<PixelShader>("psmain");

        Sampler = new SamplerState(DXService.D3D11Device, new SamplerStateDescription {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp
        });

        ShaderParams = new Buffer(DXService.D3D11Device, Utilities.SizeOf<ShaderParams>(),
        ResourceUsage.Default,
        BindFlags.ConstantBuffer,
        CpuAccessFlags.None,
        ResourceOptionFlags.None,
        0);

        var avgDesc = new Texture2DDescription {
            Width = 16,
            Height = 16,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource
        };
        AvgTexture = new Texture2D(DXService.D3D11Device, avgDesc);
        AvgRTV = new RenderTargetView(DXService.D3D11Device, AvgTexture);
        avgDesc.Usage = ResourceUsage.Staging;
        avgDesc.BindFlags = BindFlags.None;
        avgDesc.CpuAccessFlags = CpuAccessFlags.Read;
        AvgStaging = new Texture2D(DXService.D3D11Device, avgDesc);
        AvgVS = await DXService.LoadShader<VertexShader>("vsavg");
        AvgPS = await DXService.LoadShader<PixelShader>("psavg");

        unsafe {
            var ctxVtbl = *(nint**)DXService.D3D11Context!.NativePointer;
            var rtAddr = ctxVtbl[33];
            HookOMSetRenderTargets = Services.GameInteropProvider.HookFromAddress<OMSetRenderTargetsDelegate>(rtAddr, OMSetRenderTargetsDetour);
            HookOMSetRenderTargets.Enable();

            var swapChainVtbl = *(nint**)DXService.SwapChainPtr;
            var presentAddr = swapChainVtbl[8];
            HookPresent = Services.GameInteropProvider.HookFromAddress<PresentDelegate>(presentAddr, PresentDetour);
            HookPresent.Enable();
        }

        PixService.PixSpawned += OnPixSpawned;
        PixService.PixUpdated += OnPixUpdated;
        PixService.PixDespawned += OnPixDespawned;
        PixService.AllPixDespawned += OnAllPixDespawned;
    }

    private void OnPixSpawned(IPix p, bool isUserAction) {
        if(p == null) return;
        if(Renderers.TryGetValue(p.Id, out var r) && r != null) return;

        r = new Renderer(p.Id);

        RebuildTransform(p, r);
        RebuildProperties(p, r);
        RebuildGlobalProperties(Config.Global.Renderer);

        Renderers[p.Id] = r;
        ClearViews();
    }
    private void OnPixUpdated(PixUpdate u) {
        if(u.Pix == null || !PixService.IsSpawned(u.Pix)) return;
        if(!Renderers.TryGetValue(u.Pix.Id, out var r) || r == null) return;

        switch(u.Type) {
            case PixUpdateType.All or PixUpdateType.RendererTransform or PixUpdateType.RendererProperties:
                RebuildTransform(u.Pix, r);
                RebuildProperties(u.Pix, r);
                break;
        }
    }
    private void OnPixDespawned(IPix p, bool isUserAction) {
        if(p == null) return;
        if(!Renderers.TryGetValue(p.Id, out var r)) return;

        r?.Dispose();
        Renderers.Remove(p.Id);
        ClearViews();
    }
    private void OnAllPixDespawned() {
        ClearViews();
    }

    private void RebuildTransform(IPix p, Renderer r) {
        var props = p.Renderer;
        r.ScreenTransform =
            Matrix4x4.CreateScale(props.Scale) *
            Matrix4x4.CreateFromQuaternion(props.Rotation) *
            Matrix4x4.CreateTranslation(props.Position);
    }
    private void RebuildProperties(IPix p, Renderer r) {
        var props = p.Renderer;
        r.ScreenTint = props.ScreenTint;
        r.EdgeColour = props.EdgeColour;
        r.BackColour = props.BackColour;
        r.BorderColour = props.BorderColour;
        r.BorderWidthH = props.BorderWidthH;
        r.BorderWidthV = props.BorderWidthV;
        r.BorderMode = props.BorderMode;
        r.BorderFeather = props.BorderFeather;
        r.EdgeFeather = props.EdgeFeather;

        r.RasterizerState?.Dispose();
        r.RasterizerState = new RasterizerState(DXService.D3D11Device, new RasterizerStateDescription {
            FillMode = FillMode.Solid,
            CullMode = (int)props.CullMode == (int)SharpDX.Direct3D11.CullMode.Front ? SharpDX.Direct3D11.CullMode.Back : (int)props.CullMode == (int)SharpDX.Direct3D11.CullMode.Back ? SharpDX.Direct3D11.CullMode.Front : SharpDX.Direct3D11.CullMode.None, // reversed
        });

        r.DepthState?.Dispose();
        r.DepthState = new DepthStencilState(DXService.D3D11Device, new DepthStencilStateDescription {
            IsDepthEnabled = props.Depth,
            DepthWriteMask = DepthWriteMask.All,
            DepthComparison = props.DepthComparison == DepthComparison.LessEqual ? Comparison.GreaterEqual : Comparison.LessEqual, // reversed
            IsStencilEnabled = false
        });
        r.DepthOffset = props.DepthOffset;
    }
    public void RebuildGlobalProperties(RendererGlobalProperties r) {
        BlendS?.Dispose();
        BlendStateDescription blendDesc = new BlendStateDescription {
            AlphaToCoverageEnable = r.AlphaToCoverageEnable,
            IndependentBlendEnable = r.IndependentBlendEnable
        };
        blendDesc.RenderTarget[0] = new RenderTargetBlendDescription {
            IsBlendEnabled = r.IsBlendEnabled,
            SourceBlend = r.SourceBlend,
            DestinationBlend = r.DestinationBlend,
            BlendOperation = r.BlendOperation,
            SourceAlphaBlend = r.SourceAlphaBlend,
            DestinationAlphaBlend = r.DestinationAlphaBlend,
            AlphaBlendOperation = r.AlphaBlendOperation,
            RenderTargetWriteMask = r.RenderTargetWriteMask
        };
        BlendS = new BlendState(DXService.D3D11Device, blendDesc);
    }

    public void Update() {
        if(Renderers.Count == 0) return;

        // todo: move this
        BrowserService.UpdateSpatialAudio(Renderers.Values);
    }

    public void ClearViews() {
        PresentIndex = 0;
        LastPresentIndex = 0;
        SceneRendered = false;
        TargetRTV = null;
        ScoredRTVItems.Clear();
        DSVCache.Clear();
        DSVPtrs.Clear();
        RTVPtrs.Clear();
        RTVCache.Clear();
        PairCounts.Clear();
    }

    private static Format GetSRVFormatForDSV(Format fmt) {
        switch(fmt) {
            case Format.R16_Typeless: return Format.R16_UNorm;
            case Format.R24G8_Typeless: return Format.R24_UNorm_X8_Typeless;
            case Format.R32_Typeless: return Format.R32_Float;
            case Format.R32G8X24_Typeless: return Format.R32_Float_X8X24_Typeless;
            default: return fmt;
        }
    }

    private bool TryCreateDSVItem(nint curDSVPtr, int deviceWidth, int deviceHeight) {
        if(curDSVPtr == nint.Zero) return false;
        if(DSVPtrs.Contains(curDSVPtr)) return false;
        if(DSVCache.TryGetValue(curDSVPtr, out var _)) return false;
        DSVPtrs.Add(curDSVPtr);

        var dsv = new DepthStencilView(curDSVPtr);
        var item = new DSVItem(dsv);

        try {
            item.Texture = item.DSV.Resource.QueryInterface<Texture2D>();
            item.Desc = item.Texture.Description;
        } catch(System.Exception ex) {
            return false;
        }
        if(deviceWidth != item.Desc.Width && deviceHeight != item.Desc.Height) {
            return false;
        }
        if((item.Desc.BindFlags & BindFlags.ShaderResource) == 0) {
            return false;
        }
        if(item.Desc.Format != Format.R24G8_Typeless) return false;

        var srvFormat = GetSRVFormatForDSV(item.Desc.Format);
        var srvDesc = new ShaderResourceViewDescription {
            Format = srvFormat,
            Dimension = ShaderResourceViewDimension.Texture2D,
            Texture2D = new ShaderResourceViewDescription.Texture2DResource {
                MipLevels = item.Desc.MipLevels == 0 ? 1 : item.Desc.MipLevels,
                MostDetailedMip = 0
            }
        };

        DSVCache[curDSVPtr] = item;
        return true;
    }

    public List<nint> RTVPtrs = new();
    private unsafe bool TryCreateRTVItem(nint rtvPtr) {
        if(rtvPtr == nint.Zero) return false;
        if(RTVCache.TryGetValue(rtvPtr, out var _)) return false;

        if(!TryGetTexture2DDescFromView(rtvPtr, out uint w, out uint h, out uint mipLevels, out uint arraySize, out Format format, out SampleDesc sample, out BindFlags bindFlags, out uint miscFlags)) return false;
        if(Device->Width != w || Device->Height != h) return false;
        if(!ValidFormats.Contains(format)) return false;

        var rtv = new RenderTargetView(rtvPtr);
        var item = new RTVItem(rtv);
        item.Index = RTVCache.Count;
        item.Width = (int)w;
        item.Height = (int)h;
        item.Format = format;
        item.IsBound = (bindFlags & BindFlags.ShaderResource) == 0;
        RTVCache[rtvPtr] = item;

        if(!RTVPtrs.Contains(rtvPtr)) RTVPtrs.Add(rtvPtr);

        return true;
    }

    private readonly HashSet<Format> ValidFormats = new() {
        Format.R8G8B8A8_UNorm,
        Format.R8G8B8A8_Typeless,
        Format.B8G8R8A8_UNorm,
        Format.B8G8R8A8_Typeless,
        Format.R16G16B16A16_UNorm,
        Format.R16G16B16A16_Typeless,
        Format.R16G16B16A16_Float
    };

    private readonly object RtvLock = new();
    private readonly HashSet<Format> PrefTypeless = new() {
        Format.R8G8B8A8_Typeless,
        Format.B8G8R8A8_Typeless
    };
    private readonly HashSet<Format> PrefUnorm = new() {
        Format.R8G8B8A8_UNorm,
        Format.B8G8R8A8_UNorm
    };

    public class ScoredRTVItem(RTVItem rtv, double score) {
        public RTVItem RTV = rtv;
        public double Score = score;
    }

    public List<ScoredRTVItem> ScoredRTVItems = new();
    private RTVItem? SelectBestRTV(int deviceWidth, int deviceHeight, ulong presentIndex) {
        lock(RtvLock) {
            var entries = RTVCache.Values.Where(r => r != null && r.RTV.NativePointer != nint.Zero).ToList();
            if(entries.Count == 0) return null;

            var s = entries.Select(r => {
                double v = 0.0;

                v += Math.Sqrt(Math.Max(0, r.Calls)) * 10;

                if(r.LastPresent == presentIndex) {
                    v += 300;
                } else {
                    v += Math.Max(0, 100 - (int)(presentIndex - r.LastPresent));
                }

                var fmt = r.Format; // r.Desc.Format;
                var rFormat = Config.Global.Renderer.Format;
                if(rFormat != FormatType.Auto) {
                    if((int)rFormat == (int)fmt) {
                        v += 1000;
                    } else {
                        v = 0;
                    }
                } else {
                    if(PrefUnorm.Contains(fmt)) v += 500;
                    else if(PrefTypeless.Contains(fmt)) v += 550;
                    else if(fmt == Format.R16G16B16A16_Float) v += 450;
                    else v += 100;
                }

                var rBinding = Config.Global.Renderer.ResourceBindingType;
                if(rBinding != ResourceBindingType.Auto) {
                    var rBound = rBinding == ResourceBindingType.Bound;
                    if(!r.IsBound) {
                        v = rBound ? v + 1000 : 0;
                    } else {
                        v = !rBound ? v + 1000 : 0;
                    }
                }

                return new ScoredRTVItem(r, v);
            }).OrderByDescending(x => x.Score).ToList();

            ScoredRTVItems = s;
            return s.First().RTV;
        }
    }

    private SharpDX.DXGI.SwapChain? DxgiSwapChain;
    private unsafe void EnsureSwapChain() {
        var ptr = Device->SwapChain->DXGISwapChain;

        if(DxgiSwapChain == null || DxgiSwapChain.NativePointer != (nint)ptr) {
            DxgiSwapChain = new SharpDX.DXGI.SwapChain((nint)ptr);
        }
    }

    private bool ResizeInProgress = false;
    private nint LastSwapChainPtr;
    private nint LastBackBufferPtr;
    private bool IsFullScreen;
    private bool IsWindowed;
    private unsafe void OMSetRenderTargetsDetour(void* context, uint numViews, void** rtvArray, void* depthStencilView) {
        HookOMSetRenderTargets!.Original(context, numViews, rtvArray, depthStencilView);

        if(!StateService.LocalPlayerExists) { ClearViews(); return; }

        if(ResizeInProgress) {
            if(numViews > 0 && depthStencilView != null) {
                ClearViews();
                ResizeInProgress = false;
            }
            return;
        }

        if(LastSwapChainPtr != (nint)Device->SwapChain) {
            LastSwapChainPtr = (nint)Device->SwapChain;
            ResizeInProgress = true;
            return;
        }
        var backBufferPtr = (nint)Device->SwapChain->BackBuffer;
        if(LastBackBufferPtr != backBufferPtr) {
            LastBackBufferPtr = backBufferPtr;
            ResizeInProgress = true;
            return;
        }
        if(TargetRTV != null && (TargetRTV.Width != Device->Width || TargetRTV.Height != Device->Height)) {
            ResizeInProgress = true;
            return;
        }

        EnsureSwapChain();
        if(DxgiSwapChain != null) {
            RawBool isFullscreen = new(false);
            DxgiSwapChain.GetFullscreenState(out isFullscreen, out _);
            if(IsFullScreen != isFullscreen) {
                IsFullScreen = isFullscreen;
                ResizeInProgress = true;
                return;
            }

            var desc = DxgiSwapChain.Description;
            if(IsWindowed != desc.IsWindowed) {
                IsWindowed = desc.IsWindowed;
                ResizeInProgress = true;
                return;
            }
        }

        if(numViews == 0) return;

        var curDSVPtr = (nint)depthStencilView;

        bool isCurRtv = false;
        for(int i = 0; i < numViews; i++) {
            var rtvPtr = (nint)rtvArray[i];

            if(rtvPtr != nint.Zero) {
                if(curDSVPtr != nint.Zero) {
                    if(!PairCounts.ContainsKey((rtvPtr, curDSVPtr))) {
                        PairCounts.Add((rtvPtr, curDSVPtr), 0);
                    }
                    PairCounts[(rtvPtr, curDSVPtr)]++;
                }

                if(TryCreateRTVItem(rtvPtr)) {
                    var selectedRtv = SelectBestRTV((int)Device->Width, (int)Device->Height, PresentIndex);
                    if(selectedRtv != null) {
                        TargetRTV = selectedRtv;
                    }
                }

                if(RTVCache.TryGetValue(rtvPtr, out var rtv)) {
                    rtv.LastPresent = PresentIndex;
                    rtv.Calls++;
                }

                isCurRtv = TargetRTV?.RTV.NativePointer == rtvPtr;
            }
        }
        
        if(curDSVPtr != nint.Zero) {
            TryCreateDSVItem(curDSVPtr, (int)Device->Width, (int)Device->Height);
        }
        
        if(LastPresentIndex != PresentIndex && RTVPtrs.Count > 0 && PairCounts.Count > 0) {
            bool found = false;
            if(Config.Global.Renderer.UseShaderTarget) {
                TargetRTV = RTVCache.MaxBy(x => x.Value.Calls).Value;
            } else {
                var pairs = PairCounts.OrderByDescending(x => x.Value).ToList();
                for(int i = RTVPtrs.Count - 1; i >= 0; i--) {
                    var rtvPtr = RTVPtrs[i];
                    foreach(var pair in pairs) {
                        if(pair.Key.rtv != rtvPtr || pair.Value == 0) continue;
                        if(!RTVCache.TryGetValue(rtvPtr, out var rtv)) continue;

                        var bindingMatch = true;
                        var formatMatch = true;
                        var rBinding = Config.Global.Renderer.ResourceBindingType;
                        if(rBinding != ResourceBindingType.Auto) {
                            var rBound = rBinding == ResourceBindingType.Bound;
                            if((!rtv.IsBound && !rBound) || (rtv.IsBound && rBound)) bindingMatch = false;
                        }
                        if(Config.Global.Renderer.Format != FormatType.Auto) {
                            if((int)Config.Global.Renderer.Format != (int)rtv.Format) formatMatch = false;
                        }
                        if(!bindingMatch || !formatMatch) continue;

                        TargetRTV = rtv;
                        found = true;
                        break;
                    }
                    if(found) break;
                }
            }
        }
        
        if(Config.Global.Renderer.RenderMode == RenderMode.PreDraw) {
            SceneRendered = DSVCache.ContainsKey(curDSVPtr) && isCurRtv;
        }
        if(SceneRendered && LastPresentIndex != PresentIndex) {
            LastPresentIndex = PresentIndex;
            try {
                Draw();
            } catch(System.Exception ex) {
                Services.Log.Error($"Draw Failed: {ex}");
            }
        }
        if(Config.Global.Renderer.RenderMode == RenderMode.PostDraw) {
            SceneRendered = DSVCache.ContainsKey(curDSVPtr) && isCurRtv;
        }
    }

    struct Texture2DDesc {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public int Format;
        public SampleDesc SampleDesc;
        public uint Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
    }

    struct SampleDesc {
        public uint Count;
        public uint Quality;
    }

    private unsafe bool TryGetTexture2DDesc(nint texturePtr, out uint width, out uint height, out uint mipLevels, out uint arraySize, out Format format, out SampleDesc sample, out BindFlags bindFlags, out uint miscFlags) {
        width = 0;
        height = 0;
        mipLevels = 0;
        arraySize = 0;
        format = Format.Unknown;
        sample = new();
        bindFlags = 0;
        miscFlags = 0;

        if(texturePtr == nint.Zero)
            return false;

        var vtbl = *(nint**)texturePtr;
        if(vtbl == null)
            return false;

        // ID3D11Texture2D::GetDesc is vtable slot 10
        var getDescPtr = vtbl[10];
        if(getDescPtr == nint.Zero)
            return false;

        var getDesc = (delegate* unmanaged<void*, Texture2DDesc*, void>)getDescPtr;

        Texture2DDesc desc;
        getDesc((void*)texturePtr, &desc);

        width = desc.Width;
        height = desc.Height;
        mipLevels = desc.MipLevels;
        arraySize = desc.ArraySize;
        format = (Format)desc.Format;
        sample = desc.SampleDesc;
        bindFlags = (BindFlags)desc.BindFlags;
        miscFlags = desc.MiscFlags;

        return true;
    }

    private static unsafe bool TryGetTexture2DDescFromView(nint viewPtr, out uint width, out uint height, out uint mipLevels, out uint arraySize, out Format format, out SampleDesc sample, out BindFlags bindFlags, out uint miscFlags) {
        width = 0;
        height = 0;
        mipLevels = 0;
        arraySize = 0;
        format = Format.Unknown;
        sample = new();
        bindFlags = 0;
        miscFlags = 0;

        if(viewPtr == nint.Zero) return false;

        // GetResource
        var viewVtbl = *(nint**)viewPtr;
        if(viewVtbl == null) return false;
        var getResource = (delegate* unmanaged<void*, void**, void>)viewVtbl[7];

        void* resource = null;
        getResource((void*)viewPtr, &resource);

        if(resource == null)
            return false;

        var resourcePtr = (nint)resource;

        // QueryInterface for ID3D11Texture2D
        Guid iidTexture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

        var resVtbl = *(nint**)resourcePtr;
        var queryInterface = (delegate* unmanaged<void*, Guid*, void**, int>)resVtbl[0];

        void* tex2D = null;
        int hr;

        hr = queryInterface((void*)resourcePtr, &iidTexture2D, &tex2D);

        // Release resource from GetResource
        var release = (delegate* unmanaged<void*, uint>)resVtbl[2];
        release((void*)resourcePtr);

        if(hr != 0 || tex2D == null)
            return false;

        // Call GetDesc on ID3D11Texture2D
        var texVtbl = *(nint**)tex2D;

        var getDesc = (delegate* unmanaged<void*, Texture2DDesc*, void>)texVtbl[10];

        Texture2DDesc desc;
        getDesc(tex2D, &desc);

        width = desc.Width;
        height = desc.Height;
        mipLevels = desc.MipLevels;
        arraySize = desc.ArraySize;
        format = (Format)desc.Format;
        sample = desc.SampleDesc;
        bindFlags = (BindFlags)desc.BindFlags;
        miscFlags = desc.MiscFlags;

        var texRelease = (delegate* unmanaged<void*, uint>)texVtbl[2];
        texRelease(tex2D);

        return true;
    }

    private static unsafe nint GetResourcePointerFromView(nint viewPtr) {
        if(viewPtr == nint.Zero) return nint.Zero;
        var vtable = *(nint**)viewPtr;
        if(vtable == null) return nint.Zero;
        var getResourcePtr = vtable[7];
        if(getResourcePtr == 0) return nint.Zero;
        var getResource = (delegate* unmanaged<void*, void**, void>)getResourcePtr;

        void* resource = null;
        getResource((void*)viewPtr, &resource);
        if(resource == null) return nint.Zero;
        var resPtr = (nint)resource;

        // resource->Release()
        var resVtable = *(nint**)resPtr;
        if(resVtable != null) {
            var releasePtr = resVtable[2];
            if(releasePtr != 0) {
                var release = (delegate* unmanaged<void*, uint>)releasePtr;
                _ = release((void*)resPtr);
            }
        }

        return resPtr;
    }

    private unsafe int PresentDetour(void* swapChain, uint syncInterval, uint flags) {
        PresentIndex++;

        return HookPresent!.Original(swapChain, syncInterval, flags);
    }

    private void Draw() {
        try {
            if(BrowserService.State == Structs.Browser.BrowserState.Stopping) {
                BrowserService.InvokeShutdown(); // todo: remove this
                return;
            }
            if(BrowserService.State != Structs.Browser.BrowserState.Running) return;
            if(DXService.D3D11Device == null || DXService.D3D11Context == null || DXService.DXGISwapChain == null) return;
            if(DXService.D3D11Device.DeviceRemovedReason != Result.Ok) return;
            if(TargetRTV == null || DSVCache.Count == 0) return;
            if(Renderers.Count == 0) return;
            if(BrowserService.Tabs.Count == 0) return;

            var ctx = DXService.D3D11Context;

            var prevViewport = ctx.Rasterizer.GetViewports<Viewport>()[0];
            var prevRTVs = ctx.OutputMerger.GetRenderTargets(1, out var prevDSV); // bad

            var prevRS = ctx.Rasterizer.State;
            var prevBlend = ctx.OutputMerger.BlendState;
            var prevDSS = ctx.OutputMerger.DepthStencilState;

            var prevVS = ctx.VertexShader.Get();
            var prevPS = ctx.PixelShader.Get();
            var prevIL = ctx.InputAssembler.InputLayout;
            var prevTopo = ctx.InputAssembler.PrimitiveTopology;

            try {
                var camView = Matrix4x4.Transpose(CameraService.GetViewMatrix());
                var camProj = Matrix4x4.Transpose(CameraService.GetProjectionMatrix());
                foreach(var r in Renderers.Values) {
                    if(!BrowserService.Tabs.TryGetValue(r.PixId, out var t)) continue;
                    if(t.SRV == null) continue;
                    if(DrawRenderer(ctx, r, t.SRV, camView, camProj)) { // prevViewport
                        var screenAvg = ComputeLight(ctx, r, t.SRV);
                        LightService.UpdateById(r.PixId, screenAvg);
                    }
                }
            } finally {
                ctx.Rasterizer.SetViewport(prevViewport);
                ctx.OutputMerger.SetRenderTargets(prevDSV, prevRTVs);
                if(prevRTVs.Length > 0) prevRTVs[0]?.Dispose();
                prevDSV?.Dispose();

                ctx.Rasterizer.State = prevRS; prevRS?.Dispose();
                ctx.OutputMerger.SetBlendState(prevBlend); prevBlend?.Dispose();
                ctx.OutputMerger.SetDepthStencilState(prevDSS); prevDSS?.Dispose();

                ctx.VertexShader.Set(prevVS); prevVS?.Dispose();
                ctx.PixelShader.Set(prevPS); prevPS?.Dispose();
                ctx.InputAssembler.InputLayout = prevIL; prevIL?.Dispose();
                ctx.InputAssembler.PrimitiveTopology = prevTopo;
            }
        } catch(System.InvalidOperationException) {
        } catch(System.Exception ex) {
            Services.Log.Error($"Renderer Failed: {ex}");
        }
    }

    private bool DrawRenderer(DeviceContext ctx, Renderer r, ShaderResourceView srv, Matrix4x4 camView, Matrix4x4 camProj) {
        try {
            var dsvMode = Config.Global.Renderer.DepthMode;
            if(dsvMode != DepthMode.Auto) {
                var dsvList = DSVCache.ToList();
                var dsvCount = DSVCache.Count;
                if(dsvMode == DepthMode.First || dsvCount == 1) {
                    if(!DrawRenderer(dsvList[0].Value.DSV, ctx, r, srv, camView, camProj)) return false;
                } else {
                    if(!DrawRenderer(dsvList[1].Value.DSV, ctx, r, srv, camView, camProj)) return false;
                }
            } else {
                foreach(var dsv in DSVCache) {
                    if(!DrawRenderer(dsv.Value.DSV, ctx, r, srv, camView, camProj)) return false;
                }
            }
        } catch(System.Exception ex) {
            Services.Log.Error($"DrawRenderer Failed: {ex}");
            return false;
        }
        return true;
    }

    private unsafe bool DrawRenderer(DepthStencilView dsv, DeviceContext ctx, Renderer r, ShaderResourceView srv, Matrix4x4 camView, Matrix4x4 camProj) { // , Viewport prevViewport
        try {
            if(Device == null) return false;
            if(dsv == null || TargetRTV == null || TargetRTV?.RTV == null || srv == null) return false;
            if(TargetRTV.Width != Device->Width || TargetRTV.Height != Device->Height) return false;
            if(r.ScreenTransform == null) return false;

            ctx.OutputMerger.SetRenderTargets(dsv, TargetRTV.RTV);
            ctx.Rasterizer.SetViewport(new Viewport(0, 0, TargetRTV.Width, TargetRTV.Height, 0, 1)); // prevViewport.MinDepth, prevViewport.MaxDepth

            ctx.InputAssembler.InputLayout = null;
            ctx.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
            //ctx.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(null, 0, 0));
            //ctx.InputAssembler.SetIndexBuffer(null, Format.Unknown, 0);

            ctx.Rasterizer.State = r.RasterizerState;
            ctx.OutputMerger.SetBlendState(BlendS);
            ctx.OutputMerger.SetDepthStencilState(r.DepthState);

            var cbData = new ShaderParams {
                CameraView = camView,
                CameraProjection = camProj,
                ScreenTransform = Matrix4x4.Transpose(r.ScreenTransform.Value),
                ScreenTint = r.ScreenTint,
                EdgeColour = r.EdgeColour,
                BackColour = r.BackColour,
                BorderColour = r.BorderColour,
                BorderWidthH = r.BorderWidthH,
                BorderWidthV = r.BorderWidthV,
                BorderMode = (int)r.BorderMode,
                BorderFeather = r.BorderFeather,
                EdgeFeather = r.EdgeFeather,
                DepthOffset = r.DepthOffset
            };
            ctx.UpdateSubresource(ref cbData, ShaderParams);

            ctx.VertexShader.Set(VS);
            ctx.VertexShader.SetConstantBuffer(0, ShaderParams);

            ctx.PixelShader.Set(PS);
            ctx.PixelShader.SetShaderResource(0, srv);
            ctx.PixelShader.SetSampler(0, Sampler);
            ctx.PixelShader.SetConstantBuffer(0, ShaderParams);

            ctx.Draw(36, 0);

            ctx.PixelShader.SetShaderResource(0, null);
        } catch(System.Exception ex) {
            Services.Log.Error($"DrawRenderer Failed: {ex}");
            return false;
        }
        return true;
    }

    private System.Numerics.Vector3? ComputeLight(DeviceContext ctx, Renderer r, ShaderResourceView srv) {
        if(srv == null || AvgRTV == null || AvgStaging == null) return null;

        ctx.OutputMerger.SetRenderTargets(AvgRTV);
        ctx.Rasterizer.SetViewport(new Viewport(0, 0, 16, 16));

        ctx.InputAssembler.InputLayout = null;
        ctx.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;

        ctx.OutputMerger.SetDepthStencilState(null);

        var rsDesc = r.RasterizerState!.Description;
        var avgRS = new RasterizerState(DXService.D3D11Device, new RasterizerStateDescription {
            FillMode = rsDesc.FillMode,
            CullMode = SharpDX.Direct3D11.CullMode.Back
        });
        ctx.Rasterizer.State = avgRS;

        ctx.VertexShader.Set(AvgVS);
        ctx.PixelShader.Set(AvgPS);

        ctx.PixelShader.SetShaderResource(0, srv);
        ctx.PixelShader.SetSampler(0, Sampler);

        ctx.Draw(3, 0);

        ctx.PixelShader.SetShaderResource(0, null);

        avgRS.Dispose();

        ctx.CopyResource(AvgTexture, AvgStaging);
        var dataBox = ctx.MapSubresource(AvgStaging, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
        var screenAvg = System.Numerics.Vector3.Zero;
        unsafe {
            float totalR = 0, totalG = 0, totalB = 0;
            byte* ptr = (byte*)dataBox.DataPointer;
            for(int y = 0; y < 16; y++) {
                byte* row = ptr + (y * dataBox.RowPitch);
                for(int x = 0; x < 16; x++) {
                    totalR += row[(x * 4) + 0] / 255f;
                    totalG += row[(x * 4) + 1] / 255f;
                    totalB += row[(x * 4) + 2] / 255f;
                }
            }
            screenAvg = new System.Numerics.Vector3(totalR / 256f, totalG / 256f, totalB / 256f);
        }
        ctx.UnmapSubresource(AvgStaging, 0);

        return screenAvg;
    }

    private void DespawnAll() {
        foreach(var r in Renderers.Values)
            r?.Dispose();

        Renderers.Clear();
    }

    public override Task Dispose() {
        PixService.PixSpawned -= OnPixSpawned;
        PixService.PixUpdated -= OnPixUpdated;
        PixService.PixDespawned -= OnPixDespawned;
        PixService.AllPixDespawned -= OnAllPixDespawned;

        HookOMSetRenderTargets?.Disable();
        HookOMSetRenderTargets?.Dispose();
        HookPresent?.Disable();
        HookPresent?.Dispose();

        DespawnAll();

        VS?.Dispose();
        PS?.Dispose();
        Sampler?.Dispose();
        ShaderParams?.Dispose();
        BlendS?.Dispose();
        return Task.CompletedTask;
    }
}
