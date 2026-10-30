using PatternPack;
using Systemic.Content;
using Systemic.Host;
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
        DefineWorkerPopulation(materialized.World);
        return (content, materialized, materialized.World, new RuleEngine(content.Rules));
    }

    // The worker fragment addresses a population, so a world that loads the whole pack must define
    // it. No rows means it never fires, which keeps each test focused on its own mechanic.
    private static void DefineWorkerPopulation(World world) =>
        _ = new WorkerPatternSystem(world, workers: 0);

    [Fact]
    public void ThePackHasOneProducerSchedule()
    {
        var content = Pack();

        Assert.Equal("sample.pattern-pack", content.Manifest.Id);
        var schedule = Assert.Single(content.Rules.Schedules);
        Assert.Equal("pattern.produce", schedule.EventType);
        Assert.Equal(60, schedule.EveryTicks);
    }

    [Fact]
    public void TheHostRunsTheProducerScheduleAndPaysOnEveryPulse()
    {
        var content = Pack();
        using var host = HeadlessSystemicHost.Create(content, seed: 11);
        var world = host.Game.World;
        DefineWorkerPopulation(world);
        var drill = world.ById(host.Game.EntityId("pattern.drill"))!;
        var ore = world.ById(host.Game.EntityId("pattern.ore"))!;

        // Ten pulses of sixty ticks each: the schedule publishes the fact, the fragment rules pay it.
        host.RunTicks(600);

        Assert.Equal(10, world.EffectiveProperty(ore, "balance"));
        Assert.Equal(10, world.EffectiveProperty(drill, "produced"));
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
    public void UpgradeRejectsATargetThatIsNotAProducer()
    {
        var (_, materialized, world, engine) = Arrange(seed: 12);
        var shards = world.ById(materialized.EntityIds["pattern.shards"])!;
        var ore = world.ById(materialized.EntityIds["pattern.ore"])!;

        var result = engine.Dispatch(world, new SimulationFact("pattern.buy.upgrade", shards.Id, ore.Id));

        Assert.Equal(0, result.RulesMatched);
        Assert.Equal(10, world.EffectiveProperty(shards, "balance"));
        Assert.False(ore.TryGetProperty(world, "rank", out _));
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
    public void MergeCombinesTwoOfTheSameTierIntoTheNextTier()
    {
        var (_, materialized, world, engine) = Arrange(seed: 4);
        var low = world.ById(materialized.EntityIds["pattern.fruit.low"])!;
        var low2 = world.ById(materialized.EntityIds["pattern.fruit.low2"])!;
        var high2 = world.ById(materialized.EntityIds["pattern.fruit.high2"])!;
        var authored = materialized.EntityIds.Values.ToHashSet();
        var before = world.Entities.Count(entity => entity.HasTag("pattern.fruit"));

        engine.Dispatch(world, new SimulationFact("pattern.merge", low.Id, low2.Id));
        world.PruneRemoved();

        var fruit = world.Entities.Where(entity => entity.HasTag("pattern.fruit")).ToArray();
        Assert.Equal(before - 1, fruit.Length);
        Assert.DoesNotContain(fruit, entity => entity.TryGetProperty(world, "tier", out var tier) && tier == 1);
        Assert.Contains(fruit, entity => entity.TryGetProperty(world, "tier", out var tier) && tier == 2);

        // A tier-two pair climbs one more tier instead of staying put. The spawn is the newest row.
        var spawned = world.Entities.Last(entity =>
            entity.HasTag("pattern.mergeable") && !authored.Contains(entity.Id));
        engine.Dispatch(world, new SimulationFact("pattern.merge", spawned.Id, high2.Id));
        world.PruneRemoved();

        Assert.Equal(before - 2, world.Entities.Count(entity => entity.HasTag("pattern.fruit")));
        Assert.Equal(2, world.Entities.Count(entity =>
            entity.TryGetProperty(world, "tier", out var tier) && tier == 3));
    }

    [Fact]
    public void MergeRejectsTwoFruitsOfDifferentTiers()
    {
        var (_, materialized, world, engine) = Arrange(seed: 14);
        var low = world.ById(materialized.EntityIds["pattern.fruit.low"])!;
        var high2 = world.ById(materialized.EntityIds["pattern.fruit.high2"])!;
        var before = world.Entities.Count(entity => entity.HasTag("pattern.fruit"));

        var result = engine.Dispatch(world, new SimulationFact("pattern.merge", low.Id, high2.Id));

        Assert.Equal(0, result.RulesMatched);
        Assert.Equal(before, world.Entities.Count(entity => entity.HasTag("pattern.fruit")));
        Assert.NotNull(world.ById(low.Id));
        Assert.NotNull(world.ById(high2.Id));
    }

    [Fact]
    public void MergeRejectsTheSameFruitAsBothSubjects()
    {
        var (_, materialized, world, engine) = Arrange(seed: 15);
        var low = world.ById(materialized.EntityIds["pattern.fruit.low"])!;
        var before = world.Entities.Count(entity => entity.HasTag("pattern.fruit"));

        var result = engine.Dispatch(world, new SimulationFact("pattern.merge", low.Id, low.Id));
        world.PruneRemoved();

        // Without the identity guard a single fruit merges with itself into the next tier.
        Assert.Equal(0, result.RulesMatched);
        Assert.Equal(before, world.Entities.Count(entity => entity.HasTag("pattern.fruit")));
        Assert.Equal(1, world.EffectiveProperty(low, "tier"));
    }

    [Fact]
    public void PrestigeGrantsAShareOfTheRunAndClearsTheRunScope()
    {
        var (_, materialized, world, engine) = Arrange(seed: 5);
        var run = world.ById(materialized.EntityIds["pattern.run"])!;
        var meta = world.ById(materialized.EntityIds["pattern.meta"])!;

        engine.Dispatch(world, new SimulationFact("pattern.prestige", run.Id, meta.Id));
        world.PruneRemoved();

        // A tenth of the twenty-five earned, in whole shards: the transfer is proportional, not a
        // magic constant. The unspent remainder leaves with the run scope.
        Assert.Equal(2, world.EffectiveProperty(meta, "shards"));
        Assert.Equal(1, world.EffectiveProperty(meta, "prestiges"));
        Assert.DoesNotContain(world.Entities, entity => entity.HasTag("scope.run"));
        Assert.NotNull(world.ById(meta.Id));
    }

    [Fact]
    public void PrestigeRejectsAWrongScopeOrARunBelowTheMinimum()
    {
        var (_, materialized, world, engine) = Arrange(seed: 16);
        var run = world.ById(materialized.EntityIds["pattern.run"])!;
        var meta = world.ById(materialized.EntityIds["pattern.meta"])!;
        var ore = world.ById(materialized.EntityIds["pattern.ore"])!;

        var wrongScope = engine.Dispatch(world, new SimulationFact("pattern.prestige", ore.Id, meta.Id));
        Assert.Equal(0, wrongScope.RulesMatched);

        // Below ten earned a tenth floors to zero shards, so the rule declines instead of counting
        // a prestige that pays nothing.
        Assert.True(run.TrySetProperty(world, "earned", 9));
        var tooSmall = engine.Dispatch(world, new SimulationFact("pattern.prestige", run.Id, meta.Id));
        Assert.Equal(0, tooSmall.RulesMatched);

        Assert.Equal(0, world.EffectiveProperty(meta, "shards"));
        Assert.Equal(0, world.EffectiveProperty(meta, "prestiges"));
        Assert.NotEmpty(world.Entities.Where(entity => entity.HasTag("scope.run")));
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
    public void ASteppedRunAndACatchUpRunReachTheSameState()
    {
        var content = Pack();
        using var stepped = HeadlessSystemicHost.Create(content, seed: 8);
        using var caughtUp = HeadlessSystemicHost.Create(content, seed: 8);
        DefineWorkerPopulation(stepped.Game.World);
        DefineWorkerPopulation(caughtUp.Game.World);

        // Five hundred updates of twenty milliseconds is exactly ten seconds through the clock.
        for (var update = 0; update < 500; update++)
        {
            stepped.Advance(TimeSpan.FromMilliseconds(20));
        }

        // Offline is the host running the same rules over the elapsed ticks as fast as it can. The
        // pack has no offline fragment and no offline event to drift from the online economy.
        caughtUp.RunTicks(600);

        Assert.Equal(600, stepped.Game.World.LogicalTick);
        Assert.Equal(600, caughtUp.Game.World.LogicalTick);

        var steppedOre = stepped.Game.World.ById(stepped.Game.EntityId("pattern.ore"))!;
        var caughtUpOre = caughtUp.Game.World.ById(caughtUp.Game.EntityId("pattern.ore"))!;
        Assert.Equal(10, stepped.Game.World.EffectiveProperty(steppedOre, "balance"));
        Assert.Equal(10, caughtUp.Game.World.EffectiveProperty(caughtUpOre, "balance"));
        Assert.DoesNotContain(content.Rules.Rules,
            rule => rule.EventType.Contains("offline", StringComparison.Ordinal));
    }
}
