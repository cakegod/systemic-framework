using Systemic.Kernel;

namespace Systemic.Tests.Kernel;

public sealed class EventEngineAllocationTests
{
    [Fact]
    public void Warmed_reusable_root_event_dispatch_does_not_allocate()
    {
        const int warmupCount = 100;
        const int measuredCount = 100;
        var world = new World(seed: 1, valueScale: 1);
        var engine = new EngineBuilder().Build();
        var domainEvent = new ReusableTestEvent(1);

        for (var index = 0; index < warmupCount; index++)
        {
            Assert.True(engine.ExecuteReusable(world, domainEvent));
        }

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var allAccepted = true;
        for (var index = 0; index < measuredCount; index++)
        {
            allAccepted &= engine.ExecuteReusable(world, domainEvent);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
        Assert.True(allAccepted);
        Assert.Equal((long)(warmupCount + measuredCount), domainEvent.EventId);
        Assert.Equal(warmupCount + measuredCount, domainEvent.ChainId);
        Assert.Equal(0, domainEvent.Depth);
    }

    [Fact]
    public void Snapshot_dispatch_does_not_mutate_the_caller_event()
    {
        var world = new World(seed: 1, valueScale: 1);
        var engine = new EngineBuilder().Build();
        var domainEvent = new ReusableTestEvent(1);

        Assert.True(engine.Execute(world, domainEvent));

        Assert.Equal(0, domainEvent.EventId);
        Assert.Equal(default, domainEvent.Causality);
    }

    [Fact]
    public void Warmed_reusable_batch_dispatch_does_not_allocate()
    {
        const int warmupCount = 20;
        const int measuredCount = 100;
        var world = new World(seed: 1, valueScale: 1);
        world.Add(new DataEntity(["producer"], []));
        var query = new EntityQuery<DataEntity>(tags: ["producer"]);
        var engine = new EngineBuilder()
            .OnBatch<ReusableTestEvent, DataEntity>(
                "allocation.test",
                query,
                static context => context.ForEachTarget(static _ => { }),
                transactional: false)
            .Build();
        var domainEvent = new ReusableTestEvent(1);
        var structuralVersion = world.StructuralVersion;

        for (var index = 0; index < warmupCount; index++)
        {
            Assert.True(engine.ExecuteReusable(world, domainEvent));
        }

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var allAccepted = true;
        for (var index = 0; index < measuredCount; index++)
        {
            allAccepted &= engine.ExecuteReusable(world, domainEvent);
        }

        Assert.True(allAccepted);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
        Assert.Equal(structuralVersion, world.StructuralVersion);
    }

    private sealed record ReusableTestEvent(int SourceId) : KernelEvent(SourceId);
}
