using System.Text.Json;
using Systemic.Content;
using Systemic.Host;
using Systemic.Kernel;
using Systemic.Save;

namespace Systemic.Tests.Host;

public sealed class SystemicGameModuleTests
{
    [Fact]
    public void ModuleComposesSystemsCommandsAndTracksItsVersion()
    {
        var content = Content();
        var world = new World(seed: 7, valueScale: 1);
        var target = world.Add(new DataEntity([], [], ("value", 0)));
        using var game = new SystemicGameBuilder(content, world)
            .AddModule(new TestGameModule(target.Id))
            .Build();

        game.Step();
        game.Execute(new AddValue(target.Id, 4));

        Assert.Equal(5, world.EffectiveProperty(target, "value"));
        Assert.Equal(5, game.ModuleVersions["test.game"]);
    }

    [Fact]
    public void DuplicateGameModuleIdsFailLoudly()
    {
        var content = Content();
        var builder = new SystemicGameBuilder(content, new World(seed: 7, valueScale: 1))
            .AddModule(new TestGameModule(targetId: 0));

        var error = Assert.Throws<InvalidOperationException>(() =>
            builder.AddModule(new TestGameModule(targetId: 0)));

        Assert.Contains("test.game", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateCommandHandlersFailLoudly()
    {
        var content = Content();
        var builder = new SystemicGameBuilder(content, new World(seed: 7, valueScale: 1))
            .AddCommand<AddValue>(new AddValueHandler());

        var error = Assert.Throws<InvalidOperationException>(() =>
            builder.AddCommand<AddValue>(new AddValueHandler()));

        Assert.Contains(typeof(AddValue).FullName!, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandHandlersCanReadRegisteredServices()
    {
        var content = Content();
        var world = new World(seed: 7, valueScale: 1);
        var target = world.Add(new DataEntity([], [], ("value", 0)));
        var service = new TestService();
        using var game = new SystemicGameBuilder(content, world)
            .AddService(service)
            .AddCommand<UseService>(new UseServiceHandler(service))
            .Build();

        game.Execute(new UseService(target.Id));

        Assert.Equal(9, world.EffectiveProperty(target, "value"));
    }

    [Fact]
    public void TheCommandJournalRecordsThePayloadAndTheTickItRanAgainst()
    {
        var content = Content();
        var world = new World(seed: 7, valueScale: 1);
        var target = world.Add(new DataEntity([], [], ("value", 0)));
        using var game = new SystemicGameBuilder(content, world)
            .AddCommand<AddValue>(new AddValueHandler())
            .Build();

        game.Execute(new AddValue(target.Id, 4));
        game.Step();
        game.Execute(new AddValue(target.Id, 2));

        Assert.Equal(2, game.CommandJournal.Count);
        var first = game.CommandJournal[0];
        Assert.Equal(typeof(AddValue).FullName, first.CommandType);
        Assert.Equal(0, first.Tick);
        Assert.Equal(0, first.Sequence);
        Assert.True(first.Accepted);
        Assert.Equal(target.Id, first.Payload.GetProperty("EntityId"u8).GetInt32());
        Assert.Equal(4, first.Payload.GetProperty("Amount"u8).GetDouble());
        Assert.Equal(1, game.CommandJournal[1].Tick);
        Assert.Equal(1, game.CommandJournal[1].Sequence);
    }

    [Fact]
    public void CommandContextExpiresAfterExecution()
    {
        var content = Content();
        SystemicCommandContext? captured = null;
        var capturedCommands = default(WorldCommandBuffer);
        using var game = new SystemicGameBuilder(content, new World(seed: 7, valueScale: 1))
            .AddCommand<CaptureContext>(new CaptureContextHandler(context =>
            {
                captured = context;
                capturedCommands = context.Commands;
            }))
            .Build();

        game.Execute(new CaptureContext());

        Assert.NotNull(captured);
        Assert.Throws<InvalidOperationException>(() => _ = captured.Commands);
        Assert.Throws<InvalidOperationException>(() => _ = captured.Tick);
        Assert.Throws<InvalidOperationException>(() => captured.GetService<TestService>());
        Assert.Throws<InvalidOperationException>(() => capturedCommands.SetProperty(1, "value", 1));
    }

    [Fact]
    public void FailedCommandRestoresTransactionalServiceState()
    {
        var content = Content();
        var world = new World(seed: 7, valueScale: 1);
        var target = world.Add(new DataEntity([], [], ("value", 0)));
        var service = new RollbackService();
        using var game = new SystemicGameBuilder(content, world)
            .AddService(service)
            .AddCommand<MutateThenThrow>(new MutateThenThrowHandler(service))
            .Build();

        Assert.Throws<InvalidOperationException>(() => game.Execute(new MutateThenThrow(target.Id)));

        Assert.Equal(0, service.Value);
        Assert.Equal(0, world.EffectiveProperty(target, "value"));
    }

    [Fact]
    public void MissingCommandHandlerFailsLoudlyAndDoesNotMutateTheWorld()
    {
        var content = Content();
        var world = new World(seed: 7, valueScale: 1);
        var target = world.Add(new DataEntity([], [], ("value", 0)));
        using var game = new SystemicGameBuilder(content, world)
            .AddModule(new TestGameModule(target.Id))
            .Build();

        var error = Assert.Throws<InvalidOperationException>(() =>
            game.Execute(new Unregistered(target.Id)));

        Assert.Contains(typeof(Unregistered).FullName!, error.Message, StringComparison.Ordinal);
        Assert.Equal(0, world.EffectiveProperty(target, "value"));
    }

    [Fact]
    public void AHandlerRegisteredForABaseCommandHandlesDerivedCommands()
    {
        var world = new World(seed: 7, valueScale: 1);
        var target = world.Add(new DataEntity([], [], ("value", 0)));
        using var game = new SystemicGameBuilder(EmptyContent(), world)
            .AddCommand<Adjust>(new AdjustHandler())
            .Build();

        game.Execute(new AdjustByTwo(target.Id));

        Assert.Equal(2, world.EffectiveProperty(target, "value"));
    }

    [Fact]
    public void ARejectedCommandRollsBackAndIsJournaled()
    {
        var world = new World(seed: 7, valueScale: 1);
        var target = world.Add(new DataEntity([], [], ("value", 0)));
        using var game = new SystemicGameBuilder(EmptyContent(), world)
            .AddCommand<Rejectable>(new RejectableHandler())
            .Build();

        Assert.True(game.TryExecute(new Rejectable(target.Id, 3), out var accepted));
        Assert.Equal(3, world.EffectiveProperty(target, "value"));

        Assert.False(game.TryExecute(new Rejectable(target.Id, -1), out var rejected));
        Assert.Equal(3, world.EffectiveProperty(target, "value"));
        Assert.Equal("amount.negative", rejected.Reason);
        Assert.Equal([true, false], game.CommandJournal.Select(record => record.Accepted));
        Assert.Equal([0, 1], game.CommandJournal.Select(record => record.Sequence));
        Assert.All(game.CommandJournal, record => Assert.Equal(0, record.Tick));
    }

    [Fact]
    public void AChangedModuleVersionRejectsAnOldSave()
    {
        var content = SystemicTestContent.Load(
            Path.Combine(AppContext.BaseDirectory, "samples", "incremental-quarry"));
        string save;
        using (var game = new SystemicGameBuilder(content, content.CreateWorld(seed: 5, valueScale: 1))
            .AddModule(new VersionedGameModule(5))
            .Build())
        {
            save = new SystemicSaveCodec().Save(game);
        }

        var restored = new SystemicSaveCodec().Load(content, save);
        var error = Assert.Throws<InvalidOperationException>(() =>
            new SystemicGameBuilder(content, restored).AddModule(new VersionedGameModule(6)));

        Assert.Contains("version 5", error.Message, StringComparison.Ordinal);
        Assert.Contains("version 6", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARestoredSaveAcceptsTheSameModuleVersion()
    {
        var content = SystemicTestContent.Load(
            Path.Combine(AppContext.BaseDirectory, "samples", "incremental-quarry"));
        using var original = new SystemicGameBuilder(content, content.CreateWorld(seed: 5, valueScale: 1))
            .AddModule(new VersionedGameModule(5))
            .Build();
        var save = new SystemicSaveCodec().Save(original);

        var restored = new SystemicSaveCodec().Load(content, save);
        using var game = new SystemicGameBuilder(content, restored)
            .AddModule(new VersionedGameModule(5))
            .Build();

        Assert.Equal(5, game.ModuleVersions["test.game"]);
    }

    [Fact]
    public void APackWithNoRulesRunsWithoutARuleEngineButKeepsFactObservers()
    {
        var observed = new List<string>();
        using var game = new SystemicGameBuilder(EmptyContent(), new World(seed: 1, valueScale: 1))
            .AddFactObserver(0, (fact, emitter) => observed.Add(fact.Type))
            .Build();

        Assert.False(game.SupportsRules);
        Assert.DoesNotContain(game.StatefulExtensions,
            extension => extension.Id == "systemic.rule-history");

        var result = game.Publish(new Systemic.Rules.SimulationFact("test.fact", 1, 1));

        Assert.Equal(0, result.RulesMatched);
        Assert.Equal(["test.fact"], observed);
    }

    [Fact]
    public void AFailedModuleComposeMakesTheBuilderUnusable()
    {
        var builder = new SystemicGameBuilder(EmptyContent(), new World(seed: 1, valueScale: 1));

        Assert.Throws<InvalidOperationException>(() => builder.AddModule(new BrokenGameModule()));
        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    private static ContentPack EmptyContent() => new(
        Path.GetTempPath(),
        new GameManifest("test.empty", "Empty", 60, 4),
        new Systemic.Rules.CompiledRuleSet([]),
        new CompiledEntities([]));

    private record Adjust(int EntityId) : ISystemicCommand;

    private sealed record AdjustByTwo(int EntityId) : Adjust(EntityId);

    private sealed class AdjustHandler : ISystemicCommandHandler<Adjust>
    {
        public void Handle(Adjust command, SystemicCommandContext context) =>
            context.Commands.SetProperty(command.EntityId, "value", 2);
    }

    private sealed record Rejectable(int EntityId, int Amount) : ISystemicCommand;

    private sealed class RejectableHandler : ISystemicCommandHandler<Rejectable>
    {
        public void Handle(Rejectable command, SystemicCommandContext context)
        {
            if (command.Amount < 0)
            {
                context.Reject("amount.negative");
                return;
            }

            context.Commands.SetProperty(command.EntityId, "value", command.Amount);
        }
    }

    private sealed class VersionedGameModule(int version) : ISystemicGameModule
    {
        public string Id => "test.game";
        public int Version => version;

        public void Compose(SystemicGameBuilder builder) =>
            builder.AddCommand<AddValue, AddValueHandler>();
    }

    private sealed class BrokenGameModule : ISystemicGameModule
    {
        public string Id => "test.broken";
        public int Version => 1;

        public void Compose(SystemicGameBuilder builder)
        {
            builder.AddSystem(new TickSystem(targetId: 0));
            throw new InvalidOperationException("composition failed");
        }
    }

    private static ContentPack Content() =>
        SystemicTestContent.Load(Path.Combine(AppContext.BaseDirectory, "samples", "incremental-quarry"))
            with
        { Modules = [] };

    private sealed class TestGameModule(int targetId) : ISystemicGameModule
    {
        public string Id => "test.game";
        public int Version => 5;

        public void Compose(SystemicGameBuilder builder) =>
            builder
                .AddSystem(new TickSystem(targetId))
                .AddCommand<AddValue, AddValueHandler>();
    }

    private sealed class TickSystem(int targetId) : ISystemicSystem
    {
        public string Id => "test.tick";
        public IReadOnlyList<SystemAccess> Access => [SystemAccess.WriteWorld];

        public void Step(SystemicSystemContext context) =>
            context.Commands.SetProperty(targetId, "value", 1);
    }

    private sealed class AddValueHandler : ISystemicCommandHandler<AddValue>
    {
        public void Handle(AddValue command, SystemicCommandContext context)
        {
            var current = context.World.TryGetProperty(command.EntityId, "value", out var value) ? value : 0;
            context.Commands.SetProperty(command.EntityId, "value", current + command.Amount);
        }
    }

    private sealed class TestService;

    private sealed class UseServiceHandler(TestService service) : ISystemicCommandHandler<UseService>
    {
        public void Handle(UseService command, SystemicCommandContext context)
        {
            Assert.Same(service, context.GetService<TestService>());
            context.Commands.SetProperty(command.EntityId, "value", 9);
        }
    }

    private sealed class CaptureContextHandler(Action<SystemicCommandContext> capture)
        : ISystemicCommandHandler<CaptureContext>
    {
        public void Handle(CaptureContext command, SystemicCommandContext context) => capture(context);
    }

    private sealed class MutateThenThrowHandler(RollbackService service)
        : ISystemicCommandHandler<MutateThenThrow>
    {
        public void Handle(MutateThenThrow command, SystemicCommandContext context)
        {
            service.Set(5);
            context.Commands.SetProperty(command.EntityId, "value", 3);
            throw new InvalidOperationException("expected");
        }
    }

    private sealed class RollbackService : ISystemicTransactionalState
    {
        public string Id => "test.rollback";
        public int Value { get; private set; }

        public void Set(int value) => Value = value;

        public JsonElement CaptureState() => JsonSerializer.SerializeToElement(Value);

        public void RestoreState(JsonElement state) => Value = state.GetInt32();

        public object CaptureTransactionState() => Value;

        public void RestoreTransactionState(object state) => Value = (int)state;
    }

    private sealed record AddValue(int EntityId, double Amount) : ISystemicCommand;

    private sealed record UseService(int EntityId) : ISystemicCommand;

    private sealed record CaptureContext : ISystemicCommand;

    private sealed record MutateThenThrow(int EntityId) : ISystemicCommand;

    private sealed record Unregistered(int EntityId) : ISystemicCommand;
}
