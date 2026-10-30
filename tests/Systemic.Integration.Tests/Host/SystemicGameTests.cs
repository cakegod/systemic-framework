using Systemic.Kernel;
using Systemic.Host;

namespace Systemic.Tests.Host;

public sealed class SystemicGameTests
{
    [Fact]
    public void GameHostRunsSystemsInStableOrderAndUsesContentReactions()
    {
        var content = SystemicTestContent.Load(Path.Combine(AppContext.BaseDirectory, "samples", "incremental-quarry"))
            with
        { Modules = [] };
        var world = new World(seed: 1, valueScale: 1);
        var actor = world.Add(new DataEntity(["ember"], [], ("heat", 0), ("output", 0)));
        var target = world.Add(new DataEntity(["fuel"], [], ("heat", 0), ("output", 0)));
        var order = new List<string>();
        var builder = new SystemicGameBuilder(content, world)
            .AddSystem(new RecordingSystem("later", 20, context =>
            {
                order.Add("later");
                context.Publish(new Systemic.Rules.SimulationFact("contact", actor.Id, target.Id));
            }))
            .AddSystem(new RecordingSystem("first", 10, _ => order.Add("first")));

        using var game = builder.Build();
        game.Step();

        Assert.Equal(["first", "later"], order);
        Assert.True(target.TryGetProperty(world, "heat", out var heat));
        Assert.Equal(2, heat);
        Assert.True(world.HasTag(target, "warm"));
        Assert.Equal(1, world.LogicalTick);
    }

    [Fact]
    public void GameHostAccumulatesFixedStepsFromCompiledTickRate()
    {
        var content = SystemicTestContent.Load(Path.Combine(AppContext.BaseDirectory, "samples", "incremental-quarry"))
            with
        { Modules = [] };
        var steps = 0;
        using var game = new SystemicGameBuilder(content, new World(seed: 1, valueScale: 1))
            .AddSystem(new RecordingSystem("counter", 0, _ => steps++))
            .Build();

        var result = game.Advance(TimeSpan.FromMilliseconds(50));

        Assert.Equal(3, result.Steps);
        Assert.Equal(3, steps);
    }

    [Fact]
    public void HostRunsScheduledSystemsAfterLegacySystemsWithDeferredWorldCommands()
    {
        var content = SystemicTestContent.Load(Path.Combine(AppContext.BaseDirectory, "samples", "incremental-quarry"))
            with
        { Modules = [] };
        var world = new World(seed: 1, valueScale: 1);
        var target = world.Add(new DataEntity([], [], ("value", 0)));
        var order = new List<string>();
        using var game = new SystemicGameBuilder(content, world)
            .AddSystem(new RecordingSystem("legacy", 0, _ => order.Add("legacy")))
            .AddSystem(new ScheduledRecordingSystem(target.Id, order))
            .WithScheduledWorkerCount(2)
            .Build();

        game.Step();

        Assert.Equal(["legacy", "scheduled"], order);
        Assert.Equal(7, world.EffectiveProperty(target, "value"));
    }

    [Fact]
    public void DisposingAHostDisposesItsScheduleAndStopsFutureSteps()
    {
        var content = SystemicTestContent.Load(Path.Combine(AppContext.BaseDirectory, "samples", "incremental-quarry"))
            with
        { Modules = [] };
        using var game = new SystemicGameBuilder(content, new World(seed: 1, valueScale: 1))
            .AddSystem(new ScheduledRecordingSystem(1, []))
            .WithScheduledWorkerCount(2)
            .Build();

        game.Dispose();

        Assert.Throws<ObjectDisposedException>(game.Step);
    }

    [Fact]
    public void ScheduledFactDispatchRequiresExclusiveWorldWriteAccessAndExpiresItsContext()
    {
        var content = SystemicTestContent.Load(Path.Combine(AppContext.BaseDirectory, "samples", "incremental-quarry"))
            with
        { Modules = [] };
        var world = new World(seed: 1, valueScale: 1);
        var entity = world.Add(new DataEntity([], [], ("value", 0)));
        var system = new PublishingScheduledSystem(entity.Id);
        using var game = new SystemicGameBuilder(content, world)
            .AddSystem(system)
            .Build();

        Assert.Throws<InvalidOperationException>(game.Step);
        Assert.NotNull(system.ContextAfterStep);
        Assert.Throws<InvalidOperationException>(() => _ = system.ContextAfterStep.Commands);
        Assert.Equal(0, world.EffectiveProperty(entity, "value"));
    }

    [Fact]
    public void FailedScheduledTickRollsBackWorldCommandsAndLogicalTick()
    {
        var content = SystemicTestContent.Load(Path.Combine(AppContext.BaseDirectory, "samples", "incremental-quarry"))
            with
        { Modules = [] };
        var world = new World(seed: 1, valueScale: 1);
        var entity = world.Add(new DataEntity([], [], ("value", 0)));
        using var game = new SystemicGameBuilder(content, world)
            .AddSystem(new RecordingSystem("legacy-write", 0, context => context.SetProperty(entity, "value", 3)))
            .AddSystem(new ScheduledRecordingSystem(entity.Id, []))
            .AddSystem(new ThrowingScheduledSystem())
            .WithScheduledWorkerCount(2)
            .Build();

        Assert.Throws<InvalidOperationException>(game.Step);

        Assert.Equal(0, world.EffectiveProperty(entity, "value"));
        Assert.Equal(0, world.LogicalTick);
    }

    [Fact]
    public void RegisteredModuleContentRunsThroughTheCoreBuilder()
    {
        var samplePath = Path.Combine(AppContext.BaseDirectory, "samples", "incremental-quarry");
        var content = SystemicTestContent.Load(samplePath);
        using var game = new SystemicGameBuilder(content, content.CreateWorld(seed: 17, valueScale: 1)).Build();
        game.Step();

        var ore = game.World.ById(game.EntityId("quarry.ore"))!;
        Assert.True(ore.TryGetProperty(game.World, "balance", out var balance));
        Assert.Equal(2, balance);
    }

    private sealed class RecordingSystem(string id, int order, Action<SystemicSystemContext> action) : ISystemicSystem
    {
        public string Id { get; } = id;
        public int Order { get; } = order;
        public void Step(SystemicSystemContext context) => action(context);
    }

    private sealed class ScheduledRecordingSystem(int entityId, List<string> order) : ISystemicSystem
    {
        public string Id => "scheduled";
        public int Order => 1;
        public IReadOnlyList<SystemAccess> Access => [SystemAccess.DeferWorldWrites];
        public void Step(SystemicSystemContext gameContext)
        {
            order.Add("scheduled");
            gameContext.Commands.SetProperty(entityId, "value", 7);
        }
    }

    private sealed class ThrowingScheduledSystem : ISystemicSystem
    {
        public string Id => "failure";
        public int Order => 10;
        public IReadOnlyList<SystemAccess> Access => [SystemAccess.WriteWorld];
        public void Step(SystemicSystemContext gameContext) =>
            throw new InvalidOperationException("expected");
    }

    private sealed class PublishingScheduledSystem(int entityId) : ISystemicSystem
    {
        public string Id => "publishing";
        public int Order => 0;
        public IReadOnlyList<SystemAccess> Access => [SystemAccess.DeferWorldWrites];
        public SystemicSystemContext? ContextAfterStep { get; private set; }

        public void Step(SystemicSystemContext gameContext)
        {
            ContextAfterStep = gameContext;
            gameContext.Publish(new Systemic.Rules.SimulationFact("test", entityId, entityId));
        }
    }

}
