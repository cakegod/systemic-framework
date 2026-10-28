using Systemic.Kernel;
using Systemic.Rules;

namespace Systemic.Tests.Rules;

public sealed class RowAddressedRuleTests
{
    private static readonly PopulationColumnKey<int> s_healthKey = new("test.prey.health", 1, PopulationCodecs.Int32);

    [Fact]
    public void Entity_subject_addresses_one_entity_regardless_of_the_fact()
    {
        var world = new World(seed: 7, valueScale: 1);
        var first = world.Add(new DataEntity([], [], ("value", 0.0)));
        var second = world.Add(new DataEntity([], [], ("value", 0.0)));
        var engine = Bind(world,
            new CompiledRule("upkeep", "upkeep", 0, [],
                [RuleEffect.AdjustProperty(RuleSubject.Entity(second.Id), "value", 3)]));

        engine.Dispatch(world, new SimulationFact("upkeep", first.Id, first.Id));

        Assert.True(first.TryGetProperty(world, "value", out var firstValue));
        Assert.Equal(0, firstValue);
        Assert.True(second.TryGetProperty(world, "value", out var value));
        Assert.Equal(3, value);
    }

    [Fact]
    public void Row_effect_adjusts_the_addressed_column_and_leaves_other_rows_alone()
    {
        var world = new World(seed: 7, valueScale: 1);
        var prey = CreatePrey(world, 3);
        var engine = Bind(world,
            new CompiledRule("prey.bite", "prey.bite", 0, [],
                [RuleEffect.AdjustProperty(RuleSubject.Row(Prey.Id, 1), "test.prey.health", -5)]));

        var result = engine.Dispatch(world, new SimulationFact("prey.bite", 0, 0));

        Assert.Equal(1, result.EffectsApplied);
        Assert.False(result.RolledBack);
        Assert.Equal([100, 95, 100], ReadHealth(prey));
    }

    [Fact]
    public void Row_condition_reads_the_addressed_column()
    {
        var world = new World(seed: 7, valueScale: 1);
        var prey = CreatePrey(world, 2);
        var engine = Bind(world,
            new CompiledRule("prey.weaken", "prey.weaken", 0,
                [RuleCondition.PropertyAtLeast(RuleSubject.Row(Prey.Id, 1), "test.prey.health", 50)],
                [RuleEffect.AdjustProperty(RuleSubject.Row(Prey.Id, 1), "test.prey.health", -10)]));

        Assert.Equal(1, engine.Dispatch(world, new SimulationFact("prey.weaken", 0, 0)).EffectsApplied);
        Assert.Equal([100, 90], ReadHealth(prey));

        SetHealth(prey, 1, 40);
        var skipped = engine.Dispatch(world, new SimulationFact("prey.weaken", 0, 0));

        Assert.Equal(0, skipped.RulesMatched);
        Assert.Equal(0, skipped.EffectsApplied);
        Assert.Equal([100, 40], ReadHealth(prey));
    }

    [Fact]
    public void Row_effect_on_a_despawned_slot_is_skipped()
    {
        var world = new World(seed: 7, valueScale: 1);
        var prey = CreatePrey(world, 2);
        var engine = Bind(world,
            new CompiledRule("prey.bite", "prey.bite", 0, [],
                [RuleEffect.AdjustProperty(RuleSubject.Row(Prey.Id, 4), "test.prey.health", -5)]));

        var result = engine.Dispatch(world, new SimulationFact("prey.bite", 0, 0));

        Assert.False(result.RolledBack);
        Assert.Equal([100, 100], ReadHealth(prey));
    }

    [Fact]
    public void Row_effects_roll_back_with_the_dispatch_transaction()
    {
        var world = new World(seed: 7, valueScale: 1);
        var prey = CreatePrey(world, 2);
        var engine = Bind(world,
            new CompiledRule("prey.bite", "prey.bite", 0, [],
                [RuleEffect.AdjustProperty(RuleSubject.Row(Prey.Id, 0), "test.prey.health", -5)]));

        var result = engine.DispatchBatch(world,
            [new SimulationFact("prey.bite", 0, 0), new SimulationFact("poison", 0, 0)],
            (fact, emitter) =>
            {
                if (fact.Type == "poison")
                {
                    Assert.False(emitter.Enqueue(new SimulationFact("invalid", -1, 0)));
                }
            });

        Assert.True(result.RolledBack);
        Assert.Equal([100, 100], ReadHealth(prey));
    }

    [Fact]
    public void Row_dispatch_does_not_allocate_once_warmed()
    {
        var world = new World(seed: 7, valueScale: 1);
        var prey = CreatePrey(world, 4);
        var engine = Bind(world,
            new CompiledRule("prey.bite", "prey.bite", 0, [],
                [RuleEffect.AdjustProperty(RuleSubject.Row(Prey.Id, 1), "test.prey.health", -1)]));
        SimulationFact[] facts = [new("prey.bite", 0, 0)];
        for (var warmup = 0; warmup < 16; warmup++)
        {
            engine.DispatchBatch(world, facts);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var repetition = 0; repetition < 100; repetition++)
        {
            engine.DispatchBatch(world, facts);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"Row dispatch allocated {allocated} bytes over 100 dispatches.");
        Assert.Equal(100 - 116, ReadHealth(prey)[1]);
    }

