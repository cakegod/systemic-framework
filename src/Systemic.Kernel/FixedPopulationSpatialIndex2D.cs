namespace Systemic.Kernel;

/// <summary>
/// Exact row-indexed spatial queries over Q32.32 positions. This implementation scans positions
/// rather than building cells: rebuild is O(n), radius/nearest are O(n), and k-nearest has
/// O(n * k) worst-case work with O(k) caller-owned result storage. Query operations do not
/// allocate except when a caller-owned result list must grow.
/// </summary>
/// <remarks>
/// The inclusive bounds must have strictly positive dimensions. Stored coordinates must be inside
/// those bounds; query centers may be anywhere in the Q32.32 range. Each wrapped axis uses the
/// interval width, with both endpoints representing the same position. Squared distances use
/// exact integer arithmetic (including a 129th sum bit), so even opposite extreme coordinates
/// are comparable without overflow, conversion to floating point, or Q32.32 multiplication
/// rounding. A successful rebuild invalidates prior snapshots; an invalid rebuild does not.
/// Filters must be stable for the query and must not rebuild the index. Concurrent readers are
/// supported only when no rebuild occurs.
/// </remarks>
public sealed class FixedPopulationSpatialIndex2D
{
    private readonly FixedVector2 _minimum;
    private readonly FixedVector2 _maximum;
    private readonly bool _wrapX;
    private readonly bool _wrapY;
    private readonly UInt128 _width;
    private readonly UInt128 _height;
    private FixedVector2[] _positions = [];
    private int _count;
    private int _version;

    public FixedPopulationSpatialIndex2D(FixedVector2 minimum, FixedVector2 maximum, bool wrapX = false, bool wrapY = false)
    {
        if (minimum.X >= maximum.X || minimum.Y >= maximum.Y)
        {
            throw new ArgumentOutOfRangeException(nameof(maximum), "Maximum coordinates must be greater than minimum coordinates.");
        }
        _minimum = minimum;
        _maximum = maximum;
        _wrapX = wrapX;
        _wrapY = wrapY;
        _width = (UInt128)((Int128)maximum.X.RawValue - minimum.X.RawValue);
        _height = (UInt128)((Int128)maximum.Y.RawValue - minimum.Y.RawValue);
    }

    public FixedVector2 Minimum => _minimum;
    public FixedVector2 Maximum => _maximum;
    public int Count => _count;

