using System.Collections.Immutable;
using Systemic.Content;
using Systemic.Kernel;
using Systemic.Presentation;

namespace Systemic.Tests.Presentation;

public sealed class UiWidgetTests
{
    [Fact]
    public void ToggleTooltipAndBuyAmountSurfaceOnNodeState()
    {
        var layout = new UiLayout("shop", new UiNode("column", null, null, null,
        [
            new UiNode("toggle", "Auto", "selection.auto", null, [], Tooltip: "Turns automation on"),
            new UiNode("button", "Buy", null, "ui.buy", [], Amount: 10)
        ]));
        var context = new UiBindingContext(new World(seed: 41, valueScale: 1),
            ImmutableDictionary<string, int>.Empty,
            ImmutableDictionary<string, double>.Empty.Add("auto", 1));

        var frame = DeclarativeUiEvaluator.Evaluate(layout, context);

        Assert.True(frame.Children[0].IsOn);
        Assert.Equal("Turns automation on", frame.Children[0].Tooltip);
        Assert.Equal(10, frame.Children[1].Amount);
    }

    [Fact]
    public void ToggleIsOffWhenItsBindingIsZeroOrMissing()
    {
        var layout = new UiLayout("shop", new UiNode("column", null, null, null,
        [
            new UiNode("toggle", "Auto", "selection.auto", null, []),
            new UiNode("toggle", "Boost", "selection.missing", null, [])
        ]));
        var context = new UiBindingContext(new World(seed: 42, valueScale: 1),
            ImmutableDictionary<string, int>.Empty,
            ImmutableDictionary<string, double>.Empty.Add("auto", 0));

        var frame = DeclarativeUiEvaluator.Evaluate(layout, context);

        Assert.False(frame.Children[0].IsOn);
        Assert.False(frame.Children[1].IsOn);
    }

    [Fact]
    public void BuyAmountIsRejectedOutsideAButton()
    {
        using var pack = PresentationPack.Create();
        File.WriteAllText(Path.Combine(pack.Root, "ui.json"), """
            { "schemaVersion": 2, "layouts": [{ "id": "hud", "root": { "type": "column", "children": [
              { "type": "button", "text": "Buy", "action": "ui.buy", "amount": 10 },
              { "type": "value", "bind": "world.tick", "amount": 5 }
            ] } }] }
            """);

        var exception = Assert.Throws<ContentPackException>(() => ContentPackLoader.Load(pack.Root,
            new ContentModuleRegistry().Add(new PresentationContentModule())));

        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Contains("cannot define a buy amount", diagnostic.Message, StringComparison.Ordinal);
        Assert.EndsWith(".children[1].amount", diagnostic.Path, StringComparison.Ordinal);
    }
}
