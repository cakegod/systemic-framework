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
        });
    }
    private static List<T> State<T>(GardenSimulation s, string field) =>
        (List<T>)typeof(GardenSimulation).GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(s)!;

    private static void Arrange(GardenSimulation s)
    {
        var plants = State<PlantState>(s, "_plants");
        plants.Clear();
        for (var i = 0; i < 20; i++) plants.Add(new(i, GardenSimulation.Colony + new System.Numerics.Vector2(i * 8, 0), 1));
        var workers = State<CreatureState>(s, "_creatures");
        workers.Clear();
        workers.Add(new(0, CreatureKind.Burstling, GardenSimulation.Colony, 0, 0, false));
    }

    [Fact]
    public void TalentsHaveThreeIndependentSlots()
    {
        var s = new GardenSimulation(1);
        Assert.True(s.ToggleTalent(Talent.Echo));
        Assert.True(s.ToggleTalent(Talent.Clone));
        Assert.True(s.ToggleTalent(Talent.Seedstorm));
        Assert.False(s.ToggleTalent(Talent.Magnetism));
        Assert.True(s.ToggleTalent(Talent.Clone));
        Assert.True(s.ToggleTalent(Talent.Magnetism));
        Assert.Equal(3, s.ActiveTalentCount);
    }

    [Theory]
    [InlineData(Talent.Echo)]
    [InlineData(Talent.Seedstorm)]
    [InlineData(Talent.ChainReaction)]
    [InlineData(Talent.LivingProjectile)]
    public void TalentsProduceActualEffects(Talent talent)
    {
        using var host = GardenHost.Create(10);
        var s = host.Simulation;
        Arrange(s);
        s.ToggleTalent(talent);
        host.Step(1);
        if (talent is Talent.Echo or Talent.Seedstorm) Assert.Equal(0, s.EffectCount(talent));
        if (talent == Talent.ChainReaction) Assert.InRange(s.BurstCount, 2, GardenSimulation.MaxReactionBursts);
        host.Step(20);
        Assert.True(s.EffectCount(talent) > 0);
        if (talent == Talent.Seedstorm) Assert.True(s.Plants.Count > 20);
        else Assert.True(s.BurstCount > 1);
    }

    [Fact]
    public void CopiesExpireAfterCombosAndPendingEchoesExpireWithoutRipeTargets()
    {
        using var host = GardenHost.Create(11);
        var s = host.Simulation;
        Arrange(s);
        s.ToggleTalent(Talent.Clone);
        s.ToggleTalent(Talent.ChainReaction);
        s.ToggleTalent(Talent.Echo);
        host.Step(1);
        Assert.True(s.CopyCount > 0);
        s.ToggleTalent(Talent.Clone);
        State<PlantState>(s, "_plants").Clear();
        host.Step(400);
        Assert.Equal(0, s.CopyCount);
        Assert.Equal(0, s.PendingImpactCount);
    }

    [Fact]
    public void MagnetismMovesMotesAndChangesCollection()
    {
        using var host = GardenHost.Create(12);
        var s = host.Simulation;
        State<PlantState>(s, "_plants").Clear();
        var workers = State<CreatureState>(s, "_creatures");
        workers.Clear();
        workers.Add(new(0, CreatureKind.Gatherer, GardenSimulation.Colony, 0, 0, false));
        State<MoteState>(s, "_motes").Add(new(0, GardenSimulation.Colony + new System.Numerics.Vector2(12, 0), default, 1));
        s.ToggleTalent(Talent.Magnetism);
        host.Step(1);
        Assert.True(s.EffectCount(Talent.Magnetism) > 0);
        host.Step(1);
        Assert.Equal(1, s.Creatures[0].CarriedGlow);
    }

    [Theory]
    [InlineData(Talent.Echo, Talent.ChainReaction, Talent.Clone)]
    [InlineData(Talent.Seedstorm, Talent.ChainReaction, Talent.Clone)]
    [InlineData(Talent.Echo, Talent.Seedstorm, Talent.LivingProjectile)]
    public void CombinedTalentsStayBoundedAndDeterministic(Talent a, Talent b, Talent c)
    {
        using var first = GardenHost.Create(15);
        using var second = GardenHost.Create(15);
        foreach (var host in new[] { first, second })
        {
            Arrange(host.Simulation);
            foreach (var talent in new[] { a, b, c }) host.Simulation.ToggleTalent(talent);
            host.Step(5000);
            Assert.InRange(host.Simulation.CopyCount, 0, GardenSimulation.MaxCopies);
            Assert.InRange(host.Simulation.PendingImpactCount, 0, GardenSimulation.MaxPendingImpacts);
            Assert.InRange(host.Simulation.Plants.Count, 20, GardenSimulation.MaxPlants);
            Assert.InRange(host.Simulation.Motes.Count, 0, GardenSimulation.MaxMotes);
        }
        Assert.Equal(first.Simulation.Plants.ToArray(), second.Simulation.Plants.ToArray());
        Assert.Equal(first.Simulation.Creatures.ToArray(), second.Simulation.Creatures.ToArray());
        Assert.Equal(first.Simulation.Motes.ToArray(), second.Simulation.Motes.ToArray());
        foreach (var talent in Enum.GetValues<Talent>()) Assert.Equal(first.Simulation.EffectCount(talent), second.Simulation.EffectCount(talent));
    }
    [Fact]
    public void ProjectileRelocatesWorkerAndClonesOnlyAfterActualBounce()
    {
        using var host = GardenHost.Create(20);
        var s = host.Simulation;
        Arrange(s);
        s.ToggleTalent(Talent.LivingProjectile);
        s.ToggleTalent(Talent.Clone);
        s.ToggleTalent(Talent.Seedstorm);
        host.Step(1);
        Assert.Equal(1, s.EffectCount(Talent.LivingProjectile));
        Assert.Equal(1, s.CopyCount);
        Assert.NotEqual(GardenSimulation.Colony, s.Creatures[0].Position);
        host.Step(20);
        Assert.True(s.Plants.Count > 20);
        Assert.Contains(s.Plants, p => p.Id >= 20 && p.Growth > 0);
    }

    [Fact]
    public void UnripeTargetsCannotTriggerCombos()
    {
        using var host = GardenHost.Create(21);
        var s = host.Simulation;
        Arrange(s);
        var plants = State<PlantState>(s, "_plants");
        for (var i = 0; i < plants.Count; i++) plants[i] = plants[i] with { Growth = 0 };
        s.ToggleTalent(Talent.ChainReaction);
        s.ToggleTalent(Talent.Clone);
        s.ToggleTalent(Talent.LivingProjectile);
        host.Step(1);
        Assert.Equal(0, s.BurstCount);
        Assert.Equal(0, s.CopyCount);
        Assert.All(Enum.GetValues<Talent>(), t => Assert.Equal(0, s.EffectCount(t)));
    }
}

