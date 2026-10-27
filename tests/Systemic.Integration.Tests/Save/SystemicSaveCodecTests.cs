using System.Text.Json;
using System.Text.Json.Nodes;
using Systemic.Kernel;
using Systemic.Content;
using Systemic.Extensions;
using Systemic.Host;
using Systemic.Incremental;
using Systemic.Rules;
using Systemic.Save;

namespace Systemic.Tests.Save;

public sealed class SystemicSaveCodecTests
{
    [Fact]
    public void Round_trip_preserves_world_state_random_sequence_incremental_runtime_and_extension_state()
    {
        var content = SystemicTestContent.Load(Path.Combine(AppContext.BaseDirectory, "samples", "incremental-quarry"));
        var initial = content.CreateWorld(seed: 17, valueScale: 1);
        var counter = new StatefulCounter { Value = 23 };
        var game = new SystemicGameBuilder(content, initial).AddExtension(counter).Build();
        var ore = game.World.ById(game.EntityId("quarry.ore"))!;
        var custom = game.World.Add(new CounterEntity(9));
        custom.SetLink("resource", ore.Id);
        game.World.SetSpatialBounds(ore, new Bounds2(10, 20, 2, 3));
        game.World.SetModifier("test.bonus", ore, "balance", ModifierOperation.Add, 4,
            remainingTicks: 12, priority: 2, stage: ModifierStage.Additive);
        game.World.Grant("test.grant", ore, GrantKind.Tag, "energized", remainingTicks: 5);
        game.World.BindRole("test.anchor", custom);
        _ = game.World.Draws.NextFloat();
        game.World.LogicalTick = 14;
        Assert.Equal(0, game.AllocateActorVisualId());

        var codec = new SystemicSaveCodec([new CounterEntityCodec()]);
        var json = codec.Save(game);
        var loaded = codec.Load(content, json);
        var restoredCounter = new StatefulCounter { Value = -1 };
        var restoredGame = new SystemicGameBuilder(content, loaded).AddExtension(restoredCounter).Build();
        var restoredOre = restoredGame.World.ById(restoredGame.EntityId("quarry.ore"))!;
        var restoredCustom = Assert.IsType<CounterEntity>(restoredGame.World.ById(custom.Id));

        Assert.Equal(14, restoredGame.World.LogicalTick);
        Assert.Equal(game.PresentationIdentity(), restoredGame.PresentationIdentity());
        Assert.Equal(23, restoredCounter.Value);
        Assert.Equal(9, restoredCustom.Value);
        Assert.Equal(ore.Id, restoredCustom.Links["resource"]);
        Assert.Equal(custom.Id, restoredGame.World.Role("test.anchor")!.Id);
        Assert.True(restoredGame.World.HasTag(restoredOre, "energized"));
        Assert.Equal(new Bounds2(10, 20, 2, 3), GetBounds(restoredGame.World, restoredOre.Id));
        Assert.Equal(4, restoredGame.World.EffectiveProperty(restoredOre, "balance"));
        var restoredModifier = Assert.Single(restoredGame.World.Modifiers);
        Assert.Equal(2, restoredModifier.Priority);
        Assert.Equal(ModifierStage.Additive, restoredModifier.Stage);
        Assert.Equal(game.World.Draws.NextFloat(), restoredGame.World.Draws.NextFloat());

        game.Step();
        restoredGame.Step();
        Assert.True(ore.TryGetProperty(game.World, "balance", out var expectedBalance));
        Assert.True(restoredOre.TryGetProperty(restoredGame.World, "balance", out var actualBalance));
        Assert.Equal(expectedBalance, actualBalance);
    }

