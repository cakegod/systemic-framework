using Systemic.Kernel;
using Systemic.Content;
using Systemic.Host;
using Systemic.Incremental;
using Systemic.Rules;
using Systemic.Tests;

namespace Systemic.Tests.Incremental;

public sealed class IncrementalRuntimeTests
{
    [Fact]
    public void Producers_publish_facts_that_trigger_cross_trait_chain_reactions_with_capacity_bounds()
    {
        using var pack = CreatePack(initialOre: 0, capacity: 2);
        var content = TestPack.Load(pack.Root);
        var materialized = content.CreateWorld(seed: 3, valueScale: 1);
        var game = new SystemicGameBuilder(content, materialized).Build();
        var minerId = materialized.EntityIds["actor.miner"];
        var oreId = materialized.EntityIds["resource.ore"];

        Assert.True(game.AssignRole(minerId, "worker").Accepted);
        game.Step();

        Assert.True(materialized.World.ById(oreId)!.TryGetProperty(materialized.World, "balance", out var balance));
        Assert.Equal(2, balance);
        Assert.True(materialized.World.HasTag(materialized.World.ById(minerId)!, "synergy.connected"));
    }

    [Fact]
    public void Upgrade_purchase_spends_atomically_emits_chain_reactions_and_respects_limits()
    {
        using var pack = CreatePack(initialOre: 3, capacity: 5);
        var content = TestPack.Load(pack.Root);
        var materialized = content.CreateWorld(seed: 5, valueScale: 1);
        var game = new SystemicGameBuilder(content, materialized).Build();
        var playerId = materialized.EntityIds["actor.player"];
        var ore = materialized.World.ById(materialized.EntityIds["resource.ore"])!;
        var upgrade = materialized.World.ById(materialized.EntityIds["upgrade.drill"])!;

        var result = game.PurchaseUpgrade("upgrade.drill", playerId);
        var repeated = game.PurchaseUpgrade("upgrade.drill", playerId);

        Assert.True(result.Accepted);
        Assert.False(repeated.Accepted);
        Assert.True(ore.TryGetProperty(materialized.World, "balance", out var balance));
        Assert.Equal(1, balance);
        Assert.True(upgrade.TryGetProperty(materialized.World, "purchases", out var purchases));
        Assert.Equal(1, purchases);
        Assert.True(materialized.World.HasTag(upgrade, "owned"));
        Assert.True(materialized.World.HasTag(materialized.World.ById(playerId)!, "upgrade.synergy"));
    }

    [Fact]
    public void Unaffordable_upgrade_does_not_mutate_resources_or_purchase_state()
    {
        using var pack = CreatePack(initialOre: 1, capacity: 5);
        var content = TestPack.Load(pack.Root);
        var materialized = content.CreateWorld(seed: 5, valueScale: 1);
        var game = new SystemicGameBuilder(content, materialized).Build();
        var playerId = materialized.EntityIds["actor.player"];
        var ore = materialized.World.ById(materialized.EntityIds["resource.ore"])!;
        var upgrade = materialized.World.ById(materialized.EntityIds["upgrade.drill"])!;

        var result = game.PurchaseUpgrade("upgrade.drill", playerId);

        Assert.False(result.Accepted);
        Assert.True(ore.TryGetProperty(materialized.World, "balance", out var balance));
        Assert.Equal(1, balance);
        Assert.True(upgrade.TryGetProperty(materialized.World, "purchases", out var purchases));
        Assert.Equal(0, purchases);
    }

