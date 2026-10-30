using System.Collections.Immutable;
using Systemic.Content;
using Systemic.Host;
using Systemic.Kernel;
using Systemic.Rules;

namespace Systemic.Tests;

public sealed class EffectPrimitiveTests
{
    [Fact]
    public void Quantity_operations_apply_in_order_without_losing_exactness()
    {
        var world = new World(seed: 1, valueScale: 1);
        var entity = world.Add(new DataEntity([], [], ("value", 10)));
        var engine = new RuleEngine(new CompiledRuleSet([
            new CompiledRule("ops", "ops", 0, [],
            [
                RuleEffect.AdjustProperty(RuleSubject.Target, "value", 2, QuantityOperation.Set),
                RuleEffect.AdjustProperty(RuleSubject.Target, "value", 5, QuantityOperation.Multiply),
                RuleEffect.AdjustProperty(RuleSubject.Target, "value", 6, QuantityOperation.Min),
                RuleEffect.AdjustProperty(RuleSubject.Target, "value", 8, QuantityOperation.Max),
            ])
        ]));

        var result = engine.Dispatch(world, new SimulationFact("ops", entity.Id, entity.Id));

        Assert.False(result.CutOff);
        Assert.Equal(8, world.EffectiveProperty(entity, "value"));
    }

    [Fact]
    public void RemoveTag_drops_only_the_named_tag()
    {
        var world = new World(seed: 2, valueScale: 1);
        var entity = world.Add(new DataEntity(["active", "boosted"], []));
        var engine = new RuleEngine(new CompiledRuleSet([
            new CompiledRule("expire", "expire", 0, [],
                [RuleEffect.RemoveTag(RuleSubject.Target, "boosted")])
        ]));

        engine.Dispatch(world, new SimulationFact("expire", entity.Id, entity.Id));

        Assert.True(entity.HasTag("active"));
        Assert.False(entity.HasTag("boosted"));
    }

    [Fact]
    public void Despawn_marks_the_entity_and_prune_removes_it()
    {
        var world = new World(seed: 3, valueScale: 1);
        var doomed = world.Add(new DataEntity(["merge.candidate"], []));
        var engine = new RuleEngine(new CompiledRuleSet([
            new CompiledRule("merge", "merge", 0, [],
                [RuleEffect.Despawn(RuleSubject.Source), RuleEffect.Despawn(RuleSubject.Target)])
        ]));

        var result = engine.Dispatch(world, new SimulationFact("merge", doomed.Id, doomed.Id));

        Assert.False(result.RolledBack);
        Assert.True(doomed.ToRemove);
        world.PruneRemoved();
        Assert.Null(world.ById(doomed.Id));
        Assert.DoesNotContain(world.Entities, entity => entity.Id == doomed.Id);
    }

    [Fact]
    public void Transfer_moves_what_the_source_holds_and_leaves_the_rest()
    {
        var world = new World(seed: 4, valueScale: 1);
        var carrier = world.Add(new DataEntity([], [], ("ore", 3)));
        var store = world.Add(new DataEntity([], [], ("ore", 10)));
        var engine = new RuleEngine(new CompiledRuleSet([
            new CompiledRule("deposit", "deposit", 0, [],
                [RuleEffect.Transfer(RuleSubject.Source, "ore", "ore", 5)])
        ]));

        var result = engine.Dispatch(world, new SimulationFact("deposit", carrier.Id, store.Id));

        Assert.False(result.RolledBack);
        Assert.Equal(0, world.EffectiveProperty(carrier, "ore"));
        Assert.Equal(13, world.EffectiveProperty(store, "ore"));
    }

    [Fact]
    public void Spawn_instantiates_the_template_at_the_fact_position()
    {
        var world = new World(seed: 5, valueScale: 1);
        var source = world.Add(new DataEntity([], []));
        var template = new SpawnTemplate(
            "fruit.tier2",
            ["fruit", "combine.target"],
            ["renderable"],
            new Dictionary<string, double> { ["tier"] = 2 }.ToImmutableDictionary(),
            new Bounds2(-2, -2, 2, 2));
        var rules = new CompiledRuleSet(
            [
                new CompiledRule("combine", "combine", 0, [], [RuleEffect.Spawn(RuleSubject.Source, template)])
            ])
        { Templates = new Dictionary<string, SpawnTemplate> { [template.Id] = template }.ToImmutableDictionary() };
        var engine = new RuleEngine(rules);
        var position = new SimulationFactPosition(40, -16);

        engine.Dispatch(world, new SimulationFact("combine", source.Id, source.Id, 0, 0, position));

        var spawned = Assert.Single(world.Entities, entity => entity.Id != source.Id);
        Assert.True(spawned.HasTag("fruit"));
        Assert.True(world.HasCapability(spawned, "renderable"));
        Assert.Equal(2, world.EffectiveProperty(spawned, "tier"));
        Assert.True(world.TryGetSpatialBounds(spawned.Id, out var bounds));
        Assert.Equal(40, bounds.X);
        Assert.Equal(-16, bounds.Y);
        Assert.Equal(2, bounds.HalfWidth);
    }

    [Fact]
    public void Scheduled_facts_fire_only_when_the_tick_reaches_their_delay()
    {
        var world = new World(seed: 6, valueScale: 1);
        var entity = world.Add(new DataEntity([], [], ("score", 0)));
        var engine = new RuleEngine(new CompiledRuleSet([
            new CompiledRule("start", "start", 0, [], [RuleEffect.Schedule("tick.later", 2)]),
            new CompiledRule("later", "tick.later", 0, [],
                [RuleEffect.AdjustProperty(RuleSubject.Target, "score", 1)])
        ]));

        engine.Dispatch(world, new SimulationFact("start", entity.Id, entity.Id));
        engine.DispatchDue(world);
        Assert.Equal(0, world.EffectiveProperty(entity, "score"));

        world.LogicalTick = 2;
        Assert.Equal(1, engine.DispatchDue(world));
        Assert.Equal(1, world.EffectiveProperty(entity, "score"));

        Assert.Equal(0, engine.DispatchDue(world));
    }

