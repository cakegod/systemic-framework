using System.Numerics;

namespace Systemic.Kernel;

/// <summary>Controls how a spatial-grid rebuild handles positions outside the configured bounds.</summary>
public enum InvalidPositionPolicy
{
    /// <summary>Reject the rebuild and retain the previously published grid snapshot.</summary>
    Throw,

    /// <summary>Skip invalid rows while keeping each accepted row's original population index.</summary>
    Skip,
}

/// <summary>
/// Dense fixed-bounds grid for row-indexed populations. Rebuild once per simulation phase, then
/// reuse the returned snapshot for allocation-free radius and axis-aligned queries.
/// </summary>
public sealed class PopulationSpatialGrid2D
{
    private readonly Bounds2 _bounds;
    private readonly float _cellSize;
    private readonly bool _wrapX;
    private readonly bool _wrapY;
    private readonly int _cellsX;
    private readonly int _cellsY;
    private readonly int[] _cellHeads;
    private Vector2[] _positions = [];
    private int[] _nextByRow = [];
    private int[] _cellXByRow = [];
    private int[] _cellYByRow = [];
    private int[] _buildCellXByRow = [];
    private int[] _buildCellYByRow = [];
    private int _rowCount;
    private int _version;

    public PopulationSpatialGrid2D(
        Bounds2 bounds,
        float cellSize,
        bool wrapX = false,
        bool wrapY = false)
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
    }

    public Bounds2 Bounds => _bounds;
    public float CellSize => _cellSize;
    public int Count => _rowCount;

    /// <summary>Number of invalid rows skipped by the most recent successful rebuild.</summary>
    public int InvalidPositionCount { get; private set; }

    /// <summary>
    /// Finds the closest matching row, or returns false and row -1 when there is no match.
    /// Equal distances are resolved by the smallest row index. The optional row filter must give
    /// consistent results throughout a query and must not rebuild the grid. Filter invocation
    /// order is unspecified.
    /// </summary>
    public bool TryQueryNearest(Vector2 center, out int row, Predicate<int>? filter = null) =>
        TryQueryNearest(_version, center, null, out row, filter);

    /// <summary>Finds the closest matching row within the inclusive, finite radius.</summary>
    public bool TryQueryNearest(Vector2 center, float radius, out int row, Predicate<int>? filter = null) =>
        TryQueryNearest(_version, center, radius, out row, filter);

    /// <summary>
    /// Writes up to k nearest matching rows, ordered by distance and then row index. The number
    /// written is also bounded by the destination length; k must be non-negative. Expanding cell
    /// rings stop once their conservative distance bound cannot improve the result. The worst case
    /// still visits every cell and row, and insertion uses O(count * k) worst-case comparisons.
    /// The destination is scratch storage; the query makes no per-query allocations.
    /// </summary>
    public int QueryKNearest(Vector2 center, int k, Span<int> results, Predicate<int>? filter = null) =>
        QueryKNearest(_version, center, null, k, results, filter);

    /// <summary>Writes the nearest rows whose distance is within the inclusive, finite radius.</summary>
    public int QueryKNearest(Vector2 center, float radius, int k, Span<int> results, Predicate<int>? filter = null) =>
        QueryKNearest(_version, center, radius, k, results, filter);

    internal bool TryQueryNearest(int version, Vector2 center, float? radius, out int row, Predicate<int>? filter)
    {
        Span<int> result = stackalloc int[1];
        var count = QueryKNearest(version, center, radius, 1, result, filter);
        row = count == 0 ? -1 : result[0];
        return count != 0;
    }

    internal int QueryKNearest(int version, Vector2 center, float? radius, int k, Span<int> results, Predicate<int>? filter)
    {
        ValidateSnapshot(version);
        SpatialGrid.ValidateQuery(center, radius ?? 0);
        ArgumentOutOfRangeException.ThrowIfNegative(k);
        var limit = Math.Min(k, results.Length);
        if (limit == 0 || _rowCount == 0)
        {
            return 0;
        }

        var maximumDistance = radius.HasValue ? (double)radius.Value * radius.Value : double.PositiveInfinity;
        var count = 0;
        var cutoff = maximumDistance;
        var traversal = new SpatialCellRingTraversal(center, _bounds, _cellSize, _cellsX, _cellsY, _wrapX, _wrapY);
        while (traversal.MoveNext(cutoff, out var cell))
        {
            for (var row = _cellHeads[cell]; row >= 0; row = _nextByRow[row])
            {
                var distance = DistanceSquared(center, _positions[row]);
                if (distance > maximumDistance)
                {
                    continue;
                }

                if (filter is not null)
                {
                    var accepted = filter(row);
                    ValidateSnapshot(version);
                    if (!accepted)
                    {
                        continue;
                    }
                }

                var index = count;
                if (index == limit)
                {
                    var last = results[limit - 1];
                    var lastDistance = DistanceSquared(center, _positions[last]);
                    if (distance > lastDistance || (distance == lastDistance && row >= last))
                    {
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
                    var previousDistance = DistanceSquared(center, _positions[previous]);
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
                    cutoff = Math.Min(maximumDistance, DistanceSquared(center, _positions[results[count - 1]]));
                }
            }
        }

        return count;
    }

    /// <summary>Preallocates storage for the expected number of population rows.</summary>
    public void Reserve(int rowCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rowCapacity);
        EnsureCapacity(ref _positions, rowCapacity);
        EnsureCapacity(ref _nextByRow, rowCapacity);
        EnsureCapacity(ref _cellXByRow, rowCapacity);
        EnsureCapacity(ref _cellYByRow, rowCapacity);
        EnsureCapacity(ref _buildCellXByRow, rowCapacity);
        EnsureCapacity(ref _buildCellYByRow, rowCapacity);
    }

    /// <summary>
    /// Rebuilds cell links from positions. All input is validated before the prior snapshot is
    /// replaced, so invalid coordinates leave the previous grid queryable.
    /// </summary>
    public PopulationSpatialGrid2DSnapshot Rebuild(ReadOnlySpan<Vector2> positions) =>
        Rebuild(positions, "positions", InvalidPositionPolicy.Throw);

    /// <summary>
    /// Rebuilds a named population index. Strict validation reports the population, original row,
    /// offending coordinate and configured bounds. Skip mode omits invalid rows and exposes their
    /// count through <see cref="InvalidPositionCount"/>; the rows that remain keep their original
    /// indexes so callers can continue using the population's columns directly.
    /// </summary>
    public PopulationSpatialGrid2DSnapshot Rebuild(
        ReadOnlySpan<Vector2> positions,
        string populationId,
        InvalidPositionPolicy invalidPositionPolicy = InvalidPositionPolicy.Throw)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(populationId);
        if (!Enum.IsDefined(invalidPositionPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(invalidPositionPolicy));
        }

        var buildCellCapacity = _buildCellXByRow.Length >= positions.Length
            && _buildCellYByRow.Length >= positions.Length;
        var invalidPositionCount = ValidatePositions(positions, populationId, invalidPositionPolicy, buildCellCapacity);
        Reserve(positions.Length);
        if (!buildCellCapacity)
        {
            for (var row = 0; row < positions.Length; row++)
            {
                if (!IsValidPosition(positions[row]))
                {
                    _buildCellXByRow[row] = -1;
                    _buildCellYByRow[row] = -1;
                    continue;
                }

                var position = positions[row];
                _buildCellXByRow[row] = SpatialGrid.ToBoundedCell(position.X, _bounds.Left, _cellSize, _cellsX);
                _buildCellYByRow[row] = SpatialGrid.ToBoundedCell(position.Y, _bounds.Top, _cellSize, _cellsY);
            }
        }

        Array.Fill(_cellHeads, -1);
        positions.CopyTo(_positions);
        for (var row = positions.Length - 1; row >= 0; row--)
        {
            if (_buildCellXByRow[row] < 0 || _buildCellYByRow[row] < 0)
            {
                continue;
            }

            var cell = (_buildCellYByRow[row] * _cellsX) + _buildCellXByRow[row];
            _nextByRow[row] = _cellHeads[cell];
            _cellHeads[cell] = row;
        }

        (_cellXByRow, _buildCellXByRow) = (_buildCellXByRow, _cellXByRow);
        (_cellYByRow, _buildCellYByRow) = (_buildCellYByRow, _cellYByRow);
        _rowCount = positions.Length;
        InvalidPositionCount = invalidPositionCount;
        _version = _version == int.MaxValue ? 1 : _version + 1;
        return new PopulationSpatialGrid2DSnapshot(this, _version);
    }

    public void QueryRadius(Vector2 center, float radius, List<int> results) =>
        QueryRadius(_version, center, radius, results, int.MaxValue);

    /// <summary>
    /// Queries at most <paramref name="maximumResults"/> matching rows, keeping the lowest row
    /// indexes in stable order. This bounds scratch memory for dense-neighborhood consumers.
    /// </summary>
    public void QueryRadius(Vector2 center, float radius, List<int> results, int maximumResults) =>
        QueryRadius(_version, center, radius, results, maximumResults);

    /// <summary>
    /// Returns rows from cells overlapped by the radius bounding square in deterministic traversal
    /// order and stops at the result limit. Rows may lie outside the circular radius; consumers
    /// should perform their own exact distance test. This bounds work for intentionally capped systems.
    /// </summary>
    public void QueryRadiusCandidates(Vector2 center, float radius, List<int> results, int maximumResults) =>
        QueryRadius(_version, center, radius, results, maximumResults, stopWhenFull: true);

    internal void QueryRadius(
        int version,
        Vector2 center,
        float radius,
        List<int> results,
        int maximumResults = int.MaxValue,
        bool stopWhenFull = false)
    {
        ValidateSnapshot(version);
        ArgumentNullException.ThrowIfNull(results);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumResults);
        if (!float.IsFinite(center.X) || !float.IsFinite(center.Y) || !float.IsFinite(radius) || radius < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), "Query coordinates and radius must be finite; radius must be non-negative.");
        }

        results.Clear();
        if (maximumResults == 0)
        {
            return;
        }

        GetRadiusCellRanges(center, radius, stopWhenFull, out var xCells, out var yCells);
        if (xCells.Count == 0 || yCells.Count == 0)
        {
            return;
        }

        var radiusSquared = (double)radius * radius;
        for (var yOffset = 0; yOffset < yCells.Count; yOffset++)
        {
            var y = yCells.At(yOffset, _cellsY, _wrapY);
            for (var xOffset = 0; xOffset < xCells.Count; xOffset++)
            {
                var x = xCells.At(xOffset, _cellsX, _wrapX);
                for (var row = _cellHeads[(y * _cellsX) + x]; row >= 0; row = _nextByRow[row])
                {
                    if (stopWhenFull || DistanceSquared(center, _positions[row]) <= radiusSquared)
                    {
                        if (stopWhenFull)
                        {
                            results.Add(row);
                            if (results.Count == maximumResults)
                            {
                                return;
                            }
                        }
                        else
                        {
                            SpatialGrid.AddResult(results, row, maximumResults);
                        }
                    }
                }
            }
        }

        if (maximumResults == int.MaxValue)
        {
            results.Sort();
        }
    }

    /// <summary>
    /// Writes at most <paramref name="results"/>.Length rows from cells overlapped by the radius
    /// bounding square. These are broadphase candidates, not exact radius matches.
    /// </summary>
    public int QueryRadiusCandidates(Vector2 center, float radius, Span<int> results) =>
        QueryRadiusCandidates(_version, center, radius, results);

    internal int QueryRadiusCandidates(int version, Vector2 center, float radius, Span<int> results)
    {
        ValidateSnapshot(version);
        SpatialGrid.ValidateQuery(center, radius);
        if (results.IsEmpty)
        {
            return 0;
        }

        GetRadiusCellRanges(center, radius, stopWhenFull: true, out var xCells, out var yCells);
        return WriteCandidates(xCells, yCells, results);
    }

    /// <summary>
    /// Writes capped broadphase candidates from cells overlapped by a square with one cell-size
    /// radius around the position stored at <paramref name="centerRow"/>.
    /// </summary>
    public int QueryCellNeighborhoodCandidates(
        int centerRow,
        Span<int> results,
        int cellTraversalOffset = 0) =>
        QueryCellNeighborhoodCandidates(_version, centerRow, results, cellTraversalOffset);

    /// <summary>
    /// Iterates rows in cells overlapped by a square with one cell-size radius around the stored
    /// row. Use <c>break</c> to stop early; the optional offset rotates deterministic cell order.
    /// </summary>
    public CandidateEnumerator QueryCellNeighborhoodCandidates(int centerRow, int cellTraversalOffset = 0) =>
        QueryCellNeighborhoodCandidates(_version, centerRow, cellTraversalOffset);

    internal int QueryCellNeighborhoodCandidates(
        int version,
        int centerRow,
        Span<int> results,
        int cellTraversalOffset)
    {
        ValidateSnapshot(version);
        ArgumentOutOfRangeException.ThrowIfNegative(centerRow);
        if (centerRow >= _rowCount)
        {
            throw new ArgumentOutOfRangeException(nameof(centerRow), centerRow,
                "The query row must refer to a row in the current grid snapshot.");
        }

        if (results.IsEmpty)
        {
            return 0;
        }

        var fastWrappedCellQuery = _wrapX && _wrapY && _cellsX >= 3 && _cellsY >= 3;
        GetCellNeighborhoodRanges(centerRow, fastWrappedCellQuery, out var xCells, out var yCells);
        return WriteCandidates(xCells, yCells, results, cellTraversalOffset);
    }

    internal CandidateEnumerator QueryCellNeighborhoodCandidates(
        int version,
        int centerRow,
        int cellTraversalOffset)
    {
        ValidateSnapshot(version);
        ArgumentOutOfRangeException.ThrowIfNegative(centerRow);
        if (centerRow >= _rowCount)
        {
            throw new ArgumentOutOfRangeException(nameof(centerRow), centerRow,
                "The query row must refer to a row in the current grid snapshot.");
        }

        var fastWrappedCellQuery = _wrapX && _wrapY && _cellsX >= 3 && _cellsY >= 3;
        GetCellNeighborhoodRanges(centerRow, fastWrappedCellQuery, out var xCells, out var yCells);
        return new CandidateEnumerator(this, version, xCells, yCells, cellTraversalOffset);
    }

    private void GetCellNeighborhoodRanges(
        int centerRow,
        bool fastWrappedCellQuery,
        out AxisCellRange xCells,
        out AxisCellRange yCells)
    {
        if (fastWrappedCellQuery)
        {
            var center = _positions[centerRow];
            xCells = GetWrappedCellRadiusRangeFromCell(center.X, _bounds.Left,
                _bounds.HalfWidth * 2d, _cellSize, _cellsX, _cellXByRow[centerRow]);
            yCells = GetWrappedCellRadiusRangeFromCell(center.Y, _bounds.Top,
                _bounds.HalfHeight * 2d, _cellSize, _cellsY, _cellYByRow[centerRow]);
            return;
        }

        var position = _positions[centerRow];
        xCells = SpatialGrid.GetAxisCells(position.X, _cellSize, _bounds.Left, _bounds.HalfWidth * 2d,
            _cellSize, _cellsX, _wrapX);
        yCells = SpatialGrid.GetAxisCells(position.Y, _cellSize, _bounds.Top, _bounds.HalfHeight * 2d,
            _cellSize, _cellsY, _wrapY);
    }

    private int WriteCandidates(
        AxisCellRange xCells,
        AxisCellRange yCells,
        Span<int> results,
        int cellTraversalOffset = 0)
    {
        var count = 0;
        var cellCount = xCells.Count * yCells.Count;
        if (cellCount == 0)
        {
            return 0;
        }

        var firstCellOffset = cellTraversalOffset;
        if ((uint)firstCellOffset >= (uint)cellCount)
        {
            firstCellOffset %= cellCount;
            if (firstCellOffset < 0)
            {
                firstCellOffset += cellCount;
            }
        }

        var xOffset = firstCellOffset % xCells.Count;
        var yOffset = firstCellOffset / xCells.Count;
        for (var cellOffset = 0; cellOffset < cellCount; cellOffset++)
        {
            var x = xCells.At(xOffset, _cellsX, _wrapX);
            var y = yCells.At(yOffset, _cellsY, _wrapY);
            for (var row = _cellHeads[(y * _cellsX) + x]; row >= 0; row = _nextByRow[row])
            {
                results[count++] = row;
                if (count == results.Length)
                {
                    return count;
                }
            }

            xOffset++;
            if (xOffset == xCells.Count)
            {
                xOffset = 0;
                yOffset++;
                if (yOffset == yCells.Count)
                {
                    yOffset = 0;
                }
            }
        }

        return count;
    }

    private void GetRadiusCellRanges(
        Vector2 center,
        float radius,
        bool stopWhenFull,
        out AxisCellRange xCells,
        out AxisCellRange yCells)
    {
        var fastWrappedCellQuery = stopWhenFull && radius == _cellSize && _wrapX && _wrapY
            && _cellsX >= 3 && _cellsY >= 3
            && center.X >= _bounds.Left && center.X <= _bounds.Right
            && center.Y >= _bounds.Top && center.Y <= _bounds.Bottom;
        xCells = fastWrappedCellQuery
            ? GetWrappedCellRadiusRange(center.X, _bounds.Left, _bounds.HalfWidth * 2d, _cellSize, _cellsX)
            : SpatialGrid.GetAxisCells(center.X, radius, _bounds.Left, _bounds.HalfWidth * 2d, _cellSize, _cellsX, _wrapX);
        yCells = fastWrappedCellQuery
            ? GetWrappedCellRadiusRange(center.Y, _bounds.Top, _bounds.HalfHeight * 2d, _cellSize, _cellsY)
            : SpatialGrid.GetAxisCells(center.Y, radius, _bounds.Top, _bounds.HalfHeight * 2d, _cellSize, _cellsY, _wrapY);
    }

    internal void QueryBounds(int version, Bounds2 area, List<int> results)
    {
        ValidateSnapshot(version);
        ArgumentNullException.ThrowIfNull(results);
        if (!area.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(area), "Query bounds must be finite and have non-negative extents.");
        }

        results.Clear();
        var xCells = SpatialGrid.GetAxisCells(area.X, area.HalfWidth, _bounds.Left, _bounds.HalfWidth * 2d, _cellSize, _cellsX, _wrapX);
        var yCells = SpatialGrid.GetAxisCells(area.Y, area.HalfHeight, _bounds.Top, _bounds.HalfHeight * 2d, _cellSize, _cellsY, _wrapY);
        if (xCells.Count == 0 || yCells.Count == 0)
        {
            return;
        }

        for (var yOffset = 0; yOffset < yCells.Count; yOffset++)
        {
            var y = yCells.At(yOffset, _cellsY, _wrapY);
            for (var xOffset = 0; xOffset < xCells.Count; xOffset++)
            {
                var x = xCells.At(xOffset, _cellsX, _wrapX);
                for (var row = _cellHeads[(y * _cellsX) + x]; row >= 0; row = _nextByRow[row])
                {
                    if (Contains(area, _positions[row]))
                    {
                        results.Add(row);
                    }
                }
            }
        }

        results.Sort();
    }

    private int ValidatePositions(
        ReadOnlySpan<Vector2> positions,
        string populationId,
        InvalidPositionPolicy invalidPositionPolicy,
        bool buildCellIndexes)
    {
        var invalidPositionCount = 0;
        for (var row = 0; row < positions.Length; row++)
        {
            var position = positions[row];
            if (!IsValidPosition(position))
            {
                if (invalidPositionPolicy == InvalidPositionPolicy.Throw)
                {
                    var message = FormattableString.Invariant($"Population '{populationId}' has an invalid position at row {row}: ({position.X:R}, {position.Y:R}); bounds are [{_bounds.Left:R}, {_bounds.Top:R}] to [{_bounds.Right:R}, {_bounds.Bottom:R}].");
                    throw new ArgumentOutOfRangeException("positions", message);
                }

                invalidPositionCount++;
                if (buildCellIndexes)
                {
                    _buildCellXByRow[row] = -1;
                    _buildCellYByRow[row] = -1;
                }

                continue;
            }

            if (buildCellIndexes)
            {
                _buildCellXByRow[row] = SpatialGrid.ToBoundedCell(position.X, _bounds.Left, _cellSize, _cellsX);
                _buildCellYByRow[row] = SpatialGrid.ToBoundedCell(position.Y, _bounds.Top, _cellSize, _cellsY);
            }
        }

        return invalidPositionCount;
    }

    private bool IsValidPosition(Vector2 position) => float.IsFinite(position.X) && float.IsFinite(position.Y)
        && position.X >= _bounds.Left && position.X <= _bounds.Right
        && position.Y >= _bounds.Top && position.Y <= _bounds.Bottom;

    private static AxisCellRange GetWrappedCellRadiusRange(
        double center,
        double minimum,
        double size,
        double cellSize,
        int cellCount)
    {
        var startOffset = center - minimum - cellSize;
        if (startOffset < 0)
        {
            startOffset += size;
        }

        var first = (int)(startOffset / cellSize);
        var endOffset = startOffset + (cellSize * 2d);
        var lastBeforeWrap = Math.Min(cellCount - 1,
            (int)(Math.Min(endOffset, size) / cellSize));
        var count = lastBeforeWrap - first + 1;
        if (endOffset >= size)
        {
            var wrappedEnd = endOffset - size;
            count += Math.Min(cellCount, (int)(wrappedEnd / cellSize) + 1);
        }

        return new AxisCellRange(Math.Min(first, cellCount - 1), Math.Min(cellCount, count));
    }

    private static AxisCellRange GetWrappedCellRadiusRangeFromCell(
        double center,
        double minimum,
        double size,
        double cellSize,
        int cellCount,
        int centerCell)
    {
        var relative = center - minimum;
        var startOffset = relative - cellSize;
        var first = centerCell - 1;
        if (startOffset < 0)
        {
            startOffset += size;
            first = (int)(startOffset / cellSize);
        }

        var endOffset = startOffset + (cellSize * 2d);
        int count;
        if (endOffset >= size)
        {
            count = cellCount - first;
            var wrappedEnd = endOffset - size;
            count += Math.Min(cellCount, (int)(wrappedEnd / cellSize) + 1);
        }
        else
        {
            count = (centerCell + 1) - first + 1;
        }

        return new AxisCellRange(Math.Min(first, cellCount - 1), Math.Min(cellCount, count));
    }

    private double DistanceSquared(Vector2 center, Vector2 position) =>
        SpatialGrid.DistanceSquared(center, position, _bounds, _wrapX, _wrapY);

    private bool Contains(Bounds2 area, Vector2 position) =>
        SpatialGrid.AxisDistance(area.X, position.X, _bounds.HalfWidth * 2d, _wrapX) <= area.HalfWidth
        && SpatialGrid.AxisDistance(area.Y, position.Y, _bounds.HalfHeight * 2d, _wrapY) <= area.HalfHeight;

    private void ValidateSnapshot(int version)
    {
        if (version != _version || version == 0)
        {
            throw new InvalidOperationException("This spatial snapshot was invalidated by a later rebuild.");
        }
    }

    private static void EnsureCapacity<T>(ref T[] values, int capacity)
    {
        if (capacity > values.Length)
        {
            Array.Resize(ref values, capacity);
        }
    }

    public ref struct CandidateEnumerator
    {
        private readonly PopulationSpatialGrid2D _grid;
        private readonly int _version;
        private readonly int _xStart;
        private readonly int _xCount;
        private readonly int _yStart;
        private readonly int _yCount;
        private readonly int _cellCount;
        private int _cellOffset;
        private int _nextCellXOffset;
        private int _nextCellYOffset;
        private int _nextRow;
        private bool _finished;

        internal CandidateEnumerator(
            PopulationSpatialGrid2D grid,
            int version,
            AxisCellRange xCells,
            AxisCellRange yCells,
            int cellTraversalOffset)
        {
            _grid = grid;
            _version = version;
            _xStart = xCells.Start;
            _xCount = xCells.Count;
            _yStart = yCells.Start;
            _yCount = yCells.Count;
            _cellCount = _xCount * _yCount;
            _cellOffset = 0;
            if (_cellCount == 0)
            {
                _nextCellXOffset = 0;
                _nextCellYOffset = 0;
            }
            else
            {
                var firstCellOffset = cellTraversalOffset % _cellCount;
                if (firstCellOffset < 0)
                {
                    firstCellOffset += _cellCount;
                }

                _nextCellXOffset = firstCellOffset % _xCount;
                _nextCellYOffset = firstCellOffset / _xCount;
            }

            _nextRow = -1;
            _finished = false;
            Current = -1;
        }

        public int Current { get; private set; }

        public readonly CandidateEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            _grid.ValidateSnapshot(_version);

            if (_finished)
            {
                return false;
            }

            while (true)
            {
                if (_nextRow >= 0)
                {
                    Current = _nextRow;
                    _nextRow = _grid._nextByRow[_nextRow];
                    return true;
                }

                if (_cellOffset >= _cellCount)
                {
                    Current = -1;
                    _finished = true;
                    return false;
                }

                _cellOffset++;
                var x = _xStart + _nextCellXOffset;
                if (_grid._wrapX && x >= _grid._cellsX)
                {
                    x -= _grid._cellsX;
                }

                var y = _yStart + _nextCellYOffset;
                if (_grid._wrapY && y >= _grid._cellsY)
                {
                    y -= _grid._cellsY;
                }

                _nextCellXOffset++;
                if (_nextCellXOffset == _xCount)
                {
                    _nextCellXOffset = 0;
                    _nextCellYOffset++;
                    if (_nextCellYOffset == _yCount)
                    {
                        _nextCellYOffset = 0;
                    }
                }

                _nextRow = _grid._cellHeads[(y * _grid._cellsX) + x];
            }
        }
    }
}

