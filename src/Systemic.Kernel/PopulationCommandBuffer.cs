namespace Systemic.Kernel;

/// <summary>Provides generated initialization for rows added to a population schema.</summary>
public interface IPopulationSchema<TSelf>
    where TSelf : unmanaged, IPopulationKind, IPopulationSchema<TSelf>
{
    static abstract void InitializeRange(
        Population<TSelf> population,
        PopulationRange range,
        ReadOnlySpan<TSelf> values);
}

/// <summary>Writable current and next buffers for double-buffered row initialization.</summary>
public readonly ref struct DoubleBufferedPopulationEdit<T>(Span<T> current, Span<T> next)
    where T : unmanaged
{
    /// <summary>Gets the current buffer range.</summary>
    public Span<T> Current { get; } = current;

    /// <summary>Gets the next buffer range.</summary>
    public Span<T> Next { get; } = next;
}

/// <summary>Reports the rows spawned and number of rows removed by a population flush.</summary>
/// <param name="SpawnedRows">Range assigned to newly created rows.</param>
/// <param name="RemovedCount">Number of live rows removed.</param>
public readonly record struct PopulationCommandResult(PopulationRange SpawnedRows, int RemovedCount);

internal interface IDeferredPopulationCommands
{
    object PopulationIdentity { get; }
    void MergeFrom(IDeferredPopulationCommands source);
    void FlushAtBarrier();
    void Deactivate();
}

