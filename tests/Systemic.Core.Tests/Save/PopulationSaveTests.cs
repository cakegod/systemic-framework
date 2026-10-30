using System.Numerics;
using System.Text.Json.Nodes;
using Systemic.Kernel;
using Systemic.Content;
using Systemic.Host;
using Systemic.Save;
using Systemic.Tests.Content;

namespace Systemic.Tests.Save;

public sealed class PopulationSaveTests
{
    private const string PopulationExtensionId = "systemic.populations";
    private static readonly PopulationColumnKey<int> s_energyKey =
        new("energy", 1, PopulationCodecs.Int32);
    private static readonly PopulationColumnKey<Vector2> s_positionKey =
        new("position", 1, PopulationCodecs.Vector2);
    private static readonly PopulationColumnKey<float> s_velocityKey =
        new("velocity", 1, PopulationCodecs.Single);
    private static readonly PopulationColumnKey<bool> s_flagKey =
        new("flag", 1, PopulationCodecs.Boolean);

    [Fact]
    public void SaveLoadRoundTripsPopulationColumnsAndHandles()
    {
        using var pack = CoreOnlyPack.Create();
        var content = ContentPackLoader.Load(pack.Root);
        var materializedSource = content.CreateWorld(seed: 71, valueScale: 1);
        var world = materializedSource.World;
        var sourceSetup = new PopulationSetupSystem(world);
        var source = sourceSetup.Particles;
        var rows = source.AddRange(4);
        var energy = sourceSetup.Energy.EditRange(rows.Start, rows.Length);
        energy[0] = 13;
        energy[1] = 29;
        energy[2] = 47;
        energy[3] = 83;
        var positions = sourceSetup.Position.EditRange(rows.Start, rows.Length);
        positions[0] = new Vector2(1, 2);
        positions[1] = new Vector2(3, 5);
        positions[2] = new Vector2(8, 13);
        positions[3] = new Vector2(21, 34);
        var firstHandle = source.GetHandle(0);
        var firstRemovedHandle = source.GetHandle(1);
        var secondRemovedHandle = source.GetHandle(2);
        var movedHandle = source.GetHandle(3);
        source.RemoveSwapBack(1);
        source.RemoveSwapBack(2);
        var codec = new SystemicSaveCodec();

        var save = codec.Save(new SystemicGameBuilder(content, world).AddSystem(sourceSetup).Build());
        var root = JsonNode.Parse(save)!.AsObject();
        var populationState = root["extensionStates"]?[PopulationExtensionId];
        Assert.NotNull(populationState);

        var materialized = codec.Load(content, save);
        var targetSetup = new PopulationSetupSystem(materialized.World);
        _ = new SystemicGameBuilder(content, materialized).AddSystem(targetSetup).Build();

        Assert.Equal([13, 83], targetSetup.Energy.ReadOnlySpan.ToArray());
        Assert.Equal([new Vector2(1, 2), new Vector2(21, 34)],
            targetSetup.Position.ReadOnlySpan.ToArray());
        Assert.Equal(firstHandle.Slot, targetSetup.Particles.GetHandle(0).Slot);
        Assert.Equal(firstHandle.Generation, targetSetup.Particles.GetHandle(0).Generation);
        Assert.Equal(movedHandle.Slot, targetSetup.Particles.GetHandle(1).Slot);
        Assert.Equal(movedHandle.Generation, targetSetup.Particles.GetHandle(1).Generation);
        Assert.False(targetSetup.Particles.TryResolve(firstRemovedHandle, out _));
        Assert.True(targetSetup.Particles.TryGetHandle(movedHandle.Slot, movedHandle.Generation,
            out var restoredMovedHandle));
        Assert.True(targetSetup.Particles.TryResolve(restoredMovedHandle, out var restoredMovedRow));
        Assert.Equal(1, restoredMovedRow);
        Assert.False(targetSetup.Particles.TryGetHandle(firstRemovedHandle.Slot, firstRemovedHandle.Generation,
            out _));

        var restoredRows = targetSetup.Particles.AddRange(2);
        Assert.Equal(2, restoredRows.Start);
        var firstReusedHandle = targetSetup.Particles.GetHandle(2);
        var secondReusedHandle = targetSetup.Particles.GetHandle(3);
        Assert.Equal(secondRemovedHandle.Slot, firstReusedHandle.Slot);
        Assert.Equal(firstRemovedHandle.Slot, secondReusedHandle.Slot);
        Assert.NotEqual(secondRemovedHandle.Generation, firstReusedHandle.Generation);
        Assert.NotEqual(firstRemovedHandle.Generation, secondReusedHandle.Generation);
    }

