using System.Threading;

namespace Systemic.Kernel;

/// <summary>Identifies a population schema by its stable ID and version.</summary>
public interface IPopulationKind
{
    /// <summary>Gets the stable schema identifier persisted with the population.</summary>
    static abstract string Id { get; }

    /// <summary>Gets the schema version persisted with the population.</summary>
    static abstract int Version { get; }
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
/// <remarks>Structural changes are not allowed while borrowed population views are leased.</remarks>
public sealed class Population<TKind> where TKind : IPopulationKind
{
    private static long s_nextOwnerToken;
    private static long s_nextHandleGeneration;

    private readonly PopulationStorage _storage;
    private readonly Dictionary<string, PopulationColumnBase> _columns = new(StringComparer.Ordinal);
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
    }

    internal World World { get; private set; }

    /// <summary>Leases this population's row identity and buffers until the returned lease is disposed.</summary>
    /// <remarks>
    /// Dispose the lease only after every user of its views has finished. A previously returned span cannot be
    /// revoked after disposal. Concurrent access must use disjoint ranges whenever a range is written.
    /// </remarks>
    /// <summary>Leases this population's row identity and buffers until the returned lease is disposed.</summary>
    /// <remarks>Finish all users of its views before disposing the lease; concurrent writes must use disjoint ranges.</remarks>
    public PopulationLease<TKind> Borrow()
    {
        lock (World.TransactionGate)
        {
            EnsureActive();
            var lease = new PopulationLease<TKind>(this, _storage);
            _storage.AcquireLease();
            return lease;
        }
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
            clone._columns.Add(id, column.CloneTo(clone._storage));
        }

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

/// <summary>A tracked lifetime for borrowed column views and stable population rows.</summary>
/// <summary>Leases row identity and borrowed column views for one population.</summary>
/// <remarks>
/// Keep the lease alive while any issued view is in use. A view retained after disposal cannot be revoked;
/// this API does not make overlapping reads and writes or unrelated World operations thread-safe.
/// </remarks>
public sealed class PopulationLease<TKind> : IDisposable where TKind : IPopulationKind
{
    private readonly Population<TKind> _population;
    private readonly PopulationStorage _storage;
    private readonly World _world;
    private bool _disposed;

    internal PopulationLease(Population<TKind> population, PopulationStorage storage)
    {
        _population = population;
        _storage = storage;
        _world = storage.World;
    }

    /// <summary>Reads the live rows of a single-buffered column.</summary>
    public ReadOnlySpan<T> Read<T>(PopulationColumn<T> column) where T : unmanaged
    {
        lock (_world.TransactionGate)
        {
            Validate(column);
            return column.ReadOnlySpan;
        }
    }

    /// <summary>Gets a writable range from a single-buffered column.</summary>
    public Span<T> Edit<T>(PopulationColumn<T> column, int start, int length) where T : unmanaged
    {
        lock (_world.TransactionGate)
        {
            Validate(column);
            return column.EditRange(start, length);
        }
    }

    /// <summary>Reads the current values of a double-buffered column.</summary>
    public ReadOnlySpan<T> ReadCurrent<T>(DoubleBufferedPopulationColumn<T> column) where T : unmanaged
    {
        lock (_world.TransactionGate)
        {
            Validate(column);
            return column.Current;
        }
    }

    /// <summary>Gets a writable range from the next buffer of a double-buffered column.</summary>
    public Span<T> EditNext<T>(DoubleBufferedPopulationColumn<T> column, int start, int length) where T : unmanaged
    {
        lock (_world.TransactionGate)
        {
            Validate(column);
            return column.EditNext(start, length);
        }
    }

    /// <summary>Gets writable ranges from both buffers for initializing newly created rows.</summary>
    public DoubleBufferedPopulationEdit<T> EditBoth<T>(DoubleBufferedPopulationColumn<T> column, int start, int length)
        where T : unmanaged
    {
        lock (_world.TransactionGate)
        {
            Validate(column);
            return column.EditBothBuffers(start, length);
        }
    }

    /// <summary>Ends the lease and releases the population's borrowed-view lock.</summary>
    public void Dispose()
    {
        lock (_world.TransactionGate)
        {
            if (_disposed) return;
            _disposed = true;
            _storage.ReleaseLease();
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

    public override PopulationSaveState CaptureSaveState() => population.CaptureSaveState();

    public override Action PrepareRestore(PopulationSaveState state) => population.PrepareRestore(state);

    public override PopulationRegistration CloneTo(World world) =>
        new PopulationRegistration<TKind>(population.CloneTo(world));

    public override void RebindWorld(World world) => population.RebindWorld(world);

    public override void DetachWorld() => population.DetachWorld();
}