    [Fact]
    public void Invalid_upgrade_reaction_rolls_back_the_cost_and_purchase_count()
    {
        using var pack = CreatePack(initialOre: 3, capacity: 5);
        var content = TestPack.Load(pack.Root);
        content = content with
        {
            Rules = new CompiledRuleSet([
                new CompiledRule("purchase.start", "incremental.upgrade.purchased", 0, [],
                    [RuleEffect.Emit("purchase.overflow.first")]),
                new CompiledRule("purchase.overflow.first", "purchase.overflow.first", 0, [],
                    [RuleEffect.AdjustProperty(RuleSubject.Target, "purchases", double.MaxValue),
                     RuleEffect.Emit("purchase.overflow.second")]),
                new CompiledRule("purchase.overflow.second", "purchase.overflow.second", 0, [],
                    [RuleEffect.AdjustProperty(RuleSubject.Target, "purchases", double.MaxValue)])
            ])
        };
        var materialized = content.CreateWorld(seed: 5, valueScale: 1);
        var game = new SystemicGameBuilder(content, materialized).Build();
        var playerId = materialized.EntityIds["actor.player"];
        var ore = materialized.World.ById(materialized.EntityIds["resource.ore"])!;
        var upgrade = materialized.World.ById(materialized.EntityIds["upgrade.drill"])!;

        var result = game.PurchaseUpgrade("upgrade.drill", playerId);

        Assert.False(result.Accepted);
        Assert.True(result.Reactions!.Value.RolledBack);
        Assert.True(ore.TryGetProperty(materialized.World, "balance", out var balance));
        Assert.Equal(3, balance);
        Assert.True(upgrade.TryGetProperty(materialized.World, "purchases", out var purchases));
        Assert.Equal(0, purchases);
    }

    private static TestPack CreatePack(double initialOre, double capacity)
    {
        var pack = TestPack.Create();
        var gamePath = Path.Combine(pack.Root, "game.json");
        File.WriteAllText(gamePath, File.ReadAllText(gamePath).Replace(
            "\"ui\": \"ui.json\"", "\"ui\": \"ui.json\",\n  \"incremental\": \"economy.json\"", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(pack.Root, "entities.json"), """
            { "schemaVersion": 2, "entities": [
              { "id": "resource.ore", "kind": "resource", "tags": ["resource"], "properties": { "balance": 0 } },
              { "id": "actor.miner", "kind": "actor", "properties": { "rate": 0 } },
              { "id": "actor.player", "kind": "actor" },
              { "id": "upgrade.drill", "kind": "prop", "tags": ["upgrade"], "properties": { "purchases": 0 } }
            ] }
            """);
        File.WriteAllText(Path.Combine(pack.Root, "economy.json"), $$"""
            {
              "schemaVersion": 2,
              "resources": [{ "id": "ore", "entityId": "resource.ore", "initialValue": {{initialOre}}, "capacity": {{capacity}} }],
              "producers": [{ "id": "miner", "entityId": "actor.miner", "resourceId": "ore", "baseRate": 2 }],
              "upgrades": [{ "id": "upgrade.drill", "entityId": "upgrade.drill", "costs": [{ "resourceId": "ore", "amount": 2 }] }],
              "roles": [{ "id": "worker", "tags": ["worker"], "capabilities": ["produce"] }]
            }
            """);
        File.WriteAllText(Path.Combine(pack.Root, "rules.json"), """
            { "schemaVersion": 2, "rules": [
              { "id": "production.adds-bonus", "on": "incremental.production", "priority": 5,
                "when": [{ "kind": "hasTag", "subject": "source", "tag": "worker" }],
                "effects": [
                  { "kind": "adjustProperty", "subject": "target", "property": "balance", "amount": 1 },
                  { "kind": "emitEvent", "event": "incremental.production.chain" }
                ] },
              { "id": "production.chain-connects", "on": "incremental.production.chain", "priority": 1,
                "effects": [{ "kind": "addTag", "subject": "source", "tag": "synergy.connected" }] },
              { "id": "upgrade-owns", "on": "incremental.upgrade.purchased", "priority": 5,
                "when": [{ "kind": "hasTag", "subject": "target", "tag": "upgrade" }],
                "effects": [
                  { "kind": "addTag", "subject": "target", "tag": "owned" },
                  { "kind": "emitEvent", "event": "incremental.upgrade.chain" }
                ] },
              { "id": "upgrade-connects", "on": "incremental.upgrade.chain", "priority": 1,
                "effects": [{ "kind": "addTag", "subject": "source", "tag": "upgrade.synergy" }] }
            ] }
            """);
        return pack;
    }
}
