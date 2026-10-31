using System.Collections.Immutable;
using Systemic.Kernel;
using Systemic.Content;
using Systemic.Host;
using Systemic.Presentation;
using Systemic.Rules;
using Systemic.Ui;

namespace Systemic.Tests.Presentation;

public sealed class DeclarativeUiTests
{
    [Fact]
    public void AContentRecordWithDefaultLayoutsDeclaresNoActions()
    {
        var content = new PresentationModuleContent(
            new CompiledVisuals(default),
            new CompiledUi(default),
            new PresentationCatalog(default, default),
            ImmutableDictionary<string, string>.Empty);

        Assert.False(content.DeclaresAction("ui.buy"));
    }

    [Fact]
    public void EvaluatesWorldSelectionAndEventBindingsIntoHostNeutralNodeState()
    {
        var world = new World(seed: 1, valueScale: 1);
        var actor = world.Add(new DataEntity(["worker"], [], ("heat", 14)));
        var layout = new UiLayout("hud", new UiNode("column", null, null, null,
        [
            new UiNode("value", "Heat", "world.actor.heat", null, []),
            new UiNode("bar", "Heat", "world.actor.heat", null, [], Minimum: 0, Maximum: 10),
            new UiNode("value", "Energy", "selection.energy", null, []),
            new UiNode("value", "Event", "event.type", null, [])
        ]));
        var context = new UiBindingContext(world,
            ImmutableDictionary<string, int>.Empty.Add("actor", actor.Id),
            ImmutableDictionary<string, double>.Empty.Add("energy", 7),
            new SimulationFact("contact", actor.Id, actor.Id));

        var frame = DeclarativeUiEvaluator.Evaluate(layout, context);

        Assert.Equal(14, frame.Children[0].NumberValue);
        Assert.Equal(1, frame.Children[1].Progress);
        Assert.Equal(7, frame.Children[2].NumberValue);
        Assert.Equal("contact", frame.Children[3].TextValue);
    }

    [Fact]
    public void CompiledBindingsPreserveWorldSelectionAndEventValues()
    {
        using var pack = PresentationPack.Create();
        File.WriteAllText(Path.Combine(pack.Root, "entities.json"), """
            { "schemaVersion": 2, "entities": [
              { "id": "actor", "kind": "actor", "properties": { "heat": 2 } },
              { "id": "actor.worker", "kind": "actor", "tags": ["worker"], "properties": { "heat": 14 } }
            ] }
            """);
        File.WriteAllText(Path.Combine(pack.Root, "ui.json"), """
            { "schemaVersion": 2, "layouts": [{ "id": "hud", "root": { "type": "column", "children": [
              { "type": "value", "bind": "world.tick" },
              { "type": "value", "bind": "world.actor.worker.heat" },
              { "type": "value", "bind": "world.actor.worker.tag.worker" },
              { "type": "bar", "bind": "world.actor.worker.heat", "minimum": 0, "maximum": 20 },
              { "type": "value", "bind": "selection.energy" },
              { "type": "value", "bind": "event.type" },
              { "type": "value", "bind": "event.sourceId" },
              { "type": "value", "bind": "event.targetId" },
              { "type": "value", "bind": "event.depth" }
            ] } }] }
            """);
        var content = ContentPackLoader.Load(pack.Root,
            new ContentModuleRegistry().Add(new PresentationContentModule()));
        var materialized = content.CreateWorld(seed: 17, valueScale: 1);
        materialized.World.LogicalTick = 9;
        var game = new SystemicGameBuilder(content, materialized).Build();
        var sourceId = materialized.EntityIds["actor.worker"];
        var targetId = materialized.EntityIds["actor"];

        var frame = game.EvaluateUi("hud", new Dictionary<string, double> { ["energy"] = 7 },
            new SimulationFact("contact", sourceId, targetId, 2));

        Assert.Equal(9, frame.Children[0].NumberValue);
        Assert.Equal(14, frame.Children[1].NumberValue);
        Assert.Equal(1, frame.Children[2].NumberValue);
        Assert.Equal(0.7, frame.Children[3].Progress);
        Assert.Equal(7, frame.Children[4].NumberValue);
        Assert.Equal("contact", frame.Children[5].TextValue);
        Assert.Equal(sourceId, frame.Children[6].NumberValue);
        Assert.Equal(targetId, frame.Children[7].NumberValue);
        Assert.Equal(2, frame.Children[8].NumberValue);
    }

