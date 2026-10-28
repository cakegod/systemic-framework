using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Systemic.Kernel;

namespace Systemic.Tests.Kernel;

public sealed class PopulationCloneTests
{
    private static readonly PopulationColumnKey<int> s_energyKey = new("energy", 1, PopulationCodecs.Int32);
    private static readonly PopulationColumnKey<float> s_phaseKey = new("phase", 1, PopulationCodecs.Single);

    [Fact]
    public void WorldCloneSharesPopulationBuffersUntilFirstMutation()
    {
        var source = new World(seed: 7, valueScale: 1);
        var sourcePopulation = source.DefinePopulation<CloneParticleKind>(enableHandles: true);
        var sourceEnergy = sourcePopulation.Column(s_energyKey);
        var sourcePhase = sourcePopulation.DoubleBufferedColumn(s_phaseKey);
        var rows = sourcePopulation.AddRange(2);
        sourceEnergy.EditRange(rows.Start, rows.Length)[0] = 10;
        sourceEnergy.EditRange(rows.Start, rows.Length)[1] = 20;
        var next = sourcePhase.EditNext(0, 2);
        next[0] = 30;
        next[1] = 40;
        sourcePhase.Swap();
        var secondBuffer = sourcePhase.EditNext(0, 2);
        secondBuffer[0] = 1;
        secondBuffer[1] = 2;
        var rowOne = sourcePopulation.GetHandle(1);

        var clone = source.Clone();
        var clonePopulation = clone.GetPopulation<CloneParticleKind>();
        var cloneEnergy = clonePopulation.Column(s_energyKey);
        var clonePhase = clonePopulation.DoubleBufferedColumn(s_phaseKey);

        Assert.NotSame(sourcePopulation, clonePopulation);
        Assert.Equal(sourcePopulation.Count, clonePopulation.Count);
        Assert.Equal(sourcePopulation.Capacity, clonePopulation.Capacity);
        Assert.True(sourcePopulation.TryResolve(rowOne, out var sourceRow));
        Assert.True(clonePopulation.TryResolve(rowOne, out var cloneRow));
        Assert.Equal(1, sourceRow);
        Assert.Equal(sourceRow, cloneRow);
        AssertShared(sourceEnergy.ReadOnlySpan, cloneEnergy.ReadOnlySpan);
        AssertShared(sourcePhase.Current, clonePhase.Current);
        sourcePhase.Swap();
        clonePhase.Swap();
        AssertShared(sourcePhase.Current, clonePhase.Current);
        sourcePhase.Swap();
        clonePhase.Swap();

        cloneEnergy.EditRange(0, 1)[0] = 99;
        Assert.Equal(10, sourceEnergy.ReadOnlySpan[0]);
        Assert.Equal(99, cloneEnergy.ReadOnlySpan[0]);
        AssertNotShared(sourceEnergy.ReadOnlySpan, cloneEnergy.ReadOnlySpan);

        clonePhase.EditNext(0, 1)[0] = 77;
        Assert.Equal(1, sourcePhase.EditNext(0, 2)[0]);
        Assert.Equal(77, clonePhase.EditNext(0, 2)[0]);
        AssertNotShared(sourcePhase.EditNext(0, 2), clonePhase.EditNext(0, 2));
        AssertShared(sourcePhase.Current, clonePhase.Current);

        clonePhase.Swap();
        clonePhase.EditNext(0, 1)[0] = 88;
        Assert.Equal(30, sourcePhase.Current[0]);
        Assert.Equal(88, clonePhase.EditNext(0, 2)[0]);
        AssertNotShared(sourcePhase.Current, clonePhase.EditNext(0, 2));

        Assert.True(clonePopulation.TryRemove(rowOne));
        Assert.False(clonePopulation.TryResolve(rowOne, out _));
        Assert.True(sourcePopulation.TryResolve(rowOne, out _));
        var reusedRow = clonePopulation.AddRange(1);
        var reusedHandle = clonePopulation.GetHandle(reusedRow.Start);
        Assert.Equal(rowOne.Slot, reusedHandle.Slot);
        Assert.NotEqual(rowOne.Generation, reusedHandle.Generation);
        Assert.False(clonePopulation.TryResolve(rowOne, out _));
        Assert.True(sourcePopulation.TryResolve(rowOne, out _));
    }

    [Fact]
    public void ChainedWorldClonesDetachIndependentlyOnFirstWrite()
    {
        var source = new World(seed: 9, valueScale: 1);
        var sourcePopulation = source.DefinePopulation<CloneParticleKind>();
        var sourceEnergy = sourcePopulation.Column(s_energyKey);
        sourcePopulation.AddRange(1);
        sourceEnergy.EditRange(0, 1)[0] = 10;

        var child = source.Clone();
        var grandchild = child.Clone();

        source.GetPopulation<CloneParticleKind>().Column(s_energyKey).EditRange(0, 1)[0] = 11;
        child.GetPopulation<CloneParticleKind>().Column(s_energyKey).EditRange(0, 1)[0] = 12;
        grandchild.GetPopulation<CloneParticleKind>().Column(s_energyKey).EditRange(0, 1)[0] = 13;

        Assert.Equal(11, sourceEnergy.ReadOnlySpan[0]);
        Assert.Equal(12, child.GetPopulation<CloneParticleKind>().Column(s_energyKey).ReadOnlySpan[0]);
        Assert.Equal(13, grandchild.GetPopulation<CloneParticleKind>().Column(s_energyKey).ReadOnlySpan[0]);
    }

