using Systemic.Kernel;
using Systemic.Host;
using Systemic.Rules;
using Systemic.Extensions;
using System.Globalization;
using Xunit.Abstractions;

namespace Systemic.Tests;

public sealed class RuleEngineTests
{
    [Fact]
    public void Resolves_bounded_chains_in_stable_order_and_reports_cutoffs()
    {
        var world = new World(seed: 1, valueScale: 1);
        var actor = world.Add(new DataEntity([], [], ("health", 0)));
        var rules = new CompiledRuleSet([
            new CompiledRule("second", "hit", 0, [], [RuleEffect.AdjustProperty(RuleSubject.Target, "health", 1)]),
            new CompiledRule("first", "hit", 0, [], [
                RuleEffect.AdjustProperty(RuleSubject.Target, "health", 1),
                RuleEffect.Emit("again")]),
            new CompiledRule("loop", "again", 0, [], [RuleEffect.Emit("again")])
        ]);
        var engine = new RuleEngine(rules, maxEffects: 3, maxDepth: 8);

        var result = engine.Dispatch(world, new SimulationFact("hit", actor.Id, actor.Id));

        Assert.Equal(3, result.EffectsApplied);
        Assert.True(result.CutOff);
        Assert.Equal("effect_budget", result.CutoffReason);
        Assert.True(actor.TryGetProperty(world, "health", out var health));
        Assert.Equal(2, health);
    }

    [Fact]
    public void Numeric_overflow_stops_a_reaction_without_writing_infinity()
    {
        var world = new World(seed: 2, valueScale: 1);
        var entity = world.Add(new DataEntity([], [], ("heat", double.MaxValue)));
        var rules = new CompiledRuleSet([
            new CompiledRule("overflow", "heat", 0, [],
                [RuleEffect.AdjustProperty(RuleSubject.Target, "heat", double.MaxValue)])
        ]);

        var result = new RuleEngine(rules).Dispatch(world, new SimulationFact("heat", entity.Id, entity.Id));

        Assert.True(result.CutOff);
        Assert.Equal("non_finite_property", result.CutoffReason);
        Assert.True(result.RolledBack);
        Assert.True(entity.TryGetProperty(world, "heat", out var heat));
        Assert.Equal(double.MaxValue, heat);
    }

    [Fact]
    public void Repeated_reaction_property_writes_still_rollback_to_the_dispatch_start_value()
    {
        var world = new World(seed: 23, valueScale: 1);
        var entity = world.Add(new DataEntity([], [], ("score", 0), ("heat", double.MaxValue)));
        var rules = new CompiledRuleSet([
            new CompiledRule("start", "start", 0, [], [
                RuleEffect.AdjustProperty(RuleSubject.Target, "score", 1),
                RuleEffect.Emit("chain")]),
            new CompiledRule("chain", "chain", 0, [], [
                RuleEffect.AdjustProperty(RuleSubject.Target, "score", 2),
                RuleEffect.AdjustProperty(RuleSubject.Target, "score", 3),
                RuleEffect.AdjustProperty(RuleSubject.Target, "heat", double.MaxValue)])
        ]);

        var result = new RuleEngine(rules).Dispatch(world, new SimulationFact("start", entity.Id, entity.Id));

        Assert.True(result.RolledBack);
        Assert.Equal("non_finite_property", result.CutoffReason);
        Assert.Equal(0, world.EffectiveProperty(entity, "score"));
        Assert.Equal(double.MaxValue, world.EffectiveProperty(entity, "heat"));
    }

    [Fact]
    public void Modifier_effect_overflow_rolls_back_the_rule_dispatch()
    {
        var world = new World(seed: 35, valueScale: 1);
        var entity = world.Add(new DataEntity([], [], ("power", 2)));
        var engine = new RuleEngine(new CompiledRuleSet([
            new CompiledRule("overflow.modifier", "overflow.modifier", 0, [],
                [RuleEffect.SetModifier(RuleSubject.Target, "power", ModifierOperation.Multiply, double.MaxValue)])
        ]));

        var result = engine.Dispatch(world, new SimulationFact("overflow.modifier", entity.Id, entity.Id));

        Assert.True(result.RolledBack);
        Assert.Equal("non_finite_property", result.CutoffReason);
        Assert.Empty(world.Modifiers);
        Assert.Equal(2, world.EffectiveProperty(entity, "power"));
    }

