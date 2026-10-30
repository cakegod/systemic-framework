using Systemic.Kernel;

namespace Systemic.Tests.Kernel;

public sealed class DeferredPopulationScheduleTests
{
    private static readonly int[] s_values = [10, 20, 30, 40];

    private static readonly PopulationColumnKey<int> s_key = new("deferred.value", 1, PopulationCodecs.Int32);

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void BarrierCombinesOriginalRowRemovalsAndSpawnsInSystemAndPartitionOrder(int workers)
    {
        var world = new World(1, 1);
        var population = world.DefinePopulation<Kind>(enableHandles: true);
        var column = population.Column(s_key);
        population.AddRange(4);
        s_values.CopyTo(column.EditRange(0, 4));
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

        Assert.Equal([30, 40, 50, 55, 60], column.ReadOnlySpan.ToArray());
        Assert.True(population.TryResolve(kept, out var keptRow));
        Assert.Equal(0, keptRow);
    }

    [Fact]
    public void InvalidDeferredBatchRollsBackTheWholeTickAndExpiresItsBuffers()
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

        Assert.Equal([3, 3], column.ReadOnlySpan.ToArray());
        Assert.Equal(2, population.Count);
        Assert.Throws<InvalidOperationException>(() => captured!.RemoveRow(0));
        Assert.Throws<InvalidOperationException>(() => captured!.Flush());
    }

    [Fact]
    public void DeferredBuffersCannotBeFlushedBeforeTheBarrier()
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
    public void DeferredPopulationCommandsRequireAWorldWriteDeclaration()
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
    public void PartitionCallbacksCannotRegisterNewDeferredBuffers()
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
    public void ScheduledLeasesExpireBeforeDeferredStructuralCommandsRun()
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
                context.ForEachPopulationRange(lease, column,
                    static (PopulationColumn<int> target, int _, scoped ref PopulationAccessScope<Kind> scope) =>
                        scope.EditRange(target)[1] = 8);
                context.DeferPopulationCommands(population, 1)[0].RemoveRow(0);
            }, [SystemAccess.DeferWorldWrites, SystemAccess.WriteColumn<Kind, int>(s_key)]))
            .Build();

        schedule.Execute(world, 1);

        Assert.Equal([8], column.ReadOnlySpan.ToArray());
        Assert.Throws<ObjectDisposedException>(() => lease!.WithAccess(new PopulationRange(0, 1), column,
            static (PopulationColumn<int> target, scoped ref PopulationAccessScope<Kind> scope) =>
                _ = scope.ReadAll(target)));
    }

    [Fact]
    public void ScheduledLeasesCloseWhenASystemFailsSoRollbackCanComplete()
    {
        var world = new World(7, 1);
        var population = world.DefinePopulation<Kind>();
        var column = population.Column(s_key);
        population.AddRange(1);
        using var schedule = new DeterministicSystemScheduleBuilder(1)
            .Add(new TestSystem("failure", context =>
            {
                var lease = context.BorrowPopulation(population);
                context.ForEachPopulationRange(lease, column,
                    static (PopulationColumn<int> target, int _, scoped ref PopulationAccessScope<Kind> scope) =>
                        scope.EditRange(target)[0] = 9);
                throw new InvalidOperationException("expected failure");
            }, [SystemAccess.WriteColumn<Kind, int>(s_key)]))
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
                if (values[index].Value < 0)
                {
                    throw new InvalidDataException("negative");
                }

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
