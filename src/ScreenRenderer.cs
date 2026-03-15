using System;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;

namespace FFXIVTv;

/// <summary>
/// Phase 1 renderer: projects screen world-space corners to screen space using
/// IGameGui.WorldToScreen, then draws a textured quad on the ImGui background draw list.
///
/// LIMITATION: This renders as a screen-space overlay (no depth testing).
/// Characters and geometry will NOT properly occlude the screen.
/// Phase 2 will replace this with a D3D11 world-space quad with proper depth.
/// </summary>
public sealed class ScreenRenderer : IDisposable
{
    private readonly IGameGui _gameGui;
    private readonly ITextureProvider _textureProvider;

    // Cached texture wrap — reloaded when ImagePath changes.
    private ISharedImmediateTexture? _sharedTexture;
    private string _loadedPath = string.Empty;

    // UV corners for a standard non-flipped quad.
    private static readonly Vector2 UV_TL = new(0f, 0f);
    private static readonly Vector2 UV_TR = new(1f, 0f);
    private static readonly Vector2 UV_BR = new(1f, 1f);
    private static readonly Vector2 UV_BL = new(0f, 1f);

    public ScreenRenderer(IGameGui gameGui, ITextureProvider textureProvider)
    {
        _gameGui = gameGui;
        _textureProvider = textureProvider;
    }

    public void Dispose()
    {
        _sharedTexture = null;
    }

    /// <summary>
    /// Called every ImGui frame. Renders the screen if visible and in front of the camera.
    /// Call this from Plugin.DrawUI inside the UiBuilder.Draw event.
    /// </summary>
    public void Draw(Configuration config)
    {
        var screen = config.Screen;
        if (!screen.Visible) return;

        // Ensure texture is loaded / reloaded if path changed.
        RefreshTexture(config.ImagePath);

        // Project all 4 world corners to screen space.
        var (wTL, wTR, wBR, wBL) = screen.GetWorldCorners();

        if (!_gameGui.WorldToScreen(wTL, out var sTL)) return;
        if (!_gameGui.WorldToScreen(wTR, out var sTR)) return;
        if (!_gameGui.WorldToScreen(wBR, out var sBR)) return;
        if (!_gameGui.WorldToScreen(wBL, out var sBL)) return;

        var dl = ImGui.GetBackgroundDrawList();
        uint tint = ToImGuiColor(config.TintR, config.TintG, config.TintB, config.TintA);

        // Get wrap every frame — Dalamud manages the lifetime, do not cache IDalamudTextureWrap.
        var wrap = _sharedTexture?.GetWrapOrDefault();
        if (wrap != null)
        {
            // Draw textured quad.
            dl.AddImageQuad(wrap.Handle, sTL, sTR, sBR, sBL,
                            UV_TL, UV_TR, UV_BR, UV_BL, tint);
        }
        else
        {
            // No texture loaded — draw a solid purple rectangle as a placeholder.
            dl.AddQuadFilled(sTL, sTR, sBR, sBL, 0xFF8B008B);
        }

        // Debug: draw corner dots so we can verify projection.
#if DEBUG
        dl.AddCircleFilled(sTL, 4f, 0xFF0000FF);
        dl.AddCircleFilled(sTR, 4f, 0xFF00FF00);
        dl.AddCircleFilled(sBR, 4f, 0xFFFF0000);
        dl.AddCircleFilled(sBL, 4f, 0xFFFFFF00);
#endif
    }

    // ─── Private helpers ─────────────────────────────────────────────────────

    private void RefreshTexture(string path)
    {
        if (path == _loadedPath) return;

        _sharedTexture = null;
        _loadedPath = path;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        try
        {
            _sharedTexture = _textureProvider.GetFromFile(new System.IO.FileInfo(path));
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[FFXIV-TV] Failed to load texture from '{path}': {ex.Message}");
        }
    }

    private static uint ToImGuiColor(float r, float g, float b, float a)
    {
        uint ri = (uint)(Math.Clamp(r, 0f, 1f) * 255f);
        uint gi = (uint)(Math.Clamp(g, 0f, 1f) * 255f);
        uint bi = (uint)(Math.Clamp(b, 0f, 1f) * 255f);
        uint ai = (uint)(Math.Clamp(a, 0f, 1f) * 255f);
        // ImGui color format: ABGR (little-endian RGBA in memory)
        return (ai << 24) | (bi << 16) | (gi << 8) | ri;
    }
}
