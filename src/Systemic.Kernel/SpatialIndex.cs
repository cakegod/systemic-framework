namespace Systemic.Kernel;

public readonly record struct Point2(float X, float Y);

/// <summary>An axis-aligned box represented by its center and non-negative half extents.</summary>
public readonly record struct Bounds2(float X, float Y, float HalfWidth, float HalfHeight)
{
    public float Left => X - HalfWidth;
    public float Right => X + HalfWidth;
    public float Top => Y - HalfHeight;
    public float Bottom => Y + HalfHeight;

    public bool Overlaps(Bounds2 other) =>
        Left <= other.Right && Right >= other.Left
        && Top <= other.Bottom && Bottom >= other.Top;

    public bool IsValid => float.IsFinite(X) && float.IsFinite(Y)
        && float.IsFinite(HalfWidth) && float.IsFinite(HalfHeight)
        && HalfWidth >= 0 && HalfHeight >= 0
        && float.IsFinite(Left) && float.IsFinite(Right)
        && float.IsFinite(Top) && float.IsFinite(Bottom);

    internal double DistanceSquaredTo(Point2 point)
    {
        var dx = Math.Max(Math.Max((double)Left - point.X, 0), (double)point.X - Right);
        var dy = Math.Max(Math.Max((double)Top - point.Y, 0), (double)point.Y - Bottom);
        return (dx * dx) + (dy * dy);
    }
}

/// <summary>
/// Uniform-grid broadphase with exact overlap/distance filtering. Query buffers are caller-owned;
/// results are returned once each in ascending entity ID order for stable simulation behavior.
/// </summary>
public sealed class SpatialIndex
{
    private const long MaximumCellsPerEntry = 16_384;
    private const long DefaultMaximumCellMemberships = 1_000_000;
    private readonly float _cellSize;
    private readonly long _maximumCellMemberships;
    private readonly Dictionary<Cell, HashSet<int>> _cells = [];
    private readonly Dictionary<int, Entry> _entries = [];
    private readonly Dictionary<int, int> _queryMarks = [];
    private long _cellMembershipCount;
    private int _queryVersion;

