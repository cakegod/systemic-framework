using Systemic.Kernel;
using Systemic.Rules;

namespace Systemic.Tests.Rules;

/// <summary>
/// A row scan is the crowd path: one authored rule evaluated per row, one scope, and no fact per
/// row. These tests pin the outcomes and the shape refusals, not the wall clock.
/// </summary>
public sealed class RowScanTests
{
    private static readonly PopulationColumnKey<int> s_energyKey =
        new("test.scan.energy", 1, PopulationCodecs.Int32);

    [Fact]
    public void A_scan_evaluates_the_rule_for_every_row_and_applies_conditional_effects()
    {
        var world = new World(seed: 121, valueScale: 1);
        var population = Create(world, [1, 0, 2, 0, 1]);
        var engine = Bind(world, ThinkRule());

        var result = engine.EvaluateRowEvent(world, "test.think", Crowd.Id, new PopulationRange(0, 5));

        Assert.Equal(5, result.RowsEvaluated);
        Assert.Equal(3, result.RulesMatched);
        Assert.Equal(3, result.EffectsApplied);
        Assert.False(result.RolledBack);
        Assert.Equal([0, 0, 1, 0, 0], ReadEnergy(population));
    }

    [Fact]
    public void A_scan_condition_reads_tags_and_writes_them_per_row()
    {
        var world = new World(seed: 122, valueScale: 1);
        var population = Create(world, [5, 5, 5]);
        population.AddTag(1, "burning");
        var engine = Bind(world,
            new CompiledRule("test.burn", "test.think", 0,
                [RuleCondition.HasTag(RuleSubject.FactRow(Crowd.Id), "burning")],
                [RuleEffect.AddTag(RuleSubject.FactRow(Crowd.Id), "poisoned")]));

        var result = engine.EvaluateRowEvent(world, "test.think", Crowd.Id, new PopulationRange(0, 3));

        Assert.Equal(1, result.RulesMatched);
        Assert.False(population.HasTag(0, "poisoned"));
        Assert.True(population.HasTag(1, "poisoned"));
        Assert.False(population.HasTag(2, "poisoned"));
    }

    // A leading tag condition lets the scan test one mask bit per row, so this pins that the skip is
    // only a skip: a tagged row that fails a later condition must still be left alone.
    [Fact]
    public void A_tag_prefilter_still_applies_the_remaining_conditions()
    {
        var world = new World(seed: 127, valueScale: 1);
        var population = Create(world, [5, 0, 3, 5]);
        population.AddTag(1, "burning");
        population.AddTag(2, "burning");
        var engine = Bind(world,
            new CompiledRule("test.burn", "test.think", 0,
                [RuleCondition.HasTag(RuleSubject.FactRow(Crowd.Id), "burning"),
                 RuleCondition.PropertyAtLeast(RuleSubject.FactRow(Crowd.Id), "test.scan.energy", 1)],
                [RuleEffect.AdjustProperty(RuleSubject.FactRow(Crowd.Id), "test.scan.energy", -1)]));

        var result = engine.EvaluateRowEvent(world, "test.think", Crowd.Id, new PopulationRange(0, 4));

        Assert.Equal(4, result.RowsEvaluated);
        Assert.Equal(1, result.RulesMatched);
        Assert.Equal([5, 0, 2, 5], ReadEnergy(population));
    }

    [Fact]
    public void A_scan_only_evaluates_the_rows_in_the_range()
    {
        var world = new World(seed: 123, valueScale: 1);
        var population = Create(world, [2, 2, 2, 2]);
        var engine = Bind(world, ThinkRule());

        var result = engine.EvaluateRowEvent(world, "test.think", Crowd.Id, new PopulationRange(1, 2));

        Assert.Equal(2, result.RowsEvaluated);
        Assert.Equal([2, 1, 1, 2], ReadEnergy(population));
    }

