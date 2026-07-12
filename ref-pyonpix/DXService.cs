using System.Reflection;
using System.Threading.Tasks;
using PyonPix.Config;
using PyonPix.Extensions;
using PyonPix.Shared.Structs.Renderer;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D11;
using SwapChain = SharpDX.DXGI.SwapChain;

namespace PyonPix.Services.Game;

public class DXService(Configuration config, IServiceContext services) : BaseService(config, services) {
    public SwapChain? DXGISwapChain { get; private set; }
    public Device? D3D11Device { get; private set; }
    public DeviceContext? D3D11Context { get; private set; }
    public nint SwapChainPtr { get; private set; }
    public LUID Luid { get; private set; }

    public unsafe override Task Initialize() {
        var device = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance();
        SwapChainPtr = (nint)device->SwapChain->DXGISwapChain;
        DXGISwapChain = SwapChain.FromPointer<SwapChain>(SwapChainPtr);
        D3D11Device = DXGISwapChain.GetDevice<Device>();
        D3D11Context = D3D11Device.ImmediateContext;
        var dxgi = D3D11Device.QueryInterface<SharpDX.DXGI.Device>();
        Luid = dxgi.Adapter.Description.Luid.ToLUID();
        return Task.CompletedTask;
    }

    public async Task<T> LoadShader<T>(string resourceName) where T : class {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"{Plugin.Name}.Shaders.{resourceName}.cso");
        byte[] bytes = new byte[stream!.Length];
        await stream.ReadExactlyAsync(bytes);
        using var bytecode = new ShaderBytecode(bytes);
        return typeof(T) == typeof(VertexShader) ? new VertexShader(D3D11Device, bytecode) as T : new PixelShader(D3D11Device, bytecode) as T;
    }
}
