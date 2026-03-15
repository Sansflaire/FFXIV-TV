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

    // Video player — set after D3D device is available.
    private VideoPlayer? _videoPlayer;

    private static readonly string[] ContentModeNames = { "Image", "Local Video", "URL / Stream" };

    public MainWindow(Configuration config, IObjectTable objectTable)
    {
        _config       = config;
        _objectTable  = objectTable;

        _imagePathBuffer  = config.ImagePath;
        _videoPathBuffer  = config.VideoPath;
        _videoUrlBuffer   = config.VideoUrl;
        _ytDlpPathBuffer  = config.YtDlpPath;
    }

    public void SetVideoPlayer(VideoPlayer? vp) => _videoPlayer = vp;

    public void Draw()
    {
        if (!IsVisible) return;

        ImGui.SetNextWindowSize(new Vector2(480, 460), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(360, 300), new Vector2(800, 800));

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
        DrawTintSection();

        ImGui.End();
    }

    // ─── Screen Transform ────────────────────────────────────────────────────

    private void DrawScreenSection()
    {
        if (!ImGui.CollapsingHeader("Screen Transform", ImGuiTreeNodeFlags.DefaultOpen))
            return;

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

        if (changed) _config.Save();
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
        ImGui.TextDisabled("Video file path (MP4, MKV, AVI, etc.):");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##videopath", ref _videoPathBuffer, 512);

        bool hasPlayer = _videoPlayer != null;
        if (!hasPlayer) ImGui.BeginDisabled();

        if (ImGui.Button("Play##vl"))
        {
            _config.VideoPath = _videoPathBuffer;
            _config.Save();
            _videoPlayer?.Play(_videoPathBuffer);
        }
        ImGui.SameLine();
        if (ImGui.Button("Pause##vl")) _videoPlayer?.TogglePause();
        ImGui.SameLine();
        if (ImGui.Button("Stop##vl")) _videoPlayer?.Stop();

        if (!hasPlayer) ImGui.EndDisabled();

        if (_videoPlayer != null)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"  [{_videoPlayer.Status}]");
        }

        DrawScrubBar();
    }

    private void DrawUrlVideoControls()
    {
        ImGui.TextDisabled("Video URL (direct MP4/stream, YouTube, etc.):");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##videourl", ref _videoUrlBuffer, 1024);

        bool hasPlayer = _videoPlayer != null;
        if (!hasPlayer) ImGui.BeginDisabled();

        if (ImGui.Button("Play##vu"))
        {
            _config.VideoUrl = _videoUrlBuffer;
            _config.Save();
            _videoPlayer?.Play(_videoUrlBuffer);
        }
        ImGui.SameLine();
        if (ImGui.Button("Pause##vu")) _videoPlayer?.TogglePause();
        ImGui.SameLine();
        if (ImGui.Button("Stop##vu")) _videoPlayer?.Stop();

        if (!hasPlayer) ImGui.EndDisabled();

        if (_videoPlayer != null)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"  [{_videoPlayer.Status}]");
        }

        DrawScrubBar();

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
        if (_videoPlayer == null) return;
        if (!_videoPlayer.IsPlaying && !_videoPlayer.IsPaused) return;

        long timeMs   = _videoPlayer.TimeMs;
        long lengthMs = _videoPlayer.LengthMs;

        // Time label — suppress for live streams (length unknown or <= 0)
        bool hasLength = lengthMs > 0;
        string timeLabel = hasLength
            ? $"{FormatTime(timeMs)} / {FormatTime(lengthMs)}"
            : FormatTime(timeMs);
        ImGui.TextDisabled(timeLabel);

        float pos = _videoPlayer.Position;

        if (!hasLength) ImGui.BeginDisabled();
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderFloat("##scrub", ref pos, 0f, 1f, ""))
            _videoPlayer.Seek(pos);
        if (!hasLength) ImGui.EndDisabled();

        if (!hasLength && ImGui.IsItemHovered())
            ImGui.SetTooltip("Seeking not available for live streams.");

        // ── A-B loop controls ─────────────────────────────────────────────────
        if (ImGui.SmallButton("Set A")) _videoPlayer.SetLoopA();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Mark loop start at current position.");

        ImGui.SameLine();
        if (ImGui.SmallButton("Set B")) _videoPlayer.SetLoopB();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Mark loop end at current position.");

        ImGui.SameLine();

        bool loopOn = _videoPlayer.AbLoopActive;
        if (loopOn) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.15f, 0.65f, 0.15f, 1f));
        if (ImGui.SmallButton(loopOn ? "A-B: ON " : "A-B: OFF")) _videoPlayer.ToggleAbLoop();
        if (loopOn) ImGui.PopStyleColor();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Toggle A-B loop.");

        ImGui.SameLine();
        if (ImGui.SmallButton("Clear")) _videoPlayer.ClearAbLoop();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Clear A and B points.");

        // Show A/B timestamps when either point has been moved from default
        if (_videoPlayer.LoopA > 0f || _videoPlayer.LoopB < 1f)
        {
            string aStr = hasLength ? FormatTime((long)(_videoPlayer.LoopA * lengthMs)) : $"{_videoPlayer.LoopA:P0}";
            string bStr = hasLength ? FormatTime((long)(_videoPlayer.LoopB * lengthMs)) : $"{_videoPlayer.LoopB:P0}";
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
