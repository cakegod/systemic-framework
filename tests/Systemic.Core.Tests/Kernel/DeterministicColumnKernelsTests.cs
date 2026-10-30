using System.Numerics;
using System.Runtime.InteropServices;
using Systemic.Kernel;

namespace Systemic.Tests.Kernel;

public sealed class DeterministicColumnKernelsTests
{
    [Fact]
    public void IntegerKernelsProcessAllVectorLanesAndScalarTailWithoutTouchingExtraDestination()
    {
        var length = Vector<int>.Count * 3 + 3;
        var left = Enumerable.Range(0, length).Select(i => i - 100).ToArray();
        var right = Enumerable.Range(0, length).Select(i => 3 * i - 23).ToArray();
        var destination = Enumerable.Repeat(999, length + 2).ToArray();

        DeterministicColumnKernels.Add(left, right, destination);
        for (var i = 0; i < length; i++)
        {
            Assert.Equal(4 * i - 123, destination[i]);
        }

        Assert.Equal(999, destination[length]);
        Assert.Equal(999, destination[length + 1]);

        DeterministicColumnKernels.Subtract(left, right, destination);
        for (var i = 0; i < length; i++)
        {
            Assert.Equal(-2 * i - 77, destination[i]);
        }

        DeterministicColumnKernels.Multiply(left, 3, destination);
        for (var i = 0; i < length; i++)
        {
            Assert.Equal(3 * i - 300, destination[i]);
        }
    }

    [Fact]
    public void LongKernelsProcessAllVectorLanesAndScalarTail()
    {
        var length = Vector<long>.Count * 3 + 1;
        var left = Enumerable.Range(0, length).Select(i => 1_000_000_000_000L + i).ToArray();
        var right = Enumerable.Range(0, length).Select(i => -2_000L - i).ToArray();
        var destination = new long[length];

        DeterministicColumnKernels.Add(left, right, destination);
        Assert.All(destination, value => Assert.Equal(999_999_998_000L, value));
        DeterministicColumnKernels.Subtract(left, right, destination);
        for (var i = 0; i < length; i++)
        {
            Assert.Equal(1_000_000_002_000L + 2L * i, destination[i]);
        }
    }

    [Fact]
    public void SumAccumulatesAcrossLanesAndTailIntoAWideInteger()
    {
        var length = Vector<int>.Count * 2 + 1;
        var values = Enumerable.Range(0, length)
            .Select(i => i % 2 == 0 ? int.MaxValue : int.MinValue).ToArray();
        Assert.Equal((long)int.MaxValue - Vector<int>.Count, DeterministicColumnKernels.Sum(values));
        Assert.Equal(21_474_836_470L, DeterministicColumnKernels.Sum(Enumerable.Repeat(int.MaxValue, 10).ToArray()));
        Assert.Equal(0L, DeterministicColumnKernels.Sum(ReadOnlySpan<int>.Empty));
    }

    [Theory]
    [InlineData(int.MaxValue, 1)]
    [InlineData(int.MinValue, -1)]
    public void AddOverflowInAVectorLaneOrTailNeverChangesTheDestination(int a, int b)
    {
        var length = Vector<int>.Count * 2 + 1;
        foreach (var index in new[] { Vector<int>.Count - 1, length - 1 })
        {
            var left = new int[length];
            var right = new int[length];
            left[index] = a;
            right[index] = b;
            var destination = Enumerable.Repeat(47, length).ToArray();
            Assert.Throws<OverflowException>(() => DeterministicColumnKernels.Add(left, right, destination));
            Assert.All(destination, value => Assert.Equal(47, value));
        }
    }

    [Theory]
    [InlineData(int.MaxValue, -1)]
    [InlineData(int.MinValue, 1)]
    public void SubtractOverflowNeverChangesTheDestination(int a, int b)
    {
        int[] left = [1, a];
        int[] right = [1, b];
        int[] destination = [17, 18];
        Assert.Throws<OverflowException>(() => DeterministicColumnKernels.Subtract(left, right, destination));
        Assert.Equal([17, 18], destination);
    }

