using Systemic.Kernel;

namespace Systemic.Tests.Kernel;

/// <summary>
/// Row modifiers are a fixed inline slot block, so a row pays for them once and never allocates to
/// gain one. The slot key comes from the schema, which is what makes a saved block readable.
/// </summary>
public sealed class PopulationModifierTests
{
    private static readonly PopulationColumnKey<int> s_healthKey =
        new("test.modifier.health", 1, PopulationCodecs.Int32);

    [Fact]
    public void A_modifier_changes_the_effective_value_and_not_the_column()
    {
        var world = new World(seed: 61, valueScale: 1);
        var population = Create(world, 2);

        Assert.True(population.SetModifier(1, "test.modifier.health", -25));

        Assert.Equal(100, population.EffectiveValue(0, "test.modifier.health"));
        Assert.Equal(75, population.EffectiveValue(1, "test.modifier.health"));
        Assert.Equal(100, ReadHealth(population)[1]);
    }

    [Fact]
    public void Several_modifiers_on_one_property_sum_in_slot_order()
    {
        var world = new World(seed: 62, valueScale: 1);
        var population = Create(world, 1);

        population.SetModifier(0, "test.modifier.health", 10);
        population.SetModifier(0, "test.modifier.armor", 5);

        Assert.Equal(110, population.EffectiveValue(0, "test.modifier.health"));
        Assert.Equal(5, population.EffectiveValue(0, "test.modifier.armor"));
    }

    [Fact]
    public void Setting_the_same_property_again_replaces_that_row_modifier()
    {
        var world = new World(seed: 63, valueScale: 1);
        var population = Create(world, 1);

        population.SetModifier(0, "test.modifier.health", 10);
        population.SetModifier(0, "test.modifier.health", 40);

        Assert.Equal(140, population.EffectiveValue(0, "test.modifier.health"));
    }

    [Fact]
    public void A_modifier_expires_at_its_absolute_tick()
    {
        var world = new World(seed: 64, valueScale: 1);
        var population = Create(world, 1);

        population.SetModifier(0, "test.modifier.health", 10, remainingTicks: 1);
        Assert.Equal(110, population.EffectiveValue(0, "test.modifier.health"));

        world.LogicalTick = 1;

        Assert.Equal(100, population.EffectiveValue(0, "test.modifier.health"));
    }

    [Fact]
    public void An_expired_slot_is_reused_instead_of_failing()
    {
        var world = new World(seed: 65, valueScale: 1);
        var population = Create(world, 1);
        // The schema declares eight properties, which is exactly the row's slot capacity.
        for (var index = 0; index < Population<BuffPrey>.MaximumRowModifiers; index++)
        {
            population.SetModifier(0, $"test.modifier.p{index}", 1, remainingTicks: 1);
        }

        world.LogicalTick = 1;
        Assert.True(population.SetModifier(0, "test.modifier.health", 7));

        Assert.Equal(107, population.EffectiveValue(0, "test.modifier.health"));
    }

