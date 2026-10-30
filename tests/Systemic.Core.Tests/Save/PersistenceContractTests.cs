using System.Text.Json.Nodes;
using System.Text.Json;
using Systemic.Content;
using Systemic.Host;
using Systemic.Save;
using Systemic.Tests.Content;

namespace Systemic.Tests.Save;

public sealed class PersistenceContractTests
{
    [Fact]
    public void RoundTripRestoresOriginalSeedAndPartialClockPhase()
    {
        using var pack = CoreOnlyPack.Create();
        var content = ContentPackLoader.Load(pack.Root);
        using var original = new SystemicGameBuilder(content, content.CreateWorld(seed: -42, valueScale: 1)).Build();
        Assert.Equal(0, original.Advance(TimeSpan.FromTicks(166_666)).Steps);
        var codec = new SystemicSaveCodec();

        using var restored = new SystemicGameBuilder(content, codec.Load(content, codec.Save(original))).Build();

        Assert.Equal(-42, restored.World.Seed);
        Assert.Equal(original.Clock.CaptureState(), restored.Clock.CaptureState());
        Assert.Equal(1, original.Advance(TimeSpan.FromTicks(166_668)).Steps);
        Assert.Equal(1, restored.Advance(TimeSpan.FromTicks(166_668)).Steps);
        Assert.Equal(original.Clock.CaptureState(), restored.Clock.CaptureState());
        Assert.Equal(original.World.LogicalTick, restored.World.LogicalTick);
    }

    [Fact]
    public void TamperedSaveIsRejectedWithChecksumDiagnostic()
    {
        using var pack = CoreOnlyPack.Create();
        var content = ContentPackLoader.Load(pack.Root);
        using var game = new SystemicGameBuilder(content, content.CreateWorld(seed: 4, valueScale: 1)).Build();
        var codec = new SystemicSaveCodec();
        var root = JsonNode.Parse(codec.Save(game))!.AsObject();
        root["world"]!["valueScale"] = 2;

        var error = Assert.Throws<SystemicSaveException>(() => codec.Load(content, root.ToJsonString()));

        Assert.Contains("checksum", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, game.World.ValueScale);
    }

