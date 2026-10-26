namespace Systemic.Kernel;

public readonly record struct PopulationHandle<TKind> where TKind : IPopulationKind
{
    internal PopulationHandle(int slot, ulong generation, long ownerToken)
    {
        Slot = slot;
        Generation = generation;
        OwnerToken = ownerToken;
    }

    public int Slot { get; }

    public ulong Generation { get; }

    internal long OwnerToken { get; }
}