    [Theory]
    [InlineData(int.MinValue, -1)]
    [InlineData(int.MaxValue, 2)]
    public void MultiplyOverflowInAnyLaneNeverChangesTheDestination(int value, int factor)
    {
        var length = Vector<int>.Count * 2 + 1;
        foreach (var index in new[] { Vector<int>.Count - 1, length - 1 })
        {
            var input = new int[length];
            input[index] = value;
            var destination = Enumerable.Repeat(57, length).ToArray();
            Assert.Throws<OverflowException>(() => DeterministicColumnKernels.Multiply(input, factor, destination));
            Assert.All(destination, item => Assert.Equal(57, item));
        }
    }

    [Fact]
    public void LongOverflowIsCheckedForBothOperationsWithoutPartialWrites()
    {
        long[] destination = [9, 9];
        Assert.Throws<OverflowException>(() => DeterministicColumnKernels.Add([0L, long.MaxValue], [0L, 1L], destination));
        Assert.Equal([9L, 9L], destination);
        Assert.Throws<OverflowException>(() => DeterministicColumnKernels.Subtract([0L, long.MinValue], [0L, 1L], destination));
        Assert.Equal([9L, 9L], destination);
    }

    [Fact]
    public void InvalidLengthsFailBeforeAnyDestinationWrites()
    {
        int[] destination = [7, 8, 9];
        Assert.Throws<ArgumentException>(() => DeterministicColumnKernels.Add([1, 2], [3], destination));
        Assert.Equal([7, 8, 9], destination);
        Assert.Throws<ArgumentException>(() => DeterministicColumnKernels.Subtract([1, 2, 3, 4], [1, 2, 3, 4], destination));
        Assert.Equal([7, 8, 9], destination);
        Assert.Throws<ArgumentException>(() => DeterministicColumnKernels.Multiply([1, 2, 3, 4], 2, destination));
        Assert.Equal([7, 8, 9], destination);
        DeterministicColumnKernels.Add(ReadOnlySpan<int>.Empty, ReadOnlySpan<int>.Empty, destination);
        Assert.Equal([7, 8, 9], destination);
    }

    [Fact]
    public void OverlapInOppositeDirectionsUsesOriginalInputs()
    {
        int[] values = [1, 2, 3, 4, 5, 6];
        DeterministicColumnKernels.Add(values.AsSpan(0, 4), values.AsSpan(2, 4), values.AsSpan(1, 4));
        Assert.Equal([1, 4, 6, 8, 10, 6], values);
    }

    [Fact]
    public void InPlaceAndBackwardOverlappingWritesUseOriginalInputs()
    {
        int[] values = [1, 2, 3, 4, 5, 6];
        DeterministicColumnKernels.Multiply(values.AsSpan(1, 4), 10, values.AsSpan(0, 4));
        Assert.Equal([20, 30, 40, 50, 5, 6], values);
        int[] addends = [1, 1, 1, 1];
        DeterministicColumnKernels.Add(values.AsSpan(0, 4), addends, values.AsSpan(0, 4));
        Assert.Equal([21, 31, 41, 51, 5, 6], values);
    }

    [Fact]
    public void OverflowWithOverlappingInputAndDestinationChangesNoBytes()
    {
        int[] values = [1, 2, 3, int.MaxValue, 9];
        var before = values.ToArray();
        int[] ones = [1, 1, 1, 1];
        Assert.Throws<OverflowException>(() => DeterministicColumnKernels.Add(values.AsSpan(0, 4), ones, values.AsSpan(1, 4)));
        Assert.Equal(before, values);
    }

    [Fact]
    public void NonAlignedByteOverlappingSpansUseTheOriginalInputs()
    {
        var bytes = new byte[20];
        var input = MemoryMarshal.Cast<byte, int>(bytes.AsSpan(0, 16));
        input[0] = 1; input[1] = 2; input[2] = 3; input[3] = 4;
        var destination = MemoryMarshal.Cast<byte, int>(bytes.AsSpan(1, 16));
        int[] right = [10, 20, 30, 40];
        DeterministicColumnKernels.Add(input, right, destination);
        Assert.Equal([11, 22, 33, 44], destination.ToArray());
    }
}