    [Fact]
    public void Version_one_save_is_rejected_with_a_version_4_diagnostic()
    {
        var content = SystemicTestContent.Load(Path.Combine(AppContext.BaseDirectory, "samples", "incremental-quarry"));
        var game = new SystemicGameBuilder(content, content.CreateWorld(seed: 3, valueScale: 1)).Build();
        var codec = new SystemicSaveCodec();
        var root = JsonNode.Parse(codec.Save(game))!.AsObject();
        root["schemaVersion"] = 1;
        root.Remove("presentation");
        root.Remove("extensionStates");

        var error = Assert.Throws<SystemicSaveException>(() => codec.Load(content, root.ToJsonString()));
        Assert.Contains("version 4", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Round_trip_preserves_selected_and_active_build_choice_state()
    {
        var content = SystemicTestContent.Load(Path.Combine(AppContext.BaseDirectory, "samples", "incremental-quarry"));
        var initial = content.CreateWorld(seed: 19, valueScale: 1);
        var game = new SystemicGameBuilder(content, initial).Build();
        var ownerId = game.EntityId("quarry.ember-source");
        var ore = game.World.ById(game.EntityId("quarry.ore"))!;
        ore.TrySetProperty(game.World, "balance", 20);
        game.Step();
        Assert.True(game.SelectChoice("quarry.relay", ownerId).Accepted);
        Assert.True(game.SelectChoice("quarry.overdrive", ownerId).Accepted);
        Assert.True(game.ActivateChoice("quarry.overdrive", ownerId).Accepted);

        var codec = new SystemicSaveCodec();
        var loaded = codec.Load(content, codec.Save(game));
        var restored = new SystemicGameBuilder(content, loaded).Build();

        Assert.True(restored.BuildChoices().TryGetState("quarry.relay", out var relay));
        Assert.True(relay.IsSelected);
        Assert.True(restored.BuildChoices().TryGetState("quarry.overdrive", out var overdrive));
        Assert.True(overdrive.IsSelected);
        Assert.True(overdrive.IsActive);
        Assert.Equal(0, overdrive.CooldownProgress);
        Assert.True(restored.World.HasTag(restored.World.ById(ownerId)!, "quarry.relay"));
        Assert.True(restored.World.HasTag(restored.World.ById(ownerId)!, "quarry.overdrive.active"));

        game.Step();
        restored.Step();
        Assert.True(ore.TryGetProperty(game.World, "balance", out var expected));
        Assert.True(restored.World.ById(restored.EntityId("quarry.ore"))!
            .TryGetProperty(restored.World, "balance", out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Save_round_trip_preserves_bounded_expression_event_history()
    {
        var sourceContent = SystemicTestContent.Load(Path.Combine(AppContext.BaseDirectory, "samples", "incremental-quarry"));
        var rules = new CompiledRuleSet([
            new CompiledRule("history.report", "history.report", 0,
                [RuleCondition.FromExpression(ExpressionCompiler.Compile("event_count('history.observed', 10) > 0"))],
                [RuleEffect.AdjustProperty(RuleSubject.Target, "balance", 1)])
        ]);
        var content = sourceContent with
        {
            Rules = rules,
            ContentFingerprint = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(sourceContent.ContentFingerprint + ":history.report-v1")))
        };
        var host = HeadlessSystemicHost.Create(content, seed: 9);
        var oreId = host.Game.EntityId("quarry.ore");
        host.Publish(new SimulationFact("history.observed", oreId, oreId));

        var restored = HeadlessSystemicHost.Restore(content, host.Save());
        restored.Publish(new SimulationFact("history.report", oreId, oreId));

        var ore = restored.Game.World.ById(oreId)!;
        Assert.True(ore.TryGetProperty(restored.Game.World, "balance", out var balance));
        Assert.Equal(1, balance);
    }

    [Fact]
    public void Stateful_system_extension_is_registered_once_and_restored()
    {
        var content = SystemicTestContent.Load(Path.Combine(AppContext.BaseDirectory, "samples", "incremental-quarry"));
        var extension = new StatefulSystemExtension { Value = 22 };
        var game = new SystemicGameBuilder(content, content.CreateWorld(seed: 33, valueScale: 1))
            .AddExtension(extension)
            .Build();
        game.Step();
        var codec = new SystemicSaveCodec();
        var loaded = codec.Load(content, codec.Save(game));
        var restoredExtension = new StatefulSystemExtension { Value = -1 };
        var restored = new SystemicGameBuilder(content, loaded).AddExtension(restoredExtension).Build();

        Assert.Equal(extension.Value, restoredExtension.Value);
        restored.Step();
        Assert.Equal(extension.Value + 1, restoredExtension.Value);
    }

    [Fact]
    public void Rejects_missing_migration_steps_and_dangling_saved_links()
    {
        var content = SystemicTestContent.Load(Path.Combine(AppContext.BaseDirectory, "samples", "incremental-quarry"));
        var game = new SystemicGameBuilder(content, content.CreateWorld(seed: 3, valueScale: 1)).Build();
        var codec = new SystemicSaveCodec();
        var json = codec.Save(game);
        var unsupported = JsonNode.Parse(json)!.AsObject();
        unsupported["schemaVersion"] = 0;
        Assert.Throws<SystemicSaveException>(() => codec.Load(content, unsupported.ToJsonString()));

        var brokenLink = JsonNode.Parse(json)!.AsObject();
        brokenLink["world"]!["entities"]![0]!["links"]!["missing"] = 99999;
        Assert.Throws<SystemicSaveException>(() => codec.Load(content, brokenLink.ToJsonString()));
    }

    private static Bounds2 GetBounds(World world, int entityId)
    {
        Assert.True(world.TryGetSpatialBounds(entityId, out var bounds));
        return bounds;
    }

    private sealed class StatefulCounter : ISystemicExtensionModule, ISystemicStatefulExtension
    {
        public string Id => "test.counter";
        public int Value { get; set; }
        public void Register(EngineBuilder _) { }
        public JsonElement CaptureState() => JsonSerializer.SerializeToElement(Value);
        public void RestoreState(JsonElement state) => Value = state.GetInt32();
    }

    private sealed class StatefulSystemExtension : ISystemicExtensionModule, ISystemicSystem, ISystemicStatefulExtension
    {
        public string Id => "test.stateful-system";
        public int Order => 10;
        public int Value { get; set; }
        public void Register(EngineBuilder _) { }
        public void Step(SystemicSystemContext context) => Value++;
        public JsonElement CaptureState() => JsonSerializer.SerializeToElement(Value);
        public void RestoreState(JsonElement state) => Value = state.GetInt32();
    }

    private sealed class CounterEntity(int value) : Entity
    {
        public int Value { get; private set; } = value;
        public override Entity Clone()
        {
            var clone = new CounterEntity(Value);
            CopyBaseTo(clone);
            return clone;
        }

        public override void RestoreFrom(Entity snapshot)
        {
            RestoreBaseFrom(snapshot);
            Value = ((CounterEntity)snapshot).Value;
        }
    }

    private sealed class CounterEntityCodec : IEntityStateCodec
    {
        public string Id => "test.counter-entity";
        public bool CanEncode(Entity entity) => entity is CounterEntity;
        public JsonElement Encode(Entity entity) => JsonSerializer.SerializeToElement(((CounterEntity)entity).Value);
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859",
            Justification = "The codec contract requires the Entity return type.")]
        public Entity Decode(JsonElement payload) => new CounterEntity(payload.GetInt32());
    }
}
