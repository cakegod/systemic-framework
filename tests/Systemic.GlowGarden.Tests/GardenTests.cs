using GlowGarden;

using Xunit;

namespace Systemic.GlowGarden.Tests;


public class GardenTests
{
    [Fact]
    public void SeedAndProductionAreDeterministic()
    {
        using var a = GardenHost.Create(42);
        using var b = GardenHost.Create(42);

        Assert.Equal(6, a.Simulation.Plants.Count);
        Assert.Equal(6, a.Simulation.Creatures.Count);

        a.Step(3600);
        b.Step(3600);

        Assert.True(a.Simulation.BurstCount > 0);
        Assert.True(a.Simulation.BankedGlow > 0);

        Assert.Equal(a.Simulation.Glow, b.Simulation.Glow);

        Assert.Equal(a.Simulation.Plants.ToArray(), b.Simulation.Plants.ToArray());

        Assert.Equal(a.Simulation.Creatures.ToArray(), b.Simulation.Creatures.ToArray());

        Assert.Equal(a.Simulation.Motes.ToArray(), b.Simulation.Motes.ToArray());
    }

    [Fact]
    public void UnaffordablePurchasesAreNoOps()
    {
        using var host = GardenHost.Create(1);
        var s = host.Simulation;

        while (s.Buy(UpgradeKind.PodYield))
        {
        }

        var glow = s.Glow;
        var rank = s.Rank(UpgradeKind.PodYield);
        var spend = s.Spent(UpgradeTree.MagicGrowth);

        Assert.False(s.Buy(UpgradeKind.PodYield));
        Assert.False(s.Hire(CreatureKind.Burstling));

        Assert.Equal(glow, s.Glow);
        Assert.Equal(rank, s.Rank(UpgradeKind.PodYield));
        Assert.Equal(spend, s.Spent(UpgradeTree.MagicGrowth));
    }

    [Fact]
    public void ResetRefundsOnlyItsTreeOnceAndPreservesWorkersAndLoads()
    {
        using var host = GardenHost.Create(2);
        host.Step(3600);
        var s = host.Simulation;

        Assert.True(s.Hire(CreatureKind.Gatherer));
        Assert.True(s.Buy(UpgradeKind.GathererSpeed));
        Assert.True(s.Buy(UpgradeKind.PodGrowth));

        var workers = s.Creatures.ToArray();
        var glow = s.Glow;
        var refund = s.Spent(UpgradeTree.Creatures);

        Assert.Equal(refund, s.ResetTree(UpgradeTree.Creatures));
        Assert.Equal(glow + refund, s.Glow);

        Assert.Equal(workers, s.Creatures.ToArray());
        Assert.Equal(1, s.Rank(UpgradeKind.PodGrowth));

        Assert.Equal(0, s.Rank(UpgradeKind.GathererSpeed));
        Assert.Equal(0, s.ResetTree(UpgradeTree.Creatures));
    }

    [Fact]
    public void BulkStopsAtAffordabilityAndRefundsActualCost()
    {
        using var host = GardenHost.Create(3);
        var s = host.Simulation;
        var initial = s.Glow;

        Assert.True(s.Buy(UpgradeKind.BurstlingWorkRate, true));

        Assert.True(s.Glow < s.Price(UpgradeKind.BurstlingWorkRate));
        Assert.InRange(s.Rank(UpgradeKind.BurstlingWorkRate), 1, 25);

        Assert.Equal(initial - s.Glow, s.Spent(UpgradeTree.Creatures));

        s.ResetTree(UpgradeTree.Creatures);
        Assert.Equal(initial, s.Glow);
    }

    [Fact]
    public void MotionExposesLeapAndStorageStaysBounded()
    {
        using var host = GardenHost.Create(7);
        var observedLeap = false;

        for (var i = 0; i < 3600; i++)
        {
            host.Step(1);
            observedLeap |= host.Simulation.Creatures.Any(c => c.LeapProgress > 0);
        }

        Assert.True(observedLeap);

        Assert.InRange(host.Simulation.Plants.Count, 6, GardenSimulation.MaxPlants);

        Assert.InRange(host.Simulation.Motes.Count, 0, GardenSimulation.MaxMotes);

        Assert.All(host.Simulation.Creatures, c =>
        {
            Assert.InRange(c.Position.X, 0, GardenSimulation.Width); Assert.InRange(c.Position.Y, 0, GardenSimulation.Height);
        }
);
    }
}

