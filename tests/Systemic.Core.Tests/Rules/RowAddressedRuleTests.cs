using Systemic.Kernel;
using Systemic.Rules;

namespace Systemic.Tests.Rules;

public sealed class RowAddressedRuleTests
{
    private static readonly PopulationColumnKey<int> s_healthKey = new("test.prey.health", 1, PopulationCodecs.Int32);

    private static readonly PopulationColumnKey<int> s_buffHealthKey =
        new("test.buff.health", 1, PopulationCodecs.Int32);

    private static readonly PopulationColumnKey<int> s_handledHealthKey =
        new("test.handled.health", 1, PopulationCodecs.Int32);

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
    public void Unknown_row_tag_fails_at_bind_with_the_rule_tag_and_declared_tags_named()
    {
        var world = new World(seed: 7, valueScale: 1);
        CreateTaggedPrey(world, 1);
        var engine = new RuleEngine(new CompiledRuleSet([
            new CompiledRule("prey.tag", "prey.tag", 0, [],
                [RuleEffect.AddTag(RuleSubject.Row(TaggedPrey.Id, 0), "onfire")])]));

        var error = Assert.Throws<InvalidOperationException>(() => engine.Bind(world));

        Assert.Contains("prey.tag", error.Message, StringComparison.Ordinal);
        Assert.Contains("onfire", error.Message, StringComparison.Ordinal);
        Assert.Contains("poisoned", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Row_tag_effect_sets_the_schema_bit_for_one_row()
    {
        var world = new World(seed: 7, valueScale: 1);
        var prey = CreateTaggedPrey(world, 3);
        var engine = Bind(world,
            new CompiledRule("prey.envenom", "prey.envenom", 0, [],
                [RuleEffect.AddTag(RuleSubject.Row(TaggedPrey.Id, 1), "poisoned")]));

        var result = engine.Dispatch(world, new SimulationFact("prey.envenom", 0, 0));

        Assert.Equal(1, result.EffectsApplied);
        Assert.False(prey.HasTag(0, "poisoned"));
        Assert.True(prey.HasTag(1, "poisoned"));
        Assert.False(prey.HasTag(2, "poisoned"));
        Assert.False(prey.HasTag(1, "feral"));
    }

    [Fact]
    public void Row_tag_condition_reads_the_schema_bit_without_an_effect_when_unset()
    {
        var world = new World(seed: 7, valueScale: 1);
        var prey = CreateTaggedPrey(world, 2);
        var engine = Bind(world,
            new CompiledRule("prey.spread", "prey.spread", 0,
                [RuleCondition.HasTag(RuleSubject.Row(TaggedPrey.Id, 1), "poisoned")],
                [RuleEffect.AddTag(RuleSubject.Row(TaggedPrey.Id, 0), "poisoned")]));

        var skipped = engine.Dispatch(world, new SimulationFact("prey.spread", 0, 0));
        Assert.Equal(0, skipped.RulesMatched);
        Assert.False(prey.HasTag(0, "poisoned"));

        prey.AddTag(1, "poisoned");
        var spread = engine.Dispatch(world, new SimulationFact("prey.spread", 0, 0));

        Assert.Equal(1, spread.EffectsApplied);
        Assert.True(prey.HasTag(0, "poisoned"));
    }

    [Fact]
    public void Row_tag_effects_roll_back_with_the_dispatch_transaction()
    {
        var world = new World(seed: 7, valueScale: 1);
        var prey = CreateTaggedPrey(world, 2);
        var engine = Bind(world,
            new CompiledRule("prey.envenom", "prey.envenom", 0, [],
                [RuleEffect.AddTag(RuleSubject.Row(TaggedPrey.Id, 0), "poisoned")]));

        var result = engine.DispatchBatch(world,
            [new SimulationFact("prey.envenom", 0, 0), new SimulationFact("poison", 0, 0)],
            (fact, emitter) =>
            {
                if (fact.Type == "poison")
                {
                    Assert.False(emitter.Enqueue(new SimulationFact("invalid", -1, 0)));
                }
            });

        Assert.True(result.RolledBack);
        Assert.False(prey.HasTag(0, "poisoned"));
    }

    [Fact]
    public void Row_tag_dispatch_does_not_allocate_once_warmed()
    {
        var world = new World(seed: 7, valueScale: 1);
        var prey = CreateTaggedPrey(world, 4);
        var engine = Bind(world,
            new CompiledRule("prey.envenom", "prey.envenom", 0,
                [RuleCondition.HasTag(RuleSubject.Row(TaggedPrey.Id, 1), "poisoned")],
                [RuleEffect.AddTag(RuleSubject.Row(TaggedPrey.Id, 2), "poisoned")]));
        SimulationFact[] facts = [new("prey.envenom", 0, 0)];
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
        Assert.True(allocated == 0, $"Row tag dispatch allocated {allocated} bytes over 100 dispatches.");
        Assert.False(prey.HasTag(2, "poisoned"));
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

    private static RuleEngine Bind(World world, CompiledRule rule) =>
        Bind(world, [rule]);

    private static RuleEngine Bind(World world, params CompiledRule[] rules)
    {
        var engine = new RuleEngine(new CompiledRuleSet(rules));
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

    [Fact]
    public void Row_modifier_effect_changes_the_effective_value_and_not_the_column()
    {
        var world = new World(seed: 7, valueScale: 1);
        var prey = CreateBuffPrey(world, 3);
        var engine = Bind(world,
            new CompiledRule("prey.drain", "prey.drain", 0, [],
                [RuleEffect.SetModifier(RuleSubject.Row(BuffPrey.Id, 1), "test.buff.health",
                    ModifierOperation.Add, -60)]));

        var result = engine.Dispatch(world, new SimulationFact("prey.drain", 0, 0));

        Assert.Equal(1, result.EffectsApplied);
        Assert.Equal(40, prey.EffectiveValue(1, "test.buff.health"));
        Assert.Equal(100, prey.EffectiveValue(0, "test.buff.health"));
        Assert.Equal([100, 100, 100], ReadBuffHealth(prey));
    }

    [Fact]
    public void Row_condition_reads_the_effective_value_so_a_modifier_changes_the_match()
    {
        var world = new World(seed: 7, valueScale: 1);
        var prey = CreateBuffPrey(world, 2);
        var engine = Bind(world,
            new CompiledRule("prey.weaken", "prey.weaken", 0,
                [RuleCondition.PropertyAtLeast(RuleSubject.Row(BuffPrey.Id, 1), "test.buff.health", 50)],
                [RuleEffect.AdjustProperty(RuleSubject.Row(BuffPrey.Id, 1), "test.buff.health", -1)]));

        Assert.Equal(1, engine.Dispatch(world, new SimulationFact("prey.weaken", 0, 0)).RulesMatched);

        prey.SetModifier(1, "test.buff.health", -60);
        var drained = engine.Dispatch(world, new SimulationFact("prey.weaken", 0, 0));

        Assert.Equal(0, drained.RulesMatched);
        Assert.Equal(0, drained.EffectsApplied);
    }

    [Fact]
    public void Row_modifier_effects_roll_back_with_the_dispatch_transaction()
    {
        var world = new World(seed: 7, valueScale: 1);
        var prey = CreateBuffPrey(world, 2);
        var engine = Bind(world,
            new CompiledRule("prey.drain", "prey.drain", 0, [],
                [RuleEffect.SetModifier(RuleSubject.Row(BuffPrey.Id, 0), "test.buff.health",
                    ModifierOperation.Add, -60)]));

        var result = engine.DispatchBatch(world,
            [new SimulationFact("prey.drain", 0, 0), new SimulationFact("poison", 0, 0)],
            (fact, emitter) =>
            {
                if (fact.Type == "poison")
                {
                    Assert.False(emitter.Enqueue(new SimulationFact("invalid", -1, 0)));
                }
            });

        Assert.True(result.RolledBack);
        Assert.Equal(100, prey.EffectiveValue(0, "test.buff.health"));
    }

    [Fact]
    public void Row_modifier_on_an_undeclared_property_fails_at_bind_with_the_declared_list_named()
    {
        var world = new World(seed: 7, valueScale: 1);
        CreateBuffPrey(world, 1);
        var engine = new RuleEngine(new CompiledRuleSet([
            new CompiledRule("prey.drain", "prey.drain", 0, [],
                [RuleEffect.SetModifier(RuleSubject.Row(BuffPrey.Id, 0), "test.buff.stamina",
                    ModifierOperation.Add, -1)])]));

        var error = Assert.Throws<InvalidOperationException>(() => engine.Bind(world));

        Assert.Contains("test.buff.stamina", error.Message, StringComparison.Ordinal);
        Assert.Contains("test.buff.health", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Row_modifier_operation_other_than_add_fails_at_bind()
    {
        var world = new World(seed: 7, valueScale: 1);
        CreateBuffPrey(world, 1);
        var engine = new RuleEngine(new CompiledRuleSet([
            new CompiledRule("prey.mark", "prey.mark", 0, [],
                [RuleEffect.SetModifier(RuleSubject.Row(BuffPrey.Id, 0), "test.buff.health",
                    ModifierOperation.Multiply, 2)])]));

        var error = Assert.Throws<InvalidOperationException>(() => engine.Bind(world));

        Assert.Contains("Multiply", error.Message, StringComparison.Ordinal);
        Assert.Contains("Add", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Row_modifier_dispatch_does_not_allocate_once_warmed()
    {
        var world = new World(seed: 7, valueScale: 1);
        var prey = CreateBuffPrey(world, 2);
        var engine = Bind(world,
            new CompiledRule("prey.drain", "prey.drain", 0, [],
                [RuleEffect.SetModifier(RuleSubject.Row(BuffPrey.Id, 0), "test.buff.health",
                    ModifierOperation.Add, -1, durationTicks: 4)]));
        SimulationFact[] facts = [new("prey.drain", 0, 0)];
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
        Assert.True(allocated == 0, $"Row modifier dispatch allocated {allocated} bytes over 100 dispatches.");
        Assert.Equal(99, prey.EffectiveValue(0, "test.buff.health"));
    }

    [Fact]
    public void Fact_row_rule_writes_the_row_the_fact_names()
    {
        var world = new World(seed: 7, valueScale: 1);
        var prey = CreateHandledPrey(world, 3);
        var engine = Bind(world,
            new CompiledRule("prey.hurt", "prey.hurt", 0, [],
                [RuleEffect.AdjustProperty(RuleSubject.FactRow(HandledPrey.Id), "test.handled.health", -5)]));

        var result = engine.Dispatch(world, SimulationFact.ForRow("prey.hurt", prey.ReferenceOf(1)));

        Assert.Equal(1, result.EffectsApplied);
        Assert.Equal([100, 95, 100], ReadHandledHealth(prey));
    }

    [Fact]
    public void Fact_row_condition_reads_the_row_the_fact_names()
    {
        var world = new World(seed: 7, valueScale: 1);
        var prey = CreateHandledPrey(world, 2);
        var engine = Bind(world,
            new CompiledRule("prey.hurt", "prey.hurt", 0,
                [RuleCondition.PropertyAtLeast(RuleSubject.FactRow(HandledPrey.Id), "test.handled.health", 50)],
                [RuleEffect.AddTag(RuleSubject.FactRow(HandledPrey.Id), "poisoned")]));

        Assert.Equal(1, engine.Dispatch(world, SimulationFact.ForRow("prey.hurt", prey.ReferenceOf(0))).RulesMatched);

        SetHandledHealth(prey, 1, 10);
        var skipped = engine.Dispatch(world, SimulationFact.ForRow("prey.hurt", prey.ReferenceOf(1)));

        Assert.Equal(0, skipped.RulesMatched);
        Assert.True(prey.HasTag(0, "poisoned"));
        Assert.False(prey.HasTag(1, "poisoned"));
    }

    [Fact]
    public void Fact_row_on_a_despawned_row_is_skipped_instead_of_retargeting_the_new_occupant()
    {
        var world = new World(seed: 7, valueScale: 1);
        var prey = CreateHandledPrey(world, 3);
        var engine = Bind(world,
            new CompiledRule("prey.hurt", "prey.hurt", 0, [],
                [RuleEffect.AdjustProperty(RuleSubject.FactRow(HandledPrey.Id), "test.handled.health", -5)]));
        var reference = prey.ReferenceOf(2);

        prey.RemoveSwapBack(2);
        prey.AddRange(1);
        SetHandledHealth(prey, 2, 100);

        var result = engine.Dispatch(world, SimulationFact.ForRow("prey.hurt", reference));

        Assert.Equal(1, result.FactsProcessed);
        // The effect is accepted by a matched rule and has nowhere to land, exactly like an effect
        // aimed at a despawned entity; the point is that the new occupant is untouched.
        Assert.Equal([100, 100, 100], ReadHandledHealth(prey));
        Assert.False(prey.HasTag(2, "poisoned"));
    }

    [Fact]
    public void A_despawn_during_a_reaction_chain_leaves_the_new_occupant_alone()
    {
        var world = new World(seed: 7, valueScale: 1);
        var prey = CreateHandledPrey(world, 3);
        var engine = Bind(world,
            new CompiledRule("prey.hit", "prey.hit", 1, [], [RuleEffect.Emit("prey.hurt")]),
            new CompiledRule("prey.hurt", "prey.hurt", 0, [],
                [RuleEffect.AdjustProperty(RuleSubject.FactRow(HandledPrey.Id), "test.handled.health", -5)]));

        var result = engine.Dispatch(world, SimulationFact.ForRow("prey.hit", prey.ReferenceOf(1)),
            (fact, _) =>
            {
                if (fact.Type != "prey.hurt")
                {
                    return;
                }

                // The row the chain is about is gone; the row that moves into its place and the fresh
                // row that takes the freed handle slot keep their own values.
                Assert.NotNull(fact.Row);
                prey.RemoveSwapBack(1);
                prey.AddRange(1);
                SetHandledHealth(prey, 1, 50);
                SetHandledHealth(prey, 2, 100);
            });

        Assert.Equal(2, result.FactsProcessed);
        Assert.Equal([100, 50, 100], ReadHandledHealth(prey));
    }

    private static Population<HandledPrey> CreateHandledPrey(World world, int count)
    {
        var population = world.DefinePopulation<HandledPrey>(enableHandles: true);
        var health = population.Column(s_handledHealthKey);
        population.Reserve(count);
        population.AddRange(count);
        population.WithAccess(new PopulationRange(0, count), health,
            static (PopulationColumn<int> column, scoped ref PopulationAccessScope<HandledPrey> scope) =>
            {
                var values = scope.EditRange(column);
                for (var row = 0; row < values.Length; row++)
                {
                    values[row] = 100;
                }
            });
        return population;
    }

    private static int[] ReadHandledHealth(Population<HandledPrey> population)
    {
        var values = new int[population.Count];
        population.WithAccess(new PopulationRange(0, values.Length),
            (Column: population.Column(s_handledHealthKey), Values: values),
            static ((PopulationColumn<int> Column, int[] Values) state, scoped ref PopulationAccessScope<HandledPrey> scope) =>
            {
                var health = scope.ReadAll(state.Column);
                for (var row = 0; row < state.Values.Length; row++)
                {
                    state.Values[row] = health[row];
                }
            });
        return values;
    }

    private static void SetHandledHealth(Population<HandledPrey> population, int row, int value) =>
        population.WithAccess(new PopulationRange(row, 1), (Column: population.Column(s_handledHealthKey), Value: value),
            static ((PopulationColumn<int> Column, int Value) state, scoped ref PopulationAccessScope<HandledPrey> scope) =>
                scope.EditRange(state.Column)[0] = state.Value);

    private static Population<BuffPrey> CreateBuffPrey(World world, int count)
    {
        var population = world.DefinePopulation<BuffPrey>();
        var health = population.Column(s_buffHealthKey);
        population.Reserve(count);
        population.AddRange(count);
        population.WithAccess(new PopulationRange(0, count), health,
            static (PopulationColumn<int> column, scoped ref PopulationAccessScope<BuffPrey> scope) =>
            {
                var values = scope.EditRange(column);
                for (var row = 0; row < values.Length; row++)
                {
                    values[row] = 100;
                }
            });
        return population;
    }

    private static int[] ReadBuffHealth(Population<BuffPrey> population)
    {
        var values = new int[population.Count];
        population.WithAccess(new PopulationRange(0, values.Length), (Column: population.Column(s_buffHealthKey), Values: values),
            static ((PopulationColumn<int> Column, int[] Values) state, scoped ref PopulationAccessScope<BuffPrey> scope) =>
            {
                var health = scope.ReadAll(state.Column);
                for (var row = 0; row < state.Values.Length; row++)
                {
                    state.Values[row] = health[row];
                }
            });
        return values;
    }

    private static Population<TaggedPrey> CreateTaggedPrey(World world, int count)
    {
        var population = world.DefinePopulation<TaggedPrey>();
        population.Reserve(count);
        population.AddRange(count);
        return population;
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

    private readonly struct HandledPrey : IPopulationKind
    {
        public static string Id => "test.handled-prey";

        public static int Version => 1;

        public static string[] Tags => ["poisoned"];
    }

    private readonly struct BuffPrey : IPopulationKind
    {
        public static string Id => "test.buff-prey";

        public static int Version => 1;

        public static string[] ModifierProperties => ["test.buff.health"];
    }

    private readonly struct TaggedPrey : IPopulationKind
    {
        public static string Id => "test.tagged-prey";

        public static int Version => 1;

        public static string[] Tags => ["poisoned", "feral"];
    }
}
