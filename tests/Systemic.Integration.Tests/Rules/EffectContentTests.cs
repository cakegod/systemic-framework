using Systemic.Content;
using Systemic.Kernel;
using Systemic.Rules;

namespace Systemic.Tests;

public sealed class EffectContentTests
{
    [Fact]
    public void Merge_content_despawns_both_inputs_and_spawns_the_next_tier()
    {
        using var workspace = new EffectContentWorkspace();
        var content = workspace.Load();
        var materialized = content.CreateWorld(seed: 11, valueScale: 1);
        var world = materialized.World;
        var engine = new RuleEngine(content.Rules);
        var tierOne = world.ById(materialized.EntityIds["fruit.tier1"])!;

        var second = world.Add(new DataEntity(["fruit"], [], ("tier", 1)));
        engine.Dispatch(world, new SimulationFact("fruit.combine", tierOne.Id, second.Id));
        world.PruneRemoved();

        Assert.DoesNotContain(world.Entities,
            entity => entity.HasTag("fruit") && world.EffectiveProperty(entity, "tier") == 1);
        Assert.Equal(2, world.Entities.Count(entity =>
            entity.HasTag("fruit") && world.EffectiveProperty(entity, "tier") == 2));
    }

    [Fact]
    public void Hiking_buddy_content_modifies_the_actor_while_its_tag_is_present()
    {
        using var workspace = new EffectContentWorkspace();
        var content = workspace.Load();
        var materialized = content.CreateWorld(seed: 12, valueScale: 1);
        var world = materialized.World;
        var engine = new RuleEngine(content.Rules);
        var talent = world.ById(materialized.EntityIds["talent.hiking.buddy"])!;
        var walker = world.ById(materialized.EntityIds["actor.walker"])!;

        engine.Dispatch(world, new SimulationFact("talent.enable", talent.Id, walker.Id));

        Assert.Equal(3, world.EffectiveProperty(walker, "speed"));
        Assert.Single(world.Modifiers);
    }

    [Fact]
    public void An_upgrade_content_modifier_follows_its_rank_property()
    {
        using var workspace = new EffectContentWorkspace();
        var content = workspace.Load();
        var materialized = content.CreateWorld(seed: 13, valueScale: 1);
        var world = materialized.World;
        var engine = new RuleEngine(content.Rules);
        var digger = world.ById(materialized.EntityIds["actor.digger"])!;

        engine.Dispatch(world, new SimulationFact("upgrade.purchased", digger.Id, digger.Id));
        Assert.Equal(7, world.EffectiveProperty(digger, "rate"));

        Assert.True(digger.TrySetProperty(world, "rank", 4));
        Assert.Equal(9, world.EffectiveProperty(digger, "rate"));
    }
}

internal sealed class EffectContentWorkspace : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("systemic-effect-content-").FullName;

    public ContentPack Load()
    {
        File.WriteAllText(Path.Combine(Root, "game.json"), """
            {
              "schemaVersion": 3,
              "id": "sample.effect-content",
              "name": "Effect content",
              "tickRateHz": 60,
              "maxCatchUpSteps": 4,
              "rules": "rules.json",
              "entities": "entities.json"
            }
            """);
        File.WriteAllText(Path.Combine(Root, "entities.json"), """
            {
              "schemaVersion": 2,
              "entities": [
                { "id": "fruit.tier1", "kind": "product", "tags": ["fruit"],
                  "properties": { "tier": 1 },
                  "bounds": { "x": 0, "y": 0, "halfWidth": 1, "halfHeight": 1 } },
                { "id": "fruit.tier2", "kind": "product", "tags": ["fruit"],
                  "properties": { "tier": 2 },
                  "bounds": { "x": 4, "y": 0, "halfWidth": 1, "halfHeight": 1 } },
                { "id": "talent.hiking.buddy", "kind": "prop", "tags": ["talent.hiking"] },
                { "id": "actor.walker", "kind": "actor", "properties": { "speed": 2 } },
                { "id": "actor.digger", "kind": "actor", "properties": { "rate": 5, "rank": 2 } }
              ]
            }
            """);
        File.WriteAllText(Path.Combine(Root, "rules.json"), """
            {
              "schemaVersion": 2,
              "rules": [
                {
                  "id": "fruit.combine",
                  "on": "fruit.combine",
                  "priority": 0,
                  "effects": [
                    { "kind": "spawn", "subject": "source", "template": "fruit.tier2" },
                    { "kind": "despawn", "subject": "source" },
                    { "kind": "despawn", "subject": "target" }
                  ]
                },
                {
                  "id": "upgrade.miner.apply",
                  "on": "upgrade.purchased",
                  "priority": 0,
                  "effects": [
                    { "kind": "setModifier", "subject": "target", "property": "rate",
                      "operation": "add", "value": 1, "fromProperty": "rank" }
                  ]
                },
                {
                  "id": "talent.hiking.apply",
                  "on": "talent.enable",
                  "priority": 0,
                  "when": [ { "kind": "hasTag", "subject": "source", "tag": "talent.hiking" } ],
                  "effects": [
                    { "kind": "setModifier", "subject": "target", "property": "speed",
                      "operation": "add", "value": 1 }
                  ]
                }
              ]
            }
            """);
        return ContentPackLoader.Load(Root);
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
