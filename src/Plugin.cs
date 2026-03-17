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
    private readonly VideoPlayer      _videoPlayer;
    private bool _videoSetupDone;

    // Phase 3.7: Browser mode via WebView2
    private readonly BrowserPlayer    _browserPlayer;
    private bool _browserSetupDone;

    // Phase 4: Network sync (host/client)
    private readonly SyncCoordinator _sync;

    private readonly MainWindow _mainWindow;

    public Plugin()
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        _screenRenderer = new ScreenRenderer(GameGui, TextureProvider);
        _d3dRenderer    = new D3DRenderer(GameInterop);
        _videoPlayer    = new VideoPlayer(PluginInterface.AssemblyLocation.DirectoryName!);
        _browserPlayer  = new BrowserPlayer(PluginInterface.AssemblyLocation.DirectoryName!);
        _sync           = new SyncCoordinator(_videoPlayer);
        _mainWindow     = new MainWindow(Config, ObjectTable);
        _mainWindow.SetSync(_sync);
        _mainWindow.SetBrowserPlayer(_browserPlayer);

        _sync.Volume = Config.Volume;
        _sync.Muted  = Config.Muted;

        _sync.OnPlaylistAdvanced += idx =>
        {
            Config.PlaylistIndex = idx;
            Config.Save();
        };

        _sync.Client.OnScreenConfig += (cx, cy, cz, yaw, w, h) =>
        {
            Config.Screen.Center     = new System.Numerics.Vector3(cx, cy, cz);
            Config.Screen.YawDegrees = yaw;
            Config.Screen.Width      = w;
            Config.Screen.Height     = h;
            Config.Screen.Visible    = true;
            Config.Save();
        };

        CommandManager.AddHandler(CmdMain, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open FFXIV-TV settings. Sub-commands: place, hide, play <path|url>, pause, stop, playlist add <path|url>, playlist clear."
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
        _sync.Dispose();
        _videoPlayer.Dispose();
        _browserPlayer.Dispose();
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

        if (lower.StartsWith("playlist "))
        {
            var plArgs  = trimmed.Substring(9).Trim();
            var plLower = plArgs.ToLowerInvariant();
            if (plLower == "clear")
            {
                Config.Playlist.Clear();
                Config.PlaylistIndex = -1;
                Config.Save();
                ChatGui.Print("[FFXIV-TV] Playlist cleared.");
            }
            else if (plLower.StartsWith("add "))
            {
                var entry = plArgs.Substring(4).Trim();
                Config.Playlist.Add(entry);
                Config.Save();
                ChatGui.Print($"[FFXIV-TV] Added to playlist ({Config.Playlist.Count} items): {entry}");
            }
            else
            {
                ChatGui.PrintError("[FFXIV-TV] Usage: /fftv playlist add <path|url>  or  /fftv playlist clear");
            }
            return;
        }

        if (lower.StartsWith("play "))
        {
            var path = trimmed.Substring(5).Trim();
            bool isUrl = path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                         path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            Config.ActiveMode = isUrl ? ContentMode.UrlVideo : ContentMode.LocalVideo;
            if (isUrl) Config.VideoUrl  = path;
            else       Config.VideoPath = path;
            Config.Save();
            _sync.Play(path);
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
                _sync.TogglePause();
                break;
            case "stop":
                _sync.Stop();
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

        // Wire BrowserPlayer to the D3D device once (first frame after D3D init).
        if (_d3dRenderer.IsAvailable && !_browserSetupDone && _d3dRenderer.Device != null)
        {
            _browserPlayer.SetDevice(_d3dRenderer.Device);
            _d3dRenderer.SetBrowserPlayer(_browserPlayer);
            _browserSetupDone = true;

            // Auto-navigate if browser mode was active when plugin was reloaded.
            if (Config.ActiveMode == ContentMode.Browser && !string.IsNullOrEmpty(Config.BrowserUrl))
                _browserPlayer.Navigate(Config.BrowserUrl);
        }

        // Keep sync mode, yt-dlp path, and playlist state current each frame.
        _sync.Mode          = Config.SyncMode;
        _sync.YtDlpPath     = Config.YtDlpPath;
        _sync.Playlist      = Config.Playlist;
        _sync.PlaylistIndex = Config.PlaylistIndex;
        _sync.PlaylistLoop  = Config.PlaylistLoop;

        // Auto-start/stop server based on persisted config (survives plugin reloads).
        if (Config.SyncMode == NetworkMode.Host && Config.SyncServerRunning
            && !_sync.Server.IsRunning && string.IsNullOrEmpty(_sync.Server.LastError))
            _sync.Server.Start(Config.SyncPort);
        else if ((!Config.SyncServerRunning || Config.SyncMode != NetworkMode.Host)
            && _sync.Server.IsRunning)
            _sync.Server.Stop();

        if (_d3dRenderer.IsAvailable)
        {
            _d3dRenderer.Brightness = Config.Brightness;
            _d3dRenderer.Gamma      = Config.Gamma;
            _d3dRenderer.Contrast   = Config.Contrast;
            _d3dRenderer.Tint       = new Vector4(Config.TintR, Config.TintG, Config.TintB, Config.TintA);

            // Only load the image texture when in Image mode.
            _d3dRenderer.SetImagePath(Config.ActiveMode == ContentMode.Image ? Config.ImagePath : string.Empty);

            // CRITICAL: PrepareHooks must run every frame regardless of which draw path is taken.
            // Without this, the DrawPlaceholder path (Image mode + no image) never sets
            // _pendingLearnBackbuffer, breaking the entire backbuffer-learning cascade and
            // preventing any injection from firing. Draw() and DrawBlack() also call PrepareHooks
            // internally so the double-call is harmless (idempotent).
            _d3dRenderer.PrepareHooks(screen);

            // Always call D3DRenderer.Draw() in video modes so UploadFrame() runs and
            // the GPU texture is created on the first decoded frame.
            // In Image mode with no texture: show placeholder.
            // In video modes with no active texture (stopped/not started): show black backing only.
            if (Config.ActiveMode == ContentMode.Image && !_d3dRenderer.HasTexture)
            {
                _screenRenderer.DrawPlaceholder(Config);
            }
            else
            {
                _d3dRenderer.Draw(screen);
                // When video is stopped/no texture: draw a solid black quad via D3D11
                // (depth-tested) so it never covers native FFXIV UI or characters.
                // MUST NOT use ScreenRenderer.DrawBlackBacking — that path uses ImGui
                // which has no depth testing and renders over everything.
                if (Config.ActiveMode != ContentMode.Image && !_d3dRenderer.HasTexture && Config.ShowBlackBacking)
                    _d3dRenderer.DrawBlack(screen);
            }
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
