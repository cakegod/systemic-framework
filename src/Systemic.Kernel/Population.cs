using System.Threading;

namespace Systemic.Kernel;

/// <summary>Identifies a population schema by its stable ID and version.</summary>
public interface IPopulationKind
{
    /// <summary>Gets the stable schema identifier persisted with the population.</summary>
    static abstract string Id { get; }

    /// <summary>Gets the schema version persisted with the population.</summary>
    static abstract int Version { get; }

    /// <summary>Gets the tag names this schema reserves mask bits for, in bit order.</summary>
    /// <remarks>
    /// A row rule may only add or test a tag name listed here. The position in this list is the bit
    /// the saved mask uses, so the order is part of the persisted schema: append new names and bump
    /// <see cref="Version"/> rather than reordering or removing them. At most
    /// <see cref="Population{TKind}.MaximumTags"/> names are allowed.
    /// </remarks>
    static virtual string[] Tags => [];

    /// <summary>Gets the property names this schema accepts row modifiers for, in key order.</summary>
    /// <remarks>
    /// A row modifier is stored inline in the row's fixed slot block, so the property must be listed
    /// here to be addressable: the order decides the slot key and is therefore part of the persisted
    /// schema. Each named property is also a numeric column of the same ID, which the modifier adds
    /// to when the effective value is read. At most <see cref="RowModifiers.SlotsPerRow"/> modifiers
    /// can be live on one row at a time.
    /// </remarks>
    static virtual string[] ModifierProperties => [];
}

/// <summary>
/// A generation-checked reference to one population row, small enough to travel inside a fact.
/// </summary>
/// <param name="PopulationId">The schema ID the row belongs to.</param>
/// <param name="Slot">The handle slot that identifies the row's lifetime.</param>
/// <param name="Generation">The generation of that slot when the reference was taken.</param>
/// <remarks>
/// A slot is reused after a despawn, so the generation is what makes the reference a statement about
/// a particular row and not about whatever row occupies the slot later. Resolution fails once the
/// referenced row is gone, which is how a reaction chain detects a despawn instead of retargeting.
/// </remarks>
public readonly record struct PopulationRowReference(string PopulationId, int Slot, ulong Generation)
{
    public override string ToString() => $"{PopulationId}[{Slot}:{Generation}]";
}

/// <summary>Describes a contiguous range of live population rows.</summary>
/// <param name="Start">Zero-based index of the first row.</param>
/// <param name="Length">Number of rows in the range.</param>
public readonly record struct PopulationRange(int Start, int Length)
{
    /// <summary>Gets the first row after this range.</summary>
    public int End => checked(Start + Length);
}

/// <summary>Stores dense columns for one population kind in a World.</summary>
/// <remarks>Column data is available only through synchronous callback-scoped access. Leases prevent structural changes and transaction boundaries until disposed.</remarks>
public sealed class Population<TKind> where TKind : IPopulationKind
{
    /// <summary>Gets the number of tags a schema may reserve, one per bit of its mask column.</summary>
    public const int MaximumTags = 64;

    /// <summary>Gets whether this population issues generation-checked handles.</summary>
    public bool HandlesEnabled => _handlesEnabled;

    private static readonly PopulationColumnKey<ulong> s_tagMaskKey =
        new("systemic.tags", 1, PopulationCodecs.UInt64);

    private static readonly PopulationColumnKey<ModifierSlot8> s_modifierKey =
        new("systemic.modifiers", 1, PopulationModifierCodec.Instance);

    private static long s_nextOwnerToken;
    private static long s_nextHandleGeneration;

    private readonly PopulationStorage _storage;
    private readonly Dictionary<string, PopulationColumnBase> _columns = new(StringComparer.Ordinal);
    private readonly string[] _tagNames;
    private readonly Dictionary<string, int> _tagBits = new(StringComparer.Ordinal);
    private readonly string[] _modifierProperties;
    private readonly Dictionary<string, ushort> _modifierKeys = new(StringComparer.Ordinal);
    private PopulationColumn<ulong>? _tagMask;
    private PopulationColumn<ModifierSlot8>? _modifierSlots;
    private readonly bool _handlesEnabled;
    private readonly long _ownerToken;
    private readonly List<HandleSlot> _handleSlots = [];
    private readonly Stack<int> _freeHandleSlots = new();
    private readonly Stack<PopulationStructureUndoBatch> _freeStructureUndoBatches = new();
    private int[] _rowToHandleSlot = [];
    private int _capacity;

    internal Population(World world, bool handlesEnabled, long? ownerToken = null)
    {
        World = world;
        _storage = new PopulationStorage(world);
        _handlesEnabled = handlesEnabled;
        _ownerToken = ownerToken ?? Interlocked.Increment(ref s_nextOwnerToken);
        // Copied: the mask order is persisted schema, so a schema must not be able to mutate it later.
        _tagNames = [.. TKind.Tags];
        if (_tagNames.Length > MaximumTags)
        {
            throw new InvalidOperationException(
                $"Population '{TKind.Id}' declares {_tagNames.Length} tags; a schema may declare at most {MaximumTags}.");
        }

        for (var bit = 0; bit < _tagNames.Length; bit++)
        {
            var tag = _tagNames[bit];
            if (string.IsNullOrWhiteSpace(tag))
            {
                throw new InvalidOperationException(
                    $"Population '{TKind.Id}' declares a blank tag name at bit {bit}.");
            }

            if (!_tagBits.TryAdd(tag, bit))
            {
                throw new InvalidOperationException(
                    $"Population '{TKind.Id}' declares tag '{tag}' more than once.");
            }
        }

        _modifierProperties = [.. TKind.ModifierProperties];
        if (_modifierProperties.Length > ushort.MaxValue - 1)
        {
            throw new InvalidOperationException(
                $"Population '{TKind.Id}' declares more than {ushort.MaxValue - 1} modifier properties.");
        }

        for (var index = 0; index < _modifierProperties.Length; index++)
        {
            var property = _modifierProperties[index];
            if (string.IsNullOrWhiteSpace(property))
            {
                throw new InvalidOperationException(
                    $"Population '{TKind.Id}' declares a blank modifier property at key {index + 1}.");
            }

            if (!_modifierKeys.TryAdd(property, (ushort)(index + 1)))
            {
                throw new InvalidOperationException(
                    $"Population '{TKind.Id}' declares modifier property '{property}' more than once.");
            }
        }

        // The mask and modifier columns are created with the schema, not on first use: a save's
        // column set must be reproducible from the schema alone, or a load cannot match it before
        // any rule binds.
        _tagMask = _tagNames.Length > 0 ? Column(s_tagMaskKey) : null;
        _modifierSlots = _modifierProperties.Length > 0 ? Column(s_modifierKey) : null;
    }

