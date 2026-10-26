using System.Text.Json;
using Systemic.Content;
using Systemic.Host;
using Systemic.Incremental;

namespace Systemic.Tests.Modules.Incremental;

public sealed class IncrementalContentModuleTests
{
    [Fact]
    public void Incremental_schema_errors_report_the_module_file_and_json_path()
    {
        using var pack = IncrementalPack.Create();
        File.WriteAllText(Path.Combine(pack.Root, "incremental.json"), """
            {
              "schemaVersion": 2,
              "resources": [],
              "producers": [],
              "upgrades": [],
              "roles": [],
              "unexpected": true
            }
            """);

        var exception = Assert.Throws<ContentPackException>(() => ContentPackLoader.Load(pack.Root,
            new ContentModuleRegistry().Add(new IncrementalContentModule())));

        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal("content.schema_invalid", diagnostic.Code);
        Assert.Equal("incremental.json", diagnostic.File);
        Assert.Equal("$.unexpected", diagnostic.Path);
    }

    [Fact]
    public void Incremental_module_initializes_and_steps_without_optional_sibling_modules()
    {
        using var pack = IncrementalPack.Create();
        var content = ContentPackLoader.Load(pack.Root,
            new ContentModuleRegistry().Add(new IncrementalContentModule()));
        var game = new SystemicGameBuilder(content, content.CreateWorld(seed: 4, valueScale: 1)).Build();

        Assert.Single(content.Modules);
        Assert.IsType<IncrementalModuleContent>(content.Modules[0].Content);
        game.Step();

        var resource = game.World.ById(game.EntityId("resource.energy"))!;
        Assert.True(resource.TryGetProperty(game.World, "balance", out var balance));
        Assert.Equal(3, balance);
        Assert.Equal(1, game.Incremental().GetType() == typeof(IncrementalRuntime) ? 1 : 0);
    }
}

internal sealed class IncrementalPack : IDisposable
{
    private IncrementalPack(string root) => Root = root;
    public string Root { get; }

    public static IncrementalPack Create()
    {
        var root = Path.Combine(Path.GetTempPath(), $"systemic-incremental-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "game.json"), """
            { "schemaVersion": 3, "id": "sample.incremental", "name": "Incremental",
              "tickRateHz": 30, "maxCatchUpSteps": 2, "rules": "rules.json", "entities": "entities.json",
              "modules": { "systemic.incremental": { "incremental": "incremental.json", "advancements": "advancements.json" } } }
            """);
        File.WriteAllText(Path.Combine(root, "rules.json"), "{ \"schemaVersion\": 2, \"rules\": [] }");
        File.WriteAllText(Path.Combine(root, "entities.json"), """
            { "schemaVersion": 2, "entities": [
              { "id": "resource.energy", "kind": "resource", "tags": ["resource"], "properties": { "balance": 0 } },
              { "id": "actor.generator", "kind": "actor", "properties": { "rate": 3 } }
            ] }
            """);
        File.WriteAllText(Path.Combine(root, "incremental.json"), """
            { "schemaVersion": 2,
              "resources": [{ "id": "energy", "entityId": "resource.energy", "initialValue": 0 }],
              "producers": [{ "id": "generator", "entityId": "actor.generator", "resourceId": "energy", "baseRate": 3 }],
              "upgrades": [], "roles": [] }
            """);
        File.WriteAllText(Path.Combine(root, "advancements.json"), "{ \"schemaVersion\": 2, \"groups\": [], \"choices\": [] }");
        return new IncrementalPack(root);
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
