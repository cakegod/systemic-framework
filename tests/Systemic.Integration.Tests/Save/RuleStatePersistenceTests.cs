using Systemic.Host;
using Systemic.Rules;
using Systemic.Save;

namespace Systemic.Tests.Save;

public sealed class RuleStatePersistenceTests
{
    [Fact]
    public void APendingDelayedFactSurvivesSaveAndLoad()
    {
        using var workspace = new RuleJsonWorkspace(
            rules: """
                {
                  "schemaVersion": 2,
                  "rules": [
                    { "id": "start", "on": "start", "priority": 0,
                      "effects": [ { "kind": "schedule", "event": "step", "delayTicks": 2 } ] },
                    { "id": "step", "on": "step", "priority": 0,
                      "effects": [ { "kind": "adjustProperty", "subject": "target",
                                     "property": "score", "amount": 1 } ] }
                  ]
                }
                """,
            entities: """
                {
                  "schemaVersion": 2,
                  "entities": [
                    { "id": "actor.owner", "kind": "actor", "properties": { "score": 0 } }
                  ]
                }
                """);
        var content = workspace.Load();
        var materialized = content.CreateWorld(seed: 1, valueScale: 1);
        var ownerId = materialized.EntityIds["actor.owner"];
        using var original = new SystemicGameBuilder(content, materialized).Build();
        original.Publish(new SimulationFact("start", ownerId, ownerId));

        var codec = new SystemicSaveCodec();
        using var restored = new SystemicGameBuilder(content, codec.Load(content, codec.Save(original))).Build();
        restored.Step();
        restored.Step();

        Assert.Equal(1, restored.World.EffectiveProperty(restored.World.ById(ownerId)!, "score"));
    }

    [Fact]
    public void AnActiveCooldownSurvivesSaveAndLoad()
    {
        using var workspace = new RuleJsonWorkspace(
            rules: """
                {
                  "schemaVersion": 2,
                  "rules": [
                    { "id": "pulse", "on": "pulse", "priority": 0, "cooldownTicks": 10,
                      "effects": [ { "kind": "adjustProperty", "subject": "target",
                                     "property": "score", "amount": 1 } ] }
                  ]
                }
                """,
            entities: """
                {
                  "schemaVersion": 2,
                  "entities": [
                    { "id": "actor.pulse", "kind": "actor", "properties": { "score": 0 } }
                  ]
                }
                """);
        var content = workspace.Load();
        var materialized = content.CreateWorld(seed: 2, valueScale: 1);
        var pulseId = materialized.EntityIds["actor.pulse"];
        using var original = new SystemicGameBuilder(content, materialized).Build();
        original.Publish(new SimulationFact("pulse", pulseId, pulseId));

        var codec = new SystemicSaveCodec();
        using var restored = new SystemicGameBuilder(content, codec.Load(content, codec.Save(original))).Build();
        var pulse = restored.World.ById(pulseId)!;
        restored.Publish(new SimulationFact("pulse", pulseId, pulseId));

        Assert.Equal(1, restored.World.EffectiveProperty(pulse, "score"));

        restored.World.LogicalTick = 10;
        restored.Publish(new SimulationFact("pulse", pulseId, pulseId));

        Assert.Equal(2, restored.World.EffectiveProperty(pulse, "score"));
    }
}
