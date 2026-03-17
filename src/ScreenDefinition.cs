using System;
using System.Numerics;

namespace FFXIVTv;

/// <summary>
/// A virtual screen placed at a world-space position with configurable size and orientation.
/// Orientation is specified as Yaw/Pitch/Roll Euler angles (degrees).
/// </summary>
[System.Serializable]
public sealed class ScreenDefinition
{
    /// <summary>World-space center of the screen.</summary>
    public Vector3 Center { get; set; } = new Vector3(0f, 1.5f, 0f);

    /// <summary>Yaw rotation in degrees (rotation around world Y axis). 0 = facing +Z.</summary>
    public float YawDegrees { get; set; } = 0f;

    /// <summary>Pitch rotation in degrees (tilt up/down). 0 = vertical screen.</summary>
    public float PitchDegrees { get; set; } = 0f;

    /// <summary>Roll rotation in degrees (clockwise twist). 0 = upright.</summary>
    public float RollDegrees { get; set; } = 0f;

    /// <summary>Width of the screen in game units.</summary>
    public float Width { get; set; } = 4f;

    /// <summary>Height of the screen in game units.</summary>
    public float Height { get; set; } = 2.25f;

    /// <summary>Whether this screen is currently visible.</summary>
    public bool Visible { get; set; } = true;

    // ─── TRS matrix for the shader ───────────────────────────────────────────

    /// <summary>
    /// Builds the TRS matrix uploaded to the vertex shader as ScreenTransform.
    /// Scale: Width × Height (unit quad in local XY plane → world-space rect).
    /// Rotation: Yaw (Y), Pitch (X), Roll (Z) — applied in that order.
    /// Translation: moves to Center in world space.
    /// </summary>
    public Matrix4x4 ComputeScreenTransform()
    {
        float yaw   = YawDegrees   * MathF.PI / 180f;
        float pitch = PitchDegrees * MathF.PI / 180f;
        float roll  = RollDegrees  * MathF.PI / 180f;

        // Scale the unit quad to the configured width/height.
        // Thin Z scale (0.01) keeps it nearly flat while satisfying any box geometry.
        var scale       = Matrix4x4.CreateScale(Width, Height, 0.01f);
        var rotation    = Matrix4x4.CreateFromYawPitchRoll(yaw, pitch, roll);
        var translation = Matrix4x4.CreateTranslation(Center);

        return scale * rotation * translation;
    }

    // ─── Legacy corner helpers (kept for Phase 1 ScreenRenderer fallback) ────

    /// <summary>
    /// Right vector from Yaw (used by the Phase 1 ImGui fallback renderer).
    /// Yaw=0 → screen faces +Z, so right = +X.
    /// </summary>
    public Vector3 RightVector
    {
        get
        {
            float yawRad = YawDegrees * MathF.PI / 180f;
            return new Vector3(MathF.Cos(yawRad), 0f, -MathF.Sin(yawRad));
        }
    }

    /// <summary>Up vector — always world-up for the Phase 1 fallback.</summary>
    public static Vector3 UpVector => Vector3.UnitY;

    /// <summary>
    /// Computes four world-space corners using yaw-only rotation.
    /// Used by the Phase 1 ImGui fallback (ScreenRenderer).
    /// Order: top-left, top-right, bottom-right, bottom-left (clockwise from front).
    /// </summary>
    public (Vector3 TL, Vector3 TR, Vector3 BR, Vector3 BL) GetWorldCorners()
    {
        Vector3 right = RightVector * (Width / 2f);
        Vector3 up    = UpVector    * (Height / 2f);

        Vector3 tl = Center - right + up;
        Vector3 tr = Center + right + up;
        Vector3 br = Center + right - up;
        Vector3 bl = Center - right - up;

        return (tl, tr, br, bl);
    }
}
