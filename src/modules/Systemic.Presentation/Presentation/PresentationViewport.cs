using Systemic.Kernel;

namespace Systemic.Presentation;

/// <summary>
/// An engine-neutral, axis-aligned camera. At zoom 1 the world fits inside the screen rectangle
/// without distortion, with any unused space centered. Both rectangles use center and half extents.
/// </summary>
public sealed class PresentationViewport
{
    public PresentationViewport(Bounds2 worldBounds, Bounds2 screenBounds, Point2? cameraCenter = null, float zoom = 1)
    {
        if (!worldBounds.IsValid || worldBounds.HalfWidth <= 0 || worldBounds.HalfHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(worldBounds), "World bounds must have positive finite dimensions.");
        }

        if (!screenBounds.IsValid || screenBounds.HalfWidth <= 0 || screenBounds.HalfHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(screenBounds), "Screen bounds must have positive finite dimensions.");
        }

        if (!float.IsFinite(zoom) || zoom <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(zoom));
        }

        var center = cameraCenter ?? new Point2(worldBounds.X, worldBounds.Y);
        ValidatePoint(center, nameof(cameraCenter));
        var scale = Math.Min((double)screenBounds.HalfWidth / worldBounds.HalfWidth,
            (double)screenBounds.HalfHeight / worldBounds.HalfHeight) * zoom;
        if (!float.IsFinite((float)scale) || (float)scale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(zoom), "The resulting viewport scale is not representable.");
        }

        WorldBounds = worldBounds;
        ScreenBounds = screenBounds;
        Zoom = zoom;
        Scale = (float)scale;
        var visibleHalfWidth = (double)screenBounds.HalfWidth / Scale;
        var visibleHalfHeight = (double)screenBounds.HalfHeight / Scale;
        CameraCenter = new Point2(
            ClampCamera(center.X, worldBounds.X, worldBounds.HalfWidth, visibleHalfWidth),
            ClampCamera(center.Y, worldBounds.Y, worldBounds.HalfHeight, visibleHalfHeight));

        var left = Math.Max(worldBounds.Left, CameraCenter.X - visibleHalfWidth);
        var right = Math.Min(worldBounds.Right, CameraCenter.X + visibleHalfWidth);
        var top = Math.Max(worldBounds.Top, CameraCenter.Y - visibleHalfHeight);
        var bottom = Math.Min(worldBounds.Bottom, CameraCenter.Y + visibleHalfHeight);
        VisibleWorldBounds = new Bounds2((float)((left + right) / 2), (float)((top + bottom) / 2),
            (float)((right - left) / 2), (float)((bottom - top) / 2));
    }

    public Bounds2 WorldBounds { get; }
    public Bounds2 ScreenBounds { get; }
    public Bounds2 VisibleWorldBounds { get; }
    public Point2 CameraCenter { get; }
    public float Zoom { get; }
    public float Scale { get; }

    public PresentationViewport WithCamera(Point2 center) => new(WorldBounds, ScreenBounds, center, Zoom);
    public PresentationViewport WithZoom(float zoom) => new(WorldBounds, ScreenBounds, CameraCenter, zoom);

    public Point2 WorldToScreen(Point2 point)
    {
        ValidatePoint(point, nameof(point));
        return new Point2(
            CheckedFloat(ScreenBounds.X + ((double)point.X - CameraCenter.X) * Scale, nameof(point)),
            CheckedFloat(ScreenBounds.Y + ((double)point.Y - CameraCenter.Y) * Scale, nameof(point)));
    }

    public Point2 ScreenToWorld(Point2 point)
    {
        ValidatePoint(point, nameof(point));
        return new Point2(
            CheckedFloat(CameraCenter.X + ((double)point.X - ScreenBounds.X) / Scale, nameof(point)),
            CheckedFloat(CameraCenter.Y + ((double)point.Y - ScreenBounds.Y) / Scale, nameof(point)));
    }

    public Bounds2 WorldToScreen(Bounds2 bounds)
    {
        ValidateBounds(bounds);
        var center = WorldToScreen(new Point2(bounds.X, bounds.Y));
        return CheckedBounds(center, (double)bounds.HalfWidth * Scale, (double)bounds.HalfHeight * Scale);
    }

    public Bounds2 ScreenToWorld(Bounds2 bounds)
    {
        ValidateBounds(bounds);
        var center = ScreenToWorld(new Point2(bounds.X, bounds.Y));
        return CheckedBounds(center, (double)bounds.HalfWidth / Scale, (double)bounds.HalfHeight / Scale);
    }

    /// <summary>True when a world rectangle intersects both the world and screen view, including edges.</summary>
    public bool IsVisible(Bounds2 bounds)
    {
        ValidateBounds(bounds);
        return VisibleWorldBounds.Overlaps(bounds);
    }

    private static float ClampCamera(float requested, float worldCenter, float worldHalf, double viewHalf)
    {
        if (viewHalf >= worldHalf)
        {
            return worldCenter;
        }

        return (float)Math.Clamp((double)requested, worldCenter - worldHalf + viewHalf, worldCenter + worldHalf - viewHalf);
    }

    private static Bounds2 CheckedBounds(Point2 center, double halfWidth, double halfHeight)
    {
        var result = new Bounds2(center.X, center.Y, CheckedFloat(halfWidth, "bounds"), CheckedFloat(halfHeight, "bounds"));
        if (!result.IsValid)
        {
            throw new ArgumentOutOfRangeException("bounds", "The transformed bounds are not representable.");
        }

        return result;
    }

    private static float CheckedFloat(double value, string parameterName)
    {
        var result = (float)value;
        return float.IsFinite(result) ? result : throw new ArgumentOutOfRangeException(parameterName, "The transformed value is not representable.");
    }

    private static void ValidatePoint(Point2 point, string parameterName)
    {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
        {
            throw new ArgumentOutOfRangeException(parameterName, "Coordinates must be finite.");
        }
    }

    private static void ValidateBounds(Bounds2 bounds)
    {
        if (!bounds.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(bounds));
        }
    }
}