    [Fact]
    public void Action_effects_invoke_the_registered_handler_with_resolved_entities()
    {
        var world = new World(seed: 7, valueScale: 1);
        var source = world.Add(new DataEntity(["actor"], []));
        var target = world.Add(new DataEntity(["node"], [], ("struck", 0)));
        Entity? seenSource = null;
        Entity? seenTarget = null;
        var rules = new CompiledRuleSet([
            new CompiledRule("strike", "strike", 0, [],
                [RuleEffect.InvokeAction("strike", RuleSubject.Source)])
        ])
        {
            Actions = new Dictionary<string, RuleActionHandler>
            {
                ["strike"] = (in RuleActionContext context) =>
                {
                    seenSource = context.Source;
                    seenTarget = context.Target;
                    context.Target!.TrySetProperty(context.World, "struck", 1);
                }
            }.ToImmutableDictionary()
        };

        var result = new RuleEngine(rules).Dispatch(world, new SimulationFact("strike", source.Id, target.Id));

        Assert.False(result.CutOff);
        Assert.Same(source, seenSource);
        Assert.Same(target, seenTarget);
        Assert.Equal(1, world.EffectiveProperty(target, "struck"));
    }

    [Fact]
    public void Rolled_back_dispatch_does_not_leave_a_spawned_entity()
    {
        var world = new World(seed: 8, valueScale: 1);
        var source = world.Add(new DataEntity([], [], ("heat", double.MaxValue)));
        var template = new SpawnTemplate("seed.spark", ["spark"], [],
            ImmutableDictionary<string, double>.Empty);
        var rules = new CompiledRuleSet([
            new CompiledRule("grow", "grow", 0, [],
            [
                RuleEffect.Spawn(RuleSubject.Source, template),
                RuleEffect.AdjustProperty(RuleSubject.Source, "heat", double.MaxValue),
            ])
        ])
        { Templates = new Dictionary<string, SpawnTemplate> { [template.Id] = template }.ToImmutableDictionary() };

        var result = new RuleEngine(rules).Dispatch(world, new SimulationFact("grow", source.Id, source.Id));

        Assert.True(result.RolledBack);
        Assert.Equal("non_finite_property", result.CutoffReason);
        Assert.Same(source, Assert.Single(world.Entities));
        Assert.Equal(double.MaxValue, world.EffectiveProperty(source, "heat"));
    }

    [Fact]
    public void Content_rejects_a_spawn_that_names_an_unknown_template()
    {
        using var pack = new EffectPack(rules: """
            {
              "schemaVersion": 2,
              "rules": [
                { "id": "combine", "on": "contact", "priority": 0,
                  "effects": [ { "kind": "spawn", "subject": "source", "template": "fruit.missing" } ] }
              ]
            }
            """);

        var error = Assert.Throws<ContentPackException>(() => pack.Load());

        Assert.Contains("fruit.missing", error.Message);
        Assert.Contains("$.rules[0].effects[0].template", error.Message);
    }

    [Fact]
    public void Content_compiles_every_effect_primitive()
    {
        using var pack = new EffectPack(rules: """
            {
              "schemaVersion": 2,
              "rules": [
                { "id": "all", "on": "contact", "priority": 0,
                  "effects": [
                    { "kind": "adjustProperty", "subject": "target", "property": "ore", "amount": 2, "quantity": "multiply" },
                    { "kind": "removeTag", "subject": "target", "tag": "boosted" },
                    { "kind": "transfer", "subject": "source", "property": "ore", "targetProperty": "ore", "amount": 1 },
                    { "kind": "spawn", "subject": "source", "template": "fruit.tier2" },
                    { "kind": "schedule", "event": "contact", "delayTicks": 3 },
                    { "kind": "action", "subject": "source", "action": "strike" },
                    { "kind": "despawn", "subject": "source" }
                  ] }
              ]
            }
            """);

        var rules = pack.Load().Rules.Rules.Single();

        Assert.Equal(7, rules.Effects.Length);
        Assert.Equal(QuantityOperation.Multiply, rules.Effects[0].Quantity);
        Assert.Equal("boosted", rules.Effects[1].Name);
        Assert.Equal(RuleSubject.Target, rules.Effects[2].Destination);
        Assert.Equal("fruit.tier2", rules.Effects[3].Template!.Id);
        Assert.Equal(3, rules.Effects[4].DelayTicks);
        Assert.Equal("strike", rules.Effects[5].ActionName);
        Assert.Equal(RuleEffectKind.Despawn, rules.Effects[6].Kind);
        Assert.True(pack.Load().Rules.Templates.ContainsKey("fruit.tier2"));
    }
}

internal sealed class EffectPack(string rules) : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("systemic-effects-").FullName;

    public ContentPack Load()
    {
        File.WriteAllText(Path.Combine(Root, "game.json"), """
            {
              "schemaVersion": 3,
              "id": "sample.effects",
              "name": "Effects",
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
                { "id": "fruit.tier2", "kind": "product", "tags": ["fruit"],
                  "properties": { "tier": 2 }, "bounds": { "x": 0, "y": 0, "halfWidth": 2, "halfHeight": 2 } }
              ]
            }
            """);
        File.WriteAllText(Path.Combine(Root, "rules.json"), rules);
        return ContentPackLoader.Load(Root);
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
