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

    /// <summary>Tint color applied to the displayed image (RGBA, 0–255 per channel).</summary>
    public float TintR { get; set; } = 1f;
    public float TintG { get; set; } = 1f;
    public float TintB { get; set; } = 1f;
    public float TintA { get; set; } = 1f;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
