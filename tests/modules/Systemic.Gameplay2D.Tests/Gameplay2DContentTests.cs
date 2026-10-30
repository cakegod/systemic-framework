using Systemic.Kernel;
using Systemic.Content;

namespace Systemic.Gameplay2D.Tests;

public sealed class Gameplay2DContentTests
{
    [Fact]
    public void LoadedBlueprintsSpawnActorsAndApplyStatusesWithoutManualTranslation()
    {
        var content = Gameplay2DContentLoader.Parse("""
            {
              "schemaVersion": 2,
              "actors": [{
                "id": "scout",
                "tags": ["role.scout"],
                "capabilities": ["move"],
                "properties": { "power": 4 },
                "halfWidth": 2,
                "halfHeight": 3
              }],
              "statuses": [{
                "id": "quickening",
                "tag": "status.quickening",
                "property": "power",
                "operation": "multiply",
                "value": 1.5,
                "durationTicks": 8,
                "stage": "multiplicative",
                "stacking": "stack",
                "maximumStacks": 2
              }]
            }
            """);
        var world = new World(seed: 10, valueScale: 1);

        var actor = Gameplay2D.SpawnActor(world, content.Actors["scout"], new Point2(7, 9));
        var stacks = Gameplay2D.ApplyStatus(world, actor, content.Statuses["quickening"]);

        Assert.Equal(1, stacks);
        Assert.Equal(6, world.EffectiveProperty(actor, "power"));
        Assert.True(world.HasTag(actor, "status.quickening"));
        Assert.True(world.TryGetSpatialBounds(actor.Id, out var bounds));
        Assert.Equal(new Bounds2(7, 9, 2, 3), bounds);
    }

    [Fact]
    public static void QuarrySampleIncludesLoadableOptional2dDefinitions()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "samples", "incremental-quarry", "gameplay2d.json");

        var content = Gameplay2DContentLoader.Load(path);

        Assert.Contains("miner", content.Actors.Keys);
        Assert.Contains("focus", content.Statuses.Keys);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"actors\":[],\"statuses\":[]}", "$.schemaVersion")]
    [InlineData("{\"schemaVersion\":2,\"actors\":[{\"id\":\"scout\",\"tags\":[],\"capabilities\":[],\"properties\":{},\"halfWidth\":-1,\"halfHeight\":1}],\"statuses\":[]}", "$.actors[0].halfWidth")]
    [InlineData("{\"schemaVersion\":2,\"actors\":[{\"id\":\"scout\",\"tags\":[],\"capabilities\":[],\"properties\":{},\"halfHeight\":1}],\"statuses\":[]}", "$.actors[0].halfWidth")]
    [InlineData("{\"schemaVersion\":2,\"actors\":[],\"statuses\":[{\"id\":\"quickening\",\"tag\":\"status.quickening\",\"property\":\"power\",\"operation\":\"divide\",\"value\":2,\"durationTicks\":8}]}", "$.statuses[0].operation")]
    public void InvalidGameplayContentReportsAStableJsonPath(string json, string expectedPath)
    {
        var exception = Assert.Throws<Gameplay2DContentException>(() => Gameplay2DContentLoader.Parse(json));

        Assert.Equal(expectedPath, exception.ContentPath);
        Assert.Contains(expectedPath, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PublicParseAndLoadRequireSchemaCaseExactMemberNames()
    {
        const string json = "{\"SchemaVersion\":2,\"actors\":[],\"statuses\":[]}";
        var parseError = Assert.Throws<Gameplay2DContentException>(() => Gameplay2DContentLoader.Parse(json));
        Assert.Contains("schema version 2", parseError.Message, StringComparison.OrdinalIgnoreCase);

        var path = Path.Combine(Path.GetTempPath(), $"gameplay2d-case-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, json);

            var loadError = Assert.Throws<Gameplay2DContentException>(() => Gameplay2DContentLoader.Load(path));
            Assert.Equal(path, loadError.SourceFile);
            Assert.Contains("schema version 2", loadError.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2,\"actors\":[null],\"statuses\":[]}", "$.actors[0]")]
    [InlineData("{\"schemaVersion\":2,\"actors\":[],\"statuses\":[null]}", "$.statuses[0]")]
    public void NullGameplayDefinitionsReportTheirArrayPath(string json, string expectedPath)
    {
        var exception = Assert.Throws<Gameplay2DContentException>(() => Gameplay2DContentLoader.Parse(json));

        Assert.Equal(expectedPath, exception.ContentPath);
    }

    [Theory]
    [InlineData("{}", "$.modules.systemic.gameplay2d.gameplay2d")]
    [InlineData("{\"gameplay2d\": []}", "$.modules['systemic.gameplay2d'].gameplay2d")]
    public void MissingOrWrongTypeGameplayFileKeyReportsManifestDiagnostic(string declaration, string expectedPath)
    {
        using var pack = ModulePack.Create(declaration);
        var registry = new ContentModuleRegistry().Add(new Gameplay2DContentModule());

        var exception = Assert.Throws<ContentPackException>(() => ContentPackLoader.Load(pack.Root, registry));

        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal("game.json", diagnostic.File);
        Assert.Equal(expectedPath, diagnostic.Path);
    }

    [Fact]
    public void Gameplay2dSchemaErrorsReportOwningFileAndJsonPath()
    {
        using var pack = ModulePack.Create("{\"gameplay2d\": \"gameplay2d.json\"}");
        File.WriteAllText(Path.Combine(pack.Root, "gameplay2d.json"), """
            { "schemaVersion": 2, "actors": [], "statuses": [], "unexpected": true }
            """);

        var exception = Assert.Throws<ContentPackException>(() => ContentPackLoader.Load(pack.Root,
            new ContentModuleRegistry().Add(new Gameplay2DContentModule())));

        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal("content.schema_invalid", diagnostic.Code);
        Assert.Equal("gameplay2d.json", diagnostic.File);
        Assert.Equal("$.unexpected", diagnostic.Path);
    }

    private sealed class ModulePack(string root) : IDisposable
    {
        public string Root { get; } = root;

        public static ModulePack Create(string declaration)
        {
            var root = Path.Combine(Path.GetTempPath(), $"systemic-gameplay2d-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "game.json"), $$"""
                { "schemaVersion": 3, "id": "sample.gameplay2d", "name": "Gameplay2D",
                  "tickRateHz": 30, "maxCatchUpSteps": 3, "rules": "rules.json", "entities": "entities.json",
                  "modules": { "systemic.gameplay2d": {{declaration}} } }
                """);
            File.WriteAllText(Path.Combine(root, "rules.json"), "{ \"schemaVersion\": 2, \"rules\": [] }");
            File.WriteAllText(Path.Combine(root, "entities.json"), "{ \"schemaVersion\": 2, \"entities\": [] }");
            return new ModulePack(root);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
