using System.Text.Json;
using System.Text.Json.Nodes;
using Systemic.Content;
using Systemic.Extensions;
using Systemic.Host;
using Systemic.Kernel;
using Systemic.Save;
using Systemic.Tests.Content;

namespace Systemic.Tests.Save;

public sealed class SystemicSaveExtensionTests
{
    [Fact]
    public void Future_migrations_run_sequentially_on_an_independent_checked_copy()
    {
        using var pack = CoreOnlyPack.Create();
        var content = ContentPackLoader.Load(pack.Root);
        var game = new SystemicGameBuilder(content, content.CreateWorld(seed: 3, valueScale: 1)).Build();
        var codec = new SystemicSaveCodec();
        var original = codec.Save(game);
        var migrations = new ISaveMigration[]
        {
            new FutureMigration(4, root => root["extensionStates"]!["test.future"] = 23),
            new FutureMigration(5, root => root["extensionStates"]!["test.future"] = 24),
        };
        _ = new SystemicSaveCodec(migrations: migrations);

        var upgraded = SystemicSaveMigration.Upgrade(original, 6, migrations);

        Assert.Equal(4, JsonNode.Parse(original)!["schemaVersion"]!.GetValue<int>());
        Assert.Equal(6, JsonNode.Parse(upgraded)!["schemaVersion"]!.GetValue<int>());
        Assert.Equal(24, JsonNode.Parse(upgraded)!["extensionStates"]!["test.future"]!.GetValue<int>());
        Assert.Equal(upgraded, SystemicSaveMigration.Upgrade(upgraded, 6, []));
    }

    [Fact]
    public void Future_migrations_reject_tampering_and_missing_steps()
    {
        using var pack = CoreOnlyPack.Create();
        var content = ContentPackLoader.Load(pack.Root);
        var game = new SystemicGameBuilder(content, content.CreateWorld(seed: 3, valueScale: 1)).Build();
        var codec = new SystemicSaveCodec();
        var saved = codec.Save(game);
        var root = JsonNode.Parse(saved)!.AsObject();
        root["world"]!["seed"] = 44;

        Assert.Contains("checksum", Assert.Throws<SystemicSaveException>(() =>
            SystemicSaveMigration.Upgrade(root.ToJsonString(), 5, [new FutureMigration(4, _ => { })])).Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("migration", Assert.Throws<SystemicSaveException>(() =>
            SystemicSaveMigration.Upgrade(saved, 6, [new FutureMigration(4, _ => { })])).Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Throws<ArgumentException>(() => new SystemicSaveCodec(migrations: [new FutureMigration(3, _ => { })]));
    }

    [Fact]
    public void New_saves_capture_and_restore_registered_extension_state_by_id()
    {
        using var pack = CoreOnlyPack.Create();
        var content = ContentPackLoader.Load(pack.Root);
        var game = new SystemicGameBuilder(content, content.CreateWorld(seed: 5, valueScale: 1))
            .AddService(new StatefulCounter { Value = 42 })
            .Build();
        var codec = new SystemicSaveCodec();
        var saved = codec.Save(game);

        Assert.Equal(4, JsonNode.Parse(saved)!["schemaVersion"]!.GetValue<int>());
        Assert.Equal(42, JsonNode.Parse(saved)!["extensionStates"]!["test.counter"]!.GetValue<int>());

        var materialized = codec.Load(content, saved);
        var restoredCounter = new StatefulCounter { Value = -1 };
        _ = new SystemicGameBuilder(content, materialized).AddService(restoredCounter).Build();

        Assert.Equal(42, restoredCounter.Value);
    }

    [Fact]
    public void Event_ids_continue_identically_after_save_and_load()
    {
        using var pack = CoreOnlyPack.Create();
        var content = ContentPackLoader.Load(pack.Root);
        var codec = new SystemicSaveCodec();
        var liveModule = new SerialModule();
        using var live = new SystemicGameBuilder(content, content.CreateWorld(seed: 9, valueScale: 1))
            .AddExtension(liveModule).Build();
        live.Execute(new SerialEvent(1));
        live.Execute(new SerialEvent(1));
        Assert.Equal(2, liveModule.LastEventId);

        var restoredModule = new SerialModule();
        using var restored = new SystemicGameBuilder(content, codec.Load(content, codec.Save(live)))
            .AddExtension(restoredModule).Build();

        live.Execute(new SerialEvent(1));
        restored.Execute(new SerialEvent(1));

        Assert.Equal(3, liveModule.LastEventId);
        Assert.Equal(3, restoredModule.LastEventId);
    }

    private sealed record SerialEvent(int SourceId) : KernelEvent(SourceId);

    private sealed class SerialModule : ISystemicExtensionModule
    {
        public string Id => "test.serial-module";
        public long LastEventId { get; private set; }

        public void Register(EngineBuilder engine) => engine.On<SerialEvent>("test.serial-module",
            context => LastEventId = context.Event.EventId);
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
