using System.Numerics;

namespace Systemic.Kernel;

/// <summary>
/// The cell arithmetic every spatial grid in the kernel shares: how an axis is divided into cells,
/// how a coordinate maps to a cell, how cell windows are derived for wrapped and fixed bounds, and
/// how wrapped and unwrapped distances are measured.
/// </summary>
/// <remarks>
/// Grids differ only in how they store members (a rebuilt cell-offset table, an incremental bucket
/// grid, or a hashed table for unbounded coordinates). Query semantics do not: the same window,
/// wrap and distance rules must apply everywhere or two grids disagree about which members are in
/// range, which breaks determinism the moment a population and an entity share a world.
/// </remarks>
internal static class SpatialGrid
{
    /// <summary>The largest dense grid, in cells, any single grid in the kernel may allocate.</summary>
    public const int MaximumCellCount = 4_000_000;

    public static int GetCellCount(double size, float cellSize)
    {
        var count = Math.Ceiling(size / cellSize);
        if (count > MaximumCellCount)
        {
            throw new ArgumentOutOfRangeException(nameof(cellSize), "Grid axis exceeds the supported cell limit.");
        }

        return Math.Max(1, (int)count);
    }

    public static double Mod(double value, double modulus) => (value % modulus + modulus) % modulus;

    public static double AxisDistance(double center, double position, double size, bool wrap)
    {
        var distance = Math.Abs(center - position);
        if (!wrap)
        {
            return distance;
        }

        distance %= size;
        return Math.Min(distance, size - distance);
    }

    public static double DistanceSquared(Vector2 center, Vector2 position, Bounds2 bounds, bool wrapX, bool wrapY)
    {
        var dx = Math.Abs((double)center.X - position.X);
        var dy = Math.Abs((double)center.Y - position.Y);
        if (wrapX)
        {
            dx %= bounds.HalfWidth * 2d;
            dx = Math.Min(dx, (bounds.HalfWidth * 2d) - dx);
        }

        if (wrapY)
        {
            dy %= bounds.HalfHeight * 2d;
            dy = Math.Min(dy, (bounds.HalfHeight * 2d) - dy);
        }

        return (dx * dx) + (dy * dy);
    }

    /// <summary>Maps a coordinate inside a bounded, divided axis to its cell, clamped to the axis.</summary>
    public static int ToBoundedCell(double value, double minimum, double cellSize, int cellCount) =>
        Math.Clamp((int)((value - minimum) / cellSize), 0, cellCount - 1);

    /// <summary>Maps an unbounded coordinate to its cell; the grid has no axis to clamp against.</summary>
    public static int ToUnboundedCell(double value, double cellSize)
    {
        var cell = Math.Floor(value / cellSize);
        if (cell <= int.MinValue || cell >= int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Coordinate is outside the supported spatial range.");
        }

        return (int)cell;
    }

    /// <summary>
    /// The cell window covering <paramref name="center"/> ± <paramref name="extent"/> on one axis.
    /// A wrapped axis returns a window that may run past the final cell; <see cref="AxisCellRange.At"/>
    /// folds the overflow back to the start.
    /// </summary>
    public static AxisCellRange GetAxisCells(
        double center,
        double extent,
        double minimum,
        double size,
        double cellSize,
        int cellCount,
        bool wrap)
    {
        var start = center - extent;
        var end = center + extent;
        if (wrap)
        {
            if (extent * 2d >= size)
            {
                return new AxisCellRange(0, cellCount);
            }

            var startOffset = Mod((start - minimum) % size, size);
            var first = (int)Math.Floor(startOffset / cellSize);
            var endOffset = startOffset + (extent * 2d);
            var lastBeforeWrap = Math.Min(cellCount - 1, (int)Math.Floor(Math.Min(endOffset, size) / cellSize));
            var count = lastBeforeWrap - first + 1;
            if (endOffset >= size)
            {
                var wrappedEnd = endOffset - size;
                var wrappedCells = Math.Min(cellCount, (int)Math.Floor(wrappedEnd / cellSize) + 1);
                count += wrappedCells;
            }

            // Both endpoints are accepted positions. At the lower seam, the point stored at
            // the upper endpoint is equally close, so include the final cell as well.
            if (startOffset == 0 && count < cellCount)
            {
                first = cellCount - 1;
                count++;
            }

            count = Math.Min(cellCount, count);
            var firstWrapped = Math.Min(first, cellCount - 1);
            return new AxisCellRange(firstWrapped, count);
        }

        var maximum = minimum + size;
        if (end < minimum || start > maximum)
        {
            return default;
        }

        start = Math.Max(start, minimum);
        end = Math.Min(end, maximum);
        var firstCell = Math.Clamp((int)Math.Floor((start - minimum) / cellSize), 0, cellCount - 1);
        var lastCell = Math.Clamp((int)Math.Floor((end - minimum) / cellSize), 0, cellCount - 1);
        return new AxisCellRange(firstCell, lastCell - firstCell + 1);
    }

    public static void ValidateQuery(Vector2 center, float radius)
    {
        if (!float.IsFinite(center.X) || !float.IsFinite(center.Y) || !float.IsFinite(radius) || radius < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radius),
                "Query coordinates and radius must be finite; radius must be non-negative.");
        }
    }

    /// <summary>Appends a result, keeping the list sorted ascending and capped at <paramref name="maximumResults"/>.</summary>
    public static void AddResult(List<int> results, int row, int maximumResults)
    {
        if (maximumResults == int.MaxValue)
        {
            results.Add(row);
            return;
        }

        var count = results.Count;
        if (count < maximumResults)
        {
            var insertAt = count;
            while (insertAt > 0 && results[insertAt - 1] > row)
            {
                insertAt--;
            }

            results.Insert(insertAt, row);
            return;
        }

        if (row >= results[^1])
        {
            return;
        }

        var index = count - 1;
        while (index > 0 && results[index - 1] > row)
        {
            results[index] = results[index - 1];
            index--;
        }

        results[index] = row;
    }
}

/// <summary>A run of cells on one axis, starting at <see cref="Start"/> and possibly wrapping past the end.</summary>
internal readonly record struct AxisCellRange(int Start, int Count)
{
    public int At(int offset, int cellCount, bool wrap)
    {
        var index = Start + offset;
        return wrap && index >= cellCount ? index - cellCount : index;
    }
}
