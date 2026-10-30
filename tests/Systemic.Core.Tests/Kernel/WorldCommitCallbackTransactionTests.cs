using Systemic.Kernel;

namespace Systemic.Tests.Kernel;

public sealed class WorldCommitCallbackTransactionTests
{
    [Fact]
    public void CommitCallbackFailureIsDistinctAndLeavesChangesCommitted()
    {
        var world = new World(1, 1);
        var population = world.DefinePopulation<Kind>();
        var key = new PopulationColumnKey<int>("commit.value", 1, PopulationCodecs.Int32);
        var column = population.Column(key);
        population.AddRange(1);
        using var outer = world.BeginTransaction();
        using (var inner = world.BeginTransaction())
        {
            column.EditRange(0, 1)[0] = 7;
            inner.OnCommit(() => throw new InvalidOperationException("callback"));
            inner.Commit();
        }

        var error = Assert.Throws<WorldCommitCallbackException>(outer.Commit);

        Assert.IsType<InvalidOperationException>(Assert.Single(error.InnerExceptions));
        Assert.Equal(7, column.ReadOnlySpan[0]);
    }

    [Fact]
    public void RollbackCallbackFailureRemainsARollbackFailure()
    {
        var world = new World(1, 1);
        using var transaction = world.BeginTransaction();
        transaction.OnRollback(() => throw new InvalidOperationException("rollback"));

        var error = Assert.Throws<AggregateException>(transaction.Rollback);

        Assert.IsNotType<WorldCommitCallbackException>(error);
    }

    private readonly struct Kind : IPopulationKind
    {
        public static string Id => "test.commit-callback";
        public static int Version => 1;
    }
}
