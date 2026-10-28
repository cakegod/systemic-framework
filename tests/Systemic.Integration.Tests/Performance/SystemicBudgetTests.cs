using System.Collections.Immutable;
using System.Diagnostics;
using Systemic.Kernel;
using Systemic.Rules;
using Xunit.Abstractions;

namespace Systemic.Tests.Performance;

public sealed class SystemicBudgetTests(ITestOutputHelper output)
{
    private const int ProductCount = 50_000;
    private const int ActorCount = 1_000;
    // RuleEngine's default per-dispatch effect budget; the engine under test is built with defaults.
    private const int MaxEffectsPerDispatch = 1024;
    private readonly ITestOutputHelper _output = output;

    /// <summary>
    /// Diagnostic for content-scale entities well beyond the approved content tier (2,000 actors).
    /// </summary>
    /// <remarks>
    /// The 5 ms per-tick budget belongs to the kernel tier (dense populations, measured by the
    /// benchmark gate runner); content-scale timing is gated at 2,000 actors in GateTierTests. This
    /// workload keeps its correctness and zero-allocation assertions and reports its timings.
    /// </remarks>
    [Fact]
    [Trait("Category", "Diagnostic")]
    public void Fifty_thousand_products_and_one_thousand_reactive_actors_report_timings()
    {
        var world = new World(seed: 71, valueScale: 1, spatialCellSize: 24);
        var spatialQueries = new List<int>[ActorCount];
        var actors = new DataEntity[ActorCount];
        var targetIds = new int[ActorCount];

        for (var product = 0; product < ProductCount; product++)
        {
            var x = (product % 250) * 24.0f;
            var y = (product / 250) * 24.0f;
            var entity = world.Add(new DataEntity(["material", "brittle"], [],
                ("heat", 0), ("stress", 0), ("durability", 100)));
            world.SetSpatialBounds(entity, new Bounds2(x, y, 4, 4));
        }

        for (var actorIndex = 0; actorIndex < ActorCount; actorIndex++)
        {
            var x = (actorIndex % 50) * 120.0f + 10;
            var y = (actorIndex / 50) * 120.0f + 10;
            actors[actorIndex] = world.Add(new DataEntity(
                ["talent.magma", "talent.chain", "talent.echo", "talent.tempering"], [],
                ("charge", 1)));
            world.SetSpatialBounds(actors[actorIndex], new Bounds2(x, y, 3, 3));
            spatialQueries[actorIndex] = new List<int>(24);
        }

        Assert.Equal(ProductCount + ActorCount, world.SpatialEntityCount);
        var engine = new RuleEngine(CreateReactiveTalentRules());
        var noRuleEngine = new RuleEngine(new CompiledRuleSet([]));
        var oneEffectEngine = new RuleEngine(new CompiledRuleSet([
            new CompiledRule("profile.single-effect", "contact", 0, [],
                [RuleEffect.AdjustProperty(RuleSubject.Target, "stress", 0.01)])]));
        var invalidDispatches = 0;
        var lastDispatchEffects = 0;
        var lastDispatchCutoffs = 0;
        void queryOnly()
        {
            var dispatchedActors = 0;
            for (var actorIndex = 0; actorIndex < ActorCount; actorIndex++)
            {
                var actor = actors[actorIndex];
                var bounds = world.TryGetSpatialBounds(actor.Id, out var current) ? current : default;
                var nearby = spatialQueries[actorIndex];
                world.QuerySpatialNearby(new Point2(bounds.X, bounds.Y), 20, nearby);
                var target = 0;
                for (var index = 0; index < nearby.Count; index++)
                {
                    var candidateId = nearby[index];
                    if (candidateId != actor.Id && world.ById(candidateId)?.HasTag("material") == true)
                    {
                        target = candidateId;
                        break;
                    }
                }

                if (target != 0)
                {
                    targetIds[actorIndex] = target;
                    dispatchedActors++;
                }
            }

            if (dispatchedActors != ActorCount)
            {
                invalidDispatches++;
            }
        }

        void dispatchOnly(RuleEngine dispatcher, bool requireReactionCutoff)
        {
            var effectsApplied = 0;
            var cutoffs = 0;
            for (var actorIndex = 0; actorIndex < ActorCount; actorIndex++)
            {
                var result = dispatcher.Dispatch(world,
                    new SimulationFact("contact", actors[actorIndex].Id, targetIds[actorIndex]));
                effectsApplied += result.EffectsApplied;
                if (result.CutOff)
                {
                    cutoffs++;
                }

                if (requireReactionCutoff
                    && (!result.CutOff || result.CutoffReason != "reaction_depth"
                        || result.EffectsApplied is < 1 or > MaxEffectsPerDispatch))
                {
                    invalidDispatches++;
                }
            }

            lastDispatchEffects = effectsApplied;
            lastDispatchCutoffs = cutoffs;
        }

        void queryAndReact()
        {
            queryOnly();
            dispatchOnly(engine, requireReactionCutoff: true);
        }

        for (var warmup = 0; warmup < 20; warmup++)
        {
            queryAndReact();
        }

        var queryProfile = Measure(queryOnly);
        var dispatchProfile = Measure(() => dispatchOnly(noRuleEngine, requireReactionCutoff: false));
        var oneEffectProfile = Measure(() => dispatchOnly(oneEffectEngine, requireReactionCutoff: false));
        var reactionProfile = Measure(() => dispatchOnly(engine, requireReactionCutoff: true));
        var fullPhases = MeasureCombined(queryOnly, () => dispatchOnly(engine, requireReactionCutoff: true));
        var fullProfile = fullPhases.Total;
        var p95 = fullProfile.P95;
        _output.WriteLine($"Products={ProductCount}, actors={ActorCount}, warmup=20, samples=60");
        _output.WriteLine($"Full query + chain p50/p95/p99={fullProfile.P50:F3}/{fullProfile.P95:F3}/{fullProfile.P99:F3} ms; allocations={fullProfile.Allocations} B");
        _output.WriteLine($"Combined phases query p50/p95={fullPhases.First.P50:F3}/{fullPhases.First.P95:F3} ms; reaction p50/p95={fullPhases.Second.P50:F3}/{fullPhases.Second.P95:F3} ms");
        _output.WriteLine($"Spatial query p50/p95={queryProfile.P50:F3}/{queryProfile.P95:F3} ms; empty dispatch={dispatchProfile.P50:F3}/{dispatchProfile.P95:F3} ms; one-effect dispatch={oneEffectProfile.P50:F3}/{oneEffectProfile.P95:F3} ms; reaction chain={reactionProfile.P50:F3}/{reactionProfile.P95:F3} ms");
        _output.WriteLine($"Last chain sample: effects={lastDispatchEffects}, cutoff facts={lastDispatchCutoffs} (expected {ActorCount} reaction-depth cutoffs)");
        Assert.Equal(0, invalidDispatches);
        Assert.Equal(0, fullProfile.Allocations);
    }

