using Systemic.Kernel;
using Systemic.Content;
using Systemic.Extensions;
using Systemic.Host;

namespace Systemic.Tests.Host;

public sealed class SystemicGameTests
{
    [Fact]
    public void Game_host_runs_systems_in_stable_order_and_uses_content_reactions()
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
    public void Game_host_accumulates_fixed_steps_from_compiled_tick_rate()
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
    public void Host_runs_scheduled_systems_after_legacy_systems_with_deferred_world_commands()
    {
        var content = SystemicTestContent.Load(Path.Combine(AppContext.BaseDirectory, "samples", "incremental-quarry"))
            with
        { Modules = [] };
        var world = new World(seed: 1, valueScale: 1);
        var target = world.Add(new DataEntity([], [], ("value", 0)));
        var order = new List<string>();
        using var game = new SystemicGameBuilder(content, world)
            .AddSystem(new RecordingSystem("legacy", 0, _ => order.Add("legacy")))
            .AddScheduledSystem(new ScheduledRecordingSystem(target.Id, order))
            .WithScheduledWorkerCount(2)
            .Build();

        game.Step();

        Assert.Equal(["legacy", "scheduled"], order);
        Assert.Equal(7, world.EffectiveProperty(target, "value"));
    }

    [Fact]
    public void Disposing_a_host_disposes_its_schedule_and_stops_future_steps()
    {
        var content = SystemicTestContent.Load(Path.Combine(AppContext.BaseDirectory, "samples", "incremental-quarry"))
            with
        { Modules = [] };
        using var game = new SystemicGameBuilder(content, new World(seed: 1, valueScale: 1))
            .AddScheduledSystem(new ScheduledRecordingSystem(1, []))
            .WithScheduledWorkerCount(2)
            .Build();

        game.Dispose();

        Assert.Throws<ObjectDisposedException>(game.Step);
    }

    [Fact]
    public void Scheduled_fact_dispatch_requires_exclusive_world_write_access_and_expires_its_context()
    {
        var content = SystemicTestContent.Load(Path.Combine(AppContext.BaseDirectory, "samples", "incremental-quarry"))
            with
        { Modules = [] };
        var world = new World(seed: 1, valueScale: 1);
        var entity = world.Add(new DataEntity([], [], ("value", 0)));
        var system = new PublishingScheduledSystem(entity.Id);
        using var game = new SystemicGameBuilder(content, world)
            .AddScheduledSystem(system)
            .Build();

        Assert.Throws<InvalidOperationException>(game.Step);
        Assert.NotNull(system.ContextAfterStep);
        Assert.Throws<InvalidOperationException>(() => _ = system.ContextAfterStep.Commands);
        Assert.Equal(0, world.EffectiveProperty(entity, "value"));
    }

    [Fact]
    public void Failed_scheduled_tick_rolls_back_world_commands_and_logical_tick()
    {
        var content = SystemicTestContent.Load(Path.Combine(AppContext.BaseDirectory, "samples", "incremental-quarry"))
            with
        { Modules = [] };
        var world = new World(seed: 1, valueScale: 1);
        var entity = world.Add(new DataEntity([], [], ("value", 0)));
        using var game = new SystemicGameBuilder(content, world)
            .AddSystem(new RecordingSystem("legacy-write", 0, context => context.SetProperty(entity, "value", 3)))
            .AddScheduledSystem(new ScheduledRecordingSystem(entity.Id, []))
            .AddScheduledSystem(new ThrowingScheduledSystem())
            .WithScheduledWorkerCount(2)
            .Build();

        Assert.Throws<InvalidOperationException>(game.Step);

        Assert.Equal(0, world.EffectiveProperty(entity, "value"));
        Assert.Equal(0, world.LogicalTick);
    }

    [Fact]
    public void Host_routes_input_to_explicit_typed_kernel_modules()
    {
        var content = SystemicTestContent.Load(Path.Combine(AppContext.BaseDirectory, "samples", "incremental-quarry"))
            with
        { Modules = [] };
        var world = new World(seed: 2, valueScale: 1);
        var source = world.Add(new DataEntity([], []));
        var target = world.Add(new DataEntity([], [], ("value", 0)));
        using var game = new SystemicGameBuilder(content, world)
            .AddExtension(new IncrementValueModule())
            .Build();

        var result = game.Execute(new IncrementValueFact(source.Id, target.Id));

        Assert.True(result.Accepted);
        Assert.True(target.TryGetProperty(world, "value", out var value));
        Assert.Equal(1, value);
    }

    [Fact]
    public void Registered_module_content_runs_through_the_core_builder()
    {
        var samplePath = Path.Combine(AppContext.BaseDirectory, "samples", "incremental-quarry");
        var content = SystemicTestContent.Load(samplePath);
        using var game = new SystemicGameBuilder(content, content.CreateWorld(seed: 17, valueScale: 1)).Build();
        game.Step();

        var ore = game.World.ById(game.EntityId("quarry.ore"))!;
        Assert.True(ore.TryGetProperty(game.World, "balance", out var balance));
        Assert.Equal(2, balance);
    }

    private sealed class RecordingSystem(string id, int order, Action<SystemicStepContext> action) : ISystemicSystem
    {
        public string Id { get; } = id;
        public int Order { get; } = order;
        public void Step(SystemicStepContext context) => action(context);
    }

    private sealed class ScheduledRecordingSystem(int entityId, List<string> order) : ISystemicScheduledSystem
    {
        public string Id => "scheduled";
        public IReadOnlyList<SystemAccess> Access => [SystemAccess.DeferWorldWrites];
        public void Step(ScheduledSystemContext _, SystemicScheduledContext gameContext)
        {
            order.Add("scheduled");
            gameContext.Commands.SetProperty(entityId, "value", 7);
        }
    }

    private sealed class ThrowingScheduledSystem : ISystemicScheduledSystem
    {
        public string Id => "failure";
        public IReadOnlyList<SystemAccess> Access => [SystemAccess.WriteWorld];
        void ISystemicScheduledSystem.Step(ScheduledSystemContext kernelContext, SystemicScheduledContext gameContext) =>
            throw new InvalidOperationException("expected");
    }

    private sealed class PublishingScheduledSystem(int entityId) : ISystemicScheduledSystem
    {
        public string Id => "publishing";
        public IReadOnlyList<SystemAccess> Access => [SystemAccess.DeferWorldWrites];
        public SystemicScheduledContext? ContextAfterStep { get; private set; }

        public void Step(ScheduledSystemContext _, SystemicScheduledContext gameContext)
        {
            ContextAfterStep = gameContext;
            gameContext.Publish(new Systemic.Rules.SimulationFact("test", entityId, entityId));
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1067:Override Object.Equals when implementing IEquatable",
        Justification = "Record declarations synthesize object equality and matching hash codes.")]
    private sealed record IncrementValueFact(int SourceId, int TargetId) : KernelEvent(SourceId, TargetId);

    private sealed class IncrementValueModule : ISystemicExtensionModule
    {
        public string Id => "sample.increment";

        public void Register(EngineBuilder engine) => engine.On<IncrementValueFact>("sample.increment.value", context =>
        {
            if (context.World.ById(context.Event.TargetId) is { } target
                && target.TryGetProperty(context.World, "value", out var value))
            {
                target.TrySetProperty(context.World, "value", value + 1);
            }
        });
    }

}
