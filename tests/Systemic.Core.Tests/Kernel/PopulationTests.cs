using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Systemic.Kernel;

namespace Systemic.Tests.Kernel;

public sealed class PopulationTests
{
    private static readonly PopulationColumnKey<int> s_energyKey =
        new("energy", 1, PopulationCodecs.Int32);
    private static readonly PopulationColumnKey<float> s_speedKey =
        new("speed", 1, PopulationCodecs.Single);

    [Fact]
    public void DefinePopulationAddsRowsAcrossTypedColumns()
    {
        var world = new World(seed: 1, valueScale: 1);
        var population = world.DefinePopulation<ParticleKind>();
        var energy = population.Column(s_energyKey);
        var speed = population.Column(s_speedKey);

        var range = population.AddRange(3);
        energy.EditRange(range.Start, range.Length).Fill(12);
        speed.EditRange(range.Start, range.Length).Fill(1.5f);

        Assert.Same(population, world.GetPopulation<ParticleKind>());
        Assert.Equal("test.particles", ParticleKind.Id);
        Assert.Equal(1, ParticleKind.Version);
        Assert.Equal(3, population.Count);
        Assert.Equal([12, 12, 12], energy.ReadOnlySpan.ToArray());
        Assert.Equal([1.5f, 1.5f, 1.5f], speed.ReadOnlySpan.ToArray());
        Assert.Equal(new PopulationRange(0, 3), range);
    }

