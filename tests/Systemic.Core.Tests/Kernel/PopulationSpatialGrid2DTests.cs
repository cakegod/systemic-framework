using System.Numerics;
using Systemic.Kernel;

namespace Systemic.Tests.Kernel;

public sealed class PopulationSpatialGrid2DTests
{
    [Fact]
    public void Radius_and_bounds_queries_return_exact_rows_once_in_stable_order()
    {
        Vector2[] positions =
        [
            new(5, 5),
            new(3, 5),
            new(9, 9),
            new(4, 4),
            new(7, 7)
        ];
        var grid = new PopulationSpatialGrid2D(new Bounds2(5, 5, 5, 5), cellSize: 2);
        grid.Reserve(positions.Length);
        var snapshot = grid.Rebuild(positions);
        var results = new List<int>(positions.Length);

        snapshot.QueryRadius(new Vector2(4, 5), 1.5f, results);

        Assert.Equal([0, 1, 3], results);

        snapshot.QueryBounds(new Bounds2(4, 5, 1, 1), results);

        Assert.Equal([0, 1, 3], results);
    }

    [Fact]
    public void Bounded_radius_queries_keep_the_lowest_rows_in_stable_order()
    {
        Vector2[] positions = [new(5, 5), new(3, 5), new(6, 5), new(4, 5), new(5, 6)];
        var grid = new PopulationSpatialGrid2D(new Bounds2(5, 5, 5, 5), cellSize: 2);
        var results = new List<int>(2);

        grid.Rebuild(positions).QueryRadius(new Vector2(5, 5), 2, results, maximumResults: 2);

        Assert.Equal([0, 1], results);
    }

    [Fact]
    public void Candidate_queries_stop_early_in_repeatable_cell_order()
    {
        Vector2[] positions = [new(9, 9), new(1, 1), new(1.5f, 1.5f)];
        var grid = new PopulationSpatialGrid2D(new Bounds2(5, 5, 5, 5), cellSize: 2);
        var results = new List<int>(1);

        grid.Rebuild(positions).QueryRadiusCandidates(new Vector2(5, 5), 5, results, maximumResults: 1);

        Assert.Equal([1], results);
    }

    [Fact]
    public void Wrapped_queries_cross_world_edges_without_duplicate_rows()
    {
        Vector2[] positions =
        [
            new(0.5f, 5),
            new(9.5f, 5),
            new(9.5f, 9.5f)
        ];
        var grid = new PopulationSpatialGrid2D(new Bounds2(5, 5, 5, 5), cellSize: 2,
            wrapX: true, wrapY: true);
        var snapshot = grid.Rebuild(positions);
        var results = new List<int>(positions.Length);

        snapshot.QueryRadius(new Vector2(0.25f, 5), 1, results);

        Assert.Equal([0, 1], results);

        snapshot.QueryRadius(new Vector2(5, 5), 8, results);

        Assert.Equal([0, 1, 2], results);
    }

    [Fact]
    public void Wrapped_neighbor_cell_candidates_use_repeatable_order()
    {
        Vector2[] positions = [new(0.5f, 5), new(9.5f, 5), new(5, 5)];
        var grid = new PopulationSpatialGrid2D(new Bounds2(5, 5, 5, 5), cellSize: 2,
            wrapX: true, wrapY: true);
        var results = new List<int>(2);

        grid.Rebuild(positions).QueryRadiusCandidates(new Vector2(0.5f, 5), 2, results, maximumResults: 2);

        Assert.Equal([1, 0], results);
    }

    [Fact]
    public void Wrapped_candidate_query_covers_partial_final_cells_and_honors_zero_limit()
    {
        Vector2[] positions =
        [
            new(580, 300), // cell 24
            new(610, 300), // cell 25
            new(630, 300), // partial cell 26
            new(10, 300)   // wrapped cell 0
        ];
        var nonDivisibleGrid = new PopulationSpatialGrid2D(
            new Bounds2(320, 320, 320, 320), cellSize: 24, wrapX: true, wrapY: true);
        var results = new List<int>(positions.Length);

        nonDivisibleGrid.Rebuild(positions)
            .QueryRadiusCandidates(new Vector2(623.99f, 300), 24, results, positions.Length);

        Assert.Equal([0, 1, 2, 3], results);

        var divisibleGrid = new PopulationSpatialGrid2D(
            new Bounds2(10, 10, 10, 10), cellSize: 5, wrapX: true, wrapY: true);
        divisibleGrid.Rebuild([new Vector2(5, 5)])
            .QueryRadiusCandidates(new Vector2(5, 5), 5, results, maximumResults: 0);

        Assert.Empty(results);
    }

    [Fact]
    public void Span_candidate_query_uses_caller_storage_and_handles_an_empty_destination()
    {
        Vector2[] positions = [new(580, 300), new(610, 300), new(630, 300), new(10, 300)];
        var grid = new PopulationSpatialGrid2D(
            new Bounds2(320, 320, 320, 320), cellSize: 24, wrapX: true, wrapY: true);
        var snapshot = grid.Rebuild(positions);
        Span<int> results = stackalloc int[4];

        var count = snapshot.QueryRadiusCandidates(new Vector2(623.99f, 300), 24, results);

        Assert.Equal([0, 1, 2, 3], results[..count].ToArray());
        Assert.Equal([0, 1], results[..grid.QueryCellNeighborhoodCandidates(0, results)].ToArray());
        Assert.Equal(0, snapshot.QueryRadiusCandidates(new Vector2(623.99f, 300), 24, []));
    }

