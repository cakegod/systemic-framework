using Systemic.Kernel;

namespace Systemic.Tests.Kernel;

public sealed class PopulationLeaseTransactionTests
{
    private static readonly PopulationColumnKey<int> s_key = new("lease.value", 1, PopulationCodecs.Int32);

    [Fact]
    public void Lease_guards_structure_buffer_roles_and_world_lifetime_until_disposed()
    {
        var world = new World(1, 1);
        var population = world.DefinePopulation<Kind>();
        var column = population.Column(s_key);
        var doubleColumn = population.DoubleBufferedColumn(new PopulationColumnKey<int>("lease.double", 1, PopulationCodecs.Int32));
        population.AddRange(2);
        column.EditRange(0, 2).Fill(1);
        var saved = world.CapturePopulationState();
        var branch = world.Clone();
        var lease = population.Borrow();

        lease.Edit(column, 0, 1)[0] = 4;
        Assert.Equal(new[] { 4, 1 }, lease.Read(column).ToArray());
        Assert.Throws<InvalidOperationException>(() => population.AddRange(1));
        Assert.Throws<InvalidOperationException>(() => population.RemoveSwapBack(0));
        Assert.Throws<InvalidOperationException>(() => population.Reserve(population.Capacity + 1));
        Assert.Throws<InvalidOperationException>(doubleColumn.Swap);
        Assert.Throws<InvalidOperationException>(() => world.Clone());
        Assert.Throws<InvalidOperationException>(() => world.RestorePopulationState(saved));
        Assert.Throws<InvalidOperationException>(() => world.AdoptActionWorld(branch));
        Assert.Throws<InvalidOperationException>(() => world.BeginTransaction());
        Assert.Equal(2, population.Count);

        lease.Dispose();
        lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => { _ = lease.Read(column); });
        population.RemoveSwapBack(0);
        Assert.Equal(1, population.Count);
    }

    [Fact]
    public void Active_lease_prevents_commit_or_rollback_until_released()
    {
        var world = new World(2, 1);
        var population = world.DefinePopulation<Kind>();
        var column = population.Column(s_key);
        population.AddRange(1);
        using var transaction = world.BeginTransaction();
        var lease = population.Borrow();
        lease.Edit(column, 0, 1)[0] = 8;

        Assert.Throws<InvalidOperationException>(transaction.Rollback);
        Assert.Throws<InvalidOperationException>(transaction.Commit);
        Assert.Equal(8, column.ReadOnlySpan[0]);
        lease.Dispose();
        transaction.Rollback();
        Assert.Equal(0, column.ReadOnlySpan[0]);
    }

    [Fact]
    public void Lease_rejects_a_column_from_another_population()
    {
        var first = new World(3, 1).DefinePopulation<Kind>();
        var second = new World(4, 1).DefinePopulation<Kind>();
        var foreign = second.Column(s_key);
        first.AddRange(1);
        second.AddRange(1);
        using var lease = first.Borrow();

        Assert.Throws<ArgumentException>(() => { _ = lease.Read(foreign); });
        Assert.Throws<ArgumentException>(() => { _ = lease.Edit(foreign, 0, 1); });
    }

    [Fact]
    public void Multiple_leases_must_all_close_before_structure_changes()
    {
        var population = new World(5, 1).DefinePopulation<Kind>();
        population.AddRange(1);
        var first = population.Borrow();
        var second = population.Borrow();

        first.Dispose();
        first.Dispose();
        Assert.Throws<InvalidOperationException>(() => population.RemoveSwapBack(0));
        second.Dispose();
        population.RemoveSwapBack(0);
        Assert.Equal(0, population.Count);
    }

    [Fact]
    public void Lease_blocks_structural_edits_from_another_thread()
    {
        var population = new World(6, 1).DefinePopulation<Kind>();
        population.AddRange(1);
        using var lease = population.Borrow();
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try { population.RemoveSwapBack(0); }
            catch (Exception exception) { failure = exception; }
        });
        worker.Start();
        worker.Join();

        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(1, population.Count);
    }

    private readonly struct Kind : IPopulationKind
    {
        public static string Id => "test.lease";
        public static int Version => 1;
    }
}