    [Fact]
    public void A_row_with_no_free_slot_fails_loudly_instead_of_dropping_the_modifier()
    {
        var world = new World(seed: 66, valueScale: 1);
        var population = Create(world, 1);
        for (var index = 0; index < Population<BuffPrey>.MaximumRowModifiers; index++)
        {
            population.SetModifier(0, $"test.modifier.p{index}", 1);
        }

        var error = Assert.Throws<InvalidOperationException>(() =>
            population.SetModifier(0, "test.modifier.health", 1));

        Assert.Contains("Row 0", error.Message, StringComparison.Ordinal);
        Assert.Contains(BuffPrey.Id, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Modifier_writes_and_expired_reads_do_not_allocate()
    {
        var world = new World(seed: 67, valueScale: 1);
        var population = Create(world, 100_000);
        var expiry = 1_000_000;
        world.LogicalTick = expiry;

        for (var row = 0; row < population.Count; row += 97)
        {
            population.SetModifier(row, "test.modifier.health", 1);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var row = 0; row < population.Count; row += 97)
        {
            population.SetModifier(row, "test.modifier.health", 2);
            _ = population.EffectiveValue(row, "test.modifier.health");
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"Row modifier writes allocated {allocated} bytes for 100k rows.");
    }

    [Fact]
    public void A_modifier_write_rolls_back_with_the_enclosing_transaction()
    {
        var world = new World(seed: 68, valueScale: 1);
        var population = Create(world, 1);

        using (var transaction = world.BeginTransaction())
        {
            population.SetModifier(0, "test.modifier.health", -50);
            Assert.Equal(50, population.EffectiveValue(0, "test.modifier.health"));
            transaction.Rollback();
        }

        Assert.Equal(100, population.EffectiveValue(0, "test.modifier.health"));
    }

    [Fact]
    public void Modifier_slots_round_trip_through_the_population_save_state()
    {
        var source = new World(seed: 69, valueScale: 1);
        var sourcePopulation = Create(source, 2);
        sourcePopulation.SetModifier(0, "test.modifier.health", -10);
        sourcePopulation.SetModifier(1, "test.modifier.armor", 3);
        var state = source.CapturePopulationState();

        var target = new World(seed: 69, valueScale: 1);
        var targetPopulation = Create(target, 0);
        target.RestorePopulationState(state);

        Assert.Equal(2, targetPopulation.Count);
        Assert.Equal(90, targetPopulation.EffectiveValue(0, "test.modifier.health"));
        Assert.Equal(3, targetPopulation.EffectiveValue(1, "test.modifier.armor"));
        Assert.Equal(100, targetPopulation.EffectiveValue(1, "test.modifier.health"));
    }

    [Fact]
    public void An_undeclared_modifier_property_names_the_schema_and_its_declared_properties()
    {
        var world = new World(seed: 70, valueScale: 1);
        var population = Create(world, 1);

        var error = Assert.Throws<InvalidOperationException>(() =>
            population.SetModifier(0, "test.modifier.stamina", 1));

        Assert.Contains(BuffPrey.Id, error.Message, StringComparison.Ordinal);
        Assert.Contains("test.modifier.stamina", error.Message, StringComparison.Ordinal);
        Assert.Contains("test.modifier.health", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_finite_modifier_value_is_rejected()
    {
        var world = new World(seed: 71, valueScale: 1);
        var population = Create(world, 1);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            population.SetModifier(0, "test.modifier.health", double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            population.SetModifier(0, "test.modifier.health", double.MaxValue));
    }

    [Fact]
    public void A_schema_without_modifier_properties_creates_no_slot_column()
    {
        var world = new World(seed: 72, valueScale: 1);
        var population = world.DefinePopulation<PlainPrey>();

        Assert.True(population.ModifierProperties.IsEmpty);
        Assert.Null(population.ModifierColumn);
        Assert.Throws<InvalidOperationException>(() =>
            population.SetModifier(0, "test.modifier.health", 1));
    }

    private static Population<BuffPrey> Create(World world, int count)
    {
        var population = world.DefinePopulation<BuffPrey>();
        var health = population.Column(s_healthKey);
        var armor = population.Column(s_armorKey);
        population.Reserve(count);
        population.AddRange(count);
        population.WithAccess(new PopulationRange(0, count), (Health: health, Armor: armor),
            static ((PopulationColumn<int> Health, PopulationColumn<int> Armor) columns,
                scoped ref PopulationAccessScope<BuffPrey> scope) =>
            {
                var health = scope.EditRange(columns.Health);
                var armor = scope.EditRange(columns.Armor);
                for (var row = 0; row < health.Length; row++)
                {
                    health[row] = 100;
                    armor[row] = 0;
                }
            });
        return population;
    }

    private static readonly PopulationColumnKey<int> s_armorKey =
        new("test.modifier.armor", 1, PopulationCodecs.Int32);

    private static int[] ReadHealth(Population<BuffPrey> population)
    {
        var values = new int[population.Count];
        population.WithAccess(new PopulationRange(0, values.Length), (Column: population.Column(s_healthKey), Values: values),
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

    private readonly struct BuffPrey : IPopulationKind
    {
        public static string Id => "test.modifier-prey";

        public static int Version => 1;

        public static string[] ModifierProperties =>
        [
            "test.modifier.health",
            "test.modifier.armor",
            "test.modifier.p0",
            "test.modifier.p1",
            "test.modifier.p2",
            "test.modifier.p3",
            "test.modifier.p4",
            "test.modifier.p5",
            "test.modifier.p6",
            "test.modifier.p7",
        ];
    }

    private readonly struct PlainPrey : IPopulationKind
    {
        public static string Id => "test.modifier-plain-prey";

        public static int Version => 1;
    }
}
