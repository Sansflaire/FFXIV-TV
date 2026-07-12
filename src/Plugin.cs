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
    [PluginService] internal static ISigScanner             SigScanner      { get; private set; } = null!;

    private const string CmdMain = "/fftv";

    // ─── Plugin state ────────────────────────────────────────────────────────
    internal Configuration Config { get; }

    // Phase 1: ImGui overlay (always available, no depth)
    private readonly ScreenRenderer _screenRenderer;

    // Phase 2: D3D11 world-space with depth (initialized on first draw frame)
    private readonly D3DRenderer _d3dRenderer;

    // Phase 5: XMP-style CopyBlit renderer (no hooks; portable across peers).
    // Selected by Configuration.UseCopyBlitRenderer (default: true).
    private readonly CopyBlitRenderer _copyBlit;

    // Phase 3: Video playback via LibVLC
    private readonly VideoPlayer      _videoPlayer;
    private bool _videoSetupDone;

    // Phase 3.7: Browser mode via WebView2
    private readonly BrowserPlayer    _browserPlayer;
    private bool _browserSetupDone;

    // Fallback: if D3D inject has never fired after this many frames, use ImGui fallback.
    private int  _d3dNoInjectFrames = 0;
    private const int D3dFallbackAfterFrames = 180; // ~3s at 60fps

    // Phase 4: Network sync (host/client)
    private readonly SyncCoordinator _sync;

    private readonly MainWindow _mainWindow;

    // Status API — lets Claude (and anyone) verify which version is loaded.
    private readonly StatusApi _statusApi;

    public Plugin()
    {
        Log.Info("[FFXIV-TV] === SESSION START ===");
        Config      = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        _statusApi  = new StatusApi();

        _screenRenderer = new ScreenRenderer(GameGui, TextureProvider);
        _d3dRenderer    = new D3DRenderer(GameInterop);
        _copyBlit       = new CopyBlitRenderer();
        _videoPlayer    = new VideoPlayer(PluginInterface.AssemblyLocation.DirectoryName!);
        _browserPlayer  = new BrowserPlayer(PluginInterface.AssemblyLocation.DirectoryName!);
        _sync           = new SyncCoordinator(_videoPlayer);
        _mainWindow     = new MainWindow(Config, ObjectTable);
        _mainWindow.SetSync(_sync);
        _mainWindow.SetBrowserPlayer(_browserPlayer);
        _mainWindow.SetD3DRenderer(_d3dRenderer);

        _copyBlit.SetVideoPlayer(_videoPlayer);
        _copyBlit.SetGameGui(GameGui);

        _statusApi.SetSubsystems(_d3dRenderer, _videoPlayer, _browserPlayer, _sync, Config, GameGui);
        _statusApi.SetCopyBlit(_copyBlit);

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
            Log.Info($"[FFXIV-TV] OnScreenConfig: center=({cx:F2},{cy:F2},{cz:F2}) yaw={yaw:F1} w={w:F1} h={h:F1} → Visible=true");
        };

        CommandManager.AddHandler(CmdMain, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens the FFXIV-TV settings window.\n" +
                          "  version               — Displays the plugin version in echo chat.\n" +
                          "  place                 — Places the screen in front of your character.\n" +
                          "  hide / toggle         — Toggles screen visibility.\n" +
                          "  play <path|url>       — Plays a local file or stream URL.\n" +
                          "  pause                 — Toggles pause.\n" +
                          "  stop                  — Stops playback.\n" +
                          "  diag                  — Prints D3D inject diagnostics to chat.\n" +
                          "  sysinfo               — Writes a full system-info block to dalamud.log.\n" +
                          "  trace <1-30>          — Captures N frames of per-hook pipeline trace to dalamud.log.\n" +
                          "  bbdrawskip <N>        — Skip N BB draws before injecting (N >= 0).\n" +
                          "  debugshader [on|off]  — Forces the TV shader to solid red (verifies draw path).\n" +
                          "  playlist add <path>   — Appends an entry to the playlist.\n" +
                          "  playlist clear        — Clears the playlist."
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
        _statusApi.Dispose();
        _sync.Dispose();
        _videoPlayer.Dispose();
        _browserPlayer.Dispose();
        _copyBlit.Dispose();
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

        if (lower.StartsWith("trace "))
        {
            var arg = trimmed.Substring(6).Trim();
            if (int.TryParse(arg, out int n) && n >= 1 && n <= 30)
            {
                D3DRenderer.TraceSequence        = 0;
                D3DRenderer.TraceFramesRemaining = n;
                ChatGui.Print($"[FFXIV-TV] Tracing next {n} frame(s) — see [FFTV-TRACE ...] lines in dalamud.log.");
            }
            else
            {
                ChatGui.PrintError("[FFXIV-TV] Usage: /fftv trace <N>  (N = 1..30)");
            }
            return;
        }

        if (lower.StartsWith("bbdrawskip "))
        {
            var arg = trimmed.Substring(11).Trim();
            if (int.TryParse(arg, out int n) && n >= 0)
            {
                D3DRenderer.BbDrawSkip = n;
                ChatGui.Print($"[FFXIV-TV] BbDrawSkip = {n}");
            }
            else
            {
                ChatGui.PrintError("[FFXIV-TV] Usage: /fftv bbdrawskip <N>  (N >= 0)");
            }
            return;
        }

        if (lower.StartsWith("debugshader"))
        {
            // Forms: "debugshader" (toggle), "debugshader on", "debugshader off"
            var rest = lower.Length > 11 ? lower.Substring(11).Trim() : string.Empty;
            bool newVal;
            if (rest == "on")       newVal = true;
            else if (rest == "off") newVal = false;
            else if (rest == string.Empty) newVal = !_d3dRenderer.DebugShaderRed;
            else
            {
                ChatGui.PrintError("[FFXIV-TV] Usage: /fftv debugshader [on|off]");
                return;
            }
            _d3dRenderer.DebugShaderRed = newVal;
            ChatGui.Print($"[FFXIV-TV] DebugShaderRed = {newVal}");
            return;
        }

        switch (lower)
        {
            case "":
                _mainWindow.IsVisible = !_mainWindow.IsVisible;
                break;
            case "version":
            {
                var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                ChatGui.Print($"[FFXIV-TV] v{v?.ToString(3) ?? "unknown"}");
                break;
            }
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
            case "sysinfo":
            {
                _d3dRenderer.LogSysInfo();
                var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
                ChatGui.Print($"[FFXIV-TV v{v}] Logging system info to dalamud.log.");
                ChatGui.Print(
                    $"ActiveSrv={_d3dRenderer.ActiveSrvSource} HasContent={_d3dRenderer.HasTexture} " +
                    $"LastPath={_d3dRenderer.LastInjectPath}");
                break;
            }
            case "diag":
            {
                var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
                bool d3dOk   = _d3dRenderer.IsAvailable;
                bool scrnVis = Config.Screen.Visible;
                long tgt     = _d3dRenderer.TargetInjectPtr;
                int  inj     = _d3dRenderer.LdrInjectCount;
                string path  = _d3dRenderer.LastInjectPath;
                bool bbLrn   = _d3dRenderer.BackbufferLearned;
                bool dsvSet  = _d3dRenderer.MainSceneDsvSet;
                bool hasTex  = _d3dRenderer.HasTexture;
                int  cbk     = _d3dRenderer.CbkFrameCount;
                float vpM11  = _d3dRenderer.StoredViewProj.M11;
                string fmt   = _d3dRenderer.LastInjectFmt ?? "none";
                int  injW    = _d3dRenderer.LastInjectW;
                int  injH    = _d3dRenderer.LastInjectH;
                var  ctr     = _d3dRenderer.StoredScreen?.Center ?? System.Numerics.Vector3.Zero;
                int  cfdi    = _d3dRenderer.CfDiCount;
                int  disp    = _d3dRenderer.CfDispatchCount;
                int  ldrCt   = _d3dRenderer.OmSetRtLdrCount;
                long injRtv  = _d3dRenderer.LastInjectRtvPtr;
                bool injWasBb= _d3dRenderer.LastInjectWasBackbuffer;
                bool injFb   = _d3dRenderer.LastFallbackUsed;
                int  bbSkip  = _d3dRenderer.OmSetRtSkippedBbCount;
                ChatGui.Print(
                    $"[FFXIV-TV v{v}] D3D={( d3dOk ? "ok" : "FAIL")} " +
                    $"screen={( scrnVis ? "vis" : "HIDDEN")} " +
                    $"target=0x{tgt:X} injected={inj} path={path} " +
                    $"bb={( bbLrn ? "ok" : "MISS")} dsv={( dsvSet ? "ok" : "MISS")} " +
                    $"tex={( hasTex ? "ok" : "none")} frames={cbk}");
                ChatGui.Print(
                    $"[FFXIV-TV] vp.M11={vpM11:F3} fmt={fmt} {injW}x{injH} " +
                    $"center=({ctr.X:F1},{ctr.Y:F1},{ctr.Z:F1}) " +
                    $"cfdi={cfdi} disp={disp} ldr={ldrCt}");
                ChatGui.Print(
                    $"[FFXIV-TV] injRtv=0x{injRtv:X} wasBB={injWasBb} fallback={injFb} bbSkip={bbSkip}");
                break;
            }
            default:
                ChatGui.PrintError($"[FFXIV-TV] Unknown argument '{args}'. Use /fftv, /fftv version, /fftv place, /fftv hide, /fftv play <path>, /fftv pause, /fftv stop.");
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

        // v0.5.222 SAFETY: force the CopyBlit path off at load time regardless of
        // what previous versions saved to disk. The shader is still broken (dark-blue
        // fullscreen fill), and we do NOT want that appearing on any user's screen
        // when they reload after updating.
        if (Config.UseCopyBlitRenderer)
        {
            Config.UseCopyBlitRenderer = false;
            Config.Save();
            Log.Warning("[FFXIV-TV] v0.5.222: forcing UseCopyBlitRenderer=false — XMP-style path is still broken (fullscreen fill). Legacy D3DRenderer takes over.");
        }

        // ── XMP-style CopyBlit path (opt-in via /set/copyblit?v=true) ─────────
        // OFF by default until the ray-plane math is verified working.
        if (Config.UseCopyBlitRenderer)
        {
            if (!_copyBlit.IsAvailable)
                _copyBlit.TryInitialize();

            if (_copyBlit.IsAvailable && !_videoSetupDone && _copyBlit.Device != null)
            {
                _videoPlayer.SetDevice(_copyBlit.Device);
                _videoSetupDone = true;
            }

            _sync.Mode          = Config.SyncMode;
            _sync.YtDlpPath     = Config.YtDlpPath;
            _sync.Playlist      = Config.Playlist;
            _sync.PlaylistIndex = Config.PlaylistIndex;
            _sync.PlaylistLoop  = Config.PlaylistLoop;

            if (Config.SyncMode == NetworkMode.Host && Config.SyncServerRunning
                && !_sync.Server.IsRunning && string.IsNullOrEmpty(_sync.Server.LastError))
                _sync.Server.Start(Config.SyncPort);
            else if ((!Config.SyncServerRunning || Config.SyncMode != NetworkMode.Host)
                && _sync.Server.IsRunning)
                _sync.Server.Stop();

            _copyBlit.Draw(Config);
            return;
        }

        // ── Legacy hook-based D3DRenderer path (Config.UseCopyBlitRenderer = false) ──
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
            _sync.Server.Start(Config.SyncPort); // restore as direct on reload; user can switch to tunnel via UI
        else if ((!Config.SyncServerRunning || Config.SyncMode != NetworkMode.Host)
            && _sync.Server.IsRunning)
            _sync.Server.Stop();

        if (_d3dRenderer.IsAvailable)
        {
            _d3dRenderer.Brightness = Config.Brightness;
            _d3dRenderer.Gamma      = Config.Gamma;
            _d3dRenderer.Contrast   = Config.Contrast;
            _d3dRenderer.BloomCap   = Config.BloomCap;
            _d3dRenderer.Tint       = new Vector4(Config.TintR, Config.TintG, Config.TintB, Config.TintA);

            // Only load the image texture when in Image mode.
            _d3dRenderer.SetImagePath(Config.ActiveMode == ContentMode.Image ? Config.ImagePath : string.Empty);

            // CRITICAL: PrepareHooks must run every frame regardless of which draw path is taken.
            // Without this, the DrawPlaceholder path (Image mode + no image) never sets
            // _pendingLearnBackbuffer, breaking the entire backbuffer-learning cascade and
            // preventing any injection from firing. Draw() and DrawBlack() also call PrepareHooks
            // internally so the double-call is harmless (idempotent).
            _d3dRenderer.PrepareHooks(screen);

            // Draw() uploads video/browser frames and sets _activeSrv. Must run first in all
            // content modes so UploadFrame() keeps the GPU texture current each frame.
            // DrawBlack() sets _activeSrv = gradient screensaver and is the idle fallback.
            // A null _activeSrv (black fallback) is a bug: always show gradient when no content.
            _d3dRenderer.Draw(screen);

            if (!_d3dRenderer.HasTexture)
            {
                // No active content — show the gradient screensaver so the rect is always visible.
                // Applies to: video stopped, no image loaded, browser not navigated, etc.
                _d3dRenderer.DrawBlack(screen);
                if (Config.ActiveMode == ContentMode.Image)
                    _screenRenderer.DrawPlaceholder(Config);
            }

            // Inject-never-fired fallback: if the D3D inject has produced zero frames after 3s,
            // the GPU pipeline on this machine doesn't match our inject heuristic.
            // Fall back to ImGui overlay so the screen is at least visible (no depth testing).
            if (_d3dRenderer.LdrInjectCount == 0 && _d3dRenderer.CbkFrameCount > D3dFallbackAfterFrames)
            {
                _d3dNoInjectFrames++;
                if (_d3dNoInjectFrames == 1)
                    Log.Warning("[FFXIV-TV] D3D inject never fired — falling back to ImGui overlay (no depth).");
                _screenRenderer.Draw(Config);
            }
            else
            {
                _d3dNoInjectFrames = 0;
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
