using System.Text.Json;
using System.Text.Json.Nodes;
using Systemic.Kernel;
using Systemic.Content;
using Systemic.Host;
using Systemic.Save;
using Systemic.Tests.Content;

namespace Systemic.Tests.Save;

public sealed class SaveSchemaContractTests
{
    [Fact]
    public void Save_schema_matches_the_current_v5_codec_contract()
    {
        using var pack = CoreOnlyPack.Create();
        var content = ContentPackLoader.Load(pack.Root);
        var world = content.CreateWorld(seed: 9, valueScale: 1).World;
        world.SetSpatialBounds(world.ById(1)!, new Bounds2(2, 3, 4, 5));
        world.SetModifier("test.foundation", world.ById(1)!, "health", ModifierOperation.Add,
            value: 2, remainingTicks: 3, priority: 4, stage: ModifierStage.Foundation);
        var game = new SystemicGameBuilder(content, world).Build();
        using var saved = JsonDocument.Parse(new SystemicSaveCodec().Save(game));
        using var schema = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "schemas", "core", "save.schema.json")));
        using var embeddedSchemaStream = typeof(SystemicSaveCodec).Assembly.GetManifestResourceStream(
            "Systemic.Core.Schemas.save.schema.json")!;
        using var embeddedSchemaReader = new StreamReader(embeddedSchemaStream);
        using var embeddedSchema = JsonDocument.Parse(embeddedSchemaReader.ReadToEnd());
        Assert.Equal(schema.RootElement.GetRawText(), embeddedSchema.RootElement.GetRawText());
        Assert.Single(new SystemicSaveCodec().Load(content, saved.RootElement.GetRawText()).World.Entities);

        var schemaRoot = schema.RootElement;
        Assert.Equal(5, schemaRoot.GetProperty("properties"u8).GetProperty("schemaVersion"u8).GetProperty("const"u8).GetInt32());
        var required = schemaRoot.GetProperty("required"u8).EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Contains("extensionStates", required);
        Assert.DoesNotContain("presentation", required);
        Assert.False(schemaRoot.GetProperty("properties"u8).TryGetProperty("presentation"u8, out _));
        Assert.True(schemaRoot.GetProperty("properties"u8).TryGetProperty("extensionStates"u8, out _));

        var modifierSchema = schemaRoot.GetProperty("$defs"u8).GetProperty("modifier"u8);
        var modifierRequired = modifierSchema.GetProperty("required"u8).EnumerateArray()
            .Select(value => value.GetString()).ToArray();
        Assert.Contains("stage", modifierRequired);
        Assert.Contains("Foundation", modifierSchema.GetProperty("properties"u8).GetProperty("stage"u8)
            .GetProperty("enum"u8).EnumerateArray().Select(value => value.GetString()));
        var boundsRequired = schemaRoot.GetProperty("$defs"u8).GetProperty("bounds"u8).GetProperty("required"u8)
            .EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Contains("left", boundsRequired);
        Assert.Contains("isValid", boundsRequired);

        var savedRoot = saved.RootElement;
        Assert.Equal(5, savedRoot.GetProperty("schemaVersion"u8).GetInt32());
        Assert.True(savedRoot.TryGetProperty("extensionStates"u8, out _));
        Assert.False(savedRoot.TryGetProperty("presentation"u8, out _));
        Assert.Equal("Foundation", savedRoot.GetProperty("world"u8).GetProperty("modifiers"u8)[0]
            .GetProperty("stage"u8).GetString());
    }

    [Fact]
    public void Current_save_is_schema_validated_after_checksum_verification()
    {
        using var pack = CoreOnlyPack.Create();
        var content = ContentPackLoader.Load(pack.Root);
        var game = new SystemicGameBuilder(content, content.CreateWorld(seed: 12, valueScale: 1).World).Build();
        var save = JsonNode.Parse(new SystemicSaveCodec().Save(game))!.AsObject();
        save["world"]!["valueScale"] = -1;

        var error = Assert.Throws<SystemicSaveException>(() => new SystemicSaveCodec().Load(content, SaveTestDocument.Seal(save)));

        Assert.Contains("content.schema_invalid", error.Message, StringComparison.Ordinal);
        Assert.Contains("$.world.valueScale", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_semantic_validation_rejects_even_random_stream_increments()
    {
        using var pack = CoreOnlyPack.Create();
        var content = ContentPackLoader.Load(pack.Root);
        var world = content.CreateWorld(seed: 12, valueScale: 1).World;
        var game = new SystemicGameBuilder(content, world).Build();
        var save = JsonNode.Parse(new SystemicSaveCodec().Save(game))!.AsObject();
        save["world"]!["draws"]!["increment"] = 2;

        var error = Assert.Throws<SystemicSaveException>(() => new SystemicSaveCodec().Load(content, SaveTestDocument.Seal(save)));

        Assert.Contains("random stream state", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_semantic_validation_requires_next_entity_id_above_saved_ids()
    {
        using var pack = CoreOnlyPack.Create();
        var content = ContentPackLoader.Load(pack.Root);
        var game = new SystemicGameBuilder(content, content.CreateWorld(seed: 12, valueScale: 1).World).Build();
        var save = JsonNode.Parse(new SystemicSaveCodec().Save(game))!.AsObject();
        save["world"]!["nextEntityId"] = 1;

        var error = Assert.Throws<SystemicSaveException>(() => new SystemicSaveCodec().Load(content, SaveTestDocument.Seal(save)));

        Assert.Contains("Next entity ID must be greater", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_semantic_validation_rejects_invalid_spatial_bounds()
    {
        using var pack = CoreOnlyPack.Create();
        var content = ContentPackLoader.Load(pack.Root);
        var world = content.CreateWorld(seed: 12, valueScale: 1).World;
        world.SetSpatialBounds(world.ById(1)!, new Bounds2(2, 3, 4, 5));
        var game = new SystemicGameBuilder(content, world).Build();
        var save = JsonNode.Parse(new SystemicSaveCodec().Save(game))!.AsObject();
        var bounds = save["world"]!["entities"]![0]!["spatialBounds"]!;
        bounds["x"] = 3e38;
        bounds["halfWidth"] = 3e38;
        bounds["left"] = 0;
        bounds["right"] = 6e38;

        var error = Assert.Throws<SystemicSaveException>(() => new SystemicSaveCodec().Load(content, SaveTestDocument.Seal(save)));

        Assert.Contains("invalid spatial bounds", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_semantic_validation_rejects_whitespace_entity_tags()
    {
        using var pack = CoreOnlyPack.Create();
        var content = ContentPackLoader.Load(pack.Root);
        var game = new SystemicGameBuilder(content, content.CreateWorld(seed: 12, valueScale: 1).World).Build();
        var save = JsonNode.Parse(new SystemicSaveCodec().Save(game))!.AsObject();
        save["world"]!["entities"]![0]!["tags"] = new JsonArray(" ");

        var error = Assert.Throws<SystemicSaveException>(() => new SystemicSaveCodec().Load(content, SaveTestDocument.Seal(save)));

        Assert.Contains("invalid or duplicate entity record", error.Message, StringComparison.Ordinal);
    }
}
