using System.Numerics;
using Systemic.Kernel;

namespace Systemic.Tests.Kernel;

public sealed class PopulationSpatialGridDiagnosticsTests
{
    [Fact]
    public void Strict_rebuild_names_population_row_value_and_bounds_and_keeps_previous_snapshot()
    {
        var grid = new PopulationSpatialGrid2D(new Bounds2(5, 5, 5, 5), 2);
        var previous = grid.Rebuild([new Vector2(1, 1), new Vector2(9, 9)]);

        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            grid.Rebuild([new Vector2(2, 2), new Vector2(11, 4)], "garden.plants"));

        Assert.Contains("garden.plants", error.Message);
        Assert.Contains("row 1", error.Message);
        Assert.Contains("11", error.Message);
        Assert.Contains("0", error.Message);
        Assert.Contains("10", error.Message);
        Assert.True(previous.TryQueryNearest(new Vector2(1, 1), out var nearest));
        Assert.Equal(0, nearest);
        Assert.Equal(0, grid.InvalidPositionCount);
    }

    [Fact]
    public void Lenient_rebuild_skips_bad_rows_preserves_original_row_ids_and_counts_them()
    {
        var grid = new PopulationSpatialGrid2D(new Bounds2(5, 5, 5, 5), 2);

        var snapshot = grid.Rebuild(
            [new Vector2(1, 1), new Vector2(float.NaN, 3), new Vector2(9, 9)],
            "garden.motes",
            InvalidPositionPolicy.Skip);

        Assert.Equal(1, grid.InvalidPositionCount);
        Assert.True(snapshot.TryQueryNearest(new Vector2(10, 10), out var nearest));
        Assert.Equal(2, nearest);
        Assert.Equal(2, snapshot.QueryKNearest(new Vector2(5, 5), 3, stackalloc int[3]));

        grid.Rebuild([new Vector2(4, 4)], "garden.motes");
        Assert.Equal(0, grid.InvalidPositionCount);
    }
}
