using Systemic.Kernel;

namespace Systemic.Tests.Kernel;

public sealed class EventEngineTransactionTests
{
    [Fact]
    public void Rolling_back_an_enclosing_transaction_rewinds_the_event_counters()
    {
        var world = new World(seed: 1, valueScale: 1);
        var engine = new EngineBuilder().Build();
        var domainEvent = new ReusableTestEvent(1);

        using (var transaction = world.BeginTransaction())
        {
            Assert.True(engine.ExecuteReusable(world, domainEvent));
            Assert.Equal(1, domainEvent.EventId);
            transaction.Rollback();
        }

        Assert.True(engine.ExecuteReusable(world, domainEvent));

        Assert.Equal(1, domainEvent.EventId);
        Assert.Equal(1, domainEvent.ChainId);
        Assert.Equal(0, domainEvent.Depth);
    }

    [Fact]
    public void Rolling_back_an_outer_transaction_rewinds_counters_after_a_nested_commit()
    {
        var world = new World(seed: 1, valueScale: 1);
        var engine = new EngineBuilder().Build();
        var domainEvent = new ReusableTestEvent(1);

        using (var outer = world.BeginTransaction())
        {
            using (var inner = world.BeginTransaction())
            {
                Assert.True(engine.ExecuteReusable(world, domainEvent));
                inner.Commit();
            }

            outer.Rollback();
        }

        Assert.True(engine.ExecuteReusable(world, domainEvent));

        Assert.Equal(1, domainEvent.EventId);
    }

    private sealed record ReusableTestEvent(int SourceId) : KernelEvent(SourceId);
}
