namespace Systemic.Kernel;

public interface IPopulationSchema<TSelf>
    where TSelf : unmanaged, IPopulationKind, IPopulationSchema<TSelf>
{
    static abstract void InitializeRange(
        Population<TSelf> population,
        PopulationRange range,
        ReadOnlySpan<TSelf> values);
}

public readonly ref struct DoubleBufferedPopulationEdit<T>(Span<T> current, Span<T> next)
    where T : unmanaged
{
    public Span<T> Current { get; } = current;
    public Span<T> Next { get; } = next;
}

public readonly record struct PopulationCommandResult(PopulationRange SpawnedRows, int RemovedCount);

public sealed class PopulationCommandBuffer<TKind>(Population<TKind> population)
    where TKind : unmanaged, IPopulationKind, IPopulationSchema<TKind>
{
    private readonly Population<TKind> _population = population ?? throw new ArgumentNullException(nameof(population));
    private TKind[] _spawns = [];
    private RemovalCommand[] _removals = [];
    private int[] _resolvedRows = [];
    private int _spawnCount;
    private int _removalCount;

    public void Reserve(int spawnCapacity, int removalCapacity)
    {
        _population.EnsureActive();
        ArgumentOutOfRangeException.ThrowIfNegative(spawnCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(removalCapacity);
        EnsureCapacity(ref _spawns, spawnCapacity);
        EnsureCapacity(ref _removals, removalCapacity);
        EnsureCapacity(ref _resolvedRows, removalCapacity);
        _population.Reserve(checked(_population.Count + spawnCapacity));
    }

    public void Spawn(ReadOnlySpan<TKind> values)
    {
        _population.EnsureActive();
        if (values.IsEmpty)
        {
            return;
        }

        var required = checked(_spawnCount + values.Length);
        EnsureCapacity(ref _spawns, required);
        values.CopyTo(_spawns.AsSpan(_spawnCount));
        _spawnCount = required;
    }

    public void RemoveRow(int row)
    {
        _population.EnsureActive();
        AppendRemoval(new RemovalCommand(row));
    }

    public void Remove(PopulationHandle<TKind> handle)
    {
        _population.EnsureActive();
        AppendRemoval(new RemovalCommand(handle));
    }

    public PopulationCommandResult Flush()
    {
        _population.EnsureActive();
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

    public void Clear()
    {
        _spawnCount = 0;
        _removalCount = 0;
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