    /// <summary>Reserves position storage without invalidating the current snapshot.</summary>
    public void Reserve(int rowCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rowCapacity);
        if (rowCapacity > _positions.Length)
        {
            Array.Resize(ref _positions, rowCapacity);
        }
    }

    /// <summary>Copies positions and invalidates prior snapshots after validating all positions.</summary>
    public FixedPopulationSpatialIndex2DSnapshot Rebuild(ReadOnlySpan<FixedVector2> positions)
    {
        for (var row = 0; row < positions.Length; row++)
        {
            var position = positions[row];
            if (position.X < _minimum.X || position.X > _maximum.X
                || position.Y < _minimum.Y || position.Y > _maximum.Y)
            {
                throw new ArgumentOutOfRangeException(nameof(positions), $"Position at row {row} is outside the index bounds.");
            }
        }
        Reserve(positions.Length);
        positions.CopyTo(_positions);
        _count = positions.Length;
        _version = _version == int.MaxValue ? 1 : _version + 1;
        return new FixedPopulationSpatialIndex2DSnapshot(this, _version);
    }

    /// <summary>Clears and fills the list with exact inclusive-radius matches in ascending row order.</summary>
    public void QueryRadius(FixedVector2 center, Fixed64 radius, List<int> results, int maximumResults = int.MaxValue) =>
        QueryRadius(_version, center, radius, results, maximumResults);

    /// <summary>Returns false and row -1 when no row passes the optional filter.</summary>
    public bool TryQueryNearest(FixedVector2 center, out int row, Predicate<int>? filter = null) =>
        TryQueryNearest(_version, center, null, out row, filter);

    /// <summary>Finds the nearest row within the inclusive radius, breaking ties by smallest row.</summary>
    public bool TryQueryNearest(FixedVector2 center, Fixed64 radius, out int row, Predicate<int>? filter = null) =>
        TryQueryNearest(_version, center, radius, out row, filter);

    /// <summary>Writes at most k rows, also bounded by results.Length, in distance then row order.</summary>
    public int QueryKNearest(FixedVector2 center, int k, Span<int> results, Predicate<int>? filter = null) =>
        QueryKNearest(_version, center, null, k, results, filter);

    /// <summary>Writes the nearest rows within the inclusive non-negative radius.</summary>
    public int QueryKNearest(FixedVector2 center, Fixed64 radius, int k, Span<int> results, Predicate<int>? filter = null) =>
        QueryKNearest(_version, center, radius, k, results, filter);

    internal void QueryRadius(int version, FixedVector2 center, Fixed64 radius, List<int> results, int maximumResults)
    {
        ValidateSnapshot(version);
        ArgumentNullException.ThrowIfNull(results);
        ArgumentOutOfRangeException.ThrowIfNegative(radius.RawValue, nameof(radius));
        ArgumentOutOfRangeException.ThrowIfNegative(maximumResults);
        results.Clear();
        if (maximumResults == 0)
        {
            return;
        }

        var threshold = RadiusSquared(radius);
        for (var row = 0; row < _count; row++)
        {
            if (DistanceSquared(center, _positions[row]).CompareTo(threshold) <= 0)
            {
                results.Add(row);
                if (results.Count == maximumResults)
                {
                    return;
                }
            }
        }
    }

    internal bool TryQueryNearest(int version, FixedVector2 center, Fixed64? radius, out int row, Predicate<int>? filter)
    {
        Span<int> result = stackalloc int[1];
        var count = QueryKNearest(version, center, radius, 1, result, filter);
        row = count == 0 ? -1 : result[0];
        return count != 0;
    }

    internal int QueryKNearest(int version, FixedVector2 center, Fixed64? radius, int k, Span<int> results, Predicate<int>? filter)
    {
        ValidateSnapshot(version);
        if (radius.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(radius.Value.RawValue, nameof(radius));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(k);
        var limit = Math.Min(k, results.Length);
        if (limit == 0)
        {
            return 0;
        }

        var threshold = radius.HasValue ? RadiusSquared(radius.Value) : new SquaredDistance(true, UInt128.MaxValue);
        var count = 0;
        for (var row = 0; row < _count; row++)
        {
            var distance = DistanceSquared(center, _positions[row]);
            if (distance.CompareTo(threshold) > 0)
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
                var comparison = distance.CompareTo(DistanceSquared(center, _positions[last]));
                if (comparison > 0 || (comparison == 0 && row >= last))
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
                var comparison = distance.CompareTo(DistanceSquared(center, _positions[previous]));
                if (comparison > 0 || (comparison == 0 && row >= previous))
                {
                    break;
                }

                results[index] = previous;
                index--;
            }
            results[index] = row;
        }
        return count;
    }

    private void ValidateSnapshot(int version)
    {
        if (version == 0 || version != _version)
        {
            throw new InvalidOperationException("This spatial snapshot was invalidated by a later rebuild.");
        }
    }

    private SquaredDistance DistanceSquared(FixedVector2 center, FixedVector2 position)
    {
        var x = AxisDistance(center.X.RawValue, position.X.RawValue, _width, _wrapX);
        var y = AxisDistance(center.Y.RawValue, position.Y.RawValue, _height, _wrapY);
        var xSquared = x * x;
        var ySquared = y * y;
        var sum = unchecked(xSquared + ySquared);
        return new SquaredDistance(sum < xSquared, sum);
    }

    private static UInt128 AxisDistance(long first, long second, UInt128 size, bool wrap)
    {
        var signed = (Int128)first - second;
        var distance = (UInt128)(signed < 0 ? -signed : signed);
        if (!wrap)
        {
            return distance;
        }

        distance %= size;
        return UInt128.Min(distance, size - distance);
    }

    private static SquaredDistance RadiusSquared(Fixed64 radius)
    {
        var magnitude = (UInt128)radius.RawValue;
        return new SquaredDistance(false, magnitude * magnitude);
    }

    private readonly record struct SquaredDistance(bool High, UInt128 Low) : IComparable<SquaredDistance>
    {
        public int CompareTo(SquaredDistance other)
        {
            if (High != other.High)
            {
                return High ? 1 : -1;
            }

            return Low.CompareTo(other.Low);
        }
    }
}

/// <summary>A short-lived view of the latest successful fixed-point rebuild.</summary>
public readonly ref struct FixedPopulationSpatialIndex2DSnapshot(FixedPopulationSpatialIndex2D index, int version)
{
    public void QueryRadius(FixedVector2 center, Fixed64 radius, List<int> results, int maximumResults = int.MaxValue) =>
        index.QueryRadius(version, center, radius, results, maximumResults);
    public bool TryQueryNearest(FixedVector2 center, out int row, Predicate<int>? filter = null) =>
        index.TryQueryNearest(version, center, null, out row, filter);
    public bool TryQueryNearest(FixedVector2 center, Fixed64 radius, out int row, Predicate<int>? filter = null) =>
        index.TryQueryNearest(version, center, radius, out row, filter);
    public int QueryKNearest(FixedVector2 center, int k, Span<int> results, Predicate<int>? filter = null) =>
        index.QueryKNearest(version, center, null, k, results, filter);
    public int QueryKNearest(FixedVector2 center, Fixed64 radius, int k, Span<int> results, Predicate<int>? filter = null) =>
        index.QueryKNearest(version, center, radius, k, results, filter);
}
