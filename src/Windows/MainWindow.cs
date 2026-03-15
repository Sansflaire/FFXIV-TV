using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

namespace FFXIVTv.Windows;

public sealed class MainWindow
{
    public bool IsVisible { get; set; } = false;

    private readonly Configuration _config;
    private readonly IObjectTable _objectTable;

    // Temporary buffer for the image path text input.
    private string _imagePathBuffer = string.Empty;
    private bool _imagePathBufferDirty = true;

    public MainWindow(Configuration config, IObjectTable objectTable)
    {
        _config = config;
        _objectTable = objectTable;
    }

    public void Draw()
    {
        if (!IsVisible) return;

        ImGui.SetNextWindowSize(new Vector2(480, 420), ImGuiCond.FirstUseEver);
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
        DrawImageSection();
        ImGui.Spacing();
        DrawTintSection();

        ImGui.End();
    }

    // ─── Sections ────────────────────────────────────────────────────────────

    private void DrawScreenSection()
    {
        if (!ImGui.CollapsingHeader("Screen Transform", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var screen = _config.Screen;
        bool changed = false;

        // Visibility toggle
        bool vis = screen.Visible;
        if (ImGui.Checkbox("Visible", ref vis)) { screen.Visible = vis; changed = true; }

        ImGui.SameLine();

        // Always draw — don't cull on edge/corner angles
        bool always = _config.AlwaysDraw;
        if (ImGui.Checkbox("Always Draw", ref always)) { _config.AlwaysDraw = always; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Keep drawing even when corners go behind the camera.\nFixes screen disappearing at steep angles.");

        ImGui.SameLine();

        // Black backing
        bool backing = _config.ShowBlackBacking;
        if (ImGui.Checkbox("Black Backing", ref backing)) { _config.ShowBlackBacking = backing; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Draw a solid black rectangle behind the image.\nDraw order: black → image on top.");

        ImGui.SameLine();

        // Place at player position
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

        // Position sliders
        var center = screen.Center;
        if (ImGui.DragFloat3("Position (X/Y/Z)", ref center, 0.05f))
        {
            screen.Center = center;
            changed = true;
        }

        // Yaw slider
        float yaw = screen.YawDegrees;
        if (ImGui.SliderFloat("Yaw (degrees)", ref yaw, -180f, 180f))
        {
            screen.YawDegrees = yaw;
            changed = true;
        }

        // Size sliders
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

        // 16:9 lock button
        ImGui.SameLine();
        if (ImGui.SmallButton("Lock 16:9"))
        {
            screen.Height = screen.Width * 9f / 16f;
            changed = true;
        }

        if (changed) _config.Save();
    }

    private void DrawImageSection()
    {
        if (!ImGui.CollapsingHeader("Image Source", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        // Sync buffer from config on first draw or after external changes.
        if (_imagePathBufferDirty)
        {
            _imagePathBuffer = _config.ImagePath;
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
        if (ImGui.SmallButton("Apply"))
        {
            _config.ImagePath = _imagePathBuffer;
            _config.Save();
        }

        ImGui.TextDisabled("(Press Enter or click Apply to load)");
    }

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

