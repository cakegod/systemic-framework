using System.Numerics;
using Systemic.Kernel;

namespace Systemic.Tests.Kernel;

/// <summary>
/// Pins the one query contract the kernel's spatial grids share. The grids keep different storage
/// because they answer different access patterns — a rebuilt cell table for dense rows, an
/// incremental bucket grid for moving rows, a hashed table for unbounded entities — but a query for
/// the same scene under the same rules must return the same rows in the same order.
/// </summary>
public sealed class SpatialGridParityTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Rebuilt_and_incremental_population_grids_agree_with_brute_force(bool wrapX, bool wrapY)
    {
        var random = new Random(51_237);
        var bounds = new Bounds2(120, -80, 250, 180);
        var positions = new Vector2[120];
        for (var row = 0; row < positions.Length; row++)
        {
            positions[row] = new Vector2(
                bounds.Left + (random.NextSingle() * bounds.HalfWidth * 2f),
                bounds.Top + (random.NextSingle() * bounds.HalfHeight * 2f));
        }

        positions[4] = new Vector2(bounds.Left, bounds.Top);
        positions[9] = new Vector2(bounds.Right, bounds.Bottom);

        var grid = new PopulationSpatialGrid2D(bounds, 17, wrapX, wrapY);
        var snapshot = grid.Rebuild(positions);
        var incremental = new IncrementalPopulationSpatialGrid2D(bounds, 17, wrapX, wrapY);
        for (var row = 0; row < positions.Length; row++)
        {
            incremental.Add(row, positions[row]);
        }

        var actual = new List<int>();
        for (var query = 0; query < 40; query++)
        {
            var center = new Vector2(
                bounds.X + ((random.NextSingle() - 0.5f) * bounds.HalfWidth * 6f),
                bounds.Y + ((random.NextSingle() - 0.5f) * bounds.HalfHeight * 6f));
            var radius = random.NextSingle() * 90;

            var expected = Enumerable.Range(0, positions.Length)
                .Where(row => DistanceSquared(positions[row], center, bounds, wrapX, wrapY) <= (double)radius * radius)
                .ToArray();

            grid.QueryRadius(center, radius, actual);
            Assert.Equal(expected, actual);

            incremental.QueryRadius(center, radius, actual);
            Assert.Equal(expected, actual);

            var area = new Bounds2(center.X, center.Y, radius, radius);
            var expectedInBounds = Enumerable.Range(0, positions.Length)
                .Where(row => AxisDistance(positions[row].X, center.X, bounds.HalfWidth * 2d, wrapX) <= area.HalfWidth
                    && AxisDistance(positions[row].Y, center.Y, bounds.HalfHeight * 2d, wrapY) <= area.HalfHeight)
                .ToArray();

            snapshot.QueryBounds(area, actual);
            Assert.Equal(expectedInBounds, actual);

            incremental.QueryBounds(area, actual);
            Assert.Equal(expectedInBounds, actual);
        }
    }

    [Fact]
    public void Entity_index_matches_the_population_grid_for_the_same_points()
    {
        var random = new Random(88_412);
        var bounds = new Bounds2(0, 0, 400, 400);
        var positions = new Vector2[300];
        for (var row = 0; row < positions.Length; row++)
        {
            positions[row] = new Vector2((random.NextSingle() * 800f) - 400f, (random.NextSingle() * 800f) - 400f);
        }

        var grid = new PopulationSpatialGrid2D(bounds, 24);
        grid.Rebuild(positions);

        // The entity index is keyed by entity ID and stores boxes; zero extents make each entry the
        // same point the population grid holds, so both must answer the same query identically.
        var index = new SpatialIndex(24);
        for (var row = 0; row < positions.Length; row++)
        {
            index.Upsert(row + 1, new Bounds2(positions[row].X, positions[row].Y, 0, 0));
        }

        var expected = new List<int>();
        var actual = new List<int>();
        for (var query = 0; query < 60; query++)
        {
            var center = positions[query * 5 % positions.Length];
            var radius = 20 + (random.NextSingle() * 120);
            grid.QueryRadius(center, radius, expected);
            index.QueryNearby(new Point2(center.X, center.Y), radius, actual);
            Assert.Equal(expected.Select(row => row + 1), actual);
        }
    }

    [Fact]
    public void Entity_index_answers_queries_a_bounded_population_grid_rejects()
    {
        var index = new SpatialIndex(32);
        var positions = new[] { new Vector2(-30_000, 12_500), new Vector2(9_000, -4_000) };
        for (var row = 0; row < positions.Length; row++)
        {
            index.Upsert(row + 1, new Bounds2(positions[row].X, positions[row].Y, 4, 4));
        }

        var results = new List<int>();
        index.QueryNearby(new Point2(-30_010, 12_500), 30, results);
        Assert.Equal([1], results);

        index.QueryOverlaps(new Bounds2(9_000, -4_000, 8, 8), results);
        Assert.Equal([2], results);

        var grid = new PopulationSpatialGrid2D(new Bounds2(0, 0, 1_000, 1_000), 32);
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.Rebuild(positions));
    }

    private static double DistanceSquared(Vector2 position, Vector2 center, Bounds2 bounds, bool wrapX, bool wrapY)
    {
        var dx = AxisDistance(position.X, center.X, bounds.HalfWidth * 2d, wrapX);
        var dy = AxisDistance(position.Y, center.Y, bounds.HalfHeight * 2d, wrapY);
        return (dx * dx) + (dy * dy);
    }

    private static double AxisDistance(double position, double center, double size, bool wrap)
    {
        var distance = Math.Abs(center - position);
        if (!wrap)
        {
            return distance;
        }

        distance %= size;
        return Math.Min(distance, size - distance);
    }
}