    internal World World { get; private set; }

    /// <summary>Leases this population's row identity until the returned lease is disposed.</summary>
    /// <remarks>Column data is available only through synchronous callback-scoped access.</remarks>
    public PopulationLease<TKind> Borrow()
    {
        var systemAccess = World.GetCurrentSystemAccess();
        PopulationLease<TKind> lease;
        lock (World.TransactionGate)
        {
            EnsureActive();
            lease = new PopulationLease<TKind>(this, _storage);
            _storage.AcquireLease();
        }

        if (systemAccess is not null)
            lease.BindSystemAccess(systemAccess, Environment.CurrentManagedThreadId);
        return lease;
    }

    /// <summary>Runs serial work with callback-scoped access without creating a separate lease object.</summary>
    /// <typeparam name="TState">Caller-owned state passed to the callback.</typeparam>
    /// <param name="range">The only rows this callback may write.</param>
    /// <param name="state">State passed to the callback.</param>
    /// <param name="callback">Synchronous access callback.</param>
    /// <remarks>Inside a scheduled system, this method uses the system's declared per-column access.</remarks>
    public void WithAccess<TState>(
        PopulationRange range,
        TState state,
        PopulationAccessAction<TKind, TState> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var access = World.GetCurrentSystemAccess();
        var lifetime = _storage.SerialScopeLifetime;
        long generation;
        lock (World.TransactionGate)
        {
            EnsureActive();
            _storage.ValidateRange(range.Start, range.Length);
            _storage.AcquireLease();
            var scopeEntered = false;
            try
            {
                _storage.EnterScope(parallel: false, group: null);
                scopeEntered = true;
                generation = lifetime.Activate(_storage, range, access, parallel: false, group: null);
            }
            catch
            {
                if (scopeEntered)
                    _storage.ExitScope(parallel: false);
                _storage.ReleaseLease();
                throw;
            }
        }

        var scope = new PopulationAccessScope<TKind>(this, lifetime, generation);
        try
        {
            callback(state, ref scope);
        }
        finally
        {
            lock (World.TransactionGate)
            {
                lifetime.Deactivate(generation);
                _storage.ExitScope(parallel: false);
                _storage.ReleaseLease();
            }
        }
    }

    /// <summary>Runs serial initialization with caller-owned input and callback-scoped population access.</summary>
    /// <typeparam name="TInput">The unmanaged input row type.</typeparam>
    /// <typeparam name="TState">Caller-owned state passed to the callback.</typeparam>
    /// <param name="range">The only rows this callback may write.</param>
    /// <param name="input">Read-only input whose lifetime is limited to the callback.</param>
    /// <param name="state">State passed to the callback.</param>
    /// <param name="callback">Synchronous access callback.</param>
    public void WithAccess<TInput, TState>(
        PopulationRange range,
        scoped ReadOnlySpan<TInput> input,
        TState state,
        PopulationInputAccessAction<TKind, TInput, TState> callback)
        where TInput : unmanaged
    {
        ArgumentNullException.ThrowIfNull(callback);
        var access = World.GetCurrentSystemAccess();
        var lifetime = _storage.SerialScopeLifetime;
        long generation;
        lock (World.TransactionGate)
        {
            EnsureActive();
            _storage.ValidateRange(range.Start, range.Length);
            _storage.AcquireLease();
            var scopeEntered = false;
            try
            {
                _storage.EnterScope(parallel: false, group: null);
                scopeEntered = true;
                generation = lifetime.Activate(_storage, range, access, parallel: false, group: null);
            }
            catch
            {
                if (scopeEntered)
                    _storage.ExitScope(parallel: false);
                _storage.ReleaseLease();
                throw;
            }
        }

        var scope = new PopulationAccessScope<TKind>(this, lifetime, generation);
        try
        {
            callback(input, state, ref scope);
        }
        finally
        {
            lock (World.TransactionGate)
            {
                lifetime.Deactivate(generation);
                _storage.ExitScope(parallel: false);
                _storage.ReleaseLease();
            }
        }
    }

    internal object WorldIdentity => World;

    internal PopulationStorage StorageIdentity => _storage;

    internal int BeginScheduledRangeAccess()
    {
        lock (World.TransactionGate)
        {
            EnsureActive();
            _storage.AcquireLease();
            return _storage.Count;
        }
    }

    internal void EndScheduledRangeAccess()
    {
        lock (World.TransactionGate)
            _storage.ReleaseLease();
    }

    internal long BeginScheduledRangeScope(
        PopulationRange range,
        PopulationScopeLifetime lifetime,
        SystemAccess[] access,
        object group)
    {
        lock (World.TransactionGate)
        {
            EnsureActive();
            _storage.ValidateRange(range.Start, range.Length);
            _storage.EnterScope(parallel: true, group);
            try
            {
                return lifetime.Activate(_storage, range, access, parallel: true, group);
            }
            catch
            {
                _storage.ExitScope(parallel: true);
                throw;
            }
        }
    }

    internal void EndScheduledRangeScope(PopulationScopeLifetime lifetime, long generation)
    {
        lock (World.TransactionGate)
        {
            lifetime.Deactivate(generation);
            _storage.ExitScope(parallel: true);
        }
    }

