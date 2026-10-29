namespace Systemic.Kernel;

/// <summary>Portable, versioned state for all dense populations registered in one world.</summary>
/// <remarks>
/// <paramref name="HandleGeneration"/> is the world's next handle generation. It is saved because
/// generations consumed by rolled-back spawns leave no slot behind, so a load cannot derive it
/// from the saved slots alone. Version 1 saves omit it and fall back to the largest saved slot.
/// </remarks>
public sealed record PopulationWorldState(
    int Version,
    PopulationSaveState[] Populations,
    long HandleGeneration = 0)
{
    public const int CurrentVersion = 2;
    public const int OldestSupportedVersion = 1;
}

/// <summary>Portable row, handle, and column data for one registered population kind.</summary>
public sealed record PopulationSaveState(
    string Id,
    int Version,
    int RowCount,
    bool HandlesEnabled,
    int[] RowToHandleSlot,
    PopulationHandleSlotState[] HandleSlots,
    int[] FreeHandleSlots,
    PopulationColumnSaveState[] Columns);

/// <summary>One handle slot. Row -1 marks a free slot.</summary>
public readonly record struct PopulationHandleSlotState(int Row, ulong Generation);

/// <summary>Codec-produced compact bytes for one population column.</summary>
public sealed record PopulationColumnSaveState(
    string Id,
    int Version,
    string CodecId,
    int CodecVersion,
    int ElementSize,
    bool IsDoubleBuffered,
    byte[] Data);
