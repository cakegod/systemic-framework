using Systemic.Kernel;

namespace Systemic.Tests.Kernel;

public sealed class DeterministicSystemScheduleTests
{
    [Fact]
    public void IndependentSystemsRunInParallel()
    {
        using var barrier = new Barrier(2);
        var builder = new DeterministicSystemScheduleBuilder(workerCount: 2);
        builder.Add(new TestSystem("first", [SystemAccess.ReadResource("first")], _ =>
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(3)))));
        builder.Add(new TestSystem("second", [SystemAccess.ReadResource("second")], _ =>
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(3)))));

        using var schedule = builder.Build();
        schedule.Execute(new World(seed: 1, valueScale: 1), tick: 1);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void DeferredWorldWritesAreVisibleAtDependencyBarriersAndDeterministic(int workers)
    {
        var world = new World(seed: 1, valueScale: 1);
        var entity = world.Add(new DataEntity([], [], ("amount", 0)));
        var builder = new DeterministicSystemScheduleBuilder(workers);
        builder.Add(new TestSystem("write-first", [SystemAccess.DeferWorldWrites], context =>
            context.Commands.SetProperty(entity.Id, "amount", 1)));
        builder.Add(new TestSystem("read-then-write", [SystemAccess.ReadWorld, SystemAccess.DeferWorldWrites], context =>
        {
            Assert.True(context.World.TryGetProperty(entity.Id, "amount", out var amount));
            context.Commands.SetProperty(entity.Id, "amount", amount + 1);
        }));
        builder.Add(new TestSystem("same-wave-writer", [SystemAccess.DeferWorldWrites], context =>
            context.Commands.SetProperty(entity.Id, "amount", 9)));

        using var schedule = builder.Build();
        schedule.Execute(world, tick: 1);

        Assert.Equal(9, world.EffectiveProperty(entity, "amount"));
    }

    [Fact]
    public void DeferredEntityMetadataLinksModifiersAndGrantsApplyAtTheBarrier()
    {
        var world = new World(seed: 1, valueScale: 1);
        var entity = world.Add(new DataEntity([], [], ("amount", 5)));
        var target = world.Add(new DataEntity([], []));
        var system = new TestSystem("entity-commands", [SystemAccess.DeferWorldWrites], context =>
        {
            context.Commands.AddTag(entity.Id, "scheduled");
            context.Commands.AddCapability(entity.Id, "can_interact");
            context.Commands.SetLink(entity.Id, "target", target.Id);
            context.Commands.SetModifier("system", entity.Id, "amount", ModifierOperation.Add, 3);
            context.Commands.Grant("system", entity.Id, GrantKind.Capability, "can_produce");
        });

        using var schedule = new DeterministicSystemScheduleBuilder(workerCount: 4).Add(system).Build();
        schedule.Execute(world, tick: 1);

        Assert.True(world.HasTag(entity, "scheduled"));
        Assert.True(world.HasCapability(entity, "can_interact"));
        Assert.True(world.HasCapability(entity, "can_produce"));
        Assert.Equal(target.Id, entity.Links["target"]);
        Assert.Equal(8, world.EffectiveProperty(entity, "amount"));
    }

    [Fact]
    public void ScheduledWorldCommandsCannotFlushManuallyToAnotherWorld()
    {
        var scheduledWorld = new World(seed: 1, valueScale: 1);
        var otherWorld = new World(seed: 2, valueScale: 1);
        var entity = new DataEntity([], []);
        var system = new TestSystem("manual-flush", [SystemAccess.DeferWorldWrites], context =>
        {
            context.Commands.Add(entity);
            context.Commands.Flush(otherWorld);
        });
        using var schedule = new DeterministicSystemScheduleBuilder().Add(system).Build();

        Assert.Throws<InvalidOperationException>(() => schedule.Execute(scheduledWorld, tick: 1));

        Assert.Null(otherWorld.ById(1));
        Assert.Null(scheduledWorld.ById(1));
    }

    [Fact]
    public void FailureAfterABarrierRollsBackAllWorldWrites()
    {
        var world = new World(seed: 1, valueScale: 1);
        var entity = world.Add(new DataEntity([], [], ("amount", 0)));
        var builder = new DeterministicSystemScheduleBuilder(workerCount: 2);
        builder.Add(new TestSystem("write", [SystemAccess.DeferWorldWrites], context =>
            context.Commands.SetProperty(entity.Id, "amount", 5)));
        builder.Add(new TestSystem("read-and-fail", [SystemAccess.ReadWorld], _ =>
            throw new InvalidOperationException("expected")));

        using var schedule = builder.Build();
        Assert.Throws<InvalidOperationException>(() => schedule.Execute(world, tick: 1));

        Assert.Equal(0, world.EffectiveProperty(entity, "amount"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void RowPartitionsCoverEachRowOnceIndependentOfWorkerCount(int workers)
    {
        var values = new int[257];
        var system = new TestSystem("rows", [SystemAccess.WriteResource("rows")], context =>
            context.ForEachRange(values, values.Length, WriteRowRange));
        using var schedule = new DeterministicSystemScheduleBuilder(workers).Add(system).Build();

        schedule.Execute(new World(seed: 1, valueScale: 1), tick: 1);

        for (var row = 0; row < values.Length; row++)
        {
            Assert.Equal(row + 1, values[row]);
        }
    }

    [Fact]
    public void WarmedScheduleAndRowDispatchDoNotAllocateOnTheSimulationThread()
    {
        const int warmupTicks = 200;
        const int measuredTicks = 20;
        var values = new int[4096];
        var system = new TestSystem("rows", [SystemAccess.WriteResource("rows")], context =>
            context.ForEachRange(values, values.Length, WriteRowRange));
        using var schedule = new DeterministicSystemScheduleBuilder(workerCount: 4).Add(system).Build();
        var world = new World(seed: 1, valueScale: 1);
        for (var tick = 0; tick < warmupTicks; tick++)
        {
            schedule.Execute(world, tick);
        }

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var tick = 0; tick < measuredTicks; tick++)
        {
            schedule.Execute(world, tick);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
    }

    [Fact]
    public void DisposedScheduleRejectsMoreWork()
    {
        using var schedule = new DeterministicSystemScheduleBuilder(workerCount: 2)
            .Add(new TestSystem("rows", [SystemAccess.ReadResource("rows")], static _ => { }))
            .Build();
        var world = new World(seed: 1, valueScale: 1);

        schedule.Execute(world, tick: 1);
        schedule.Dispose();

        Assert.Throws<ObjectDisposedException>(() => schedule.Execute(world, tick: 2));
    }

    [Fact]
    public void ScheduledContextCommandBuffersExpireAfterExecution()
    {
        var world = new World(seed: 1, valueScale: 1);
        var entity = world.Add(new DataEntity([], [], ("amount", 0)));
        ScheduledSystemContext? capturedContext = null;
        Action? appendCapturedCommand = null;
        var system = new TestSystem("capture-context", [SystemAccess.DeferWorldWrites], context =>
        {
            capturedContext = context;
            var commands = context.Commands;
            appendCapturedCommand = () => commands.SetProperty(entity.Id, "amount", 1);
        });

        using var schedule = new DeterministicSystemScheduleBuilder().Add(system).Build();
        schedule.Execute(world, tick: 1);

        Assert.Throws<InvalidOperationException>(() => _ = capturedContext!.World);
        Assert.Throws<InvalidOperationException>(appendCapturedCommand!);
    }

    [Fact]
    public void CommandWriterFromAnEarlierTickStaysExpiredDuringTheNextTick()
    {
        var world = new World(seed: 1, valueScale: 1);
        var entity = world.Add(new DataEntity([], [], ("amount", 0)));
        Action? appendOldCommand = null;
        var system = new TestSystem("capture-writer", [SystemAccess.DeferWorldWrites], context =>
        {
            if (appendOldCommand is null)
            {
                var commands = context.Commands;
                appendOldCommand = () => commands.SetProperty(entity.Id, "amount", 99);
                return;
            }

            Assert.Throws<InvalidOperationException>(appendOldCommand);
            context.Commands.SetProperty(entity.Id, "amount", 2);
        });
        using var schedule = new DeterministicSystemScheduleBuilder().Add(system).Build();

        schedule.Execute(world, tick: 1);
        schedule.Execute(world, tick: 2);

        Assert.Equal(2, world.EffectiveProperty(entity, "amount"));
    }

    [Fact]
    public void DuplicateSystemIdsAreRejectedBeforeExecution()
    {
        var builder = new DeterministicSystemScheduleBuilder();
        builder.Add(new TestSystem("same", [SystemAccess.ReadResource("a")], static _ => { }));

        Assert.Throws<InvalidOperationException>(() =>
            builder.Add(new TestSystem("same", [SystemAccess.ReadResource("b")], static _ => { })));
    }

    private sealed class TestSystem(
        string id,
        IReadOnlyList<SystemAccess> access,
        Action<ScheduledSystemContext> execute) : IScheduledKernelSystem
    {
        public string Id { get; } = id;
        public IReadOnlyList<SystemAccess> Access { get; } = access;
        public void Execute(ScheduledSystemContext context) => execute(context);
    }

    private static void WriteRowRange(int[] values, int _, PopulationRange range)
    {
        for (var row = range.Start; row < range.End; row++)
        {
            values[row] = row + 1;
        }
    }
}