    [Fact]
    public void Emitted_facts_preserve_the_original_source_and_target()
    {
        var world = new World(seed: 3, valueScale: 1);
        var source = world.Add(new DataEntity(["spark"], []));
        var target = world.Add(new DataEntity([], [], ("heat", 0)));
        var rules = new CompiledRuleSet([
            new CompiledRule("start", "start", 0, [], [RuleEffect.Emit("chain")]),
            new CompiledRule("chain", "chain", 0,
                [RuleCondition.HasTag(RuleSubject.Source, "spark")],
                [RuleEffect.AdjustProperty(RuleSubject.Target, "heat", 1)])
        ]);

        var result = new RuleEngine(rules).Dispatch(world, new SimulationFact("start", source.Id, target.Id));

        Assert.Equal(2, result.FactsProcessed);
        Assert.False(result.CutOff);
        Assert.True(target.TryGetProperty(world, "heat", out var heat));
        Assert.Equal(1, heat);
    }

    [Fact]
    public void Expression_conditions_read_committed_event_counts_and_rates()
    {
        var world = new World(seed: 30, valueScale: 1) { LogicalTick = 10 };
        var entity = world.Add(new DataEntity([], [], ("score", 0)));
        var rules = new CompiledRuleSet([
            new CompiledRule("delivery.summary", "summary", 0,
                [RuleCondition.FromExpression(ExpressionCompiler.Compile(
                    "event_count('delivery', 10) >= 2 && rate('delivery', 10) >= 3"))],
                [RuleEffect.AdjustProperty(RuleSubject.Target, "score", 1)])
        ]);
        var engine = new RuleEngine(rules, tickRateHz: 10);

        engine.Dispatch(world, new SimulationFact("delivery", entity.Id, entity.Id, Amount: 1));
        engine.Dispatch(world, new SimulationFact("delivery", entity.Id, entity.Id, Amount: 2));
        var result = engine.Dispatch(world, new SimulationFact("summary", entity.Id, entity.Id));

        Assert.Equal(1, result.RulesMatched);
        Assert.True(entity.TryGetProperty(world, "score", out var score));
        Assert.Equal(1, score);
    }

    [Fact]
    public void Rolled_back_dispatches_do_not_enter_expression_event_history()
    {
        var world = new World(seed: 34, valueScale: 1) { LogicalTick = 10 };
        var entity = world.Add(new DataEntity([], [], ("heat", double.MaxValue), ("score", 0)));
        var engine = new RuleEngine(new CompiledRuleSet([
            new CompiledRule("broken", "broken", 0, [],
                [RuleEffect.AdjustProperty(RuleSubject.Target, "heat", double.MaxValue)]),
            new CompiledRule("report", "report", 0,
                [RuleCondition.FromExpression(ExpressionCompiler.Compile("event_count('broken', 10) > 0"))],
                [RuleEffect.AdjustProperty(RuleSubject.Target, "score", 1)])
        ]));

        var rolledBack = engine.Dispatch(world, new SimulationFact("broken", entity.Id, entity.Id));
        var report = engine.Dispatch(world, new SimulationFact("report", entity.Id, entity.Id));

        Assert.True(rolledBack.RolledBack);
        Assert.Equal(0, report.RulesMatched);
        Assert.True(entity.TryGetProperty(world, "score", out var score));
        Assert.Equal(0, score);
    }

    [Fact]
    public void Nested_dispatch_history_is_discarded_when_its_outer_world_transaction_rolls_back()
    {
        var world = new World(seed: 36, valueScale: 1) { LogicalTick = 20 };
        var entity = world.Add(new DataEntity([], [], ("score", 0)));
        var engine = new RuleEngine(new CompiledRuleSet([
            new CompiledRule("report.delivery", "report.delivery", 0,
                [RuleCondition.FromExpression(ExpressionCompiler.Compile("event_count('delivery', 8) > 0"))],
                [RuleEffect.AdjustProperty(RuleSubject.Target, "score", 1)])
        ]));

        using (var outerTransaction = world.BeginTransaction())
        {
            var delivery = engine.Dispatch(world, new SimulationFact("delivery", entity.Id, entity.Id));
            Assert.False(delivery.RolledBack);
            outerTransaction.Rollback();
        }

        var report = engine.Dispatch(world, new SimulationFact("report.delivery", entity.Id, entity.Id));

        Assert.Equal(0, report.RulesMatched);
        Assert.Equal(0, world.EffectiveProperty(entity, "score"));
    }

