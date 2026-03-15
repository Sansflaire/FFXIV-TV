using Dalamud.Configuration;

namespace FFXIVTv;

[System.Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>The single screen definition (Phase 1: one screen at a time).</summary>
    public ScreenDefinition Screen { get; set; } = new ScreenDefinition();

    /// <summary>
    /// Path to the image file currently displayed on the screen.
    /// Supports any file loadable by ITextureProvider (PNG, JPEG, etc.).
    /// Empty string = show a solid test color instead.
    /// </summary>
    public string ImagePath { get; set; } = string.Empty;

    /// <summary>Tint color applied to the displayed image (RGBA, 0–1 per channel).</summary>
    public float TintR { get; set; } = 1f;
    public float TintG { get; set; } = 1f;
    public float TintB { get; set; } = 1f;
    public float TintA { get; set; } = 1f;

    /// <summary>
    /// When true, draw the screen even when corners go behind the camera.
    /// Fixes the screen "disappearing" when viewed from steep angles.
    /// Uses the screen center visibility as the only culling check.
    /// </summary>
    public bool AlwaysDraw { get; set; } = true;

    /// <summary>
    /// When true, forces Phase 1 rendering (WorldToScreen + ImGui AddImageQuad).
    /// Image displays correctly but no depth testing — characters always render in front.
    /// Use this as a sandbox to compare Phase 1 vs Phase 2 output.
    /// </summary>
    public bool UsePhase1Sandbox { get; set; } = false;

    /// <summary>
    /// When true, draw a solid black backing rectangle behind the image/video.
    /// Ensures no transparency or see-through when the image has alpha.
    /// Draw order: black backing → image/video on top.
    /// </summary>
    public bool ShowBlackBacking { get; set; } = true;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
