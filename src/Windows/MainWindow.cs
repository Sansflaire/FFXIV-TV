using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

namespace FFXIVTv.Windows;

public sealed class MainWindow
{
    public bool IsVisible { get; set; } = false;

    private readonly Configuration _config;
    private readonly IObjectTable  _objectTable;

    // Content source buffers — kept in sync with config on construction.
    private string _imagePathBuffer;
    private bool   _imagePathBufferDirty = false;

    private string _videoPathBuffer;
    private string _videoUrlBuffer;
    private string _ytDlpPathBuffer;

    // Network sync buffers
    private int    _syncPortBuffer;
    private string _syncAddressBuffer;

    // Sync coordinator — set after D3D device is available.
    private SyncCoordinator? _sync;

    // Cached local IP — expensive to query every frame via NetworkInterface.
    private string? _cachedLocalIp;
    private int     _syncPortAtLastIpQuery = -1;

    private static readonly string[] ContentModeNames  = { "Image", "Local Video", "URL / Stream" };
    private static readonly string[] NetworkModeNames  = { "Off", "Host", "Client" };

    public MainWindow(Configuration config, IObjectTable objectTable)
    {
        _config       = config;
        _objectTable  = objectTable;

        _imagePathBuffer   = config.ImagePath;
        _videoPathBuffer   = config.VideoPath;
        _videoUrlBuffer    = config.VideoUrl;
        _ytDlpPathBuffer   = config.YtDlpPath;
        _syncPortBuffer    = config.SyncPort;
        _syncAddressBuffer = config.SyncHostAddress;
    }

    public void SetSync(SyncCoordinator sync) => _sync = sync;

    public void Draw()
    {
        if (!IsVisible) return;

        ImGui.SetNextWindowSize(new Vector2(480, 520), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(360, 300), new Vector2(800, 900));

        bool open = IsVisible;
        if (!ImGui.Begin("FFXIV-TV Settings", ref open))
        {
            IsVisible = open;
            ImGui.End();
            return;
        }
        IsVisible = open;

        DrawScreenSection();
        ImGui.Spacing();
        DrawContentSection();
        ImGui.Spacing();
        DrawNetworkSection();
        ImGui.Spacing();
        DrawTintSection();

        ImGui.End();
    }

    // ─── Screen Transform ────────────────────────────────────────────────────

