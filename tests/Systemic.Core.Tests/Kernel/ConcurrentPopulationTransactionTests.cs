using System.Reflection;
using System.Runtime.ExceptionServices;
using Systemic.Kernel;

namespace Systemic.Tests.Kernel;

public sealed class ConcurrentPopulationTransactionTests
{
    private static readonly PopulationColumnKey<int> s_first = new("concurrent.first", 1, PopulationCodecs.Int32);
    private static readonly PopulationColumnKey<int> s_second = new("concurrent.second", 1, PopulationCodecs.Int32);
    private static readonly PopulationColumnKey<int> s_third = new("concurrent.third", 1, PopulationCodecs.Int32);
    private static readonly PopulationColumnKey<int> s_fourth = new("concurrent.fourth", 1, PopulationCodecs.Int32);

    [Fact]
    public void Concurrent_disjoint_ranges_detach_a_cloned_buffer_once_without_a_transaction()
    {
        const int count = 131_075;
        const int workers = 8;
        var world = new World(8, 1);
        var population = world.DefinePopulation<ConcurrentKind>();
        var column = population.Column(s_first);
        population.AddRange(count);
        column.EditRange(0, count).Fill(5);
        var clone = world.Clone();
        using var barrier = new Barrier(workers);
        var failures = new Exception?[workers];
        var threads = new Thread[workers];
        for (var index = 0; index < workers; index++)
        {
            var worker = index;
            threads[index] = new Thread(() =>
            {
                try
                {
                    Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(5)));
                    var start = worker * count / workers;
                    var end = (worker + 1) * count / workers;
                    column.EditMemory(start, end - start).Span.Fill(9);
                }
                catch (Exception exception)
                {
                    failures[worker] = exception;
                }
            });
            threads[index].Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        Assert.All(failures, Assert.Null);
        AssertValues(column.ReadOnlySpan, 9);
        AssertValues(clone.GetPopulation<ConcurrentKind>().Column(s_first).ReadOnlySpan, 5);
    }

    [Fact]
    public void Disjoint_ranges_on_a_cloned_column_write_the_same_detached_buffer_and_roll_back()
    {
        const int count = 131_075; // Range boundaries also split undo pages.
        var world = new World(1, 1);
        var population = world.DefinePopulation<ConcurrentKind>();
        var column = population.Column(s_first);
        population.AddRange(count);
        column.EditRange(0, count).Fill(3);
        var clone = world.Clone();
        using var barrier = new Barrier(8);
        using var schedule = new DeterministicSystemScheduleBuilder(8)
            .Add(new TestSystem("range", [SystemAccess.WriteColumn<ConcurrentKind, int>(s_first)], context =>
                context.ForEachRange((column, barrier), count, static (state, _, range) =>
                {
                    Assert.True(state.barrier.SignalAndWait(TimeSpan.FromSeconds(5)));
                    state.column.EditRange(range.Start, range.Length).Fill(7);
                })))
            .Build();

        using (var outer = world.BeginTransaction())
        {
            schedule.Execute(world, 1);
            AssertValues(column.ReadOnlySpan, 7);
            outer.Rollback();
        }

        AssertValues(column.ReadOnlySpan, 3);
        AssertValues(clone.GetPopulation<ConcurrentKind>().Column(s_first).ReadOnlySpan, 3);
    }

    [Fact]
    public void Independent_column_systems_keep_every_undo_record_for_outer_rollback()
    {
        const int count = 8_192;
        for (var trial = 0; trial < 25; trial++)
        {
            var world = new World(2, 1);
            var population = world.DefinePopulation<ConcurrentKind>();
            population.AddRange(count);
            var keys = new[] { s_first, s_second, s_third, s_fourth };
            var columns = keys.Select(population.Column).ToArray();
            using var barrier = new Barrier(columns.Length);
            var builder = new DeterministicSystemScheduleBuilder(columns.Length);
            for (var index = 0; index < columns.Length; index++)
            {
                var column = columns[index];
                var value = index + 1;
                builder.Add(new TestSystem($"column-{index}", [SystemAccess.WriteColumn<ConcurrentKind, int>(keys[index])], _ =>
                {
                    Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(5)));
                    column.EditMemory(0, count).Span.Fill(value);
                }));
            }

            using var schedule = builder.Build();
            using (var outer = world.BeginTransaction())
            {
                schedule.Execute(world, trial);
                for (var index = 0; index < columns.Length; index++)
                {
                    AssertValues(columns[index].ReadOnlySpan, index + 1);
                }

                outer.Rollback();
            }

            foreach (var column in columns)
            {
                AssertValues(column.ReadOnlySpan, 0);
            }
        }
    }

    [Fact]
    public void Concurrent_page_edits_followed_by_full_capture_keep_outer_and_inner_rollback_states()
    {
        const int count = 8_195;
        var world = new World(3, 1);
        var population = world.DefinePopulation<ConcurrentKind>();
        var column = population.Column(s_first);
        population.AddRange(count);
        column.EditRange(0, count).Fill(1);
        using var barrier = new Barrier(8);
        using var schedule = new DeterministicSystemScheduleBuilder(8)
            .Add(new TestSystem("pages", [SystemAccess.WriteColumn<ConcurrentKind, int>(s_first)], context =>
                context.ForEachRange((column, barrier, value: context.Tick + 1), count, static (state, _, range) =>
                {
                    Assert.True(state.barrier.SignalAndWait(TimeSpan.FromSeconds(5)));
                    state.column.EditRange(range.Start, range.Length).Fill(state.value);
                })))
            .Build();

        using (var outer = world.BeginTransaction())
        {
            column.EditRange(0, 1)[0] = 9;
            schedule.Execute(world, 1); // The schedule commits its nested transaction.
            using (var inner = world.BeginTransaction())
            {
                schedule.Execute(world, 2);
                AssertValues(column.ReadOnlySpan, 3);
                inner.Rollback();
            }

            AssertValues(column.ReadOnlySpan, 2);
            using (var inner = world.BeginTransaction())
            {
                column.EditRange(0, count).Fill(4);
                inner.Rollback();
            }

            AssertValues(column.ReadOnlySpan, 2);
            column.EditRange(0, count).Fill(5); // Captured pages must win over a later full buffer.
            outer.Rollback();
        }

        AssertValues(column.ReadOnlySpan, 1);
    }

    [Fact]
    public void Parallel_double_buffer_edits_and_swaps_restore_both_physical_buffers()
    {
        const int count = 8_195;
        var world = new World(4, 1);
        var population = world.DefinePopulation<ConcurrentKind>();
        var column = population.DoubleBufferedColumn(s_first);
        population.AddRange(count);
        var initial = column.EditBothBuffers(0, count);
        initial.Current.Fill(1);
        initial.Next.Fill(2);
        var clone = world.Clone();
        using var barrier = new Barrier(8);
        using var schedule = new DeterministicSystemScheduleBuilder(8)
            .Add(new TestSystem("double", [SystemAccess.WriteColumn<ConcurrentKind, int>(s_first)], context =>
                context.ForEachRange((column, barrier), count, static (state, _, range) =>
                {
                    Assert.True(state.barrier.SignalAndWait(TimeSpan.FromSeconds(5)));
                    state.column.EditNextMemory(range.Start, range.Length).Span.Fill(8);
                })))
            .Build();

        using (var outer = world.BeginTransaction())
        {
            schedule.Execute(world, 1);
            column.Swap();
            schedule.Execute(world, 2);
            column.Swap();
            using (var inner = world.BeginTransaction())
            {
                var both = column.EditBothBuffers(0, count);
                both.Current.Fill(6);
                both.Next.Fill(7);
                inner.Rollback();
            }

            AssertValues(column.Current, 8);
            outer.Rollback();
        }

        AssertValues(column.Current, 1);
        column.Swap();
        AssertValues(column.Current, 2);
        var cloned = clone.GetPopulation<ConcurrentKind>().DoubleBufferedColumn(s_first);
        AssertValues(cloned.Current, 1);
        cloned.Swap();
        AssertValues(cloned.Current, 2);
    }

    [Fact]
    public void Failed_page_capture_does_not_replace_the_original_exception_during_cleanup()
    {
        var world = new World(5, 1);
        var population = world.DefinePopulation<ConcurrentKind>();
        var column = population.Column(s_first);
        population.AddRange(1);
        var field = typeof(PopulationColumnBase).GetField("_freePageUndoBatches",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var pool = (Stack<PopulationPageUndoBatch>)field.GetValue(column)!;
        pool.Push(null!); // Fault injection: a damaged pool makes both capture and cleanup fail.
        using var transaction = world.BeginTransaction();
        Exception? firstFailure = null;
        var thread = Environment.CurrentManagedThreadId;
        EventHandler<FirstChanceExceptionEventArgs> listener = (_, args) =>
        {
            if (Environment.CurrentManagedThreadId == thread && args.Exception is NullReferenceException)
            {
                firstFailure ??= args.Exception;
            }
        };

        AppDomain.CurrentDomain.FirstChanceException += listener;
        NullReferenceException thrown;
        try
        {
            thrown = Assert.Throws<NullReferenceException>(() => column.EditRange(0, 1));
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= listener;
        }

        Assert.NotNull(firstFailure);
        Assert.Same(firstFailure, thrown);
    }

    [Fact]
    public void Closing_a_reused_child_scope_does_not_let_an_old_handle_close_the_new_scope()
    {
        var world = new World(6, 1);
        var population = world.DefinePopulation<ConcurrentKind>();
        var column = population.Column(s_first);
        population.AddRange(1);
        using var outer = world.BeginTransaction();
        var first = world.BeginTransaction();
        column.EditRange(0, 1)[0] = 1;
        first.Commit();
        using (var second = world.BeginTransaction())
        {
            column.EditRange(0, 1)[0] = 2;
            Assert.False(first.HasChanges);
            first.Dispose();
            Assert.Equal(2, column.ReadOnlySpan[0]);
            Assert.Throws<InvalidOperationException>(() => first.OnRollback(() => { }));
            second.Commit();
        }

        outer.Rollback();
        Assert.Equal(0, column.ReadOnlySpan[0]);
    }

    [Fact]
    public void Rollback_callback_can_wait_for_an_independent_transaction_to_finish()
    {
        var world = new World(7, 1);
        using var transaction = world.BeginTransaction();
        transaction.OnRollback(() =>
        {
            Exception? failure = null;
            var worker = new Thread(() =>
            {
                try
                {
                    using var next = world.BeginTransaction();
                    next.Commit();
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            })
            { IsBackground = true };
            worker.Start();
            Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
            Assert.Null(failure);
        });

        transaction.Rollback();
    }

    private static void AssertValues(ReadOnlySpan<int> values, int expected)
    {
        for (var row = 0; row < values.Length; row++)
        {
            Assert.Equal(expected, values[row]);
        }
    }

    private readonly struct ConcurrentKind : IPopulationKind
    {
        public static string Id => "test.concurrent-transaction";
        public static int Version => 1;
    }

    private sealed class TestSystem(string id, IReadOnlyList<SystemAccess> access,
        Action<ScheduledSystemContext> execute) : IScheduledKernelSystem
    {
        public string Id => id;
        public IReadOnlyList<SystemAccess> Access => access;
        public void Execute(ScheduledSystemContext context) => execute(context);
    }
}
