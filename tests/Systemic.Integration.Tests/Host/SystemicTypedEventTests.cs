using System.Text.Json;
using Systemic.Content;
using Systemic.Host;
using Systemic.Kernel;

namespace Systemic.Tests.Host;

public sealed class SystemicTypedEventTests
{
    [Fact]
    public void TypedEventHandlersRunInRegistrationOrder()
    {
        var content = Content();
        var seen = new List<string>();
        using var game = new SystemicGameBuilder(content, content.CreateWorld(seed: 9, valueScale: 1).World)
            .AddEvent<Harvested>(new FirstHandler(seen))
            .AddEvent<Harvested>(new SecondHandler(seen))
            .Build();

        game.Emit(new Harvested(7));

        Assert.Equal(["first:7", "second:7"], seen);
    }

    [Fact]
    public void EventHandlersCanReadRegisteredServices()
    {
        var content = Content();
        var service = new TestService();
        using var game = new SystemicGameBuilder(content, content.CreateWorld(seed: 9, valueScale: 1).World)
            .AddService(service)
            .AddEvent<Harvested>(new ServiceHandler(service))
            .Build();

        game.Emit(new Harvested(7));
    }

    [Fact]
    public void EmittingThroughTheEventInterfaceUsesTheRuntimeType()
    {
        var content = Content();
        var seen = new List<int>();
        using var game = new SystemicGameBuilder(content, content.CreateWorld(seed: 9, valueScale: 1).World)
            .AddEvent<Harvested>(new RecordingHandler(seen))
            .Build();
        ISystemicEvent @event = new Harvested(7);

        game.Emit(@event);

        Assert.Equal([7], seen);
    }

    [Fact]
    public void HandlersRegisteredForABaseEventReceiveDerivedEvents()
    {
        var content = Content();
        var seen = new List<int>();
        using var game = new SystemicGameBuilder(content, content.CreateWorld(seed: 9, valueScale: 1).World)
            .AddEvent<DomainEvent>(new DomainHandler(seen))
            .Build();

        game.Emit(new Harvested(7));

        Assert.Equal([7], seen);
    }

    [Fact]
    public void HandlersRegisteredForTheEventInterfaceReceiveEveryEvent()
    {
        var content = Content();
        var seen = new List<string>();
        using var game = new SystemicGameBuilder(content, content.CreateWorld(seed: 9, valueScale: 1).World)
            .AddEvent<ISystemicEvent>(new AnyEventHandler(seen))
            .Build();

        game.Emit(new Harvested(7));

        Assert.Equal([nameof(Harvested)], seen);
    }

    [Fact]
    public void FailedEventWithAFailingRollbackPreservesBothFailures()
    {
        var content = Content();
        var service = new BadRestoreService();
        using var game = new SystemicGameBuilder(content, content.CreateWorld(seed: 9, valueScale: 1).World)
            .AddService(service)
            .AddEvent<Harvested>(new ThrowAfterMutationHandler(service))
            .Build();

        var failure = Assert.Throws<AggregateException>(() => game.Emit(new Harvested(7))).Flatten();

        Assert.Contains(failure.InnerExceptions, error => error.Message == "expected");
        Assert.Contains(failure.InnerExceptions, error => error.Message == "restore failed");
    }

    [Fact]
    public void EventContextExpiresAfterDispatch()
    {
        var content = Content();
        SystemicEventContext? captured = null;
        using var game = new SystemicGameBuilder(content, content.CreateWorld(seed: 9, valueScale: 1).World)
            .AddEvent<Harvested>(new CaptureContextHandler(context => captured = context))
            .Build();

        game.Emit(new Harvested(7));

        Assert.NotNull(captured);
        Assert.Throws<InvalidOperationException>(() => _ = captured.World);
        Assert.Throws<InvalidOperationException>(() => captured.GetService<TestService>());
    }

    [Fact]
    public void FailedEventHandlerRestoresTransactionalServiceState()
    {
        var content = Content();
        var service = new RollbackService();
        using var game = new SystemicGameBuilder(content, content.CreateWorld(seed: 9, valueScale: 1).World)
            .AddService(service)
            .AddEvent<Harvested>(new ThrowAfterMutationHandler(service))
            .Build();

        Assert.Throws<InvalidOperationException>(() => game.Emit(new Harvested(7)));

        Assert.Equal(0, service.Value);
    }

    [Fact]
    public void EmittingAnEventWithoutHandlersIsANoOp()
    {
        var content = Content();
        using var game = new SystemicGameBuilder(content, new World(seed: 9, valueScale: 1)).Build();

        game.Emit(new Harvested(7));
    }

    [Fact]
    public void SystemsEmitTypedEventsThatDispatchAfterTheTick()
    {
        var content = Content();
        var world = content.CreateWorld(seed: 9, valueScale: 1).World;
        var target = world.Add(new DataEntity([], [], ("value", 0)));
        var seen = new List<string>();
        using var game = new SystemicGameBuilder(content, world)
            .AddSystem(new StepAndEmit(target.Id))
            .AddEvent<Harvested>(new RecordingTextHandler(seen))
            .AddEvent<Harvested>(new PropertyHandler(seen, target.Id))
            .Build();

        game.Step();

        Assert.Equal(["event:7", "property:1"], seen);
    }

    [Fact]
    public void DeferredEventsDispatchInSystemOrder()
    {
        var content = Content();
        var world = content.CreateWorld(seed: 9, valueScale: 1).World;
        var seen = new List<int>();
        using var game = new SystemicGameBuilder(content, world)
            .AddSystem(new EmitOrdered("test.emit.a", 1))
            .AddSystem(new EmitOrdered("test.emit.b", 2))
            .AddEvent<Harvested>(new RecordingHandler(seen))
            .Build();

        game.Step();

        Assert.Equal([1, 2], seen);
    }

