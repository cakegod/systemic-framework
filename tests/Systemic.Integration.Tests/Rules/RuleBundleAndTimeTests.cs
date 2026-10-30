using Systemic.Content;
using Systemic.Kernel;
using Systemic.Rules;

namespace Systemic.Tests;

public sealed class RuleBundleAndTimeTests
{
    [Fact]
    public void ABundleRunsOnlyWhileItsTagIsPresent()
    {
        using var workspace = new RuleJsonWorkspace(
            rules: """
                {
                  "schemaVersion": 2,
                  "bundles": [ { "id": "talent.hiking", "enabledByTag": "talent.hiking" } ],
                  "rules": [
                    { "id": "talent.hiking.boost", "on": "talent.tick", "bundle": "talent.hiking",
                      "priority": 0,
                      "effects": [ { "kind": "adjustProperty", "subject": "target",
                                     "property": "speed", "amount": 1 } ] }
                  ]
                }
                """,
            entities: """
                {
                  "schemaVersion": 2,
                  "entities": [
                    { "id": "talent.hiking", "kind": "prop", "tags": ["talent.hiking"] },
                    { "id": "actor.walker", "kind": "actor", "properties": { "speed": 0 } }
                  ]
                }
                """);
        var content = workspace.Load();
        var materialized = content.CreateWorld(seed: 1, valueScale: 1);
        var world = materialized.World;
        var engine = new RuleEngine(content.Rules);
        var talent = world.ById(materialized.EntityIds["talent.hiking"])!;
        var walker = world.ById(materialized.EntityIds["actor.walker"])!;

        engine.Dispatch(world, new SimulationFact("talent.tick", talent.Id, walker.Id));
        Assert.Equal(1, world.EffectiveProperty(walker, "speed"));

        talent.RemoveTag("talent.hiking");
        engine.Dispatch(world, new SimulationFact("talent.tick", talent.Id, walker.Id));
        Assert.Equal(1, world.EffectiveProperty(walker, "speed"));
    }

    [Fact]
    public void AnExpiredTimedTagDisablesItsBundle()
    {
        using var workspace = new RuleJsonWorkspace(
            rules: """
                {
                  "schemaVersion": 2,
                  "bundles": [ { "id": "status.haste", "enabledByTag": "status.haste" } ],
                  "rules": [
                    { "id": "status.haste.tick", "on": "status.tick", "bundle": "status.haste",
                      "priority": 0,
                      "effects": [ { "kind": "adjustProperty", "subject": "target",
                                     "property": "speed", "amount": 1 } ] }
                  ]
                }
                """,
            entities: """
                {
                  "schemaVersion": 2,
                  "entities": [
                    { "id": "actor.walker", "kind": "actor", "properties": { "speed": 0 } }
                  ]
                }
                """);
        var content = workspace.Load();
        var materialized = content.CreateWorld(seed: 2, valueScale: 1);
        var world = materialized.World;
        world.LogicalTick = 1;
        var engine = new RuleEngine(content.Rules);
        var walker = world.ById(materialized.EntityIds["actor.walker"])!;
        world.Grant("status.haste.source", walker, GrantKind.Tag, "status.haste", remainingTicks: 1);

        engine.Dispatch(world, new SimulationFact("status.tick", walker.Id, walker.Id));
        Assert.Equal(1, world.EffectiveProperty(walker, "speed"));

        world.LogicalTick = 2;
        world.AdvanceModifiers();
        engine.Dispatch(world, new SimulationFact("status.tick", walker.Id, walker.Id));
        Assert.Equal(1, world.EffectiveProperty(walker, "speed"));
    }

