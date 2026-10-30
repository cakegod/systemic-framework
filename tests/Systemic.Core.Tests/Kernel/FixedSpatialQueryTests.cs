using System.Buffers.Binary;
using System.Numerics;
using Systemic.Kernel;

namespace Systemic.Tests.Kernel;

public sealed class FixedSpatialQueryTests
{
    [Fact]
    public void ExactFixedNearestDistinguishesOneRawUnitAndPreservesTies()
    {
        var one = 1L << 32;
        var grid = new FixedPopulationSpatialIndex2D(Point(0, 0), Point(10 * one, 10 * one));
        var snapshot = grid.Rebuild([Point(one + 1, one), Point(one - 1, one), Point(one, one)]);
        var center = Point(one, one);
        Assert.True(snapshot.TryQueryNearest(center, out var row));
        Assert.Equal(2, row);
        Span<int> result = stackalloc int[3];
        Assert.Equal(3, snapshot.QueryKNearest(center, 3, result));
        Assert.Equal([2, 0, 1], result.ToArray());
        var rows = new List<int>();
        snapshot.QueryRadius(center, Fixed64.FromRaw(1), rows);
        Assert.Equal([0, 1, 2], rows);
        Assert.True(snapshot.TryQueryNearest(center, Fixed64.FromRaw(1), out row, row => row != 2));
        Assert.Equal(0, row);
        snapshot.QueryRadius(center, Fixed64.Zero, rows);
        Assert.Equal([2], rows);
    }

    [Fact]
    public void FixedWrappedEndpointsAreEqualAndResultsDoNotRepeat()
    {
        var scale = 1L << 32;
        var grid = new FixedPopulationSpatialIndex2D(Point(0, 0), Point(10 * scale, 10 * scale), wrapX: true, wrapY: true);
        var snapshot = grid.Rebuild([Point(0, 0), Point(10 * scale, 10 * scale), Point(scale, 0), Point(9 * scale, 0)]);
        var rows = new List<int>();
        snapshot.QueryRadius(Point(0, 0), Fixed64.Zero, rows);
        Assert.Equal([0, 1], rows);
        Span<int> result = stackalloc int[4];
        var count = snapshot.QueryKNearest(Point(0, 0), (Fixed64)1, 10, result);
        Assert.Equal([0, 1, 2, 3], result[..count].ToArray());
        Assert.True(snapshot.TryQueryNearest(Point(25 * scale, 20 * scale), out var nearest));
        Assert.Equal(2, nearest);
    }