/// <summary>Collects population spawns and removals for one transactional flush.</summary>
/// <remarks>Scheduled buffers are execution-scoped and apply only at their schedule wave barrier.</remarks>
public sealed class PopulationCommandBuffer<TKind> : IDeferredPopulationCommands
    where TKind : unmanaged, IPopulationKind, IPopulationSchema<TKind>
{
    private readonly Population<TKind> _population;
    private readonly bool _scheduled;
    private bool _active = true;
    private TKind[] _spawns = [];
    private RemovalCommand[] _removals = [];
    private int[] _resolvedRows = [];
    private int _spawnCount;
    private int _removalCount;

    /// <summary>Creates a buffer for direct population setup and mutation.</summary>
    public PopulationCommandBuffer(Population<TKind> population) : this(population, scheduled: false) { }

    internal PopulationCommandBuffer(Population<TKind> population, bool scheduled)
    {
        _population = population ?? throw new ArgumentNullException(nameof(population));
        _scheduled = scheduled;
    }

    /// <summary>Reserves storage for queued spawns, removals, and resolved removal rows.</summary>
    public void Reserve(int spawnCapacity, int removalCapacity)
    {
        EnsureActive();
        ArgumentOutOfRangeException.ThrowIfNegative(spawnCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(removalCapacity);
        EnsureCapacity(ref _spawns, spawnCapacity);
        EnsureCapacity(ref _removals, removalCapacity);
        EnsureCapacity(ref _resolvedRows, removalCapacity);
        // A scheduled buffer is filled before the barrier, where structural changes are forbidden.
        if (!_scheduled) _population.Reserve(checked(_population.Count + spawnCapacity));
    }

    /// <summary>Queues values to append as initialized rows at flush time.</summary>
    public void Spawn(ReadOnlySpan<TKind> values)
    {
        EnsureActive();
        if (values.IsEmpty)
        {
            return;
        }

        var required = checked(_spawnCount + values.Length);
        EnsureCapacity(ref _spawns, required);
        values.CopyTo(_spawns.AsSpan(_spawnCount));
        _spawnCount = required;
    }

    /// <summary>Queues removal of the row index resolved at the flush barrier.</summary>
    public void RemoveRow(int row)
    {
        EnsureActive();
        AppendRemoval(new RemovalCommand(row));
    }

    /// <summary>Queues removal of the row currently resolved by a handle.</summary>
    public void Remove(PopulationHandle<TKind> handle)
    {
        EnsureActive();
        AppendRemoval(new RemovalCommand(handle));
    }

    /// <summary>Applies queued removals before queued spawns in a transaction.</summary>
    /// <exception cref="InvalidOperationException">The buffer belongs to a scheduled execution.</exception>
    public PopulationCommandResult Flush()
    {
        EnsureActive();
        if (_scheduled) throw new InvalidOperationException("Scheduled population commands flush only at their barrier.");
        return FlushCore();
    }

    private PopulationCommandResult FlushCore()
    {
        if (_spawnCount == 0 && _removalCount == 0)
        {
            return new PopulationCommandResult(new PopulationRange(_population.Count, 0), 0);
        }

        using var transaction = _population.World.BeginTransaction();
        var resolvedCount = ResolveRemovals();
        Array.Sort(_resolvedRows, 0, resolvedCount);
        var uniqueCount = RemoveDuplicateRows(resolvedCount);
        var removedCount = 0;
        for (var index = uniqueCount - 1; index >= 0; index--)
        {
            _population.RemoveSwapBack(_resolvedRows[index]);
            removedCount++;
        }

        var range = new PopulationRange(_population.Count, 0);
        if (_spawnCount > 0)
        {
            range = _population.AddRange(_spawnCount);
            TKind.InitializeRange(_population, range, _spawns.AsSpan(0, _spawnCount));
        }

        transaction.Commit();
        _spawnCount = 0;
        _removalCount = 0;
        return new PopulationCommandResult(range, removedCount);
    }

    /// <summary>Discards queued changes without applying them.</summary>
    public void Clear()
    {
        EnsureActive();
        _spawnCount = 0;
        _removalCount = 0;
    }

    object IDeferredPopulationCommands.PopulationIdentity => _population;

    void IDeferredPopulationCommands.MergeFrom(IDeferredPopulationCommands source)
    {
        EnsureActive();
        if (source is not PopulationCommandBuffer<TKind> other || !ReferenceEquals(_population, other._population))
            throw new InvalidOperationException("Deferred command buffers must target the same population.");
        other.EnsureActive();
        var spawnCount = checked(_spawnCount + other._spawnCount);
        var removalCount = checked(_removalCount + other._removalCount);
        EnsureCapacity(ref _spawns, spawnCount);
        EnsureCapacity(ref _removals, removalCount);
        EnsureCapacity(ref _resolvedRows, removalCount);
        other._spawns.AsSpan(0, other._spawnCount).CopyTo(_spawns.AsSpan(_spawnCount));
        other._removals.AsSpan(0, other._removalCount).CopyTo(_removals.AsSpan(_removalCount));
        _spawnCount = spawnCount;
        _removalCount = removalCount;
    }

    void IDeferredPopulationCommands.FlushAtBarrier()
    {
        EnsureActive();
        _ = FlushCore();
    }

    void IDeferredPopulationCommands.Deactivate() => _active = false;

    private void EnsureActive()
    {
        if (!_active) throw new InvalidOperationException("The scheduled population command buffer has expired.");
        _population.EnsureActive();
    }

    private int ResolveRemovals()
    {
        var resolvedCount = 0;
        var originalCount = _population.Count;
        for (var index = 0; index < _removalCount; index++)
        {
            var command = _removals[index];
            if (command.ByHandle)
            {
                if (_population.TryResolve(command.Handle, out var row))
                {
                    _resolvedRows[resolvedCount++] = row;
                }

                continue;
            }

            if ((uint)command.Row >= (uint)originalCount)
            {
                throw new ArgumentOutOfRangeException(nameof(command.Row), command.Row,
                    "A queued row removal must reference a row that was live when the batch was flushed.");
            }

            _resolvedRows[resolvedCount++] = command.Row;
        }

        return resolvedCount;
    }

    private int RemoveDuplicateRows(int count)
    {
        if (count < 2)
        {
            return count;
        }

        var uniqueCount = 1;
        for (var index = 1; index < count; index++)
        {
            if (_resolvedRows[index] != _resolvedRows[uniqueCount - 1])
            {
                _resolvedRows[uniqueCount++] = _resolvedRows[index];
            }
        }

        return uniqueCount;
    }

    private void AppendRemoval(RemovalCommand command)
    {
        var required = checked(_removalCount + 1);
        EnsureCapacity(ref _removals, required);
        EnsureCapacity(ref _resolvedRows, required);
        _removals[_removalCount++] = command;
    }

    private static void EnsureCapacity<T>(ref T[] values, int required)
    {
        if (required <= values.Length)
        {
            return;
        }

        var capacity = values.Length == 0 ? 4 : values.Length;
        while (capacity < required)
        {
            var next = (long)capacity * 2;
            capacity = next >= int.MaxValue ? int.MaxValue : (int)next;
            if (capacity < required && capacity == int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(required), "The command batch exceeds supported capacity.");
            }
        }

        Array.Resize(ref values, capacity);
    }

    private readonly struct RemovalCommand
    {
        public RemovalCommand(int row)
        {
            Row = row;
            Handle = default;
            ByHandle = false;
        }

        public RemovalCommand(PopulationHandle<TKind> handle)
        {
            Row = -1;
            Handle = handle;
            ByHandle = true;
        }

        public int Row { get; }
        public PopulationHandle<TKind> Handle { get; }
        public bool ByHandle { get; }
    }
}
