using System.Collections.Generic;
using Dalamud.Configuration;

namespace FFXIVTv;

public enum ContentMode
{
    Image,
    LocalVideo,
    UrlVideo,
    Browser,
}

public enum NetworkMode
{
    Off,
    Host,
    Client,
}

[System.Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>The single screen definition (Phase 1: one screen at a time).</summary>
    public ScreenDefinition Screen { get; set; } = new ScreenDefinition();

    /// <summary>Which content source is currently active on the screen.</summary>
    public ContentMode ActiveMode { get; set; } = ContentMode.Image;

    /// <summary>
    /// Path to the image file displayed when ActiveMode == Image.
    /// Supports any file loadable by System.Drawing (PNG, JPEG, etc.).
    /// Empty string = show solid placeholder.
    /// </summary>
    public string ImagePath { get; set; } = string.Empty;

    /// <summary>Path to a local video file displayed when ActiveMode == LocalVideo.</summary>
    public string VideoPath { get; set; } = string.Empty;

    /// <summary>URL of a video stream displayed when ActiveMode == UrlVideo.
    /// Supports direct HTTP video URLs and YouTube links (requires yt-dlp).</summary>
    public string VideoUrl { get; set; } = string.Empty;

    /// <summary>
    /// Optional explicit path to yt-dlp.exe for YouTube URL resolution.
    /// Empty = auto-discover: plugin dir first, then system PATH.
    /// </summary>
    public string YtDlpPath { get; set; } = string.Empty;

    /// <summary>URL navigated to when ActiveMode == Browser. Persisted across reloads.</summary>
    public string BrowserUrl { get; set; } = string.Empty;

    /// <summary>
    /// Brightness multiplier applied to the rendered content. 1.0 = original. Range 0–4.
    /// Applied in the pixel shader; 0 = black, 2 = double brightness, etc.
    /// </summary>
    public float Brightness { get; set; } = 1.0f;

    /// <summary>Gamma power curve. 1.0 = no change. >1 darkens midtones; &lt;1 lifts them. Range 0.1–3.0.</summary>
    public float Gamma { get; set; } = 1.0f;

    /// <summary>Contrast around 0.5 midpoint. 1.0 = no change. >1 = more contrast. Range 0.0–3.0.</summary>
    public float Contrast { get; set; } = 1.0f;

    /// <summary>Output scale multiplier applied in the pixel shader before writing to the render target.
    /// We inject into the post-tonemap LDR intermediate (sRGB surface); 1.0 = correct sRGB output.
    /// Only needs adjustment if colors look wrong; range 0.01–1.0.</summary>
    public float HdrScale { get; set; } = 1.0f;

    /// <summary>Tint color applied to the displayed image/video (RGBA, 0–1 per channel).</summary>
    public float TintR { get; set; } = 1f;
    public float TintG { get; set; } = 1f;
    public float TintB { get; set; } = 1f;
    public float TintA { get; set; } = 1f;

    /// <summary>
    /// When true, draw the screen even when corners go behind the camera.
    /// Fixes the screen "disappearing" when viewed from steep angles.
    /// </summary>
    public bool AlwaysDraw { get; set; } = true;

    /// <summary>
    /// When true, forces Phase 1 rendering (WorldToScreen + ImGui AddImageQuad).
    /// Image displays correctly but no depth testing — characters always render in front.
    /// </summary>
    public bool UsePhase1Sandbox { get; set; } = false;

    /// <summary>
    /// When true, draw a solid black backing rectangle behind the image/video.
    /// </summary>
    public bool ShowBlackBacking { get; set; } = true;

    // ── Network Sync ──────────────────────────────────────────────────────────

    /// <summary>Current network sync role (Off / Host / Client).</summary>
    public NetworkMode SyncMode { get; set; } = NetworkMode.Off;

    /// <summary>Port the sync server listens on when in Host mode.</summary>
    public int SyncPort { get; set; } = 9834;

    /// <summary>IP:port string the sync client connects to when in Client mode (e.g. "192.168.1.5:9834").</summary>
    public string SyncHostAddress { get; set; } = string.Empty;

    /// <summary>Whether the sync server should be running. Persisted so it survives plugin reloads.</summary>
    public bool SyncServerRunning { get; set; } = false;

    /// <summary>Playback volume 0–100. Local only — not synced to clients.</summary>
    public int Volume { get; set; } = 100;

    /// <summary>When true, audio is muted. Local only — not synced to clients.</summary>
    public bool Muted { get; set; } = false;

    // ── Playlist ──────────────────────────────────────────────────────────────

    /// <summary>Ordered list of file paths / URLs to play in sequence.</summary>
    public List<string> Playlist { get; set; } = new List<string>();

    /// <summary>Index of the currently active playlist item. -1 = no active item.</summary>
    public int PlaylistIndex { get; set; } = -1;

    /// <summary>When true, the playlist wraps back to item 0 after the last item finishes.</summary>
    public bool PlaylistLoop { get; set; } = true;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
