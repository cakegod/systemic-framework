using Systemic.Kernel;

namespace Systemic.Tests.Kernel;

public sealed class StaticBlockingFieldTests
{
    private static readonly bool[] s_expectedMask = [false, true, false, false, false, true];

    [Fact]
    public void MaskIsAReadOnlySnapshotOfTheInputCells()
    {
        var blocked = new List<GridCell2D> { new(1, 0), new(2, 1) };
        var field = new StaticBlockingField2D(3, 2, blocked, GridOutOfBoundsPolicy.Blocked);
        blocked.Clear();

        Assert.Equal(s_expectedMask, field.BlockedMask);
        Assert.True(field.IsBlocked(new(1, 0)));
        Assert.Throws<NotSupportedException>(() => ((IList<bool>)field.BlockedMask)[1] = false);
        Assert.True(field.IsBlocked(new(1, 0)));
    }

    [Fact]
    public void DimensionsAndInputCellsMustFitTheBoundedMask()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new StaticBlockingField2D(0, 1, [], GridOutOfBoundsPolicy.Blocked));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new StaticBlockingField2D(2, -1, [], GridOutOfBoundsPolicy.Blocked));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new StaticBlockingField2D(int.MaxValue, 2, [], GridOutOfBoundsPolicy.Blocked));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new StaticBlockingField2D(2, 2, [new(2, 0)], GridOutOfBoundsPolicy.Blocked));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new StaticBlockingField2D(2, 2, [], (GridOutOfBoundsPolicy)42));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(3, 0)]
    [InlineData(0, 2)]
    public void OutOfBoundsQueriesFollowTheExplicitPolicy(int x, int y)
    {
        var blocked = new StaticBlockingField2D(3, 2, [], GridOutOfBoundsPolicy.Blocked);
        var free = new StaticBlockingField2D(3, 2, [], GridOutOfBoundsPolicy.Free);
        var cell = new GridCell2D(x, y);

        Assert.False(blocked.IsInBounds(cell));
        Assert.True(blocked.IsBlocked(cell));
        Assert.False(free.IsBlocked(cell));
        Assert.True(blocked.IsInBounds(new(2, 1)));
    }

    [Fact]
    public void LineOfSightChecksEndpointsAndOutOfBoundsPolicy()
    {
        var field = new StaticBlockingField2D(4, 2, [new(0, 0), new(3, 0)], GridOutOfBoundsPolicy.Blocked);
        Assert.False(field.HasLineOfSight(new(0, 0), new(2, 0)));
        Assert.False(field.HasLineOfSight(new(1, 0), new(3, 0)));
        Assert.False(field.HasLineOfSight(new(-1, 1), new(2, 1)));
        Assert.True(field.HasLineOfSight(new(1, 1), new(1, 1)));

        var freeOutside = new StaticBlockingField2D(4, 2, [], GridOutOfBoundsPolicy.Free);
        Assert.True(freeOutside.HasLineOfSight(new(-1, 1), new(4, 1)));
    }

    [Fact]
    public void LineOfSightHandlesExtremeOutsideCoordinatesWithoutOverflow()
    {
        var field = new StaticBlockingField2D(2, 2, [new(1, 0)], GridOutOfBoundsPolicy.Free);
        var empty = new StaticBlockingField2D(2, 2, [], GridOutOfBoundsPolicy.Free);
        var first = new GridCell2D(int.MinValue, int.MinValue);
        var last = new GridCell2D(int.MaxValue, int.MaxValue);

        Assert.False(field.HasLineOfSight(first, last));
        Assert.False(field.HasLineOfSight(last, first));
        Assert.True(empty.HasLineOfSight(first, last));
    }

    [Fact]
    public void LineOfSightCannotSkipAThinObstacle()
    {
        var field = new StaticBlockingField2D(7, 4, [new(3, 2), new(1, 0)], GridOutOfBoundsPolicy.Blocked);

        Assert.False(field.HasLineOfSight(new(0, 2), new(6, 2)));
        Assert.False(field.HasLineOfSight(new(6, 2), new(0, 2)));
        Assert.False(field.HasLineOfSight(new(0, 0), new(3, 2)));
        Assert.True(field.HasLineOfSight(new(0, 3), new(6, 3)));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public void LineOfSightChecksBothCellsTouchedAtACorner(int blockedX, int blockedY)
    {
        var field = new StaticBlockingField2D(2, 2, [new(blockedX, blockedY)], GridOutOfBoundsPolicy.Blocked);
        Assert.False(field.HasLineOfSight(new(0, 0), new(1, 1)));
        Assert.False(field.HasLineOfSight(new(1, 1), new(0, 0)));
    }

    [Fact]
    public void ConnectivityIsFourWayBoundedAndHasStableBreadthFirstOrder()
    {
        var field = new StaticBlockingField2D(3, 3, [new(1, 0), new(1, 1), new(1, 2)], GridOutOfBoundsPolicy.Free);

        var connected = field.GetConnectedCells(new(0, 1));
        Assert.Equal(new[] { new GridCell2D(0, 1), new(0, 0), new(0, 2) }, connected);
        Assert.Throws<NotSupportedException>(() => ((IList<GridCell2D>)connected)[0] = new(2, 1));
        Assert.True(field.AreConnected(new(0, 0), new(0, 2)));
        Assert.False(field.AreConnected(new(0, 1), new(2, 1)));
        Assert.False(field.AreConnected(new(0, 1), new(1, 1)));
        Assert.False(field.AreConnected(new(0, 1), new(-1, 1)));
        Assert.Empty(field.GetConnectedCells(new(1, 1)));
        Assert.Empty(field.GetConnectedCells(new(-1, 1)));
    }

    [Fact]
    public void NearestFreeCellUsesManhattanDistanceAndRowMajorTies()
    {
        var field = new StaticBlockingField2D(3, 3, [new(1, 1)], GridOutOfBoundsPolicy.Blocked);
        Assert.True(field.TryFindNearestFree(new(1, 1), out var nearest));
        Assert.Equal(new GridCell2D(1, 0), nearest);
        Assert.True(field.TryFindNearestFree(new(int.MinValue, 2), out var edge));
        Assert.Equal(new GridCell2D(0, 2), edge);

        var full = new StaticBlockingField2D(2, 1, [new(0, 0), new(1, 0)], GridOutOfBoundsPolicy.Free);
        Assert.False(full.TryFindNearestFree(new(0, 0), out _));
        Assert.Empty(full.GetConnectedCells(new(0, 0)));
        Assert.False(full.AreConnected(new(0, 0), new(0, 0)));
    }
}