    internal void ValidateScopedColumn(PopulationColumnBase column)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(column);
        if (!Owns(column))
            throw new ArgumentException("The column does not belong to this population.", nameof(column));
    }

    internal bool Owns(PopulationColumnBase column) =>
        _columns.TryGetValue(column.Id, out var existing) && ReferenceEquals(existing, column);

    /// <summary>Gets the number of live rows.</summary>
    public int Count
    {
        get
        {
            EnsureActive();
            return _storage.Count;
        }
    }

    /// <summary>Gets the reserved row capacity.</summary>
    public int Capacity
    {
        get
        {
            EnsureActive();
            return _capacity;
        }
    }

    /// <summary>Reserves row storage without changing the live row count.</summary>
    public void Reserve(int capacity)
    {
        lock (World.TransactionGate)
        {
            EnsureActive();
            ArgumentOutOfRangeException.ThrowIfNegative(capacity);
            if (capacity <= _capacity)
            {
                return;
            }

            _storage.EnsureNoLeases();
            CaptureStructureUndo();
            SetCapacity(capacity);
        }
    }

    /// <summary>Adds initialized rows and returns their contiguous range.</summary>
    /// <remarks>Use generated schema initialization or a command buffer when new rows need values.</remarks>
    public PopulationRange AddRange(int count)
    {
        lock (World.TransactionGate)
        {
            EnsureActive();
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (count > int.MaxValue - Count)
            {
                throw new ArgumentOutOfRangeException(nameof(count), "The range exceeds the maximum population row count.");
            }

            var start = Count;
            if (count == 0)
            {
                return new PopulationRange(start, 0);
            }

            _storage.EnsureNoLeases();
            CaptureStructureUndo();
            var requiredCapacity = start + count;
            if (requiredCapacity > _capacity)
            {
                SetCapacity(GrowCapacity(requiredCapacity));
            }

            foreach (var column in _columns.Values)
            {
                column.ClearRange(start, count);
            }

            if (_handlesEnabled)
            {
                for (var row = start; row < requiredCapacity; row++)
                {
                    _rowToHandleSlot[row] = AllocateHandle(row);
                }
            }

            _storage.Count = requiredCapacity;
            return new PopulationRange(start, count);
        }
    }

    /// <summary>Removes a row by moving the final row into its position.</summary>
    public void RemoveSwapBack(int row)
    {
        lock (World.TransactionGate)
        {
            EnsureActive();
            ArgumentOutOfRangeException.ThrowIfNegative(row);
            if (row >= Count)
            {
                throw new ArgumentOutOfRangeException(nameof(row), row, "The row is outside the live population.");
            }

            _storage.EnsureNoLeases();
            CaptureStructureUndo();
            var lastRow = Count - 1;
            foreach (var column in _columns.Values)
            {
                column.MoveRow(lastRow, row);
            }

            if (_handlesEnabled)
            {
                var removedSlot = _rowToHandleSlot[row];
                if (row != lastRow)
                {
                    var movedSlot = _rowToHandleSlot[lastRow];
                    _rowToHandleSlot[row] = movedSlot;
                    var moved = _handleSlots[movedSlot];
                    moved.Row = row;
                    _handleSlots[movedSlot] = moved;
                }

                _rowToHandleSlot[lastRow] = -1;
                ReleaseHandle(removedSlot);
            }

            _storage.Count = lastRow;
        }
    }

    /// <summary>Looks up a column by its stable ID without knowing its value type.</summary>
    internal bool TryGetColumn(string columnId, out PopulationColumnBase column)
    {
        EnsureActive();
        return _columns.TryGetValue(columnId, out column!);
    }

    /// <summary>Gets or creates a single-buffered column with the supplied stable key.</summary>
    public PopulationColumn<T> Column<T>(PopulationColumnKey<T> key) where T : unmanaged
    {
        lock (World.TransactionGate)
        {
            EnsureActive();
            ArgumentNullException.ThrowIfNull(key);
            if (_columns.TryGetValue(key.Id, out var existing))
            {
                if (existing is PopulationColumn<T> column && MatchesDefinition(existing, key, isDoubleBuffered: false))
                {
                    return column;
                }

                throw new InvalidOperationException($"Population column ID '{key.Id}' is already defined with a different schema.");
            }

            _storage.EnsureNoLeases();
            var created = new PopulationColumn<T>(_storage, key);
            created.EnsureCapacity(_capacity);
            _columns.Add(key.Id, created);
            return created;
        }
    }

    /// <summary>Gets or creates a double-buffered column with the supplied stable key.</summary>
    public DoubleBufferedPopulationColumn<T> DoubleBufferedColumn<T>(PopulationColumnKey<T> key) where T : unmanaged
    {
        lock (World.TransactionGate)
        {
            EnsureActive();
            ArgumentNullException.ThrowIfNull(key);
            if (_columns.TryGetValue(key.Id, out var existing))
            {
                if (existing is DoubleBufferedPopulationColumn<T> column && MatchesDefinition(existing, key, isDoubleBuffered: true))
                {
                    return column;
                }

                throw new InvalidOperationException($"Population column ID '{key.Id}' is already defined with a different schema.");
            }

            _storage.EnsureNoLeases();
            var created = new DoubleBufferedPopulationColumn<T>(_storage, key);
            created.EnsureCapacity(_capacity);
            _columns.Add(key.Id, created);
            return created;
        }
    }

    /// <summary>Gets the stable handle currently assigned to a live row.</summary>
    public PopulationHandle<TKind> GetHandle(int row)
    {
        EnsureActive();
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        if (row >= Count)
        {
            throw new ArgumentOutOfRangeException(nameof(row), row, "The row is outside the live population.");
        }

        if (!_handlesEnabled)
        {
            throw new InvalidOperationException("Handles are disabled for this population.");
        }

        var slotIndex = _rowToHandleSlot[row];
        var slot = _handleSlots[slotIndex];
        return new PopulationHandle<TKind>(slotIndex, slot.Generation, _ownerToken);
    }

    /// <summary>Creates a current-world handle from a persisted slot and generation pair.</summary>
    /// <summary>Recreates a handle from a persisted slot and generation pair when it is still live.</summary>
    public bool TryGetHandle(int slotIndex, ulong generation, out PopulationHandle<TKind> handle)
    {
        EnsureActive();
        handle = default;
        if (!_handlesEnabled || generation == 0 || (uint)slotIndex >= (uint)_handleSlots.Count)
        {
            return false;
        }

        var slot = _handleSlots[slotIndex];
        if (slot.Row < 0 || slot.Row >= Count || slot.Generation != generation)
        {
            return false;
        }

        handle = new PopulationHandle<TKind>(slotIndex, generation, _ownerToken);
        return true;
    }

    /// <summary>Resolves a current handle to its live row index.</summary>
    public bool TryResolve(PopulationHandle<TKind> handle, out int row)
    {
        EnsureActive();
        row = -1;
        if (!_handlesEnabled || handle.OwnerToken != _ownerToken
            || (uint)handle.Slot >= (uint)_handleSlots.Count)
        {
            return false;
        }

        var slot = _handleSlots[handle.Slot];
        if (slot.Row < 0 || slot.Generation != handle.Generation || slot.Row >= Count)
        {
            return false;
        }

        row = slot.Row;
        return true;
    }

    public bool TryRemove(PopulationHandle<TKind> handle)
    {
        if (!TryResolve(handle, out var row))
        {
            return false;
        }

        RemoveSwapBack(row);
        return true;
    }

    /// <summary>Gets a fact-safe, generation-checked reference to a live row.</summary>
    /// <exception cref="InvalidOperationException">Handles are disabled for this population.</exception>
    public PopulationRowReference ReferenceOf(int row)
    {
        var handle = GetHandle(row);
        return new PopulationRowReference(TKind.Id, handle.Slot, handle.Generation);
    }

    /// <summary>Resolves a slot and generation pair to a live row, or reports that the row is gone.</summary>
    internal bool TryResolveRow(int slotIndex, ulong generation, out int row)
    {
        if (!TryGetHandle(slotIndex, generation, out var handle))
        {
            row = -1;
            return false;
        }

        return TryResolve(handle, out row);
    }

    /// <summary>Gets the tag names this schema reserves mask bits for, in bit order.</summary>
    public ReadOnlyMemory<string> Tags => _tagNames;

    /// <summary>Gets whether a live row carries the named schema tag.</summary>
    /// <param name="row">The row slot.</param>
    /// <param name="tag">A tag name declared by the schema.</param>
    /// <returns><see langword="false"/> when the row is not live.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="tag"/> is not declared by the schema.</exception>
    public bool HasTag(int row, string tag) => TryHasTagBit(row, ResolveTagBit(tag));

    /// <summary>Adds a schema tag to a live row.</summary>
    /// <returns><see langword="false"/> when the row is not live or already carried the tag.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="tag"/> is not declared by the schema.</exception>
    public bool AddTag(int row, string tag) => TrySetTagBit(row, ResolveTagBit(tag), value: true);

    /// <summary>Removes a schema tag from a live row.</summary>
    /// <returns><see langword="false"/> when the row is not live or did not carry the tag.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="tag"/> is not declared by the schema.</exception>
    public bool RemoveTag(int row, string tag) => TrySetTagBit(row, ResolveTagBit(tag), value: false);

    /// <summary>Gets the property names this schema accepts row modifiers for, in key order.</summary>
    public ReadOnlyMemory<string> ModifierProperties => _modifierProperties;

    /// <summary>Gets the number of modifiers one row of this schema can carry at once.</summary>
    public static int MaximumRowModifiers => RowModifiers.SlotsPerRow;

    /// <summary>Adds an additive modifier to a live row's named property.</summary>
    /// <param name="row">The row slot.</param>
    /// <param name="property">A property declared in <see cref="IPopulationKind.ModifierProperties"/>.</param>
    /// <param name="value">The additive amount.</param>
    /// <param name="remainingTicks">Lifetime in ticks; a negative value never expires.</param>
    /// <returns><see langword="false"/> when the row is not live.</returns>
    /// <remarks>Setting the same property again replaces that row's existing modifier for it.</remarks>
    /// <exception cref="InvalidOperationException">The property is not declared, or the row already
    /// carries <see cref="MaximumRowModifiers"/> different modifiers.</exception>
    public bool SetModifier(int row, string property, double value, int remainingTicks = -1)
    {
        EnsureActive();
        if (!double.IsFinite(value) || !float.IsFinite((float)value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value,
                $"Row modifier values for population '{TKind.Id}' must be finite single-precision numbers.");
        }

        return TrySetRowModifier(row, ResolveModifierKey(property), value,
            World.ExpiryFromRemainingTicks(remainingTicks));
    }

    /// <summary>Reads a live row's column value with its active modifiers applied.</summary>
    /// <param name="row">The row slot.</param>
    /// <param name="property">A numeric column ID.</param>
    /// <returns>The column value plus every active modifier declared for that property.</returns>
    /// <exception cref="InvalidOperationException">The column is unknown or the row is not live.</exception>
    public double EffectiveValue(int row, string property)
    {
        EnsureActive();
        if (!_columns.TryGetValue(property, out var column)
            || !TryGetEffectiveRowValue(row, column, ResolveModifierKeyOrEmpty(property), out var value))
        {
            throw new InvalidOperationException(
                $"Population '{TKind.Id}' has no live row {row} with column '{property}'.");
        }

        return value;
    }

    internal ReadOnlySpan<string> TagNames => _tagNames;

    internal PopulationColumn<ulong>? MaskColumn => _tagMask;

    internal ReadOnlySpan<string> ModifierPropertyNames => _modifierProperties;

    internal PopulationColumn<ModifierSlot8>? ModifierColumn => _modifierSlots;

    internal bool TryGetModifierKey(string property, out ushort key) =>
        _modifierKeys.TryGetValue(property, out key);

    internal bool TrySetRowModifier(int row, ushort key, double value, int expiryTick)
    {
        EnsureActive();
        if (_modifierSlots is null || key == RowModifierSlot.EmptyKey || !_modifierSlots.TryReadRow(row, out var block))
        {
            return false;
        }

        var slots = RowModifiers.AsSpan(ref block);
        var currentTick = World.LogicalTick;
        var target = -1;
        var reusable = -1;
        for (var index = 0; index < slots.Length; index++)
        {
            var slot = slots[index];
            if (slot.Key == key)
            {
                target = index;
                break;
            }

            if (reusable < 0 && !slot.IsActive(currentTick))
            {
                reusable = index;
            }
        }

        target = target >= 0 ? target : reusable;
        if (target < 0)
        {
            throw new InvalidOperationException(
                $"Row {row} of population '{TKind.Id}' already carries {RowModifiers.SlotsPerRow} active modifiers "
                + "and has no free slot; expire or replace one before adding another.");
        }

        slots[target] = new RowModifierSlot(key, (float)value, expiryTick);
        return _modifierSlots.TryWriteRow(row, block);
    }

    internal bool TryGetEffectiveRowValue(int row, PopulationColumnBase column, ushort modifierKey, out double value)
    {
        EnsureActive();
        if (!column.TryGetRowValue(row, out value))
        {
            value = 0;
            return false;
        }

        if (_modifierSlots is null || modifierKey == RowModifierSlot.EmptyKey
            || !_modifierSlots.TryReadRow(row, out var block))
        {
            return true;
        }

        var slots = RowModifiers.AsReadOnlySpan(ref block);
        var currentTick = World.LogicalTick;
        for (var index = 0; index < slots.Length; index++)
        {
            var slot = slots[index];
            if (slot.Key == modifierKey && slot.IsActive(currentTick))
            {
                value += slot.Value;
            }
        }

        return true;
    }

    internal bool TryGetTagBit(string tag, out int bit) => _tagBits.TryGetValue(tag, out bit);

    internal bool TryHasTagBit(int row, int bit)
    {
        EnsureActive();
        ValidateTagBit(bit);
        return _tagMask is not null && _tagMask.TryReadRow(row, out var mask) && (mask & (1UL << bit)) != 0;
    }

    internal bool TrySetTagBit(int row, int bit, bool value)
    {
        EnsureActive();
        ValidateTagBit(bit);
        if (_tagMask is null || !_tagMask.TryReadRow(row, out var mask))
        {
            return false;
        }

        var updated = value ? mask | (1UL << bit) : mask & ~(1UL << bit);
        if (updated == mask)
        {
            return false;
        }

        return _tagMask.TryWriteRow(row, updated);
    }

    private ushort ResolveModifierKeyOrEmpty(string property) =>
        _modifierKeys.TryGetValue(property, out var key) ? key : RowModifierSlot.EmptyKey;

    private ushort ResolveModifierKey(string property)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(property);
        if (_modifierKeys.TryGetValue(property, out var key))
        {
            return key;
        }

        throw new InvalidOperationException(
            $"Population '{TKind.Id}' does not declare modifier property '{property}'. "
            + $"Declared properties: '{string.Join("', '", _modifierProperties)}'.");
    }

    private int ResolveTagBit(string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        if (_tagBits.TryGetValue(tag, out var bit))
        {
            return bit;
        }

        throw new InvalidOperationException(
            $"Population '{TKind.Id}' does not declare tag '{tag}'. Declared tags: '{string.Join("', '", _tagNames)}'.");
    }

    private void ValidateTagBit(int bit)
    {
        if ((uint)bit >= (uint)_tagNames.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(bit), bit, $"Population '{TKind.Id}' declares {_tagNames.Length} tags.");
        }
    }

    internal void ValidateRange(int start, int length) => _storage.ValidateRange(start, length);

    internal void EnsureActive() => _storage.EnsureActive();

    internal PopulationSaveState CaptureSaveState()
    {
        EnsureActive();
        var rowToHandleSlot = _handlesEnabled
            ? _rowToHandleSlot.AsSpan(0, Count).ToArray()
            : [];
        var handleSlots = _handlesEnabled
            ? _handleSlots.Select(slot => new PopulationHandleSlotState(slot.Row, slot.Generation)).ToArray()
            : [];
        var freeHandleSlots = _handlesEnabled ? _freeHandleSlots.ToArray() : [];
        var columns = _columns.Values.OrderBy(column => column.Id, StringComparer.Ordinal)
            .Select(column => column.CaptureSaveState(Count))
            .ToArray();
        return new PopulationSaveState(TKind.Id, TKind.Version, Count, _handlesEnabled,
            rowToHandleSlot, handleSlots, freeHandleSlots, columns);
    }

    internal Action PrepareRestore(PopulationSaveState state)
    {
        EnsureActive();
        if (state is null || !string.Equals(state.Id, TKind.Id, StringComparison.Ordinal)
            || state.Version != TKind.Version || state.RowCount < 0)
        {
            throw new InvalidDataException($"Population '{TKind.Id}' has an unknown or incompatible schema.");
        }

        if (state.HandlesEnabled != _handlesEnabled
            || state.RowToHandleSlot is null || state.HandleSlots is null || state.FreeHandleSlots is null)
        {
            throw new InvalidDataException($"Population '{TKind.Id}' has invalid handle metadata.");
        }

        var rowToHandleSlot = (int[])state.RowToHandleSlot.Clone();
        var handleSlots = (PopulationHandleSlotState[])state.HandleSlots.Clone();
        var freeHandleSlots = (int[])state.FreeHandleSlots.Clone();
        var maximumGeneration = ValidateHandleState(state.RowCount, rowToHandleSlot, handleSlots,
            freeHandleSlots);

        if (state.Columns is null || state.Columns.Length != _columns.Count)
        {
            throw new InvalidDataException($"Population '{TKind.Id}' has a missing or unknown column.");
        }

        var savedColumns = new Dictionary<string, PopulationColumnSaveState>(StringComparer.Ordinal);
        foreach (var columnState in state.Columns)
        {
            if (columnState is null || string.IsNullOrWhiteSpace(columnState.Id)
                || !savedColumns.TryAdd(columnState.Id, columnState))
            {
                throw new InvalidDataException($"Population '{TKind.Id}' has an invalid or duplicate column.");
            }
        }

        var columnRestores = new List<Action>(_columns.Count);
        foreach (var (id, column) in _columns.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!savedColumns.TryGetValue(id, out var columnState))
            {
                throw new InvalidDataException($"Population '{TKind.Id}' is missing column '{id}'.");
            }

            columnRestores.Add(column.PrepareRestore(columnState, state.RowCount));
        }

        return () =>
        {
            CaptureStructureUndo();
            if (state.RowCount > _capacity)
            {
                SetCapacity(state.RowCount);
            }

            foreach (var restoreColumn in columnRestores)
            {
                restoreColumn();
            }

            _storage.Count = state.RowCount;
            if (_handlesEnabled)
            {
                var capacityRowToHandleSlot = new int[_capacity];
                Array.Fill(capacityRowToHandleSlot, -1);
                Array.Copy(rowToHandleSlot, capacityRowToHandleSlot, rowToHandleSlot.Length);
                _rowToHandleSlot = capacityRowToHandleSlot;
                _handleSlots.Clear();
                foreach (var slot in handleSlots)
                {
                    _handleSlots.Add(new HandleSlot(slot.Row, slot.Generation));
                }

                _freeHandleSlots.Clear();
                for (var index = freeHandleSlots.Length - 1; index >= 0; index--)
                {
                    _freeHandleSlots.Push(freeHandleSlots[index]);
                }

                ObserveHandleGeneration(maximumGeneration);
            }
        };
    }

    private ulong ValidateHandleState(int rowCount, int[] rowToHandleSlot,
        PopulationHandleSlotState[] handleSlots, int[] freeHandleSlots)
    {
        if (!_handlesEnabled)
        {
            if (rowToHandleSlot.Length != 0 || handleSlots.Length != 0 || freeHandleSlots.Length != 0)
            {
                throw new InvalidDataException($"Population '{TKind.Id}' has handles disabled but saved handle data.");
            }

            return 0;
        }

        if (rowToHandleSlot.Length != rowCount)
        {
            throw new InvalidDataException($"Population '{TKind.Id}' row-to-handle mapping has an invalid length.");
        }

        var seenSlots = new bool[handleSlots.Length];
        var seenGenerations = new HashSet<ulong>();
        var maximumGeneration = 0UL;
        for (var row = 0; row < rowToHandleSlot.Length; row++)
        {
            var slotIndex = rowToHandleSlot[row];
            if ((uint)slotIndex >= (uint)handleSlots.Length || seenSlots[slotIndex])
            {
                throw new InvalidDataException($"Population '{TKind.Id}' row-to-handle mapping is invalid.");
            }

            var slot = handleSlots[slotIndex];
            if (slot.Row != row)
            {
                throw new InvalidDataException($"Population '{TKind.Id}' handle slot does not match its row.");
            }

            seenSlots[slotIndex] = true;
        }

        var freeSet = new HashSet<int>();
        foreach (var slotIndex in freeHandleSlots)
        {
            if ((uint)slotIndex >= (uint)handleSlots.Length || !freeSet.Add(slotIndex))
            {
                throw new InvalidDataException($"Population '{TKind.Id}' free handle list is invalid.");
            }

            var slot = handleSlots[slotIndex];
            if (slot.Row != -1)
            {
                throw new InvalidDataException($"Population '{TKind.Id}' free handle slot is active.");
            }

            seenSlots[slotIndex] = true;
        }

        for (var index = 0; index < handleSlots.Length; index++)
        {
            var slot = handleSlots[index];
            if (!seenSlots[index] || slot.Generation == 0 || slot.Generation > long.MaxValue
                || !seenGenerations.Add(slot.Generation))
            {
                throw new InvalidDataException($"Population '{TKind.Id}' handle slot metadata is invalid.");
            }

            maximumGeneration = Math.Max(maximumGeneration, slot.Generation);
        }

        return maximumGeneration;
    }

    private static void ObserveHandleGeneration(ulong generation)
    {
        var target = checked((long)generation);
        while (target > 0)
        {
            var current = Volatile.Read(ref s_nextHandleGeneration);
            if (current >= target
                || Interlocked.CompareExchange(ref s_nextHandleGeneration, target, current) == current)
            {
                return;
            }
        }
    }

    internal Population<TKind> CloneTo(World world)
    {
        var clone = new Population<TKind>(world, _handlesEnabled, _ownerToken)
        {
            _capacity = _capacity,
        };
        clone._storage.Count = Count;
        foreach (var (id, column) in _columns)
        {
            // Assignment, not addition: the constructor already created the schema-declared mask column.
            clone._columns[id] = column.CloneTo(clone._storage);
        }

        clone._tagMask = clone._columns.TryGetValue(s_tagMaskKey.Id, out var clonedMask)
            ? (PopulationColumn<ulong>)clonedMask
            : null;
        clone._modifierSlots = clone._columns.TryGetValue(s_modifierKey.Id, out var clonedModifiers)
            ? (PopulationColumn<ModifierSlot8>)clonedModifiers
            : null;

        if (_handlesEnabled)
        {
            clone._rowToHandleSlot = (int[])_rowToHandleSlot.Clone();
            clone._handleSlots.AddRange(_handleSlots);
            foreach (var slot in _freeHandleSlots.Reverse())
            {
                clone._freeHandleSlots.Push(slot);
            }
        }

        return clone;
    }

    internal void RebindWorld(World world)
    {
        World = world;
        _storage.Rebind(world);
    }

    internal void DetachWorld()
    {
        _storage.Detach();
    }

    private static bool MatchesDefinition<T>(PopulationColumnBase existing, PopulationColumnKey<T> key, bool isDoubleBuffered)
        where T : unmanaged =>
        existing.Version == key.Version
        && existing.ValueType == typeof(T)
        && existing.CodecId == key.Codec.Id
        && existing.CodecVersion == key.Codec.Version
        && existing.ElementSize == key.Codec.ElementSize
        && existing.IsDoubleBuffered == isDoubleBuffered;

    private int AllocateHandle(int row)
    {
        int slotIndex;
        HandleSlot slot;
        if (_freeHandleSlots.Count > 0)
        {
            slotIndex = _freeHandleSlots.Pop();
            slot = _handleSlots[slotIndex];
            slot.Row = row;
            slot.Generation = NextHandleGeneration();
            _handleSlots[slotIndex] = slot;
        }
        else
        {
            slotIndex = _handleSlots.Count;
            slot = new HandleSlot(row, NextHandleGeneration());
            _handleSlots.Add(slot);
        }

        return slotIndex;
    }

    private void ReleaseHandle(int slotIndex)
    {
        var slot = _handleSlots[slotIndex];
        slot.Row = -1;
        _handleSlots[slotIndex] = slot;
        _freeHandleSlots.Push(slotIndex);
    }

    private static ulong NextHandleGeneration()
    {
        var next = Interlocked.Increment(ref s_nextHandleGeneration);
        if (next <= 0)
        {
            throw new InvalidOperationException("The population handle generation space is exhausted.");
        }

        return (ulong)next;
    }

    private void CaptureStructureUndo()
    {
        if (!World.TryCapturePopulationStructure(this))
        {
            return;
        }

        var batch = _freeStructureUndoBatches.Count > 0
            ? _freeStructureUndoBatches.Pop()
            : new PopulationStructureUndoBatch(this);
        try
        {
            batch.Capture();
            World.RecordPopulationUndo(batch);
        }
        catch
        {
            batch.Release();
            World.ReleasePopulationStructureCapture(this);
            throw;
        }
    }

    private void ReleaseStructureUndo(PopulationStructureUndoBatch batch)
    {
        batch.Reset();
        _freeStructureUndoBatches.Push(batch);
    }

    private int GrowCapacity(int required)
    {
        if (_capacity == 0)
        {
            return Math.Max(4, required);
        }

        var doubled = (long)_capacity * 2;
        return (int)Math.Min(int.MaxValue, Math.Max(required, doubled));
    }

    private void SetCapacity(int capacity)
    {
        foreach (var column in _columns.Values)
        {
            column.EnsureCapacity(capacity);
        }

        if (_handlesEnabled && _rowToHandleSlot.Length < capacity)
        {
            Array.Resize(ref _rowToHandleSlot, capacity);
            Array.Fill(_rowToHandleSlot, -1, _capacity, capacity - _capacity);
        }

        _capacity = capacity;
    }

    private struct HandleSlot(int row, ulong generation)
    {
        public int Row = row;

        public ulong Generation = generation;
    }

    private sealed class PopulationStructureUndoBatch(Population<TKind> population) : IColumnUndoBatch
    {
        private int[] _rowToHandleSlot = [];
        private HandleSlot[] _handleSlots = [];
        private int[] _freeHandleSlots = [];
        private int _count;
        private int _capacity;
        private int _rowToHandleSlotCount;
        private int _handleSlotCount;
        private int _freeHandleSlotCount;

        public void Capture()
        {
            _count = population._storage.Count;
            _capacity = population._capacity;
            _rowToHandleSlotCount = population._rowToHandleSlot.Length;
            _handleSlotCount = population._handleSlots.Count;
            _freeHandleSlotCount = population._freeHandleSlots.Count;

            EnsureLength(ref _rowToHandleSlot, _rowToHandleSlotCount);
            Array.Copy(population._rowToHandleSlot, _rowToHandleSlot, _rowToHandleSlotCount);

            EnsureLength(ref _handleSlots, _handleSlotCount);
            for (var index = 0; index < _handleSlotCount; index++)
            {
                _handleSlots[index] = population._handleSlots[index];
            }

            EnsureLength(ref _freeHandleSlots, _freeHandleSlotCount);
            var freeIndex = 0;
            foreach (var slot in population._freeHandleSlots)
            {
                _freeHandleSlots[freeIndex++] = slot;
            }
        }

        public void Undo()
        {
            population._storage.Count = _count;
            population._capacity = _capacity;
            if (population._rowToHandleSlot.Length < _rowToHandleSlotCount)
            {
                Array.Resize(ref population._rowToHandleSlot, _rowToHandleSlotCount);
            }

            Array.Copy(_rowToHandleSlot, population._rowToHandleSlot, _rowToHandleSlotCount);
            if (population._rowToHandleSlot.Length > _rowToHandleSlotCount)
            {
                Array.Fill(population._rowToHandleSlot, -1, _rowToHandleSlotCount,
                    population._rowToHandleSlot.Length - _rowToHandleSlotCount);
            }

            population._handleSlots.Clear();
            for (var index = 0; index < _handleSlotCount; index++)
            {
                population._handleSlots.Add(_handleSlots[index]);
            }

            population._freeHandleSlots.Clear();
            for (var index = _freeHandleSlotCount - 1; index >= 0; index--)
            {
                population._freeHandleSlots.Push(_freeHandleSlots[index]);
            }
        }

        public void Release() => population.ReleaseStructureUndo(this);

        public void Reset()
        {
            _count = 0;
            _capacity = 0;
            _rowToHandleSlotCount = 0;
            _handleSlotCount = 0;
            _freeHandleSlotCount = 0;
        }

        private static void EnsureLength<T>(ref T[] values, int length)
        {
            if (values.Length < length)
            {
                Array.Resize(ref values, length);
            }
        }
    }
}

