using Systemic.Content;
using Systemic.Kernel;
using Systemic.Rules;
using Xunit;

namespace Systemic.Tests.Patterns;

public sealed class PatternPackTests
{
    private static ContentPack Pack() =>
        ContentPackLoader.Load(Path.Combine(AppContext.BaseDirectory, "samples", "pattern-pack"));

    private static (ContentPack Content, MaterializedContentWorld Materialized, World World, RuleEngine Engine) Arrange(int seed)
    {
        var content = Pack();
        var materialized = content.CreateWorld(seed: seed, valueScale: 1);
        return (content, materialized, materialized.World, new RuleEngine(content.Rules));
    }

    [Fact]
    public void ThePackLoadsWithOneProducerSchedule()
    {
        var content = Pack();

        Assert.Equal("sample.pattern-pack", content.Manifest.Id);
        var schedule = Assert.Single(content.Rules.Schedules);
        Assert.Equal("pattern.produce", schedule.EventType);
        Assert.Equal(60, schedule.EveryTicks);
    }

    [Fact]
    public void ProducerProducesOnItsPulseAndPaysMoreOnceTheRateIsRaised()
    {
        var (_, _, world, engine) = Arrange(seed: 1);
        var drill = world.ById(world.Entities.Single(entity => entity.HasTag("pattern.producer")).Id)!;
        var ore = world.ById(world.Entities.Single(entity => entity.HasTag("pattern.resource")).Id)!;

        engine.Dispatch(world, new SimulationFact("pattern.produce", drill.Id, ore.Id));

        Assert.Equal(1, world.EffectiveProperty(ore, "balance"));
        Assert.Equal(1, world.EffectiveProperty(drill, "produced"));

        // The rate bonus is a separate rule guarded by the rate property, not a second producer.
        world.SetModifier("pattern.test.rate", drill, "rate", ModifierOperation.Add, 1);
        engine.Dispatch(world, new SimulationFact("pattern.produce", drill.Id, ore.Id));

        Assert.Equal(3, world.EffectiveProperty(ore, "balance"));
    }

    [Fact]
    public void UpgradeCostsShardsAndRaisesTheRateByItsNewRank()
    {
        var (_, materialized, world, engine) = Arrange(seed: 2);
        var shards = world.ById(materialized.EntityIds["pattern.shards"])!;
        var drill = world.ById(materialized.EntityIds["pattern.drill"])!;
        var ore = world.ById(materialized.EntityIds["pattern.ore"])!;

        engine.Dispatch(world, new SimulationFact("pattern.buy.upgrade", shards.Id, drill.Id));

        Assert.Equal(5, world.EffectiveProperty(shards, "balance"));
        Assert.Equal(1, world.EffectiveProperty(drill, "rank"));
        Assert.Equal(2, world.EffectiveProperty(drill, "rate"));

        // The raised rate is read by the producer's ranked bonus, so one pulse pays twice.
        engine.Dispatch(world, new SimulationFact("pattern.produce", drill.Id, ore.Id));
        Assert.Equal(2, world.EffectiveProperty(ore, "balance"));

        // Spending the remaining shards is allowed; a third purchase is rejected by the guard.
        engine.Dispatch(world, new SimulationFact("pattern.buy.upgrade", shards.Id, drill.Id));
        Assert.Equal(0, world.EffectiveProperty(shards, "balance"));
        Assert.Equal(2, world.EffectiveProperty(drill, "rank"));

        var rejected = engine.Dispatch(world, new SimulationFact("pattern.buy.upgrade", shards.Id, drill.Id));
        Assert.Equal(0, rejected.RulesMatched);
        Assert.Equal(2, world.EffectiveProperty(drill, "rank"));
    }

