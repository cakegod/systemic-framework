using Systemic.Kernel;
using Systemic.Rules;

namespace Systemic.Tests.Rules;

public sealed class FactBatchDispatchTests
{
    [Fact]
    public void Batch_commits_every_fact_in_one_transaction()
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
    public void Batch_rolls_back_earlier_facts_when_a_later_fact_aborts()
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
    public void Empty_batch_reports_no_work()
    {
        var world = new World(seed: 1, valueScale: 1);
        var rules = new RuleEngine(new CompiledRuleSet([AddRule()]));

        var result = rules.DispatchBatch(world, []);

        Assert.Equal(0, result.FactsProcessed);
        Assert.False(result.RolledBack);
    }

    [Fact]
    public void A_batch_that_cannot_begin_a_transaction_does_not_poison_the_engine()
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

    private static CompiledRule AddRule() =>
        new("test.add", "add", 0, [], [RuleEffect.AdjustProperty(RuleSubject.Target, "value", 1)]);

    private readonly struct BatchKind : IPopulationKind
    {
        public static string Id => "test.batch-kind";

        public static int Version => 1;
    }
}
