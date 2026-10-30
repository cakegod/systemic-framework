using System.Text.Json.Nodes;
using Systemic.Content;
using Systemic.Host;
using Systemic.Kernel;
using Systemic.Presentation;
using Systemic.Rules;
using Systemic.Save;
using Xunit;

namespace Systemic.Tests.Presentation;

/// <summary>Regressions raised in review of the UI list, action, and scope-reset work.</summary>
public sealed class UiReviewRegressionTests
{
    [Fact]
    public void NestedListsStopAtTheEvaluationBudget()
    {
        var world = new World(seed: 92, valueScale: 1);
        for (var index = 0; index < 20; index++)
        {
            world.Add(new DataEntity(["ui.node"], []));
        }

        UiNode template = new("text", "leaf", null, null, []);
        for (var level = 0; level < 4; level++)
        {
            template = new UiNode("list", null, "list.tag:ui.node", null, [template]);
        }

        var exception = Assert.Throws<InvalidOperationException>(() =>
            DeclarativeUiEvaluator.Evaluate(new UiLayout("nested", template),
                new UiBindingContext(world, new Dictionary<string, int>())));

        Assert.Contains("nodes", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ListItemsCarryTheirEntityOnEveryNode()
    {
        var world = new World(seed: 93, valueScale: 1);
        var first = world.Add(new DataEntity(["ui.choice"], [], ("cost", 5)));
        var second = world.Add(new DataEntity(["ui.choice"], [], ("cost", 7)));
        var layout = new UiLayout("items", new UiNode("list", null, "list.tag:ui.choice", null,
        [
            new UiNode("row", null, null, null,
            [
                new UiNode("value", null, "item.cost", null, []),
                new UiNode("button", "Buy", null, "ui.buy", [], Amount: 1)
            ])
        ]));

        var state = DeclarativeUiEvaluator.Evaluate(layout, new UiBindingContext(world, new Dictionary<string, int>()));

        Assert.Equal(2, state.Children.Length);
        Assert.Equal(first.Id, state.Children[0].ItemEntityId);
        Assert.Equal(second.Id, state.Children[1].ItemEntityId);
        Assert.Equal(first.Id, state.Children[0].Children[0].ItemEntityId);
        Assert.Equal(second.Id, state.Children[1].Children[1].ItemEntityId);
    }

    [Fact]
    public void AListTemplateLoadsFromJsonAndCarriesEachItemsEntity()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "samples", "incremental-quarry");
        var root = Directory.CreateTempSubdirectory("systemic-ui-list-").FullName;
        try
        {
            foreach (var file in Directory.GetFiles(source))
            {
                File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
            }

            var uiPath = Path.Combine(root, "ui.json");
            var document = JsonNode.Parse(File.ReadAllText(uiPath))!.AsObject();
            document["layouts"]!.AsArray().Add(JsonNode.Parse("""
                {
                  "id": "loaded-list",
                  "root": {
                    "type": "list", "bind": "list.tag:upgrade",
                    "children": [
                      { "type": "row", "children": [
                        { "type": "value", "bind": "item.purchases" },
                        { "type": "toggle", "bind": "item.tag.tool" }
                      ] }
                    ]
                  }
                }
                """));
            File.WriteAllText(uiPath, document.ToJsonString());

            var content = SystemicTestContent.Load(root);
            var materialized = content.CreateWorld(seed: 87, valueScale: 1);
            var game = new SystemicGameBuilder(content, materialized).Build();

            var state = game.EvaluateUi("loaded-list");

            var item = Assert.Single(state.Children);
            Assert.Equal(materialized.EntityIds["quarry.drill-upgrade"], item.ItemEntityId);
            Assert.Equal(0, item.Children[0].NumberValue);
            Assert.True(item.Children[1].IsOn);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RejectsItemBindingsOutsideAListTemplate()
    {
        using var pack = TestPack.Create();
        File.WriteAllText(Path.Combine(pack.Root, "ui.json"), """
            { "schemaVersion": 2, "layouts": [
              { "id": "hud", "root": { "type": "value", "bind": "item.cost" } }
            ] }
            """);

        var error = Assert.Throws<ContentPackException>(() => SystemicTestContent.Load(pack.Root));

        Assert.Contains("outside a list template", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AResetClearsGrantedScopeTagsToo()
    {
        using var workspace = new ClearScopeWorkspace();
        var content = workspace.Load();
        var materialized = content.CreateWorld(seed: 94, valueScale: 1);
        var world = materialized.World;
        var engine = new RuleEngine(content.Rules);
        var meta = world.ById(materialized.EntityIds["meta.record"])!;
        world.Grant("test.scope", meta, GrantKind.Tag, "scope.run", remainingTicks: 5);
        Assert.True(world.HasTag(meta, "scope.run"));

        engine.Dispatch(world, new SimulationFact("run.reset", meta.Id, meta.Id));
        world.PruneRemoved();

        Assert.Null(world.ById(meta.Id));
    }

    [Fact]
    public void ASaveTakenAfterAScopeResetLoads()
    {
        using var workspace = new ClearScopeWorkspace();
        var content = workspace.Load();
        var host = HeadlessSystemicHost.Create(content, seed: 95);
        var runId = host.Game.EntityId("run.actor");
        var codec = new SystemicSaveCodec();

        host.Game.Publish(new SimulationFact("run.reset", runId, runId));
        host.Game.World.PruneRemoved();

        var saved = codec.Save(host.Game);
        var restored = codec.Load(content, saved);

        Assert.DoesNotContain(restored.World.Entities, entity => entity.HasTag("scope.run"));
        Assert.Contains(restored.World.Entities, entity => entity.HasTag("scope.meta"));
    }
}
