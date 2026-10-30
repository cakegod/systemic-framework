using System.Text.Json;
using System.Text.Json.Nodes;
using Systemic.Content;
using Systemic.Host;
using Systemic.Save;
using Systemic.Tests.Content;

namespace Systemic.Tests.Save;

public sealed class SystemicSaveExtensionTests
{
    [Fact]
    public void FutureMigrationsRunSequentiallyOnAnIndependentCheckedCopy()
    {
        using var pack = CoreOnlyPack.Create();
        var content = ContentPackLoader.Load(pack.Root);
        var game = new SystemicGameBuilder(content, content.CreateWorld(seed: 3, valueScale: 1)).Build();
        var codec = new SystemicSaveCodec();
        var original = codec.Save(game);
        var migrations = new ISaveMigration[]
        {
            new FutureMigration(5, root => root["extensionStates"]!["test.future"] = 23),
            new FutureMigration(6, root => root["extensionStates"]!["test.future"] = 24),
        };
        _ = new SystemicSaveCodec(migrations: migrations);

        var upgraded = SystemicSaveMigration.Upgrade(original, 7, migrations);

        Assert.Equal(6, JsonNode.Parse(original)!["schemaVersion"]!.GetValue<int>());
        Assert.Equal(7, JsonNode.Parse(upgraded)!["schemaVersion"]!.GetValue<int>());
        Assert.Equal(24, JsonNode.Parse(upgraded)!["extensionStates"]!["test.future"]!.GetValue<int>());
        Assert.Equal(upgraded, SystemicSaveMigration.Upgrade(upgraded, 7, []));
    }

    [Fact]
    public void FutureMigrationsRejectTamperingAndMissingSteps()
    {
        using var pack = CoreOnlyPack.Create();
        var content = ContentPackLoader.Load(pack.Root);
        var game = new SystemicGameBuilder(content, content.CreateWorld(seed: 3, valueScale: 1)).Build();
        var codec = new SystemicSaveCodec();
        var saved = codec.Save(game);
        var root = JsonNode.Parse(saved)!.AsObject();
        root["world"]!["seed"] = 44;

        Assert.Contains("checksum", Assert.Throws<SystemicSaveException>(() =>
            SystemicSaveMigration.Upgrade(root.ToJsonString(), 6,
                [new FutureMigration(5, _ => { }), new FutureMigration(6, _ => { })])).Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("migration", Assert.Throws<SystemicSaveException>(() =>
            SystemicSaveMigration.Upgrade(saved, 7, [new FutureMigration(4, _ => { })])).Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Throws<ArgumentException>(() => new SystemicSaveCodec(migrations: [new FutureMigration(3, _ => { })]));
    }

    [Fact]
    public void NewSavesCaptureAndRestoreRegisteredExtensionStateById()
    {
        using var pack = CoreOnlyPack.Create();
        var content = ContentPackLoader.Load(pack.Root);
        var game = new SystemicGameBuilder(content, content.CreateWorld(seed: 5, valueScale: 1))
            .AddService(new StatefulCounter { Value = 42 })
            .Build();
        var codec = new SystemicSaveCodec();
        var saved = codec.Save(game);

        Assert.Equal(6, JsonNode.Parse(saved)!["schemaVersion"]!.GetValue<int>());
        Assert.Equal(42, JsonNode.Parse(saved)!["extensionStates"]!["test.counter"]!.GetValue<int>());

        var materialized = codec.Load(content, saved);
        var restoredCounter = new StatefulCounter { Value = -1 };
        _ = new SystemicGameBuilder(content, materialized).AddService(restoredCounter).Build();

        Assert.Equal(42, restoredCounter.Value);
    }

    [Fact]
    public void VersionFiveDocumentsDropTheRetiredEventEngineState()
    {
        using var pack = CoreOnlyPack.Create();
        var content = ContentPackLoader.Load(pack.Root);
        using var game = new SystemicGameBuilder(content, content.CreateWorld(seed: 9, valueScale: 1)).Build();
        var root = JsonNode.Parse(new SystemicSaveCodec().Save(game))!.AsObject();
        root["schemaVersion"] = 5;
        root["extensionStates"]!.AsObject()["systemic.events"] = JsonNode.Parse("""{"serial":4,"chainId":2}"""u8);
        var versionFive = SaveTestDocument.Seal(root);

        var materialized = new SystemicSaveCodec().Load(content, versionFive);

        Assert.DoesNotContain("systemic.events", materialized.ExtensionStates!.Keys);
        using var restored = new SystemicGameBuilder(content, materialized).Build();
        Assert.Equal(0, restored.World.LogicalTick);
    }

    private sealed class StatefulCounter : ISystemicStatefulExtension
    {
        public string Id => "test.counter";
        public int Value { get; set; }
        public JsonElement CaptureState() => JsonSerializer.SerializeToElement(Value);
        public void RestoreState(JsonElement state) => Value = state.GetInt32();
    }

    private sealed class FutureMigration(int version, Action<JsonObject> apply) : ISaveMigration
    {
        public int FromVersion => version;
        public void Apply(JsonObject document) => apply(document);
    }
}
