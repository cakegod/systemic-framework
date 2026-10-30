using Systemic.Content;
using Systemic.Kernel;
using Systemic.Rules;

namespace Systemic.Tests;

public sealed class FactPipelineTests
{
    [Fact]
    public void Declared_payloads_travel_through_emitted_and_scheduled_facts()
    {
        using var pack = TestPack.Create();
        File.WriteAllText(Path.Combine(pack.Root, "entities.json"), """
            { "schemaVersion": 2, "entities": [
              { "id": "actor.source", "kind": "actor", "properties": { "health": 10 } },
              { "id": "actor.target", "kind": "actor", "properties": { "health": 10 } }
            ] }
            """);
        File.WriteAllText(Path.Combine(pack.Root, "rules.json"), """
            {
              "schemaVersion": 2,
              "facts": [
                { "id": "test.combine", "payload": [{ "key": "tier", "kind": "integer" }] },
                { "id": "test.created", "payload": [
                  { "key": "tier", "kind": "integer" },
                  { "key": "label", "kind": "text" }
                ] },
                { "id": "test.strike", "payload": [
                  { "key": "damage", "kind": "number" },
                  { "key": "critical", "kind": "boolean" }
                ] }
              ],
              "rules": [
                { "id": "combine.emit", "on": "test.combine", "priority": 0,
                  "effects": [{ "kind": "emitEvent", "event": "test.created",
                    "payload": [
                      { "key": "tier", "value": 3 },
                      { "key": "label", "value": "fruit" }
                    ] }] },
                { "id": "strike.hit", "on": "test.strike", "priority": 0,
                  "effects": [{ "kind": "adjustProperty", "subject": "target",
                    "property": "health", "amount": -1 }] }
              ],
              "schedules": [
                { "id": "strike.every-tick", "event": "test.strike", "everyTicks": 1,
                  "targetEntityId": "actor.target",
                  "payload": [
                    { "key": "damage", "value": 4.5 },
                    { "key": "critical", "value": true }
                  ] }
              ]
            }
            """);

        var content = SystemicTestContent.Load(pack.Root);
        Assert.Equal(["test.combine", "test.created", "test.strike"],
            content.Rules.Facts.Select(fact => fact.Id));

        var schedule = Assert.Single(content.Rules.Schedules);
        Assert.True(schedule.Payload!.TryGetValue("damage", out var scheduledDamage));
        Assert.Equal(4.5, scheduledDamage.Number);
        Assert.True(schedule.Payload.TryGetValue("critical", out var scheduledCritical));
        Assert.True(scheduledCritical.Boolean);

        var world = new World(seed: 12, valueScale: 1);
        var source = world.Add(new DataEntity([], [], ("health", 10)));
        var target = world.Add(new DataEntity([], [], ("health", 10)));
        var engine = new RuleEngine(content.Rules);
        var seen = new List<SimulationFact>();

        var combine = engine.Dispatch(world, new SimulationFact("test.combine", source.Id, target.Id),
            (fact, _) => seen.Add(fact));

        Assert.False(combine.RolledBack);
        var created = Assert.Single(seen, fact => fact.Type == "test.created");
        Assert.True(created.Payload!.TryGetValue("tier", out var tier));
        Assert.Equal(3, tier.Integer);
        Assert.True(created.Payload.TryGetValue("label", out var label));
        Assert.Equal("fruit", label.Text);

        var strike = engine.Dispatch(world, new SimulationFact("test.strike", source.Id, target.Id,
            Payload: new SimulationFactPayload([
                new("damage", SimulationFactValue.FromNumber(4.5)),
                new("critical", SimulationFactValue.FromBoolean(true))
            ])));

        Assert.Equal(1, strike.RulesMatched);
        Assert.Equal(9, world.EffectiveProperty(target, "health"));
    }

    [Fact]
    public void Rejects_payload_values_that_do_not_match_the_declared_field()
    {
        using var pack = TestPack.Create();
        File.WriteAllText(Path.Combine(pack.Root, "rules.json"), """
            {
              "schemaVersion": 2,
              "facts": [
                { "id": "test.created", "payload": [{ "key": "tier", "kind": "integer" }] }
              ],
              "rules": [
                { "id": "create", "on": "test.created", "priority": 0,
                  "effects": [{ "kind": "emitEvent", "event": "test.created",
                    "payload": [{ "key": "tier", "value": "three" }] }] }
              ]
            }
            """);

        var error = Assert.Throws<ContentPackException>(() => SystemicTestContent.Load(pack.Root));

        var diagnostic = Assert.Single(error.Diagnostics);
        Assert.Equal("rules.json", diagnostic.File);
        Assert.Equal("$.rules[0].effects[0].payload[0].value", diagnostic.Path);
        Assert.Contains("must be integer", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_duplicate_payload_keys_with_a_content_diagnostic()
    {
        using var pack = TestPack.Create();
        File.WriteAllText(Path.Combine(pack.Root, "rules.json"), """
            {
              "schemaVersion": 2,
              "facts": [
                { "id": "test.created", "payload": [{ "key": "tier", "kind": "integer" }] }
              ],
              "rules": [
                { "id": "create", "on": "test.created", "priority": 0,
                  "effects": [{ "kind": "emitEvent", "event": "test.created",
                    "payload": [
                      { "key": "tier", "value": 1 },
                      { "key": "tier", "value": 2 }
                    ] }] }
              ]
            }
            """);

        var error = Assert.Throws<ContentPackException>(() => SystemicTestContent.Load(pack.Root));

        var diagnostic = Assert.Single(error.Diagnostics);
        Assert.Equal("content.invalid", diagnostic.Code);
        Assert.Equal("$.rules[0].effects[0].payload[1].key", diagnostic.Path);
        Assert.Contains("Duplicate", diagnostic.Message, StringComparison.Ordinal);
    }
}
