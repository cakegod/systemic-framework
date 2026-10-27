using System.Text;

namespace Systemic.Kernel;

/// <summary>
/// A versioned counter-based random source for a particular seed, system, entity and tick.
/// Draws are addressed explicitly and have no mutable cursor, so execution order cannot change
/// their values. This generator is for simulation, not cryptography.
/// </summary>
/// <remarks>
/// Version 1 hashes the exact UTF-8 system identifier with FNV-1a-64, without case folding or
/// Unicode normalization. It then repeatedly applies the SplitMix64 finalizer to
/// <c>seed XOR 0xD1B54A32D192ED03</c>, followed in order by XORs with system hash, entity id,
/// tick and draw index (one finalizer after each XOR). All operations wrap modulo 2^64. Persist
/// the seed, system identifier, stable entity id, tick, draw indexes and algorithm version when
/// replaying; do not change identifier spelling or reuse an index for independent draws.
/// </remarks>
public readonly struct DeterministicRandom
{
    /// <summary>The replay algorithm version for this implementation.</summary>
    public const int AlgorithmVersion = 1;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ulong _state;

    /// <summary>Creates an immutable random source for one stable system/entity/tick tuple.</summary>
    public DeterministicRandom(ulong seed, string systemId, ulong entityId, ulong tick)
        : this(seed, HashSystemId(systemId), entityId, tick)
    {
    }

    internal DeterministicRandom(ulong seed, ulong systemHash, ulong entityId, ulong tick)
    {
        var state = Mix(seed ^ 0xD1B54A32D192ED03UL);
        state = Mix(state ^ systemHash);
        state = Mix(state ^ entityId);
        _state = Mix(state ^ tick);
    }

    /// <summary>
    /// Hashes the exact UTF-8 bytes using FNV-1a-64. Empty identifiers and invalid UTF-16 are
    /// rejected; distinct Unicode spellings remain distinct even when visually equivalent.
    /// </summary>
    public static ulong HashSystemId(string systemId)
    {
        ArgumentException.ThrowIfNullOrEmpty(systemId);
        var bytes = StrictUtf8.GetBytes(systemId);
        unchecked
        {
            var hash = 0xCBF29CE484222325UL;
            foreach (var b in bytes) hash = (hash ^ b) * 0x100000001B3UL;
            return hash;
        }
    }

    /// <summary>Returns the reproducible 64-bit value at a caller-chosen draw index.</summary>
    public ulong At(ulong drawIndex) => Mix(_state ^ drawIndex);

    /// <summary>Returns a Q32.32 value uniformly distributed over [0, 1) using the top 32 bits.</summary>
    public Fixed64 UnitFixed(ulong drawIndex) => Fixed64.FromRaw((long)(At(drawIndex) >> 32));

    private static ulong Mix(ulong value)
    {
        unchecked
        {
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }
}

/// <summary>Caches the system identifier so per-entity draws need no allocation or string hashing.</summary>
public readonly struct DeterministicRandomFactory
{
    private readonly ulong _seed, _systemHash;
    private readonly bool _initialized;

    public DeterministicRandomFactory(ulong seed, string systemId)
    {
        _seed = seed;
        _systemHash = DeterministicRandom.HashSystemId(systemId);
        _initialized = true;
    }

    public DeterministicRandom ForEntity(ulong entityId, ulong tick)
    {
        if (!_initialized) throw new InvalidOperationException("The deterministic random factory is not initialized.");
        return new DeterministicRandom(_seed, _systemHash, entityId, tick);
    }
}
