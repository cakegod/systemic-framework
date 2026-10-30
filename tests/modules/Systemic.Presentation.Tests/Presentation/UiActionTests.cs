using Systemic.Content;
using Systemic.Host;
using Systemic.Presentation;
using Systemic.Rules;

namespace Systemic.Tests.Presentation;

public sealed class UiActionTests
{
    private const string AdjustHeatRule = """
        { "schemaVersion": 2, "rules": [
          { "id": "ui.buy", "on": "ui.buy", "priority": 0,
            "effects": [{ "kind": "adjustProperty", "subject": "target", "property": "heat", "amount": 1 }] }
        ] }
        """;

    private const string CaptureRule = """
        { "schemaVersion": 2, "rules": [
          { "id": "ui.buy", "on": "ui.buy", "priority": 0,
            "effects": [{ "kind": "action", "subject": "target", "action": "capture" }] }
        ] }
        """;

    [Fact]
    public void InvokeUiActionPublishesTheDeclaredActionAndReportsTheDispatchResult()
    {
        using var pack = PresentationPack.Create();
        WriteContent(pack.Root, AdjustHeatRule, "<<buy>>");
        var content = ContentPackLoader.Load(pack.Root,
            new ContentModuleRegistry().Add(new PresentationContentModule()));
        var materialized = content.CreateWorld(seed: 23, valueScale: 1);
        var game = new SystemicGameBuilder(content, materialized).Build();
        var actorId = materialized.EntityIds["actor"];

        var result = game.InvokeUiAction("hud", "ui.buy", actorId);

        Assert.Equal("hud", result.LayoutId);
        Assert.Equal("ui.buy", result.ActionId);
        Assert.True(result.Handled);
        Assert.False(result.RolledBack);
        Assert.Equal(3, game.EvaluateUi("hud").Children[0].NumberValue);
    }

    [Fact]
    public void InvokeUiActionRejectsAnActionTheLayoutDoesNotDeclare()
    {
        using var pack = PresentationPack.Create();
        WriteContent(pack.Root, AdjustHeatRule, "<<buy>>");
        var content = ContentPackLoader.Load(pack.Root,
            new ContentModuleRegistry().Add(new PresentationContentModule()));
        var materialized = content.CreateWorld(seed: 24, valueScale: 1);
        var game = new SystemicGameBuilder(content, materialized).Build();

        var exception = Assert.Throws<KeyNotFoundException>(() =>
            game.InvokeUiAction("hud", "ui.missing", materialized.EntityIds["actor"]));

        Assert.Contains("ui.missing", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InvokeUiActionReportsUnhandledWhenNoRuleMatchesTheAction()
    {
        using var pack = PresentationPack.Create();
        WriteContent(pack.Root, """
            { "schemaVersion": 2, "rules": [
              { "id": "ui.other", "on": "ui.other", "priority": 0,
                "effects": [{ "kind": "adjustProperty", "subject": "target", "property": "heat", "amount": 1 }] }
            ] }
            """, "<<buy>>");
        var content = ContentPackLoader.Load(pack.Root,
            new ContentModuleRegistry().Add(new PresentationContentModule()));
        var materialized = content.CreateWorld(seed: 26, valueScale: 1);
        var game = new SystemicGameBuilder(content, materialized).Build();

        var result = game.InvokeUiAction("hud", "ui.buy", materialized.EntityIds["actor"]);

        Assert.False(result.Handled);
        Assert.False(result.RolledBack);
        Assert.Equal(2, game.EvaluateUi("hud").Children[0].NumberValue);
    }

    [Fact]
    public void InvokeUiActionReportsUnhandledWhenTheRuleConditionFails()
    {
        using var pack = PresentationPack.Create();
        WriteContent(pack.Root, """
            { "schemaVersion": 2, "rules": [
              { "id": "ui.buy", "on": "ui.buy", "priority": 0,
                "when": [{ "kind": "hasTag", "subject": "target", "tag": "missing" }],
                "effects": [{ "kind": "adjustProperty", "subject": "target", "property": "heat", "amount": 1 }] } ] }
            """, "<<buy>>");
        var content = ContentPackLoader.Load(pack.Root,
            new ContentModuleRegistry().Add(new PresentationContentModule()));
        var materialized = content.CreateWorld(seed: 26, valueScale: 1);
        var game = new SystemicGameBuilder(content, materialized).Build();

        var result = game.InvokeUiAction("hud", "ui.buy", materialized.EntityIds["actor"]);

        Assert.False(result.Handled);
        Assert.False(result.RolledBack);
        Assert.Equal(2, game.EvaluateUi("hud").Children[0].NumberValue);
    }

    [Fact]
    public void InvokeUiActionCarriesTheButtonAmountOnTheFact()
    {
        using var pack = PresentationPack.Create();
        WriteContent(pack.Root, CaptureRule, """
            { "type": "button", "text": "Buy", "action": "ui.buy", "amount": 10 }
            """);
        var content = ContentPackLoader.Load(pack.Root,
            new ContentModuleRegistry().Add(new PresentationContentModule()));
        var materialized = content.CreateWorld(seed: 25, valueScale: 1);
        var captured = double.NaN;
        var game = new SystemicGameBuilder(content, materialized)
            .AddRuleAction("capture", (in RuleActionContext context) => captured = context.Fact.Amount)
            .Build();

        game.InvokeUiAction("hud", "ui.buy", materialized.EntityIds["actor"]);

        Assert.Equal(10, captured);
    }

    [Fact]
    public void TwoBuyAmountsRequireTheClickedAmountAndUseItOnTheFact()
    {
        using var pack = PresentationPack.Create();
        WriteContent(pack.Root, CaptureRule, """
            { "type": "button", "text": "Buy", "action": "ui.buy", "amount": 1 },
            { "type": "button", "text": "Buy x10", "action": "ui.buy", "amount": 10 }
            """);
        var content = ContentPackLoader.Load(pack.Root,
            new ContentModuleRegistry().Add(new PresentationContentModule()));
        var materialized = content.CreateWorld(seed: 27, valueScale: 1);
        var captured = new List<double>();
        var game = new SystemicGameBuilder(content, materialized)
            .AddRuleAction("capture", (in RuleActionContext context) => captured.Add(context.Fact.Amount))
            .Build();

        var exception = Assert.Throws<ArgumentException>(() =>
            game.InvokeUiAction("hud", "ui.buy", materialized.EntityIds["actor"]));
        Assert.Contains("amount", exception.Message, StringComparison.OrdinalIgnoreCase);

        game.InvokeUiAction("hud", "ui.buy", materialized.EntityIds["actor"], amount: 10);

        Assert.Equal([10d], captured);
    }

    private static void WriteContent(string root, string rules, string button)
    {
        var buttonJson = button == "<<buy>>"
            ? """
              { "type": "button", "text": "Buy", "action": "ui.buy" }
              """
            : button;
        File.WriteAllText(Path.Combine(root, "entities.json"), """
            { "schemaVersion": 2, "entities": [
              { "id": "actor", "kind": "actor", "properties": { "heat": 2 } }
            ] }
            """);
        File.WriteAllText(Path.Combine(root, "rules.json"), rules);
        File.WriteAllText(Path.Combine(root, "ui.json"), $$"""
            { "schemaVersion": 2, "layouts": [{ "id": "hud", "root": { "type": "column", "children": [
              { "type": "value", "bind": "world.actor.heat" },
              {{buttonJson}}
            ] } }] }
            """);
    }
}
