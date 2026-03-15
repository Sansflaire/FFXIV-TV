using Dalamud.Configuration;

namespace FFXIVTv;

public enum ContentMode
{
    Image,
    LocalVideo,
    UrlVideo,
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

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
