using Microsoft.Xna.Framework;
using PixelMine;
using Xunit;

namespace Systemic.PixelMine.Tests;

public sealed class MineMenuTests
{
    [Fact]
    public void MenuChangesSettingsAndStartsGame()
    {
        var menu = new MineMenu();
        Assert.Equal(MineScreen.Title, menu.Screen);
        Assert.True(menu.VSync);
        Assert.Equal(60, menu.FrameLimit);

        menu.Move(1);
        menu.Activate();
        Assert.Equal(MineScreen.Settings, menu.Screen);
        menu.Activate();
        Assert.False(menu.VSync);
        menu.Move(1);
        menu.Activate();
        Assert.Equal(120, menu.FrameLimit);
        menu.Activate();
        Assert.Equal(0, menu.FrameLimit);
        menu.Activate();
        Assert.Equal(30, menu.FrameLimit);
        menu.Activate(-1);
        Assert.Equal(0, menu.FrameLimit);

        menu.Back();
        Assert.Equal(MineScreen.Title, menu.Screen);
        menu.Move(-1);
        menu.Activate();
        Assert.Equal(MineScreen.Playing, menu.Screen);
        Assert.False(menu.VSync);
        Assert.Equal(0, menu.FrameLimit);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(240)]
    public void SimulationRunsAtSixtyTicksRegardlessOfFrameRate(int framesPerSecond)
    {
        long remainder = 0;
        var ticks = 0;
        for (var frame = 0; frame < framesPerSecond; frame++)
        {
            var start = frame * TimeSpan.TicksPerSecond / framesPerSecond;
            var end = (frame + 1) * TimeSpan.TicksPerSecond / framesPerSecond;
            ticks += MineGame.SimulationTicks(ref remainder, TimeSpan.FromTicks(end - start));
        }

        Assert.Equal(60, ticks);
        Assert.Equal(0, remainder);
    }

    [Fact]
    public void BackdropBallBouncesOffTheCaveBoundary()
    {
        var position = new Vector2(1233, 400);
        var velocity = new Vector2(80, 0);

        var bounced = MineMenuBackdrop.AdvanceBall(ref position, ref velocity, 0.05f);

        Assert.True(bounced);
        Assert.Equal(new Vector2(1233, 400), position);
        Assert.True(velocity.X < 0);
    }

    [Fact]
    public void MenuBallTravelsWithinTheSelectedButton()
    {
        var start = MineGame.MenuBallOffset(0f);
        var later = MineGame.MenuBallOffset(0.5f);

        Assert.InRange(start, 40f, 430f);
        Assert.InRange(later, 40f, 430f);
        Assert.NotEqual(start, later);
    }
}
