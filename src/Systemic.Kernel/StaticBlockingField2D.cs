using System.Collections.ObjectModel;

namespace Systemic.Kernel;

/// <summary>An integer cell coordinate, with the origin at the top-left of the field.</summary>
public readonly record struct GridCell2D(int X, int Y);

/// <summary>Controls whether cells beyond the finite mask are considered blocked.</summary>
public enum GridOutOfBoundsPolicy
{
    Blocked,
    Free
}

/// <summary>
/// An immutable, finite grid of blocked cells. Coordinates are zero-based and the mask is
/// row-major. The source cells are copied during construction. Connectivity is always bounded,
/// even when out-of-bounds queries are configured as free.
/// </summary>
public sealed class StaticBlockingField2D
{
    private const int MaximumCellCount = 4_000_000;
    private readonly bool[] _blocked;
    private readonly ReadOnlyCollection<bool> _blockedMask;

    /// <summary>Creates a field with positive dimensions and at most 4,000,000 cells.</summary>
    public StaticBlockingField2D(
        int width,
        int height,
        IEnumerable<GridCell2D> blockedCells,
        GridOutOfBoundsPolicy outOfBoundsPolicy)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(blockedCells);
        if ((long)width * height > MaximumCellCount)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "The field exceeds the supported dense cell limit.");
        }

        if (outOfBoundsPolicy is not GridOutOfBoundsPolicy.Blocked and not GridOutOfBoundsPolicy.Free)
        {
            throw new ArgumentOutOfRangeException(nameof(outOfBoundsPolicy));
        }

        Width = width;
        Height = height;
        OutOfBoundsPolicy = outOfBoundsPolicy;
        _blocked = new bool[width * height];
        foreach (var cell in blockedCells)
        {
            if (!IsInBounds(cell))
            {
                throw new ArgumentOutOfRangeException(nameof(blockedCells), "Blocked cells must lie within the field.");
            }

            _blocked[Index(cell)] = true;
        }

        _blockedMask = Array.AsReadOnly(_blocked);
    }

    public int Width { get; }
    public int Height { get; }
    public GridOutOfBoundsPolicy OutOfBoundsPolicy { get; }

    /// <summary>A read-only row-major snapshot, where true denotes a blocked cell.</summary>
    public IReadOnlyList<bool> BlockedMask => _blockedMask;

    public bool IsInBounds(GridCell2D cell) =>
        (uint)cell.X < (uint)Width && (uint)cell.Y < (uint)Height;

    public bool IsBlocked(GridCell2D cell) =>
        IsInBounds(cell) ? _blocked[Index(cell)] : OutOfBoundsPolicy == GridOutOfBoundsPolicy.Blocked;

    /// <summary>
    /// Tests the line between cell centers against all touched cells, including both side cells
    /// at an exact corner crossing. The out-of-bounds policy applies to every touched cell.
    /// All geometry uses integer arithmetic.
    /// </summary>
    public bool HasLineOfSight(GridCell2D start, GridCell2D end)
    {
        if (IsBlocked(start) || IsBlocked(end))
        {
            return false;
        }

        var dx = Math.Abs((long)end.X - start.X);
        var dy = Math.Abs((long)end.Y - start.Y);
        // A long segment through free outside space can have billions of cells. Testing each
        // bounded obstacle against the segment keeps that case bounded by the mask size.
        if (dx + dy > (2L * _blocked.Length) + Width + Height)
        {
            return HasLineOfSightByMask(start, end);
        }

        var stepX = Math.Sign((long)end.X - start.X);
        var stepY = Math.Sign((long)end.Y - start.Y);
        var x = start.X;
        var y = start.Y;
        long traversedX = 0;
        long traversedY = 0;
        while (traversedX < dx || traversedY < dy)
        {
            var xBoundary = ((Int128)(2 * traversedX + 1)) * dy;
            var yBoundary = ((Int128)(2 * traversedY + 1)) * dx;
            if (xBoundary == yBoundary)
            {
                // The segment touches the corner of all four neighboring cells.
                if (IsBlocked(new GridCell2D(x + stepX, y)) ||
                    IsBlocked(new GridCell2D(x, y + stepY)))
                {
                    return false;
                }

                x += stepX;
                y += stepY;
                traversedX++;
                traversedY++;
            }
            else if (xBoundary < yBoundary)
            {
                x += stepX;
                traversedX++;
            }
            else
            {
                y += stepY;
                traversedY++;
            }

            if (IsBlocked(new GridCell2D(x, y)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Returns the in-bounds connected component in breadth-first order, visiting north, east,
    /// south, then west. A blocked or out-of-bounds start yields an empty snapshot.
    /// </summary>
    public IReadOnlyList<GridCell2D> GetConnectedCells(GridCell2D start)
    {
        if (!IsInBounds(start) || IsBlocked(start))
        {
            return Array.AsReadOnly(Array.Empty<GridCell2D>());
        }

        var visited = new bool[_blocked.Length];
        var cells = new List<GridCell2D> { start };
        visited[Index(start)] = true;
        for (var i = 0; i < cells.Count; i++)
        {
            var current = cells[i];
            AddIfFree(new GridCell2D(current.X, current.Y - 1));
            AddIfFree(new GridCell2D(current.X + 1, current.Y));
            AddIfFree(new GridCell2D(current.X, current.Y + 1));
            AddIfFree(new GridCell2D(current.X - 1, current.Y));
        }

        return Array.AsReadOnly(cells.ToArray());

        void AddIfFree(GridCell2D candidate)
        {
            if (!IsInBounds(candidate))
            {
                return;
            }

            var index = Index(candidate);
            if (!visited[index] && !_blocked[index])
            {
                visited[index] = true;
                cells.Add(candidate);
            }
        }
    }

    /// <summary>Tests four-way connectivity within the finite field.</summary>
    public bool AreConnected(GridCell2D start, GridCell2D end) =>
        IsInBounds(end) && !IsBlocked(end) && GetConnectedCells(start).Contains(end);

    /// <summary>
    /// Finds the in-bounds free cell with the least Manhattan distance to the requested coordinate.
    /// Ties choose the lowest y, then x. Returns false if the field has no free cells; reachability
    /// from a different cell is not considered.
    /// </summary>
    public bool TryFindNearestFree(GridCell2D desired, out GridCell2D nearest)
    {
        nearest = default;
        var found = false;
        var bestDistance = long.MaxValue;
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                if (_blocked[(y * Width) + x])
                {
                    continue;
                }

                var distance = Math.Abs((long)x - desired.X) + Math.Abs((long)y - desired.Y);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    nearest = new GridCell2D(x, y);
                    found = true;
                }
            }
        }

        return found;
    }

    private int Index(GridCell2D cell) => (cell.Y * Width) + cell.X;

    private bool HasLineOfSightByMask(GridCell2D start, GridCell2D end)
    {
        // Coordinates are doubled so each cell has a closed, integer-aligned square. A segment
        // intersects that square exactly when their projections overlap on the x, y, and segment
        // normal axes. Int128 prevents overflow even at opposite Int32 coordinate extremes.
        var sx = (long)start.X * 2;
        var sy = (long)start.Y * 2;
        var ex = (long)end.X * 2;
        var ey = (long)end.Y * 2;
        var dx = ex - sx;
        var dy = ey - sy;
        var minX = Math.Min(sx, ex);
        var maxX = Math.Max(sx, ex);
        var minY = Math.Min(sy, ey);
        var maxY = Math.Max(sy, ey);
        var maximumCross = (Int128)Math.Abs(dx) + Math.Abs(dy);
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                if (!_blocked[(y * Width) + x])
                {
                    continue;
                }

                var centerX = (long)x * 2;
                var centerY = (long)y * 2;
                if (maxX < centerX - 1 || minX > centerX + 1 ||
                    maxY < centerY - 1 || minY > centerY + 1)
                {
                    continue;
                }

                var cross = (Int128)dx * (centerY - sy) - (Int128)dy * (centerX - sx);
                if (Int128.Abs(cross) <= maximumCross)
                {
                    return false;
                }
            }
        }

        return true;
    }
}