/// <summary>Leases stable population row identity while callback-scoped column access is active.</summary>
/// <remarks>
/// Column spans can only be obtained inside <see cref="WithAccess{TState}"/> callbacks and expire when the callback returns.
/// The lease prevents structural changes, buffer swaps and transaction boundaries until it is disposed.
/// </remarks>
public sealed class PopulationLease<TKind> : IDisposable where TKind : IPopulationKind
{
    private readonly Population<TKind> _population;
    private readonly PopulationStorage _storage;
    private readonly World _world;
    private bool _disposed;
    private bool _systemBound;
    private int _systemThreadId;
    private SystemAccess[]? _systemAccess;

    internal PopulationLease(Population<TKind> population, PopulationStorage storage)
    {
        _population = population;
        _storage = storage;
        _world = storage.World;
    }

    /// <summary>Gets the stable live row count protected by this lease.</summary>
    public int Count
    {
        get
        {
            lock (_world.TransactionGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _storage.Count;
            }
        }
    }

    /// <summary>Runs serial work with column spans that expire when the callback returns.</summary>
    /// <typeparam name="TState">Caller-owned state passed to the callback.</typeparam>
    /// <param name="range">The only rows this callback may write.</param>
    /// <param name="state">State passed to the callback.</param>
    /// <param name="callback">Synchronous access callback.</param>
    public void WithAccess<TState>(
        PopulationRange range,
        TState state,
        PopulationAccessAction<TKind, TState> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var access = _systemAccess;
        if (_systemBound && Environment.CurrentManagedThreadId != _systemThreadId)
            throw new InvalidOperationException("Serial population access from a scheduled system belongs to its execution thread.");
        var lifetime = _storage.SerialScopeLifetime;
        var generation = BeginScopedAccess(range, lifetime, access, parallel: false, group: null);
        var scope = new PopulationAccessScope<TKind>(this, lifetime, generation);
        try
        {
            callback(state, ref scope);
        }
        finally
        {
            EndScopedAccess(lifetime, generation, parallel: false);
        }
    }

