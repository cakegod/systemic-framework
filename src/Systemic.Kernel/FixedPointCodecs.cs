using System.Buffers.Binary;

namespace Systemic.Kernel;

/// <summary>Encodes <see cref="Fixed64"/> values as their exact signed Q32.32 bits, little-endian.</summary>
/// <remarks>
/// The persisted form is the raw value, never a decimal rendering: a save round trip must not depend
/// on decimal formatting, and the raw bits are what every platform computes identically.
/// </remarks>
public sealed class Fixed64Codec : IPopulationCodec<Fixed64>
{
    /// <summary>Gets the shared codec instance.</summary>
    public static Fixed64Codec Instance { get; } = new();

    private Fixed64Codec()
    {
    }

    public string Id => "systemic.fixed64.q32.32";

    public int Version => 1;

    public int ElementSize => sizeof(long);

    public int GetEncodedLength(int valueCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(valueCount);
        return checked(valueCount * ElementSize);
    }

    public void Encode(ReadOnlySpan<Fixed64> values, Span<byte> destination)
    {
        if (destination.Length != GetEncodedLength(values.Length))
        {
            throw new ArgumentException("The destination length does not match the value count.", nameof(destination));
        }

        for (var index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(destination[(index * ElementSize)..], values[index].RawValue);
        }
    }

    public void Decode(ReadOnlySpan<byte> source, Span<Fixed64> values)
    {
        if (source.Length != GetEncodedLength(values.Length))
        {
            throw new ArgumentException("The source length does not match the value count.", nameof(source));
        }

        for (var index = 0; index < values.Length; index++)
        {
            values[index] = Fixed64.FromRaw(BinaryPrimitives.ReadInt64LittleEndian(source[(index * ElementSize)..]));
        }
    }
}

/// <summary>Encodes <see cref="FixedVector2"/> values as two exact signed Q32.32 components.</summary>
/// <remarks>X precedes Y, each little-endian, so the layout is stable and byte-comparable.</remarks>
public sealed class FixedVector2Codec : IPopulationCodec<FixedVector2>
{
    /// <summary>Gets the shared codec instance.</summary>
    public static FixedVector2Codec Instance { get; } = new();

    private FixedVector2Codec()
    {
    }

    public string Id => "systemic.fixed-vector2.q32.32";

    public int Version => 1;

    public int ElementSize => sizeof(long) * 2;

    public int GetEncodedLength(int valueCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(valueCount);
        return checked(valueCount * ElementSize);
    }

    public void Encode(ReadOnlySpan<FixedVector2> values, Span<byte> destination)
    {
        if (destination.Length != GetEncodedLength(values.Length))
        {
            throw new ArgumentException("The destination length does not match the value count.", nameof(destination));
        }

        for (var index = 0; index < values.Length; index++)
        {
            var offset = index * ElementSize;
            BinaryPrimitives.WriteInt64LittleEndian(destination[offset..], values[index].X.RawValue);
            BinaryPrimitives.WriteInt64LittleEndian(destination[(offset + sizeof(long))..], values[index].Y.RawValue);
        }
    }

    public void Decode(ReadOnlySpan<byte> source, Span<FixedVector2> values)
    {
        if (source.Length != GetEncodedLength(values.Length))
        {
            throw new ArgumentException("The source length does not match the value count.", nameof(source));
        }

        for (var index = 0; index < values.Length; index++)
        {
            var offset = index * ElementSize;
            values[index] = new FixedVector2(
                Fixed64.FromRaw(BinaryPrimitives.ReadInt64LittleEndian(source[offset..])),
                Fixed64.FromRaw(BinaryPrimitives.ReadInt64LittleEndian(source[(offset + sizeof(long))..])));
        }
    }
}