    [Fact]
    public void ARolledBackTickDoesNotDispatchQueuedEvents()
    {
        var content = Content();
        var world = content.CreateWorld(seed: 9, valueScale: 1).World;
        var seen = new List<int>();
        using var game = new SystemicGameBuilder(content, world)
            .AddSystem(new EmitThenThrow())
            .AddEvent<Harvested>(new RecordingHandler(seen))
            .Build();

        Assert.Throws<InvalidOperationException>(() => game.Step());

        Assert.Empty(seen);
    }

    private sealed class StepAndEmit(int entityId) : ISystemicSystem
    {
        public string Id => "test.step.emit";
        public IReadOnlyList<SystemAccess> Access => [SystemAccess.WriteWorld];

        public void Step(SystemicSystemContext context)
        {
            context.Commands.SetProperty(entityId, "value", 1);
            context.EmitEvent(new Harvested(7));
        }
    }

    private sealed class EmitOrdered(string id, int amount) : ISystemicSystem
    {
        public string Id => id;

        public void Step(SystemicSystemContext context) => context.EmitEvent(new Harvested(amount));
    }

    private sealed class EmitThenThrow : ISystemicSystem
    {
        public string Id => "test.emit.throws";

        public void Step(SystemicSystemContext context)
        {
            context.EmitEvent(new Harvested(7));
            throw new InvalidOperationException("expected");
        }
    }

    private sealed class RecordingTextHandler(List<string> seen) : ISystemicEventHandler<Harvested>
    {
        public void Handle(Harvested harvested, SystemicEventContext context) =>
            seen.Add($"event:{harvested.Amount}");
    }

    private sealed class PropertyHandler(List<string> seen, int entityId) : ISystemicEventHandler<Harvested>
    {
        public void Handle(Harvested harvested, SystemicEventContext context)
        {
            var value = context.World.TryGetProperty(entityId, "value", out var property) ? property : 0;
            seen.Add($"property:{(int)value}");
        }
    }

    private static ContentPack Content() =>
        SystemicTestContent.Load(Path.Combine(AppContext.BaseDirectory, "samples", "incremental-quarry"))
            with
        { Modules = [] };

    private abstract record DomainEvent(int Amount) : ISystemicEvent;

    private sealed record Harvested(int Amount) : DomainEvent(Amount);

    private sealed class TestService;

    private sealed class ServiceHandler(TestService service) : ISystemicEventHandler<Harvested>
    {
        public void Handle(Harvested harvested, SystemicEventContext context) =>
            Assert.Same(service, context.GetService<TestService>());
    }

    private sealed class RecordingHandler(List<int> seen) : ISystemicEventHandler<Harvested>
    {
        public void Handle(Harvested harvested, SystemicEventContext context) => seen.Add(harvested.Amount);
    }

    private sealed class CaptureContextHandler(Action<SystemicEventContext> capture) : ISystemicEventHandler<Harvested>
    {
        public void Handle(Harvested harvested, SystemicEventContext context) => capture(context);
    }

    private interface IMutableService
    {
        void Set(int value);
    }

    private sealed class ThrowAfterMutationHandler(IMutableService service) : ISystemicEventHandler<Harvested>
    {
        public void Handle(Harvested harvested, SystemicEventContext context)
        {
            service.Set(5);
            throw new InvalidOperationException("expected");
        }
    }

    private sealed class RollbackService : ISystemicTransactionalState, IMutableService
    {
        public string Id => "test.event.rollback";
        public int Value { get; private set; }

        public void Set(int value) => Value = value;

        public JsonElement CaptureState() => JsonSerializer.SerializeToElement(Value);

        public void RestoreState(JsonElement state) => Value = state.GetInt32();

        public object CaptureTransactionState() => Value;

        public void RestoreTransactionState(object state) => Value = (int)state;
    }

    private sealed class BadRestoreService : ISystemicTransactionalState, IMutableService
    {
        public string Id => "test.event.bad-restore";
        public int Value { get; private set; }

        public void Set(int value) => Value = value;

        public JsonElement CaptureState() => JsonSerializer.SerializeToElement(Value);

        public void RestoreState(JsonElement state) => Value = state.GetInt32();

        public object CaptureTransactionState() => Value;

        public void RestoreTransactionState(object state) =>
            throw new InvalidOperationException("restore failed");
    }

    private sealed class DomainHandler(List<int> seen) : ISystemicEventHandler<DomainEvent>
    {
        public void Handle(DomainEvent @event, SystemicEventContext context) => seen.Add(@event.Amount);
    }

    private sealed class AnyEventHandler(List<string> seen) : ISystemicEventHandler<ISystemicEvent>
    {
        public void Handle(ISystemicEvent @event, SystemicEventContext context) =>
            seen.Add(@event.GetType().Name);
    }

    private sealed class FirstHandler(List<string> seen) : ISystemicEventHandler<Harvested>
    {
        public void Handle(Harvested harvested, SystemicEventContext context)
        {
            Assert.Equal(0, context.Tick);
            Assert.True(context.World.ContainsEntity(1));
            seen.Add($"first:{harvested.Amount}");
        }
    }

    private sealed class SecondHandler(List<string> seen) : ISystemicEventHandler<Harvested>
    {
        public void Handle(Harvested harvested, SystemicEventContext context) =>
            seen.Add($"second:{harvested.Amount}");
    }
}
