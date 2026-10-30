using System.Numerics;
using System.Reflection;
using GlowGarden;
using Xunit;

namespace Systemic.GlowGarden.Tests;

public sealed class GardenObstacleTraversalTests
{
    [Fact]
    public void ClearPathChecksAThinWallBeyondTheOld64SampleLimit()
    {
        var obstacles = OpenField(1024, 64);
        SetBlocked(obstacles, 69, 0, true);

        Assert.False(obstacles.ClearPath(new Vector2(4, 4), new Vector2(1004, 4)));
    }

    [Fact]
    public void SteerDoesNotTakeAFreeEndpointThroughAnInterveningThicket()
    {
        var obstacles = OpenField(256, 128);
        SetBlocked(obstacles, 7, 0, true);
        var from = new Vector2(4, 4);
        var target = new Vector2(100, 4);
        var side = 0;

        var moved = obstacles.Steer(from, target, ref side);

        Assert.NotEqual(target, moved);
        Assert.True(obstacles.ClearPath(from, moved));
    }

    [Fact]
    public void ClearPathRejectsACornerCrossingThatTouchesABlockedSideCell()
    {
        var obstacles = OpenField(64, 64);
        SetBlocked(obstacles, 1, 0, true);

        Assert.False(obstacles.ClearPath(new Vector2(4, 4), new Vector2(12, 12)));
    }

    [Fact]
    public void ClearPathChecksBothSidesOfAGridLineForTheFullSegment()
    {
        var obstacles = OpenField(64, 64);
        SetBlocked(obstacles, 1, 0, true);

        Assert.False(obstacles.ClearPath(new Vector2(4, 8), new Vector2(20, 8)));
    }

    [Fact]
    public void EscapeDoesNotCutBetweenTwoWallsThatMeetAtACorner()
    {
        var obstacles = OpenField(64, 64);
        SetBlocked(obstacles, 1, 0, true);
        SetBlocked(obstacles, 2, 1, true);
        SetBlocked(obstacles, 1, 2, true);
        SetBlocked(obstacles, 0, 1, true);

        var escaped = obstacles.Escape(new Vector2(12, 12), 8, Vector2.One);

        Assert.Equal(new Vector2(12, 12), escaped);
    }

    [Fact]
    public void EscapeUsesAClearCardinalExitFromAFreeWedge()
    {
        var obstacles = OpenField(64, 64);
        SetBlocked(obstacles, 1, 0, true);
        SetBlocked(obstacles, 1, 2, true);
        SetBlocked(obstacles, 0, 1, true);

        var escaped = obstacles.Escape(new Vector2(12, 12), 20, Vector2.UnitX);

        Assert.Equal(new Vector2(20, 12), escaped);
        Assert.True(obstacles.ClearPath(new Vector2(12, 12), escaped));
    }

    private static GardenObstacles OpenField(int width, int height)
    {
        var obstacles = new GardenObstacles(width, height, 1, Vector2.Zero, 0);
        Array.Fill(Field(obstacles), 0);
        return obstacles;
    }

    private static void SetBlocked(GardenObstacles obstacles, int column, int row, bool blocked) =>
        Field(obstacles)[(row * Columns(obstacles)) + column] = blocked ? 1 : 0;

    private static float[] Field(GardenObstacles obstacles) =>
        (float[])typeof(GardenObstacles).GetField("_field", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(obstacles)!;

    private static int Columns(GardenObstacles obstacles) =>
        (int)typeof(GardenObstacles).GetField("_columns", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(obstacles)!;
}