    [Fact]
    public void Event_queries_match_an_ordered_scan_for_mixed_types_and_windows()
    {
        var samples = new[]
        {
            new RuleEventHistorySample(6, "alpha", 80),
            new RuleEventHistorySample(6, "beta", -100),
            new RuleEventHistorySample(8, "beta", 3),
            new RuleEventHistorySample(8, "alpha", 2),
            new RuleEventHistorySample(9, "beta", 7),
            new RuleEventHistorySample(10, "alpha", 4),
            new RuleEventHistorySample(10, "beta", 5),
        };
        const int currentTick = 10;
        const int tickRateHz = 10;
        var comparisons = new List<string>();
        foreach (var eventType in new[] { "alpha", "beta" })
        {
            foreach (var window in new[] { 2, 5 })
            {
                var firstTick = currentTick - window + 1;
                var matching = samples.Where(sample => sample.Tick >= firstTick && sample.EventType == eventType).ToArray();
                var count = matching.Length;
                var amount = 0.0;
                foreach (var sample in samples)
                {
                    if (sample.Tick >= firstTick && sample.EventType == eventType)
                    {
                        amount += sample.Amount;
                    }
                }

                var rate = amount * tickRateHz / window;
                comparisons.Add($"event_count('{eventType}', {window}) == {count.ToString(CultureInfo.InvariantCulture)}");
                comparisons.Add($"rate('{eventType}', {window}) == {rate.ToString("G17", CultureInfo.InvariantCulture)}");
            }
        }

        var world = new World(seed: 45, valueScale: 1) { LogicalTick = currentTick };
        var entity = world.Add(new DataEntity([], [], ("score", 0)));
        var engine = new RuleEngine(new CompiledRuleSet([
            new CompiledRule("query.oracle", "summary", 0,
                [RuleCondition.FromExpression(ExpressionCompiler.Compile(string.Join(" && ", comparisons)))],
                [RuleEffect.AdjustProperty(RuleSubject.Target, "score", 1)])
        ]), tickRateHz: tickRateHz);
        engine.RestoreEventHistory(samples, currentTick);

        var result = engine.Dispatch(world, new SimulationFact("summary", entity.Id, entity.Id));

        Assert.Equal(1, result.RulesMatched);
        Assert.Equal(1, world.EffectiveProperty(entity, "score"));
    }

    [Fact]
    public void Pending_event_queries_update_between_reaction_facts()
    {
        var world = new World(seed: 46, valueScale: 1) { LogicalTick = 10 };
        var entity = world.Add(new DataEntity([], [], ("score", 0)));
        var engine = new RuleEngine(new CompiledRuleSet([
            new CompiledRule("start.summary", "start", 0, [], [RuleEffect.Emit("summary.first")]),
            new CompiledRule("summary.first", "summary.first", 0,
                [RuleCondition.FromExpression(ExpressionCompiler.Compile(
                    "event_count('delivery', 5) == 0 && rate('delivery', 5) == 0"))],
                [RuleEffect.Emit("delivery")]),
            new CompiledRule("delivery.summary", "delivery", 0, [], [RuleEffect.Emit("summary.second")]),
            new CompiledRule("summary.second", "summary.second", 0,
                [RuleCondition.FromExpression(ExpressionCompiler.Compile(
                    "event_count('delivery', 5) == 1 && rate('delivery', 5) == 18"))],
                [RuleEffect.AdjustProperty(RuleSubject.Target, "score", 1)])
        ]), tickRateHz: 30);

        var result = engine.Dispatch(world, new SimulationFact("start", entity.Id, entity.Id, Amount: 3));

        Assert.False(result.CutOff);
        Assert.Equal(4, result.FactsProcessed);
        Assert.Equal(4, result.RulesMatched);
        Assert.Equal(1, world.EffectiveProperty(entity, "score"));
        Assert.Equal(new[] { new RuleEventHistorySample(10, "delivery", 3) }, engine.CaptureEventHistory());
    }