    [Fact]
    public void A_scan_does_not_allocate_once_warmed()
    {
        var world = new World(seed: 124, valueScale: 1);
        var population = Create(world, [.. Enumerable.Repeat(1_000_000, 100_000)]);
        var engine = Bind(world, ThinkRule());
        var range = new PopulationRange(0, 100_000);
        for (var warmup = 0; warmup < 3; warmup++)
        {
            engine.EvaluateRowEvent(world, "test.think", Crowd.Id, range);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        var matched = 0;
        for (var repetition = 0; repetition < 5; repetition++)
        {
            matched += engine.EvaluateRowEvent(world, "test.think", Crowd.Id, range).RulesMatched;
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"Row scan allocated {allocated} bytes over 500,000 row evaluations.");
        Assert.Equal(500_000, matched);
        // Three warm-up scans and five measured ones, each spending one energy from every row.
        Assert.Equal(1_000_000 - 8, ReadEnergy(population)[0]);
    }

    [Fact]
    public void A_scan_rejects_entity_subjects_and_emitted_events_before_any_row()
    {
        var world = new World(seed: 125, valueScale: 1);
        var population = Create(world, [3, 3]);
        var entityRule = new RuleEngine(new CompiledRuleSet([
            new CompiledRule("test.think", "test.think", 0, [],
                [RuleEffect.AdjustProperty(RuleSubject.Target, "energy", -1)])]));
        var emitRule = new RuleEngine(new CompiledRuleSet([
            new CompiledRule("test.think", "test.think", 0, [], [RuleEffect.Emit("test.other")])]));

        var entityError = Assert.Throws<InvalidOperationException>(() =>
            entityRule.EvaluateRowEvent(world, "test.think", Crowd.Id, new PopulationRange(0, 2)));
        var emitError = Assert.Throws<InvalidOperationException>(() =>
            emitRule.EvaluateRowEvent(world, "test.think", Crowd.Id, new PopulationRange(0, 2)));

        Assert.Contains("test.think", entityError.Message, StringComparison.Ordinal);
        Assert.Contains(Crowd.Id, emitError.Message, StringComparison.Ordinal);
        Assert.Equal([3, 3], ReadEnergy(population));
    }

    [Fact]
    public void A_scan_rejects_a_range_outside_the_population_and_a_population_without_handles()
    {
        var world = new World(seed: 126, valueScale: 1);
        var population = Create(world, [1, 1]);
        var handlesOff = world.DefinePopulation<NoHandlesCrowd>();
        handlesOff.AddRange(2);
        var engine = Bind(world, ThinkRule());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            engine.EvaluateRowEvent(world, "test.think", Crowd.Id, new PopulationRange(1, 3)));
        var error = Assert.Throws<InvalidOperationException>(() =>
            engine.EvaluateRowEvent(world, "test.think", NoHandlesCrowd.Id, new PopulationRange(0, 2)));
        var missing = Assert.Throws<InvalidOperationException>(() =>
            engine.EvaluateRowEvent(world, "test.think", "test.missing", new PopulationRange(0, 2)));

        Assert.Contains("enableHandles", error.Message, StringComparison.Ordinal);
        Assert.Contains("test.missing", missing.Message, StringComparison.Ordinal);
        Assert.Equal([1, 1], ReadEnergy(population));
    }

    private static CompiledRule ThinkRule() =>
        new("test.think", "test.think", 0,
            [RuleCondition.PropertyAtLeast(RuleSubject.FactRow(Crowd.Id), "test.scan.energy", 1)],
            [RuleEffect.AdjustProperty(RuleSubject.FactRow(Crowd.Id), "test.scan.energy", -1)]);

    private static RuleEngine Bind(World world, CompiledRule rule)
    {
        var engine = new RuleEngine(new CompiledRuleSet([rule]));
        engine.Bind(world);
        return engine;
    }

    private static Population<Crowd> Create(World world, int[] energy)
    {
        var population = world.DefinePopulation<Crowd>(enableHandles: true);
        var column = population.Column(s_energyKey);
        population.Reserve(energy.Length);
        population.AddRange(energy.Length);
        population.WithAccess(new PopulationRange(0, energy.Length), (Column: column, Values: energy),
            static ((PopulationColumn<int> Column, int[] Values) state,
                scoped ref PopulationAccessScope<Crowd> scope) =>
            {
                var values = scope.EditRange(state.Column);
                state.Values.AsSpan().CopyTo(values);
            });
        return population;
    }

    private static int[] ReadEnergy(Population<Crowd> population)
    {
        var values = new int[population.Count];
        population.WithAccess(new PopulationRange(0, values.Length),
            (Column: population.Column(s_energyKey), Values: values),
            static ((PopulationColumn<int> Column, int[] Values) state,
                scoped ref PopulationAccessScope<Crowd> scope) =>
            {
                var energy = scope.ReadAll(state.Column);
                for (var row = 0; row < state.Values.Length; row++)
                {
                    state.Values[row] = energy[row];
                }
            });
        return values;
    }

    private readonly struct Crowd : IPopulationKind
    {
        public static string Id => "test.scan-crowd";

        public static int Version => 1;

        public static string[] Tags => ["burning", "poisoned"];
    }

    private readonly struct NoHandlesCrowd : IPopulationKind
    {
        public static string Id => "test.scan-no-handles";

        public static int Version => 1;
    }
}
