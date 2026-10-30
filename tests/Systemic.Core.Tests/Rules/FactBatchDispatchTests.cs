using System.Collections.Immutable;
using Systemic.Kernel;
using Systemic.Rules;

namespace Systemic.Tests.Rules;

public sealed class FactBatchDispatchTests
{
    [Fact]
    public void BatchCommitsEveryFactInOneTransaction()
    {
        var world = new World(seed: 1, valueScale: 1);
        var target = world.Add(new DataEntity([], [], ("value", 0.0)));
        var rules = new RuleEngine(new CompiledRuleSet([AddRule()]));

        var result = rules.DispatchBatch(world,
            [new SimulationFact("add", 0, target.Id), new SimulationFact("add", 0, target.Id)]);

        Assert.False(result.RolledBack);
        Assert.False(result.CutOff);
        Assert.True(target.TryGetProperty(world, "value", out var value));
        Assert.Equal(2, value);
    }

    [Fact]
    public void BatchRollsBackEarlierFactsWhenALaterFactAborts()
    {
        var world = new World(seed: 1, valueScale: 1);
        var target = world.Add(new DataEntity([], [], ("value", 0.0)));
        var rules = new RuleEngine(new CompiledRuleSet([AddRule()]));

        var result = rules.DispatchBatch(world,
            [new SimulationFact("add", 0, target.Id), new SimulationFact("poison", 0, target.Id)],
            (fact, emitter) =>
            {
                if (fact.Type == "poison")
                {
                    Assert.False(emitter.Enqueue(new SimulationFact("invalid", -1, 0)));
                }
            });

        Assert.True(result.RolledBack);
        Assert.Equal("invalid_fact", result.CutoffReason);
        Assert.True(target.TryGetProperty(world, "value", out var value));
        // A per-fact transaction would have committed the first fact, leaving value at 1.
        Assert.Equal(0, value);
    }

    [Fact]
    public void EmptyBatchReportsNoWork()
    {
        var world = new World(seed: 1, valueScale: 1);
        var rules = new RuleEngine(new CompiledRuleSet([AddRule()]));

        var result = rules.DispatchBatch(world, []);

        Assert.Equal(0, result.FactsProcessed);
        Assert.False(result.RolledBack);
    }

    [Fact]
    public void ABatchThatCannotBeginATransactionDoesNotPoisonTheEngine()
    {
        var world = new World(seed: 2, valueScale: 1);
        var target = world.Add(new DataEntity([], [], ("value", 0.0)));
        var rules = new RuleEngine(new CompiledRuleSet([AddRule()]));
        var population = world.DefinePopulation<BatchKind>(enableHandles: true);
        SimulationFact[] facts = [new("add", 0, target.Id), new("add", 0, target.Id)];

        var lease = population.Borrow();
        try
        {
            // An active population lease rejects the batch transaction before the scan starts.
            Assert.Throws<InvalidOperationException>(() => rules.DispatchBatch(world, facts));
        }
        finally
        {
            lease.Dispose();
        }

        var result = rules.DispatchBatch(world, facts);

        Assert.False(result.RolledBack);
        Assert.True(target.TryGetProperty(world, "value", out var value));
        Assert.Equal(2, value);
    }

    [Fact]
    public void AThrowingActionRestoresTheScheduledQueueAndCooldowns()
    {
        var world = new World(seed: 3, valueScale: 1);
        var target = world.Add(new DataEntity([], [], ("value", 0.0)));
        var engine = new RuleEngine(new CompiledRuleSet([
            new CompiledRule("slow", "slow", 0, [],
                [RuleEffect.AdjustProperty(RuleSubject.Target, "value", 1)])
            {
                CooldownTicks = 10,
            },
            new CompiledRule("wait", "wait", 0, [], [RuleEffect.Schedule("later", 5)]),
            new CompiledRule("fail", "fail", 0, [],
                [RuleEffect.InvokeAction("explode", RuleSubject.Target)])
        ])
        {
            Actions = new Dictionary<string, RuleActionHandler>
            {
                ["explode"] = (in RuleActionContext context) =>
                    throw new InvalidOperationException("action failed"),
            }.ToImmutableDictionary()
        });

        Assert.Throws<InvalidOperationException>(() => engine.DispatchBatch(world, [
            new SimulationFact("slow", 0, target.Id),
            new SimulationFact("wait", 0, target.Id),
            new SimulationFact("fail", 0, target.Id),
        ]));

        // The exception path undoes both engine tables, so the cooldown and the delayed fact are gone.
        var retry = engine.Dispatch(world, new SimulationFact("slow", 0, target.Id));
        Assert.Equal(1, retry.RulesMatched);
        world.LogicalTick = 5;
        Assert.Equal(0, engine.DispatchDue(world));
    }

    private static CompiledRule AddRule() =>
        new("test.add", "add", 0, [], [RuleEffect.AdjustProperty(RuleSubject.Target, "value", 1)]);

    private readonly struct BatchKind : IPopulationKind
    {
        public static string Id => "test.batch-kind";

        public static int Version => 1;
    }
}