    [Theory]
    [InlineData("world.actor.tag.", "empty tag binding")]
    [InlineData("world.actor.missing", "unknown property 'missing'")]
    [InlineData("world.ghost.heat", "unknown world entity or property")]
    public void InvalidWorldBindingsKeepCompilerDiagnostics(string binding, string message)
    {
        using var pack = PresentationPack.Create();
        File.WriteAllText(Path.Combine(pack.Root, "entities.json"), """
            { "schemaVersion": 2, "entities": [
              { "id": "actor", "kind": "actor", "properties": { "heat": 2 } }
            ] }
            """);
        WriteUiBinding(pack.Root, binding);

        var exception = Assert.Throws<ContentPackException>(() => ContentPackLoader.Load(pack.Root,
            new ContentModuleRegistry().Add(new PresentationContentModule())));

        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal("game.json", diagnostic.File);
        Assert.Equal("$.modules.systemic.presentation.layouts[0].root.bind", diagnostic.Path);
        Assert.Contains(message, diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BindingPlanUsesEachEvaluationContextAndProvider()
    {
        var layout = new UiLayout("context", new UiNode("column", null, null, null,
        [
            new UiNode("value", null, "world.tick", null, []),
            new UiNode("value", null, "selection.energy", null, []),
            new UiNode("value", null, "event.type", null, []),
            new UiNode("value", null, "selection.providerValue", null, []),
            new UiNode("value", null, "world.actor.missing", null, []),
            new UiNode("value", null, "extension.value", null, []),
            new UiNode("value", null, "choice.feature.selected", null, []),
            new UiNode("icon", null, "choice.feature.selected", null, [], VisualRecipeId: "spark")
        ]));
        var firstWorld = new World(seed: 18, valueScale: 1) { LogicalTick = 3 };
        var secondWorld = new World(seed: 19, valueScale: 1) { LogicalTick = 9 };
        var rejectingProvider = new RejectingUiProvider();
        var resolvingProvider = new ContextUiProvider();
        var firstContext = new UiBindingContext(firstWorld, ImmutableDictionary<string, int>.Empty,
            ImmutableDictionary<string, double>.Empty.Add("energy", 4),
            new SimulationFact("contact", 1, 2, 3), [rejectingProvider, resolvingProvider], choiceOwnerEntityId: 11);
        var secondContext = new UiBindingContext(secondWorld, ImmutableDictionary<string, int>.Empty,
            ImmutableDictionary<string, double>.Empty.Add("energy", 8), providers: [rejectingProvider, resolvingProvider],
            choiceOwnerEntityId: 22);

        var first = DeclarativeUiEvaluator.Evaluate(layout, firstContext);
        var second = DeclarativeUiEvaluator.Evaluate(layout, secondContext);

        Assert.Equal(3, first.Children[0].NumberValue);
        Assert.Equal(4, first.Children[1].NumberValue);
        Assert.Equal("contact", first.Children[2].TextValue);
        Assert.Equal(3, first.Children[3].NumberValue);
        Assert.Equal(3, first.Children[4].NumberValue);
        Assert.Equal(3, first.Children[5].NumberValue);
        Assert.Equal(11, first.Children[6].NumberValue);
        Assert.Equal("owner-11-tick-3", first.Children[7].VisualState);
        Assert.Equal(9, second.Children[0].NumberValue);
        Assert.Equal(8, second.Children[1].NumberValue);
        Assert.Equal("provider-event-9", second.Children[2].TextValue);
        Assert.Equal(9, second.Children[3].NumberValue);
        Assert.Equal(9, second.Children[4].NumberValue);
        Assert.Equal(9, second.Children[5].NumberValue);
        Assert.Equal(22, second.Children[6].NumberValue);
        Assert.Equal("owner-22-tick-9", second.Children[7].VisualState);
        Assert.Contains("extension.value", rejectingProvider.Bindings);
        Assert.Contains("choice.feature.selected", resolvingProvider.Bindings);
        Assert.DoesNotContain("event.type", resolvingProvider.Bindings.Take(3));
        Assert.Contains((11, 3), resolvingProvider.Contexts);
        Assert.Contains((22, 9), resolvingProvider.Contexts);
        Assert.Contains(("choice.feature.selected", 22, 9), resolvingProvider.VisualContexts);
    }

    [Fact]
    public void EmptyBindingPathsPreserveRuntimeSelectionEventAndProviderFallbacks()
    {
        var provider = new EmptyPathProvider();
        var layout = new UiLayout("empty-paths", new UiNode("column", null, null, null,
        [
            new UiNode("value", null, "selection.", null, []),
            new UiNode("value", null, "world.", null, []),
            new UiNode("value", null, "event.", null, [])
        ]));
        var context = new UiBindingContext(new World(seed: 20, valueScale: 1),
            ImmutableDictionary<string, int>.Empty,
            ImmutableDictionary<string, double>.Empty.Add(string.Empty, 4),
            new SimulationFact("contact", 1, 2), [provider]);

        var frame = DeclarativeUiEvaluator.Evaluate(layout, context);

        Assert.Equal(4, frame.Children[0].NumberValue);
        Assert.Equal(9, frame.Children[1].NumberValue);
        Assert.False(frame.Children[2].HasValue);
        Assert.Equal(["world."], provider.Bindings);
    }

    private static void WriteUiBinding(string packRoot, string binding) =>
        File.WriteAllText(Path.Combine(packRoot, "ui.json"), $$"""
            { "schemaVersion": 2, "layouts": [{ "id": "hud", "root": {
              "type": "value", "bind": "{{binding}}" } }] }
            """);

    private sealed class RejectingUiProvider : ISystemicUiBindingProvider
    {
        public List<string> Bindings { get; } = [];

        public bool TryResolve(string binding, SystemicUiBindingContext context, out SystemicUiBindingValue value)
        {
            Bindings.Add(binding);
            if (binding == "extension.value")
            {
                value = SystemicUiBindingValue.FromNumber(double.NaN);
                return true;
            }

            value = default;
            return false;
        }
    }

    private sealed class ContextUiProvider : ISystemicUiBindingProvider
    {
        public List<string> Bindings { get; } = [];
        public List<(int? OwnerId, int Tick)> Contexts { get; } = [];
        public List<(string Binding, int? OwnerId, int Tick)> VisualContexts { get; } = [];

        public bool TryResolve(string binding, SystemicUiBindingContext context, out SystemicUiBindingValue value)
        {
            Bindings.Add(binding);
            Contexts.Add((context.OwnerEntityId, context.World.LogicalTick));
            value = binding switch
            {
                "event.type" when context.EventFact is null => SystemicUiBindingValue.FromText($"provider-event-{context.World.LogicalTick}"),
                "selection.providerValue" or "world.actor.missing" or "extension.value" =>
                    SystemicUiBindingValue.FromNumber(context.World.LogicalTick),
                "choice.feature.selected" => SystemicUiBindingValue.FromNumber(context.OwnerEntityId ?? -1),
                _ => default,
            };
            return value.HasValue;
        }

        public string? ResolveVisualState(string binding, SystemicUiBindingContext context)
        {
            VisualContexts.Add((binding, context.OwnerEntityId, context.World.LogicalTick));
            return binding == "choice.feature.selected"
                ? $"owner-{context.OwnerEntityId}-tick-{context.World.LogicalTick}"
                : null;
        }
    }

    private sealed class EmptyPathProvider : ISystemicUiBindingProvider
    {
        public List<string> Bindings { get; } = [];

        public bool TryResolve(string binding, SystemicUiBindingContext context, out SystemicUiBindingValue value)
        {
            Bindings.Add(binding);
            value = binding is "world." or "event."
                ? SystemicUiBindingValue.FromNumber(9)
                : default;
            return value.HasValue;
        }
    }
}