    [Fact]
    public void SaveIncludesPopulationsDefinedAfterTheHostWasBuilt()
    {
        using var pack = CoreOnlyPack.Create();
        var content = ContentPackLoader.Load(pack.Root);
        using var game = new SystemicGameBuilder(content, content.CreateWorld(77, 1)).Build();
        var source = new PopulationSetupSystem(game.World);
        source.Particles.AddRange(1);
        source.Energy.EditRange(0, 1)[0] = 41;
        var codec = new SystemicSaveCodec();

        var saved = codec.Save(game);
        var materialized = codec.Load(content, saved);
        var restored = new PopulationSetupSystem(materialized.World);
        using var restoredGame = new SystemicGameBuilder(content, materialized).AddSystem(restored).Build();

        Assert.Equal(1, restored.Particles.Count);
        Assert.Equal(41, restored.Energy.ReadOnlySpan[0]);
    }

    [Fact]
    public void SaveRestoresDoubleBufferCurrentAndReinitializesNext()
    {
        using var pack = CoreOnlyPack.Create();
        var content = ContentPackLoader.Load(pack.Root);
        var materializedSource = content.CreateWorld(seed: 72, valueScale: 1);
        var world = materializedSource.World;
        var sourceSetup = new PopulationSetupSystem(world);
        var rows = sourceSetup.Particles.AddRange(3);
        var sourceNext = sourceSetup.Velocity.EditNext(rows.Start, rows.Length);
        sourceNext[0] = 1.25f;
        sourceNext[1] = -2.5f;
        sourceNext[2] = 9;
        sourceSetup.Velocity.Swap();
        var codec = new SystemicSaveCodec();
        var save = codec.Save(new SystemicGameBuilder(content, world).AddSystem(sourceSetup).Build());
        Assert.NotNull(JsonNode.Parse(save)!["extensionStates"]?[PopulationExtensionId]);

        var materialized = codec.Load(content, save);
        var targetSetup = new PopulationSetupSystem(materialized.World);
        _ = new SystemicGameBuilder(content, materialized).AddSystem(targetSetup).Build();

        Assert.Equal([1.25f, -2.5f, 9], targetSetup.Velocity.Current.ToArray());
        targetSetup.Velocity.Swap();
        Assert.Equal([1.25f, -2.5f, 9], targetSetup.Velocity.Current.ToArray());
    }

