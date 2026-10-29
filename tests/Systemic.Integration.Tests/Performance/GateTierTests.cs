using Systemic.Content;
using Systemic.Host;
using Systemic.Kernel;
using Systemic.Rules;
using Xunit.Abstractions;

namespace Systemic.Tests.Performance;

/// <summary>The content tier's allocation budget: entities and rules, 2,000 actors, at most 4 KiB per tick.</summary>
/// <remarks>
/// The tiers are: kernel (dense populations, 5 ms, zero warmed allocations), content (this file),
/// and crowd (100,000 population rows with rule subjects, which cannot exist before populations can
/// be rule subjects).
/// <para>
/// Only the allocation budget is asserted here. The content tier's frame-time claim is gated by the
/// benchmark runner as <c>--gate --scenario content</c>, which ticks 500 times in a dedicated
/// process and fails on any tick at or over 5 ms. A wall-clock assertion in this suite measured the
/// machine instead of the code: ten test assemblies run in parallel, so a 30-sample p95 was really
/// the second-slowest tick of whatever else the box was doing. The timings stay as diagnostics, as
/// they are in the sibling budget tests.
/// </para>
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

        var timings = new double[TickCount];
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var tick = 0; tick < TickCount; tick++)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            game.Step();
            timings[tick] = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        var perTick = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / TickCount;
        Array.Sort(timings);
        var p95 = timings[(int)Math.Ceiling(timings.Length * 0.95) - 1];
        _output.WriteLine($"Content tier: {ActorCount} actors, {perTick} B/tick, p95 {p95:F3} ms "
            + "(timing is diagnostic here; --gate --scenario content is the gate)");
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
