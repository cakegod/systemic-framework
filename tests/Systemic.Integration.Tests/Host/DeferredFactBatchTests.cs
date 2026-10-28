using Systemic.Content;
using Systemic.Host;
using Systemic.Kernel;
using Systemic.Rules;

namespace Systemic.Tests.Host;

public sealed class DeferredFactBatchTests
{
    [Fact]
    public void A_tick_of_deferred_facts_commits_as_one_transaction()
    {
        var world = new World(seed: 3, valueScale: 1);
        var target = world.Add(new DataEntity([], [], ("value", 0.0)));
        using var game = new SystemicGameBuilder(Content(), world)
            .AddSystem(new DeferSystem("first", 10, "add", target.Id))
            .AddSystem(new DeferSystem("second", 20, "add", target.Id))
            .Build();

        game.Step();

        Assert.True(target.TryGetProperty(world, "value", out var value));
        Assert.Equal(2, value);
    }

    [Fact]
    public void A_tick_rolls_back_every_deferred_fact_when_a_later_fact_aborts()
    {
        var world = new World(seed: 3, valueScale: 1);
        var target = world.Add(new DataEntity([], [], ("value", 0.0)));
        using var game = new SystemicGameBuilder(Content(), world)
            .AddSystem(new DeferSystem("first", 10, "add", target.Id))
            .AddSystem(new DeferSystem("second", 20, "poison", target.Id))
            .AddFactObserver(0, (fact, emitter) =>
            {
                if (fact.Type == "poison")
                {
                    Assert.False(emitter.Enqueue(new SimulationFact("invalid", -1, 0)));
                }
            })
            .Build();

        game.Step();

        Assert.True(target.TryGetProperty(world, "value", out var value));
        // One transaction per tick means the aborted fact also undoes the committed-looking first fact.
        Assert.Equal(0, value);
    }

    private static ContentPack Content() => new("", new GameManifest("test", "Test", 30, 4),
        new CompiledRuleSet([
            new CompiledRule("test.add", "add", 0, [],
                [RuleEffect.AdjustProperty(RuleSubject.Target, "value", 1)]),
            new CompiledRule("test.poison", "poison", 0, [],
                [RuleEffect.AdjustProperty(RuleSubject.Target, "value", 1)])]),
        new CompiledEntities([]));

    private sealed class DeferSystem(string id, int order, string factType, int targetId) : ISystemicSystem
    {
        public string Id => id;

        public int Order => order;

        public IReadOnlyList<SystemAccess> Access => [SystemAccess.DeferWorldWrites];

        public void Step(SystemicSystemContext context) =>
            context.DeferFact(new SimulationFact(factType, 0, targetId));
    }
}
