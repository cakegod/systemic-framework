using Systemic.Kernel;

namespace Systemic.Gameplay2D.Tests;

public sealed class NavigationOverhaulTests
{
    [Fact]
    public void Equal_cost_path_uses_stable_neighbour_and_priority_order()
    {
        var field = new StaticBlockingField2D(3, 3, [], GridOutOfBoundsPolicy.Blocked);
        var expected = new[] { new GridCell2D(0, 0), new(1, 0), new(2, 0), new(2, 1), new(2, 2) };

        var first = DeterministicNavigation2D.FindPath(field, new(0, 0), new(2, 2));
        var second = DeterministicNavigation2D.FindPath(field, new(0, 0), new(2, 2));

        Assert.Equal(expected, first);
        Assert.Equal(expected, second);
        Assert.Throws<NotSupportedException>(() => ((IList<GridCell2D>)first)[0] = new(1, 1));
    }

    [Fact]
    public void Path_and_reusable_flow_field_route_around_a_blocker()
    {
        var field = new StaticBlockingField2D(3, 3, [new(1, 1)], GridOutOfBoundsPolicy.Blocked);
        var path = DeterministicNavigation2D.FindPath(field, new(0, 1), new(2, 1));

        Assert.Equal(new[] { new GridCell2D(0, 1), new(0, 0), new(1, 0), new(2, 0), new(2, 1) }, path);
        var flow = Assert.IsType<NavigationFlowField2D>(DeterministicNavigation2D.CreateFlowField(field, new(2, 1)));
        Assert.True(flow.TryGetDistance(new(0, 1), out var distance));
        Assert.Equal(4, distance);
        Assert.True(flow.TryGetNextCell(new(0, 1), out var next));
        Assert.Equal(new GridCell2D(0, 0), next);
        Assert.True(flow.TryGetNextCell(new(2, 0), out next));
        Assert.Equal(new GridCell2D(2, 1), next);
        Assert.True(flow.TryGetDistance(new(2, 1), out distance));
        Assert.Equal(0, distance);
        Assert.False(flow.TryGetNextCell(new(2, 1), out _));
        Assert.False(flow.TryGetDistance(new(1, 1), out _));
        Assert.Equal(new[] { 3, 2, 1, 4, -1, 0, 3, 2, 1 }, flow.Distances);
        Assert.Throws<NotSupportedException>(() => ((IList<int>)flow.Distances)[0] = 0);
    }

    [Fact]
    public void Invalid_blocked_or_unreachable_endpoints_have_no_path()
    {
        var field = new StaticBlockingField2D(3, 3, [new(1, 0), new(1, 1), new(1, 2)], GridOutOfBoundsPolicy.Free);
        Assert.Empty(DeterministicNavigation2D.FindPath(field, new(0, 0), new(2, 0)));
        Assert.Empty(DeterministicNavigation2D.FindPath(field, new(1, 0), new(0, 0)));
        Assert.Empty(DeterministicNavigation2D.FindPath(field, new(0, 0), new(1, 0)));
        Assert.Empty(DeterministicNavigation2D.FindPath(field, new(-1, 0), new(0, 0)));
        Assert.Empty(DeterministicNavigation2D.FindPath(field, new(0, 0), new(3, 0)));
        Assert.Null(DeterministicNavigation2D.CreateFlowField(field, new(1, 0)));
        Assert.Null(DeterministicNavigation2D.CreateFlowField(field, new(-1, 0)));

        var flow = Assert.IsType<NavigationFlowField2D>(DeterministicNavigation2D.CreateFlowField(field, new(0, 0)));
        Assert.False(flow.TryGetDistance(new(2, 0), out _));
        Assert.False(flow.TryGetNextCell(new(2, 0), out _));
        Assert.False(flow.TryGetDistance(new(-1, 0), out _));
        Assert.Equal(new[] { new GridCell2D(0, 0) }, DeterministicNavigation2D.FindPath(field, new(0, 0), new(0, 0)));
    }

    [Fact]
    public void Diagonal_contact_does_not_connect_two_four_way_cells()
    {
        var field = new StaticBlockingField2D(2, 2, [new(1, 0), new(0, 1)], GridOutOfBoundsPolicy.Blocked);
        Assert.Empty(DeterministicNavigation2D.FindPath(field, new(0, 0), new(1, 1)));
        var flow = Assert.IsType<NavigationFlowField2D>(DeterministicNavigation2D.CreateFlowField(field, new(1, 1)));
        Assert.False(flow.TryGetDistance(new(0, 0), out _));
    }
}
