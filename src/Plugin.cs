using System;
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
    private readonly ScreenRenderer _renderer;
    private readonly MainWindow _mainWindow;

    public Plugin()
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        _renderer   = new ScreenRenderer(GameGui, TextureProvider);
        _mainWindow = new MainWindow(Config, ObjectTable);

        CommandManager.AddHandler(CmdMain, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open FFXIV-TV settings. /fftv place — place screen at player. /fftv hide — toggle visibility."
        });

        PluginInterface.UiBuilder.Draw         += OnDraw;
        PluginInterface.UiBuilder.OpenMainUi   += OnOpenMainUi;

        Log.Info("[FFXIV-TV] Loaded.");
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw         -= OnDraw;
        PluginInterface.UiBuilder.OpenMainUi   -= OnOpenMainUi;
        CommandManager.RemoveHandler(CmdMain);
        _renderer.Dispose();
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
        _renderer.Draw(Config);
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

        // Place the screen 3 units in front of the player.
        float yawRad = player.Rotation; // Dalamud rotation is in radians
        Config.Screen.Center = player.Position + new System.Numerics.Vector3(
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