/// <summary>A short-lived view of the most recently rebuilt population grid.</summary>
public readonly ref struct PopulationSpatialGrid2DSnapshot(PopulationSpatialGrid2D grid, int version)
{
    public bool TryQueryNearest(Vector2 center, out int row, Predicate<int>? filter = null) =>
        grid.TryQueryNearest(version, center, null, out row, filter);

    public bool TryQueryNearest(Vector2 center, float radius, out int row, Predicate<int>? filter = null) =>
        grid.TryQueryNearest(version, center, radius, out row, filter);

    public int QueryKNearest(Vector2 center, int k, Span<int> results, Predicate<int>? filter = null) =>
        grid.QueryKNearest(version, center, null, k, results, filter);

    public int QueryKNearest(Vector2 center, float radius, int k, Span<int> results, Predicate<int>? filter = null) =>
        grid.QueryKNearest(version, center, radius, k, results, filter);

    public void QueryRadius(Vector2 center, float radius, List<int> results) =>
        grid.QueryRadius(version, center, radius, results);

    public void QueryRadius(Vector2 center, float radius, List<int> results, int maximumResults) =>
        grid.QueryRadius(version, center, radius, results, maximumResults);

    public void QueryRadiusCandidates(Vector2 center, float radius, List<int> results, int maximumResults) =>
        grid.QueryRadius(version, center, radius, results, maximumResults, stopWhenFull: true);

    public int QueryRadiusCandidates(Vector2 center, float radius, Span<int> results) =>
        grid.QueryRadiusCandidates(version, center, radius, results);

    public int QueryCellNeighborhoodCandidates(
        int centerRow,
        Span<int> results,
        int cellTraversalOffset = 0) =>
        grid.QueryCellNeighborhoodCandidates(version, centerRow, results, cellTraversalOffset);

    public PopulationSpatialGrid2D.CandidateEnumerator QueryCellNeighborhoodCandidates(
        int centerRow,
        int cellTraversalOffset = 0) =>
        grid.QueryCellNeighborhoodCandidates(version, centerRow, cellTraversalOffset);

    public void QueryBounds(Bounds2 area, List<int> results) =>
        grid.QueryBounds(version, area, results);
}
