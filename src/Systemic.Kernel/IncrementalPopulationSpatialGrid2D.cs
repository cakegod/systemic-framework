using System.Numerics;

namespace Systemic.Kernel;

/// <summary>
/// A mutable, fixed-bounds grid of positions keyed by explicit, non-negative stable row IDs.
/// Each row occupies one cell. Add, update and remove take expected O(1) dictionary and link
/// operations, with occasional dictionary resizing; storage includes one head per cell and one
/// entry per row. Radius and bounds queries inspect overlapping cells and return ascending row IDs;
/// nearest queries visit expanding cell rings and stop when unvisited cells cannot improve the
/// result. They use O(k) caller storage, with O(count * k) worst-case comparison work.
/// </summary>
/// <remarks>
/// Bounds must have finite, positive dimensions, cell size must be finite and positive, and the
/// dense grid is limited to 4,000,000 cells. Positions must be finite and within both inclusive
/// bounds, even when an axis wraps; query centers may be outside the bounds. After a population
/// moves, adds, removes, compacts, or changes its row-to-ID mapping, callers must synchronously
/// update this index before querying. IDs are not automatically tracked or remapped. Mutations
/// become visible immediately. Concurrent reads are allowed only while there are no mutations;
/// do not mutate the grid from a filter or concurrently with a query. Filters must return a
/// consistent answer for a row throughout a query; filter invocation order is unspecified.
/// </remarks>
public sealed class IncrementalPopulationSpatialGrid2D
{
    private readonly Bounds2 _bounds;
    private readonly float _cellSize;
    private readonly bool _wrapX;
    private readonly bool _wrapY;
    private readonly int _cellsX;
    private readonly int _cellsY;
    private readonly int[] _cellHeads;
    private readonly Dictionary<int, Entry> _entries = [];
    private ulong _version;

