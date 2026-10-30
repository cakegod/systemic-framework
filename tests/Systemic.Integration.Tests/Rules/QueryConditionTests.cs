using Systemic.Content;
using Systemic.Kernel;
using Systemic.Rules;

namespace Systemic.Tests;

public sealed class QueryConditionTests
{
    [Fact]
    public void Counts_entities_by_tag()
    {
        using var pack = TestPack.Create();
        WriteEntities(pack, """
            { "schemaVersion": 2, "entities": [
              { "id": "housing.a", "kind": "prop", "tags": ["housing"], "properties": { "built": 1 } },
              { "id": "housing.b", "kind": "prop", "tags": ["housing"], "properties": { "built": 1 } },
              { "id": "housing.c", "kind": "prop", "tags": ["housing"], "properties": { "built": 1 } },
              { "id": "actor.owner", "kind": "actor", "properties": { "score": 0 } }
            ] }
            """);
        WriteRules(pack, """
            { "schemaVersion": 2, "rules": [
              { "id": "housing.bonus", "on": "contact", "priority": 0,
                "when": [{ "kind": "query", "selector": { "tag": "housing" },
                  "aggregate": "count", "comparison": "atLeast", "value": 3 }],
                "effects": [{ "kind": "adjustProperty", "subject": "target", "property": "score", "amount": 1 }] }
            ] }
            """);
        var content = SystemicTestContent.Load(pack.Root);
        var materialized = content.CreateWorld(seed: 1, valueScale: 1);
        var owner = materialized.World.ById(materialized.EntityIds["actor.owner"])!;

        var result = new RuleEngine(content.Rules).Dispatch(materialized.World,
            new SimulationFact("contact", owner.Id, owner.Id));

        Assert.Equal(1, result.RulesMatched);
        Assert.True(owner.TryGetProperty(materialized.World, "score", out var score));
        Assert.Equal(1, score);
    }

    [Fact]
    public void Filters_entities_by_property_threshold()
    {
        using var pack = TestPack.Create();
        WriteEntities(pack, """
            { "schemaVersion": 2, "entities": [
              { "id": "housing.a", "kind": "prop", "tags": ["housing"], "properties": { "built": 2 } },
              { "id": "housing.b", "kind": "prop", "tags": ["housing"], "properties": { "built": 1 } },
              { "id": "actor.owner", "kind": "actor", "properties": { "score": 0 } }
            ] }
            """);
        WriteRules(pack, """
            { "schemaVersion": 2, "rules": [
              { "id": "housing.upgrade", "on": "contact", "priority": 0,
                "when": [{ "kind": "query", "selector": { "tag": "housing", "property": "built",
                    "propertyComparison": "atLeast", "propertyValue": 2 },
                  "aggregate": "count", "comparison": "equals", "value": 1 }],
                "effects": [{ "kind": "adjustProperty", "subject": "target", "property": "score", "amount": 1 }] }
            ] }
            """);
        var content = SystemicTestContent.Load(pack.Root);
        var materialized = content.CreateWorld(seed: 1, valueScale: 1);
        var owner = materialized.World.ById(materialized.EntityIds["actor.owner"])!;

        var result = new RuleEngine(content.Rules).Dispatch(materialized.World,
            new SimulationFact("contact", owner.Id, owner.Id));

        Assert.Equal(1, result.RulesMatched);
        Assert.True(owner.TryGetProperty(materialized.World, "score", out var score));
        Assert.Equal(1, score);
    }

    [Fact]
    public void Selects_entities_inside_a_spatial_range()
    {
        using var pack = TestPack.Create();
        WriteEntities(pack, """
            { "schemaVersion": 2, "entities": [
              { "id": "housing.near", "kind": "prop", "tags": ["housing"], "bounds": { "x": 3, "y": 0, "halfWidth": 1, "halfHeight": 1 } },
              { "id": "housing.far", "kind": "prop", "tags": ["housing"], "bounds": { "x": 40, "y": 0, "halfWidth": 1, "halfHeight": 1 } },
              { "id": "actor.owner", "kind": "actor", "bounds": { "x": 0, "y": 0, "halfWidth": 1, "halfHeight": 1 }, "properties": { "score": 0 } }
            ] }
            """);
        WriteRules(pack, """
            { "schemaVersion": 2, "rules": [
              { "id": "housing.nearby", "on": "contact", "priority": 0,
                "when": [{ "kind": "query", "selector": { "tag": "housing", "origin": "source", "radius": 5 },
                  "aggregate": "count", "comparison": "equals", "value": 1 }],
                "effects": [{ "kind": "adjustProperty", "subject": "target", "property": "score", "amount": 1 }] }
            ] }
            """);
        var content = SystemicTestContent.Load(pack.Root);
        var materialized = content.CreateWorld(seed: 1, valueScale: 1);
        var owner = materialized.World.ById(materialized.EntityIds["actor.owner"])!;

        var result = new RuleEngine(content.Rules).Dispatch(materialized.World,
            new SimulationFact("contact", owner.Id, owner.Id));

        Assert.Equal(1, result.RulesMatched);
    }

