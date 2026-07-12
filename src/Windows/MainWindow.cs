using System;
using System.IO;
using System.Linq;
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
    private string _browserUrlBuffer;

    // Network sync buffers
    private int    _syncPortBuffer;
    private string _syncAddressBuffer;

    // Playlist UI buffer
    private string _playlistAddBuffer = string.Empty;

    // Sync coordinator — set after D3D device is available.
    private SyncCoordinator? _sync;

    // Browser player — set after plugin wires it up.
    private BrowserPlayer? _browserPlayer;

    // D3D renderer — used by Debug tab to toggle inject parameters and show live state.
    private D3DRenderer? _d3dRenderer;

    // Cached local IP — expensive to query every frame via NetworkInterface.
    private string? _cachedLocalIp;
    private int     _syncPortAtLastIpQuery = -1;


    private static readonly string[] ContentModeNames  = { "Image", "Local Video", "URL / Stream", "Browser (WebView2)" };
    private static readonly string[] NetworkModeNames  = { "Off", "Host", "Client" };
    private static readonly string[] RenderModeNames   = { "Legacy", "CopyBlit", "PyonPix", "PyonPixExact (recommended)" };

    public MainWindow(Configuration config, IObjectTable objectTable)
    {
        _config            = config;
        _objectTable       = objectTable;

        _imagePathBuffer   = config.ImagePath;
        _videoPathBuffer   = config.VideoPath;
        _videoUrlBuffer    = config.VideoUrl;
        _ytDlpPathBuffer   = config.YtDlpPath;
        _browserUrlBuffer  = config.BrowserUrl;
        _syncPortBuffer    = config.SyncPort;
        _syncAddressBuffer = config.SyncHostAddress;
    }

    public void SetSync(SyncCoordinator sync) => _sync = sync;
    public void SetBrowserPlayer(BrowserPlayer bp) => _browserPlayer = bp;
    public void SetD3DRenderer(D3DRenderer r) => _d3dRenderer = r;

    public void Draw()
    {
        if (!IsVisible) return;

        ImGui.SetNextWindowSize(new Vector2(480, 520), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(360, 300), new Vector2(800, 900));

        bool open = IsVisible;
        if (!ImGui.Begin("FFXIV-TV", ref open))
        {
            IsVisible = open;
            ImGui.End();
            return;
        }
        IsVisible = open;

        if (ImGui.BeginTabBar("##maintabs"))
        {
            if (ImGui.BeginTabItem("Player"))
            {
                DrawPlayerTab();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Placement"))
            {
                DrawScreenSection();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Network"))
            {
                DrawNetworkTab();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Settings"))
            {
                DrawSettingsTab();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Debug"))
            {
                DrawDebugTab();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }

        ImGui.End();
    }

    // ─── Player Tab ───────────────────────────────────────────────────────────

    private void DrawPlayerTab()
    {
        DrawContentSection();
    }

    // ─── Screen Transform ─────────────────────────────────────────────────────

    private void DrawScreenSection()
    {
        bool isClient = _config.SyncMode == NetworkMode.Client && (_sync?.Client.IsConnected ?? false);
        var  screen   = _config.Screen;
        bool changed  = false;

        // Row: Place at Player
        bool isClientMode = _config.SyncMode == NetworkMode.Client;
        if (isClientMode) ImGui.BeginDisabled();
        if (ImGui.SmallButton("Place at Player"))
        {
            var player = _objectTable.LocalPlayer;
            if (player != null)
            {
                screen.Center     = player.Position + new Vector3(0f, 0f, 3f);
                screen.YawDegrees = player.Rotation * (180f / MathF.PI);
                changed = true;
            }
        }
        if (isClientMode)
        {
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Screen position is controlled by the host.");
        }

        // When connected as a client, HIDE position controls entirely.
        if (isClient)
        {
            ImGui.TextDisabled("Screen position is controlled by the host.");
        }
        else
        {
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

            float pitch = screen.PitchDegrees;
            if (ImGui.SliderFloat("Pitch (degrees)", ref pitch, -90f, 90f))
            {
                screen.PitchDegrees = pitch;
                changed = true;
            }

            float roll = screen.RollDegrees;
            if (ImGui.SliderFloat("Roll (degrees)", ref roll, -180f, 180f))
            {
                screen.RollDegrees = roll;
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
        }

        if (changed)
        {
            _config.Save();
            if (_sync?.Mode == NetworkMode.Host && _sync.Server.IsRunning)
                _sync.Server.BroadcastScreenConfig(screen);
        }
    }

    // ─── Content Source ───────────────────────────────────────────────────────

    private void DrawContentSection()
    {
        ImGui.Text("Content");
        ImGui.Separator();

        int mode = (int)_config.ActiveMode;
        if (ImGui.Combo("Mode###contentmode", ref mode, ContentModeNames, ContentModeNames.Length))
        {
            _config.ActiveMode = (ContentMode)mode;
            _config.Save();
        }

        ImGui.Spacing();

        switch (_config.ActiveMode)
        {
            case ContentMode.Image:      DrawImageControls();      break;
            case ContentMode.LocalVideo: DrawLocalVideoControls(); break;
            case ContentMode.UrlVideo:   DrawUrlVideoControls();   break;
            case ContentMode.Browser:    DrawBrowserControls();    break;
        }

        // Playlist only applies to video modes.
        if (_config.ActiveMode == ContentMode.LocalVideo || _config.ActiveMode == ContentMode.UrlVideo)
        {
            ImGui.Spacing();
            DrawPlaylistSection();
        }

        // Volume/Mute only applies to video modes.
        if (_config.ActiveMode == ContentMode.LocalVideo || _config.ActiveMode == ContentMode.UrlVideo)
        {
            ImGui.Spacing();
            DrawVolumeMute();
        }
    }

    private void DrawVolumeMute()
    {
        bool muted = _config.Muted;
        if (muted)
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.7f, 0.2f, 0.2f, 1f));
        if (ImGui.Button(muted ? "Unmute##mute" : "  Mute  ##mute"))
        {
            _config.Muted = !muted;
            if (_sync != null) _sync.Muted = !muted;
            _config.Save();
        }
        if (muted) ImGui.PopStyleColor();

        ImGui.SameLine();
        if (muted) ImGui.BeginDisabled();
        ImGui.SetNextItemWidth(200);
        int vol = _config.Volume;
        if (ImGui.SliderInt("Volume##vol", ref vol, 0, 100))
        {
            _config.Volume = vol;
            if (_sync != null) _sync.Volume = vol;
            _config.Save();
        }
        if (muted) ImGui.EndDisabled();
    }

    private void DrawImageControls()
    {
        if (_imagePathBufferDirty)
        {
            _imagePathBuffer      = _config.ImagePath;
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

        bool hasPlayer      = _sync != null;
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

        bool hasPlayer       = _sync != null;
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
    }

    private void DrawBrowserControls()
    {
        ImGui.TextDisabled("URL to open (YouTube, Reddit, any website — no yt-dlp needed):");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##browserurl", ref _browserUrlBuffer, 1024);

        if (ImGui.Button("Open##br"))
        {
            _config.BrowserUrl = _browserUrlBuffer;
            _config.Save();
            _browserPlayer?.Navigate(_browserUrlBuffer);
        }
        ImGui.SameLine();
        if (ImGui.Button("Stop##br"))
            _browserPlayer?.Stop();

        ImGui.SameLine();
        ImGui.TextDisabled($"  [{_browserPlayer?.Status ?? "Stopped"}]");

        ImGui.Spacing();
        ImGui.TextDisabled("Captures at ~6fps. First load takes a moment to start WebView2.");
        ImGui.TextDisabled("Requires: Microsoft Edge / WebView2 Runtime (pre-installed on Win11).");
    }

    private void DrawPlaylistSection()
    {
        if (!ImGui.CollapsingHeader("Playlist"))
            return;

        bool isClient = _config.SyncMode == NetworkMode.Client;
        var  playlist = _config.Playlist;
        int  count    = playlist.Count;

        bool loop = _config.PlaylistLoop;
        if (ImGui.Checkbox("Loop##plloop", ref loop))
        {
            _config.PlaylistLoop = loop;
            _config.Save();
        }
        if (count > 0)
        {
            ImGui.SameLine();
            int    cur      = _config.PlaylistIndex;
            string idxLabel = cur >= 0 && cur < count
                ? $"  Item {cur + 1} / {count}"
                : $"  {count} item(s)";
            ImGui.TextDisabled(idxLabel);
        }

        ImGui.Separator();

        int removeIdx = -1, moveUp = -1, moveDown = -1;
        for (int i = 0; i < count; i++)
        {
            bool isCurrent = i == _config.PlaylistIndex;
            if (isCurrent) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.35f, 0.9f, 0.35f, 1f));

            string item  = playlist[i];
            string label = item.Length > 48 ? "…" + item[^46..] : item;
            ImGui.TextUnformatted($"{i + 1}. {label}");

            if (isCurrent) ImGui.PopStyleColor();

            ImGui.SameLine();
            if (ImGui.SmallButton($"▲##pu{i}") && i > 0)          moveUp   = i;
            ImGui.SameLine();
            if (ImGui.SmallButton($"▼##pd{i}") && i < count - 1)  moveDown = i;
            ImGui.SameLine();
            if (!isClient && ImGui.SmallButton($"▶##pp{i}"))
            {
                _config.PlaylistIndex = i;
                _config.Save();
                _sync?.Play(playlist[i]);
            }
            ImGui.SameLine();
            if (ImGui.SmallButton($"✕##pr{i}")) removeIdx = i;
        }

        if (removeIdx >= 0)
        {
            playlist.RemoveAt(removeIdx);
            if (_config.PlaylistIndex >= playlist.Count)
                _config.PlaylistIndex = playlist.Count - 1;
            _config.Save();
        }
        if (moveUp >= 0)
        {
            (playlist[moveUp - 1], playlist[moveUp]) = (playlist[moveUp], playlist[moveUp - 1]);
            if      (_config.PlaylistIndex == moveUp)     _config.PlaylistIndex = moveUp - 1;
            else if (_config.PlaylistIndex == moveUp - 1) _config.PlaylistIndex = moveUp;
            _config.Save();
        }
        if (moveDown >= 0)
        {
            (playlist[moveDown], playlist[moveDown + 1]) = (playlist[moveDown + 1], playlist[moveDown]);
            if      (_config.PlaylistIndex == moveDown)     _config.PlaylistIndex = moveDown + 1;
            else if (_config.PlaylistIndex == moveDown + 1) _config.PlaylistIndex = moveDown;
            _config.Save();
        }

        ImGui.Separator();

        ImGui.SetNextItemWidth(-82);
        ImGui.InputText("##pladd", ref _playlistAddBuffer, 1024);
        ImGui.SameLine();
        if (ImGui.SmallButton("Add##pladd") && !string.IsNullOrWhiteSpace(_playlistAddBuffer))
        {
            playlist.Add(_playlistAddBuffer.Trim());
            _playlistAddBuffer = string.Empty;
            _config.Save();
        }
        ImGui.TextDisabled("File path or URL");

        if (count > 0)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear All##plclr"))
            {
                playlist.Clear();
                _config.PlaylistIndex = -1;
                _config.Save();
            }
        }
    }

    private void DrawScrubBar()
    {
        if (_sync == null) return;
        if (!_sync.IsPlaying && !_sync.IsPaused) return;

        long timeMs   = _sync.TimeMs;
        long lengthMs = _sync.LengthMs;

        bool   hasLength  = lengthMs > 0;
        string timeLabel  = hasLength
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

    // ─── Network Tab ─────────────────────────────────────────────────────────

    private void DrawNetworkTab()
    {
        int netMode = (int)_config.SyncMode;
        if (ImGui.Combo("Role###networkmode", ref netMode, NetworkModeNames, NetworkModeNames.Length))
        {
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

        ImGui.Separator();
        ImGui.Spacing();
        if (ImGui.Button("Copy Diagnostics Log"))
            CopyDiagnosticsLog();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Reads dalamud.log and copies all FFXIV-TV lines to clipboard.\nShare with the developer to diagnose issues.");
    }

    private static void CopyDiagnosticsLog()
    {
        try
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "XIVLauncher", "dalamud.log");

            string allText;
            using (var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream))
                allText = reader.ReadToEnd();

            var allLines = allText
                .Split('\n')
                .Where(l => l.Contains("FFXIV-TV"))
                .ToArray();

            // Find the last SESSION START marker — only show the current session.
            int sessionIdx = -1;
            for (int i = allLines.Length - 1; i >= 0; i--)
            {
                if (allLines[i].Contains("=== SESSION START ==="))
                { sessionIdx = i; break; }
            }
            var sessionLines = sessionIdx >= 0 ? allLines.Skip(sessionIdx).ToArray() : allLines;
            var lines = sessionLines.TakeLast(2000).ToArray();

            ImGui.SetClipboardText(lines.Length > 0
                ? string.Join("\n", lines)
                : "(No FFXIV-TV log entries found in dalamud.log)");
        }
        catch (Exception ex)
        {
            ImGui.SetClipboardText($"Error reading dalamud.log: {ex.Message}");
        }
    }

    private void DrawHostControls()
    {
        if (_sync == null) return;
        var server = _sync.Server;

        if (server.IsRunning)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.15f, 0.15f, 1f));
            if (ImGui.Button("Stop##stopserver")) { server.Stop(); _config.SyncServerRunning = false; _config.Save(); }
            ImGui.PopStyleColor();
            ImGui.SameLine();
            ImGui.TextDisabled($"{server.ClientCount} viewer(s) connected");

            ImGui.Spacing();

            if (server.TunnelMode)
            {
                // ── Tunnel mode: show the URL clients connect to ─────────────
                string? tunnelStatus = server.TunnelStatus;
                string? tunnelUrl    = server.TunnelUrl;

                if (tunnelUrl != null)
                {
                    // Strip wss:// prefix so the display fits; clients can paste it directly.
                    string display = tunnelUrl.Replace("wss://", "", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
                    ImGui.TextDisabled("Share with viewers:");
                    ImGui.SameLine();
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.3f, 1f, 0.5f, 1f));
                    ImGui.Text(display);
                    ImGui.PopStyleColor();
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Click to copy");
                    if (ImGui.IsItemClicked()) ImGui.SetClipboardText(display);
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Copy##copytunnel")) ImGui.SetClipboardText(display);
                }
                else
                {
                    ImGui.TextDisabled(tunnelStatus ?? "Starting tunnel...");
                }
            }
            else
            {
                // ── Direct mode: show IP:port ────────────────────────────────
                if (!string.IsNullOrEmpty(server.UPnPStatus))
                {
                    bool mapped = server.UPnPStatus.EndsWith("✓");
                    if (mapped) ImGui.TextColored(new Vector4(0.3f, 0.9f, 0.3f, 1f), server.UPnPStatus);
                    else        ImGui.TextDisabled(server.UPnPStatus);
                }
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
                ImGui.TextDisabled("Share with viewers:");
                ImGui.SameLine();
                ImGui.Text(connectStr);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Click to copy");
                if (ImGui.IsItemClicked()) ImGui.SetClipboardText(connectStr);
            }
        }
        else
        {
            // ── Start buttons ────────────────────────────────────────────────
            if (ImGui.Button("Create Room"))
            {
                _config.SyncServerRunning = true;
                _config.Save();
                string pluginDir = Plugin.PluginInterface.AssemblyLocation.DirectoryName ?? ".";
                server.StartWithTunnel(pluginDir, _config.SyncPort);
                server.BroadcastScreenConfig(_config.Screen);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Starts a local server + a Cloudflare tunnel.\n" +
                    "No port forwarding or firewall rules needed.\n" +
                    "cloudflared.exe (~30 MB) is downloaded once on first use.");

            ImGui.SameLine();
            ImGui.TextDisabled("or");
            ImGui.SameLine();

            if (ImGui.Button("LAN Only"))
            {
                _config.SyncServerRunning = true;
                _config.Save();
                server.Start(_config.SyncPort);
                server.BroadcastScreenConfig(_config.Screen);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Direct TCP server for same-network use.\nRequires a firewall rule for the port.");

            if (!string.IsNullOrEmpty(server.LastError))
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), $"Error: {server.LastError}");

            // Port setting (small, unobtrusive)
            ImGui.Spacing();
            ImGui.TextDisabled("Port:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(70);
            if (ImGui.InputInt("##syncport", ref _syncPortBuffer, 0))
            {
                _syncPortBuffer  = Math.Clamp(_syncPortBuffer, 1024, 65535);
                _config.SyncPort = _syncPortBuffer;
                _config.Save();
            }
        }

        if (_config.ActiveMode == ContentMode.Image)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f),
                "Image mode cannot be synced. Use Local Video or URL mode to share with clients.");
        }
    }

    private void DrawClientControls()
    {
        if (_sync == null) return;
        var client = _sync.Client;

        ImGui.TextDisabled("Address from host:");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##syncaddr", ref _syncAddressBuffer, 256,
                            ImGuiInputTextFlags.EnterReturnsTrue))
        {
            _config.SyncHostAddress = _syncAddressBuffer;
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Paste the address the host shared with you.\n" +
                "For tunnel mode: something like  random-words.trycloudflare.com\n" +
                "For LAN mode: IP:port  e.g.  192.168.1.5:9834");

        ImGui.Spacing();

        if (client.IsRunning)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.15f, 0.15f, 1f));
            if (ImGui.Button("Disconnect")) client.Disconnect();
            ImGui.PopStyleColor();
            ImGui.SameLine();
            if (client.IsConnected)
                ImGui.TextColored(new Vector4(0.3f, 0.9f, 0.3f, 1f), "Connected");
            else
            {
                bool isFailed = client.Status.StartsWith("Failed:");
                if (isFailed) ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), client.Status);
                else          ImGui.TextDisabled(client.Status);
            }
        }
        else
        {
            bool canConnect = !string.IsNullOrWhiteSpace(_syncAddressBuffer);
            if (!canConnect) ImGui.BeginDisabled();
            if (ImGui.Button("Connect"))
            {
                _config.SyncHostAddress = _syncAddressBuffer;
                _config.Save();
                client.Connect(_syncAddressBuffer);
            }
            if (!canConnect) ImGui.EndDisabled();

            if (!string.IsNullOrEmpty(client.Status) && client.Status != "Disconnected")
            {
                ImGui.SameLine();
                bool isFailed = client.Status.StartsWith("Failed:");
                if (isFailed) ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), client.Status);
                else          ImGui.TextDisabled(client.Status);
            }
        }

        ImGui.Spacing();
        ImGui.TextDisabled("Playback is controlled by the host while connected.");
    }

    // ─── Settings Tab ─────────────────────────────────────────────────────────

    private void DrawSettingsTab()
    {
        bool changed = false;

        bool vis = _config.Screen.Visible;
        if (ImGui.Checkbox("Visible##vis", ref vis)) { _config.Screen.Visible = vis; changed = true; }
        ImGui.Separator();

        float bloomCap = _config.BloomCap;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Peak Limit##bloomcap", ref bloomCap, 0.0f, 1.0f, bloomCap <= 0.001f ? "Off" : "%.2f"))
        {
            _config.BloomCap = bloomCap;
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Caps the maximum pixel brightness written into FFXIV's HDR buffer.\n0 = off (full bloom glow around the rect).\n~0.35 = good balance: visible content, minimal bloom.\nLower = less bloom glow but dimmer image.");

        float brightness = _config.Brightness;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Brightness##bright", ref brightness, 0f, 4f, "%.2f"))
        {
            _config.Brightness = brightness;
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("1.0 = original. Increase if video appears dark.");

        ImGui.Separator();

        bool always = _config.AlwaysDraw;
        if (ImGui.Checkbox("Always Draw", ref always)) { _config.AlwaysDraw = always; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Keep drawing even when corners go behind the camera.");

        bool backing = _config.ShowBlackBacking;
        if (ImGui.Checkbox("Black Backing", ref backing)) { _config.ShowBlackBacking = backing; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Draw a solid black rectangle behind the content.");

        if (changed) _config.Save();

        ImGui.Separator();

        // Post-processing curves
        float gamma = _config.Gamma;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Gamma##gamma", ref gamma, 0.1f, 3.0f, "%.2f"))
        {
            _config.Gamma = gamma;
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("1.0 = no change. >1 darkens midtones; <1 lifts them.");

        float contrast = _config.Contrast;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Contrast##contrast", ref contrast, 0.0f, 3.0f, "%.2f"))
        {
            _config.Contrast = contrast;
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("1.0 = no change. >1 = more contrast; <1 = flat/grey.");

        ImGui.SameLine();
        if (ImGui.SmallButton("Reset##curves"))
        {
            _config.Gamma    = 1.0f;
            _config.Contrast = 1.0f;
            _config.Save();
        }

        ImGui.Separator();

        var tint = new Vector4(_config.TintR, _config.TintG, _config.TintB, _config.TintA);
        if (ImGui.ColorEdit4("Tint color", ref tint))
        {
            _config.TintR = tint.X;
            _config.TintG = tint.Y;
            _config.TintB = tint.Z;
            _config.TintA = tint.W;
            _config.Save();
        }

        ImGui.Separator();

        ImGui.TextDisabled("yt-dlp path (host only — needed for YouTube):");
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

        if (_config.SyncMode == NetworkMode.Client)
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f),
                "yt-dlp runs on the host only — not needed for clients.");
    }

    // ─── Debug Tab ────────────────────────────────────────────────────────────
    // Sole control: rendering-mode picker. Everything else was removed as of
    // v0.5.246 — PyonPixExact is the confirmed-working renderer for peers and
    // no diagnostic knobs are needed in the shipping UI. Live diagnostics still
    // available via the StatusApi HTTP endpoints for developers.
    private void DrawDebugTab()
    {
        int rmIdx = (int)_config.RenderMode;
        ImGui.TextUnformatted("Renderer");
        ImGui.SetNextItemWidth(260);
        if (ImGui.Combo("##rendermode", ref rmIdx, RenderModeNames, RenderModeNames.Length))
        {
            _config.RenderMode = (RenderingMode)rmIdx;
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "PyonPixExact  — recommended. Ports priprii/PyonPix's rendering pipeline\n" +
                "                exactly using their compiled shader bytecode.\n" +
                "                Confirmed working end-to-end.\n" +
                "PyonPix       — from-scratch shader variant of PyonPixExact (A/B).\n" +
                "CopyBlit      — ImGui composite path (no game hooks).\n" +
                "Legacy        — historical D3DRenderer path; invisible/grayscale on peers.");
    }

}
