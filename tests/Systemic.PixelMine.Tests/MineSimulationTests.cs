using PixelMine;
using Xunit;

namespace Systemic.PixelMine.Tests;

public sealed class MineSimulationTests
{
    [Fact]
    public void BallMaskHasRoundEdges()
    {
        Assert.True(PixelMineRenderer.IsBallPixel(8, 8));
        Assert.True(PixelMineRenderer.IsBallPixel(7, 0));
        Assert.False(PixelMineRenderer.IsBallPixel(0, 0));
        Assert.False(PixelMineRenderer.IsBallPixel(15, 15));
    }

    [Fact]
    public void MineHoldsExactlyTenMillionPixels()
    {
        Assert.Equal(10_000_000, PixelMineWorld.CellCount);
        var world = new PixelMineWorld(1);
        Assert.Equal(PixelMineWorld.Width / 2, world.SpawnX);
        Assert.Equal(PixelMineWorld.Height / 2, world.SpawnY);
        Assert.Equal(Material.Air, world.MaterialAt(world.SpawnX, world.SpawnY));
        Assert.NotEqual(Material.Air, world.MaterialAt(0, 0));
        Assert.NotEqual(Material.Air, world.MaterialAt(world.SpawnX + 64, world.SpawnY));
        Assert.Equal(Material.Gold, world.MaterialAt(world.GoldX, world.GoldY));
    }

    [Fact]
    public void RepeatableUpgradeCapsScaleFourfold()
    {
        using var host = PixelMineHost.Create(9);
        var upgrades = host.Simulation.Upgrades;

        Assert.Equal(20, upgrades[(int)UpgradeKind.Power].MaxLevel);
        Assert.Equal(20, upgrades[(int)UpgradeKind.Speed].MaxLevel);
        Assert.Equal(16, upgrades[(int)UpgradeKind.Capacity].MaxLevel);
        Assert.Equal(12, upgrades[(int)UpgradeKind.AutoFire].MaxLevel);
        Assert.Equal(1, upgrades[(int)UpgradeKind.Rod].MaxLevel);
        Assert.Equal(1, upgrades[(int)UpgradeKind.Smash].MaxLevel);
        Assert.Equal(1, upgrades[(int)UpgradeKind.Directional].MaxLevel);
        Assert.Equal(1080, PixelMineSimulation.MaximumBalls);
    }

    [Fact]
    public void ShotSpendsDustAndAddsBall()
    {
        using var host = PixelMineHost.Create(3);
        var simulation = host.Simulation;
        var dust = simulation.Dust;
        var balls = simulation.BallCount;

        simulation.RequestShot(simulation.World.SpawnX + 120, simulation.World.SpawnY, BallKind.Basic);
        host.Step(1);

        Assert.Equal(dust - PixelMineSimulation.ShotCost(BallKind.Basic), simulation.Dust);
        Assert.Equal(balls + 1, simulation.BallCount);
    }

    [Fact]
    public void BallsRemainAliveBeyondTheirFormerLifetime()
    {
        using var host = PixelMineHost.Create(7);
        host.Step(301);

        Assert.Equal(PixelMineSimulation.StartingBalls, host.Simulation.BallCount);
    }

    [Fact]
    public void StartingBallsSpawnAtMapCenter()
    {
        using var host = PixelMineHost.Create(8);
        var simulation = host.Simulation;

        for (var ball = 0; ball < PixelMineSimulation.StartingBalls; ball++)
        {
            Assert.Equal(simulation.World.SpawnX, simulation.BallX[ball] >> 16);
            Assert.Equal(simulation.World.SpawnY, simulation.BallY[ball] >> 16);
        }
    }

    [Fact]
    public void BallSnapshotsTrackMovementBetweenTicks()
    {
        using var host = PixelMineHost.Create(17);
        var simulation = host.Simulation;
        var initialX = simulation.BallX[0];
        var initialY = simulation.BallY[0];

        host.Step(1);

        Assert.True(initialX != simulation.BallX[0] || initialY != simulation.BallY[0]);
    }