    [Fact]
    public void ContentFingerprintUsesLoadedBytesAndIgnoresUnrelatedFiles()
    {
        using var pack = CoreOnlyPack.Create();
        var content = ContentPackLoader.Load(pack.Root);
        using var game = new SystemicGameBuilder(content, content.CreateWorld(seed: 4, valueScale: 1)).Build();
        File.WriteAllText(Path.Combine(pack.Root, "unrelated.txt"), "irrelevant");
        var rulesPath = Path.Combine(pack.Root, "rules.json");
        File.WriteAllText(rulesPath, File.ReadAllText(rulesPath).Replace("-2", "-3", StringComparison.Ordinal));
        var codec = new SystemicSaveCodec();
        var saved = codec.Save(game);

        Assert.NotNull(codec.Load(content, saved));
        var changedContent = ContentPackLoader.Load(pack.Root);
        var error = Assert.Throws<SystemicSaveException>(() => codec.Load(changedContent, saved));
        Assert.Contains("content", error.Message, StringComparison.OrdinalIgnoreCase);

        File.WriteAllText(rulesPath, File.ReadAllText(rulesPath).Replace("-3", "-2", StringComparison.Ordinal));
        Assert.NotNull(codec.Load(ContentPackLoader.Load(pack.Root), saved));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void OlderSaveVersionsAreRejectedWithActionableDiagnostic(int version)
    {
        using var pack = CoreOnlyPack.Create();
        var content = ContentPackLoader.Load(pack.Root);
        using var game = new SystemicGameBuilder(content, content.CreateWorld(seed: 4, valueScale: 1)).Build();
        var codec = new SystemicSaveCodec();
        var root = JsonNode.Parse(codec.Save(game))!.AsObject();
        root["schemaVersion"] = version;

        var error = Assert.Throws<SystemicSaveException>(() => codec.Load(content, root.ToJsonString()));

        Assert.Contains(version.ToString(System.Globalization.CultureInfo.InvariantCulture), error.Message, StringComparison.Ordinal);
        Assert.Contains($"version {SystemicSaveCodec.CurrentSchemaVersion}", error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SaveChecksumAcceptsReorderedPropertiesButRejectsDuplicates()
    {
        using var pack = CoreOnlyPack.Create();
        var content = ContentPackLoader.Load(pack.Root);
        using var game = new SystemicGameBuilder(content, content.CreateWorld(seed: 8, valueScale: 1)).Build();
        var codec = new SystemicSaveCodec();
        var saved = codec.Save(game);
        var reordered = ReverseProperties(JsonNode.Parse(saved)!).ToJsonString();

        Assert.Equal(8, codec.Load(content, reordered).World.Seed);
        var duplicate = saved.Insert(1, "\"schemaVersion\":4,");
        var error = Assert.Throws<SystemicSaveException>(() => codec.Load(content, duplicate));
        Assert.Contains("duplicate", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SyntheticAndModifiedPacksRequireAnExplicitContentFingerprint()
    {
        using var pack = CoreOnlyPack.Create();
        var loaded = ContentPackLoader.Load(pack.Root);
        var modified = loaded with { Manifest = loaded.Manifest with { Name = "Other" } };
        using var missingIdentity = new SystemicGameBuilder(modified, modified.CreateWorld(2, 1)).Build();
        var codec = new SystemicSaveCodec();
        Assert.Contains("ContentFingerprint", Assert.Throws<SystemicSaveException>(() =>
            codec.Save(missingIdentity)).Message, StringComparison.Ordinal);

        var synthetic = modified with { ContentFingerprint = new string('a', 64) };
        using var game = new SystemicGameBuilder(synthetic, synthetic.CreateWorld(2, 1)).Build();
        var saved = codec.Save(game);
        Assert.Equal(2, codec.Load(synthetic, saved).World.Seed);
        var other = modified with { ContentFingerprint = new string('b', 64) };
        Assert.Contains("content", Assert.Throws<SystemicSaveException>(() => codec.Load(other, saved)).Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClockPhaseLargerThanUint64RoundTripsWithoutLoss()
    {
        using var pack = CoreOnlyPack.Create();
        var content = ContentPackLoader.Load(pack.Root);
        using var game = new SystemicGameBuilder(content, content.CreateWorld(17, 1)).Build();
        var phase = (Int128)ulong.MaxValue + 123;
        game.Clock.RestoreState(new FixedStepClockState(30, 3, phase));
        var codec = new SystemicSaveCodec();

        using var restored = new SystemicGameBuilder(content, codec.Load(content, codec.Save(game))).Build();

        Assert.Equal(phase, restored.Clock.CaptureState().PhaseUnits);
    }

    [Fact]
    public void SavedClockConfigurationMustMatchTheLoadedManifest()
    {
        using var pack = CoreOnlyPack.Create();
        var content = ContentPackLoader.Load(pack.Root);
        using var game = new SystemicGameBuilder(content, content.CreateWorld(17, 1)).Build();
        var codec = new SystemicSaveCodec();
        var root = JsonNode.Parse(codec.Save(game))!.AsObject();
        root["extensionStates"]!["systemic.clock"]!["tickRateHz"] = 60;

        var error = Assert.Throws<SystemicSaveException>(() => codec.Load(content, SaveTestDocument.Seal(root)));

        Assert.Contains("clock", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, game.World.LogicalTick);
    }

    [Fact]
    public void SaveFileReplacesExistingFileAndLoadsTheNewState()
    {
        using var pack = CoreOnlyPack.Create();
        var content = ContentPackLoader.Load(pack.Root);
        using var game = new SystemicGameBuilder(content, content.CreateWorld(-29, 1)).Build();
        var path = Path.Combine(pack.Root, "save.json");
        File.WriteAllText(path, "old save");
        var codec = new SystemicSaveCodec();

        codec.SaveToFile(game, path);

        Assert.Equal(-29, codec.LoadFromFile(content, path).World.Seed);
        Assert.Empty(Directory.EnumerateFiles(pack.Root, ".save.json.*.tmp"));
    }

    [Fact]
    public void FailedReplacementCleansTemporaryFileAndPreservesDestination()
    {
        using var pack = CoreOnlyPack.Create();
        var content = ContentPackLoader.Load(pack.Root);
        using var game = new SystemicGameBuilder(content, content.CreateWorld(2, 1)).Build();
        var destination = Path.Combine(pack.Root, "blocked");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "original"), "keep me");

        Assert.ThrowsAny<IOException>(() => new SystemicSaveCodec().SaveToFile(game, destination));

        Assert.Equal("keep me", File.ReadAllText(Path.Combine(destination, "original")));
        Assert.Empty(Directory.EnumerateFiles(pack.Root, ".blocked.*.tmp"));
    }

    [Fact]
    public void CaptureFailurePreservesAnExistingSaveFile()
    {
        using var pack = CoreOnlyPack.Create();
        var content = ContentPackLoader.Load(pack.Root);
        using var game = new SystemicGameBuilder(content, content.CreateWorld(2, 1))
            .RegisterStatefulExtension(new FailingCapture()).Build();
        var path = Path.Combine(pack.Root, "save.json");
        File.WriteAllText(path, "previous save");

        Assert.Throws<InvalidOperationException>(() => new SystemicSaveCodec().SaveToFile(game, path));

        Assert.Equal("previous save", File.ReadAllText(path));
        Assert.Empty(Directory.EnumerateFiles(pack.Root, ".save.json.*.tmp"));
    }

    [Fact]
    public void LoadFromFileRejectsInvalidUtf8AsASaveError()
    {
        using var pack = CoreOnlyPack.Create();
        var content = ContentPackLoader.Load(pack.Root);
        var path = Path.Combine(pack.Root, "save.json");
        File.WriteAllBytes(path, [0xff]);

        Assert.Throws<SystemicSaveException>(() => new SystemicSaveCodec().LoadFromFile(content, path));
    }

    private sealed class FailingCapture : ISystemicStatefulExtension
    {
        public string Id => "test.failing-capture";
        public JsonElement CaptureState() => throw new InvalidOperationException("Capture failed");
        public void RestoreState(JsonElement state) { }
    }

    private static JsonNode ReverseProperties(JsonNode node) => node switch
    {
        JsonObject obj => new JsonObject(obj.Reverse().Select(pair =>
            new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value is null ? null : ReverseProperties(pair.Value)))),
        JsonArray array => new JsonArray(array.Select(child => child is null ? null : ReverseProperties(child)).ToArray()),
        _ => node.DeepClone()
    };
}
