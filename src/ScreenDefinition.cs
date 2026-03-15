using System;
using System.Numerics;

namespace FFXIVTv;

/// <summary>
/// A virtual screen placed at a world-space position with configurable size and orientation.
/// The screen faces in the direction determined by Yaw (rotation around Y axis).
/// </summary>
[System.Serializable]
public sealed class ScreenDefinition
{
    /// <summary>World-space center of the screen.</summary>
    public Vector3 Center { get; set; } = new Vector3(0f, 1.5f, 0f);

    /// <summary>
    /// Yaw rotation in degrees (0 = facing +Z, 90 = facing +X).
    /// Controls which direction the screen face points.
    /// </summary>
    public float YawDegrees { get; set; } = 0f;

    /// <summary>Width of the screen in game units.</summary>
    public float Width { get; set; } = 4f;

    /// <summary>Height of the screen in game units.</summary>
    public float Height { get; set; } = 2.25f;

    /// <summary>Whether this screen is currently visible.</summary>
    public bool Visible { get; set; } = true;

    // ─── Derived geometry ────────────────────────────────────────────────────

    /// <summary>
    /// Returns the right vector (horizontal axis of the screen face) from Yaw.
    /// Yaw=0 → screen faces +Z, so right = +X.
    /// </summary>
    public Vector3 RightVector
    {
        get
        {
            float yawRad = YawDegrees * MathF.PI / 180f;
            // Facing direction: (sin(yaw), 0, cos(yaw))
            // Right is perpendicular on XZ plane: (cos(yaw), 0, -sin(yaw))
            return new Vector3(MathF.Cos(yawRad), 0f, -MathF.Sin(yawRad));
        }
    }

    /// <summary>Up vector is always world-up for a vertical screen.</summary>
    public static Vector3 UpVector => Vector3.UnitY;

    /// <summary>
    /// Computes the four world-space corners of the screen.
    /// Order: top-left, top-right, bottom-right, bottom-left  (clockwise from front).
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
