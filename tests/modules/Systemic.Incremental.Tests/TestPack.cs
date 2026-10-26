using Systemic.Content;
using Systemic.Incremental;

namespace Systemic.Tests;

internal sealed class TestPack : IDisposable
{
    private TestPack(string root) => Root = root;

    public string Root { get; }

    public static TestPack Create()
    {
        var root = Path.Combine(Path.GetTempPath(), $"systemic-incremental-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "game.json"), """
            { "schemaVersion": 3, "id": "sample.incremental", "name": "Incremental",
              "tickRateHz": 60, "maxCatchUpSteps": 4, "rules": "rules.json", "entities": "entities.json",
              "modules": { "systemic.incremental": { "incremental": "economy.json", "advancements": "advancements.json" } } }
            """);
        File.WriteAllText(Path.Combine(root, "rules.json"), "{ \"schemaVersion\": 2, \"rules\": [] }");
        File.WriteAllText(Path.Combine(root, "entities.json"), "{ \"schemaVersion\": 2, \"entities\": [] }");
        File.WriteAllText(Path.Combine(root, "economy.json"), """
            { "schemaVersion": 2, "resources": [], "producers": [], "upgrades": [], "roles": [] }
            """);
        File.WriteAllText(Path.Combine(root, "advancements.json"), "{ \"schemaVersion\": 2, \"groups\": [], \"choices\": [] }");
        return new TestPack(root);
    }

    public static ContentPack Load(string root) => ContentPackLoader.Load(root,
        new ContentModuleRegistry().Add(new IncrementalContentModule()));

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
