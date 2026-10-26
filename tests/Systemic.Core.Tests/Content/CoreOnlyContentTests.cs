using System.Text.Json;
using Systemic.Content;
using Systemic.Host;
using Systemic.Rules;

namespace Systemic.Tests.Content;

public sealed class CoreOnlyContentTests
{
    [Fact]
    public void Core_schema_errors_report_the_source_file_and_json_path()
    {
        using var pack = CoreOnlyPack.Create();
        File.WriteAllText(Path.Combine(pack.Root, "entities.json"), """
            {
              "schemaVersion": 2,
              "entities": [{ "id": "actor.target", "kind": "actor", "unexpected": true }]
            }
            """);

        var exception = Assert.Throws<ContentPackException>(() => ContentPackLoader.Load(pack.Root));

        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal("content.schema_invalid", diagnostic.Code);
        Assert.Equal("entities.json", diagnostic.File);
        Assert.Equal("$.entities[0].unexpected", diagnostic.Path);
    }

    [Fact]
    public void Core_schema_json_pointer_escapes_slash_and_tilde_in_property_names()
    {
        using var pack = CoreOnlyPack.Create();
        File.WriteAllText(Path.Combine(pack.Root, "entities.json"), """
            { "schemaVersion": 2, "entities": [{ "id": "actor.target", "kind": "actor", "odd/name~key": true }] }
            """);

        var exception = Assert.Throws<ContentPackException>(() => ContentPackLoader.Load(pack.Root));

        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "content.schema_invalid"
            && diagnostic.File == "entities.json" && diagnostic.Path == "$.entities[0]['odd/name~key']");
    }

    [Fact]
    public void Core_schema_json_path_escapes_control_characters_in_property_names()
    {
        using var pack = CoreOnlyPack.Create();
        File.WriteAllText(Path.Combine(pack.Root, "entities.json"), """
            { "schemaVersion": 2, "entities": [{ "id": "actor.target", "kind": "actor", "odd\nname": true }] }
            """);

        var exception = Assert.Throws<ContentPackException>(() => ContentPackLoader.Load(pack.Root));

        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal("content.schema_invalid", diagnostic.Code);
        Assert.Equal("entities.json", diagnostic.File);
        Assert.Equal("$.entities[0]['odd\\nname']", diagnostic.Path);
        Assert.DoesNotContain('\n', diagnostic.Path);
    }

    [Fact]
    public void Core_only_pack_without_visuals_or_ui_loads_and_runs_rules()
    {
        using var pack = CoreOnlyPack.Create();

        var content = ContentPackLoader.Load(pack.Root);
        var host = HeadlessSystemicHost.Create(content, seed: 7);
        var entityId = host.Game.EntityIds["actor.target"];

        var result = host.Publish(new SimulationFact("contact", entityId, entityId));

        Assert.Equal(1, result.RulesMatched);
        Assert.True(host.Game.World.ById(entityId)!.TryGetProperty(host.Game.World, "health", out var health));
        Assert.Equal(3, health);
    }

    [Fact]
    public void Module_context_rejects_symlinks_that_escape_the_pack_root()
    {
        using var pack = CoreOnlyPack.Create();
        var content = ContentPackLoader.Load(pack.Root);
        var context = new ContentModuleContext(content.RootDirectory, content.Manifest, content.Entities,
            content.Rules, new ContentReferenceCatalog());
        var outsidePath = Path.Combine(Path.GetTempPath(), $"systemic-module-outside-{Guid.NewGuid():N}.json");
        var linkedPath = Path.Combine(pack.Root, "module.json");
        File.WriteAllText(outsidePath, "{}");
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

            var error = Assert.Throws<ContentPackException>(() => context.ReadJson("module.json"));

            Assert.Contains("pack root", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(linkedPath);
            File.Delete(outsidePath);
        }
    }

    [Fact]
    public void Module_schema_cache_rejects_conflicting_text_for_the_same_key()
    {
        using var pack = CoreOnlyPack.Create();
        var content = ContentPackLoader.Load(pack.Root);
        var context = new ContentModuleContext(content.RootDirectory, content.Manifest, content.Entities,
            content.Rules, new ContentReferenceCatalog());
        var schemaKey = $"https://example.test/schemas/{Guid.NewGuid():N}";
        const string firstSchema = """
            {"type":"object","required":["first"],"properties":{"first":{"type":"string"}},"additionalProperties":false}
            """;
        const string secondSchema = """
            {"type":"object","required":["second"],"properties":{"second":{"type":"number"}},"additionalProperties":false}
            """;
        File.WriteAllText(Path.Combine(pack.Root, "first.json"), "{\"first\":\"value\"}");
        File.WriteAllText(Path.Combine(pack.Root, "second.json"), "{\"second\":2}");
        using var firstDeclaration = JsonDocument.Parse("{\"file\":\"first.json\"}");
        using var secondDeclaration = JsonDocument.Parse("{\"file\":\"second.json\"}");

        var first = context.ReadJson(firstDeclaration.RootElement, "file", "$", schemaKey, firstSchema, out _);
        var error = Assert.Throws<ContentPackException>(() =>
            context.ReadJson(secondDeclaration.RootElement, "file", "$", schemaKey, secondSchema, out _));

        Assert.Equal("value", first.GetProperty("first"u8).GetString());
        var diagnostic = Assert.Single(error.Diagnostics);
        Assert.Equal("content.schema_conflict", diagnostic.Code);
        Assert.Equal("second.json", diagnostic.File);
    }

    [Fact]
    public void Module_json_read_reports_the_content_file_and_parse_path()
    {
        using var pack = CoreOnlyPack.Create();
        var content = ContentPackLoader.Load(pack.Root);
        var context = new ContentModuleContext(content.RootDirectory, content.Manifest, content.Entities,
            content.Rules, new ContentReferenceCatalog());
        File.WriteAllText(Path.Combine(pack.Root, "module.json"), "{\"items\":[}");

        var error = Assert.Throws<ContentPackException>(() => context.ReadJson("module.json"));

        var diagnostic = Assert.Single(error.Diagnostics);
        Assert.Equal("content.invalid_json", diagnostic.Code);
        Assert.Equal("module.json", diagnostic.File);
        Assert.Equal("$", diagnostic.Path);

        using var declaration = JsonDocument.Parse("{\"file\":\"module.json\"}");
        var declarationError = Assert.Throws<ContentPackException>(() =>
            context.ReadJson(declaration.RootElement, "file", "$", out _));
        var declarationDiagnostic = Assert.Single(declarationError.Diagnostics);
        Assert.Equal("module.json", declarationDiagnostic.File);
        Assert.Equal("$", declarationDiagnostic.Path);
    }

    [Fact]
    public void Unused_rule_fragments_reject_unknown_condition_and_effect_kinds()
    {
        using var pack = CoreOnlyPack.Create();
        File.WriteAllText(Path.Combine(pack.Root, "rules.json"), """
            {
              "schemaVersion": 2,
              "rules": [],
              "fragments": [
                { "id": "invalid.condition", "rules": [{
                  "id": "example", "on": "contact", "priority": 0,
                  "when": [{ "kind": "neverKnown" }], "effects": []
                }] },
                { "id": "invalid.effect", "rules": [{
                  "id": "example", "on": "contact", "priority": 0,
                  "when": [], "effects": [{ "kind": "neverKnown" }]
                }] }
              ]
            }
            """);

        var exception = Assert.Throws<ContentPackException>(() => ContentPackLoader.Load(pack.Root));

        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "content.schema_invalid"
            && diagnostic.File == "rules.json"
            && diagnostic.Path == "$.fragments[0].rules[0].when[0].kind");
        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "content.schema_invalid"
            && diagnostic.File == "rules.json"
            && diagnostic.Path == "$.fragments[1].rules[0].effects[0].kind");
    }

    [Fact]
    public void Unused_rule_fragments_require_fields_for_each_condition_and_effect_kind()
    {
        using var pack = CoreOnlyPack.Create();
        File.WriteAllText(Path.Combine(pack.Root, "rules.json"), """
            {
              "schemaVersion": 2,
              "rules": [],
              "fragments": [
                { "id": "invalid.condition.has-tag", "rules": [{ "id": "example", "on": "contact", "priority": 0, "when": [{ "kind": "hasTag" }], "effects": [] }] },
                { "id": "invalid.condition.property-at-least", "rules": [{ "id": "example", "on": "contact", "priority": 0, "when": [{ "kind": "propertyAtLeast" }], "effects": [] }] },
                { "id": "invalid.condition.expression", "rules": [{ "id": "example", "on": "contact", "priority": 0, "when": [{ "kind": "expression" }], "effects": [] }] },
                { "id": "invalid.condition.chance", "rules": [{ "id": "example", "on": "contact", "priority": 0, "when": [{ "kind": "chance" }], "effects": [] }] },
                { "id": "invalid.effect.adjust-property", "rules": [{ "id": "example", "on": "contact", "priority": 0, "when": [], "effects": [{ "kind": "adjustProperty" }] }] },
                { "id": "invalid.effect.set-modifier", "rules": [{ "id": "example", "on": "contact", "priority": 0, "when": [], "effects": [{ "kind": "setModifier" }] }] },
                { "id": "invalid.effect.add-tag", "rules": [{ "id": "example", "on": "contact", "priority": 0, "when": [], "effects": [{ "kind": "addTag" }] }] },
                { "id": "invalid.effect.emit-event", "rules": [{ "id": "example", "on": "contact", "priority": 0, "when": [], "effects": [{ "kind": "emitEvent" }] }] }
              ]
            }
            """);

        var exception = Assert.Throws<ContentPackException>(() => ContentPackLoader.Load(pack.Root));

        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "content.schema_invalid"
            && diagnostic.File == "rules.json"
            && diagnostic.Path == "$.fragments[0].rules[0].when[0]");
        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "content.schema_invalid"
            && diagnostic.File == "rules.json"
            && diagnostic.Path == "$.fragments[1].rules[0].when[0]");
        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "content.schema_invalid"
            && diagnostic.File == "rules.json"
            && diagnostic.Path == "$.fragments[2].rules[0].when[0]");
        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "content.schema_invalid"
            && diagnostic.File == "rules.json"
            && diagnostic.Path == "$.fragments[3].rules[0].when[0]");
        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "content.schema_invalid"
            && diagnostic.File == "rules.json"
            && diagnostic.Path == "$.fragments[4].rules[0].effects[0]");
        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "content.schema_invalid"
            && diagnostic.File == "rules.json"
            && diagnostic.Path == "$.fragments[5].rules[0].effects[0]");
        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "content.schema_invalid"
            && diagnostic.File == "rules.json"
            && diagnostic.Path == "$.fragments[6].rules[0].effects[0]");
        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "content.schema_invalid"
            && diagnostic.File == "rules.json"
            && diagnostic.Path == "$.fragments[7].rules[0].effects[0]");
    }

    [Fact]
    public static void Selected_rule_fragment_accepts_all_supported_condition_and_effect_kinds()
    {
        using var pack = CoreOnlyPack.Create();
        File.WriteAllText(Path.Combine(pack.Root, "rules.json"), """
            {
              "schemaVersion": 2,
              "rules": [],
              "useFragments": ["valid.kinds"],
              "fragments": [{
                "id": "valid.kinds",
                "rules": [{
                  "id": "example", "on": "contact", "priority": 0,
                  "when": [
                    { "kind": "hasTag", "subject": "source", "tag": "worker" },
                    { "kind": "propertyAtLeast", "subject": "source", "property": "health", "value": 0 },
                    { "kind": "expression", "expression": "target.health >= 0" },
                    { "kind": "chance", "probability": 1 }
                  ],
                  "effects": [
                    { "kind": "adjustProperty", "subject": "target", "property": "health", "amount": 0 },
                    { "kind": "setModifier", "subject": "target", "property": "health", "operation": "add", "value": 0 },
                    { "kind": "addTag", "subject": "target", "tag": "checked" },
                    { "kind": "emitEvent", "event": "checked.event" }
                  ]
                }]
              }]
            }
            """);

        var content = ContentPackLoader.Load(pack.Root);

        Assert.Single(content.Rules.Rules);
    }

    [Fact]
    public void Declared_module_without_a_registered_loader_reports_its_manifest_path()
    {
        using var pack = CoreOnlyPack.Create();
        var gamePath = Path.Combine(pack.Root, "game.json");
        File.WriteAllText(gamePath, File.ReadAllText(gamePath).Replace(
            "\"entities\": \"entities.json\"", "\"entities\": \"entities.json\",\n  \"modules\": { \"missing\": { \"data\": \"missing.json\" } }",
            StringComparison.Ordinal));

        var error = Assert.Throws<ContentPackException>(() => { ContentPackLoader.Load(pack.Root); });

        Assert.Contains(error.Diagnostics, diagnostic => diagnostic.Path == "$.modules.missing"
            && diagnostic.Message.Contains("No content module registered", StringComparison.Ordinal));
    }
}

internal sealed class CoreOnlyPack : IDisposable
{
    private CoreOnlyPack(string root) => Root = root;

    public string Root { get; }

    public static CoreOnlyPack Create()
    {
        var root = Path.Combine(Path.GetTempPath(), $"systemic-core-pack-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "game.json"), """
            {
              "schemaVersion": 2,
              "id": "sample.core",
              "name": "Core Sample",
              "tickRateHz": 30,
              "maxCatchUpSteps": 3,
              "rules": "rules.json",
              "entities": "entities.json"
            }
            """);
        File.WriteAllText(Path.Combine(root, "rules.json"), """
            {
              "schemaVersion": 2,
              "rules": [{
                "id": "contact.damage",
                "on": "contact",
                "priority": 0,
                "when": [],
                "effects": [{
                  "kind": "adjustProperty",
                  "subject": "target",
                  "property": "health",
                  "amount": -2
                }]
              }]
            }
            """);
        File.WriteAllText(Path.Combine(root, "entities.json"), """
            {
              "schemaVersion": 2,
              "entities": [{
                "id": "actor.target",
                "kind": "actor",
                "properties": { "health": 5 }
              }]
            }
            """);

        return new CoreOnlyPack(root);
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
