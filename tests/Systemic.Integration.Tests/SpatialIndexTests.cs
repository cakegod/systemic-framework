using Systemic.Kernel;

namespace Systemic.Tests;

public sealed class SpatialIndexTests
{
    [Fact]
    public void Nearby_queries_are_exact_deterministic_and_reusable()
    {
        var index = new SpatialIndex(cellSize: 8);
        index.Upsert(9, new Bounds2(0, 0, 1, 1));
        index.Upsert(2, new Bounds2(3, 0, 1, 1));
        index.Upsert(5, new Bounds2(40, 0, 1, 1));
        var results = new List<int>(8);

        index.QueryNearby(new Point2(0, 0), radius: 5, results);

        Assert.Equal([2, 9], results);
        index.Upsert(9, new Bounds2(20, 20, 1, 1));
        index.QueryNearby(new Point2(0, 0), radius: 5, results);
        Assert.Equal([2], results);
        Assert.True(index.Remove(2));
        index.QueryNearby(new Point2(0, 0), radius: 5, results);
        Assert.Empty(results);
    }

    [Fact]
    public void Overlap_queries_include_edge_touching_bounds_once()
    {
        var index = new SpatialIndex(cellSize: 4);
        index.Upsert(3, new Bounds2(0, 0, 2, 2));
        index.Upsert(1, new Bounds2(4, 0, 2, 2));
        index.Upsert(2, new Bounds2(4.01f, 0, 0, 0));
        var results = new List<int>();

        index.QueryOverlaps(new Bounds2(2, 0, 2, 2), results);

        Assert.Equal([1, 3], results);
    }
}
