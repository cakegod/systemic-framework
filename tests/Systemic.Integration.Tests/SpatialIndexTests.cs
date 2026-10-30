using Systemic.Kernel;

namespace Systemic.Tests;

public sealed class SpatialIndexTests
{
    [Fact]
    public void NearbyQueriesAreExactDeterministicAndReusable()
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
    public void OverlapQueriesIncludeEdgeTouchingBoundsOnce()
    {
        var index = new SpatialIndex(cellSize: 4);
        index.Upsert(3, new Bounds2(0, 0, 2, 2));
        index.Upsert(1, new Bounds2(4, 0, 2, 2));
        index.Upsert(2, new Bounds2(4.01f, 0, 0, 0));
        var results = new List<int>();

        index.QueryOverlaps(new Bounds2(2, 0, 2, 2), results);

        Assert.Equal([1, 3], results);
    }

    [Fact]
    public void WorldSpatialQueriesKeepSortedResultsAndFilterPendingRemovals()
    {
        var world = new World(seed: 4, valueScale: 1, spatialCellSize: 8);
        var first = world.Add(new DataEntity([], []));
        var second = world.Add(new DataEntity([], []));
        world.SetSpatialBounds(first, new Bounds2(0, 0, 1, 1));
        world.SetSpatialBounds(second, new Bounds2(0, 0, 1, 1));
        var results = new List<int>();

        world.QuerySpatialNearby(new Point2(0, 0), 2, results);

        Assert.Equal([first.Id, second.Id], results);
        Assert.True(world.Remove(first));
        world.QuerySpatialNearby(new Point2(0, 0), 2, results);
        Assert.Equal([second.Id], results);
        world.PruneRemoved();
        world.QuerySpatialNearby(new Point2(0, 0), 2, results);
        Assert.Equal([second.Id], results);
    }

    [Fact]
    public void SpatialQueriesSupportSparseIdsOutsideTheDenseLookupCache()
    {
        var index = new SpatialIndex(cellSize: 4);
        index.Upsert(int.MaxValue - 1, new Bounds2(0, 0, 1, 1));
        var results = new List<int>();

        index.QueryNearby(new Point2(0, 0), 2, results);

        Assert.Equal([int.MaxValue - 1], results);
    }

    [Fact]
    public void SpatialQueriesFallBackForSparseIdsInsideTheDenseCacheCapacity()
    {
        var index = new SpatialIndex(cellSize: 4);
        index.Upsert(1, new Bounds2(0, 0, 1, 1));
        Assert.True(index.Remove(1));
        index.Upsert(15, new Bounds2(0, 0, 1, 1));
        var results = new List<int>();

        index.QueryNearby(new Point2(0, 0), 2, results);

        Assert.Equal([15], results);
    }

    [Fact]
    public void MovingAPreviouslyCachedEntityAfterRemovalsUpdatesItsBounds()
    {
        var index = new SpatialIndex(cellSize: 10);
        index.Upsert(1, new Bounds2(0, 0, 0, 0));
        index.Upsert(2, new Bounds2(10, 0, 0, 0));
        index.Upsert(3, new Bounds2(20, 0, 0, 0));
        index.Remove(1);
        index.Remove(2);
        index.Upsert(3, new Bounds2(100, 0, 0, 0));
        var results = new List<int>();

        index.QueryOverlaps(new Bounds2(100, 0, 0, 0), results);

        Assert.Equal([3], results);
        index.QueryNearby(new Point2(100, 0), 0, results);
        Assert.Equal([3], results);
    }
}