    [Fact]
    public void FixedQueriesHandleFullSignedRangeWithoutSquaredDistanceOverflow()
    {
        var grid = new FixedPopulationSpatialIndex2D(Point(long.MinValue, long.MinValue), Point(long.MaxValue, long.MaxValue));
        var snapshot = grid.Rebuild([
            Point(long.MaxValue, long.MaxValue),
            Point(long.MaxValue, long.MinValue),
            Point(long.MinValue, long.MaxValue),
            Point(long.MinValue, long.MinValue)
        ]);
        Span<int> result = stackalloc int[4];
        Assert.Equal(4, snapshot.QueryKNearest(Point(long.MinValue, long.MinValue), 4, result));
        Assert.Equal([3, 1, 2, 0], result.ToArray());
        var rows = new List<int>();
        snapshot.QueryRadius(Point(long.MinValue, long.MinValue), Fixed64.FromRaw(long.MaxValue), rows);
        Assert.Equal([3], rows);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void FixedQueriesMatchBigIntegerOracleAcrossFullRange(bool wrapX, bool wrapY)
    {
        var random = new Random(202606);
        var grid = new FixedPopulationSpatialIndex2D(Point(long.MinValue, long.MinValue), Point(long.MaxValue, long.MaxValue), wrapX, wrapY);
        var positions = new FixedVector2[80];
        for (var i = 0; i < positions.Length; i++)
        {
            positions[i] = Point(NextRaw(random), NextRaw(random));
        }

        positions[0] = Point(long.MinValue, long.MinValue);
        positions[1] = Point(long.MaxValue, long.MaxValue);
        var snapshot = grid.Rebuild(positions);
        var results = new int[7];
        var rows = new List<int>();
        for (var query = 0; query < 100; query++)
        {
            var center = Point(NextRaw(random), NextRaw(random));
            var radius = Fixed64.FromRaw(random.NextInt64(long.MaxValue));
            var expected = Enumerable.Range(0, positions.Length)
                .Where(row => row % 5 != 0)
                .Select(row => (Row: row, Distance: Oracle(positions[row], center, wrapX, wrapY)))
                .Where(value => value.Distance <= (BigInteger)radius.RawValue * radius.RawValue)
                .OrderBy(value => value.Distance).ThenBy(value => value.Row)
                .Take(results.Length).Select(value => value.Row).ToArray();
            var count = snapshot.QueryKNearest(center, radius, results.Length, results, row => row % 5 != 0);
            Assert.Equal(expected, results.Take(count));
            snapshot.QueryRadius(center, radius, rows);
            var expectedRows = Enumerable.Range(0, positions.Length)
                .Where(row => Oracle(positions[row], center, wrapX, wrapY) <= (BigInteger)radius.RawValue * radius.RawValue)
                .ToArray();
            Assert.Equal(expectedRows, rows);
        }
    }

    [Fact]
    public void FixedRebuildValidationAndSnapshotLifetimeAreExplicit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FixedPopulationSpatialIndex2D(Point(0, 0), Point(0, 1)));
        var grid = new FixedPopulationSpatialIndex2D(Point(0, 0), Point(10, 10));
        var old = grid.Rebuild([Point(3, 4)]);
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.Rebuild([Point(11, 0)]));
        Assert.True(old.TryQueryNearest(Point(3, 4), out var row));
        Assert.Equal(0, row);
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.QueryKNearest(Point(0, 0), -1, new int[1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.QueryRadius(Point(0, 0), Fixed64.FromRaw(-1), new List<int>()));
        grid.Rebuild([]);
        var stale = false;
        try { old.TryQueryNearest(Point(0, 0), out _); }
        catch (InvalidOperationException) { stale = true; }
        Assert.True(stale);
        Assert.False(grid.TryQueryNearest(Point(0, 0), out row));
        Assert.Equal(-1, row);
        Assert.Equal(0, grid.QueryKNearest(Point(0, 0), 0, Span<int>.Empty));
    }

    [Fact]
    public void FixedQueriesRejectRebuildsFromAFilterAndRetainSmallestRowsWhenCapped()
    {
        var grid = new FixedPopulationSpatialIndex2D(Point(0, 0), Point(10, 10));
        grid.Rebuild([Point(1, 1), Point(1, 1), Point(1, 1)]);
        var results = new List<int>();
        grid.QueryRadius(Point(1, 1), Fixed64.Zero, results, maximumResults: 2);
        Assert.Equal([0, 1], results);
        Assert.Throws<InvalidOperationException>(() => grid.TryQueryNearest(Point(0, 0), out _, row =>
        {
            grid.Rebuild([Point(2, 2)]);
            return true;
        }));
        Assert.True(grid.TryQueryNearest(Point(2, 2), out var nearest));
        Assert.Equal(0, nearest);
    }

    private static FixedVector2 Point(long x, long y) => new(Fixed64.FromRaw(x), Fixed64.FromRaw(y));

    private static long NextRaw(Random random)
    {
        Span<byte> bytes = stackalloc byte[8];
        random.NextBytes(bytes);
        return BinaryPrimitives.ReadInt64LittleEndian(bytes);
    }

    private static BigInteger Oracle(FixedVector2 position, FixedVector2 center, bool wrapX, bool wrapY)
    {
        var dx = BigInteger.Abs((BigInteger)position.X.RawValue - center.X.RawValue);
        var dy = BigInteger.Abs((BigInteger)position.Y.RawValue - center.Y.RawValue);
        var size = (BigInteger)ulong.MaxValue;
        if (wrapX) { dx %= size; dx = BigInteger.Min(dx, size - dx); }
        if (wrapY) { dy %= size; dy = BigInteger.Min(dy, size - dy); }
        return dx * dx + dy * dy;
    }
}