    private void DrawScreenSection()
    {
        if (!ImGui.CollapsingHeader("Screen Transform", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        bool isClient = _config.SyncMode == NetworkMode.Client;
        if (isClient)
        {
            ImGui.TextDisabled("Screen position is controlled by the host.");
            ImGui.BeginDisabled();
        }

        var screen = _config.Screen;
        bool changed = false;

        bool vis = screen.Visible;
        if (ImGui.Checkbox("Visible", ref vis)) { screen.Visible = vis; changed = true; }

        ImGui.SameLine();

        bool always = _config.AlwaysDraw;
        if (ImGui.Checkbox("Always Draw", ref always)) { _config.AlwaysDraw = always; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Keep drawing even when corners go behind the camera.");

        ImGui.SameLine();

        bool sandbox = _config.UsePhase1Sandbox;
        if (ImGui.Checkbox("Phase 1 Sandbox", ref sandbox)) { _config.UsePhase1Sandbox = sandbox; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Force Phase 1 rendering (WorldToScreen + ImGui). No depth testing.");

        ImGui.SameLine();

        bool backing = _config.ShowBlackBacking;
        if (ImGui.Checkbox("Black Backing", ref backing)) { _config.ShowBlackBacking = backing; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Draw a solid black rectangle behind the content.");

        ImGui.SameLine();

        if (ImGui.SmallButton("Place at Player"))
        {
            var player = _objectTable.LocalPlayer;
            if (player != null)
            {
                screen.Center = player.Position + new Vector3(0f, 0f, 3f);
                screen.YawDegrees = player.Rotation * (180f / MathF.PI);
                changed = true;
            }
        }

        ImGui.Separator();

        var center = screen.Center;
        if (ImGui.DragFloat3("Position (X/Y/Z)", ref center, 0.05f))
        {
            screen.Center = center;
            changed = true;
        }

        float yaw = screen.YawDegrees;
        if (ImGui.SliderFloat("Yaw (degrees)", ref yaw, -180f, 180f))
        {
            screen.YawDegrees = yaw;
            changed = true;
        }

        float w = screen.Width;
        float h = screen.Height;

        if (ImGui.DragFloat("Width (units)", ref w, 0.05f, 0.1f, 30f))
        {
            screen.Width = w;
            changed = true;
        }
        if (ImGui.DragFloat("Height (units)", ref h, 0.05f, 0.1f, 20f))
        {
            screen.Height = h;
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Lock 16:9"))
        {
            screen.Height = screen.Width * 9f / 16f;
            changed = true;
        }

        if (changed)
        {
            _config.Save();
            if (_sync?.Mode == NetworkMode.Host && _sync.Server.IsRunning)
                _sync.Server.BroadcastScreenConfig(screen);
        }

        if (isClient) ImGui.EndDisabled();
    }

    // ─── Content Source ──────────────────────────────────────────────────────

    private void DrawContentSection()
    {
        if (!ImGui.CollapsingHeader("Content Source", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        int mode = (int)_config.ActiveMode;
        if (ImGui.Combo("Mode###contentmode", ref mode, ContentModeNames, ContentModeNames.Length))
        {
            _config.ActiveMode = (ContentMode)mode;
            _config.Save();
        }

        ImGui.Separator();

        switch (_config.ActiveMode)
        {
            case ContentMode.Image:      DrawImageControls();      break;
            case ContentMode.LocalVideo: DrawLocalVideoControls(); break;
            case ContentMode.UrlVideo:   DrawUrlVideoControls();   break;
        }

        if (_config.ActiveMode != ContentMode.Image)
        {
            ImGui.Spacing();
            ImGui.Separator();
            int vol = _config.Volume;
            ImGui.SetNextItemWidth(200);
            if (ImGui.SliderInt("Volume##vol", ref vol, 0, 100))
            {
                _config.Volume = vol;
                _sync?.Volume  = vol;
                _config.Save();
            }
        }
    }

    private void DrawImageControls()
    {
        if (_imagePathBufferDirty)
        {
            _imagePathBuffer     = _config.ImagePath;
            _imagePathBufferDirty = false;
        }

        ImGui.TextDisabled("PNG / JPEG path on disk:");
        if (ImGui.InputText("##imagepath", ref _imagePathBuffer, 512,
                            ImGuiInputTextFlags.EnterReturnsTrue))
        {
            _config.ImagePath = _imagePathBuffer;
            _config.Save();
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Apply##img"))
        {
            _config.ImagePath = _imagePathBuffer;
            _config.Save();
        }
        ImGui.TextDisabled("(Press Enter or click Apply to load)");
    }

    private void DrawLocalVideoControls()
    {
        bool isClient = _config.SyncMode == NetworkMode.Client;

        ImGui.TextDisabled("Video file path (MP4, MKV, AVI, etc.):");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##videopath", ref _videoPathBuffer, 512);

        bool hasPlayer = _sync != null;
        bool disableControls = !hasPlayer || isClient;
        if (disableControls) ImGui.BeginDisabled();

        if (ImGui.Button("Play##vl"))
        {
            _config.VideoPath = _videoPathBuffer;
            _config.Save();
            _sync?.Play(_videoPathBuffer);
        }
        ImGui.SameLine();
        if (ImGui.Button("Pause##vl")) _sync?.TogglePause();
        ImGui.SameLine();
        if (ImGui.Button("Stop##vl"))  _sync?.Stop();

        if (disableControls) ImGui.EndDisabled();

        if (isClient)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("  [Controlled by host]");
        }
        else if (_sync != null)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"  [{_sync.VideoStatus}]");
        }

        if (!isClient) DrawScrubBar();
    }

    private void DrawUrlVideoControls()
    {
        bool isClient = _config.SyncMode == NetworkMode.Client;

        ImGui.TextDisabled("Video URL (direct MP4/stream, YouTube, etc.):");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##videourl", ref _videoUrlBuffer, 1024);

        bool hasPlayer = _sync != null;
        bool disableControls = !hasPlayer || isClient;
        if (disableControls) ImGui.BeginDisabled();

        if (ImGui.Button("Play##vu"))
        {
            _config.VideoUrl = _videoUrlBuffer;
            _config.Save();
            _sync?.Play(_videoUrlBuffer);
        }
        ImGui.SameLine();
        if (ImGui.Button("Pause##vu")) _sync?.TogglePause();
        ImGui.SameLine();
        if (ImGui.Button("Stop##vu"))  _sync?.Stop();

        if (disableControls) ImGui.EndDisabled();

        if (isClient)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("  [Controlled by host]");
        }
        else if (_sync != null)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"  [{_sync.VideoStatus}]");
        }

        if (!isClient) DrawScrubBar();

        ImGui.Spacing();
        ImGui.TextDisabled("yt-dlp path (optional — needed for YouTube):");
        if (ImGui.InputText("##ytdlppath", ref _ytDlpPathBuffer, 512,
                            ImGuiInputTextFlags.EnterReturnsTrue))
        {
            _config.YtDlpPath = _ytDlpPathBuffer;
            _config.Save();
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Apply##ytd"))
        {
            _config.YtDlpPath = _ytDlpPathBuffer;
            _config.Save();
        }
        ImGui.TextDisabled("(Leave empty to auto-find yt-dlp.exe in plugin folder)");
    }

    private void DrawScrubBar()
    {
        if (_sync == null) return;
        if (!_sync.IsPlaying && !_sync.IsPaused) return;

        long timeMs   = _sync.TimeMs;
        long lengthMs = _sync.LengthMs;

        bool hasLength = lengthMs > 0;
        string timeLabel = hasLength
            ? $"{FormatTime(timeMs)} / {FormatTime(lengthMs)}"
            : FormatTime(timeMs);
        ImGui.TextDisabled(timeLabel);

        float pos = _sync.Position;

        if (!hasLength) ImGui.BeginDisabled();
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderFloat("##scrub", ref pos, 0f, 1f, ""))
            _sync.Seek(pos);
        if (!hasLength) ImGui.EndDisabled();

        if (!hasLength && ImGui.IsItemHovered())
            ImGui.SetTooltip("Seeking not available for live streams.");

        // ── A-B loop controls ─────────────────────────────────────────────────
        if (ImGui.SmallButton("Set A")) _sync.SetLoopA();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Mark loop start at current position.");

        ImGui.SameLine();
        if (ImGui.SmallButton("Set B")) _sync.SetLoopB();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Mark loop end at current position.");

        ImGui.SameLine();

        bool loopOn = _sync.AbLoopActive;
        if (loopOn) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.15f, 0.65f, 0.15f, 1f));
        if (ImGui.SmallButton(loopOn ? "A-B: ON " : "A-B: OFF")) _sync.ToggleAbLoop();
        if (loopOn) ImGui.PopStyleColor();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Toggle A-B loop.");

        ImGui.SameLine();
        if (ImGui.SmallButton("Clear")) _sync.ClearAbLoop();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Clear A and B points.");

        if (_sync.LoopA > 0f || _sync.LoopB < 1f)
        {
            string aStr = hasLength ? FormatTime((long)(_sync.LoopA * lengthMs)) : $"{_sync.LoopA:P0}";
            string bStr = hasLength ? FormatTime((long)(_sync.LoopB * lengthMs)) : $"{_sync.LoopB:P0}";
            ImGui.SameLine();
            ImGui.TextDisabled($"A: {aStr}  B: {bStr}");
        }
    }

    private static string FormatTime(long ms)
    {
        if (ms <= 0) return "0:00";
        var ts = System.TimeSpan.FromMilliseconds(ms);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes}:{ts.Seconds:D2}";
    }

