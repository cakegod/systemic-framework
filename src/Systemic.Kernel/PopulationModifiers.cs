using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Systemic.Kernel;

/// <summary>One additive modifier applied to a row column, at a fixed position in the row's slot block.</summary>
/// <remarks>
/// A zero <see cref="Key"/> marks an unused slot. <see cref="ExpiryTick"/> is absolute and the
/// entity convention applies: -1 never expires, and a non-negative value expires at that tick.
/// </remarks>
public struct RowModifierSlot
{
    /// <summary>Gets the key that marks an unused slot. Declared modifier properties start at one.</summary>
    public const ushort EmptyKey = 0;

    /// <summary>Gets or sets the declared modifier property key.</summary>
    public ushort Key;

    /// <summary>Gets or sets the additive amount.</summary>
    public float Value;

    /// <summary>Gets or sets the absolute expiry tick, or -1 for a modifier that never expires.</summary>
    public int ExpiryTick;

    public RowModifierSlot(ushort key, float value, int expiryTick)
    {
        Key = key;
        Value = value;
        ExpiryTick = expiryTick;
    }

    /// <summary>Gets whether the slot holds a live modifier at the given logical tick.</summary>
    public readonly bool IsActive(int currentTick) =>
        Key != EmptyKey && (ExpiryTick < 0 || ExpiryTick > currentTick);

    public override readonly string ToString() => $"{Key}:{Value}@{ExpiryTick}";
}

/// <summary>Eight modifier slots stored inline for every live row.</summary>
/// <remarks>
/// The block is a fixed layout rather than a growable collection: a row that carries modifiers
/// pays the same 96 bytes as one that does not, and no row ever allocates to gain a modifier.
/// </remarks>
[InlineArray(RowModifiers.SlotsPerRow)]
public struct ModifierSlot8
{
    private RowModifierSlot _slot0;
}

/// <summary>Reads and writes the inline <see cref="ModifierSlot8"/> block of one row.</summary>
public static class RowModifiers
{
    /// <summary>Gets the number of modifiers one row can carry at once.</summary>
    public const int SlotsPerRow = 8;

    /// <summary>Gets the slots of one row as a mutable span.</summary>
    /// <param name="block">A row's slot block, by reference.</param>
    /// <remarks>The span aliases the block; it must not outlive the row read it came from.</remarks>
    public static Span<RowModifierSlot> AsSpan(ref ModifierSlot8 block) =>
        MemoryMarshal.CreateSpan(ref Unsafe.As<ModifierSlot8, RowModifierSlot>(ref block), SlotsPerRow);

    /// <summary>Gets the slots of one row as a read-only span.</summary>
    public static ReadOnlySpan<RowModifierSlot> AsReadOnlySpan(ref ModifierSlot8 block) => AsSpan(ref block);
}

/// <summary>Encodes rows of modifier slots with a fixed, versioned, little-endian layout.</summary>
public sealed class PopulationModifierCodec : IPopulationCodec<ModifierSlot8>
{
    /// <summary>Gets the shared codec instance.</summary>
    public static PopulationModifierCodec Instance { get; } = new();

    private const int EncodedSlotSize = 10;

    private PopulationModifierCodec()
    {
    }

    public string Id => "systemic.modifier-slot8";

    public int Version => 1;

    public int ElementSize => RowModifiers.SlotsPerRow * EncodedSlotSize;

    public int GetEncodedLength(int valueCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(valueCount);
        return checked(valueCount * ElementSize);
    }

    public void Encode(ReadOnlySpan<ModifierSlot8> values, Span<byte> destination)
    {
        if (destination.Length != GetEncodedLength(values.Length))
        {
            throw new ArgumentException("The destination length does not match the value count.", nameof(destination));
        }

        for (var row = 0; row < values.Length; row++)
        {
            var block = values[row];
            var slots = RowModifiers.AsReadOnlySpan(ref block);
            var offset = row * ElementSize;
            for (var index = 0; index < slots.Length; index++)
            {
                var slot = slots[index];
                var slotOffset = offset + (index * EncodedSlotSize);
                BinaryPrimitives.WriteUInt16LittleEndian(destination[slotOffset..], slot.Key);
                BinaryPrimitives.WriteInt32LittleEndian(destination[(slotOffset + 2)..], BitConverter.SingleToInt32Bits(slot.Value));
                BinaryPrimitives.WriteInt32LittleEndian(destination[(slotOffset + 6)..], slot.ExpiryTick);
            }
        }
    }

    public void Decode(ReadOnlySpan<byte> source, Span<ModifierSlot8> values)
    {
        if (source.Length != GetEncodedLength(values.Length))
        {
            throw new ArgumentException("The source length does not match the value count.", nameof(source));
        }

        for (var row = 0; row < values.Length; row++)
        {
            var offset = row * ElementSize;
            var block = default(ModifierSlot8);
            var slots = RowModifiers.AsSpan(ref block);
            for (var index = 0; index < slots.Length; index++)
            {
                var slotOffset = offset + (index * EncodedSlotSize);
                var key = BinaryPrimitives.ReadUInt16LittleEndian(source[slotOffset..]);
                var value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(source[(slotOffset + 2)..]));
                var expiryTick = BinaryPrimitives.ReadInt32LittleEndian(source[(slotOffset + 6)..]);
                if (expiryTick < -1)
                {
                    throw new FormatException("A row modifier expiry tick must be -1 or a logical tick.");
                }

                slots[index] = new RowModifierSlot(key, value, expiryTick);
            }

            values[row] = block;
        }
    }
}