    [Fact]
    public void Restored_history_survives_prune_rollback_and_is_pruned_after_commit()
    {
        var world = new World(seed: 47, valueScale: 1) { LogicalTick = 5 };
        var entity = world.Add(new DataEntity([], [], ("score", 0)));
        var engine = new RuleEngine(new CompiledRuleSet([
            new CompiledRule("history.report", "report", 0,
                [RuleCondition.FromExpression(ExpressionCompiler.Compile(
                    "event_count('delivery', 3) == 1 && rate('delivery', 3) == 2"))],
                [RuleEffect.AdjustProperty(RuleSubject.Target, "score", 1)])
        ]), tickRateHz: 3);
        var restored = new[] { new RuleEventHistorySample(4, "delivery", 2) };
        engine.RestoreEventHistory(restored, currentTick: 5);

        using (var outerTransaction = world.BeginTransaction())
        {
            world.LogicalTick = 10;
            var pruned = engine.Dispatch(world, new SimulationFact("report", entity.Id, entity.Id));
            Assert.Equal(0, pruned.RulesMatched);
            outerTransaction.Rollback();
        }

        Assert.Equal(5, world.LogicalTick);
        Assert.Equal(restored, engine.CaptureEventHistory());
        var afterRollback = engine.Dispatch(world, new SimulationFact("report", entity.Id, entity.Id));
        Assert.Equal(1, afterRollback.RulesMatched);
        Assert.Equal(1, world.EffectiveProperty(entity, "score"));

        world.LogicalTick = 10;
        var afterCommittedPrune = engine.Dispatch(world, new SimulationFact("report", entity.Id, entity.Id));
        Assert.Equal(0, afterCommittedPrune.RulesMatched);
        Assert.Empty(engine.CaptureEventHistory());
    }

    [Fact]
    public void Prune_rollback_restores_expired_prefix_before_retained_history()
    {
        var world = new World(seed: 49, valueScale: 1) { LogicalTick = 10 };
        var entity = world.Add(new DataEntity([], [], ("score", 0)));
        var engine = new RuleEngine(new CompiledRuleSet([
            new CompiledRule("history.report", "report", 0,
                [RuleCondition.FromExpression(ExpressionCompiler.Compile(
                    "event_count('delivery', 3) == 2 && rate('delivery', 3) == 10"))],
                [RuleEffect.AdjustProperty(RuleSubject.Target, "score", 1)])
        ]), tickRateHz: 3);
        var restored = new[]
        {
            new RuleEventHistorySample(8, "delivery", 3),
            new RuleEventHistorySample(10, "delivery", 7)
        };
        engine.RestoreEventHistory(restored, currentTick: 10);
        var beforePrune = engine.Dispatch(world, new SimulationFact("report", entity.Id, entity.Id));

        Assert.Equal(1, beforePrune.RulesMatched);

        using (var outerTransaction = world.BeginTransaction())
        {
            world.LogicalTick = 11;
            var pruningDispatch = engine.Dispatch(world, new SimulationFact("probe", entity.Id, entity.Id));
            Assert.False(pruningDispatch.RolledBack);
            outerTransaction.Rollback();
        }

        var restoredHistory = engine.CaptureEventHistory();
        var afterRollback = engine.Dispatch(world, new SimulationFact("report", entity.Id, entity.Id));

        Assert.Equal(restored, restoredHistory);
        Assert.Equal(1, afterRollback.RulesMatched);
        Assert.Equal(2, world.EffectiveProperty(entity, "score"));
    }