    // ─── Network Sync ─────────────────────────────────────────────────────────

    private void DrawNetworkSection()
    {
        if (!ImGui.CollapsingHeader("Network Sync"))
            return;

        int netMode = (int)_config.SyncMode;
        if (ImGui.Combo("Role###networkmode", ref netMode, NetworkModeNames, NetworkModeNames.Length))
        {
            // Changing role stops active server/client first
            if (_config.SyncMode == NetworkMode.Host)
                _sync?.Server.Stop();
            else if (_config.SyncMode == NetworkMode.Client)
                _sync?.Client.Disconnect();

            _config.SyncMode = (NetworkMode)netMode;
            _config.Save();
        }

        ImGui.Separator();

        switch (_config.SyncMode)
        {
            case NetworkMode.Off:
                ImGui.TextDisabled("Select Host to share video, or Client to watch a host.");
                break;

            case NetworkMode.Host:
                DrawHostControls();
                break;

            case NetworkMode.Client:
                DrawClientControls();
                break;
        }
    }

    private void DrawHostControls()
    {
        if (_sync == null) return;
        var server = _sync.Server;

        ImGui.TextDisabled("Port:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80);
        if (ImGui.InputInt("##syncport", ref _syncPortBuffer, 0))
        {
            _syncPortBuffer      = Math.Clamp(_syncPortBuffer, 1024, 65535);
            _config.SyncPort = _syncPortBuffer;
            _config.Save();
        }

        ImGui.SameLine();

        if (server.IsRunning)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.15f, 0.15f, 1f));
            if (ImGui.Button("Stop Server")) { server.Stop(); _config.SyncServerRunning = false; _config.Save(); }
            ImGui.PopStyleColor();
            ImGui.SameLine();
            ImGui.TextDisabled($"Running — {server.ClientCount} client(s) connected");
        }
        else
        {
            if (ImGui.Button("Start Server"))
            {
                _config.SyncServerRunning = true;
                _config.Save();
                server.Start(_config.SyncPort);
                server.BroadcastScreenConfig(_config.Screen); // seeds _latestScreenJson for new clients
            }
            if (!string.IsNullOrEmpty(server.LastError))
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), $"Error: {server.LastError}");
            }
        }

        // Warn if active content is a local file — can't be shared
        bool localFileActive =
            (_config.ActiveMode == ContentMode.LocalVideo && !string.IsNullOrEmpty(_config.VideoPath)) ||
            (_config.ActiveMode == ContentMode.Image);
        if (localFileActive)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f),
                "Only URLs can be shared. Switch to URL mode to sync playback.");
        }

        // UPnP status
        ImGui.Spacing();
        if (!string.IsNullOrEmpty(server.UPnPStatus))
        {
            bool mapped = server.UPnPStatus.EndsWith("✓");
            if (mapped)
                ImGui.TextColored(new Vector4(0.3f, 0.9f, 0.3f, 1f), server.UPnPStatus);
            else
                ImGui.TextDisabled(server.UPnPStatus);
        }

        // "Tell clients to connect to" — prefer public IP when UPnP gave us one
        ImGui.Spacing();
        string displayIp;
        if (!string.IsNullOrEmpty(server.PublicIp))
        {
            displayIp = server.PublicIp;
        }
        else
        {
            if (_cachedLocalIp == null || _syncPortAtLastIpQuery != _config.SyncPort)
            {
                _cachedLocalIp         = UPnPHelper.GetLocalIp() ?? "?.?.?.?";
                _syncPortAtLastIpQuery = _config.SyncPort;
            }
            displayIp = _cachedLocalIp;
        }
        string connectStr = $"{displayIp}:{_config.SyncPort}";
        ImGui.TextDisabled($"Tell clients to connect to:  {connectStr}");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Click to copy");
        if (ImGui.IsItemClicked())
            ImGui.SetClipboardText(connectStr);
    }

    private void DrawClientControls()
    {
        if (_sync == null) return;
        var client = _sync.Client;

        ImGui.TextDisabled("Host address (IP:port):");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##syncaddr", ref _syncAddressBuffer, 128,
                            ImGuiInputTextFlags.EnterReturnsTrue))
        {
            _config.SyncHostAddress = _syncAddressBuffer;
            _config.Save();
        }

        if (client.IsConnected)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.15f, 0.15f, 1f));
            if (ImGui.Button("Disconnect")) client.Disconnect();
            ImGui.PopStyleColor();
            ImGui.SameLine();
            ImGui.TextDisabled("Connected");
        }
        else
        {
            if (ImGui.Button("Connect"))
            {
                _config.SyncHostAddress = _syncAddressBuffer;
                _config.Save();
                client.Connect(_syncAddressBuffer);
            }
            ImGui.SameLine();
            // Red for errors/failures, grey for connecting/reconnecting
            bool isFailed = client.Status.StartsWith("Failed:");
            if (isFailed) ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), client.Status);
            else          ImGui.TextDisabled(client.Status);
        }

        ImGui.Spacing();
        ImGui.TextDisabled("Playback is controlled by the host while connected.");
    }

    // ─── Tint / Opacity ──────────────────────────────────────────────────────

    private void DrawTintSection()
    {
        if (!ImGui.CollapsingHeader("Tint / Opacity"))
            return;

        var tint = new Vector4(_config.TintR, _config.TintG, _config.TintB, _config.TintA);
        if (ImGui.ColorEdit4("Tint color", ref tint))
        {
            _config.TintR = tint.X;
            _config.TintG = tint.Y;
            _config.TintB = tint.Z;
            _config.TintA = tint.W;
            _config.Save();
        }
    }
}
