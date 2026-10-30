using Systemic.Content;
using Systemic.Host;
using Systemic.Presentation;

namespace Systemic.Tests.Presentation;

public sealed class PresentationContentModuleTests
{
    [Fact]
    public void DeclaredPresentationModuleCompilesContentAndRegistersTimelineRuntime()
    {
        using var pack = PresentationPack.Create();
        var content = ContentPackLoader.Load(pack.Root,
            new ContentModuleRegistry().Add(new PresentationContentModule()));
        var game = new SystemicGameBuilder(content, content.CreateWorld(seed: 7, valueScale: 1)).Build();

        Assert.Single(content.Modules);
        Assert.Single(game.PresentationContent().Visuals.Recipes);
        Assert.True(game.TryGetPresentationTimeline("pulse", out var timeline));
        Assert.Equal("spark", timeline.VisualRecipeId);
        Assert.Same(game.Presentation(), game.GetService<PresentationRuntime>());
    }

    [Fact]
    public void PresentationSchemaErrorsReportOwningFileAndJsonPath()
    {
        using var pack = PresentationPack.Create();
        File.WriteAllText(Path.Combine(pack.Root, "visuals.json"), """
            { "schemaVersion": 2, "recipes": [], "unexpected": true }
            """);

        var exception = Assert.Throws<ContentPackException>(() => ContentPackLoader.Load(pack.Root,
            new ContentModuleRegistry().Add(new PresentationContentModule())));

        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal("content.schema_invalid", diagnostic.Code);
        Assert.Equal("visuals.json", diagnostic.File);
        Assert.Equal("$.unexpected", diagnostic.Path);
    }

    [Theory]
    [InlineData("\"visuals\": \"visuals.json\", ", "", "$.modules.systemic.presentation.visuals")]
    [InlineData("\"visuals\": \"visuals.json\"", "\"visuals\": 7", "$.modules['systemic.presentation'].visuals")]
    public void MissingOrWrongTypeVisualsFileKeyReportsManifestDiagnostic(
        string original, string replacement, string expectedPath)
    {
        using var pack = PresentationPack.Create();
        var manifestPath = Path.Combine(pack.Root, "game.json");
        var manifest = File.ReadAllText(manifestPath);
        var rewritten = manifest.Replace(original, replacement, StringComparison.Ordinal);
        Assert.NotEqual(manifest, rewritten);
        File.WriteAllText(manifestPath, rewritten);

        var exception = Assert.Throws<ContentPackException>(() => ContentPackLoader.Load(pack.Root,
            new ContentModuleRegistry().Add(new PresentationContentModule())));

        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal("game.json", diagnostic.File);
        Assert.Equal(expectedPath, diagnostic.Path);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2,\"entities\":[{\"id\":\"actor\",\"kind\":\"actor\",\"visualRecipeId\":\"missing.recipe\"}]}",
        "$.entities[0].visualRecipeId")]
    [InlineData("{\"schemaVersion\":2,\"archetypes\":[{\"id\":\"visual.prop\",\"kind\":\"prop\",\"visualRecipeId\":\"missing.recipe\"}],\"entities\":[{\"id\":\"actor\",\"archetype\":\"visual.prop\"}]}",
        "$.archetypes[0].visualRecipeId")]
    public void UnknownEntityVisualRecipeReportsItsOriginalSourceField(string entitiesJson, string expectedPath)
    {
        using var pack = PresentationPack.Create();
        File.WriteAllText(Path.Combine(pack.Root, "entities.json"), entitiesJson);

        var exception = Assert.Throws<ContentPackException>(() => ContentPackLoader.Load(pack.Root,
            new ContentModuleRegistry().Add(new PresentationContentModule())));

        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal("entities.json", diagnostic.File);
        Assert.Equal(expectedPath, diagnostic.Path);
    }
}

internal sealed class PresentationPack : IDisposable
{
    private PresentationPack(string root) => Root = root;
    public string Root { get; }

    public static PresentationPack Create()
    {
        var root = Path.Combine(Path.GetTempPath(), $"systemic-presentation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "game.json"), """
            { "schemaVersion": 3, "id": "sample.presentation", "name": "Presentation",
              "tickRateHz": 60, "maxCatchUpSteps": 4, "rules": "rules.json", "entities": "entities.json",
              "modules": { "systemic.presentation": {
                "visuals": "visuals.json", "ui": "ui.json", "presentation": "presentation.json" } } }
            """);
        File.WriteAllText(Path.Combine(root, "rules.json"), "{ \"schemaVersion\": 2, \"rules\": [] }");
        File.WriteAllText(Path.Combine(root, "entities.json"), "{ \"schemaVersion\": 2, \"entities\": [] }");
        File.WriteAllText(Path.Combine(root, "visuals.json"), """
            { "schemaVersion": 2, "recipes": [
              { "id": "spark", "kind": "particle", "seed": 7, "canvasWidth": 16, "canvasHeight": 16,
                "palette": ["#fff2a8"], "layers": [] }
            ] }
            """);
        File.WriteAllText(Path.Combine(root, "ui.json"), """
            { "schemaVersion": 2, "layouts": [
              { "id": "hud", "root": { "type": "text", "text": "tick", "bind": "world.tick" } }
            ] }
            """);
        File.WriteAllText(Path.Combine(root, "presentation.json"), """
            { "schemaVersion": 2, "timelines": [
              { "id": "pulse", "visualRecipeId": "spark", "durationTicks": 1, "loop": false,
                "particleCount": 1, "keyframes": [{ "tick": 0 }] }
            ], "bindings": [] }
            """);
        return new PresentationPack(root);
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