    [Fact]
    public void ReservePreventsFurtherCapacityGrowth()
    {
        var population = new World(seed: 2, valueScale: 1).DefinePopulation<ParticleKind>();
        var energy = population.Column(s_energyKey);

        population.Reserve(16);
        var capacity = population.Capacity;
        var range = population.AddRange(16);
        energy.EditRange(range.Start, range.Length).Fill(4);

        Assert.True(capacity >= 16);
        Assert.Equal(capacity, population.Capacity);
        Assert.Equal(16, population.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => population.Reserve(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => population.AddRange(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => energy.EditRange(16, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => energy.EditRange(0, -1));

        energy.EditRange(0, 16).Clear();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 1_000; iteration++)
        {
            energy.EditRange(0, 16).Fill(iteration);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
    }

    [Fact]
    public void DoubleBufferSwapExchangesCurrentAndNextWithoutCopy()
    {
        var population = new World(seed: 3, valueScale: 1).DefinePopulation<ParticleKind>();
        var column = population.DoubleBufferedColumn(s_speedKey);
        population.AddRange(3);
        var initialCurrent = column.Current;
        var next = column.EditNext(0, 3);
        next[0] = 3;
        next[1] = 5;
        next[2] = 7;
        ref var initialCurrentReference = ref MemoryMarshal.GetReference(initialCurrent);
        ref var nextReference = ref MemoryMarshal.GetReference(next);
        Assert.Throws<ArgumentOutOfRangeException>(() => column.EditNext(3, 1));

        column.Swap();

        var swappedCurrent = column.Current;
        var swappedNext = column.EditNext(0, 3);
        ref var swappedCurrentReference = ref MemoryMarshal.GetReference(swappedCurrent);
        ref var swappedNextReference = ref MemoryMarshal.GetReference(swappedNext);
        Assert.True(Unsafe.AreSame(ref nextReference, ref swappedCurrentReference));
        Assert.True(Unsafe.AreSame(ref initialCurrentReference, ref swappedNextReference));
        Assert.Equal([3, 5, 7], swappedCurrent.ToArray());
    }

    [Fact]
    public void SwapBackRemovalKeepsColumnsAligned()
    {
        var population = new World(seed: 4, valueScale: 1).DefinePopulation<ParticleKind>();
        var energy = population.Column(s_energyKey);
        var speed = population.Column(s_speedKey);
        var range = population.AddRange(3);
        var energyRows = energy.EditRange(range.Start, range.Length);
        energyRows[0] = 10;
        energyRows[1] = 20;
        energyRows[2] = 30;
        var speedRows = speed.EditRange(range.Start, range.Length);
        speedRows[0] = 1;
        speedRows[1] = 2;
        speedRows[2] = 3;

        population.RemoveSwapBack(1);

        Assert.Equal(2, population.Count);
        Assert.Equal([10, 30], energy.ReadOnlySpan.ToArray());
        Assert.Equal([1, 3], speed.ReadOnlySpan.ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => population.RemoveSwapBack(2));
    }

    [Fact]
    public void HandlesFollowMovedRowsAndRejectReusedSlots()
    {
        var population = new World(seed: 5, valueScale: 1).DefinePopulation<ParticleKind>(enableHandles: true);
        var energy = population.Column(s_energyKey);
        var range = population.AddRange(3);
        var energyRows = energy.EditRange(range.Start, range.Length);
        energyRows[0] = 10;
        energyRows[1] = 20;
        energyRows[2] = 30;
        var removed = population.GetHandle(1);
        var moved = population.GetHandle(2);

        population.RemoveSwapBack(1);

        Assert.False(population.TryResolve(removed, out _));
        Assert.True(population.TryResolve(moved, out var movedRow));
        Assert.Equal(1, movedRow);
        Assert.True(population.TryRemove(moved));
        Assert.False(population.TryResolve(moved, out _));

        var added = population.AddRange(1);
        energy.EditRange(added.Start, added.Length)[0] = 40;
        var reusedSlot = population.GetHandle(1);
        Assert.Equal(moved.Slot, reusedSlot.Slot);
        Assert.NotEqual(moved.Generation, reusedSlot.Generation);
        Assert.False(population.TryResolve(moved, out _));
        Assert.True(population.TryResolve(reusedSlot, out var reusedRow));
        Assert.Equal(1, reusedRow);
    }

    [Fact]
    public void RejectsDuplicateOrMismatchedColumnDefinitions()
    {
        var world = new World(seed: 6, valueScale: 1);
        var population = world.DefinePopulation<ParticleKind>();
        var energy = population.Column(s_energyKey);

        Assert.Same(population, world.GetPopulation<ParticleKind>());
        Assert.Same(energy, population.Column(s_energyKey));
        Assert.Throws<InvalidOperationException>(() => population.Column(
            new PopulationColumnKey<int>("energy", 2, PopulationCodecs.Int32)));
        Assert.Throws<InvalidOperationException>(() => population.Column(
            new PopulationColumnKey<float>("energy", 1, PopulationCodecs.Single)));
        Assert.Throws<InvalidOperationException>(() => world.DefinePopulation<ParticleKind>());
        Assert.Throws<InvalidOperationException>(() => world.DefinePopulation<DuplicateParticleKind>());
    }

    [Fact]
    public void BuiltInPopulationCodecsUseStableLittleEndianValues()
    {
        Span<int> source = [-1, 0x12345678];
        Span<byte> encoded = stackalloc byte[8];
        PopulationCodecs.Int32.Encode(source, encoded);
        Assert.Equal([0xff, 0xff, 0xff, 0xff, 0x78, 0x56, 0x34, 0x12], encoded.ToArray());

        Span<int> decoded = stackalloc int[2];
        PopulationCodecs.Int32.Decode(encoded, decoded);
        Assert.Equal(source.ToArray(), decoded.ToArray());

        Span<System.Numerics.Vector2> vector = [new System.Numerics.Vector2(1, -2)];
        Span<byte> encodedVector = stackalloc byte[8];
        PopulationCodecs.Vector2.Encode(vector, encodedVector);
        Span<System.Numerics.Vector2> decodedVector = stackalloc System.Numerics.Vector2[1];
        PopulationCodecs.Vector2.Decode(encodedVector, decodedVector);
        Assert.Equal(vector[0], decodedVector[0]);
    }

    [Fact]
    public void BorrowedMemoryViewsReadAndWriteTheLiveColumnRows()
    {
        var population = new World(seed: 7, valueScale: 1).DefinePopulation<ParticleKind>();
        var energy = population.Column(s_energyKey);
        var speed = population.DoubleBufferedColumn(s_speedKey);
        population.AddRange(2);

        var energyMemory = energy.EditMemory(0, 2);
        energyMemory.Span[0] = 17;
        energyMemory.Span[1] = 31;
        var speedMemory = speed.EditNextMemory(0, 2);
        speedMemory.Span[0] = 1.5f;
        speedMemory.Span[1] = 2.5f;
        speed.Swap();

        Assert.Equal([17, 31], energy.ReadOnlyMemory.ToArray());
        Assert.Equal([1.5f, 2.5f], speed.CurrentMemory.ToArray());
    }

    private readonly struct ParticleKind : IPopulationKind
    {
        public static string Id => "test.particles";
        public static int Version => 1;
    }

    private readonly struct DuplicateParticleKind : IPopulationKind
    {
        public static string Id => "test.particles";
        public static int Version => 2;
    }
}
