using System.Numerics;
using Systemic.Kernel;

namespace Systemic.Tests.Kernel;

public sealed class SpatialTraversalTests
{
    [Fact]
    public void Nearest_stops_before_filtering_rows_in_cells_that_cannot_improve_result()
    {
        Vector2[] positions = [new(50.1f, 50.1f), new(95, 95), new(3, 6), new(91, 1)];
        var bounds = new Bounds2(50, 50, 50, 50);
        var grid = new PopulationSpatialGrid2D(bounds, 5);
        grid.Rebuild(positions);
        var incremental = new IncrementalPopulationSpatialGrid2D(bounds, 5);
        for (var row = 0; row < positions.Length; row++) incremental.Add(row, positions[row]);
        var visits = 0;
        Predicate<int> filter = _ => { visits++; return true; };
        Assert.True(grid.TryQueryNearest(new Vector2(50, 50), out var nearest, filter));
        Assert.Equal(0, nearest);
        Assert.Equal(1, visits);
        visits = 0;
        Assert.True(incremental.TryQueryNearest(new Vector2(50, 50), out nearest, filter));
        Assert.Equal(0, nearest);
        Assert.Equal(1, visits);
    }

    [Fact]
    public void Nearest_continues_across_cell_boundaries_for_equal_distance_and_smaller_row()
    {
        Vector2[] positions = [new(49, 50), new(51, 50), new(90, 90)];
        var bounds = new Bounds2(50, 50, 50, 50);
        var grid = new PopulationSpatialGrid2D(bounds, 10);
        grid.Rebuild(positions);
        var incremental = new IncrementalPopulationSpatialGrid2D(bounds, 10);
        for (var row = 0; row < positions.Length; row++) incremental.Add(row, positions[row]);
        Assert.True(grid.TryQueryNearest(new Vector2(50, 50), out var nearest));
        Assert.Equal(0, nearest);
        Assert.True(incremental.TryQueryNearest(new Vector2(50, 50), out nearest));
        Assert.Equal(0, nearest);
        Span<int> rows = stackalloc int[2];
        Assert.Equal(2, grid.QueryKNearest(new Vector2(50, 50), 2, rows));
        Assert.Equal([0, 1], rows.ToArray());
        Assert.Equal(2, incremental.QueryKNearest(new Vector2(50, 50), 2, rows));
        Assert.Equal([0, 1], rows.ToArray());
    }

    [Fact]
    public void Wrapped_partial_seam_search_finds_smaller_tie_and_skips_far_cells()
    {
        Vector2[] positions = [new(99, 50), new(1, 50), new(40, 50), new(70, 75)];
        var bounds = new Bounds2(50, 50, 50, 50);
        var grid = new PopulationSpatialGrid2D(bounds, 7, wrapX: true);
        grid.Rebuild(positions);
        var incremental = new IncrementalPopulationSpatialGrid2D(bounds, 7, wrapX: true);
        for (var row = 0; row < positions.Length; row++) incremental.Add(row, positions[row]);
        var visits = 0;
        Predicate<int> filter = _ => { visits++; return true; };
        Assert.True(grid.TryQueryNearest(new Vector2(0, 50), out var nearest, filter));
        Assert.Equal(0, nearest);
        Assert.Equal(2, visits);
        visits = 0;
        Assert.True(incremental.TryQueryNearest(new Vector2(0, 50), out nearest, filter));
        Assert.Equal(0, nearest);
        Assert.Equal(2, visits);
    }