    public IncrementalPopulationSpatialGrid2D(Bounds2 bounds, float cellSize, bool wrapX = false, bool wrapY = false)
    {
        if (!bounds.IsValid || bounds.HalfWidth <= 0 || bounds.HalfHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bounds), "Grid bounds must be finite and have positive dimensions.");
        }

        if (!float.IsFinite(cellSize) || cellSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cellSize));
        }

        _bounds = bounds;
        _cellSize = cellSize;
        _wrapX = wrapX;
        _wrapY = wrapY;
        _cellsX = SpatialGrid.GetCellCount(bounds.HalfWidth * 2d, cellSize);
        _cellsY = SpatialGrid.GetCellCount(bounds.HalfHeight * 2d, cellSize);
        var cellCount = (long)_cellsX * _cellsY;
        if (cellCount > SpatialGrid.MaximumCellCount)
        {
            throw new ArgumentOutOfRangeException(nameof(bounds), "Grid exceeds the supported dense cell limit.");
        }

        _cellHeads = new int[(int)cellCount];
        Array.Fill(_cellHeads, -1);
    }

    public Bounds2 Bounds => _bounds;
    public float CellSize => _cellSize;
    public int Count => _entries.Count;

    /// <summary>Adds a new row. Duplicate or negative IDs and invalid positions are rejected.</summary>
    public void Add(int row, Vector2 position)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ValidatePosition(position);
        var cell = GetCell(position);
        var head = _cellHeads[cell];
        _entries.Add(row, new Entry(position, cell, -1, head));
        if (head >= 0)
        {
            var previousHead = _entries[head];
            previousHead.Previous = row;
            _entries[head] = previousHead;
        }
        _cellHeads[cell] = row;
        _version++;
    }

    /// <summary>Moves an existing row. Missing rows and invalid positions are rejected.</summary>
    public void Update(int row, Vector2 position)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ValidatePosition(position);
        if (!_entries.TryGetValue(row, out var entry))
        {
            throw new KeyNotFoundException($"Row {row} is not in the spatial grid.");
        }

        var cell = GetCell(position);
        if (cell != entry.Cell)
        {
            Unlink(entry);
            entry.Cell = cell;
            entry.Previous = -1;
            entry.Next = _cellHeads[cell];
            if (entry.Next >= 0)
            {
                var previousHead = _entries[entry.Next];
                previousHead.Previous = row;
                _entries[entry.Next] = previousHead;
            }
            _cellHeads[cell] = row;
        }

        entry.Position = position;
        _entries[row] = entry;
        _version++;
    }

    /// <summary>Removes a row and returns false if that ID was absent.</summary>
    public bool Remove(int row)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        if (!_entries.TryGetValue(row, out var entry))
        {
            return false;
        }

        Unlink(entry);
        _entries.Remove(row);
        _version++;
        return true;
    }

    /// <summary>
    /// Clears and fills results with exact matches in ascending ID order. A finite non-negative
    /// radius includes points on its boundary. When capped, the smallest matching IDs are kept.
    /// </summary>
    public void QueryRadius(Vector2 center, float radius, List<int> results, int maximumResults = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumResults);
        SpatialGrid.ValidateQuery(center, radius);
        results.Clear();
        if (maximumResults == 0)
        {
            return;
        }

        GetCellRanges(center, radius, radius, out var xCells, out var yCells);
        var radiusSquared = (double)radius * radius;
        for (var yOffset = 0; yOffset < yCells.Count; yOffset++)
        {
            var y = yCells.At(yOffset, _cellsY, _wrapY);
            for (var xOffset = 0; xOffset < xCells.Count; xOffset++)
            {
                var x = xCells.At(xOffset, _cellsX, _wrapX);
                for (var row = _cellHeads[(y * _cellsX) + x]; row >= 0;)
                {
                    var entry = _entries[row];
                    if (DistanceSquared(center, entry.Position) <= radiusSquared)
                    {
                        SpatialGrid.AddResult(results, row, maximumResults);
                    }
                    row = entry.Next;
                }
            }
        }

        if (maximumResults == int.MaxValue)
        {
            results.Sort();
        }
    }

    /// <summary>Clears and fills results with rows in the inclusive bounds, in ascending ID order.</summary>
    public void QueryBounds(Bounds2 area, List<int> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (!area.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(area), "Query bounds must be finite and have non-negative extents.");
        }

        results.Clear();
        GetCellRanges(new Vector2(area.X, area.Y), area.HalfWidth, area.HalfHeight, out var xCells, out var yCells);
        for (var yOffset = 0; yOffset < yCells.Count; yOffset++)
        {
            var y = yCells.At(yOffset, _cellsY, _wrapY);
            for (var xOffset = 0; xOffset < xCells.Count; xOffset++)
            {
                var x = xCells.At(xOffset, _cellsX, _wrapX);
                for (var row = _cellHeads[(y * _cellsX) + x]; row >= 0;)
                {
                    var entry = _entries[row];
                    if (SpatialGrid.AxisDistance(area.X, entry.Position.X, _bounds.HalfWidth * 2d, _wrapX) <= area.HalfWidth
                        && SpatialGrid.AxisDistance(area.Y, entry.Position.Y, _bounds.HalfHeight * 2d, _wrapY) <= area.HalfHeight)
                    {
                        results.Add(row);
                    }
                    row = entry.Next;
                }
            }
        }
        results.Sort();
    }

    /// <summary>Finds the closest matching row, breaking ties by smallest ID; false writes -1.</summary>
    public bool TryQueryNearest(Vector2 center, out int row, Predicate<int>? filter = null) =>
        TryQueryNearest(center, null, out row, filter);

    /// <summary>Finds the closest matching row within the inclusive, finite radius.</summary>
    public bool TryQueryNearest(Vector2 center, float radius, out int row, Predicate<int>? filter = null) =>
        TryQueryNearest(center, (float?)radius, out row, filter);

    /// <summary>
    /// Writes up to k nearest matching IDs by distance then ID. The destination length also
    /// limits the count, and k must be non-negative. This uses no per-query scratch allocation.
    /// </summary>
    public int QueryKNearest(Vector2 center, int k, Span<int> results, Predicate<int>? filter = null) =>
        QueryKNearest(center, null, k, results, filter);

    /// <summary>Writes the nearest matching IDs within the inclusive, finite radius.</summary>
    public int QueryKNearest(Vector2 center, float radius, int k, Span<int> results, Predicate<int>? filter = null) =>
        QueryKNearest(center, (float?)radius, k, results, filter);

    private bool TryQueryNearest(Vector2 center, float? radius, out int row, Predicate<int>? filter)
    {
        Span<int> result = stackalloc int[1];
        var count = QueryKNearest(center, radius, 1, result, filter);
        row = count == 0 ? -1 : result[0];
        return count != 0;
    }

    private int QueryKNearest(Vector2 center, float? radius, int k, Span<int> results, Predicate<int>? filter)
    {
        SpatialGrid.ValidateQuery(center, radius ?? 0);
        ArgumentOutOfRangeException.ThrowIfNegative(k);
        var limit = Math.Min(k, results.Length);
        if (limit == 0 || _entries.Count == 0)
        {
            return 0;
        }

        var version = _version;
        var maximumDistance = radius.HasValue ? (double)radius.Value * radius.Value : double.PositiveInfinity;
        var count = 0;
        var cutoff = maximumDistance;
        var traversal = new SpatialCellRingTraversal(
            center, _bounds, _cellSize, _cellsX, _cellsY, _wrapX, _wrapY);
        while (traversal.MoveNext(cutoff, out var cell))
        {
            for (var row = _cellHeads[cell]; row >= 0;)
            {
                var entry = _entries[row];
                var nextRow = entry.Next;
                var distance = DistanceSquared(center, entry.Position);
                if (distance > maximumDistance)
                {
                    row = nextRow;
                    continue;
                }

                if (filter is not null)
                {
                    var accepted = filter(row);
                    if (version != _version)
                    {
                        throw new InvalidOperationException("The spatial grid was changed during a query.");
                    }
                    if (!accepted)
                    {
                        row = nextRow;
                        continue;
                    }
                }

                var index = count;
                if (index == limit)
                {
                    var last = results[limit - 1];
                    var lastDistance = DistanceSquared(center, _entries[last].Position);
                    if (distance > lastDistance || (distance == lastDistance && row >= last))
                    {
                        row = nextRow;
                        continue;
                    }
                    index--;
                }
                else
                {
                    count++;
                }

                while (index > 0)
                {
                    var previous = results[index - 1];
                    var previousDistance = DistanceSquared(center, _entries[previous].Position);
                    if (distance > previousDistance || (distance == previousDistance && row >= previous))
                    {
                        break;
                    }
                    results[index] = previous;
                    index--;
                }
                results[index] = row;
                if (count == limit)
                {
                    cutoff = Math.Min(maximumDistance, DistanceSquared(center, _entries[results[count - 1]].Position));
                }
                row = nextRow;
            }
        }
        return count;
    }

    private void Unlink(Entry entry)
    {
        if (entry.Previous < 0)
        {
            _cellHeads[entry.Cell] = entry.Next;
        }
        else
        {
            var previous = _entries[entry.Previous];
            previous.Next = entry.Next;
            _entries[entry.Previous] = previous;
        }
        if (entry.Next >= 0)
        {
            var next = _entries[entry.Next];
            next.Previous = entry.Previous;
            _entries[entry.Next] = next;
        }
    }

    private void ValidatePosition(Vector2 position)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y)
            || position.X < _bounds.Left || position.X > _bounds.Right
            || position.Y < _bounds.Top || position.Y > _bounds.Bottom)
        {
            throw new ArgumentOutOfRangeException(nameof(position), "Position must be finite and within the grid bounds.");
        }
    }

    private int GetCell(Vector2 position)
    {
        var x = Math.Clamp((int)(((double)position.X - _bounds.Left) / _cellSize), 0, _cellsX - 1);
        var y = Math.Clamp((int)(((double)position.Y - _bounds.Top) / _cellSize), 0, _cellsY - 1);
        return y * _cellsX + x;
    }

    private void GetCellRanges(Vector2 center, float extentX, float extentY,
        out AxisCellRange xCells, out AxisCellRange yCells)
    {
        xCells = SpatialGrid.GetAxisCells(center.X, extentX, _bounds.Left,
            _bounds.HalfWidth * 2d, _cellSize, _cellsX, _wrapX);
        yCells = SpatialGrid.GetAxisCells(center.Y, extentY, _bounds.Top,
            _bounds.HalfHeight * 2d, _cellSize, _cellsY, _wrapY);
    }

    private double DistanceSquared(Vector2 center, Vector2 position) =>
        SpatialGrid.DistanceSquared(center, position, _bounds, _wrapX, _wrapY);

    private struct Entry(Vector2 position, int cell, int previous, int next)
    {
        public Vector2 Position = position;
        public int Cell = cell;
        public int Previous = previous;
        public int Next = next;
    }
}
