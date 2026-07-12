using System;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using PyonPix.Config;
using PyonPix.Services;
using PyonPix.Ui;

namespace PyonPix;

public class Plugin : IAsyncDalamudPlugin {
    public const string Name = "PyonPix";
    public static Version Version { get; private set; } = null!;

    private readonly Configuration Config = null!;
    private readonly ServiceContext Services = null!;
    private readonly WindowContext Windows = null!;

    public Plugin(IDalamudPluginInterface pi) {
        Services = new(pi);
        Windows = new();
        Version = Services.PluginInterface.Manifest.AssemblyVersion;
        Config = Services.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Config.Initialize(Services.PluginInterface);
    }

    public async Task LoadAsync(CancellationToken cancellationToken) {
        await Services.Initialize(Config);
        UIShared.Initialize(Config, Services);
        Windows.Initialize(Config, Services);
    }

    public async ValueTask DisposeAsync() {
        await Services.Dispose();
        Windows.Dispose();
        UIShared.Dispose();
    }
}
