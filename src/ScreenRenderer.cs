using System;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;

namespace FFXIVTv;

/// <summary>
/// Phase 1 renderer: projects screen world-space corners to screen space using
/// IGameGui.WorldToScreen, then draws on the ImGui background draw list.
///
/// Draw order per frame:
///   1. Black backing quad   (if ShowBlackBacking = true)
///   2. Image / video quad   (or purple placeholder if no texture loaded)
///
/// LIMITATION: ImGui overlay has no depth testing — characters always appear
/// BEHIND the screen regardless of world position. Phase 2 (D3DRenderer) fixes this.
/// </summary>
public sealed class ScreenRenderer : IDisposable
{
    private readonly IGameGui        _gameGui;
    private readonly ITextureProvider _textureProvider;

    // Cached shared texture — reloaded when ImagePath changes.
    private ISharedImmediateTexture? _sharedTexture;
    private string                   _loadedPath = string.Empty;

    // UV corners for a standard non-flipped quad (TL→TR→BR→BL).
    private static readonly Vector2 UV_TL = new(0f, 0f);
    private static readonly Vector2 UV_TR = new(1f, 0f);
    private static readonly Vector2 UV_BR = new(1f, 1f);
    private static readonly Vector2 UV_BL = new(0f, 1f);

    private const uint BLACK        = 0xFF000000u;
    private const uint PLACEHOLDER  = 0xFF8B008Bu; // dark magenta

    public ScreenRenderer(IGameGui gameGui, ITextureProvider textureProvider)
    {
        _gameGui         = gameGui;
        _textureProvider = textureProvider;
    }

    public void Dispose() => _sharedTexture = null;

    /// <summary>
    /// Returns the current texture wrap (updates if path changed).
    /// Called by Plugin when D3DRenderer is active — it handles drawing, we just supply the texture.
    /// Returns null if no image is loaded (D3DRenderer will skip drawing).
    /// </summary>
    public Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap? GetCurrentWrap(Configuration config)
    {
        RefreshTexture(config.ImagePath);
        return _sharedTexture?.GetWrapOrDefault();
    }

    /// <summary>
    /// Phase 1 full draw: WorldToScreen projection + ImGui quad (no depth testing).
    /// Used as fallback when D3DRenderer is unavailable, or for the placeholder.
    /// </summary>
    public void Draw(Configuration config)
    {
        var screen = config.Screen;
        if (!screen.Visible) return;

        RefreshTexture(config.ImagePath);

        if (!ProjectCorners(screen, config.AlwaysDraw,
                out var sTL, out var sTR, out var sBR, out var sBL)) return;

        var dl   = ImGui.GetBackgroundDrawList();
        uint tint = ToImGuiColor(config.TintR, config.TintG, config.TintB, config.TintA);

        if (config.ShowBlackBacking)
            dl.AddQuadFilled(sTL, sTR, sBR, sBL, BLACK);

        var wrap = _sharedTexture?.GetWrapOrDefault();
        if (wrap != null)
            dl.AddImageQuad(wrap.Handle, sTL, sTR, sBR, sBL, UV_TL, UV_TR, UV_BR, UV_BL, tint);
        else
            dl.AddQuadFilled(sTL, sTR, sBR, sBL, PLACEHOLDER);

#if DEBUG
        dl.AddCircleFilled(sTL, 4f, 0xFF0000FF);
        dl.AddCircleFilled(sTR, 4f, 0xFF00FF00);
        dl.AddCircleFilled(sBR, 4f, 0xFFFF0000);
        dl.AddCircleFilled(sBL, 4f, 0xFFFFFF00);
#endif
    }

    /// <summary>
    /// Draws only the black backing quad via ImGui (used in Phase 2 mode).
    /// </summary>
    public void DrawBlackBacking(Configuration config)
    {
        if (!config.ShowBlackBacking) return;
        var screen = config.Screen;
        if (!screen.Visible) return;
        if (!ProjectCorners(screen, config.AlwaysDraw,
                out var sTL, out var sTR, out var sBR, out var sBL)) return;
        ImGui.GetBackgroundDrawList().AddQuadFilled(sTL, sTR, sBR, sBL, BLACK);
    }

    /// <summary>
    /// Draws the purple placeholder quad when D3DRenderer is active but no texture is loaded.
    /// </summary>
    public void DrawPlaceholder(Configuration config)
    {
        var screen = config.Screen;
        if (!screen.Visible) return;

        if (!ProjectCorners(screen, config.AlwaysDraw,
                out var sTL, out var sTR, out var sBR, out var sBL)) return;

        var dl = ImGui.GetBackgroundDrawList();
        if (config.ShowBlackBacking)
            dl.AddQuadFilled(sTL, sTR, sBR, sBL, BLACK);
        dl.AddQuadFilled(sTL, sTR, sBR, sBL, PLACEHOLDER);
    }

    private bool ProjectCorners(ScreenDefinition screen, bool alwaysDraw,
        out System.Numerics.Vector2 sTL, out System.Numerics.Vector2 sTR,
        out System.Numerics.Vector2 sBR, out System.Numerics.Vector2 sBL)
    {
        sTL = sTR = sBR = sBL = default;
        var (wTL, wTR, wBR, wBL) = screen.GetWorldCorners();

        bool centerVisible = _gameGui.WorldToScreen(screen.Center, out _);
        if (!centerVisible && !alwaysDraw) return false;

        _gameGui.WorldToScreen(wTL, out sTL);
        _gameGui.WorldToScreen(wTR, out sTR);
        _gameGui.WorldToScreen(wBR, out sBR);
        _gameGui.WorldToScreen(wBL, out sBL);
        return true;
    }

    // ─── Private helpers ─────────────────────────────────────────────────────

    private void RefreshTexture(string path)
    {
        if (path == _loadedPath) return;

        _sharedTexture = null;
        _loadedPath    = path;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        try
        {
            _sharedTexture = _textureProvider.GetFromFile(new FileInfo(path));
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[FFXIV-TV] Failed to load texture '{path}': {ex.Message}");
        }
    }

    /// <summary>Converts 0–1 float RGBA to packed ImGui ABGR uint.</summary>
    private static uint ToImGuiColor(float r, float g, float b, float a)
    {
        uint ri = (uint)(Math.Clamp(r, 0f, 1f) * 255f);
        uint gi = (uint)(Math.Clamp(g, 0f, 1f) * 255f);
        uint bi = (uint)(Math.Clamp(b, 0f, 1f) * 255f);
        uint ai = (uint)(Math.Clamp(a, 0f, 1f) * 255f);
        return (ai << 24) | (bi << 16) | (gi << 8) | ri;
    }
}