    public SpatialIndex(float cellSize, long maximumCellMemberships = DefaultMaximumCellMemberships)
    {
        if (!float.IsFinite(cellSize) || cellSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cellSize));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCellMemberships);
        _cellSize = cellSize;
        _maximumCellMemberships = maximumCellMemberships;
    }

    public int Count => _entries.Count;

    internal void ValidateBounds(Bounds2 bounds)
    {
        Validate(bounds);
        _ = CreateEntry(bounds);
    }

    internal Entry ValidateUpsert(int entityId, Bounds2 bounds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(entityId);
        Validate(bounds);
        var next = CreateEntry(bounds);
        var previousCellCount = _entries.TryGetValue(entityId, out var previous) ? previous.CellCount : 0;
        if (_cellMembershipCount - previousCellCount + next.CellCount > _maximumCellMemberships)
        {
            throw new ArgumentOutOfRangeException(nameof(bounds), "Spatial index exceeds its total cell-membership limit.");
        }

        return next;
    }

    public void Upsert(int entityId, Bounds2 bounds)
    {
        var next = ValidateUpsert(entityId, bounds);
        if (_entries.TryGetValue(entityId, out var previous))
        {
            if (previous == next)
            {
                return;
            }

            RemoveFromCells(entityId, previous);
        }

        _entries[entityId] = next;
        AddToCells(entityId, next);
    }

    public bool Remove(int entityId)
    {
        if (!_entries.Remove(entityId, out var entry))
        {
            return false;
        }

        RemoveFromCells(entityId, entry);
        return true;
    }

    public bool TryGetBounds(int entityId, out Bounds2 bounds)
    {
        if (_entries.TryGetValue(entityId, out var entry))
        {
            bounds = entry.Bounds;
            return true;
        }

        bounds = default;
        return false;
    }

    public void QueryOverlaps(Bounds2 area, List<int> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        Validate(area);
        BeginQuery(results);
        CollectCandidates(CreateEntry(area), area, default, 0, checkDistance: false, results);
        results.Sort();
    }

    public void QueryNearby(Point2 point, float radius, List<int> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)
            || !float.IsFinite(radius) || radius < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), "Point and radius must be finite; radius must be non-negative.");
        }

        BeginQuery(results);
        var area = new Bounds2(point.X, point.Y, radius, radius);
        CollectCandidates(CreateEntry(area), default, point, (double)radius * radius, checkDistance: true, results);
        results.Sort();
    }

    internal void Clear()
    {
        _cells.Clear();
        _entries.Clear();
        _queryMarks.Clear();
        _cellMembershipCount = 0;
        _queryVersion = 0;
    }

    private void BeginQuery(List<int> results)
    {
        results.Clear();
        if (_queryVersion == int.MaxValue)
        {
            _queryMarks.Clear();
            _queryVersion = 0;
        }

        _queryVersion++;
    }

    private void CollectCandidates(
        Entry range,
        Bounds2 overlap,
        Point2 point,
        double radiusSquared,
        bool checkDistance,
        List<int> results)
    {
        for (var y = range.MinY; y <= range.MaxY; y++)
        {
            for (var x = range.MinX; x <= range.MaxX; x++)
            {
                if (!_cells.TryGetValue(new Cell(x, y), out var bucket))
                {
                    continue;
                }

                foreach (var entityId in bucket)
                {
                    if (_queryMarks.TryGetValue(entityId, out var markedVersion) && markedVersion == _queryVersion)
                    {
                        continue;
                    }

                    _queryMarks[entityId] = _queryVersion;
                    var bounds = _entries[entityId].Bounds;
                    if (checkDistance)
                    {
                        if (bounds.DistanceSquaredTo(point) <= radiusSquared)
                        {
                            results.Add(entityId);
                        }
                    }
                    else if (bounds.Overlaps(overlap))
                    {
                        results.Add(entityId);
                    }
                }
            }
        }
    }

    private Entry CreateEntry(Bounds2 bounds)
    {
        var minX = ToCell(bounds.Left);
        var minY = ToCell(bounds.Top);
        var maxX = ToCell(bounds.Right);
        var maxY = ToCell(bounds.Bottom);
        var cellWidth = (long)maxX - minX + 1;
        var cellHeight = (long)maxY - minY + 1;
        if (cellWidth > MaximumCellsPerEntry || cellHeight > MaximumCellsPerEntry
            || cellWidth * cellHeight > MaximumCellsPerEntry)
        {
            throw new ArgumentOutOfRangeException(nameof(bounds), "Bounds cover too many spatial cells.");
        }

        return new Entry(bounds, minX, minY, maxX, maxY, cellWidth * cellHeight);
    }

    private int ToCell(float value)
    {
        var cell = Math.Floor(value / _cellSize);
        if (cell <= int.MinValue || cell >= int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Coordinate is outside the supported spatial range.");
        }

        return (int)cell;
    }

    private static void Validate(Bounds2 bounds)
    {
        if (!bounds.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(bounds), "Bounds must be finite and have non-negative extents.");
        }
    }

    private void AddToCells(int entityId, Entry entry)
    {
        for (var y = entry.MinY; y <= entry.MaxY; y++)
        {
            for (var x = entry.MinX; x <= entry.MaxX; x++)
            {
                var cell = new Cell(x, y);
                if (!_cells.TryGetValue(cell, out var bucket))
                {
                    bucket = [];
                    _cells.Add(cell, bucket);
                }

                bucket.Add(entityId);
                _cellMembershipCount++;
            }
        }
    }

    private void RemoveFromCells(int entityId, Entry entry)
    {
        for (var y = entry.MinY; y <= entry.MaxY; y++)
        {
            for (var x = entry.MinX; x <= entry.MaxX; x++)
            {
                var cell = new Cell(x, y);
                if (_cells.TryGetValue(cell, out var bucket) && bucket.Remove(entityId))
                {
                    _cellMembershipCount--;
                    if (bucket.Count == 0)
                    {
                        _cells.Remove(cell);
                    }
                }
            }
        }
    }

    private readonly record struct Cell(int X, int Y);
    internal readonly record struct Entry(Bounds2 Bounds, int MinX, int MinY, int MaxX, int MaxY, long CellCount);
}
