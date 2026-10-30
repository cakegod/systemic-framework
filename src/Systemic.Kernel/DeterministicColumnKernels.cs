using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Systemic.Kernel;

/// <summary>Checked, deterministic integer kernels over contiguous population-column data.</summary>
/// <remarks>
/// These operations use hardware vectors when available and otherwise use the same scalar integer
/// operations. Results do not depend on vector width. Input lengths must match, and destination
/// must be large enough; elements beyond the output length are not changed. Every operation
/// validates lengths and all arithmetic before writing, so an overflow or invalid length leaves
/// the destination unchanged. Inputs may overlap each other or the destination, including at
/// non-element-aligned byte offsets; results use the original input values. Partial overlap may
/// require a temporary pooled array. Callers must not concurrently mutate the input or destination
/// spans, or retain population spans across structure, clone, swap, or transaction changes.
/// </remarks>
public static class DeterministicColumnKernels
{
    /// <summary>Adds signed 32-bit columns with checked overflow.</summary>
    public static void Add(ReadOnlySpan<int> left, ReadOnlySpan<int> right, Span<int> destination) =>
        Binary(left, right, destination, subtract: false);

    /// <summary>Subtracts signed 32-bit columns with checked overflow.</summary>
    public static void Subtract(ReadOnlySpan<int> left, ReadOnlySpan<int> right, Span<int> destination) =>
        Binary(left, right, destination, subtract: true);

    /// <summary>Adds signed 64-bit columns with checked overflow.</summary>
    public static void Add(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> destination) =>
        Binary(left, right, destination, subtract: false);

    /// <summary>Subtracts signed 64-bit columns with checked overflow.</summary>
    public static void Subtract(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> destination) =>
        Binary(left, right, destination, subtract: true);

    /// <summary>Multiplies every signed 32-bit value by a scalar with checked overflow.</summary>
    public static void Multiply(ReadOnlySpan<int> values, int factor, Span<int> destination)
    {
        ValidateDestination(values.Length, destination.Length);
        ValidateMultiply(values, factor);
        var target = destination[..values.Length];
        if (!RequiresTemporary(values, target))
        {
            WriteMultiply(values, factor, target);
            return;
        }

        var temporary = ArrayPool<int>.Shared.Rent(values.Length);
        try
        {
            var output = temporary.AsSpan(0, values.Length);
            WriteMultiply(values, factor, output);
            output.CopyTo(target);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(temporary);
        }
    }

    /// <summary>
    /// Sums signed 32-bit values exactly into a signed 64-bit result. Any span's maximum possible
    /// length and the range of its elements guarantee that the mathematical result fits in 64 bits.
    /// </summary>
    public static long Sum(ReadOnlySpan<int> values)
    {
        var index = 0;
        long result = 0;
        if (Vector.IsHardwareAccelerated && values.Length >= Vector<int>.Count)
        {
            var lowSum = Vector<long>.Zero;
            var highSum = Vector<long>.Zero;
            var width = Vector<int>.Count;
            for (; index <= values.Length - width; index += width)
            {
                Vector.Widen(new Vector<int>(values.Slice(index, width)), out Vector<long> low, out Vector<long> high);
                lowSum += low;
                highSum += high;
            }
            for (var lane = 0; lane < Vector<long>.Count; lane++)
            {
                result += lowSum[lane] + highSum[lane];
            }
        }
        for (; index < values.Length; index++)
        {
            result += values[index];
        }

        return result;
    }

    private static void Binary<T>(ReadOnlySpan<T> left, ReadOnlySpan<T> right, Span<T> destination, bool subtract)
        where T : unmanaged, IBinaryInteger<T>
    {
        if (left.Length != right.Length)
        {
            throw new ArgumentException("Input column lengths must match.", nameof(right));
        }

        ValidateDestination(left.Length, destination.Length);
        ValidateBinary(left, right, subtract);
        var target = destination[..left.Length];
        if (!RequiresTemporary(left, target) && !RequiresTemporary(right, target))
        {
            WriteBinary(left, right, target, subtract);
            return;
        }

        var temporary = ArrayPool<T>.Shared.Rent(left.Length);
        try
        {
            var output = temporary.AsSpan(0, left.Length);
            WriteBinary(left, right, output, subtract);
            output.CopyTo(target);
        }
        finally
        {
            ArrayPool<T>.Shared.Return(temporary);
        }
    }

