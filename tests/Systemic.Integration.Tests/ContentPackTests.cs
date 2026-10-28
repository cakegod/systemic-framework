using Systemic.Kernel;
using Systemic.Content;
using Systemic.Incremental;
using Systemic.Host;
using Systemic.Preview;
using Systemic.Rules;

namespace Systemic.Tests;

public sealed class ContentPackTests
{
    [Fact]
    public void Compiles_a_versioned_pack_and_keeps_stable_rule_order()
    {
        using var pack = TestPack.Create();

        var compiled = SystemicTestContent.Load(pack.Root);

        Assert.Equal("sample.quarry", compiled.Manifest.Id);
        Assert.Equal(60, compiled.Manifest.TickRateHz);
        Assert.Equal(["first", "second"], compiled.Rules.ForEvent("contact").Select(rule => rule.Id));
        Assert.Equal("spark", SystemicTestContent.Presentation(compiled).Visuals.Recipes.Single().Id);
        Assert.Equal("hud", SystemicTestContent.Presentation(compiled).Ui.Layouts.Single().Id);
        Assert.Empty(compiled.Entities.Definitions);
    }

    [Fact]
    public void Keeps_alternative_prerequisite_closures_that_avoid_later_exclusions()
    {
        using var pack = TestPack.Create();
        var gamePath = Path.Combine(pack.Root, "game.json");
        File.WriteAllText(gamePath, File.ReadAllText(gamePath)
            .Replace("\"ui\": \"ui.json\"", "\"ui\": \"ui.json\", \"advancements\": \"advancements.json\"", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(pack.Root, "entities.json"), """
            { "schemaVersion": 2, "entities": [
              { "id": "actor.owner", "kind": "actor", "properties": { "progress": 0 } },
              { "id": "choice.b", "kind": "prop", "properties": { "selected": 0 } },
              { "id": "choice.c", "kind": "prop", "properties": { "selected": 0 } },
              { "id": "choice.d", "kind": "prop", "properties": { "selected": 0 } },
              { "id": "choice.p", "kind": "prop", "properties": { "selected": 0 } },
              { "id": "choice.x", "kind": "prop", "properties": { "selected": 0 } },
              { "id": "choice.z", "kind": "prop", "properties": { "selected": 0 } }
            ] }
            """);
        File.WriteAllText(Path.Combine(pack.Root, "advancements.json"), """
            { "schemaVersion": 2,
              "groups": [{ "id": "talents", "ownerEntityId": "actor.owner",
                "progressEntityId": "actor.owner", "progressProperty": "progress",
                "unlockAt": 0, "selectionLimit": 5 }],
              "choices": [
                { "id": "choice.b", "groupId": "talents", "entityId": "choice.b", "activationMode": "passive" },
                { "id": "choice.d", "groupId": "talents", "entityId": "choice.d", "activationMode": "passive" },
                { "id": "choice.p", "groupId": "talents", "entityId": "choice.p", "activationMode": "passive",
                  "requiresAny": ["choice.b", "choice.c"] },
                { "id": "choice.x", "groupId": "talents", "entityId": "choice.x", "activationMode": "passive",
                  "excludes": ["choice.b"] },
                { "id": "choice.z", "groupId": "talents", "entityId": "choice.z", "activationMode": "passive",
                  "requires": ["choice.p", "choice.x"] },
                { "id": "choice.c", "groupId": "talents", "entityId": "choice.c", "activationMode": "passive",
                  "requires": ["choice.d"] }
              ] }
            """);

        var content = SystemicTestContent.Load(pack.Root);

        Assert.Equal(6, SystemicTestContent.Incremental(content).Choices.Choices.Length);
    }

    [Fact]
    public void Rejects_version_one_content_with_a_version_upgrade_diagnostic()
    {
        using var pack = TestPack.Create(schemaVersion: 1);

        var error = Assert.Throws<ContentPackException>(() => SystemicTestContent.Load(pack.Root));

        Assert.Contains("schemaVersion must be 2 or 3", error.Message, StringComparison.Ordinal);
        Assert.Contains("$.schemaVersion", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compiles_and_evaluates_expression_rule_conditions()
    {
        using var pack = TestPack.Create();
        File.WriteAllText(Path.Combine(pack.Root, "rules.json"), """
            {
              "schemaVersion": 2,
              "rules": [{ "id": "hot.contact", "on": "contact", "priority": 0,
                "when": [{ "kind": "expression", "expression": "target.heat >= 2 && count('fuel') > 0" }],
                "effects": [{ "kind": "adjustProperty", "subject": "target", "property": "power", "amount": 1 }] }]
            }
            """);
        var content = SystemicTestContent.Load(pack.Root);
        var world = new World(seed: 1, valueScale: 1);
        var source = world.Add(new DataEntity([], []));
        var target = world.Add(new DataEntity(["fuel"], [], ("heat", 2), ("power", 0)));

        var result = new RuleEngine(content.Rules).Dispatch(world,
            new SimulationFact("contact", source.Id, target.Id));

        Assert.Equal(1, result.RulesMatched);
        Assert.True(target.TryGetProperty(world, "power", out var power));
        Assert.Equal(1, power);
    }

    [Fact]
    public void Seeded_chance_conditions_replay_from_the_world_draw_stream()
    {
        using var pack = TestPack.Create();
        File.WriteAllText(Path.Combine(pack.Root, "rules.json"), """
            { "schemaVersion": 2,
              "rules": [{ "id": "chance.reward", "on": "chance", "priority": 0,
                "when": [{ "kind": "chance", "probability": 0.5 }],
                "effects": [{ "kind": "adjustProperty", "subject": "target", "property": "score", "amount": 1 }] }] }
            """);
        var content = SystemicTestContent.Load(pack.Root);
        var firstWorld = new World(seed: 812, valueScale: 1);
        var secondWorld = new World(seed: 812, valueScale: 1);
        var firstTarget = firstWorld.Add(new DataEntity([], [], ("score", 0)));
        var secondTarget = secondWorld.Add(new DataEntity([], [], ("score", 0)));
        var firstEngine = new RuleEngine(content.Rules);
        var secondEngine = new RuleEngine(content.Rules);

        for (var index = 0; index < 128; index++)
        {
            firstEngine.Dispatch(firstWorld, new SimulationFact("chance", firstTarget.Id, firstTarget.Id));
            secondEngine.Dispatch(secondWorld, new SimulationFact("chance", secondTarget.Id, secondTarget.Id));
        }

        Assert.True(firstTarget.TryGetProperty(firstWorld, "score", out var firstScore));
        Assert.True(secondTarget.TryGetProperty(secondWorld, "score", out var secondScore));
        Assert.Equal(firstScore, secondScore);
        Assert.InRange(firstScore, 0, 128);
        Assert.Equal(firstWorld.Draws.CaptureState(), secondWorld.Draws.CaptureState());
    }

    [Fact]
    public void Logical_time_schedules_repeat_from_the_saved_tick()
    {
        using var pack = TestPack.Create();
        File.WriteAllText(Path.Combine(pack.Root, "entities.json"), """
            { "schemaVersion": 2, "entities": [
              { "id": "actor.miner", "kind": "actor", "properties": { "score": 0 } }
            ] }
            """);
        File.WriteAllText(Path.Combine(pack.Root, "rules.json"), """
            { "schemaVersion": 2,
              "rules": [{ "id": "pulse.award", "on": "pulse", "priority": 0,
                "effects": [{ "kind": "adjustProperty", "subject": "target", "property": "score", "amount": 1 }] }],
              "schedules": [{ "id": "pulse.every-two", "event": "pulse", "everyTicks": 2,
                "offsetTicks": 0, "targetEntityId": "actor.miner" }] }
            """);
        var content = SystemicTestContent.Load(pack.Root);
        var host = HeadlessSystemicHost.Create(content, seed: 51);
        var minerId = host.Game.EntityId("actor.miner");

        host.RunTicks(2);
        var restored = HeadlessSystemicHost.Restore(content, host.Save());
        restored.RunTicks(2);

        var miner = restored.Game.World.ById(minerId)!;
        Assert.True(miner.TryGetProperty(restored.Game.World, "score", out var score));
        Assert.Equal(2, score);
    }

    [Fact]
    public void Rejects_content_paths_that_escape_the_pack_root()
    {
        using var pack = TestPack.Create(rulesPath: "../outside.json");

        var error = Assert.Throws<ContentPackException>(() => SystemicTestContent.Load(pack.Root));

        Assert.Contains("pack root", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_content_symlinks_that_escape_the_pack_root()
    {
        using var pack = TestPack.Create();
        var outsidePath = Path.Combine(Path.GetTempPath(), $"systemic-outside-{Guid.NewGuid():N}.json");
        var linkedPath = Path.Combine(pack.Root, "rules.json");
        File.WriteAllText(outsidePath, "{ \"schemaVersion\": 2, \"rules\": [] }");
        File.Delete(linkedPath);
        try
        {
            try
            {
                File.CreateSymbolicLink(linkedPath, outsidePath);
            }
            catch (Exception exception) when (exception is PlatformNotSupportedException or UnauthorizedAccessException)
            {
                throw Xunit.Sdk.SkipException.ForSkip($"File symlinks are unavailable: {exception.Message}");
            }

            var error = Assert.Throws<ContentPackException>(() => SystemicTestContent.Load(pack.Root));

            Assert.Contains("pack root", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(linkedPath);
            File.Delete(outsidePath);
        }
    }

    [Fact]
    public void Rejects_json_property_names_that_do_not_match_the_schema_case()
    {
        using var pack = TestPack.Create();
        var gamePath = Path.Combine(pack.Root, "game.json");
        File.WriteAllText(gamePath, File.ReadAllText(gamePath).Replace("\"schemaVersion\"", "\"SchemaVersion\"", StringComparison.Ordinal));

        Assert.Throws<ContentPackException>(() => SystemicTestContent.Load(pack.Root));
    }

    [Fact]
    public void Rejects_numeric_values_encoded_as_json_strings()
    {
        using var pack = TestPack.Create();
        var gamePath = Path.Combine(pack.Root, "game.json");
        File.WriteAllText(gamePath, File.ReadAllText(gamePath).Replace("\"tickRateHz\": 60", "\"tickRateHz\": \"60\"", StringComparison.Ordinal));

        Assert.Throws<ContentPackException>(() => SystemicTestContent.Load(pack.Root));
    }

    [Fact]
    public void Rejects_unknown_rule_effects_at_schema_validation()
    {
        using var pack = TestPack.Create(ruleEffect: "run_script");

        var error = Assert.Throws<ContentPackException>(() => SystemicTestContent.Load(pack.Root));

        Assert.Contains(error.Diagnostics, diagnostic => diagnostic.Code == "content.schema_invalid"
            && diagnostic.File == "rules.json"
            && diagnostic.Path == "$.rules[0].effects[0].kind");
    }

    [Fact]
    public void Rule_effects_can_apply_sourced_timed_modifiers()
    {
        using var pack = TestPack.Create();
        File.WriteAllText(Path.Combine(pack.Root, "rules.json"), """
            { "schemaVersion": 2,
              "rules": [{ "id": "boost.contact", "on": "contact", "priority": 0,
                "effects": [{ "kind": "setModifier", "subject": "target", "property": "power",
                  "operation": "multiply", "value": 2, "stage": "multiplicative", "durationTicks": 3 }] }] }
            """);

        var content = SystemicTestContent.Load(pack.Root);
        var world = new World(seed: 1, valueScale: 1);
        var actor = world.Add(new DataEntity([], [], ("power", 4)));
        var result = new RuleEngine(content.Rules).Dispatch(world,
            new SimulationFact("contact", actor.Id, actor.Id));

        Assert.Equal(1, result.EffectsApplied);
        Assert.Equal(8, world.EffectiveProperty(actor, "power"));
        world.LogicalTick = 1;
        world.AdvanceModifiers();
        world.LogicalTick = 2;
        world.AdvanceModifiers();
        Assert.Equal(8, world.EffectiveProperty(actor, "power"));
        world.LogicalTick = 3;
        world.AdvanceModifiers();
        Assert.Equal(4, world.EffectiveProperty(actor, "power"));
    }

    [Fact]
    public void Timed_modifier_effects_roll_back_with_their_dispatch()
    {
        using var pack = TestPack.Create();
        File.WriteAllText(Path.Combine(pack.Root, "rules.json"), """
            { "schemaVersion": 2,
              "rules": [{ "id": "boost.contact", "on": "contact", "priority": 0,
                "effects": [
                  { "kind": "setModifier", "subject": "target", "property": "power",
                    "operation": "multiply", "value": 2, "stage": "multiplicative", "durationTicks": 3 },
                  { "kind": "adjustProperty", "subject": "target", "property": "power", "amount": 1.7976931348623157E+308 }
                ] }] }
            """);
        var content = SystemicTestContent.Load(pack.Root);
        var world = new World(seed: 1, valueScale: 1);
        var actor = world.Add(new DataEntity([], [], ("power", double.MaxValue)));

        var result = new RuleEngine(content.Rules).Dispatch(world,
            new SimulationFact("contact", actor.Id, actor.Id));

        Assert.True(result.RolledBack);
        Assert.Equal(double.MaxValue, world.EffectiveProperty(actor, "power"));
        Assert.Empty(world.Modifiers);
    }

    [Fact]
    public void Rejects_dangling_entity_links_before_world_materialization()
    {
        using var pack = TestPack.Create();
        File.WriteAllText(Path.Combine(pack.Root, "entities.json"), """
            { "schemaVersion": 2, "entities": [
              { "id": "sample.one", "kind": "actor", "links": { "target": "missing.entity" } }
            ] }
            """);

        var error = Assert.Throws<ContentPackException>(() => SystemicTestContent.Load(pack.Root));

        Assert.Contains("missing entity", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Applies_single_level_entity_archetypes_with_overrides_and_explicit_removals()
    {
        using var pack = TestPack.Create();
        File.WriteAllText(Path.Combine(pack.Root, "entities.json"), """
            {
              "schemaVersion": 2,
              "archetypes": [
                { "id": "worker", "kind": "actor", "tags": ["worker", "organic"],
                  "capabilities": ["produce"], "properties": { "rate": 2, "stamina": 10 },
                  "links": { "home": "sample.home" }, "visualRecipeId": "spark" }
              ],
              "entities": [
                { "id": "sample.home", "kind": "prop" },
                { "id": "sample.worker", "archetype": "worker", "tags": ["hired"],
                  "removeTags": ["organic"], "removeCapabilities": ["produce"],
                  "properties": { "rate": 4 }, "links": { "tool": "sample.home" } }
              ]
            }
            """);

        var content = SystemicTestContent.Load(pack.Root);
        var worker = content.Entities.Definitions.Single(entity => entity.Id == "sample.worker");

        Assert.Equal(["worker", "hired"], worker.Tags);
        Assert.Empty(worker.Capabilities);
        Assert.Equal(4, worker.Properties["rate"]);
        Assert.Equal(10, worker.Properties["stamina"]);
        Assert.Equal("sample.home", worker.Links["home"]);
        Assert.Equal("sample.home", worker.Links["tool"]);
        Assert.Equal("spark", worker.VisualRecipeId);
    }

    [Fact]
    public void Rejects_archetype_chains_before_expanding_entities()
    {
        using var pack = TestPack.Create();
        File.WriteAllText(Path.Combine(pack.Root, "entities.json"), """
            { "schemaVersion": 2, "archetypes": [
              { "id": "base", "kind": "actor" },
              { "id": "derived", "archetype": "base", "kind": "actor" }
            ], "entities": [{ "id": "sample.worker", "archetype": "missing" }] }
            """);

        var error = Assert.Throws<ContentPackException>(() => SystemicTestContent.Load(pack.Root));

        Assert.Contains("cannot reference another archetype", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_entities_that_reference_unknown_archetypes()
    {
        using var pack = TestPack.Create();
        File.WriteAllText(Path.Combine(pack.Root, "entities.json"), """
            { "schemaVersion": 2, "archetypes": [],
              "entities": [{ "id": "sample.worker", "archetype": "missing" }] }
            """);

        var error = Assert.Throws<ContentPackException>(() => SystemicTestContent.Load(pack.Root));

        Assert.Contains("unknown archetype 'missing'", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Expands_rule_fragments_in_order_before_local_rules()
    {
        using var pack = TestPack.Create();
        File.WriteAllText(Path.Combine(pack.Root, "rules.json"), """
            {
              "schemaVersion": 2,
              "fragments": [
                { "id": "shared.first", "rules": [
                  { "id": "react", "on": "contact", "priority": 1,
                    "effects": [{ "kind": "emitEvent", "event": "shared.chained" }] }
                ] },
                { "id": "shared.second", "rules": [
                  { "id": "respond", "on": "shared.chained", "priority": 2,
                    "effects": [{ "kind": "addTag", "subject": "target", "tag": "reacted" }] }
                ] }
              ],
              "useFragments": ["shared.second", "shared.first"],
              "rules": [
                { "id": "local", "on": "contact", "priority": 0, "effects": [] }
              ]
            }
            """);

        var rules = SystemicTestContent.Load(pack.Root).Rules.Rules;

        Assert.Equal(["shared.first.react", "local", "shared.second.respond"],
            rules.Select(rule => rule.Id));
    }

    [Fact]
    public void Rejects_unused_rule_fragment_without_a_rules_array()
    {
        using var pack = TestPack.Create();
        File.WriteAllText(Path.Combine(pack.Root, "rules.json"), """
            { "schemaVersion": 2,
              "fragments": [{ "id": "unused.fragment" }],
              "useFragments": [],
              "rules": [] }
            """);

        var error = Assert.Throws<ContentPackException>(() => SystemicTestContent.Load(pack.Root));

        Assert.Contains(error.Diagnostics, diagnostic => diagnostic.Code == "content.schema_invalid"
            && diagnostic.File == "rules.json"
            && diagnostic.Path == "$.fragments[0].rules");
    }

    [Fact]
    public void Accepts_unused_rule_fragment_with_an_empty_rules_array()
    {
        using var pack = TestPack.Create();
        File.WriteAllText(Path.Combine(pack.Root, "rules.json"), """
            { "schemaVersion": 2,
              "fragments": [{ "id": "unused.fragment", "rules": [] }],
              "useFragments": [],
              "rules": [] }
            """);

        var rules = SystemicTestContent.Load(pack.Root).Rules.Rules;

        Assert.Empty(rules);
    }

    [Fact]
    public void Rejects_unused_fragment_rule_without_an_effects_array()
    {
        using var pack = TestPack.Create();
        File.WriteAllText(Path.Combine(pack.Root, "rules.json"), """
            { "schemaVersion": 2,
              "fragments": [{ "id": "unused.fragment", "rules": [
                { "id": "incomplete", "on": "contact", "priority": 0 }
              ] }],
              "useFragments": [],
              "rules": [] }
            """);

        var error = Assert.Throws<ContentPackException>(() => SystemicTestContent.Load(pack.Root));

        Assert.Contains(error.Diagnostics, diagnostic => diagnostic.Code == "content.schema_invalid"
            && diagnostic.File == "rules.json"
            && diagnostic.Path == "$.fragments[0].rules[0].effects");
    }

    [Fact]
    public void Accepts_unused_fragment_rule_with_an_empty_effects_array()
    {
        using var pack = TestPack.Create();
        File.WriteAllText(Path.Combine(pack.Root, "rules.json"), """
            { "schemaVersion": 2,
              "fragments": [{ "id": "unused.fragment", "rules": [
                { "id": "empty", "on": "contact", "priority": 0, "effects": [] }
              ] }],
              "useFragments": [],
              "rules": [] }
            """);

        var rules = SystemicTestContent.Load(pack.Root).Rules.Rules;

        Assert.Empty(rules);
    }

    [Fact]
    public void Rejects_recursive_or_unknown_rule_fragments()
    {
        using var pack = TestPack.Create();
        File.WriteAllText(Path.Combine(pack.Root, "rules.json"), """
            { "schemaVersion": 2,
              "fragments": [{ "id": "shared.base", "rules": [], "useFragments": ["other"] }],
              "useFragments": ["missing"], "rules": [] }
            """);

        var error = Assert.Throws<ContentPackException>(() => SystemicTestContent.Load(pack.Root));

        Assert.Contains("cannot reference another fragment", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_unknown_rule_fragments()
    {
        using var pack = TestPack.Create();
        File.WriteAllText(Path.Combine(pack.Root, "rules.json"), """
            { "schemaVersion": 2, "fragments": [], "useFragments": ["missing"], "rules": [] }
            """);

        var error = Assert.Throws<ContentPackException>(() => SystemicTestContent.Load(pack.Root));

        Assert.Contains("Unknown rule fragment 'missing'", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolves_visual_fragment_layers_and_named_palette_roles()
    {
        using var pack = TestPack.Create();
        File.WriteAllText(Path.Combine(pack.Root, "visuals.json"), """
            {
              "schemaVersion": 2,
              "palettes": { "quarry": { "stone": "#665544", "spark": "#ffcc44" } },
              "fragments": [{ "id": "glow", "layers": [
                { "shape": "ellipse", "colorRole": "spark", "x": 1, "y": 1, "width": 3, "height": 3 }
              ] }],
              "recipes": [{ "id": "visual.spark", "kind": "effect", "seed": 1,
                "canvasWidth": 8, "canvasHeight": 8, "paletteId": "quarry",
                "useFragments": ["glow"], "layers": [
                  { "shape": "rect", "colorRole": "stone", "x": 2, "y": 2, "width": 2, "height": 2 }
                ] }]
            }
            """);
        TestPack.SetTimelineVisual(pack, "visual.spark");
        File.WriteAllText(Path.Combine(pack.Root, "entities.json"), """
            { "schemaVersion": 2, "entities": [
              { "id": "sample.effect", "kind": "prop", "visualRecipeId": "visual.spark" }
            ] }
            """);

        var recipe = SystemicTestContent.Presentation(SystemicTestContent.Load(pack.Root)).Visuals.Recipes.Single();

        Assert.Equal(["#ffcc44", "#665544"], recipe.Palette);
        Assert.Equal(2, recipe.Layers.Length);
        Assert.Equal(0, recipe.Layers[0].Color);
        Assert.Equal(1, recipe.Layers[1].Color);
    }

    [Fact]
    public void Rejects_visual_parts_that_reference_unknown_recipes()
    {
        using var pack = TestPack.Create();
        File.WriteAllText(Path.Combine(pack.Root, "visuals.json"), """
            { "schemaVersion": 2, "recipes": [
              { "id": "visual.actor", "kind": "actor", "seed": 1, "canvasWidth": 8, "canvasHeight": 8,
                "palette": ["#ffffff"], "parts": [{ "recipeId": "visual.missing", "x": 0, "y": 0 }] }
            ] }
            """);

        var error = Assert.Throws<ContentPackException>(() => SystemicTestContent.Load(pack.Root));

        Assert.Contains("unknown part recipe 'visual.missing'", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_nested_visual_parts_to_keep_composition_bounded()
    {
        using var pack = TestPack.Create();
        File.WriteAllText(Path.Combine(pack.Root, "visuals.json"), """
            { "schemaVersion": 2, "recipes": [
              { "id": "visual.base", "kind": "prop", "seed": 1, "canvasWidth": 2, "canvasHeight": 2,
                "palette": ["#ffffff"], "layers": [] },
              { "id": "visual.composite", "kind": "prop", "seed": 2, "canvasWidth": 4, "canvasHeight": 4,
                "palette": ["#ffffff"], "parts": [{ "recipeId": "visual.base", "x": 0, "y": 0 }] },
              { "id": "visual.actor", "kind": "actor", "seed": 3, "canvasWidth": 8, "canvasHeight": 8,
                "palette": ["#ffffff"], "parts": [{ "recipeId": "visual.composite", "x": 0, "y": 0 }] }
            ] }
            """);

        var error = Assert.Throws<ContentPackException>(() => SystemicTestContent.Load(pack.Root));

        Assert.Contains("cannot compose nested part recipe 'visual.composite'", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Presentation_module_diagnostics_use_the_manifest_module_path()
    {
        using var pack = TestPack.Create();
        File.WriteAllText(Path.Combine(pack.Root, "ui.json"), """
            { "schemaVersion": 2, "layouts": [
              { "id": "hud", "root": { "type": "mystery" } }
            ] }
            """);

        var error = Assert.Throws<ContentPackException>(() => SystemicTestContent.Load(pack.Root));

        var diagnostic = Assert.Single(error.Diagnostics);
        Assert.Equal("game.json", diagnostic.File);
        Assert.Equal("$.modules.systemic.presentation.layouts[0].root.type", diagnostic.Path);
        Assert.Equal("module.invalid", diagnostic.Code);
    }

    [Fact]
    public void Rejects_composed_visual_recipes_that_exceed_the_raster_work_budget()
    {
        using var pack = TestPack.Create();
        var fullCanvasLayer = "{\"shape\":\"rect\",\"color\":0,\"x\":0,\"y\":0,\"width\":256,\"height\":256}";
        var fullRecipe = "{\"id\":\"visual.full\",\"kind\":\"prop\",\"seed\":1,\"canvasWidth\":256,\"canvasHeight\":256,\"palette\":[\"#ffffff\"],\"layers\":["
            + string.Join(',', Enumerable.Repeat(fullCanvasLayer, 128)) + "]}";
        var compositeRecipe = "{\"id\":\"visual.composite\",\"kind\":\"actor\",\"seed\":2,\"canvasWidth\":256,\"canvasHeight\":256,\"palette\":[\"#00000000\"],\"parts\":["
            + "{\"recipeId\":\"visual.full\",\"x\":0,\"y\":0},"
            + "{\"recipeId\":\"visual.full\",\"x\":0,\"y\":0}]}";
        File.WriteAllText(Path.Combine(pack.Root, "visuals.json"),
            "{\"schemaVersion\":2,\"recipes\":[" + fullRecipe + "," + compositeRecipe + "]}");
        TestPack.SetTimelineVisual(pack, "visual.composite");

        var error = Assert.Throws<ContentPackException>(() => SystemicTestContent.Load(pack.Root));

        Assert.Contains("raster work budget", error.Message, StringComparison.OrdinalIgnoreCase);
        var diagnostic = Assert.Single(error.Diagnostics);
        Assert.Equal("game.json", diagnostic.File);
        Assert.Equal("$.modules.systemic.presentation.recipes[1].parts", diagnostic.Path);
    }

    [Fact]
    public void Loads_an_optional_engine_neutral_incremental_catalog()
    {
        using var pack = TestPack.Create();
        var gamePath = Path.Combine(pack.Root, "game.json");
        File.WriteAllText(gamePath, File.ReadAllText(gamePath).Replace(
            "\"ui\": \"ui.json\"", "\"ui\": \"ui.json\",\n  \"incremental\": \"economy.json\"", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(pack.Root, "economy.json"), """
            { "schemaVersion": 2, "resources": [], "producers": [], "upgrades": [], "roles": [] }
            """);

        var content = SystemicTestContent.Load(pack.Root);

        Assert.Empty(SystemicTestContent.Incremental(content).Catalog.Resources);
    }

    [Fact]
    public void Incremental_validation_reports_the_invalid_producer_field_path()
    {
        using var pack = TestPack.Create();
        File.WriteAllText(Path.Combine(pack.Root, "game.json"), File.ReadAllText(Path.Combine(pack.Root, "game.json"))
            .Replace("\"ui\": \"ui.json\"", "\"ui\": \"ui.json\",\n  \"incremental\": \"economy.json\"", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(pack.Root, "entities.json"), """
            { "schemaVersion": 2, "entities": [
              { "id": "ore", "kind": "resource", "tags": ["resource"], "properties": { "balance": 0 } },
              { "id": "drill", "kind": "actor", "properties": { "rate": 1 } }
            ] }
            """);
        File.WriteAllText(Path.Combine(pack.Root, "economy.json"), """
            { "schemaVersion": 2,
              "resources": [{ "id": "ore", "entityId": "ore", "balanceProperty": "balance" }],
              "producers": [{ "id": "drill", "entityId": "drill", "resourceId": "ore",
                "rateProperty": "rate", "baseRate": -1, "intervalTicks": 1 }],
              "upgrades": [], "roles": [] }
            """);

        var error = Assert.Throws<ContentPackException>(() => SystemicTestContent.Load(pack.Root));

        var diagnostic = Assert.Single(error.Diagnostics);
        Assert.Equal("economy.json", diagnostic.File);
        Assert.Equal("$.producers[0].baseRate", diagnostic.Path);
        Assert.Equal("content.schema_invalid", diagnostic.Code);
    }

    [Fact]
    public void Loads_an_optional_gated_build_choice_catalog()
    {
        using var pack = TestPack.Create();
        var gamePath = Path.Combine(pack.Root, "game.json");
        File.WriteAllText(gamePath, File.ReadAllText(gamePath).Replace(
            "\"ui\": \"ui.json\"", "\"ui\": \"ui.json\",\n  \"advancements\": \"advancements.json\"", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(pack.Root, "entities.json"), """
            { "schemaVersion": 2, "entities": [
              { "id": "player", "kind": "actor", "properties": { "progress": 6 } },
              { "id": "choice.free", "kind": "prop", "properties": { "selected": 0 } },
              { "id": "choice.burst", "kind": "prop", "visualRecipeId": "spark",
                "properties": { "selected": 0, "activeUntilTick": 0, "cooldownUntilTick": 0 } }
            ] }
            """);
        File.WriteAllText(Path.Combine(pack.Root, "advancements.json"), """
            { "schemaVersion": 2,
              "groups": [{ "id": "crafting", "ownerEntityId": "player",
                "progressEntityId": "player", "progressProperty": "progress",
                "unlockAt": 5, "selectionLimit": 1, "freeChoiceId": "choice.free" }],
              "choices": [
                { "id": "choice.free", "groupId": "crafting", "entityId": "choice.free",
                  "activationMode": "passive", "isFree": true, "choiceTag": "choice.free" },
                { "id": "choice.burst", "groupId": "crafting", "entityId": "choice.burst",
                  "activationMode": "timed", "activationEvent": "choice.burst.activated",
                  "choiceTag": "choice.burst", "activeTicks": 3, "cooldownTicks": 4 }
              ] }
            """);
        File.WriteAllText(Path.Combine(pack.Root, "presentation.json"), """
            { "schemaVersion": 2, "timelines": [
              { "id": "spark.effect", "visualRecipeId": "spark", "durationTicks": 1, "loop": false,
                "particleCount": 0, "keyframes": [{ "tick": 0 }] }
            ], "bindings": [], "choiceIcons": { "choice.burst": "spark" } }
            """);

        var content = SystemicTestContent.Load(pack.Root);

        var compiledChoices = SystemicTestContent.Incremental(content).Choices;
        Assert.Equal("crafting", compiledChoices.Groups.Single().Id);
        Assert.Equal(1, compiledChoices.Groups.Single().SelectionLimit);
        Assert.Equal("choice.free", compiledChoices.Groups.Single().FreeChoiceId);
        var burst = Assert.Single(compiledChoices.Choices, choice => choice.Id == "choice.burst");
        Assert.Equal(ChoiceActivationMode.Timed, burst.ActivationMode);
        Assert.Equal("spark", SystemicTestContent.Presentation(content).ChoiceVisualRecipes["choice.burst"]);
    }

    [Fact]
    public void Advancement_validation_reports_the_invalid_choice_field_path()
    {
        using var pack = TestPack.Create();
        File.WriteAllText(Path.Combine(pack.Root, "game.json"), File.ReadAllText(Path.Combine(pack.Root, "game.json"))
            .Replace("\"ui\": \"ui.json\"", "\"ui\": \"ui.json\",\n  \"advancements\": \"advancements.json\"", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(pack.Root, "entities.json"), """
            { "schemaVersion": 2, "entities": [
              { "id": "player", "kind": "actor", "properties": { "progress": 0 } },
              { "id": "choice.burst", "kind": "prop", "properties": {
                "selected": 0, "activeUntilTick": 0, "cooldownUntilTick": 0 } }
            ] }
            """);
        File.WriteAllText(Path.Combine(pack.Root, "advancements.json"), """
            { "schemaVersion": 2,
              "groups": [{ "id": "crafting", "ownerEntityId": "player",
                "progressEntityId": "player", "progressProperty": "progress",
                "unlockAt": 0, "selectionLimit": 1 }],
              "choices": [{ "id": "choice.burst", "groupId": "crafting", "entityId": "choice.burst",
                "activationMode": "timed", "activeTicks": 0, "cooldownTicks": 4,
                "activationEvent": "choice.burst.activated" }] }
            """);

        var error = Assert.Throws<ContentPackException>(() => SystemicTestContent.Load(pack.Root));

        var diagnostic = Assert.Single(error.Diagnostics);
        Assert.Equal("advancements.json", diagnostic.File);
        Assert.Equal("$.choices[0].activeTicks", diagnostic.Path);
        Assert.Equal("content.schema_invalid", diagnostic.Code);
    }

    [Fact]
    public void Rejects_entity_bounds_that_exceed_the_world_spatial_membership_budget()
    {
        using var pack = TestPack.Create();
        var definitions = Enumerable.Range(0, 62).Select(index =>
            $"{{\"id\":\"bulk.item-{index}\",\"kind\":\"product\",\"bounds\":{{\"x\":2048,\"y\":2048,\"halfWidth\":2032,\"halfHeight\":2032}}}}");
        File.WriteAllText(Path.Combine(pack.Root, "entities.json"),
            $"{{\"schemaVersion\":2,\"entities\":[{string.Join(",", definitions)}]}}");

        var error = Assert.Throws<ContentPackException>(() => SystemicTestContent.Load(pack.Root));

        Assert.Contains("spatial index limits", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_missing_required_visual_canvas_dimensions()
    {
        using var pack = TestPack.Create();
        var visualsPath = Path.Combine(pack.Root, "visuals.json");
        File.WriteAllText(visualsPath, File.ReadAllText(visualsPath).Replace("\"canvasWidth\": 16,", string.Empty, StringComparison.Ordinal));

        var error = Assert.Throws<ContentPackException>(() => SystemicTestContent.Load(pack.Root));

        Assert.Contains("canvasWidth", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_rules_that_omit_required_effect_fields()
    {
        using var pack = TestPack.Create();
        var rulesPath = Path.Combine(pack.Root, "rules.json");
        File.WriteAllText(rulesPath, """
            { "schemaVersion": 2, "rules": [
              { "id": "hurt", "on": "contact", "priority": 0,
                "effects": [{ "kind": "adjustProperty", "subject": "target", "property": "health" }] }
            ] }
            """);

        var error = Assert.Throws<ContentPackException>(() => SystemicTestContent.Load(pack.Root));

        Assert.Contains("amount", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_rules_without_an_effect_array_at_schema_validation()
    {
        using var pack = TestPack.Create();
        var rulesPath = Path.Combine(pack.Root, "rules.json");
        File.WriteAllText(rulesPath, """
            { "schemaVersion": 2, "rules": [
              { "id": "empty", "on": "contact", "priority": 0 }
            ] }
            """);

        var error = Assert.Throws<ContentPackException>(() => SystemicTestContent.Load(pack.Root));

        Assert.Contains(error.Diagnostics, diagnostic => diagnostic.Code == "content.schema_invalid"
            && diagnostic.File == "rules.json"
            && diagnostic.Path == "$.rules[0].effects");
    }

    [Fact]
    public void Rejects_ui_bindings_without_a_path_after_the_namespace()
    {
        using var pack = TestPack.Create();
        File.WriteAllText(Path.Combine(pack.Root, "ui.json"), """
            { "schemaVersion": 2, "layouts": [
              { "id": "hud", "root": { "type": "value", "bind": "world." } }
            ] }
            """);

        var error = Assert.Throws<ContentPackException>(() => SystemicTestContent.Load(pack.Root));

        Assert.Contains("value path", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compiles_the_generic_sample_and_renders_only_read_only_preview_markup()
    {
        var samplePath = Path.Combine(AppContext.BaseDirectory, "samples", "incremental-quarry");
        var compiled = SystemicTestContent.Load(samplePath);

        var html = ContentPreviewPage.Render(compiled);

        Assert.Equal("sample.quarry", compiled.Manifest.Id);
        Assert.Contains("Visual recipes", html);
        Assert.Contains("UI layouts", html);
        Assert.Contains("quarry.ember-spark", html);
        Assert.Contains("ui-icon", html);
        Assert.Contains("quarry.overdrive-icon", html);
        Assert.DoesNotContain("<script>alert", html, StringComparison.OrdinalIgnoreCase);
        var incremental = SystemicTestContent.Incremental(compiled);
        Assert.Single(incremental.Catalog.Resources);
        Assert.Single(incremental.Catalog.Producers);
        Assert.Single(incremental.Catalog.Upgrades);
        Assert.Single(incremental.Catalog.Roles);
        Assert.Equal(2, incremental.Choices.Groups.Length);
        Assert.Equal(6, incremental.Choices.Choices.Length);
    }

    [Fact]
    public void Preview_enforces_an_aggregate_pixel_budget_before_rasterizing()
    {
        var samplePath = Path.Combine(AppContext.BaseDirectory, "samples", "incremental-quarry");
        var content = SystemicTestContent.Load(samplePath);
        var presentation = SystemicTestContent.Presentation(content);
        var largestRecipe = presentation.Visuals.Recipes[0] with { CanvasWidth = 256, CanvasHeight = 256 };
        var tooManyPixels = SystemicTestContent.WithPresentation(content, presentation with
        {
            Visuals = new CompiledVisuals([.. Enumerable.Repeat(largestRecipe, 65)])
        });

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => ContentPreviewPage.Render(tooManyPixels));

        Assert.Contains("generated pixels", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Preview_enforces_an_aggregate_raster_work_budget_before_rendering()
    {
        var samplePath = Path.Combine(AppContext.BaseDirectory, "samples", "incremental-quarry");
        var content = SystemicTestContent.Load(samplePath);
        var expensive = new VisualRecipe("visual.expensive", "prop", 1, 256, 256, ["#ffffff"],
            [.. Enumerable.Repeat(new VisualLayer("rect", 0, 0, 0, 256, 256, 0), 250)]);
        var tooMuchWork = SystemicTestContent.WithPresentation(content,
            SystemicTestContent.Presentation(content) with
            {
                Visuals = new CompiledVisuals([expensive, expensive with { Id = "visual.expensive-copy" }])
            });

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => ContentPreviewPage.Render(tooMuchWork));

        Assert.Contains("raster work", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public static void Preview_includes_states_defined_only_by_composed_parts()
    {
        var samplePath = Path.Combine(AppContext.BaseDirectory, "samples", "incremental-quarry");
        var content = SystemicTestContent.Load(samplePath);
        var child = new VisualRecipe("visual.child", "part", 1, 2, 2, ["#00000000", "#ffffff"],
            [new VisualLayer("rect", 1, 0, 0, 1, 1, 0, State: "active")]);
        var composite = new VisualRecipe("visual.composite", "actor", 2, 2, 2, ["#00000000"], [],
            Parts: [new VisualPart(child, 0, 0, 1)]);
        var withComposite = SystemicTestContent.WithPresentation(content,
            SystemicTestContent.Presentation(content) with
            {
                Visuals = new CompiledVisuals([.. SystemicTestContent.Presentation(content).Visuals.Recipes, composite, child])
            });

        var html = ContentPreviewPage.Render(withComposite);

        Assert.Contains("\"id\":\"visual.composite\",\"state\":\"active\"", html, StringComparison.Ordinal);
    }
}

internal sealed class TestPack : IDisposable
{
    private TestPack(string root) => Root = root;

    public string Root { get; }

    public static TestPack Create(
        string rulesPath = "rules.json",
        string ruleEffect = "adjustProperty",
        int schemaVersion = 3)
    {
        var root = Path.Combine(Path.GetTempPath(), $"systemic-pack-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "game.json"), $$"""
            {
              "schemaVersion": {{schemaVersion}},
              "id": "sample.quarry",
              "name": "Quarry",
              "tickRateHz": 60,
              "maxCatchUpSteps": 4,
              "rules": "{{rulesPath}}",
              "entities": "entities.json",
              "modules": {
                "systemic.incremental": { "incremental": "economy.json", "advancements": "advancements.json" },
                "systemic.presentation": { "visuals": "visuals.json", "ui": "ui.json", "presentation": "presentation.json" }
              }
            }
            """);
        if (rulesPath == "rules.json")
        {
            File.WriteAllText(Path.Combine(root, rulesPath), $$"""
                {
                  "schemaVersion": 2,
                  "rules": [
                    { "id": "second", "on": "contact", "priority": 1,
                      "when": [], "effects": [{ "kind": "{{ruleEffect}}", "subject": "target", "property": "health", "amount": -1 }] },
                    { "id": "first", "on": "contact", "priority": 1,
                      "when": [], "effects": [{ "kind": "{{ruleEffect}}", "subject": "target", "property": "health", "amount": -1 }] }
                  ]
                }
                """);
        }

        File.WriteAllText(Path.Combine(root, "visuals.json"), $$"""
            { "schemaVersion": 2, "recipes": [
              { "id": "spark", "kind": "particle", "seed": 7, "canvasWidth": 16, "canvasHeight": 16,
                "palette": ["#fff2a8", "#ff7139"] }
            ] }
            """);
        File.WriteAllText(Path.Combine(root, "ui.json"), $$"""
            { "schemaVersion": 2, "layouts": [
              { "id": "hud", "root": { "type": "column", "children": [
                { "type": "text", "text": "Quarry", "bind": "world.tick" }
              ] } }
            ] }
            """);
        File.WriteAllText(Path.Combine(root, "entities.json"), $$"""
            { "schemaVersion": 2, "entities": [] }
            """);
        File.WriteAllText(Path.Combine(root, "economy.json"), """
            { "schemaVersion": 2, "resources": [], "producers": [], "upgrades": [], "roles": [] }
            """);
        File.WriteAllText(Path.Combine(root, "advancements.json"), "{ \"schemaVersion\": 2, \"groups\": [], \"choices\": [] }");
        File.WriteAllText(Path.Combine(root, "presentation.json"), """
            { "schemaVersion": 2, "timelines": [
              { "id": "spark.effect", "visualRecipeId": "spark", "durationTicks": 1, "loop": false,
                "particleCount": 0, "keyframes": [{ "tick": 0 }] }
            ], "bindings": [] }
            """);

        return new TestPack(root);
    }

    public static void SetTimelineVisual(TestPack pack, string recipeId) =>
        File.WriteAllText(Path.Combine(pack.Root, "presentation.json"), $$"""
            { "schemaVersion": 2, "timelines": [
              { "id": "spark.effect", "visualRecipeId": "{{recipeId}}", "durationTicks": 1, "loop": false,
                "particleCount": 0, "keyframes": [{ "tick": 0 }] }
            ], "bindings": [] }
            """);

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
