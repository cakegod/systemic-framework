using Systemic.Content;
using Systemic.Host;
using Systemic.Presentation;
using Systemic.Rules;

namespace Systemic.Tests.Presentation;

public sealed class UiCommandActionTests
{
    [Fact]
    public void InvokeUiActionRunsABoundTypedCommandWithTheClickedAmount()
    {
        using var pack = PresentationPack.Create();
        WriteContent(pack.Root, """
            { "type": "button", "text": "Buy", "action": "ui.buy", "amount": 10 }
            """);
        var content = ContentPackLoader.Load(pack.Root,
            new ContentModuleRegistry().Add(new PresentationContentModule()));
        var materialized = content.CreateWorld(seed: 31, valueScale: 1);
        var actorId = materialized.EntityIds["actor"];
        using var game = new SystemicGameBuilder(content, materialized)
            .AddCommand<BuyCommand, BuyHandler>()
            .BindUiAction<BuyCommand>("ui.buy", action => new BuyCommand(action.OwnerEntityId, action.Amount))
            .Build();

        var result = game.InvokeUiAction("hud", "ui.buy", actorId);

        Assert.True(result.Handled);
        Assert.False(result.RolledBack);
        Assert.Equal(12, game.World.EffectiveProperty(game.World.ById(actorId)!, "heat"));
    }

    [Fact]
    public void ExecutingTheDefaultUiActionFailsWithAUsageError()
    {
        using var pack = PresentationPack.Create();
        WriteContent(pack.Root, """
            { "type": "button", "text": "Buy", "action": "ui.buy" }
            """);
        var content = ContentPackLoader.Load(pack.Root,
            new ContentModuleRegistry().Add(new PresentationContentModule()));
        using var game = new SystemicGameBuilder(content, content.CreateWorld(seed: 34, valueScale: 1))
            .AddCommand<BuyCommand, BuyHandler>()
            .BindUiAction<BuyCommand>("ui.buy", action => new BuyCommand(action.OwnerEntityId, action.Amount))
            .Build();

        // A null action ID surfaces as an ArgumentNullException, a subclass of ArgumentException.
        Assert.ThrowsAny<ArgumentException>(() => game.TryExecuteUiAction(default));
    }

