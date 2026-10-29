using System.Text.Json;
using Systemic.Content;
using Systemic.Host;
using Systemic.Rules;

namespace Systemic.Tests.Content;

public sealed class ContentReloadTests
{
    [Fact]
    public void Compatible_candidate_compiles_new_rules_without_changing_the_running_game()
    {
        using var pack = CoreOnlyPack.Create();
        var active = ContentPackLoader.Load(pack.Root);
        using var game = new SystemicGameBuilder(active, active.CreateWorld(7, 1)).Build();
        var rulesFile = Path.Combine(pack.Root, "rules.json");
        File.WriteAllText(rulesFile, File.ReadAllText(rulesFile).Replace("-2", "-3", StringComparison.Ordinal));

        var result = ContentReload.Prepare(active, pack.Root);

        Assert.True(result.Succeeded);
        var candidate = Assert.IsType<ContentReloadCandidate>(result.Candidate);
        Assert.Same(active, game.Content);
        Assert.True(candidate.IsBasedOn(active));
        Assert.NotEqual(active.ContentFingerprint, candidate.Content.ContentFingerprint);
        Assert.Equal(active.ContentFingerprint, candidate.SourceFingerprint);
        Assert.Equal(-3, candidate.Content.Rules.Rules[0].Effects[0].Value);
        var target = game.World.ById(game.EntityId("actor.target"))!;
        game.Publish(new SimulationFact("contact", target.Id, target.Id));
        Assert.Equal(3, game.World.EffectiveProperty(target, "health"));
        Assert.False(candidate.IsBasedOn(ContentPackLoader.Load(pack.Root)));
    }

    [Fact]
    public void Invalid_schema_has_actionable_file_diagnostics_and_keeps_active_content()
    {
        using var pack = CoreOnlyPack.Create();
        var active = ContentPackLoader.Load(pack.Root);
        var path = Path.Combine(pack.Root, "rules.json");
        File.WriteAllText(path, "{\"schemaVersion\":2,\"rules\":42}");

        var result = ContentReload.Prepare(active, pack.Root);

        Assert.False(result.Succeeded);
        Assert.Null(result.Candidate);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.File == "rules.json"
            && diagnostic.Path.Contains("rules", StringComparison.Ordinal));
        Assert.Equal(-2, active.Rules.Rules[0].Effects[0].Value);
    }

    [Fact]
    public void Reload_requires_the_active_tuning_schemas_to_validate_its_candidate()
    {
        using var pack = CoreOnlyPack.Create();
        var schema = new ContentTuningSchema([TuningProperty.Number("health", minimum: 0, maximum: 10)]);
        var registry = new ContentModuleRegistry().AddTuningSchema("actor.target", schema);
        var active = ContentPackLoader.Load(pack.Root, registry);
        File.WriteAllText(Path.Combine(pack.Root, "entities.json"),
            File.ReadAllText(Path.Combine(pack.Root, "entities.json"))
                .Replace("\"health\": 5", "\"health\": 11", StringComparison.Ordinal));

        var result = ContentReload.Prepare(active, pack.Root);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "reload.tuning_schemas_missing");
        Assert.Equal(5, active.Tuning("actor.target").GetNumber("health"));
    }

    [Fact]
    public void Reload_runs_supplied_tuning_schemas_and_preserves_typed_candidate_values()
    {
        using var pack = CoreOnlyPack.Create();
        var schema = new ContentTuningSchema([TuningProperty.Number("health", minimum: 0, maximum: 10)]);
        var active = ContentPackLoader.Load(pack.Root,
            new ContentModuleRegistry().AddTuningSchema("actor.target", schema));
        var entitiesPath = Path.Combine(pack.Root, "entities.json");
        File.WriteAllText(entitiesPath, File.ReadAllText(entitiesPath)
            .Replace("\"health\": 5", "\"health\": 11", StringComparison.Ordinal));

        var invalid = ContentReload.Prepare(active, pack.Root,
            tuningSchemas: new Dictionary<string, ContentTuningSchema> { ["actor.target"] = schema });

        Assert.False(invalid.Succeeded);
        Assert.Contains(invalid.Diagnostics, diagnostic => diagnostic.Code == "tuning.range"
            && diagnostic.File == "entities.json");

        File.WriteAllText(entitiesPath, File.ReadAllText(entitiesPath)
            .Replace("\"health\": 11", "\"health\": 7", StringComparison.Ordinal));
        var valid = ContentReload.Prepare(active, pack.Root,
            tuningSchemas: new Dictionary<string, ContentTuningSchema> { ["actor.target"] = schema });

        Assert.True(valid.Succeeded);
        Assert.Equal(7, valid.Candidate!.Content.Tuning("actor.target").GetNumber("health"));
    }

    [Theory]
    [InlineData("\"id\": \"sample.core\"", "\"id\": \"sample.other\"", "reload.identity_mismatch")]
    [InlineData("\"tickRateHz\": 30", "\"tickRateHz\": 60", "reload.clock_mismatch")]
    public void Incompatible_manifest_is_rejected(string original, string replacement, string diagnosticCode)
    {
        using var pack = CoreOnlyPack.Create();
        var active = ContentPackLoader.Load(pack.Root);
        var path = Path.Combine(pack.Root, "game.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace(original, replacement, StringComparison.Ordinal));

        var result = ContentReload.Prepare(active, pack.Root);

        Assert.False(result.Succeeded);
        Assert.Null(result.Candidate);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == diagnosticCode);
        Assert.Equal("sample.core", active.Manifest.Id);
        Assert.Equal(30, active.Manifest.TickRateHz);
    }

    [Fact]
    public void Changed_stable_entity_identity_is_rejected()
    {
        using var pack = CoreOnlyPack.Create();
        var active = ContentPackLoader.Load(pack.Root);
        var path = Path.Combine(pack.Root, "entities.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("actor.target", "actor.replacement", StringComparison.Ordinal));

        var result = ContentReload.Prepare(active, pack.Root);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "reload.entity_mismatch");
        Assert.Equal("actor.target", active.Entities.Definitions[0].Id);
    }

    [Fact]
    public void Module_compilation_never_reuses_the_active_module_instance()
    {
        using var pack = CoreOnlyPack.Create();
        var path = Path.Combine(pack.Root, "game.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"entities\": \"entities.json\"",
            "\"entities\": \"entities.json\", \"modules\": { \"test.reload\": {} }", StringComparison.Ordinal));
        var activeModule = new TestModule();
        var active = ContentPackLoader.Load(pack.Root, new ContentModuleRegistry().Add(activeModule));
        Assert.Equal(1, activeModule.CompileCount);

        var reused = ContentReload.Prepare(active, pack.Root, [activeModule]);

        Assert.False(reused.Succeeded);
        Assert.Contains(reused.Diagnostics, diagnostic => diagnostic.Code == "reload.module_instance_reused");
        Assert.Equal(1, activeModule.CompileCount);
        var freshModule = new TestModule();
        var fresh = ContentReload.Prepare(active, pack.Root, [freshModule]);
        Assert.True(fresh.Succeeded);
        Assert.Equal(1, freshModule.CompileCount);
        Assert.Equal(1, activeModule.CompileCount);
    }

    private sealed class TestModule : ISystemicContentModule
    {
        public string Id => "test.reload";
        public int CompileCount { get; private set; }
        public ISystemicModuleContent Compile(ContentModuleContext context, JsonElement declaration)
        {
            CompileCount++;
            return new TestContent();
        }
        public void Configure(SystemicGameBuilder builder, ISystemicModuleContent content) { }
    }

    private sealed class TestContent : ISystemicModuleContent;
}