    [Fact]
    public void Row_candidate_enumerator_matches_span_order_and_supports_early_exit()
    {
        Vector2[] positions = [new(580, 300), new(610, 300), new(630, 300), new(10, 300)];
        var grid = new PopulationSpatialGrid2D(
            new Bounds2(320, 320, 320, 320), cellSize: 24, wrapX: true, wrapY: true);
        var snapshot = grid.Rebuild(positions);
        Span<int> expected = stackalloc int[positions.Length];
        var expectedCount = snapshot.QueryCellNeighborhoodCandidates(0, expected);
        var actual = new List<int>(expectedCount);

        foreach (var row in snapshot.QueryCellNeighborhoodCandidates(0))
        {
            actual.Add(row);
        }

        Assert.Equal(expected[..expectedCount].ToArray(), actual);

        var first = snapshot.QueryCellNeighborhoodCandidates(0);
        Assert.True(first.MoveNext());
        Assert.Equal(actual[0], first.Current);
        Assert.True(first.MoveNext());
    }

    [Fact]
    public void Wrapped_row_enumerator_matches_span_order_at_cell_edges_and_partial_world_edges()
    {
        Vector2[] positions =
        [
            new(0, 0), new(1, 1), new(15.999f, 15.999f), new(16, 16),
            new(31.999f, 32), new(63.999f, 50), new(127.999f, 99.999f),
            new(100, 99), new(80, 48), new(64, 64)
        ];
        var grid = new PopulationSpatialGrid2D(new Bounds2(64, 50, 64, 50), cellSize: 16,
            wrapX: true, wrapY: true);
        var snapshot = grid.Rebuild(positions);
        Span<int> expected = stackalloc int[positions.Length];
        var actual = new List<int>(positions.Length);

        foreach (var centerRow in Enumerable.Range(0, positions.Length))
        {
            foreach (var offset in new[] { -9, -1, 0, 8, 17 })
            {
                var expectedCount = snapshot.QueryCellNeighborhoodCandidates(centerRow, expected, offset);
                actual.Clear();
                foreach (var row in snapshot.QueryCellNeighborhoodCandidates(centerRow, offset))
                {
                    actual.Add(row);
                }

                Assert.Equal(expected[..expectedCount].ToArray(), actual);
            }
        }
    }

    [Fact]
    public void Warmed_row_candidate_enumeration_does_not_allocate()
    {
        Vector2[] positions = new Vector2[128];
        for (var row = 0; row < positions.Length; row++)
        {
            positions[row] = new Vector2((row % 16) * 6, (row / 16) * 6);
        }

        var grid = new PopulationSpatialGrid2D(new Bounds2(45, 21, 45, 21), cellSize: 8, wrapX: true, wrapY: true);
        grid.Reserve(positions.Length);
        grid.Rebuild(positions);
        for (var iteration = 0; iteration < 8; iteration++)
        {
            foreach (var row in grid.QueryCellNeighborhoodCandidates(0))
            {
                _ = row;
            }
        }

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 100; iteration++)
        {
            foreach (var row in grid.QueryCellNeighborhoodCandidates(0))
            {
                _ = row;
            }
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
    }

    [Fact]
    public void Rebuilding_invalidates_an_active_candidate_enumerator()
    {
        Vector2[] positions = [new(2, 3), new(2.1f, 3)];
        var grid = new PopulationSpatialGrid2D(new Bounds2(5, 5, 5, 5), cellSize: 2);
        var previous = grid.Rebuild(positions);
        var candidates = previous.QueryCellNeighborhoodCandidates(0);
        Assert.True(candidates.MoveNext());
        grid.Rebuild([new Vector2(2, 3)]);

        var invalidated = false;
        try
        {
            candidates.MoveNext();
        }
        catch (InvalidOperationException)
        {
            invalidated = true;
        }

        Assert.True(invalidated);
    }

    [Fact]
    public void Invalid_rebuild_keeps_the_previous_wrapped_row_candidate_snapshot_intact()
    {
        var grid = new PopulationSpatialGrid2D(
            new Bounds2(10, 10, 10, 10), cellSize: 4, wrapX: true, wrapY: true);
        var snapshot = grid.Rebuild([new Vector2(8, 8), new Vector2(5, 5)]);
        Span<int> expected = stackalloc int[4];
        var expectedCount = snapshot.QueryCellNeighborhoodCandidates(0, expected);
        Assert.Equal([1, 0], expected[..expectedCount].ToArray());

        Assert.Throws<ArgumentOutOfRangeException>(() => grid.Rebuild([new Vector2(12, 12), new Vector2(21, 5)]));

        Span<int> actual = stackalloc int[4];
        var actualCount = snapshot.QueryCellNeighborhoodCandidates(0, actual);
        Assert.Equal([1, 0], actual[..actualCount].ToArray());
        Assert.Equal(expected[..expectedCount].ToArray(), actual[..actualCount].ToArray());
    }