    [Fact]
    public void ACooldownBlocksEarlyMatchesAndAllowsTheNextWindow()
    {
        using var workspace = new RuleJsonWorkspace(
            rules: """
                {
                  "schemaVersion": 2,
                  "rules": [
                    { "id": "pulse", "on": "pulse", "priority": 0, "cooldownTicks": 5,
                      "effects": [ { "kind": "adjustProperty", "subject": "target",
                                     "property": "score", "amount": 1 } ] }
                  ]
                }
                """,
            entities: """
                {
                  "schemaVersion": 2,
                  "entities": [ { "id": "actor.pulse", "kind": "actor", "properties": { "score": 0 } } ]
                }
                """);
        var content = workspace.Load();
        var materialized = content.CreateWorld(seed: 3, valueScale: 1);
        var world = materialized.World;
        var engine = new RuleEngine(content.Rules);
        var pulse = world.ById(materialized.EntityIds["actor.pulse"])!;

        engine.Dispatch(world, new SimulationFact("pulse", pulse.Id, pulse.Id));
        Assert.Equal(1, world.EffectiveProperty(pulse, "score"));

        world.LogicalTick = 1;
        engine.Dispatch(world, new SimulationFact("pulse", pulse.Id, pulse.Id));
        Assert.Equal(1, world.EffectiveProperty(pulse, "score"));

        world.LogicalTick = 5;
        engine.Dispatch(world, new SimulationFact("pulse", pulse.Id, pulse.Id));
        Assert.Equal(2, world.EffectiveProperty(pulse, "score"));
    }

    [Fact]
    public void EightHoursOfFastForwardMatchesSteppingEveryTick()
    {
        const int ticksPerStep = 60;
        const int eightHoursAtSixtyHertz = 60 * 60 * 8 * 60;
        const int expectedSteps = eightHoursAtSixtyHertz / ticksPerStep;

        var online = CreateDelayedChain();
        online.Engine.Dispatch(online.World, new SimulationFact("start", online.Score.Id, online.Score.Id));
        for (var tick = 1; tick <= eightHoursAtSixtyHertz; tick++)
        {
            online.World.LogicalTick = tick;
            online.Engine.DispatchDue(online.World);
        }

        var fastForward = CreateDelayedChain();
        fastForward.Engine.Dispatch(fastForward.World,
            new SimulationFact("start", fastForward.Score.Id, fastForward.Score.Id));
        fastForward.World.LogicalTick = eightHoursAtSixtyHertz;
        fastForward.Engine.DispatchDue(fastForward.World);

        Assert.Equal(expectedSteps, online.World.EffectiveProperty(online.Score, "score"));
        Assert.Equal(expectedSteps, fastForward.World.EffectiveProperty(fastForward.Score, "score"));
    }

    [Fact]
    public void AFactWithoutALiveTargetCanScheduleDelayedWork()
    {
        var world = new World(seed: 5, valueScale: 1);
        var actor = world.Add(new DataEntity([], [], ("score", 0)));
        var engine = new RuleEngine(new CompiledRuleSet([
            new CompiledRule("start", "start", 0, [], [RuleEffect.Schedule("step", 2)]),
            new CompiledRule("step", "step", 0, [],
                [RuleEffect.AdjustProperty(RuleSubject.Source, "score", 1)])
        ]), maxEffects: 8, maxFacts: 8);

        // The fact names a live source and no live target, which is how a global event looks.
        engine.Dispatch(world, new SimulationFact("start", actor.Id, actor.Id + 1000));
        world.LogicalTick = 2;

        Assert.Equal(1, engine.DispatchDue(world));
        Assert.Equal(1, world.EffectiveProperty(actor, "score"));
    }

    [Fact]
    public void FastForwardExpiresATimedBonusBeforeALaterDueFact()
    {
        var world = new World(seed: 6, valueScale: 1);
        var actor = world.Add(new DataEntity([], [], ("score", 0), ("speed", 0)));
        var engine = new RuleEngine(new CompiledRuleSet([
            new CompiledRule("start", "start", 0, [], [RuleEffect.Schedule("step", 10)]),
            new CompiledRule("step", "step", 0,
                [RuleCondition.FromExpression(ExpressionCompiler.Compile("target.speed >= 2"))],
                [RuleEffect.AdjustProperty(RuleSubject.Target, "score", 1)])
        ]), maxEffects: 8, maxFacts: 8);

        world.LogicalTick = 1;
        world.SetModifier("bonus", actor, "speed", ModifierOperation.Add, 2, remainingTicks: 3);
        engine.Dispatch(world, new SimulationFact("start", actor.Id, actor.Id));

        // Stepping every tick would have removed the bonus at tick 4, long before the fact due at 11.
        world.LogicalTick = 11;
        engine.DispatchDue(world);

        Assert.Equal(0, world.EffectiveProperty(actor, "score"));
    }