    private static void ValidateBinary<T>(ReadOnlySpan<T> left, ReadOnlySpan<T> right, bool subtract)
        where T : unmanaged, IBinaryInteger<T>
    {
        var index = 0;
        if (Vector.IsHardwareAccelerated && left.Length >= Vector<T>.Count)
        {
            var width = Vector<T>.Count;
            for (; index <= left.Length - width; index += width)
            {
                var a = new Vector<T>(left.Slice(index, width));
                var b = new Vector<T>(right.Slice(index, width));
                var result = subtract ? a - b : a + b;
                var overflow = subtract ? (a ^ b) & (a ^ result) : (a ^ result) & (b ^ result);
                if (Vector.LessThanAny(overflow, Vector<T>.Zero))
                {
                    throw new OverflowException();
                }
            }
        }
        for (; index < left.Length; index++)
        {
            _ = subtract ? checked(left[index] - right[index]) : checked(left[index] + right[index]);
        }
    }

    private static void WriteBinary<T>(ReadOnlySpan<T> left, ReadOnlySpan<T> right, Span<T> destination, bool subtract)
        where T : unmanaged, IBinaryInteger<T>
    {
        var index = 0;
        if (Vector.IsHardwareAccelerated && left.Length >= Vector<T>.Count)
        {
            var width = Vector<T>.Count;
            for (; index <= left.Length - width; index += width)
            {
                var a = new Vector<T>(left.Slice(index, width));
                var b = new Vector<T>(right.Slice(index, width));
                (subtract ? a - b : a + b).CopyTo(destination.Slice(index, width));
            }
        }
        for (; index < left.Length; index++)
        {
            destination[index] = subtract ? unchecked(left[index] - right[index]) : unchecked(left[index] + right[index]);
        }
    }

    private static void ValidateMultiply(ReadOnlySpan<int> values, int factor)
    {
        var index = 0;
        if (Vector.IsHardwareAccelerated && values.Length >= Vector<int>.Count)
        {
            var width = Vector<int>.Count;
            var factor64 = new Vector<long>(factor);
            var min = new Vector<long>(int.MinValue);
            var max = new Vector<long>(int.MaxValue);
            for (; index <= values.Length - width; index += width)
            {
                Vector.Widen(new Vector<int>(values.Slice(index, width)), out Vector<long> low, out Vector<long> high);
                low *= factor64;
                high *= factor64;
                if (Vector.LessThanAny(low, min) || Vector.GreaterThanAny(low, max)
                    || Vector.LessThanAny(high, min) || Vector.GreaterThanAny(high, max))
                {
                    throw new OverflowException();
                }
            }
        }
        for (; index < values.Length; index++)
        {
            _ = checked(values[index] * factor);
        }
    }

    private static void WriteMultiply(ReadOnlySpan<int> values, int factor, Span<int> destination)
    {
        var index = 0;
        if (Vector.IsHardwareAccelerated && values.Length >= Vector<int>.Count)
        {
            var width = Vector<int>.Count;
            var multiplier = new Vector<int>(factor);
            for (; index <= values.Length - width; index += width)
            {
                (new Vector<int>(values.Slice(index, width)) * multiplier).CopyTo(destination.Slice(index, width));
            }
        }
        for (; index < values.Length; index++)
        {
            destination[index] = unchecked(values[index] * factor);
        }
    }

    private static void ValidateDestination(int inputLength, int destinationLength)
    {
        if (destinationLength < inputLength)
        {
            throw new ArgumentException("The destination is shorter than the input column.", "destination");
        }
    }

    private static bool RequiresTemporary<T>(ReadOnlySpan<T> source, Span<T> destination) where T : unmanaged
    {
        ReadOnlySpan<T> target = destination;
        // The overload returning an element offset rejects byte-misaligned overlap. Comparing
        // first references instead also handles spans constructed over unaligned byte slices.
        return source.Overlaps(target)
            && !Unsafe.AreSame(ref MemoryMarshal.GetReference(source), ref MemoryMarshal.GetReference(target));
    }
}