    [Fact]
    public void Selects_the_nearest_entity_with_a_limit()
    {
        using var pack = TestPack.Create();
        WriteEntities(pack, """
            { "schemaVersion": 2, "entities": [
              { "id": "enemy.far", "kind": "prop", "tags": ["enemy"], "bounds": { "x": 8, "y": 0, "halfWidth": 1, "halfHeight": 1 } },
              { "id": "enemy.near", "kind": "prop", "tags": ["enemy"], "bounds": { "x": 2, "y": 0, "halfWidth": 1, "halfHeight": 1 } },
              { "id": "actor.owner", "kind": "actor", "bounds": { "x": 0, "y": 0, "halfWidth": 1, "halfHeight": 1 }, "properties": { "score": 0 } }
            ] }
            """);
        WriteRules(pack, """
            { "schemaVersion": 2, "rules": [
              { "id": "enemy.nearest", "on": "contact", "priority": 0,
                "when": [{ "kind": "query", "selector": { "tag": "enemy", "origin": "source",
                    "order": "nearest", "limit": 1 },
                  "aggregate": "nearest", "comparison": "atMost", "value": 1 }],
                "effects": [{ "kind": "adjustProperty", "subject": "target", "property": "score", "amount": 1 }] }
            ] }
            """);
        var content = SystemicTestContent.Load(pack.Root);
        var materialized = content.CreateWorld(seed: 1, valueScale: 1);
        var owner = materialized.World.ById(materialized.EntityIds["actor.owner"])!;

        var result = new RuleEngine(content.Rules).Dispatch(materialized.World,
            new SimulationFact("contact", owner.Id, owner.Id));

        Assert.Equal(1, result.RulesMatched);
    }

    [Fact]
    public void Aggregates_a_property_and_tests_a_formula()
    {
        using var pack = TestPack.Create();
        WriteEntities(pack, """
            { "schemaVersion": 2, "entities": [
              { "id": "worker.a", "kind": "actor", "tags": ["worker"], "properties": { "energy": 6 } },
              { "id": "worker.b", "kind": "actor", "tags": ["worker"], "properties": { "energy": 4 } },
              { "id": "actor.owner", "kind": "actor", "properties": { "score": 0 } }
            ] }
            """);
        WriteRules(pack, """
            { "schemaVersion": 2, "rules": [
              { "id": "worker.power", "on": "contact", "priority": 0,
                "when": [{ "kind": "query", "selector": { "tag": "worker" },
                  "aggregate": "sum", "aggregateProperty": "energy",
                  "formula": "query.value >= 10 && query.count == 2" }],
                "effects": [{ "kind": "adjustProperty", "subject": "target", "property": "score", "amount": 1 }] }
            ] }
            """);
        var content = SystemicTestContent.Load(pack.Root);
        var materialized = content.CreateWorld(seed: 1, valueScale: 1);
        var owner = materialized.World.ById(materialized.EntityIds["actor.owner"])!;

        var result = new RuleEngine(content.Rules).Dispatch(materialized.World,
            new SimulationFact("contact", owner.Id, owner.Id));

        Assert.Equal(1, result.RulesMatched);
        Assert.True(owner.TryGetProperty(materialized.World, "score", out var score));
        Assert.Equal(1, score);
    }

    private static void WriteEntities(TestPack pack, string json) =>
        File.WriteAllText(Path.Combine(pack.Root, "entities.json"), json);

    private static void WriteRules(TestPack pack, string json) =>
        File.WriteAllText(Path.Combine(pack.Root, "rules.json"), json);
}
