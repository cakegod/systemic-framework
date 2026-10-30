using Systemic.Kernel;

namespace Systemic.Tests.Kernel;

public sealed class PopulationLeaseTransactionTests
{
    private static readonly PopulationColumnKey<int> s_key = new("lease.value", 1, PopulationCodecs.Int32);

    [Fact]
    public void LeaseGuardsStructureBufferRolesAndWorldLifetimeUntilDisposed()
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

        lease.WithAccess(new PopulationRange(0, 2), column,
            static (PopulationColumn<int> target, scoped ref PopulationAccessScope<Kind> scope) =>
            {
                scope.EditRange(target)[0] = 4;
                Assert.Equal([4, 1], scope.ReadAll(target).ToArray());
            });
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
        Assert.Throws<ObjectDisposedException>(() => lease.WithAccess(new PopulationRange(0, 1), column,
            static (PopulationColumn<int> target, scoped ref PopulationAccessScope<Kind> scope) =>
                _ = scope.ReadAll(target)));
        population.RemoveSwapBack(0);
        Assert.Equal(1, population.Count);
    }

    [Fact]
    public void ActiveLeasePreventsCommitOrRollbackUntilReleased()
    {
        var world = new World(2, 1);
        var population = world.DefinePopulation<Kind>();
        var column = population.Column(s_key);
        population.AddRange(1);
        using var transaction = world.BeginTransaction();
        var lease = population.Borrow();
        lease.WithAccess(new PopulationRange(0, 1), column,
            static (PopulationColumn<int> target, scoped ref PopulationAccessScope<Kind> scope) =>
                scope.EditRange(target)[0] = 8);

        Assert.Throws<InvalidOperationException>(transaction.Rollback);
        Assert.Throws<InvalidOperationException>(transaction.Commit);
        Assert.Equal(8, column.ReadOnlySpan[0]);
        lease.Dispose();
        transaction.Rollback();
        Assert.Equal(0, column.ReadOnlySpan[0]);
    }

    [Fact]
    public void LeaseRejectsAColumnFromAnotherPopulation()
    {
        var first = new World(3, 1).DefinePopulation<Kind>();
        var second = new World(4, 1).DefinePopulation<Kind>();
        var foreign = second.Column(s_key);
        first.AddRange(1);
        second.AddRange(1);
        using var lease = first.Borrow();

        Assert.Throws<ArgumentException>(() => lease.WithAccess(new PopulationRange(0, 1), foreign,
            static (PopulationColumn<int> target, scoped ref PopulationAccessScope<Kind> scope) =>
                _ = scope.ReadAll(target)));
        Assert.Throws<ArgumentException>(() => lease.WithAccess(new PopulationRange(0, 1), foreign,
            static (PopulationColumn<int> target, scoped ref PopulationAccessScope<Kind> scope) =>
                _ = scope.EditRange(target)));
    }

    [Fact]
    public void MultipleLeasesMustAllCloseBeforeStructureChanges()
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
    public void LeaseBlocksStructuralEditsFromAnotherThread()
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
