using Systemic.Content;
using Systemic.Host;
using Systemic.Kernel;
using Systemic.Rules;

namespace Systemic.Tests.Host;

public sealed class DeferredFactTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void DeferredFactsFlushInSystemOrder(int workers)
    {
        var seen = new List<string>();
        using var game = new SystemicGameBuilder(EmptyContent(), new World(seed: 3, valueScale: 1))
            .AddSystem(new DeferSystem("second", 20, "test.second"))
            .AddSystem(new DeferSystem("first", 10, "test.first"))
            .AddFactObserver(0, (fact, _) => seen.Add(fact.Type))
            .WithScheduledWorkerCount(workers)
            .Build();

        game.Step();

        Assert.Equal(["test.first", "test.second"], seen);
    }

    [Fact]
    public void DeferringFactsWithoutDeferredOrExclusiveAccessThrows()
    {
        using var game = new SystemicGameBuilder(EmptyContent(), new World(seed: 3, valueScale: 1))
            .AddSystem(new RestrictedDeferSystem())
            .Build();

        Assert.Throws<InvalidOperationException>(game.Step);
    }

    private static ContentPack EmptyContent() => new("", new GameManifest("test", "Test", 30, 4),
        new CompiledRuleSet([]), new CompiledEntities([]));

    private sealed class DeferSystem(string id, int order, string factType) : ISystemicSystem
    {
        public string Id => id;

        public int Order => order;

        public IReadOnlyList<SystemAccess> Access => [SystemAccess.DeferWorldWrites];

        public void Step(SystemicSystemContext context) =>
            context.DeferFact(new SimulationFact(factType, 0, 0));
    }

    private sealed class RestrictedDeferSystem : ISystemicSystem
    {
        public string Id => "restricted";

        public IReadOnlyList<SystemAccess> Access => ResolveAccess();

        public void Step(SystemicSystemContext context) =>
            context.DeferFact(new SimulationFact("test.blocked", 0, 0));

        private static IReadOnlyList<SystemAccess> ResolveAccess() => [SystemAccess.ReadWorld];
    }
}