    [Fact]
    public void Event_rate_keeps_the_existing_floating_point_sum_order_at_the_rule_threshold()
    {
        var world = new World(seed: 48, valueScale: 1) { LogicalTick = 10 };
        var entity = world.Add(new DataEntity([], [], ("score", 0)));
        var engine = new RuleEngine(new CompiledRuleSet([
            new CompiledRule("rate.summary", "summary", 0,
                [RuleCondition.FromExpression(ExpressionCompiler.Compile("rate('delivery', 1) == 10"))],
                [RuleEffect.AdjustProperty(RuleSubject.Target, "score", 1)])
        ]), tickRateHz: 10);
        engine.RestoreEventHistory([new RuleEventHistorySample(10, "delivery", 1e16)], currentTick: 10);
        var observer = new SimulationFactObserver((fact, emitter) =>
        {
            if (fact.Type == "start")
            {
                emitter.Enqueue(new SimulationFact("delivery", fact.SourceId, fact.TargetId, 1, -1e16));
            }
            else if (fact.Type == "delivery" && fact.Depth == 1)
            {
                emitter.Enqueue(new SimulationFact("delivery", fact.SourceId, fact.TargetId, 2, 1));
            }
            else if (fact.Type == "delivery" && fact.Depth == 2)
            {
                emitter.Enqueue(new SimulationFact("summary", fact.SourceId, fact.TargetId, 3));
            }
        });

        var result = engine.Dispatch(world, new SimulationFact("start", entity.Id, entity.Id), observer);

        Assert.False(result.CutOff);
        Assert.Equal(1, result.RulesMatched);
        Assert.Equal(1, world.EffectiveProperty(entity, "score"));
        Assert.Equal(1, engine.CaptureEventHistory().Last().Amount);
    }

    [Fact]
    public void Observed_reactions_share_the_bounded_fact_budget()
    {
        var world = new World(seed: 31, valueScale: 1);
        var observer = new SimulationFactObserver((fact, enqueue) =>
            enqueue.Enqueue(new SimulationFact("observed", fact.SourceId, fact.TargetId, fact.Depth + 1)));
        var engine = new RuleEngine(new CompiledRuleSet([]), maxFacts: 3);

        var result = engine.Dispatch(world, new SimulationFact("start", 0, 0), observer);

        Assert.True(result.CutOff);
        Assert.Equal("fact_budget", result.CutoffReason);
        Assert.Equal(3, result.FactsProcessed);
    }

    [Fact]
    public void Invalid_observed_fact_rolls_back_prior_effects_in_the_dispatch()
    {
        var world = new World(seed: 37, valueScale: 1);
        var entity = world.Add(new DataEntity([], [], ("score", 0)));
        var engine = new RuleEngine(new CompiledRuleSet([
            new CompiledRule("start", "start", 0, [], [
                RuleEffect.AdjustProperty(RuleSubject.Target, "score", 1),
                RuleEffect.Emit("chain")]),
        ]));
        var observer = new SimulationFactObserver((fact, emitter) =>
        {
            if (fact.Type == "chain")
            {
                emitter.Enqueue(new SimulationFact("observed", -1, fact.TargetId));
            }
        });

        var result = engine.Dispatch(world, new SimulationFact("start", entity.Id, entity.Id), observer);

        Assert.True(result.CutOff);
        Assert.True(result.RolledBack);
        Assert.Equal("invalid_fact", result.CutoffReason);
        Assert.Equal(0, world.EffectiveProperty(entity, "score"));
    }

    [Fact]
    public void Trace_records_the_ordered_facts_and_rules_that_caused_each_reaction()
    {
        var world = new World(seed: 32, valueScale: 1);
        var entity = world.Add(new DataEntity([], [], ("heat", 0)));
        var engine = new RuleEngine(new CompiledRuleSet([
            new CompiledRule("heat.start", "heat", 0, [], [RuleEffect.Emit("heat.chain")]),
            new CompiledRule("heat.chain", "heat.chain", 0, [],
                [RuleEffect.AdjustProperty(RuleSubject.Target, "heat", 1)])
        ]));

        var trace = engine.DispatchWithTrace(world, new SimulationFact("heat", entity.Id, entity.Id));

        Assert.Equal(2, trace.Facts.Length);
        Assert.Equal("heat", trace.Facts[0].Fact.Type);
        Assert.Equal(["heat.start"], trace.Facts[0].MatchedRules);
        Assert.Equal("heat.chain", trace.Facts[1].Fact.Type);
        Assert.Equal(["heat.chain"], trace.Facts[1].MatchedRules);
        Assert.Equal(2, trace.Result.FactsProcessed);
    }

