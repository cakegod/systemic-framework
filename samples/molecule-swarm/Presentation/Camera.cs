using System.Numerics;

namespace MoleculeSwarm;

/// <summary>
/// What part of the field the window shows: a centre and a zoom. At zoom one the centre is the middle
/// of the field, which is the identity mapping every shader falls back to when the camera is not
/// moved, so the game looks exactly the same before anybody touches the wheel.
/// </summary>
internal readonly record struct Camera(Vector2 Center, float Zoom)
{
    public const float MinZoom = 0.55f;
    public const float MaxZoom = 2.6f;

    private static readonly Vector2 Middle = new(Sim.Width * 0.5f, Sim.Height * 0.5f);

    public static Camera Default { get; } = new(Middle, 1f);

    /// <summary>Screen point to field point, the inverse of what the shaders do.</summary>
    public Vector2 ToWorld(Vector2 screen) => Center + ((screen - Middle) / Zoom);

    public Camera ZoomedBy(float factor)
    {
        var zoom = Math.Clamp(Zoom * factor, MinZoom, MaxZoom);
        var half = new Vector2(Sim.Width / (2f * zoom), Sim.Height / (2f * zoom));
        return new Camera(Clamp(Center, half), zoom);
    }

    public Camera PannedBy(Vector2 delta)
    {
        var half = new Vector2(Sim.Width / (2f * Zoom), Sim.Height / (2f * Zoom));
        return new Camera(Clamp(Center + delta, half), Zoom);
    }

    /// <summary>
    /// Keeps the view inside the field: zoomed in, the edge of the view may not pass the edge of the
    /// field; zoomed out, the field is smaller than the window and the view is pinned on its middle.
    /// </summary>
    private static Vector2 Clamp(Vector2 center, Vector2 half)
    {
        var x = half.X >= Sim.Width * 0.5f
            ? Sim.Width * 0.5f
            : Math.Clamp(center.X, half.X, Sim.Width - half.X);
        var y = half.Y >= Sim.Height * 0.5f
            ? Sim.Height * 0.5f
            : Math.Clamp(center.Y, half.Y, Sim.Height - half.Y);
        return new Vector2(x, y);
    }
}