    /// <summary>Runs serial initialization with caller-owned input and callback-scoped population access.</summary>
    /// <typeparam name="TInput">The unmanaged input row type.</typeparam>
    /// <typeparam name="TState">Caller-owned state passed to the callback.</typeparam>
    /// <param name="range">The only rows this callback may write.</param>
    /// <param name="input">Read-only input whose lifetime is limited to the callback.</param>
    /// <param name="state">State passed to the callback.</param>
    /// <param name="callback">Synchronous access callback.</param>
    public void WithAccess<TInput, TState>(
        PopulationRange range,
        scoped ReadOnlySpan<TInput> input,
        TState state,
        PopulationInputAccessAction<TKind, TInput, TState> callback)
        where TInput : unmanaged
    {
        ArgumentNullException.ThrowIfNull(callback);
        var access = _systemAccess;
        if (_systemBound && Environment.CurrentManagedThreadId != _systemThreadId)
            throw new InvalidOperationException("Serial population access from a scheduled system belongs to its execution thread.");
        var lifetime = _storage.SerialScopeLifetime;
        var generation = BeginScopedAccess(range, lifetime, access, parallel: false, group: null);
        var scope = new PopulationAccessScope<TKind>(this, lifetime, generation);
        try
        {
            callback(input, state, ref scope);
        }
        finally
        {
            EndScopedAccess(lifetime, generation, parallel: false);
        }
    }

