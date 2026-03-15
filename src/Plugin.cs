using System;
using System.Numerics;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVTv.Windows;

namespace FFXIVTv;

public sealed class Plugin : IDalamudPlugin
{
    // ─── Injected services ───────────────────────────────────────────────────
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static IPluginLog              Log             { get; private set; } = null!;
    [PluginService] internal static ICommandManager         CommandManager  { get; private set; } = null!;
    [PluginService] internal static IGameGui                GameGui         { get; private set; } = null!;
    [PluginService] internal static ITextureProvider        TextureProvider { get; private set; } = null!;
    [PluginService] internal static IObjectTable            ObjectTable     { get; private set; } = null!;
    [PluginService] internal static IChatGui                ChatGui         { get; private set; } = null!;

    private const string CmdMain = "/fftv";

    // ─── Plugin state ────────────────────────────────────────────────────────
    internal Configuration Config { get; }

    // Phase 1: ImGui overlay (always available, no depth)
    private readonly ScreenRenderer _screenRenderer;

    // Phase 2: D3D11 world-space with depth (initialized on first draw frame)
    private readonly D3DRenderer _d3dRenderer;

    private readonly MainWindow _mainWindow;

    public Plugin()
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        _screenRenderer = new ScreenRenderer(GameGui, TextureProvider);
        _d3dRenderer    = new D3DRenderer();
        _mainWindow     = new MainWindow(Config, ObjectTable);

        CommandManager.AddHandler(CmdMain, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open FFXIV-TV settings. /fftv place — place at player. /fftv hide — toggle."
        });

        PluginInterface.UiBuilder.Draw       += OnDraw;
        PluginInterface.UiBuilder.OpenMainUi += OnOpenMainUi;

        Log.Info("[FFXIV-TV] Loaded.");
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw       -= OnDraw;
        PluginInterface.UiBuilder.OpenMainUi -= OnOpenMainUi;
        CommandManager.RemoveHandler(CmdMain);
        _d3dRenderer.Dispose();
        _screenRenderer.Dispose();
        Config.Save();
        Log.Info("[FFXIV-TV] Unloaded.");
    }

    // ─── Command handler ─────────────────────────────────────────────────────

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "":
                _mainWindow.IsVisible = !_mainWindow.IsVisible;
                break;
            case "place":
                PlaceAtPlayer();
                break;
            case "hide":
            case "toggle":
                Config.Screen.Visible = !Config.Screen.Visible;
                Config.Save();
                ChatGui.Print($"[FFXIV-TV] Screen {(Config.Screen.Visible ? "shown" : "hidden")}.");
                break;
            default:
                ChatGui.PrintError($"[FFXIV-TV] Unknown argument '{args}'. Use /fftv, /fftv place, /fftv hide.");
                break;
        }
    }

    // ─── Draw ────────────────────────────────────────────────────────────────

    private void OnDraw()
    {
        _mainWindow.Draw();

        var screen = Config.Screen;
        if (!screen.Visible) return;

        // Try to initialize the D3D11 renderer on the first draw frame
        // (device isn't available until after Dalamud's ImGui init completes).
        if (!_d3dRenderer.IsAvailable)
            _d3dRenderer.TryInitialize();

        if (_d3dRenderer.IsAvailable)
        {
            // Phase 2: proper depth-tested world-space geometry.
            // Get texture from Phase 1 loader (reuse same loading infrastructure).
            var wrap = _screenRenderer.GetCurrentWrap(Config);
            if (wrap != null)
                _d3dRenderer.Draw(screen, wrap.Handle.Handle);
            else
                _screenRenderer.DrawPlaceholder(Config); // purple rect placeholder, no depth
        }
        else
        {
            // Phase 1 fallback: ImGui overlay (no depth testing).
            _screenRenderer.Draw(Config);
        }
    }

    private void OnOpenMainUi() => _mainWindow.IsVisible = true;

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private void PlaceAtPlayer()
    {
        var player = ObjectTable.LocalPlayer;
        if (player == null)
        {
            ChatGui.PrintError("[FFXIV-TV] No player found.");
            return;
        }

        float yawRad = player.Rotation;
        Config.Screen.Center = player.Position + new Vector3(
            MathF.Sin(yawRad) * 3f,
            1.5f,
            MathF.Cos(yawRad) * 3f
        );
        Config.Screen.YawDegrees = yawRad * (180f / MathF.PI);
        Config.Screen.Visible = true;
        Config.Save();

        ChatGui.Print("[FFXIV-TV] Screen placed in front of you.");
    }
}
