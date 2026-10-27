using Systemic.Kernel;

namespace Systemic.Tests.Kernel;

public sealed class WorldStateTransactionTests
{
    private static readonly PopulationColumnKey<int> s_key = new("transaction.value", 1, PopulationCodecs.Int32);

    [Fact]
    public void Rollback_removes_role_and_required_bindings_added_to_existing_entities()
    {
        var world = new World(1, 1);
        var entity = world.Add(new DataEntity([], []));
        using (var transaction = world.BeginTransaction())
        {
            world.BindRole("test.rollback-role", entity);
            world.MarkRequired(entity);
            transaction.Rollback();
        }

        Assert.Null(world.Role("test.rollback-role"));
        Assert.DoesNotContain(entity.Id, world.RequiredEntityIds);
    }

    [Fact]
    public void Rollback_restores_roles_cleared_by_pruning_an_entity()
    {
        var world = new World(2, 1);
        var entity = world.Add(new DataEntity([], []));
        world.BindRole("test.pruned-role", entity);
        using (var transaction = world.BeginTransaction())
        {
            entity.ToRemove = true;
            world.PruneRemoved();
            transaction.Rollback();
        }

        Assert.Same(entity, world.ById(entity.Id));
        Assert.Same(entity, world.Role("test.pruned-role"));
    }

    [Fact]
    public void Rolling_back_a_nested_population_definition_removes_it_and_invalidates_the_reference()
    {
        var world = new World(3, 1);
        Population<Kind> created;
        using (var outer = world.BeginTransaction())
        {
            using var inner = world.BeginTransaction();
            created = world.DefinePopulation<Kind>();
            inner.Commit();
            outer.Rollback();
        }

        Assert.False(world.HasPopulations);
        Assert.Throws<InvalidOperationException>(() => _ = created.Count);
        Assert.NotNull(world.DefinePopulation<Kind>());
    }

    [Fact]
    public void Rolling_back_a_population_restore_restores_rows_values_and_both_buffers()
    {
        var world = new World(4, 1);
        var population = world.DefinePopulation<Kind>(enableHandles: true);
        var values = population.Column(s_key);
        var buffered = population.DoubleBufferedColumn(new PopulationColumnKey<int>("transaction.buffered", 1, PopulationCodecs.Int32));
        population.AddRange(1);
        values.EditRange(0, 1)[0] = 1;
        buffered.EditNext(0, 1)[0] = 10;
        buffered.Swap();
        var saved = world.CapturePopulationState();
        population.AddRange(1);
        values.EditRange(0, 2).Fill(2);
        buffered.EditNext(0, 2).Fill(30);
        var handle = population.GetHandle(1);

        using (var transaction = world.BeginTransaction())
        {
            world.RestorePopulationState(saved);
            transaction.Rollback();
        }

        Assert.Equal(2, population.Count);
        Assert.Equal([2, 2], values.ReadOnlySpan.ToArray());
        Assert.Equal([10, 0], buffered.Current.ToArray());
        buffered.Swap();
        Assert.Equal([30, 30], buffered.Current.ToArray());
        Assert.True(population.TryResolve(handle, out var row));
        Assert.Equal(1, row);
    }

    private readonly struct Kind : IPopulationKind
    {
        public static string Id => "test.transaction-kind";
        public static int Version => 1;
    }
}
