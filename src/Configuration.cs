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

/// <summary>
/// World-render pipeline selection. Peer visibility is the axis that matters:
///
/// PyonPix       — port of priprii/PyonPix's RendererService written against our
///                 own HLSL. Same hook + RTV-scoring architecture, but shader math
///                 is a from-scratch approximation because upstream ships only
///                 compiled .cso bytecode (no HLSL source). Uses a 6-vert quad.
///
/// PyonPixExact  — same architecture, but loads PyonPix's actual compiled
///                 vsmain.cso / psmain.cso bytecode from embedded resources
///                 and mirrors their exact 288-byte ShaderParams cbuffer +
///                 36-vertex cube-shell draw + separated CameraView/Projection
///                 matrices (transposed for column-major HLSL). Highest-fidelity
///                 replica of the reference plugin — used when the from-scratch
///                 shader in "PyonPix" mode gets the target RTV right but the
///                 pixels are still wrong.
///
/// CopyBlit  — no game hooks. CopyResource depth + composite in a plugin-owned
///             offscreen RTV + blit via ImGui.AddImageQuad + native ATK addon
///             UI restore. Peer failure mode: see-through on peers, probably a
///             colorspace/alpha mismatch on the ImGui blit path.
///
/// Legacy    — the original D3DRenderer. Hooks DrawIndexed (slot 12) and
///             pattern-matches a specific LDR post-tonemap surface. Works on the
///             host, fails on peers with different GPU/settings/patch combos.
/// </summary>
public enum RenderingMode
{
    Legacy,
    CopyBlit,
    PyonPix,
    PyonPixExact,
}

public enum NetworkMode
{
    Off,
    Host,
    Client,
}

/// <summary>
/// Alpha-blend mode override for the BB-bind inject.
/// INERT in this build — not yet consulted by the renderer.
/// Auto  = let renderer pick (current behavior)
/// Hud   = InvDestAlpha (TV fills LDR alpha=0 areas, behind HUD)
/// Opaque= opaque-RGB preserve-alpha
/// </summary>
public enum AlphaMode
{
    Auto,
    Hud,
    Opaque,
}

/// <summary>
/// Depth-stencil mode override for the BB-bind inject.
/// INERT in this build — not yet consulted by the renderer.
/// Auto       = let renderer pick (current behavior)
/// ReadWrite  = GreaterEqual + write
/// ReadOnly   = GreaterEqual, no write
/// NoDepth    = ignore depth entirely
/// </summary>
public enum DepthMode
{
    Auto,
    ReadWrite,
    ReadOnly,
    NoDepth,
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

    /// <summary>Peak brightness limit applied during the scene depth-only inject pass.
    /// This no longer affects visual quality (the LDR inject handles color post-bloom).
    /// Range 0.0–1.0. Kept for backwards compatibility and potential future use.</summary>
    public float BloomCap { get; set; } = 0.35f;

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
    /// When true, draw a solid black backing rectangle behind the image/video.
    /// </summary>
    public bool ShowBlackBacking { get; set; } = true;

    /// <summary>
    /// When true, use the XMP-style CopyBlit renderer instead of the hook-based D3DRenderer.
    /// CopyBlit runs entirely at UiBuilder.Draw time, uses CopyResource on the game's depth
    /// buffer + a fullscreen-triangle pixel shader that samples the video and does a software
    /// depth test, then blits an offscreen RTV via ImGui.AddImage. Trades ~2 full-screen
    /// copies per frame of VRAM bandwidth for a render path that does not depend on
    /// pattern-matching a specific GPU/settings-dependent inject point, so it renders
    /// correctly on every peer's machine rather than only the host's.
    /// Default: true (the XMP-clone path is what peers see).
    /// </summary>
    public bool UseCopyBlitRenderer { get; set; } = true;

    /// <summary>
    /// Active rendering mode. Overrides UseCopyBlitRenderer when reading.
    /// v0.5.247 default flipped to PyonPixExact — the only mode confirmed to
    /// render peer-visible TV. Legacy has a known "grayscale-shadow" bug on
    /// non-host machines; CopyBlit shows a see-through rectangle for peers.
    /// PyonPixExact is safe as a default because the v0.5.224 crash root
    /// cause (hooking IDXGISwapChain::Present, already hooked by Dalamud)
    /// has long been fixed — we hook only OMSetRenderTargets slot 33 now.
    /// </summary>
    public RenderingMode RenderMode { get; set; } = RenderingMode.PyonPixExact;

    // ── Network Sync ──────────────────────────────────────────────────────────

    /// <summary>Current network sync role (Off / Host / Client).</summary>
    public NetworkMode SyncMode { get; set; } = NetworkMode.Off;

    /// <summary>Port the sync server listens on when in Host mode.</summary>
    public int SyncPort { get; set; } = 9834;

    /// <summary>Address the client connects to (tunnel URL or IP:port for LAN).</summary>
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

    // ── Debug / diagnostic overrides (INERT — not consulted by D3DRenderer yet) ──

    /// <summary>
    /// Alpha-blend mode override for the BB-bind inject. INERT — reserved for a future
    /// inject-path refactor. Default = Auto.
    /// </summary>
    public AlphaMode AlphaMode { get; set; } = AlphaMode.Auto;

    /// <summary>
    /// Depth-stencil mode override for the BB-bind inject. INERT — reserved for a future
    /// inject-path refactor. Default = Auto.
    /// </summary>
    public DepthMode DepthMode { get; set; } = DepthMode.Auto;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