    [Fact]
    public void AFragmentCarriesTheBundleAndScheduleItsRulesNeed()
    {
        using var workspace = new RuleJsonWorkspace(
            rules: """
                {
                  "schemaVersion": 2,
                  "rules": [],
                  "fragments": [
                    {
                      "id": "pattern.producer",
                      "schedules": [{ "id": "pattern.producer.pulse", "event": "produce", "everyTicks": 2,
                        "targetEntityId": "actor.machine" }],
                      "bundles": [{ "id": "pattern.producer.boost", "enabledByTag": "pattern.producer.boost" }],
                      "rules": [
                        { "id": "pattern.producer.produces", "on": "produce", "priority": 0,
                          "effects": [{ "kind": "adjustProperty", "subject": "target", "property": "score", "amount": 1 }] },
                        { "id": "pattern.producer.boosted", "on": "produce", "priority": 1,
                          "bundle": "pattern.producer.boost",
                          "effects": [{ "kind": "adjustProperty", "subject": "target", "property": "score", "amount": 2 }] }
                      ]
                    }
                  ],
                  "useFragments": ["pattern.producer"]
                }
                """,
            entities: """
                {
                  "schemaVersion": 2,
                  "entities": [ { "id": "actor.machine", "kind": "prop", "tags": ["pattern.producer.boost"],
                    "properties": { "score": 0 } } ]
                }
                """);
        var content = workspace.Load();

        // Copying the fragment alone keeps the pulse and the bundle that its rules depend on.
        Assert.Single(content.Rules.Schedules);
        Assert.Equal("pattern.producer.boost", content.Rules.Bundles["pattern.producer.boost"]);

        var materialized = content.CreateWorld(seed: 7, valueScale: 1);
        var world = materialized.World;
        var machine = world.ById(materialized.EntityIds["actor.machine"])!;
        var engine = new RuleEngine(content.Rules);
        machine.RemoveTag("pattern.producer.boost");

        engine.Dispatch(world, new SimulationFact("produce", machine.Id, machine.Id));
        Assert.Equal(1, world.EffectiveProperty(machine, "score"));

        machine.AddTag("pattern.producer.boost");
        engine.Dispatch(world, new SimulationFact("produce", machine.Id, machine.Id));
        Assert.Equal(4, world.EffectiveProperty(machine, "score"));
    }

    [Fact]
    public void ATransferShareMovesWholeBasisPointsAndKeepsTheRest()
    {
        using var workspace = new RuleJsonWorkspace(
            rules: """
                {
                  "schemaVersion": 2,
                  "rules": [
                    { "id": "bank.deposit", "on": "bank.deposit", "priority": 0,
                      "effects": [{ "kind": "transfer", "subject": "source", "property": "earned",
                        "targetProperty": "shards", "basisPoints": 2900 }] }
                  ]
                }
                """,
            entities: """
                {
                  "schemaVersion": 2,
                  "entities": [
                    { "id": "bank.run", "kind": "prop", "properties": { "earned": 100 } },
                    { "id": "bank.meta", "kind": "prop", "properties": { "shards": 0 } }
                  ]
                }
                """);
        var content = workspace.Load();
        var materialized = content.CreateWorld(seed: 8, valueScale: 1);
        var world = materialized.World;
        var run = world.ById(materialized.EntityIds["bank.run"])!;
        var meta = world.ById(materialized.EntityIds["bank.meta"])!;
        var engine = new RuleEngine(content.Rules);

        engine.Dispatch(world, new SimulationFact("bank.deposit", run.Id, meta.Id));

        // 2,900 basis points of 100 is 29 whole shards. The same share as the binary fraction 0.29
        // would round down to 28, which is why the primitive takes an integer share.
        Assert.Equal(29, world.EffectiveProperty(meta, "shards"));
        Assert.Equal(71, world.EffectiveProperty(run, "earned"));
    }