    /// <summary>Ends the lease and releases its protection against structural and transaction changes.</summary>
    public void Dispose()
    {
        lock (_world.TransactionGate)
        {
            if (_disposed) return;
            if (_storage.HasActiveScopes)
                throw new InvalidOperationException("A population lease cannot be disposed while a scoped callback is active.");
            _disposed = true;
            _storage.ReleaseLease();
        }
    }

    internal object WorldIdentity => _world;

    internal PopulationStorage StorageIdentity => _storage;

    internal void BindSystemAccess(SystemAccess[] access, int executionThreadId)
    {
        lock (_world.TransactionGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _systemAccess = access;
            _systemThreadId = executionThreadId;
            _systemBound = true;
        }
    }

    internal long BeginScopedAccess(
        PopulationRange range,
        PopulationScopeLifetime lifetime,
        SystemAccess[]? access,
        bool parallel,
        object? group)
    {
        lock (_world.TransactionGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(lifetime);
            _storage.ValidateRange(range.Start, range.Length);
            _storage.EnterScope(parallel, group);
            try
            {
                return lifetime.Activate(_storage, range, access, parallel, group);
            }
            catch
            {
                _storage.ExitScope(parallel);
                throw;
            }
        }
    }

    internal void EndScopedAccess(PopulationScopeLifetime lifetime, long generation, bool parallel)
    {
        lock (_world.TransactionGate)
        {
            lifetime.Deactivate(generation);
            _storage.ExitScope(parallel);
        }
    }