    [Fact]
    public void TalentBundleAppliesOnlyWhileItsTagIsSet()
    {
        var (_, materialized, world, engine) = Arrange(seed: 3);
        var drill = world.ById(materialized.EntityIds["pattern.drill"])!;
        var ore = world.ById(materialized.EntityIds["pattern.ore"])!;

        engine.Dispatch(world, new SimulationFact("pattern.produce", drill.Id, ore.Id));
        var withoutTalent = world.EffectiveProperty(ore, "balance");

        drill.AddTag("pattern.talent.echo");
        engine.Dispatch(world, new SimulationFact("pattern.produce", drill.Id, ore.Id));
        var withTalent = world.EffectiveProperty(ore, "balance");

        Assert.Equal(3, withTalent - withoutTalent);

        drill.RemoveTag("pattern.talent.echo");
        engine.Dispatch(world, new SimulationFact("pattern.produce", drill.Id, ore.Id));
        Assert.Equal(withTalent + 1, world.EffectiveProperty(ore, "balance"));
    }

    [Fact]
    public void MergeDespawnsBothInputsAndSpawnsTheNextTier()
    {
        var (_, materialized, world, engine) = Arrange(seed: 4);
        var low = world.ById(materialized.EntityIds["pattern.fruit.low"])!;
        var low2 = world.ById(materialized.EntityIds["pattern.fruit.low2"])!;
        var before = world.Entities.Count(entity => entity.HasTag("pattern.fruit"));

        engine.Dispatch(world, new SimulationFact("pattern.merge", low.Id, low2.Id));
        world.PruneRemoved();

        var fruit = world.Entities.Where(entity => entity.HasTag("pattern.fruit")).ToArray();
        Assert.Equal(before - 1, fruit.Length);
        Assert.DoesNotContain(fruit, entity => entity.HasTag("pattern.mergeable"));
        Assert.Contains(fruit, entity => entity.TryGetProperty(world, "tier", out var tier) && tier == 2);
    }

    [Fact]
    public void PrestigeGrantsShardsThroughTheMetaEntityAndClearsTheRunScope()
    {
        var (_, materialized, world, engine) = Arrange(seed: 5);
        var run = world.ById(materialized.EntityIds["pattern.run"])!;
        var meta = world.ById(materialized.EntityIds["pattern.meta"])!;

        engine.Dispatch(world, new SimulationFact("pattern.prestige", run.Id, meta.Id));
        world.PruneRemoved();

        Assert.DoesNotContain(world.Entities, entity => entity.HasTag("scope.run"));
        var survivor = world.ById(meta.Id);
        Assert.NotNull(survivor);
        Assert.Equal(10, world.EffectiveProperty(survivor, "shards"));
        Assert.Equal(1, world.EffectiveProperty(survivor, "prestiges"));
    }

    [Fact]
    public void StatusAppliesATimedMultiplierAndExpires()
    {
        var (_, materialized, world, engine) = Arrange(seed: 6);
        var drill = world.ById(materialized.EntityIds["pattern.drill"])!;

        engine.Dispatch(world, new SimulationFact("pattern.status.apply", drill.Id, drill.Id));
        Assert.Equal(2, world.EffectiveProperty(drill, "rate"));

        world.LogicalTick += 60;
        world.AdvanceModifiers();

        Assert.Equal(1, world.EffectiveProperty(drill, "rate"));
    }

    [Fact]
    public void UnlockGatesProductionBehindATagItAdds()
    {
        var (_, materialized, world, engine) = Arrange(seed: 7);
        var gate = world.ById(materialized.EntityIds["pattern.unlock-switch"])!;
        var ore = world.ById(materialized.EntityIds["pattern.ore"])!;

        engine.Dispatch(world, new SimulationFact("pattern.produce", gate.Id, ore.Id));
        Assert.Equal(0, world.EffectiveProperty(ore, "balance"));

        engine.Dispatch(world, new SimulationFact("pattern.unlock", gate.Id, gate.Id));
        engine.Dispatch(world, new SimulationFact("pattern.produce", gate.Id, ore.Id));

        Assert.Equal(1, world.EffectiveProperty(ore, "balance"));
    }

    [Fact]
    public void OfflineCatchUpGrantsTheElapsedProductionInOneFact()
    {
        var (_, materialized, world, engine) = Arrange(seed: 8);
        var ore = world.ById(materialized.EntityIds["pattern.ore"])!;

        engine.Dispatch(world, new SimulationFact("pattern.offline.catchup", ore.Id, ore.Id));

        Assert.Equal(60, world.EffectiveProperty(ore, "balance"));
    }
}