    [Fact]
    public void Reserved_span_candidate_queries_do_not_allocate()
    {
        Vector2[] positions = new Vector2[128];
        for (var row = 0; row < positions.Length; row++)
        {
            positions[row] = new Vector2((row % 16) * 6, (row / 16) * 6);
        }

        var grid = new PopulationSpatialGrid2D(new Bounds2(45, 21, 45, 21), cellSize: 8, wrapX: true, wrapY: true);
        grid.Reserve(positions.Length);
        Span<int> results = stackalloc int[12];
        for (var iteration = 0; iteration < 8; iteration++)
        {
            grid.Rebuild(positions).QueryCellNeighborhoodCandidates(0, results);
        }

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 100; iteration++)
        {
            grid.Rebuild(positions).QueryCellNeighborhoodCandidates(0, results);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
    }

    [Fact]
    public void Wrapped_query_spanning_multiple_cells_after_seam_keeps_all_candidates()
    {
        Vector2[] positions = [new(45, 5), new(75, 5), new(5, 5), new(100, 5)];
        var grid = new PopulationSpatialGrid2D(new Bounds2(52.5f, 5, 52.5f, 5), cellSize: 30,
            wrapX: true, wrapY: true);
        var results = new List<int>(positions.Length);

        grid.Rebuild(positions).QueryRadius(new Vector2(110, 5), 40, results);

        Assert.Equal([0, 1, 2, 3], results);
    }

    [Fact]
    public void Rebuilding_invalidates_older_snapshots()
    {
        Vector2[] positions = [new(2, 3)];
        var grid = new PopulationSpatialGrid2D(new Bounds2(5, 5, 5, 5), cellSize: 2);
        var oldSnapshot = grid.Rebuild(positions);
        var nextSnapshot = grid.Rebuild(positions);
        var results = new List<int>(1);

        var staleSnapshotRejected = false;
        try
        {
            oldSnapshot.QueryRadius(new Vector2(2, 3), 1, results);
        }
        catch (InvalidOperationException)
        {
            staleSnapshotRejected = true;
        }

        Assert.True(staleSnapshotRejected);
        nextSnapshot.QueryRadius(new Vector2(2, 3), 1, results);
        Assert.Equal([0], results);
    }

    [Fact]
    public void Invalid_rebuild_preserves_the_previous_snapshot()
    {
        var validPositions = new Vector2[] { new(2, 3), new(8, 7) };
        var invalidPositions = new Vector2[] { new(4, 5), new(float.NaN, 1) };
        var grid = new PopulationSpatialGrid2D(new Bounds2(5, 5, 5, 5), cellSize: 2);
        var previous = grid.Rebuild(validPositions);
        var results = new List<int>(2);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            _ = grid.Rebuild(invalidPositions);
        });
        previous.QueryRadius(new Vector2(2, 3), 0.1f, results);

        Assert.Equal([0], results);
    }

    [Fact]
    public void Radius_queries_match_a_brute_force_reference_for_seeded_positions()
    {
        var random = new Random(1234);
        var positions = new Vector2[256];
        for (var index = 0; index < positions.Length; index++)
        {
            positions[index] = new Vector2(random.NextSingle() * 100, random.NextSingle() * 100);
        }

        var grid = new PopulationSpatialGrid2D(new Bounds2(50, 50, 50, 50), cellSize: 7.5f);
        var snapshot = grid.Rebuild(positions);
        var actual = new List<int>(positions.Length);
        var expected = new List<int>(positions.Length);
        for (var query = 0; query < 40; query++)
        {
            var center = new Vector2(random.NextSingle() * 100, random.NextSingle() * 100);
            var radius = 1.0f + random.NextSingle() * 24.0f;
            snapshot.QueryRadius(center, radius, actual);
            expected.Clear();
            var radiusSquared = radius * radius;
            for (var row = 0; row < positions.Length; row++)
            {
                var dx = positions[row].X - center.X;
                var dy = positions[row].Y - center.Y;
                if ((dx * dx) + (dy * dy) <= radiusSquared)
                {
                    expected.Add(row);
                }
            }

            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Reserved_rebuild_and_query_path_does_not_allocate()
    {
        var positions = new Vector2[128];
        for (var row = 0; row < positions.Length; row++)
        {
            positions[row] = new Vector2((row % 16) * 6, (row / 16) * 6);
        }

        var grid = new PopulationSpatialGrid2D(new Bounds2(45, 21, 45, 21), cellSize: 8);
        grid.Reserve(positions.Length);
        var results = new List<int>(positions.Length);
        for (var iteration = 0; iteration < 8; iteration++)
        {
            grid.Rebuild(positions).QueryRadius(new Vector2(24, 24), 15, results);
        }

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 100; iteration++)
        {
            grid.Rebuild(positions).QueryRadius(new Vector2(24, 24), 15, results);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
        Assert.NotEmpty(results);
    }
}