    private static TimingProfile Measure(Action action)
    {
        for (var warmup = 0; warmup < 20; warmup++)
        {
            action();
        }

        var timings = new double[60];
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var sample = 0; sample < timings.Length; sample++)
        {
            var started = Stopwatch.GetTimestamp();
            action();
            timings[sample] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        var allocations = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Array.Sort(timings);
        return new TimingProfile(
            Percentile(timings, 0.50),
            Percentile(timings, 0.95),
            Percentile(timings, 0.99),
            allocations);
    }

    private static CombinedTimingProfile MeasureCombined(Action first, Action second)
    {
        for (var warmup = 0; warmup < 20; warmup++)
        {
            first();
            second();
        }

        var total = new double[60];
        var firstPhase = new double[60];
        var secondPhase = new double[60];
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var sample = 0; sample < total.Length; sample++)
        {
            var started = Stopwatch.GetTimestamp();
            first();
            var middle = Stopwatch.GetTimestamp();
            second();
            var finished = Stopwatch.GetTimestamp();
            firstPhase[sample] = Stopwatch.GetElapsedTime(started, middle).TotalMilliseconds;
            secondPhase[sample] = Stopwatch.GetElapsedTime(middle, finished).TotalMilliseconds;
            total[sample] = Stopwatch.GetElapsedTime(started, finished).TotalMilliseconds;
        }

        var allocations = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        return new CombinedTimingProfile(
            CreateProfile(total, allocations),
            CreateProfile(firstPhase, allocations),
            CreateProfile(secondPhase, allocations));
    }

    private static TimingProfile CreateProfile(double[] timings, long allocations)
    {
        Array.Sort(timings);
        return new TimingProfile(
            Percentile(timings, 0.50),
            Percentile(timings, 0.95),
            Percentile(timings, 0.99),
            allocations);
    }

    private static double Percentile(double[] sorted, double percentile) =>
        sorted[(int)Math.Ceiling(sorted.Length * percentile) - 1];

    private readonly record struct TimingProfile(double P50, double P95, double P99, long Allocations);
    private readonly record struct CombinedTimingProfile(TimingProfile Total, TimingProfile First, TimingProfile Second);

    private static CompiledRuleSet CreateReactiveTalentRules()
    {
        var rules = new List<CompiledRule>
        {
            new("talent.magma-contact", "contact", 10,
                [RuleCondition.HasTag(RuleSubject.Source, "talent.magma"),
                 RuleCondition.HasTag(RuleSubject.Source, "talent.chain"),
                 RuleCondition.HasTag(RuleSubject.Target, "material")],
                [RuleEffect.AdjustProperty(RuleSubject.Target, "heat", 1), RuleEffect.Emit("reaction.chain.0")]),
            new("talent.echo-contact", "contact", 5,
                [RuleCondition.HasTag(RuleSubject.Source, "talent.echo"),
                 RuleCondition.HasTag(RuleSubject.Target, "brittle")],
                [RuleEffect.AdjustProperty(RuleSubject.Target, "stress", 1), RuleEffect.Emit("reaction.chain.0")]),
            new("talent.tempering-contact", "contact", 0,
                [RuleCondition.HasTag(RuleSubject.Source, "talent.tempering"),
                 RuleCondition.HasTag(RuleSubject.Target, "brittle")],
                [RuleEffect.AdjustProperty(RuleSubject.Target, "durability", -0.25)])
        };

        for (var reaction = 0; reaction < 8; reaction++)
        {
            rules.Add(new CompiledRule(
                $"reaction.chain.{reaction}",
                $"reaction.chain.{reaction}",
                0,
                [RuleCondition.PropertyAtLeast(RuleSubject.Target, "heat", 0)],
                [RuleEffect.AdjustProperty(RuleSubject.Target, "stress", 0.25),
                 RuleEffect.Emit($"reaction.chain.{reaction + 1}")]));
        }

        return new CompiledRuleSet([.. rules]);
    }
}
