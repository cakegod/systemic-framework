using Systemic.Kernel;

namespace Systemic.Tests.Kernel;

public sealed class DeferredPopulationScheduleTests
{
    private static readonly PopulationColumnKey<int> s_key = new("deferred.value", 1, PopulationCodecs.Int32);

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Barrier_combines_original_row_removals_and_spawns_in_system_and_partition_order(int workers)
    {
        var world = new World(1, 1);
        var population = world.DefinePopulation<Kind>(enableHandles: true);
        var column = population.Column(s_key);
        population.AddRange(4);
        new[] { 10, 20, 30, 40 }.CopyTo(column.EditRange(0, 4));
        var kept = population.GetHandle(2);
        var builder = new DeterministicSystemScheduleBuilder(workers);
        builder.Add(new TestSystem("first", context =>
        {
            var partitions = context.DeferPopulationCommands(population, 2);
            partitions[0].RemoveRow(0);
            partitions[0].Spawn([new Kind(50)]);
            partitions[1].Spawn([new Kind(55)]);
        }));
        builder.Add(new TestSystem("second", context =>
        {
            var partitions = context.DeferPopulationCommands(population, 1);
            partitions[0].RemoveRow(1);
            partitions[0].RemoveRow(1);
            partitions[0].Spawn([new Kind(60)]);
        }));
        using var schedule = builder.Build();

        schedule.Execute(world, 1);

        Assert.Equal(new[] { 30, 40, 50, 55, 60 }, column.ReadOnlySpan.ToArray());
        Assert.True(population.TryResolve(kept, out var keptRow));
        Assert.Equal(0, keptRow);
    }

    [Fact]
    public void Invalid_deferred_batch_rolls_back_the_whole_tick_and_expires_its_buffers()
    {
        var world = new World(2, 1);
        var population = world.DefinePopulation<Kind>();
        var column = population.Column(s_key);
        population.AddRange(2);
        column.EditRange(0, 2).Fill(3);
        PopulationCommandBuffer<Kind>? captured = null;
        using var schedule = new DeterministicSystemScheduleBuilder(2)
            .Add(new TestSystem("one", context =>
            {
                var parts = context.DeferPopulationCommands(population, 1);
                captured = parts[0];
                captured.RemoveRow(0);
                captured.Spawn([new Kind(-1)]); // Schema rejects this after removals.
            }))
            .Build();

        Assert.Throws<InvalidDataException>(() => schedule.Execute(world, 1));

        Assert.Equal(new[] { 3, 3 }, column.ReadOnlySpan.ToArray());
        Assert.Equal(2, population.Count);
        Assert.Throws<InvalidOperationException>(() => captured!.RemoveRow(0));
        Assert.Throws<InvalidOperationException>(() => captured!.Flush());
    }

    [Fact]
    public void Deferred_buffers_cannot_be_flushed_before_the_barrier()
    {
        var world = new World(3, 1);
        var population = world.DefinePopulation<Kind>();
        using var schedule = new DeterministicSystemScheduleBuilder(1)
            .Add(new TestSystem("one", context =>
            {
                var buffer = context.DeferPopulationCommands(population, 1)[0];
                buffer.Spawn([new Kind(4)]);
                Assert.Throws<InvalidOperationException>(() => buffer.Flush());
            }))
            .Build();

        schedule.Execute(world, 1);
        Assert.Equal(4, population.Column(s_key).ReadOnlySpan[0]);
    }

    [Fact]
    public void Deferred_population_commands_require_a_world_write_declaration()
    {
        var world = new World(4, 1);
        var population = world.DefinePopulation<Kind>();
        using var schedule = new DeterministicSystemScheduleBuilder(1)
            .Add(new TestSystem("undeclared", context => context.DeferPopulationCommands(population, 1), []))
            .Build();

        Assert.Throws<InvalidOperationException>(() => schedule.Execute(world, 1));
        Assert.Equal(0, population.Count);
    }

    [Fact]
    public void Partition_callbacks_cannot_register_new_deferred_buffers()
    {
        var world = new World(5, 1);
        var population = world.DefinePopulation<Kind>();
        using var schedule = new DeterministicSystemScheduleBuilder(2)
            .Add(new TestSystem("partition", context =>
                context.ForEachRange(context, 2, (owner, _, _) =>
                    Assert.Throws<InvalidOperationException>(() => owner.DeferPopulationCommands(population, 1)))))
            .Build();

        schedule.Execute(world, 1);
    }

    [Fact]
    public void Scheduled_leases_expire_before_deferred_structural_commands_run()
    {
        var world = new World(6, 1);
        var population = world.DefinePopulation<Kind>();
        var column = population.Column(s_key);
        population.AddRange(2);
        PopulationLease<Kind>? lease = null;
        using var schedule = new DeterministicSystemScheduleBuilder(1)
            .Add(new TestSystem("lease", context =>
            {
                lease = context.BorrowPopulation(population);
                lease.Edit(column, 1, 1)[0] = 8;
                context.DeferPopulationCommands(population, 1)[0].RemoveRow(0);
            }))
            .Build();

        schedule.Execute(world, 1);

        Assert.Equal(new[] { 8 }, column.ReadOnlySpan.ToArray());
        Assert.Throws<ObjectDisposedException>(() => { _ = lease!.Read(column); });
    }

    [Fact]
    public void Scheduled_leases_close_when_a_system_fails_so_rollback_can_complete()
    {
        var world = new World(7, 1);
        var population = world.DefinePopulation<Kind>();
        var column = population.Column(s_key);
        population.AddRange(1);
        using var schedule = new DeterministicSystemScheduleBuilder(1)
            .Add(new TestSystem("failure", context =>
            {
                var lease = context.BorrowPopulation(population);
                lease.Edit(column, 0, 1)[0] = 9;
                throw new InvalidOperationException("expected failure");
            }))
            .Build();

        Assert.Throws<InvalidOperationException>(() => schedule.Execute(world, 1));
        Assert.Equal(0, column.ReadOnlySpan[0]);
        using var next = world.BeginTransaction();
        next.Commit();
    }

    private readonly record struct Kind(int Value) : IPopulationKind, IPopulationSchema<Kind>
    {
        public static string Id => "test.deferred-population";
        public static int Version => 1;
        public static void InitializeRange(Population<Kind> population, PopulationRange range, ReadOnlySpan<Kind> values)
        {
            var destination = population.Column(s_key).EditRange(range.Start, range.Length);
            for (var index = 0; index < values.Length; index++)
            {
                if (values[index].Value < 0) throw new InvalidDataException("negative");
                destination[index] = values[index].Value;
            }
        }
    }

    private sealed class TestSystem(string id, Action<ScheduledSystemContext> execute,
        IReadOnlyList<SystemAccess>? access = null) : IScheduledKernelSystem
    {
        public string Id => id;
        public IReadOnlyList<SystemAccess> Access { get; } = access ?? [SystemAccess.DeferWorldWrites];
        public void Execute(ScheduledSystemContext context) => execute(context);
    }
}
