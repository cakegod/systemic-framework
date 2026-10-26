namespace Systemic.Kernel;

/// <summary>Portable, versioned state for all dense populations registered in one world.</summary>
public sealed record PopulationWorldState(int Version, PopulationSaveState[] Populations)
{
    public const int CurrentVersion = 1;
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