    [Fact]
    public void SiblingClonesAllocateDistinctReusedHandleGenerations()
    {
        var source = new World(seed: 10, valueScale: 1);
        var sourcePopulation = source.DefinePopulation<CloneParticleKind>(enableHandles: true);
        sourcePopulation.AddRange(1);
        var inheritedHandle = sourcePopulation.GetHandle(0);

        var left = source.Clone().GetPopulation<CloneParticleKind>();
        var right = source.Clone().GetPopulation<CloneParticleKind>();
        Assert.True(left.TryResolve(inheritedHandle, out _));
        Assert.True(right.TryResolve(inheritedHandle, out _));

        Assert.True(left.TryRemove(inheritedHandle));
        Assert.True(right.TryRemove(inheritedHandle));
        var leftRow = left.AddRange(1);
        var rightRow = right.AddRange(1);
        var leftHandle = left.GetHandle(leftRow.Start);
        var rightHandle = right.GetHandle(rightRow.Start);

        Assert.Equal(inheritedHandle.Slot, leftHandle.Slot);
        Assert.Equal(inheritedHandle.Slot, rightHandle.Slot);
        Assert.NotEqual(leftHandle.Generation, rightHandle.Generation);
        Assert.True(left.TryResolve(leftHandle, out _));
        Assert.False(right.TryResolve(leftHandle, out _));
        Assert.True(right.TryResolve(rightHandle, out _));
        Assert.False(left.TryResolve(rightHandle, out _));
    }

    [Fact]
    public void SiblingClonesAllocateDistinctGenerationsForSlotsFreedBeforeClone()
    {
        var source = new World(seed: 12, valueScale: 1);
        var sourcePopulation = source.DefinePopulation<CloneParticleKind>(enableHandles: true);
        sourcePopulation.AddRange(2);
        var inheritedHandle = sourcePopulation.GetHandle(1);
        Assert.True(sourcePopulation.TryRemove(sourcePopulation.GetHandle(0)));

        var left = source.Clone().GetPopulation<CloneParticleKind>();
        var right = source.Clone().GetPopulation<CloneParticleKind>();
        Assert.True(left.TryResolve(inheritedHandle, out _));
        Assert.True(right.TryResolve(inheritedHandle, out _));

        var leftRow = left.AddRange(1);
        var rightRow = right.AddRange(1);
        var leftHandle = left.GetHandle(leftRow.Start);
        var rightHandle = right.GetHandle(rightRow.Start);

        Assert.Equal(0, leftHandle.Slot);
        Assert.Equal(leftHandle.Slot, rightHandle.Slot);
        Assert.NotEqual(leftHandle.Generation, rightHandle.Generation);
        Assert.False(right.TryResolve(leftHandle, out _));
        Assert.False(left.TryResolve(rightHandle, out _));
    }

    [Fact]
    public void DetachedPopulationReferencesRejectReadsAfterAdoption()
    {
        var world = new World(seed: 11, valueScale: 1);
        var stalePopulation = world.DefinePopulation<CloneParticleKind>(enableHandles: true);
        var staleEnergy = stalePopulation.Column(s_energyKey);
        var stalePhase = stalePopulation.DoubleBufferedColumn(s_phaseKey);
        stalePopulation.AddRange(1);
        staleEnergy.EditRange(0, 1)[0] = 42;
        var handle = stalePopulation.GetHandle(0);
        var actionWorld = world.Clone();

        Adopt(world, actionWorld);

        Assert.Throws<InvalidOperationException>(() => _ = stalePopulation.Count);
        Assert.Throws<InvalidOperationException>(() => _ = stalePopulation.Capacity);
        Assert.Throws<InvalidOperationException>(() => stalePopulation.TryResolve(handle, out _));
        Assert.Throws<InvalidOperationException>(() => _ = staleEnergy.Count);
        Assert.Throws<InvalidOperationException>(() => _ = staleEnergy.ReadOnlySpan.Length);
        Assert.Throws<InvalidOperationException>(() => _ = stalePhase.Count);
        Assert.Throws<InvalidOperationException>(() => _ = stalePhase.Current.Length);
    }

    private static void Adopt(World destination, World actionWorld)
    {
        var method = typeof(World).GetMethod("AdoptActionWorld", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(destination, [actionWorld]);
    }

    private static void AssertShared<T>(ReadOnlySpan<T> first, ReadOnlySpan<T> second) where T : unmanaged
    {
        Assert.Equal(first.Length, second.Length);
        Assert.True(first.Length > 0);
        ref var firstReference = ref MemoryMarshal.GetReference(first);
        ref var secondReference = ref MemoryMarshal.GetReference(second);
        Assert.True(Unsafe.AreSame(ref firstReference, ref secondReference));
    }

    private static void AssertNotShared<T>(ReadOnlySpan<T> first, ReadOnlySpan<T> second) where T : unmanaged
    {
        Assert.Equal(first.Length, second.Length);
        Assert.True(first.Length > 0);
        ref var firstReference = ref MemoryMarshal.GetReference(first);
        ref var secondReference = ref MemoryMarshal.GetReference(second);
        Assert.False(Unsafe.AreSame(ref firstReference, ref secondReference));
    }

    private readonly struct CloneParticleKind : IPopulationKind
    {
        public static string Id => "test.clone-particles";

        public static int Version => 1;
    }
}
