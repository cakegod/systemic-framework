using System.Numerics;
using Systemic.Kernel;

namespace Systemic.Tests.Kernel;

public sealed class SpatialOverhaulTests
{
    [Fact]
    public void Nearest_queries_respect_wrapped_seams_inclusive_radius_filters_and_ties()
    {
        var grid = new PopulationSpatialGrid2D(new Bounds2(5, 5, 5, 5), 3, wrapX: true, wrapY: true);
        var snapshot = grid.Rebuild([new(10, 5), new(0, 5), new(1, 5), new(9, 5), new(5, 5)]);
        Span<int> results = stackalloc int[6];

        Assert.True(snapshot.TryQueryNearest(new Vector2(20, 5), 0, out var nearest));
        Assert.Equal(0, nearest);
        Assert.True(snapshot.TryQueryNearest(new Vector2(0, 5), 1, out nearest, row => row > 1));
        Assert.Equal(2, nearest);
        var count = snapshot.QueryKNearest(new Vector2(0, 5), 1, 10, results);
        Assert.Equal([0, 1, 2, 3], results[..count].ToArray());
        count = snapshot.QueryKNearest(new Vector2(0, 5), 4, results, row => row != 0);
        Assert.Equal([1, 2, 3, 4], results[..count].ToArray());
        Assert.False(snapshot.TryQueryNearest(new Vector2(0, 0), 0, out nearest));
        Assert.Equal(-1, nearest);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Nearest_and_k_nearest_match_brute_force_across_boundaries(bool wrapX, bool wrapY)
    {
        var random = new Random(41831);
        var positions = new Vector2[93];
        positions[0] = new Vector2(0, 0);
        positions[1] = new Vector2(10, 10);
        positions[2] = new Vector2(3, 6);
        positions[3] = positions[2];
        for (var row = 4; row < positions.Length; row++)
        {
            positions[row] = new Vector2(random.NextSingle() * 10, random.NextSingle() * 10);
        }

        var grid = new PopulationSpatialGrid2D(new Bounds2(5, 5, 5, 5), 3, wrapX, wrapY);
        var snapshot = grid.Rebuild(positions);
        Span<int> results = stackalloc int[17];
        Predicate<int> filter = static row => row % 4 != 0;
        for (var iteration = 0; iteration < 110; iteration++)
        {
            var center = iteration switch
            {
                0 => new Vector2(0, 0),
                1 => new Vector2(10, 10),
                2 => new Vector2(3, 6),
                _ => new Vector2((random.NextSingle() * 40) - 15, (random.NextSingle() * 40) - 15)
            };
            var radius = iteration == 2 ? 0 : random.NextSingle() * 12;
            var k = iteration % 22;
            var expected = Oracle(positions, center, radius, wrapX, wrapY, filter);
            var count = snapshot.QueryKNearest(center, radius, k, results, filter);
            Assert.Equal(expected.Take(Math.Min(k, results.Length)), results[..count].ToArray());
            var found = snapshot.TryQueryNearest(center, radius, out var nearest, filter);
            Assert.Equal(expected.Length != 0, found);
            Assert.Equal(expected.Length == 0 ? -1 : expected[0], nearest);
            var all = Oracle(positions, center, null, wrapX, wrapY, filter);
            count = grid.QueryKNearest(center, k, results, filter);
            Assert.Equal(all.Take(Math.Min(k, results.Length)), results[..count].ToArray());
            found = grid.TryQueryNearest(center, out nearest, filter);
            Assert.Equal(all[0], nearest);
            Assert.True(found);
        }
    }

    [Fact]
    public void Nearest_queries_handle_empty_and_invalid_inputs_and_stale_snapshots()
    {
        var grid = new PopulationSpatialGrid2D(new Bounds2(5, 5, 5, 5), 3);
        grid.Rebuild([]);
        Assert.False(grid.TryQueryNearest(Vector2.Zero, out var nearest));
        Assert.Equal(-1, nearest);
        Assert.Equal(0, grid.QueryKNearest(Vector2.Zero, 5, new int[3]));
        Assert.Equal(0, grid.QueryKNearest(Vector2.Zero, 0, new int[3]));
        Assert.Equal(0, grid.QueryKNearest(Vector2.Zero, 5, Span<int>.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.QueryKNearest(Vector2.Zero, -1, new int[3]));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.QueryKNearest(Vector2.Zero, -1f, 0, new int[3]));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.TryQueryNearest(new Vector2(float.NaN, 0), out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.TryQueryNearest(Vector2.Zero, float.PositiveInfinity, out _));

        var old = grid.Rebuild([new(1, 1)]);
        grid.Rebuild([new(2, 2)]);
        var rejected = false;
        try
        {
            old.TryQueryNearest(Vector2.Zero, out _);
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }
        Assert.True(rejected);
        rejected = false;
        try
        {
            old.QueryKNearest(Vector2.Zero, 0, Span<int>.Empty);
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }
        Assert.True(rejected);
    }

    [Fact]
    public void Wrapped_radius_and_bounds_include_both_stored_endpoints_without_duplicates()
    {
        Vector2[] positions = [new(0, 0), new(10, 0), new(0, 10), new(10, 10), new(9, 0), new(1, 0)];
        var bounds = new Bounds2(5, 5, 5, 5);
        var grid = new PopulationSpatialGrid2D(bounds, 3, wrapX: true, wrapY: true);
        var snapshot = grid.Rebuild(positions);
        var incremental = new IncrementalPopulationSpatialGrid2D(bounds, 3, wrapX: true, wrapY: true);
        for (var row = 0; row < positions.Length; row++)
        {
            incremental.Add(row, positions[row]);
        }

        var actual = new List<int>();
        snapshot.QueryRadius(Vector2.Zero, 0, actual);
        Assert.Equal([0, 1, 2, 3], actual);
        incremental.QueryRadius(Vector2.Zero, 0, actual);
        Assert.Equal([0, 1, 2, 3], actual);
        snapshot.QueryBounds(new Bounds2(10, 10, 0, 0), actual);
        Assert.Equal([0, 1, 2, 3], actual);
        incremental.QueryBounds(new Bounds2(10, 10, 0, 0), actual);
        Assert.Equal([0, 1, 2, 3], actual);
        incremental.QueryRadius(Vector2.Zero, 1, actual, maximumResults: 3);
        Assert.Equal([0, 1, 2], actual);
        incremental.QueryRadius(Vector2.Zero, 1, actual);
        Assert.Equal([0, 1, 2, 3, 4, 5], actual);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Radius_and_bounds_match_independent_oracles_for_edges_and_outside_centers(bool wrapX, bool wrapY)
    {
        var random = new Random(7781);
        var positions = new Vector2[70];
        positions[0] = new Vector2(0, 0);
        positions[1] = new Vector2(10, 10);
        positions[2] = new Vector2(10, 0);
        positions[3] = new Vector2(0, 10);
        for (var row = 4; row < positions.Length; row++)
        {
            positions[row] = RandomPosition(random);
        }
        var bounds = new Bounds2(5, 5, 5, 5);
        var grid = new PopulationSpatialGrid2D(bounds, 3, wrapX, wrapY);
        var snapshot = grid.Rebuild(positions);
        var incremental = new IncrementalPopulationSpatialGrid2D(bounds, 3, wrapX, wrapY);
        for (var row = 0; row < positions.Length; row++)
        {
            incremental.Add(row, positions[row]);
        }
        var actual = new List<int>();
        for (var iteration = 0; iteration < 150; iteration++)
        {
            var center = iteration % 15 == 0
                ? new Vector2((iteration % 3) * 10, (iteration % 4) * 10)
                : new Vector2((random.NextSingle() * 40) - 15, (random.NextSingle() * 40) - 15);
            var radius = iteration % 11 == 0 ? 0 : iteration % 13 == 0 ? 5 : random.NextSingle() * 12;
            var expectedRadius = Enumerable.Range(0, positions.Length)
                .Where(row => OracleDistance(positions[row], center, wrapX, wrapY) <= (double)radius * radius)
                .ToArray();
            snapshot.QueryRadius(center, radius, actual);
            Assert.Equal(expectedRadius, actual);
            incremental.QueryRadius(center, radius, actual);
            Assert.Equal(expectedRadius, actual);
            var area = new Bounds2(center.X, center.Y, radius, radius / 2);
            var expectedBounds = Enumerable.Range(0, positions.Length)
                .Where(row => OracleAxisDistance(positions[row].X, center.X, wrapX) <= area.HalfWidth
                    && OracleAxisDistance(positions[row].Y, center.Y, wrapY) <= area.HalfHeight)
                .ToArray();
            snapshot.QueryBounds(area, actual);
            Assert.Equal(expectedBounds, actual);
            incremental.QueryBounds(area, actual);
            Assert.Equal(expectedBounds, actual);
        }
    }

    [Fact]
    public void Rebuilding_from_a_nearest_filter_rejects_the_stale_query()
    {
        var grid = new PopulationSpatialGrid2D(new Bounds2(5, 5, 5, 5), 2);
        grid.Rebuild([Vector2.One]);
        Assert.Throws<InvalidOperationException>(() => grid.TryQueryNearest(Vector2.Zero, out _, row =>
        {
            grid.Rebuild([new(2, 2)]);
            return true;
        }));
        Assert.True(grid.TryQueryNearest(new Vector2(2, 2), out var nearest));
        Assert.Equal(0, nearest);
    }

    [Fact]
    public void Incremental_moves_removals_and_additions_match_a_rebuild_with_sparse_stable_ids()
    {
        var bounds = new Bounds2(5, 5, 5, 5);
        var incremental = new IncrementalPopulationSpatialGrid2D(bounds, 3, wrapX: true, wrapY: true);
        var grid = new PopulationSpatialGrid2D(bounds, 3, wrapX: true, wrapY: true);
        var positions = new SortedDictionary<int, Vector2>();
        var random = new Random(911);
        for (var id = 0; id < 50; id++)
        {
            var position = RandomPosition(random);
            positions.Add(id * 3, position);
            incremental.Add(id * 3, position);
        }

        var incrementalRows = new List<int>();
        var rebuiltRows = new List<int>();
        var incrementalNearest = new int[9];
        var rebuiltNearest = new int[9];
        for (var iteration = 0; iteration < 180; iteration++)
        {
            var row = random.Next(65) * 3;
            if (iteration % 3 == 0)
            {
                Assert.Equal(positions.Remove(row), incremental.Remove(row));
            }
            else if (positions.ContainsKey(row))
            {
                var position = RandomPosition(random);
                positions[row] = position;
                incremental.Update(row, position);
            }
            else
            {
                var position = RandomPosition(random);
                positions.Add(row, position);
                incremental.Add(row, position);
            }

            var ids = positions.Keys.ToArray();
            var snapshot = grid.Rebuild(positions.Values.ToArray());
            var center = new Vector2((random.NextSingle() * 25) - 7, (random.NextSingle() * 25) - 7);
            var radius = random.NextSingle() * 8;
            incremental.QueryRadius(center, radius, incrementalRows);
            snapshot.QueryRadius(center, radius, rebuiltRows);
            Assert.Equal(rebuiltRows.Select(index => ids[index]), incrementalRows);
            var area = new Bounds2(center.X, center.Y, radius, radius / 2);
            incremental.QueryBounds(area, incrementalRows);
            snapshot.QueryBounds(area, rebuiltRows);
            Assert.Equal(rebuiltRows.Select(index => ids[index]), incrementalRows);
            var incrementalCount = incremental.QueryKNearest(center, radius, 9, incrementalNearest);
            var rebuiltCount = snapshot.QueryKNearest(center, radius, 9, rebuiltNearest);
            Assert.Equal(rebuiltNearest.Take(rebuiltCount).Select(index => ids[index]),
                incrementalNearest.Take(incrementalCount));
            Assert.Equal(positions.Count, incremental.Count);
        }
    }

    [Fact]
    public void Incremental_updates_are_immediately_visible_and_invalid_mutations_preserve_state()
    {
        var grid = new IncrementalPopulationSpatialGrid2D(new Bounds2(5, 5, 5, 5), 2, wrapX: true);
        grid.Add(40, new Vector2(1, 5));
        grid.Add(3, new Vector2(9, 5));
        grid.Add(10, new Vector2(5, 5));
        var rows = new List<int>();
        grid.QueryRadius(new Vector2(0, 5), 1, rows);
        Assert.Equal([3, 40], rows);
        grid.Update(40, new Vector2(6, 5));
        Assert.True(grid.Remove(3));
        Assert.False(grid.Remove(3));
        grid.Add(3, new Vector2(7, 5));
        grid.QueryRadius(new Vector2(0, 5), 1, rows);
        Assert.Empty(rows);
        Assert.True(grid.TryQueryNearest(new Vector2(7, 5), out var nearest, row => row != 3));
        Assert.Equal(40, nearest);
        Assert.Throws<ArgumentException>(() => grid.Add(3, new Vector2(1, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.Add(-1, new Vector2(1, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.Update(3, new Vector2(float.NaN, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.Update(3, new Vector2(11, 1)));
        Assert.Throws<KeyNotFoundException>(() => grid.Update(99, new Vector2(1, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.Remove(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.QueryKNearest(Vector2.Zero, -1, new int[2]));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.QueryKNearest(Vector2.Zero, -1f, 0, new int[2]));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.QueryRadius(Vector2.Zero, float.NaN, rows));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.QueryBounds(new Bounds2(0, 0, -1, 1), rows));
        Assert.Throws<ArgumentNullException>(() => grid.QueryRadius(Vector2.Zero, 1, null!));
        Assert.True(grid.TryQueryNearest(new Vector2(7, 5), out nearest));
        Assert.Equal(3, nearest);
        Assert.Equal(3, grid.Count);
        Assert.Equal(0, grid.QueryKNearest(Vector2.Zero, 0, new int[2]));
        Assert.Equal(0, grid.QueryKNearest(Vector2.Zero, 20, Span<int>.Empty));
    }

    [Fact]
    public void Incremental_grid_rejects_invalid_bounds_and_cell_sizes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new IncrementalPopulationSpatialGrid2D(new Bounds2(0, 0, 0, 1), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new IncrementalPopulationSpatialGrid2D(new Bounds2(0, 0, 1, 1), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new IncrementalPopulationSpatialGrid2D(new Bounds2(0, 0, 1, 1), float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new IncrementalPopulationSpatialGrid2D(new Bounds2(0, 0, 1000, 1000), 0.1f));
    }

    [Fact]
    public void Mutating_an_index_from_a_nearest_filter_rejects_the_stale_query()
    {
        var grid = new IncrementalPopulationSpatialGrid2D(new Bounds2(5, 5, 5, 5), 2);
        grid.Add(0, Vector2.One);
        Assert.Throws<InvalidOperationException>(() => grid.TryQueryNearest(Vector2.Zero, out _, row =>
        {
            grid.Update(row, new Vector2(2, 2));
            return true;
        }));
        Assert.True(grid.TryQueryNearest(new Vector2(2, 2), out var nearest));
        Assert.Equal(0, nearest);
    }

    [Fact]
    public void Incremental_nearest_ties_keep_smallest_ids_regardless_of_add_order()
    {
        var grid = new IncrementalPopulationSpatialGrid2D(new Bounds2(5, 5, 5, 5), 3, wrapX: true);
        grid.Add(50, new Vector2(10, 5));
        grid.Add(30, new Vector2(0, 5));
        grid.Add(20, new Vector2(1, 5));
        grid.Add(10, new Vector2(9, 5));
        Span<int> results = stackalloc int[5];
        Assert.True(grid.TryQueryNearest(new Vector2(0, 5), 0, out var nearest));
        Assert.Equal(30, nearest);
        Assert.False(grid.TryQueryNearest(new Vector2(0, 5), 0, out nearest, row => row > 100));
        Assert.Equal(-1, nearest);
        var count = grid.QueryKNearest(new Vector2(0, 5), 1, 10, results, row => row != 30);
        Assert.Equal([50, 10, 20], results[..count].ToArray());
        grid.Remove(50);
        grid.Add(50, new Vector2(10, 5));
        count = grid.QueryKNearest(new Vector2(0, 5), 10, results);
        Assert.Equal([30, 50, 10, 20], results[..count].ToArray());
    }

    [Fact]
    public void Nearest_queries_use_caller_storage_without_query_allocations()
    {
        Vector2[] positions = [new(1, 1), new(2, 2), new(3, 3), new(4, 4)];
        var bounds = new Bounds2(5, 5, 5, 5);
        var grid = new PopulationSpatialGrid2D(bounds, 2);
        grid.Rebuild(positions);
        var incremental = new IncrementalPopulationSpatialGrid2D(bounds, 2);
        for (var row = 0; row < positions.Length; row++)
        {
            incremental.Add(row, positions[row]);
        }
        Span<int> buffer = stackalloc int[3];
        for (var i = 0; i < 32; i++)
        {
            grid.QueryKNearest(Vector2.Zero, 3, buffer);
            incremental.QueryKNearest(Vector2.Zero, 3, buffer);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            grid.QueryKNearest(Vector2.Zero, 3, buffer);
            incremental.QueryKNearest(Vector2.Zero, 3, buffer);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal([0, 1, 2], buffer.ToArray());
    }

    private static Vector2 RandomPosition(Random random) =>
        new(random.NextSingle() * 10, random.NextSingle() * 10);

    private static int[] Oracle(Vector2[] positions, Vector2 center, float? radius, bool wrapX, bool wrapY,
        Predicate<int> filter)
    {
        return Enumerable.Range(0, positions.Length)
            .Where(row => filter(row))
            .Select(row => (Row: row, Distance: OracleDistance(positions[row], center, wrapX, wrapY)))
            .Where(value => radius is null || value.Distance <= (double)radius.Value * radius.Value)
            .OrderBy(value => value.Distance)
            .ThenBy(value => value.Row)
            .Select(value => value.Row)
            .ToArray();
    }

    private static double OracleDistance(Vector2 position, Vector2 center, bool wrapX, bool wrapY)
    {
        var dx = OracleAxisDistance(position.X, center.X, wrapX);
        var dy = OracleAxisDistance(position.Y, center.Y, wrapY);
        return dx * dx + dy * dy;
    }

    private static double OracleAxisDistance(float position, float center, bool wrap)
    {
        var distance = Math.Abs((double)position - center);
        if (wrap)
        {
            distance %= 10;
            distance = Math.Min(distance, 10 - distance);
        }
        return distance;
    }
}
