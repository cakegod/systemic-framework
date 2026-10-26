using Systemic.Kernel;

namespace Systemic.Tests.Kernel;

public sealed class PopulationTransactionTests
{
    private static readonly PopulationColumnKey<int> s_valueKey = new("transaction.values", 1, PopulationCodecs.Int32);

    [Fact]
    public void EditRangeWithinTransactionRestoresValuesOnRollback()
    {
        var world = new World(seed: 21, valueScale: 1);
        var population = world.DefinePopulation<TransactionParticleKind>();
        var values = population.Column(s_valueKey);
        population.AddRange(2_048);
        values.EditRange(0, population.Count).Fill(1);
        values.EditRange(0, 1)[0] = 10;
        values.EditRange(1_023, 1)[0] = 20;
        values.EditRange(1_024, 1)[0] = 30;

        using (var transaction = world.BeginTransaction())
        {
            values.EditRange(0, 1)[0] = -10;
            values.EditRange(1_023, 1)[0] = -20;
            values.EditRange(1_024, 1)[0] = -30;
            transaction.Rollback();
        }

        Assert.Equal(10, values.ReadOnlySpan[0]);
        Assert.Equal(20, values.ReadOnlySpan[1_023]);
        Assert.Equal(30, values.ReadOnlySpan[1_024]);
        Assert.Equal(1, values.ReadOnlySpan[2_047]);
    }

    [Fact]
    public void NestedTransactionsRestoreTheCorrectPageState()
    {
        var world = new World(seed: 22, valueScale: 1);
        var population = world.DefinePopulation<TransactionParticleKind>();
        var values = population.Column(s_valueKey);
        population.AddRange(2_048);
        values.EditRange(0, population.Count).Fill(1);
        values.EditRange(0, 1)[0] = 10;
        values.EditRange(1_024, 1)[0] = 20;

        using (var outer = world.BeginTransaction())
        {
            values.EditRange(0, 1)[0] = 100;
            using (var inner = world.BeginTransaction())
            {
                values.EditRange(0, 1)[0] = 200;
                values.EditRange(1_024, 1)[0] = 300;
                inner.Rollback();
            }

            Assert.Equal(100, values.ReadOnlySpan[0]);
            Assert.Equal(20, values.ReadOnlySpan[1_024]);
            values.EditRange(0, 1)[0] = 150;
            outer.Rollback();
        }

        Assert.Equal(10, values.ReadOnlySpan[0]);
        Assert.Equal(20, values.ReadOnlySpan[1_024]);

        using (var outer = world.BeginTransaction())
        {
            using (var inner = world.BeginTransaction())
            {
                values.EditRange(1_024, 1)[0] = 400;
                inner.Commit();
            }

            values.EditRange(1_024, 1)[0] = 500;
            outer.Rollback();
        }

        Assert.Equal(20, values.ReadOnlySpan[1_024]);
    }

    [Fact]
    public void Full_buffer_capture_preserves_sparse_and_nested_rollback_order()
    {
        var world = new World(seed: 29, valueScale: 1);
        var population = world.DefinePopulation<TransactionParticleKind>();
        var values = population.Column(s_valueKey);
        population.AddRange(2_048);
        values.EditRange(0, population.Count).Fill(1);

        using (var outer = world.BeginTransaction())
        {
            values.EditRange(0, 1)[0] = 10;
            using (var inner = world.BeginTransaction())
            {
                values.EditRange(0, population.Count).Fill(2);
                inner.Commit();
            }

            Assert.Equal(2, values.ReadOnlySpan[0]);
            Assert.Equal(2, values.ReadOnlySpan[^1]);
            outer.Rollback();
        }

        Assert.All(values.ReadOnlySpan.ToArray(), value => Assert.Equal(1, value));
    }

    [Fact]
    public void Full_range_write_records_one_undo_item_instead_of_one_per_page()
    {
        var world = new World(seed: 30, valueScale: 1);
        var population = world.DefinePopulation<TransactionParticleKind>();
        var values = population.Column(s_valueKey);
        population.Reserve(50_000);
        population.AddRange(50_000);

        using var transaction = world.BeginTransaction();
        values.EditRange(0, population.Count).Fill(1);

        Assert.Equal(1, world.UndoRecordCount);
        transaction.Rollback();
    }

    [Fact]
    public void SwapBackRemovalRollbackRestoresCountRowsAndHandles()
    {
        var world = new World(seed: 23, valueScale: 1);
        var population = world.DefinePopulation<TransactionParticleKind>(enableHandles: true);
        var values = population.Column(s_valueKey);
        population.AddRange(3);
        var rows = values.EditRange(0, 3);
        rows[0] = 10;
        rows[1] = 20;
        rows[2] = 30;
        var handles = Enumerable.Range(0, 3).Select(population.GetHandle).ToArray();

        using (var transaction = world.BeginTransaction())
        {
            population.RemoveSwapBack(1);
            Assert.Equal(2, population.Count);
            Assert.Equal([10, 30], values.ReadOnlySpan.ToArray());
            transaction.Rollback();
        }

        Assert.Equal(3, population.Count);
        Assert.Equal([10, 20, 30], values.ReadOnlySpan.ToArray());
        for (var row = 0; row < handles.Length; row++)
        {
            Assert.True(population.TryResolve(handles[row], out var restoredRow));
            Assert.Equal(row, restoredRow);
        }
    }