    [Fact]
    public void K_nearest_with_filter_continues_past_rejected_near_rows_and_exits_after_filling()
    {
        Vector2[] positions = [new(50.1f, 50), new(51, 50), new(52, 50), new(99, 99)];
        var bounds = new Bounds2(50, 50, 50, 50);
        var grid = new PopulationSpatialGrid2D(bounds, 5);
        grid.Rebuild(positions);
        var incremental = new IncrementalPopulationSpatialGrid2D(bounds, 5);
        for (var row = 0; row < positions.Length; row++) incremental.Add(row, positions[row]);
        var visits = 0;
        Predicate<int> filter = row => { visits++; return row != 0; };
        Span<int> rows = stackalloc int[2];
        Assert.Equal(2, grid.QueryKNearest(new Vector2(50, 50), 2, rows, filter));
        Assert.Equal([1, 2], rows.ToArray());
        Assert.Equal(3, visits);
        visits = 0;
        Assert.Equal(2, incremental.QueryKNearest(new Vector2(50, 50), 2, rows, filter));
        Assert.Equal([1, 2], rows.ToArray());
        Assert.Equal(3, visits);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Ring_traversal_matches_brute_force_for_irregular_bounds_and_distant_centers(bool wrapX, bool wrapY)
    {
        var random = new Random(76239);
        for (var world = 0; world < 25; world++)
        {
            var bounds = new Bounds2(random.Next(-300, 300), random.Next(-300, 300),
                0.5f + random.NextSingle() * 45, 0.5f + random.NextSingle() * 45);
            var positions = new Vector2[75];
            positions[0] = new Vector2(bounds.Left, bounds.Top);
            positions[1] = new Vector2(bounds.Right, bounds.Bottom);
            for (var row = 2; row < positions.Length; row++)
            {
                positions[row] = new Vector2(
                    Math.Clamp(bounds.Left + random.NextSingle() * (bounds.Right - bounds.Left), bounds.Left, bounds.Right),
                    Math.Clamp(bounds.Top + random.NextSingle() * (bounds.Bottom - bounds.Top), bounds.Top, bounds.Bottom));
            }
            var cellSize = 0.5f + random.NextSingle() * 18;
            var grid = new PopulationSpatialGrid2D(bounds, cellSize, wrapX, wrapY);
            grid.Rebuild(positions);
            var incremental = new IncrementalPopulationSpatialGrid2D(bounds, cellSize, wrapX, wrapY);
            for (var row = 0; row < positions.Length; row++) incremental.Add(row * 7 + 3, positions[row]);
            var actual = new int[9];
            for (var query = 0; query < 30; query++)
            {
                var center = query switch
                {
                    0 => new Vector2(bounds.Left, bounds.Top),
                    1 => new Vector2(bounds.Right, bounds.Bottom),
                    2 => new Vector2(float.MaxValue, -float.MaxValue),
                    _ => new Vector2(bounds.X + ((random.NextSingle() - 0.5f) * bounds.HalfWidth * 8),
                        bounds.Y + ((random.NextSingle() - 0.5f) * bounds.HalfHeight * 8))
                };
                var radius = random.NextSingle() * 100;
                var expected = Enumerable.Range(0, positions.Length)
                    .Where(row => row % 4 != 0)
                    .Select(row => (Row: row, Distance: Distance(positions[row], center, bounds, wrapX, wrapY)))
                    .Where(value => value.Distance <= (double)radius * radius)
                    .OrderBy(value => value.Distance).ThenBy(value => value.Row)
                    .Take(actual.Length).Select(value => value.Row).ToArray();
                var count = grid.QueryKNearest(center, radius, actual.Length, actual, row => row % 4 != 0);
                Assert.Equal(expected, actual.Take(count));
                count = incremental.QueryKNearest(center, radius, actual.Length, actual, id => ((id - 3) / 7) % 4 != 0);
                Assert.Equal(expected.Select(row => row * 7 + 3), actual.Take(count));
            }
        }
    }

    private static double Distance(Vector2 position, Vector2 center, Bounds2 bounds, bool wrapX, bool wrapY)
    {
        var dx = Math.Abs((double)position.X - center.X);
        var dy = Math.Abs((double)position.Y - center.Y);
        if (wrapX)
        {
            var width = (double)bounds.HalfWidth * 2;
            dx %= width;
            dx = Math.Min(dx, width - dx);
        }
        if (wrapY)
        {
            var height = (double)bounds.HalfHeight * 2;
            dy %= height;
            dy = Math.Min(dy, height - dy);
        }
        return dx * dx + dy * dy;
    }
}
