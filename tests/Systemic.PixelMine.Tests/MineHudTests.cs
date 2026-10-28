using PixelMine;
using Xunit;

namespace Systemic.PixelMine.Tests;

public sealed class MineHudTests
{
    [Fact]
    public void UpgradeFocusOpensOnHoverAndRetractsAfterLeaving()
    {
        var focus = new MineHudFocus();

        focus.Update(TimeSpan.FromSeconds(0.125), 3, false);

        Assert.Equal(3, focus.UpgradeIndex);
        Assert.Equal(1f, focus.UpgradeReveal);

        focus.Update(TimeSpan.FromSeconds(1), -1, false);

        Assert.Equal(3, focus.UpgradeIndex);
        Assert.Equal(1f, focus.UpgradeReveal);

        focus.Update(TimeSpan.FromSeconds(1), -1, false);

        Assert.Equal(-1, focus.UpgradeIndex);
        Assert.Equal(0f, focus.UpgradeReveal);
    }

    [Fact]
    public void KeyboardInspectionWinsOverAStationaryPointer()
    {
        var focus = new MineHudFocus();
        focus.Update(TimeSpan.FromSeconds(0.125), 0, false);

        focus.InspectUpgrade(6);
        focus.Update(TimeSpan.FromSeconds(0.125), 0, false);

        Assert.Equal(6, focus.UpgradeIndex);
        Assert.Equal(1f, focus.UpgradeReveal);

        focus.Update(TimeSpan.FromSeconds(0.125), 1, false);

        Assert.Equal(1, focus.UpgradeIndex);
    }

    [Fact]
    public void BallDetailsFollowElapsedTimeWithoutSimulationTicks()
    {
        var focus = new MineHudFocus();

        focus.InspectBall();
        focus.Update(TimeSpan.FromSeconds(0.125), -1, false);

        Assert.Equal(1f, focus.BallReveal);

        focus.Update(TimeSpan.FromSeconds(2), -1, false);

        Assert.Equal(0f, focus.BallReveal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void UpgradeHitTargetsMatchTheDrawnSlots(int index)
    {
        var bounds = MineGame.UpgradeBounds(index);

        Assert.Equal(index, MineGame.UpgradeAt(bounds.Center.X, bounds.Center.Y));
        Assert.Equal(index, MineGame.UpgradeAt(bounds.Left, bounds.Top));
        Assert.Equal(index, MineGame.UpgradeAt(bounds.Right - 1, bounds.Bottom - 1));
        Assert.Equal(-1, MineGame.UpgradeAt(bounds.Right, bounds.Center.Y));
        Assert.Equal(-1, MineGame.UpgradeAt(bounds.Center.X, bounds.Bottom));
    }

    [Fact]
    public void ForgeFlashRequiresALevelGainAndExpiresWithoutSimulationTicks()
    {
        var focus = new MineHudFocus();
        focus.ObserveUpgrade(0, 0);

        Assert.Equal(0f, focus.UpgradeFlash(0));

        focus.ObserveUpgrade(0, 1);

        Assert.Equal(1f, focus.UpgradeFlash(0));
        Assert.Equal(0f, focus.UpgradeFlash(1));

        focus.Update(TimeSpan.FromSeconds(0.35), -1, false);
        focus.ObserveUpgrade(0, 1);

        Assert.Equal(0.5f, focus.UpgradeFlash(0));

        focus.Update(TimeSpan.FromSeconds(1), -1, false);

        Assert.Equal(0f, focus.UpgradeFlash(0));

        focus.ObserveUpgrade(0, 2);

        Assert.Equal(1f, focus.UpgradeFlash(0));
    }
}