    internal void ValidateScopedColumn(PopulationColumnBase column)
    {
        Validate(column);
    }

    internal void RegisterScopedDependency(PopulationScopeLifetime lifetime)
    {
        lock (_world.TransactionGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lifetime.RegisterDependency(_storage);
        }
    }

    private void Validate(PopulationColumnBase column)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(column);
        if (!_population.Owns(column))
            throw new ArgumentException("The column does not belong to the leased population.", nameof(column));
    }
}

internal abstract class PopulationRegistration
{
    public abstract Type Kind { get; }

    public abstract string Id { get; }

    public abstract object Value { get; }

    public abstract ReadOnlyMemory<string> TagNames { get; }

    public abstract PopulationColumn<ulong>? MaskColumn { get; }

    public abstract ReadOnlyMemory<string> ModifierPropertyNames { get; }

    public abstract bool TryGetModifierKey(string property, out ushort key);

    public abstract bool TrySetRowModifier(int row, ushort key, double value, int remainingTicks);

    public abstract bool TryGetEffectiveRowValue(int row, PopulationColumnBase column, ushort modifierKey,
        out double value);

    public abstract bool TryResolveRow(int slotIndex, ulong generation, out int row);

    public abstract bool HandlesEnabled { get; }

    public abstract int RowCount { get; }