    [Theory]
    [InlineData("""{ "kind": "transfer", "subject": "source", "property": "earned", "targetProperty": "shards", "basisPoints": 0 }""")]
    [InlineData("""{ "kind": "transfer", "subject": "source", "property": "earned", "targetProperty": "shards", "basisPoints": 10001 }""")]
    [InlineData("""{ "kind": "transfer", "subject": "source", "property": "earned", "targetProperty": "shards", "amount": 1, "basisPoints": 100 }""")]
    public void ATransferShareOutsideTheContractIsRejectedAtLoad(string effect)
    {
        using var workspace = new RuleJsonWorkspace(
            rules: $$"""
                {
                  "schemaVersion": 2,
                  "rules": [
                    { "id": "bank.deposit", "on": "bank.deposit", "priority": 0,
                      "effects": [{{effect}}] }
                  ]
                }
                """,
            entities: """
                {
                  "schemaVersion": 2,
                  "entities": [
                    { "id": "bank.run", "kind": "prop", "properties": { "earned": 100 } },
                    { "id": "bank.meta", "kind": "prop", "properties": { "shards": 0 } }
                  ]
                }
                """);

        var error = Assert.Throws<ContentPackException>(() => workspace.Load());

        Assert.Contains("effects[0]", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFragmentCannotUseAnotherFragmentsBundle()
    {
        using var workspace = new RuleJsonWorkspace(
            rules: """
                {
                  "schemaVersion": 2,
                  "rules": [],
                  "fragments": [
                    { "id": "pattern.one",
                      "bundles": [{ "id": "pattern.one.boost", "enabledByTag": "pattern.one.boost" }],
                      "rules": [] },
                    { "id": "pattern.two", "rules": [
                      { "id": "borrow", "on": "pulse", "priority": 0, "bundle": "pattern.one.boost",
                        "effects": [{ "kind": "adjustProperty", "subject": "target",
                                       "property": "score", "amount": 1 }] }
                    ] }
                  ],
                  "useFragments": ["pattern.one", "pattern.two"]
                }
                """,
            entities: """
                {
                  "schemaVersion": 2,
                  "entities": [{ "id": "actor.one", "kind": "actor", "properties": { "score": 0 } }]
                }
                """);

        var error = Assert.Throws<ContentPackException>(() => workspace.Load());

        Assert.Contains("does not declare", error.Message, StringComparison.Ordinal);
    }

    private static (World World, RuleEngine Engine, Entity Score) CreateDelayedChain()
    {
        var world = new World(seed: 4, valueScale: 1);
        var score = world.Add(new DataEntity([], [], ("score", 0)));
        var engine = new RuleEngine(new CompiledRuleSet([
            new CompiledRule("start", "start", 0, [], [RuleEffect.Schedule("step", 60)]),
            new CompiledRule("step", "step", 0,
                [RuleCondition.FromExpression(ExpressionCompiler.Compile("target.score < 28800"))],
                [
                    RuleEffect.AdjustProperty(RuleSubject.Target, "score", 1),
                    RuleEffect.Schedule("step", 60),
                ])
        ]), maxEffects: 64, maxFacts: 64);

        return (world, engine, score);
    }
}

internal sealed class RuleJsonWorkspace(string rules, string entities) : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("systemic-rules-").FullName;

    public ContentPack Load()
    {
        File.WriteAllText(Path.Combine(Root, "game.json"), """
            {
              "schemaVersion": 3,
              "id": "sample.rules",
              "name": "Rules",
              "tickRateHz": 60,
              "maxCatchUpSteps": 4,
              "rules": "rules.json",
              "entities": "entities.json"
            }
            """);
        File.WriteAllText(Path.Combine(Root, "entities.json"), entities);
        File.WriteAllText(Path.Combine(Root, "rules.json"), rules);
        return ContentPackLoader.Load(Root);
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