    [Fact]
    public void SameSeedRepeatsTheRunExactly()
    {
        var first = Run(11, 900);
        var second = Run(11, 900);
        var other = Run(12, 900);

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
    }

    [Fact]
    public void AutoplayFindsGoldenPixelAndAuthoredRuleAwardsWin()
    {
        using var host = PixelMineHost.Create(42);
        var simulation = host.Simulation;
        for (var tick = 0; tick < 12_000 && !simulation.Won; tick++)
        {
            Program.AutoplayTick(simulation, tick);
            host.Step(1);
        }

        Assert.True(simulation.Won, "The rod and drill should reach the golden pixel during the autoplay run.");
        Assert.True(simulation.MinedPixels > 100_000);

        var mine = host.Game.World.ById(host.Game.EntityId("pixel.mine"));
        Assert.NotNull(mine);
        Assert.True(host.Game.World.TryGetEffectiveProperty(mine, "wins", out var wins));
        Assert.Equal(1, wins);
    }

    [Fact]
    public void UpgradeSpendsDustAndRaisesLevel()
    {
        using var host = PixelMineHost.Create(5);
        var simulation = host.Simulation;
        var cost = simulation.Upgrades[(int)UpgradeKind.Power].Cost;
        for (var tick = 0; tick < 600 && simulation.Dust < cost; tick++)
        {
            host.Step(1);
        }

        var level = simulation.UpgradeLevel(UpgradeKind.Power);
        var maxLevel = simulation.Upgrades[(int)UpgradeKind.Power].MaxLevel;
        var dust = simulation.Dust;
        Assert.True(level < maxLevel);
        Assert.True(dust >= cost, "Starting balls should mine enough dust for the first power level.");

        simulation.RequestUpgrade(UpgradeKind.Power);
        host.Step(1);

        Assert.Equal(level + 1, simulation.UpgradeLevel(UpgradeKind.Power));
        Assert.True(simulation.Dust < dust);
    }

    [Fact]
    public void CaveVisibilityIncludesTheWallAndTwoInnerCells()
    {
        const int width = 9;
        const int height = 9;
        var materials = Enumerable.Repeat((byte)Material.Stone, width * height).ToArray();
        materials[(4 * width) + 4] = (byte)Material.Air;
        var depths = new byte[materials.Length];

        PixelMineVisibility.BuildDepth(materials, depths, width, height);

        Assert.Equal(0, depths[(4 * width) + 4]);
        Assert.Equal(1, depths[(4 * width) + 3]);
        Assert.Equal(2, depths[(4 * width) + 2]);
        Assert.Equal(3, depths[(4 * width) + 1]);
        Assert.Equal(4, depths[(4 * width)]);
    }

    [Fact]
    public void StressRunMovesTwentyThousandBallsAtSixteenTimesSpeed()
    {
        using var host = PixelMineHost.CreateStressTest(17, 20_000, 16);
        var simulation = host.Simulation;

        Assert.Equal(20_000, simulation.BallCount);
        host.Step(1);

        Assert.Equal(20_000, simulation.BallCount);
        Assert.True(simulation.MinedPixels > 0);
        Assert.InRange(simulation.HitEvents.Length, 1, 24);
    }

    [Fact]
    public void StressOptionsDefaultToTenMeasuredTicksAndRejectSmallLoads()
    {
        var options = MineOptions.Parse(["--headless", "--stress-balls", "20000"]);

        Assert.Equal(10, options.Ticks);
        Assert.Equal(16, options.StressSpeed);
        Assert.Throws<ArgumentException>(() => MineOptions.Parse(["--headless", "--stress-balls", "1000"]));
    }

    private static (long Mined, long Dust, int Prisms, int Balls, int Shots, bool Won) Run(int seed, int ticks)
    {
        using var host = PixelMineHost.Create(seed);
        var simulation = host.Simulation;
        for (var tick = 0; tick < ticks; tick++)
        {
            Program.AutoplayTick(simulation, tick);
            host.Step(1);
        }

        return (simulation.MinedPixels, simulation.Dust, simulation.Prisms, simulation.BallCount, simulation.BallsFired, simulation.Won);
    }
}