    public abstract bool TryGetTagBit(string tag, out int bit);

    public abstract bool TryHasTagBit(int row, int bit);

    public abstract bool TrySetTagBit(int row, int bit, bool value);

    public abstract bool TryGetColumn(string columnId, out PopulationColumnBase column);

    public abstract PopulationSaveState CaptureSaveState();

    public abstract Action PrepareRestore(PopulationSaveState state);

    public abstract PopulationRegistration CloneTo(World world);

    public abstract void RebindWorld(World world);

    public abstract void DetachWorld();
}

internal sealed class PopulationRegistration<TKind>(Population<TKind> population) : PopulationRegistration
    where TKind : IPopulationKind
{
    public override Type Kind => typeof(TKind);

    public override string Id => TKind.Id;

    public override object Value => population;

    public override ReadOnlyMemory<string> TagNames => population.Tags;

    public override PopulationColumn<ulong>? MaskColumn => population.MaskColumn;

    public override ReadOnlyMemory<string> ModifierPropertyNames => population.ModifierProperties;

    public override bool TryGetModifierKey(string property, out ushort key) =>
        population.TryGetModifierKey(property, out key);

    public override bool TrySetRowModifier(int row, ushort key, double value, int remainingTicks) =>
        population.TrySetRowModifier(row, key, value, population.World.ExpiryFromRemainingTicks(remainingTicks));

    public override bool TryGetEffectiveRowValue(int row, PopulationColumnBase column, ushort modifierKey,
        out double value) => population.TryGetEffectiveRowValue(row, column, modifierKey, out value);

    public override bool TryResolveRow(int slotIndex, ulong generation, out int row) =>
        population.TryResolveRow(slotIndex, generation, out row);

    public override bool HandlesEnabled => population.HandlesEnabled;

    public override int RowCount => population.Count;

    public override bool TryGetTagBit(string tag, out int bit) => population.TryGetTagBit(tag, out bit);

    public override bool TryHasTagBit(int row, int bit) => population.TryHasTagBit(row, bit);

    public override bool TrySetTagBit(int row, int bit, bool value) => population.TrySetTagBit(row, bit, value);

    public override bool TryGetColumn(string columnId, out PopulationColumnBase column) =>
        population.TryGetColumn(columnId, out column);

    public override PopulationSaveState CaptureSaveState() => population.CaptureSaveState();

    public override Action PrepareRestore(PopulationSaveState state) => population.PrepareRestore(state);

    public override PopulationRegistration CloneTo(World world) =>
        new PopulationRegistration<TKind>(population.CloneTo(world));

    public override void RebindWorld(World world) => population.RebindWorld(world);

    public override void DetachWorld() => population.DetachWorld();
}

/// <summary>Addresses the rows of one population for a per-row rule scan.</summary>
internal readonly struct PopulationRowAccess(PopulationRegistration registration)
{
    private readonly PopulationRegistration _registration = registration;

    /// <summary>Gets whether the population can produce generation-checked references.</summary>
    public bool HandlesEnabled => _registration.HandlesEnabled;

    /// <summary>Gets the live row count.</summary>
    public int RowCount => _registration.RowCount;
}

/// <summary>Addresses the schema tags of one population without another lookup per row.</summary>
/// <remarks>
/// A rule binds a tag name to its bit once, then reads and writes the mask column through this
/// handle. It is only valid while the population it was resolved from is registered in the world.
/// </remarks>
internal readonly struct PopulationTagAccess(PopulationRegistration registration)
{
    private readonly PopulationRegistration _registration = registration;

    /// <summary>Gets the schema's tag names, in bit order.</summary>
    public ReadOnlyMemory<string> Names => _registration.TagNames;

    /// <summary>Resolves a tag name to its mask bit.</summary>
    public bool TryGetBit(string tag, out int bit) => _registration.TryGetTagBit(tag, out bit);

    /// <summary>Tests one row's mask bit.</summary>
    public bool TryHasTagBit(int row, int bit) => _registration.TryHasTagBit(row, bit);

    /// <summary>Sets or clears one row's mask bit.</summary>
    public bool TrySetTagBit(int row, int bit, bool value) => _registration.TrySetTagBit(row, bit, value);
}

/// <summary>Addresses the inline modifier slots of one population without another lookup per row.</summary>
/// <remarks>
/// A rule binds a modifier property to its key once, then writes and reads effective values through
/// this handle. It is only valid while the population it was resolved from is registered.
/// </remarks>
internal readonly struct PopulationModifierAccess(PopulationRegistration registration)
{
    private readonly PopulationRegistration _registration = registration;

    /// <summary>Gets the schema's modifier property names, in key order.</summary>
    public ReadOnlyMemory<string> Properties => _registration.ModifierPropertyNames;

    /// <summary>Resolves a modifier property to its slot key.</summary>
    public bool TryGetKey(string property, out ushort key) => _registration.TryGetModifierKey(property, out key);

    /// <summary>Writes one row's modifier for a property, converting the authored duration to an expiry tick.</summary>
    public bool TrySet(int row, ushort key, double value, int remainingTicks) =>
        _registration.TrySetRowModifier(row, key, value, remainingTicks);

    /// <summary>Reads one row's column value with its active modifiers applied.</summary>
    public bool TryGetEffectiveValue(int row, PopulationColumnBase column, ushort key, out double value) =>
        _registration.TryGetEffectiveRowValue(row, column, key, out value);
}