    [Fact]
    public void AddRangeWithHandlesRollbackRestoresAndInvalidatesIssuedHandles()
    {
        var world = new World(seed: 26, valueScale: 1);
        var population = world.DefinePopulation<TransactionParticleKind>(enableHandles: true);
        population.AddRange(1);
        var priorHandle = population.GetHandle(0);
        var priorCapacity = population.Capacity;
        PopulationHandle<TransactionParticleKind> rolledBackHandle;

        using (var transaction = world.BeginTransaction())
        {
            var rows = population.AddRange(5);
            rolledBackHandle = population.GetHandle(rows.Start);
            Assert.True(population.TryResolve(priorHandle, out var priorRow));
            Assert.Equal(0, priorRow);
            transaction.Rollback();
        }

        Assert.Equal(1, population.Count);
        Assert.Equal(priorCapacity, population.Capacity);
        Assert.True(population.TryResolve(priorHandle, out var restoredRow));
        Assert.Equal(0, restoredRow);
        Assert.False(population.TryResolve(rolledBackHandle, out _));

        var nextRows = population.AddRange(1);
        var reusedHandle = population.GetHandle(nextRows.Start);
        Assert.Equal(rolledBackHandle.Slot, reusedHandle.Slot);
        Assert.NotEqual(rolledBackHandle.Generation, reusedHandle.Generation);
        Assert.False(population.TryResolve(rolledBackHandle, out _));
    }

    [Fact]
    public void DoubleBufferRollbackRestoresCurrentSideAfterMultipleSwaps()
    {
        var world = new World(seed: 24, valueScale: 1);
        var population = world.DefinePopulation<TransactionParticleKind>();
        var column = population.DoubleBufferedColumn(s_valueKey);
        population.AddRange(2);
        var firstBuffer = column.EditNext(0, 2);
        firstBuffer[0] = 10;
        firstBuffer[1] = 20;
        column.Swap();
        var secondBuffer = column.EditNext(0, 2);
        secondBuffer[0] = 1;
        secondBuffer[1] = 2;

        using (var transaction = world.BeginTransaction())
        {
            column.Swap();
            column.EditNext(0, 1)[0] = 99;
            column.Swap();
            column.EditNext(1, 1)[0] = 88;
            transaction.Rollback();
        }

        Assert.Equal([10, 20], column.Current.ToArray());
        column.Swap();
        Assert.Equal([1, 2], column.Current.ToArray());
    }

    [Fact]
    public void CommitKeepsChangesAndReleasesUndoStateForReuse()
    {
        var world = new World(seed: 25, valueScale: 1);
        var population = world.DefinePopulation<TransactionParticleKind>();
        var values = population.Column(s_valueKey);
        population.AddRange(2);
        values.EditRange(0, 2).Fill(1);

        using (var transaction = world.BeginTransaction())
        {
            values.EditRange(0, 1)[0] = 42;
            transaction.Commit();
        }

        Assert.Equal(42, values.ReadOnlySpan[0]);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        using (var transaction = world.BeginTransaction())
        {
            values.EditRange(0, 1)[0] = 99;
            transaction.Rollback();
        }
        var allocatedAfter = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, allocatedAfter - allocatedBefore);
        Assert.Equal(42, values.ReadOnlySpan[0]);
    }

    [Fact]
    public void PageRollbackAfterWorldClonePreservesSingleColumnBranchState()
    {
        var world = new World(seed: 27, valueScale: 1);
        var population = world.DefinePopulation<TransactionParticleKind>();
        var values = population.Column(s_valueKey);
        population.AddRange(1);
        values.EditRange(0, 1)[0] = 10;
        World clone;

        using (var transaction = world.BeginTransaction())
        {
            values.EditRange(0, 1)[0] = 20;
            clone = world.Clone();
            transaction.Rollback();
        }

        Assert.Equal(10, values.ReadOnlySpan[0]);
        Assert.Equal(20, clone.GetPopulation<TransactionParticleKind>().Column(s_valueKey).ReadOnlySpan[0]);
    }

    [Fact]
    public void PageRollbackAfterWorldClonePreservesDoubleBufferBranchState()
    {
        var world = new World(seed: 28, valueScale: 1);
        var population = world.DefinePopulation<TransactionParticleKind>();
        var column = population.DoubleBufferedColumn(s_valueKey);
        population.AddRange(1);
        column.EditNext(0, 1)[0] = 10;
        column.Swap();
        World clone;

        using (var transaction = world.BeginTransaction())
        {
            column.EditNext(0, 1)[0] = 20;
            column.Swap();
            clone = world.Clone();
            transaction.Rollback();
        }

        Assert.Equal(10, column.Current[0]);
        Assert.Equal(20, clone.GetPopulation<TransactionParticleKind>()
            .DoubleBufferedColumn(s_valueKey).Current[0]);
    }

    private readonly struct TransactionParticleKind : IPopulationKind
    {
        public static string Id => "test.transaction-particles";

        public static int Version => 1;
    }
}
