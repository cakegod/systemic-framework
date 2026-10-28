using Systemic.Content;
using Systemic.Host;
using Systemic.Kernel;
using Systemic.Rules;
using Xunit.Abstractions;

namespace Systemic.Tests.Performance;

/// <summary>The content tier of the approved three-tier performance contract.</summary>
/// <remarks>
/// The tiers are: kernel (dense populations, 5 ms p95, zero warmed allocations), content (entities
/// and rules, 2,000 actors, at most 4 KiB per tick), and crowd (100,000 population rows with rule
/// subjects, which cannot exist before populations can be rule subjects). This file owns the
/// content tier; the kernel tier stays with the benchmark gate runner.
/// </remarks>
public sealed class GateTierTests(ITestOutputHelper output)
{
    private const int ActorCount = 2_000;
    private const int TickCount = 30;
    private const long MaximumBytesPerTick = 4 * 1024;

    private readonly ITestOutputHelper _output = output;

    [Fact]
    [Trait("Category", "Performance")]
    public void Content_tier_stays_within_its_allocation_budget()
    {
        var world = new World(seed: 17, valueScale: 1);
        var target = world.Add(new DataEntity([], [], ("value", 0.0)));
        using var game = new SystemicGameBuilder(Content(), world)
            .AddSystem(new EmitSystem(target.Id))
            .Build();

        for (var tick = 0; tick < TickCount; tick++)
        {
            game.Step();
        }

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var tick = 0; tick < TickCount; tick++)
        {
            game.Step();
        }

        var perTick = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / TickCount;
        _output.WriteLine($"Content tier: {ActorCount} actors, {perTick} B/tick");
        Assert.True(perTick <= MaximumBytesPerTick,
            $"Content tier allocated {perTick} B per tick for {ActorCount} actors; budget is {MaximumBytesPerTick} B.");
    }

    private static ContentPack Content() => new("", new GameManifest("test", "Test", 30, 4),
        new CompiledRuleSet([
            new CompiledRule("test.adjust", "adjust", 0, [],
                [RuleEffect.AdjustProperty(RuleSubject.Target, "value", 1)])]),
        new CompiledEntities([]));

    private sealed class EmitSystem(int targetId) : ISystemicSystem
    {
        public string Id => "test.emit";

        public IReadOnlyList<SystemAccess> Access => [SystemAccess.DeferWorldWrites];

        public void Step(SystemicSystemContext context)
        {
            for (var actor = 0; actor < ActorCount; actor++)
            {
                context.DeferFact(new SimulationFact("adjust", 0, targetId));
            }
        }
    }
}
