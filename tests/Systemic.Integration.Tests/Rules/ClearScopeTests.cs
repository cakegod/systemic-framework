using System.Text.Json.Nodes;
using Systemic.Content;
using Systemic.Host;
using Systemic.Rules;
using Systemic.Save;

namespace Systemic.Tests;

public sealed class ClearScopeTests
{
    [Fact]
    public void ARunResetClearsTheRunScopeAndPreservesTheMetaScope()
    {
        using var workspace = new ClearScopeWorkspace();
        var content = workspace.Load();
        var materialized = content.CreateWorld(seed: 31, valueScale: 1);
        var world = materialized.World;
        var engine = new RuleEngine(content.Rules);
        var runId = materialized.EntityIds["run.actor"];

        engine.Dispatch(world, new SimulationFact("run.reset", runId, runId));
        world.PruneRemoved();

        Assert.DoesNotContain(world.Entities, entity => entity.HasTag("scope.run"));
        Assert.Contains(world.Entities, entity => entity.HasTag("scope.meta"));

        // The preserved entity is untouched, not merely recreated: its property still reads.
        var meta = world.Entities.Single(entity => entity.HasTag("scope.meta"));
        Assert.Equal(7, world.EffectiveProperty(meta, "shards"));
    }

    [Fact]
    public void ATransientScopeExpiresWithoutTouchingPersistentScopes()
    {
        using var workspace = new ClearScopeWorkspace();
        var content = workspace.Load();
        var materialized = content.CreateWorld(seed: 32, valueScale: 1);
        var world = materialized.World;
        var engine = new RuleEngine(content.Rules);
        var summonId = materialized.EntityIds["scope.summon"];

        engine.Dispatch(world, new SimulationFact("session.expire", summonId, summonId));
        world.PruneRemoved();

        Assert.DoesNotContain(world.Entities, entity => entity.HasTag("scope.session"));
        Assert.Contains(world.Entities, entity => entity.HasTag("scope.run"));
        Assert.Contains(world.Entities, entity => entity.HasTag("scope.meta"));
    }

    [Fact]
    public void ASaveTakenBeforeAResetRestoresTheClearedScope()
    {
        using var workspace = new ClearScopeWorkspace();
        var content = workspace.Load();
        var host = HeadlessSystemicHost.Create(content, seed: 33);
        var runId = host.Game.EntityId("run.actor");
        var codec = new SystemicSaveCodec();
        var saved = codec.Save(host.Game);

        host.Game.Publish(new SimulationFact("run.reset", runId, runId));
        host.Game.World.PruneRemoved();
        Assert.DoesNotContain(host.Game.World.Entities, entity => entity.HasTag("scope.run"));

        var restored = codec.Load(content, saved);

        Assert.Contains(restored.World.Entities, entity => entity.HasTag("scope.run"));
        Assert.Contains(restored.World.Entities, entity => entity.HasTag("scope.meta"));

        // Re-saving the restored world must reproduce the original world section exactly: the codec's
        // canonical JSON and checksum make that a stronger claim than counting entities. Extension
        // state is excluded because a rebuilt game registers a fresh clock and rule history.
        using var reloaded = new SystemicGameBuilder(content, restored).Build();
        Assert.Equal(JsonNode.Parse(saved)!["world"]!.ToJsonString(),
            JsonNode.Parse(codec.Save(reloaded))!["world"]!.ToJsonString());
    }

    [Fact]
    public void ClearScopeRejectsASubjectBecauseItAddressesTheWholeScope()
    {
        using var workspace = new ClearScopeWorkspace();

        var error = Assert.Throws<ContentPackException>(() => workspace.Load("""
            { "schemaVersion": 2, "rules": [
              { "id": "run.reset", "on": "run.reset", "priority": 0,
                "effects": [{ "kind": "clearScope", "tag": "scope.run", "subject": "target" }] }
            ] }
            """));

        Assert.Contains("content.schema_invalid", error.Message, StringComparison.Ordinal);
        Assert.Contains("rules.json", error.Message, StringComparison.Ordinal);
    }
}

internal sealed class ClearScopeWorkspace : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("systemic-clear-scope-").FullName;

    public ContentPack Load(string? rules = null)
    {
        File.WriteAllText(Path.Combine(Root, "game.json"), """
            { "schemaVersion": 3, "id": "sample.clear-scope", "name": "Clear scope",
              "tickRateHz": 60, "maxCatchUpSteps": 4, "rules": "rules.json", "entities": "entities.json" }
            """);
        File.WriteAllText(Path.Combine(Root, "entities.json"), """
            { "schemaVersion": 2, "entities": [
              { "id": "run.actor", "kind": "product", "tags": ["scope.run"], "properties": { "heat": 3 } },
              { "id": "meta.record", "kind": "product", "tags": ["scope.meta"], "properties": { "shards": 7 } },
              { "id": "scope.summon", "kind": "product", "tags": ["scope.session"], "properties": { "life": 1 } }
            ] }
            """);
        File.WriteAllText(Path.Combine(Root, "rules.json"), rules ?? """
            { "schemaVersion": 2, "rules": [
              { "id": "run.reset", "on": "run.reset", "priority": 0,
                "effects": [{ "kind": "clearScope", "tag": "scope.run" }] },
              { "id": "session.expire", "on": "session.expire", "priority": 0,
                "effects": [{ "kind": "clearScope", "tag": "scope.session" }] }
            ] }
            """);
        return ContentPackLoader.Load(Root);
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}
