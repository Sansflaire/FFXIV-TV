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
    [PluginService] internal static IGameInteropProvider    GameInterop     { get; private set; } = null!;

    private const string CmdMain = "/fftv";

    // ─── Plugin state ────────────────────────────────────────────────────────
    internal Configuration Config { get; }

    // Phase 1: ImGui overlay (always available, no depth)
    private readonly ScreenRenderer _screenRenderer;

    // Phase 2: D3D11 world-space with depth (initialized on first draw frame)
    private readonly D3DRenderer _d3dRenderer;

    // Phase 3: Video playback via LibVLC
    private readonly VideoPlayer _videoPlayer;
    private bool _videoSetupDone;

    private readonly MainWindow _mainWindow;

    public Plugin()
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        _screenRenderer = new ScreenRenderer(GameGui, TextureProvider);
        _d3dRenderer    = new D3DRenderer(GameInterop);
        _videoPlayer    = new VideoPlayer(PluginInterface.AssemblyLocation.DirectoryName!);
        _mainWindow     = new MainWindow(Config, ObjectTable);
        _mainWindow.SetVideoPlayer(_videoPlayer);

        CommandManager.AddHandler(CmdMain, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open FFXIV-TV settings. /fftv place — place at player. /fftv hide — toggle. /fftv play <path> — play video. /fftv pause — pause/resume. /fftv stop — stop video."
        });

        PluginInterface.UiBuilder.DisableUserUiHide = true;

        PluginInterface.UiBuilder.Draw       += OnDraw;
        PluginInterface.UiBuilder.OpenMainUi += OnOpenMainUi;

        Log.Info("[FFXIV-TV] Loaded.");
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw       -= OnDraw;
        PluginInterface.UiBuilder.OpenMainUi -= OnOpenMainUi;
        CommandManager.RemoveHandler(CmdMain);
        _videoPlayer.Dispose();
        _d3dRenderer.Dispose();
        _screenRenderer.Dispose();
        Config.Save();
        Log.Info("[FFXIV-TV] Unloaded.");
    }

    // ─── Command handler ─────────────────────────────────────────────────────

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim();
        var lower   = trimmed.ToLowerInvariant();

        if (lower.StartsWith("play "))
        {
            var path = trimmed.Substring(5).Trim();
            bool isUrl = path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                         path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            Config.ActiveMode = isUrl ? ContentMode.UrlVideo : ContentMode.LocalVideo;
            if (isUrl) Config.VideoUrl  = path;
            else       Config.VideoPath = path;
            Config.Save();
            _videoPlayer.Play(path);
            return;
        }

        switch (lower)
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
            case "pause":
                _videoPlayer.TogglePause();
                break;
            case "stop":
                _videoPlayer.Stop();
                break;
            default:
                ChatGui.PrintError($"[FFXIV-TV] Unknown argument '{args}'. Use /fftv, /fftv place, /fftv hide, /fftv play <path>, /fftv pause, /fftv stop.");
                break;
        }
    }

    // ─── Draw ────────────────────────────────────────────────────────────────

    private void OnDraw()
    {
        // Settings window respects the user's UI hide (Scroll Lock).
        // The world-space screen always renders because DisableUserUiHide = true.
        if (PluginInterface.UiBuilder.ShouldModifyUi)
            _mainWindow.Draw();

        var screen = Config.Screen;
        if (!screen.Visible) return;

        // Phase 1 sandbox: force the original WorldToScreen + ImGui path.
        // Useful for comparing Phase 1 image quality vs Phase 2.
        if (Config.UsePhase1Sandbox)
        {
            _screenRenderer.Draw(Config);
            return;
        }

        // Try to initialize the D3D11 renderer on the first draw frame
        // (device isn't available until after Dalamud's ImGui init completes).
        if (!_d3dRenderer.IsAvailable)
            _d3dRenderer.TryInitialize();

        // Wire VideoPlayer to the D3D device once (first frame after D3D init).
        if (_d3dRenderer.IsAvailable && !_videoSetupDone && _d3dRenderer.Device != null)
        {
            _videoPlayer.SetDevice(_d3dRenderer.Device);
            _d3dRenderer.SetVideoPlayer(_videoPlayer);
            _videoSetupDone = true;
        }

        // Keep yt-dlp path current (cheap property set, user may update via UI at any time).
        _videoPlayer.YtDlpPath = Config.YtDlpPath;

        if (_d3dRenderer.IsAvailable)
        {
            // Only load the image texture when in Image mode.
            _d3dRenderer.SetImagePath(Config.ActiveMode == ContentMode.Image ? Config.ImagePath : string.Empty);

            // In Image mode: show the ImGui placeholder when no image is loaded yet.
            // In video modes: ALWAYS call D3DRenderer.Draw() so UploadFrame() runs each tick
            // and can create the GPU texture on the first decoded frame. Draw() returns early
            // if no SRV is ready, which is fine — nothing renders until the first frame arrives.
            // Never fall back to the ImGui placeholder in video mode: it has no depth testing.
            if (Config.ActiveMode == ContentMode.Image && !_d3dRenderer.HasTexture)
                _screenRenderer.DrawPlaceholder(Config);
            else
                _d3dRenderer.Draw(screen);
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