    [Fact]
    public void Warm_rule_dispatch_allocates_nothing_per_fact()
    {
        var world = new World(seed: 33, valueScale: 1);
        var entity = world.Add(new DataEntity([], [], ("heat", 0)));
        var engine = new RuleEngine(new CompiledRuleSet([
            new CompiledRule("heat.increment", "heat", 0, [],
                [RuleEffect.AdjustProperty(RuleSubject.Target, "heat", 1)])
        ]));
        var fact = new SimulationFact("heat", entity.Id, entity.Id);
        for (var warmup = 0; warmup < 8; warmup++)
        {
            engine.Dispatch(world, fact);
        }

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1000; index++)
        {
            engine.Dispatch(world, fact);
        }

        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Equal(0, allocatedBytes);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(9)]
    public void Rejects_initial_fact_depth_outside_the_configured_range(int depth)
    {
        var world = new World(seed: 4, valueScale: 1);
        var engine = new RuleEngine(new CompiledRuleSet([]), maxDepth: 8);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            engine.Dispatch(world, new SimulationFact("start", 0, 0, depth)));
    }

    [Fact]
    public void Fixed_step_clock_caps_catch_up_without_changing_step_size()
    {
        var clock = new FixedStepClock(tickRateHz: 50, maxCatchUpSteps: 2);
        var steps = 0;

        var result = clock.Advance(TimeSpan.FromMilliseconds(100), () => steps++);

        Assert.Equal(2, steps);
        Assert.Equal(2, result.Steps);
        Assert.True(result.DroppedTime > TimeSpan.Zero);
    }

    [Fact]
    public void Extensions_are_explicit_and_duplicate_modules_are_rejected()
    {
        var catalog = new ExtensionModuleCatalog().Add(new TestExtension());

        Assert.Contains("sample.extension", catalog.ModuleIds);
        Assert.Throws<InvalidOperationException>(() => catalog.Add(new TestExtension()));
    }

    private sealed class TestExtension : ISystemicExtensionModule
    {
        public string Id => "sample.extension";
    }
}

public sealed class RuleEngineHistoryWorkloadTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Performance")]
    public void Repeated_event_queries_report_release_cost_without_a_timing_gate()
    {
        const int historySamples = 4_320;
        const int ruleCount = 96;
        const int querySamples = 15;
        const int currentTick = 239;
        const int windowTicks = 240;
        const string expression = "event_count('alpha', 240) > 0 && rate('alpha', 240) >= 0 "
            + "&& event_count('beta', 240) > 0 && rate('beta', 240) >= 0";
        var rules = Enumerable.Range(0, ruleCount).Select(index =>
            new CompiledRule($"history.query.{index:D3}", "probe", 0,
                [RuleCondition.FromExpression(ExpressionCompiler.Compile(expression))], []));
        var engine = new RuleEngine(new CompiledRuleSet(rules));
        var samples = new List<RuleEventHistorySample>(historySamples);
        for (var tick = 0; tick < windowTicks; tick++)
        {
            for (var repeat = 0; repeat < 9; repeat++)
            {
                samples.Add(new RuleEventHistorySample(tick, "alpha", 1));
                samples.Add(new RuleEventHistorySample(tick, "beta", 1));
            }
        }

        Assert.Equal(historySamples, samples.Count);
        engine.RestoreEventHistory(samples, currentTick);
        var world = new World(seed: 95, valueScale: 1) { LogicalTick = currentTick };
        for (var warmup = 0; warmup < 4; warmup++)
        {
            Assert.Equal(ruleCount, engine.Dispatch(world, new SimulationFact("probe", 0, 0)).RulesMatched);
        }

        var timings = new double[querySamples];
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < timings.Length; index++)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var result = engine.Dispatch(world, new SimulationFact("probe", 0, 0));
            timings[index] = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Assert.Equal(ruleCount, result.RulesMatched);
        }

        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Array.Sort(timings);
        output.WriteLine($"History samples={historySamples}, rules={ruleCount}, repeated queries per fact=4, "
            + $"warmup=4, samples={querySamples}; p50/p95={timings[7]:F3}/{timings[14]:F3} ms; "
            + $"allocations={allocatedBytes} B total ({allocatedBytes / querySamples} B/dispatch).");
    }
}