    [Fact]
    public void BindingAnActionNotDeclaredByTheUiFailsAtBuild()
    {
        using var pack = PresentationPack.Create();
        WriteContent(pack.Root, """
            { "type": "button", "text": "Buy", "action": "ui.buy" }
            """);
        var content = ContentPackLoader.Load(pack.Root,
            new ContentModuleRegistry().Add(new PresentationContentModule()));
        var builder = new SystemicGameBuilder(content, content.CreateWorld(seed: 33, valueScale: 1))
            .AddCommand<BuyCommand, BuyHandler>()
            .BindUiAction<BuyCommand>("ui.missing", action => new BuyCommand(action.OwnerEntityId, action.Amount));

        var error = Assert.Throws<InvalidOperationException>(() => { _ = builder.Build(); });

        Assert.Contains("ui.missing", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BindingACommandWithoutAHandlerFailsAtBuild()
    {
        using var pack = PresentationPack.Create();
        WriteContent(pack.Root, """
            { "type": "button", "text": "Buy", "action": "ui.buy" }
            """);
        var content = ContentPackLoader.Load(pack.Root,
            new ContentModuleRegistry().Add(new PresentationContentModule()));
        var builder = new SystemicGameBuilder(content, content.CreateWorld(seed: 32, valueScale: 1))
            .BindUiAction<BuyCommand>("ui.buy", action => new BuyCommand(action.OwnerEntityId, action.Amount));

        var error = Assert.Throws<InvalidOperationException>(() => { _ = builder.Build(); });

        Assert.Contains(typeof(BuyCommand).FullName!, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABoundActionWithoutALoadedUiModuleFailsAtBuild()
    {
        using var pack = PresentationPack.Create();
        WriteContent(pack.Root, """
            { "type": "button", "text": "Buy", "action": "ui.buy" }
            """);
        var content = ContentPackLoader.Load(pack.Root,
            new ContentModuleRegistry().Add(new PresentationContentModule())) with
        {
            Modules = [],
        };
        var builder = new SystemicGameBuilder(content, content.CreateWorld(seed: 36, valueScale: 1))
            .AddCommand<BuyCommand, BuyHandler>()
            .BindUiAction<BuyCommand>("ui.buy", action => new BuyCommand(action.OwnerEntityId, action.Amount));

        var error = Assert.Throws<InvalidOperationException>(() => { _ = builder.Build(); });

        Assert.Contains("no loaded content module", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARejectedCommandReportsItsReasonAndRollsBack()
    {
        using var pack = PresentationPack.Create();
        WriteContent(pack.Root, """
            { "type": "button", "text": "Buy", "action": "ui.buy", "amount": 10 }
            """);
        var content = ContentPackLoader.Load(pack.Root,
            new ContentModuleRegistry().Add(new PresentationContentModule()));
        var materialized = content.CreateWorld(seed: 37, valueScale: 1);
        var actorId = materialized.EntityIds["actor"];
        using var game = new SystemicGameBuilder(content, materialized)
            .AddCommand<BuyCommand, RejectingBuyHandler>()
            .BindUiAction<BuyCommand>("ui.buy", action => new BuyCommand(action.OwnerEntityId, action.Amount))
            .Build();

        var result = game.InvokeUiAction("hud", "ui.buy", actorId);

        Assert.False(result.Handled);
        Assert.True(result.Rejected);
        Assert.Equal("insufficient.heat", result.Reason);
        Assert.Equal(2, game.World.EffectiveProperty(game.World.ById(actorId)!, "heat"));
        Assert.False(game.CommandJournal[0].Accepted);
    }

    [Fact]
    public void ADataOnlyUiActionStillPublishesItsFactToObservers()
    {
        using var pack = PresentationPack.Create();
        WriteContent(pack.Root, """
            { "type": "button", "text": "Pulse", "action": "ui.pulse", "amount": 5 }
            """);
        var content = ContentPackLoader.Load(pack.Root,
            new ContentModuleRegistry().Add(new PresentationContentModule()));
        var actorId = content.CreateWorld(seed: 38, valueScale: 1).EntityIds["actor"];
        var observed = new List<SimulationFact>();
        using var game = new SystemicGameBuilder(content, content.CreateWorld(seed: 38, valueScale: 1))
            .AddFactObserver(0, (fact, _) => observed.Add(fact))
            .Build();
        Assert.False(game.SupportsRules);

        var result = game.InvokeUiAction("hud", "ui.pulse", actorId);

        var fact = Assert.Single(observed);
        Assert.Equal("ui.pulse", fact.Type);
        Assert.Equal(actorId, fact.SourceId);
        Assert.Equal(actorId, fact.TargetId);
        Assert.Equal(5, fact.Amount);
        Assert.False(result.RolledBack);
    }

    private sealed class RejectingBuyHandler : ISystemicCommandHandler<BuyCommand>
    {
        public void Handle(BuyCommand command, SystemicCommandContext context)
        {
            var heat = context.World.TryGetProperty(command.EntityId, "heat", out var value) ? value : 0;
            if (command.Amount > heat)
            {
                context.Reject("insufficient.heat");
                return;
            }

            context.Commands.SetProperty(command.EntityId, "heat", heat - command.Amount);
        }
    }

    private static void WriteContent(string root, string button)
    {
        File.WriteAllText(Path.Combine(root, "entities.json"), """
            { "schemaVersion": 2, "entities": [
              { "id": "actor", "kind": "actor", "properties": { "heat": 2 } }
            ] }
            """);
        File.WriteAllText(Path.Combine(root, "rules.json"), "{ \"schemaVersion\": 2, \"rules\": [] }");
        File.WriteAllText(Path.Combine(root, "ui.json"), $$"""
            { "schemaVersion": 2, "layouts": [{ "id": "hud", "root": { "type": "column", "children": [
              { "type": "value", "bind": "world.actor.heat" },
              {{button}}
            ] } }] }
            """);
    }

    private sealed record BuyCommand(int EntityId, int Amount) : ISystemicCommand;

    private sealed class BuyHandler : ISystemicCommandHandler<BuyCommand>
    {
        public void Handle(BuyCommand command, SystemicCommandContext context)
        {
            var current = context.World.TryGetProperty(command.EntityId, "heat", out var heat) ? heat : 0;
            context.Commands.SetProperty(command.EntityId, "heat", current + command.Amount);
        }
    }
}
