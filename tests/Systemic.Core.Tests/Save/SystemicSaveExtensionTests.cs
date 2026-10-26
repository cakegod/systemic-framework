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
    public void Version_two_presentation_state_migrates_to_opaque_extension_state()
    {
        using var pack = CoreOnlyPack.Create();
        var content = ContentPackLoader.Load(pack.Root);
        var game = new SystemicGameBuilder(content, content.CreateWorld(seed: 3, valueScale: 1)).Build();
        var codec = new SystemicSaveCodec();
        var root = JsonNode.Parse(codec.Save(game))!.AsObject();
        root["schemaVersion"] = 2;
        root["presentation"] = new JsonObject
        {
            ["visualRootSeed"] = 23,
            ["nextActorVisualId"] = 17,
            ["nextCueSeedOrdinal"] = 9
        };
        root.Remove("extensionStates");

        var materialized = codec.Load(content, root.ToJsonString());

        var migrated = materialized.ExtensionStates!["systemic.presentation.identity"];
        Assert.Equal(23, migrated.GetProperty("visualRootSeed"u8).GetInt64());
        Assert.Equal(17, migrated.GetProperty("nextActorVisualId"u8).GetInt64());
        Assert.Equal(9, migrated.GetProperty("nextCueSeedOrdinal"u8).GetInt64());
    }

    [Fact]
    public void Version_two_migration_rejects_present_non_object_extension_state()
    {
        using var pack = CoreOnlyPack.Create();
        var content = ContentPackLoader.Load(pack.Root);
        var game = new SystemicGameBuilder(content, content.CreateWorld(seed: 3, valueScale: 1)).Build();
        var codec = new SystemicSaveCodec();
        var root = JsonNode.Parse(codec.Save(game))!.AsObject();
        root["schemaVersion"] = 2;
        root["presentation"] = new JsonObject { ["visualRootSeed"] = 23 };
        root["extensionStates"] = "this value must not be discarded";

        var exception = Assert.Throws<SystemicSaveException>(() => codec.Load(content, root.ToJsonString()));

        Assert.Contains("extensionStates", exception.Message, StringComparison.Ordinal);
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

        Assert.Equal(3, JsonNode.Parse(saved)!["schemaVersion"]!.GetValue<int>());
        Assert.Equal(42, JsonNode.Parse(saved)!["extensionStates"]!["test.counter"]!.GetValue<int>());

        var materialized = codec.Load(content, saved);
        var restoredCounter = new StatefulCounter { Value = -1 };
        _ = new SystemicGameBuilder(content, materialized).AddService(restoredCounter).Build();

        Assert.Equal(42, restoredCounter.Value);
    }

    private sealed class StatefulCounter : ISystemicStatefulExtension
    {
        public string Id => "test.counter";
        public int Value { get; set; }
        public JsonElement CaptureState() => JsonSerializer.SerializeToElement(Value);
        public void RestoreState(JsonElement state) => Value = state.GetInt32();
    }
}