    [Fact]
    public void UnknownPopulationOrColumnVersionFailsLoad()
    {
        using var pack = CoreOnlyPack.Create();
        var content = ContentPackLoader.Load(pack.Root);
        var codec = new SystemicSaveCodec();
        var save = CreatePopulatedSave(content, codec);

        var unknownPopulation = JsonNode.Parse(save)!.AsObject();
        Assert.NotNull(unknownPopulation["extensionStates"]?[PopulationExtensionId]);
        unknownPopulation["extensionStates"]![PopulationExtensionId]!["populations"]![0]!["id"] = "unknown.population";
        AssertPopulationRestoreFails(content, codec, SaveTestDocument.Seal(unknownPopulation));

        var unknownColumnVersion = JsonNode.Parse(save)!.AsObject();
        unknownColumnVersion["extensionStates"]![PopulationExtensionId]!["populations"]![0]!["columns"]![0]!["version"] = 2;
        AssertPopulationRestoreFails(content, codec, SaveTestDocument.Seal(unknownColumnVersion));

        var unknownPopulationVersion = JsonNode.Parse(save)!.AsObject();
        unknownPopulationVersion["extensionStates"]![PopulationExtensionId]!["populations"]![0]!["version"] = 2;
        AssertPopulationRestoreFails(content, codec, SaveTestDocument.Seal(unknownPopulationVersion));

        var unknownColumnId = JsonNode.Parse(save)!.AsObject();
        unknownColumnId["extensionStates"]![PopulationExtensionId]!["populations"]![0]!["columns"]![0]!["id"] = "unknown.column";
        AssertPopulationRestoreFails(content, codec, SaveTestDocument.Seal(unknownColumnId));

        var noDefinitions = codec.Load(content, SaveTestDocument.Seal(unknownPopulation));
        var noPopulationException = Assert.Throws<SystemicSaveException>(() =>
            new SystemicGameBuilder(content, noDefinitions).Build());
        Assert.Contains("population", noPopulationException.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidColumnPayloadLengthFailsWithoutPartialRestore()
    {
        using var pack = CoreOnlyPack.Create();
        var content = ContentPackLoader.Load(pack.Root);
        var codec = new SystemicSaveCodec();
        var save = CreatePopulatedSave(content, codec, includeSecondary: true);
        var root = JsonNode.Parse(save)!.AsObject();
        Assert.NotNull(root["extensionStates"]?[PopulationExtensionId]);
        Assert.Equal("test.particles", root["extensionStates"]![PopulationExtensionId]!["populations"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("test.secondary-particles", root["extensionStates"]![PopulationExtensionId]!["populations"]![1]!["id"]!.GetValue<string>());
        root["extensionStates"]![PopulationExtensionId]!["populations"]![1]!["columns"]![0]!["data"] =
            Convert.ToBase64String([0x01, 0x02]);

        var materialized = codec.Load(content, SaveTestDocument.Seal(root));
        var targetSetup = new PopulationSetupSystem(materialized.World, includeSecondary: true);
        var exception = Assert.Throws<SystemicSaveException>(() =>
            new SystemicGameBuilder(content, materialized).AddSystem(targetSetup).Build());

        Assert.Contains("payload", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, targetSetup.Particles.Count);
        Assert.Equal(0, targetSetup.Secondary!.Count);
    }

    [Fact]
    public void MissingPopulationFailsWithoutPartialRestore()
    {
        using var pack = CoreOnlyPack.Create();
        var content = ContentPackLoader.Load(pack.Root);
        var codec = new SystemicSaveCodec();
        var root = JsonNode.Parse(CreatePopulatedSave(content, codec, includeSecondary: true))!.AsObject();
        root["extensionStates"]![PopulationExtensionId]!["populations"]!.AsArray().RemoveAt(1);

        var materialized = codec.Load(content, SaveTestDocument.Seal(root));
        var targetSetup = new PopulationSetupSystem(materialized.World, includeSecondary: true);
        var exception = Assert.Throws<SystemicSaveException>(() =>
            new SystemicGameBuilder(content, materialized).AddSystem(targetSetup).Build());

        Assert.Contains("population", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, targetSetup.Particles.Count);
        Assert.Equal(0, targetSetup.Secondary!.Count);
    }

    [Fact]
    public void InvalidHandleMetadataFailsWithoutPartialRestore()
    {
        using var pack = CoreOnlyPack.Create();
        var content = ContentPackLoader.Load(pack.Root);
        var codec = new SystemicSaveCodec();
        var root = JsonNode.Parse(CreatePopulatedSave(content, codec, includeSecondary: true))!.AsObject();
        root["extensionStates"]![PopulationExtensionId]!["populations"]![0]!["rowToHandleSlot"]![0] = 12;

        var materialized = codec.Load(content, SaveTestDocument.Seal(root));
        var targetSetup = new PopulationSetupSystem(materialized.World, includeSecondary: true);
        var exception = Assert.Throws<SystemicSaveException>(() =>
            new SystemicGameBuilder(content, materialized).AddSystem(targetSetup).Build());

        Assert.Contains("handle", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, targetSetup.Particles.Count);
        Assert.Equal(0, targetSetup.Secondary!.Count);
    }

    [Fact]
    public void MalformedBooleanPayloadFailsAsSaveExceptionWithoutPartialRestore()
    {
        using var pack = CoreOnlyPack.Create();
        var content = ContentPackLoader.Load(pack.Root);
        var materializedSource = content.CreateWorld(seed: 76, valueScale: 1);
        var sourceSetup = new BooleanPopulationSetupSystem(materializedSource.World);
        var primaryRow = sourceSetup.Particles.AddRange(1);
        sourceSetup.Energy.EditRange(primaryRow.Start, primaryRow.Length)[0] = 42;
        var booleanRow = sourceSetup.Flags.AddRange(1);
        sourceSetup.FlagsColumn.EditRange(booleanRow.Start, booleanRow.Length)[0] = true;
        var codec = new SystemicSaveCodec();
        var save = codec.Save(new SystemicGameBuilder(content, materializedSource)
            .AddSystem(sourceSetup).Build());
        var root = JsonNode.Parse(save)!.AsObject();
        root["extensionStates"]![PopulationExtensionId]!["populations"]![1]!["columns"]![0]!["data"] =
            Convert.ToBase64String([0x02]);

        var materialized = codec.Load(content, SaveTestDocument.Seal(root));
        var targetSetup = new BooleanPopulationSetupSystem(materialized.World);
        var exception = Assert.Throws<SystemicSaveException>(() =>
            new SystemicGameBuilder(content, materialized).AddSystem(targetSetup).Build());

        Assert.Contains("population", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, targetSetup.Particles.Count);
        Assert.Equal(0, targetSetup.Flags.Count);
    }

    [Fact]
    public void EmptyPopulationStateRoundTrips()
    {
        using var pack = CoreOnlyPack.Create();
        var content = ContentPackLoader.Load(pack.Root);
        var materializedSource = content.CreateWorld(seed: 73, valueScale: 1);
        var world = materializedSource.World;
        var sourceSetup = new PopulationSetupSystem(world);
        var codec = new SystemicSaveCodec();
        var save = codec.Save(new SystemicGameBuilder(content, world).AddSystem(sourceSetup).Build());
        Assert.NotNull(JsonNode.Parse(save)!["extensionStates"]?[PopulationExtensionId]);

        var materialized = codec.Load(content, save);
        var targetSetup = new PopulationSetupSystem(materialized.World);
        _ = new SystemicGameBuilder(content, materialized).AddSystem(targetSetup).Build();

        Assert.Equal(0, targetSetup.Particles.Count);
        Assert.Empty(targetSetup.Energy.ReadOnlySpan.ToArray());
    }

    [Fact]
    public void ExistingSaveWithoutPopulationStateStillLoads()
    {
        using var pack = CoreOnlyPack.Create();
        var content = ContentPackLoader.Load(pack.Root);
        var world = content.CreateWorld(seed: 74, valueScale: 1);
        var codec = new SystemicSaveCodec();
        var save = codec.Save(new SystemicGameBuilder(content, world).Build());
        var root = JsonNode.Parse(save)!.AsObject();
        Assert.Null(root["extensionStates"]![PopulationExtensionId]);

        var materialized = codec.Load(content, save);
        var game = new SystemicGameBuilder(content, materialized).Build();

        Assert.Equal(content.Entities.Definitions.Length, game.World.Entities.Count);
    }

    private static string CreatePopulatedSave(ContentPack content, SystemicSaveCodec codec, bool includeSecondary = false)
    {
        var materializedSource = content.CreateWorld(seed: 75, valueScale: 1);
        var world = materializedSource.World;
        var setup = new PopulationSetupSystem(world, includeSecondary);
        var rows = setup.Particles.AddRange(1);
        setup.Energy.EditRange(rows.Start, rows.Length)[0] = 41;
        if (includeSecondary)
        {
            var secondaryRows = setup.Secondary!.AddRange(1);
            setup.SecondaryEnergy!.EditRange(secondaryRows.Start, secondaryRows.Length)[0] = 99;
        }

        return codec.Save(new SystemicGameBuilder(content, world).AddSystem(setup).Build());
    }

    private static void AssertPopulationRestoreFails(ContentPack content, SystemicSaveCodec codec, string save)
    {
        var materialized = codec.Load(content, save);
        var setup = new PopulationSetupSystem(materialized.World);
        var exception = Assert.Throws<SystemicSaveException>(() =>
            new SystemicGameBuilder(content, materialized).AddSystem(setup).Build());

        Assert.Equal(0, setup.Particles.Count);
        Assert.Equal(0, setup.Energy.Count);
        Assert.Contains("population", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class PopulationSetupSystem : ISystemicSystem
    {
        public PopulationSetupSystem(World world, bool includeSecondary = false)
        {
            if (includeSecondary)
            {
                Secondary = world.DefinePopulation<SecondaryParticleKind>();
                SecondaryEnergy = Secondary.Column(s_energyKey);
            }

            Particles = world.DefinePopulation<ParticleKind>(enableHandles: true);
            Energy = Particles.Column(s_energyKey);
            Position = Particles.Column(s_positionKey);
            Velocity = Particles.DoubleBufferedColumn(s_velocityKey);
        }

        public string Id => "test.population-setup";

        public int Order => 0;

        public Population<ParticleKind> Particles { get; }

        public PopulationColumn<int> Energy { get; }

        public PopulationColumn<Vector2> Position { get; }

        public DoubleBufferedPopulationColumn<float> Velocity { get; }

        public Population<SecondaryParticleKind>? Secondary { get; }

        public PopulationColumn<int>? SecondaryEnergy { get; }

        public void Step(SystemicSystemContext context)
        {
        }
    }

    private sealed class BooleanPopulationSetupSystem(World world) : ISystemicSystem
    {
        public Population<ParticleKind> Particles { get; } = world.DefinePopulation<ParticleKind>();

        public PopulationColumn<int> Energy { get; } = world.GetPopulation<ParticleKind>().Column(s_energyKey);

        public Population<BooleanPopulationKind> Flags { get; } = world.DefinePopulation<BooleanPopulationKind>();

        public PopulationColumn<bool> FlagsColumn { get; } = world.GetPopulation<BooleanPopulationKind>().Column(s_flagKey);

        public string Id => "test.boolean-population-setup";

        public int Order => 0;

        public void Step(SystemicSystemContext context)
        {
        }
    }

    private readonly struct ParticleKind : IPopulationKind
    {
        public static string Id => "test.particles";

        public static int Version => 1;
    }

    private readonly struct SecondaryParticleKind : IPopulationKind
    {
        public static string Id => "test.secondary-particles";

        public static int Version => 1;
    }

    private readonly struct BooleanPopulationKind : IPopulationKind
    {
        public static string Id => "test.zbools";

        public static int Version => 1;
    }
}