    [Fact]
    public void Unknown_row_property_fails_at_bind_with_the_rule_and_schema_named()
    {
        var world = new World(seed: 7, valueScale: 1);
        CreatePrey(world, 1);
        var engine = new RuleEngine(new CompiledRuleSet([
            new CompiledRule("prey.stamina", "prey.stamina", 0, [],
                [RuleEffect.AdjustProperty(RuleSubject.Row(Prey.Id, 0), "test.prey.stamina", 1)])]));

        var error = Assert.Throws<InvalidOperationException>(() => engine.Bind(world));

        Assert.Contains("prey.stamina", error.Message, StringComparison.Ordinal);
        Assert.Contains("stamina", error.Message, StringComparison.Ordinal);
        Assert.Contains(Prey.Id, error.Message, StringComparison.Ordinal);
        // A rejected bind must not leave the engine half-bound and ready to dispatch.
        Assert.Throws<InvalidOperationException>(() =>
            engine.Dispatch(world, new SimulationFact("prey.stamina", 0, 0)));
    }

    [Fact]
    public void Unknown_population_fails_at_bind()
    {
        var world = new World(seed: 7, valueScale: 1);
        CreatePrey(world, 1);
        var engine = new RuleEngine(new CompiledRuleSet([
            new CompiledRule("ghost.bite", "ghost.bite", 0, [],
                [RuleEffect.AdjustProperty(RuleSubject.Row("test.ghost", 0), "test.prey.health", 1)])]));

        var error = Assert.Throws<InvalidOperationException>(() => engine.Bind(world));

        Assert.Contains("ghost.bite", error.Message, StringComparison.Ordinal);
        Assert.Contains("test.ghost", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Row_tag_effects_are_rejected_at_bind_instead_of_silently_ignored()
    {
        var world = new World(seed: 7, valueScale: 1);
        CreatePrey(world, 1);
        var engine = new RuleEngine(new CompiledRuleSet([
            new CompiledRule("prey.tag", "prey.tag", 0, [],
                [RuleEffect.AddTag(RuleSubject.Row(Prey.Id, 0), "poisoned")])]));

        var error = Assert.Throws<InvalidOperationException>(() => engine.Bind(world));

        Assert.Contains("prey.tag", error.Message, StringComparison.Ordinal);
        Assert.Contains("poisoned", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Fractional_adjustment_of_an_integer_column_is_rejected_at_bind()
    {
        var world = new World(seed: 7, valueScale: 1);
        CreatePrey(world, 1);
        var engine = new RuleEngine(new CompiledRuleSet([
            new CompiledRule("prey.bleed", "prey.bleed", 0, [],
                [RuleEffect.AdjustProperty(RuleSubject.Row(Prey.Id, 0), "test.prey.health", -0.5)])]));

        var error = Assert.Throws<InvalidOperationException>(() => engine.Bind(world));

        Assert.Contains("prey.bleed", error.Message, StringComparison.Ordinal);
        Assert.Contains("whole-number", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Row_rules_bind_on_first_dispatch_so_a_missing_column_still_fails_loudly()
    {
        var world = new World(seed: 7, valueScale: 1);
        CreatePrey(world, 1);
        var engine = new RuleEngine(new CompiledRuleSet([
            new CompiledRule("prey.stamina", "prey.stamina", 0, [],
                [RuleEffect.AdjustProperty(RuleSubject.Row(Prey.Id, 0), "test.prey.stamina", 1)])]));

        var error = Assert.Throws<InvalidOperationException>(() =>
            engine.Dispatch(world, new SimulationFact("prey.stamina", 0, 0)));

        Assert.Contains("stamina", error.Message, StringComparison.Ordinal);
        Assert.Contains(Prey.Id, error.Message, StringComparison.Ordinal);
    }

    private static RuleEngine Bind(World world, CompiledRule rule)
    {
        var engine = new RuleEngine(new CompiledRuleSet([rule]));
        engine.Bind(world);
        return engine;
    }

    private static PreyFixture CreatePrey(World world, int count)
    {
        var population = world.DefinePopulation<Prey>();
        var health = population.Column(s_healthKey);
        population.Reserve(count);
        population.AddRange(count);
        population.WithAccess(new PopulationRange(0, count), health,
            static (PopulationColumn<int> column, scoped ref PopulationAccessScope<Prey> scope) =>
            {
                var values = scope.EditRange(column);
                for (var row = 0; row < values.Length; row++)
                {
                    values[row] = 100;
                }
            });
        return new PreyFixture(population, health);
    }

    private static int[] ReadHealth(PreyFixture prey)
    {
        var values = new int[prey.Population.Count];
        prey.Population.WithAccess(new PopulationRange(0, values.Length), (Health: prey.Health, Values: values),
            static ((PopulationColumn<int> Health, int[] Values) state, scoped ref PopulationAccessScope<Prey> scope) =>
            {
                var health = scope.ReadAll(state.Health);
                for (var row = 0; row < state.Values.Length; row++)
                {
                    state.Values[row] = health[row];
                }
            });
        return values;
    }

    private static void SetHealth(PreyFixture prey, int row, int value) =>
        prey.Population.WithAccess(new PopulationRange(row, 1), (Health: prey.Health, Value: value),
            static ((PopulationColumn<int> Health, int Value) state, scoped ref PopulationAccessScope<Prey> scope) =>
                scope.EditRange(state.Health)[0] = state.Value);

    private sealed record PreyFixture(Population<Prey> Population, PopulationColumn<int> Health);

    private readonly struct Prey : IPopulationKind
    {
        public static string Id => "test.prey";

        public static int Version => 1;
    }
}
