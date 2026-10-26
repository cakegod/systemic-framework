using System.Runtime.ExceptionServices;

namespace Systemic.Kernel;

public enum SystemAccessMode
{
    Read,
    Write,
    DeferredWorldWrite,
}

/// <summary>A declared dependency used to build deterministic parallel schedule waves.</summary>
public readonly record struct SystemAccess(string Resource, SystemAccessMode Mode)
{
    private const string WorldResource = "$world";

    public static SystemAccess ReadWorld { get; } = new(WorldResource, SystemAccessMode.Read);
    /// <summary>Exclusive access for operations that mutate World immediately, such as rule dispatch.</summary>
    public static SystemAccess WriteWorld { get; } = new(WorldResource, SystemAccessMode.Write);
    public static SystemAccess DeferWorldWrites { get; } = new(WorldResource, SystemAccessMode.DeferredWorldWrite);

    public static SystemAccess ReadResource(string resource) => Create(resource, SystemAccessMode.Read);
    public static SystemAccess WriteResource(string resource) => Create(resource, SystemAccessMode.Write);

    public static SystemAccess ReadColumn<TKind, TValue>(PopulationColumnKey<TValue> column)
        where TKind : IPopulationKind
        where TValue : unmanaged => CreateColumnAccess<TKind, TValue>(column, SystemAccessMode.Read);

    public static SystemAccess WriteColumn<TKind, TValue>(PopulationColumnKey<TValue> column)
        where TKind : IPopulationKind
        where TValue : unmanaged => CreateColumnAccess<TKind, TValue>(column, SystemAccessMode.Write);

    private static SystemAccess CreateColumnAccess<TKind, TValue>(
        PopulationColumnKey<TValue> column,
        SystemAccessMode mode)
        where TKind : IPopulationKind
        where TValue : unmanaged
    {
        ArgumentNullException.ThrowIfNull(column);
        return new SystemAccess($"population:{TKind.Id}/column:{column.Id}", mode);
    }

    private static SystemAccess Create(string resource, SystemAccessMode mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        return new SystemAccess(resource, mode);
    }
}

/// <summary>Opt-in simulation work. Direct population writes must match the declared access set.</summary>
public interface IScheduledKernelSystem
{
    string Id { get; }
    IReadOnlyList<SystemAccess> Access { get; }
    void Execute(ScheduledSystemContext context);
}

/// <summary>Builds a deterministic schedule; conflicting systems keep registration order.</summary>
public sealed class DeterministicSystemScheduleBuilder
{
    private readonly List<SystemNode> _systems = [];
    private readonly int _workerCount;
    private bool _built;

    public DeterministicSystemScheduleBuilder(int workerCount = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(workerCount);

        // Four logical workers match common low-power development hardware without creating a
        // large worker fleet on high-core-count hosts. Callers can still choose a different cap.
        _workerCount = workerCount == 0 ? Math.Clamp(Environment.ProcessorCount, 1, 4) : workerCount;
    }

    public DeterministicSystemScheduleBuilder Add(IScheduledKernelSystem system)
    {
        if (_built)
        {
            throw new InvalidOperationException("The system schedule has already been built.");
        }

        ArgumentNullException.ThrowIfNull(system);
        ArgumentException.ThrowIfNullOrWhiteSpace(system.Id);
        if (_systems.Any(existing => string.Equals(existing.System.Id, system.Id, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"System '{system.Id}' is registered more than once.");
        }

        ArgumentNullException.ThrowIfNull(system.Access);
        var access = system.Access.ToArray();
        if (access.Any(item => string.IsNullOrWhiteSpace(item.Resource)
            || !Enum.IsDefined(item.Mode)))
        {
            throw new ArgumentException($"System '{system.Id}' has an invalid access declaration.", nameof(system));
        }

        _systems.Add(new SystemNode(system, access, _systems.Count));
        return this;
    }

    public DeterministicSystemSchedule Build()
    {
        if (_built)
        {
            throw new InvalidOperationException("The system schedule can only be built once.");
        }

        _built = true;
        var waveBySystem = new int[_systems.Count];
        for (var system = 0; system < _systems.Count; system++)
        {
            for (var previous = 0; previous < system; previous++)
            {
                if (Conflicts(_systems[previous].Access, _systems[system].Access))
                {
                    waveBySystem[system] = Math.Max(waveBySystem[system], waveBySystem[previous] + 1);
                }
            }
        }

        var waveCount = waveBySystem.Length == 0 ? 0 : waveBySystem.Max() + 1;
        var waves = new SystemNode[waveCount][];
        for (var wave = 0; wave < waveCount; wave++)
        {
            waves[wave] = [.. _systems.Where((_, index) => waveBySystem[index] == wave)];
        }

        return new DeterministicSystemSchedule(waves, _workerCount);
    }

    private static bool Conflicts(SystemAccess[] left, SystemAccess[] right)
    {
        foreach (var first in left)
        {
            foreach (var second in right)
            {
                if (!string.Equals(first.Resource, second.Resource, StringComparison.Ordinal))
                {
                    continue;
                }

                if (first.Mode == SystemAccessMode.Read && second.Mode == SystemAccessMode.Read)
                {
                    continue;
                }

                // Buffered World commands are private to each system until an ordered barrier.
                if (first.Mode == SystemAccessMode.DeferredWorldWrite
                    && second.Mode == SystemAccessMode.DeferredWorldWrite)
                {
                    continue;
                }

                return true;
            }
        }

        return false;
    }

    internal sealed record SystemNode(IScheduledKernelSystem System, SystemAccess[] Access, int Order);
}

/// <summary>
/// Executes access-compatible systems concurrently and applies deferred World commands in stable
/// registration order at each dependency barrier. A failed tick rolls back every applied command.
/// </summary>
public sealed class DeterministicSystemSchedule : IDisposable
{
    private readonly DeterministicSystemScheduleBuilder.SystemNode[][] _waves;
    private readonly int _workerCount;
    private readonly ReusableWorkerPool _workerPool;
    private readonly SystemWaveBatch _waveBatch;
    private readonly ScheduledSystemContext[] _contexts;
    private readonly ExceptionDispatchInfo?[] _failures;
    private int _state;

    internal DeterministicSystemSchedule(
        DeterministicSystemScheduleBuilder.SystemNode[][] waves,
        int workerCount)
    {
        _waves = waves;
        _workerCount = workerCount;
        _workerPool = new ReusableWorkerPool(workerCount);
        _waveBatch = new SystemWaveBatch(this);
        var systemCount = waves.Sum(wave => wave.Length);
        _contexts = new ScheduledSystemContext[systemCount];
        _failures = new ExceptionDispatchInfo?[systemCount];
        foreach (var wave in waves)
        {
            foreach (var node in wave)
            {
                _contexts[node.Order] = new ScheduledSystemContext(
                    new WorldCommandStorage(executionScoped: true), _workerPool);
            }
        }
    }

    public int WorkerCount => _workerCount;
    public int SystemCount => _contexts.Length;

    public void Execute(World world, int tick)
    {
        ArgumentNullException.ThrowIfNull(world);
        var state = Interlocked.CompareExchange(ref _state, 1, 0);
        if (state != 0)
        {
            throw state == 2
                ? new ObjectDisposedException(nameof(DeterministicSystemSchedule))
                : new InvalidOperationException("A deterministic schedule cannot execute concurrently with itself.");
        }

        try
        {
            using var transaction = world.BeginTransaction();
            foreach (var wave in _waves)
            {
                Array.Clear(_failures);
                try
                {
                    RunWave(wave, world, tick);
                    ThrowFirstFailure(wave);
                    foreach (var node in wave)
                    {
                        _contexts[node.Order].FlushCommands(world);
                    }
                }
                finally
                {
                    foreach (var node in wave)
                    {
                        var context = _contexts[node.Order];
                        context.RegisterRollbackActions(transaction);
                        context.EndExecution();
                    }
                }
            }

            transaction.Commit();
        }
        finally
        {
            foreach (var context in _contexts)
            {
                context.Clear();
            }

            Volatile.Write(ref _state, 0);
        }
    }

    public void Dispose()
    {
        var state = Interlocked.CompareExchange(ref _state, 2, 0);
        if (state == 1)
        {
            throw new InvalidOperationException("A deterministic schedule cannot be disposed while it is executing.");
        }

        if (state == 0)
        {
            _workerPool.Dispose();
        }
    }

    private void RunWave(DeterministicSystemScheduleBuilder.SystemNode[] wave, World world, int tick)
    {
        if (_workerCount == 1 || wave.Length < 2)
        {
            foreach (var node in wave)
            {
                ExecuteOne(node, world, tick, _workerCount);
            }

            return;
        }

        _waveBatch.Configure(wave, world, tick);
        _workerPool.Run(_waveBatch);
    }

    private void ExecuteOne(
        DeterministicSystemScheduleBuilder.SystemNode node,
        World world,
        int tick,
        int systemWorkerCount)
    {
        try
        {
            _contexts[node.Order].Prepare(world, tick, systemWorkerCount);
            node.System.Execute(_contexts[node.Order]);
        }
        catch (Exception exception)
        {
            _failures[node.Order] = ExceptionDispatchInfo.Capture(exception);
        }
    }

    private void ThrowFirstFailure(DeterministicSystemScheduleBuilder.SystemNode[] wave)
    {
        foreach (var node in wave)
        {
            _failures[node.Order]?.Throw();
        }
    }

    private sealed class SystemWaveBatch(DeterministicSystemSchedule schedule) : IReusableWorkBatch
    {
        private DeterministicSystemScheduleBuilder.SystemNode[] _systems = [];
        private World? _world;
        private int _tick;

        public int Count => _systems.Length;

        public void Configure(DeterministicSystemScheduleBuilder.SystemNode[] systems, World world, int tick)
        {
            _systems = systems;
            _world = world;
            _tick = tick;
        }

        public void Execute(int partition) => schedule.ExecuteOne(_systems[partition], _world!, _tick, systemWorkerCount: 1);
    }
}

internal interface IReusableWorkBatch
{
    int Count { get; }
    void Execute(int partition);
}

internal interface IReusableRangeBatch : IReusableWorkBatch;

/// <summary>Owns persistent workers so warmed schedules never queue per-tick ThreadPool work.</summary>
internal sealed class ReusableWorkerPool : IDisposable
{
    private readonly int _workerCount;
    private readonly Thread[] _workers;
    private readonly ExceptionDispatchInfo?[] _failures;
    private readonly object _gate = new();
    private IReusableWorkBatch? _batch;
    private int _activeWorkerCount;
    private int _completedWorkerCount;
    private int _generation;
    private int _running;
    private bool _disposed;

    public ReusableWorkerPool(int workerCount)
    {
        _workerCount = workerCount;
        _workers = new Thread[Math.Max(0, workerCount - 1)];
        _failures = new ExceptionDispatchInfo?[workerCount];
        for (var index = 0; index < _workers.Length; index++)
        {
            var workerIndex = index + 1;
            var worker = new Thread(() => WorkerLoop(workerIndex))
            {
                IsBackground = true,
                Name = $"Systemic kernel worker {workerIndex}",
            };
            _workers[index] = worker;
            worker.Start();
        }
    }

    public void Run(IReusableWorkBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0)
        {
            return;
        }

        var activeWorkers = Math.Min(_workerCount, batch.Count);
        if (activeWorkers == 1)
        {
            ThrowIfDisposed();
            batch.Execute(0);
            return;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running != 0)
            {
                throw new InvalidOperationException("A schedule worker pool cannot execute nested work batches.");
            }

            _running = 1;
            _batch = batch;
            _activeWorkerCount = activeWorkers;
            _completedWorkerCount = 0;
            Array.Clear(_failures, 0, activeWorkers);
            _generation++;
            Monitor.PulseAll(_gate);
        }

        try
        {
            ExecuteWorker(0, batch, activeWorkers);
            lock (_gate)
            {
                while (_completedWorkerCount < activeWorkers - 1)
                {
                    Monitor.Wait(_gate);
                }
            }

            for (var worker = 0; worker < activeWorkers; worker++)
            {
                _failures[worker]?.Throw();
            }
        }
        finally
        {
            lock (_gate)
            {
                _batch = null;
                _activeWorkerCount = 0;
                _running = 0;
                Monitor.PulseAll(_gate);
            }
        }
    }

    private void ExecuteWorker(int worker, IReusableWorkBatch batch, int activeWorkers)
    {
        try
        {
            for (var partition = worker; partition < batch.Count; partition += activeWorkers)
            {
                batch.Execute(partition);
            }
        }
        catch (Exception exception)
        {
            _failures[worker] = ExceptionDispatchInfo.Capture(exception);
        }
    }

    private void WorkerLoop(int worker)
    {
        var observedGeneration = 0;
        while (true)
        {
            IReusableWorkBatch batch;
            int activeWorkers;
            lock (_gate)
            {
                while (!_disposed && _generation == observedGeneration)
                {
                    Monitor.Wait(_gate);
                }

                if (_disposed)
                {
                    return;
                }

                observedGeneration = _generation;
                activeWorkers = _activeWorkerCount;
                if (worker >= activeWorkers)
                {
                    continue;
                }

                batch = _batch!;
            }

            try
            {
                ExecuteWorker(worker, batch, activeWorkers);
            }
            finally
            {
                lock (_gate)
                {
                    _completedWorkerCount++;
                    if (_completedWorkerCount == _activeWorkerCount - 1)
                    {
                        Monitor.PulseAll(_gate);
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_running != 0)
            {
                throw new InvalidOperationException("A schedule worker pool cannot be disposed while it is executing.");
            }

            _disposed = true;
            Monitor.PulseAll(_gate);
        }

        foreach (var worker in _workers)
        {
            worker.Join();
        }
    }

    private void ThrowIfDisposed()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }
}

/// <summary>Per-system context with read-only World access and a private deferred command buffer.</summary>
public sealed class ScheduledSystemContext
{
    private World? _world;
    private int _workerCount = 1;
    private readonly ReusableWorkerPool _workerPool;
    private readonly WorldCommandStorage _commands;
    private readonly Dictionary<Type, IReusableRangeBatch> _rangeBatches = [];

    internal ScheduledSystemContext(WorldCommandStorage commands, ReusableWorkerPool workerPool)
    {
        _commands = commands;
        _workerPool = workerPool;
    }

    public int Tick { get; private set; }
    public ReadOnlyWorldView World => new(_world ?? throw new InvalidOperationException("The system is not executing."));
    public WorldCommandBuffer Commands => _world is null
        ? throw new InvalidOperationException("The system is not executing.")
        : _commands.CreateWriter();
    public int WorkerCount => _workerCount;

    public void OnRollback(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _ = _world ?? throw new InvalidOperationException("The system is not executing.");
        _commands.OnRollback(action);
    }

    /// <summary>Runs deterministic disjoint row ranges, never assigning a row to more than one worker.</summary>
    public void ForEachRange<TState>(
        TState state,
        int rowCount,
        Action<TState, int, PopulationRange> executeRange)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rowCount);
        ArgumentNullException.ThrowIfNull(executeRange);
        if (rowCount == 0)
        {
            return;
        }

        var partitionCount = Math.Min(rowCount, _workerCount);
        var stateType = typeof(TState);
        if (!_rangeBatches.TryGetValue(stateType, out var existing))
        {
            existing = new RangeBatch<TState>();
            _rangeBatches.Add(stateType, existing);
        }

        var batch = (RangeBatch<TState>)existing;
        batch.Configure(state, rowCount, partitionCount, executeRange);
        _workerPool.Run(batch);
        batch.ThrowFirstFailure();
    }

    internal void Prepare(World world, int tick, int workerCount)
    {
        _world = world;
        Tick = tick;
        _workerCount = workerCount;
        _commands.Activate();
    }

    internal void RegisterRollbackActions(WorldTransaction transaction) =>
        _commands.RegisterRollbackActions(transaction);

    internal void FlushCommands(World world) => _commands.Flush(world);

    internal void EndExecution()
    {
        _commands.Deactivate();
        _world = null;
        _workerCount = 1;
    }

    internal void Clear()
    {
        EndExecution();
        _commands.Clear();
        _commands.ClearRollbackActions();
    }

    private sealed class RangeBatch<TState> : IReusableRangeBatch
    {
        private TState _state = default!;
        private int _rowCount;
        private Action<TState, int, PopulationRange> _executeRange = null!;
        private ExceptionDispatchInfo?[] _failures = [];

        public int Count { get; private set; }

        public void Configure(
            TState state,
            int rowCount,
            int partitionCount,
            Action<TState, int, PopulationRange> executeRange)
        {
            _state = state;
            _rowCount = rowCount;
            Count = partitionCount;
            _executeRange = executeRange;
            if (_failures.Length < partitionCount)
            {
                Array.Resize(ref _failures, partitionCount);
            }

            Array.Clear(_failures, 0, partitionCount);
        }

        public void Execute(int partition)
        {
            var start = (int)((long)_rowCount * partition / Count);
            var end = (int)((long)_rowCount * (partition + 1) / Count);
            try
            {
                _executeRange(_state, partition, new PopulationRange(start, end - start));
            }
            catch (Exception exception)
            {
                _failures[partition] = ExceptionDispatchInfo.Capture(exception);
            }
        }

        public void ThrowFirstFailure()
        {
            for (var partition = 0; partition < Count; partition++)
            {
                _failures[partition]?.Throw();
            }
        }
    }
}

public readonly struct ReadOnlyWorldView(World world)
{
    public bool ContainsEntity(int entityId) => world.ById(entityId) is { ToRemove: false };

    public bool TryGetProperty(int entityId, string property, out double value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(property);
        var entity = world.ById(entityId);
        if (entity is { ToRemove: false })
        {
            return world.TryReadEffectiveProperty(entity, property, out value);
        }

        value = 0;
        return false;
    }

    public bool HasTag(int entityId, string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        var entity = world.ById(entityId);
        return entity is { ToRemove: false } && entity.HasTag(tag);
    }

    public bool TryGetSpatialBounds(int entityId, out Bounds2 bounds) => world.TryGetSpatialBounds(entityId, out bounds);
}

/// <summary>System-local World commands. Capacity is retained across ticks.</summary>
internal sealed class WorldCommandStorage
{
    private WorldCommand[] _commands = [];
    private Action?[] _rollbackActions = [];
    private readonly bool _executionScoped;
    private bool _active = true;
    private long _generation;
    private int _count;
    private int _rollbackActionCount;

    public WorldCommandStorage()
    {
    }

    internal WorldCommandStorage(bool executionScoped)
    {
        _executionScoped = executionScoped;
        _active = !executionScoped;
    }

    public int Count => _count;

    public void Reserve(int commandCapacity)
    {
        EnsureActive();
        ArgumentOutOfRangeException.ThrowIfNegative(commandCapacity);
        EnsureCapacity(commandCapacity);
    }

    internal void OnRollback(Action action)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(action);
        if (_rollbackActionCount == _rollbackActions.Length)
        {
            Array.Resize(ref _rollbackActions, _rollbackActions.Length == 0 ? 2 : _rollbackActions.Length * 2);
        }

        _rollbackActions[_rollbackActionCount++] = action;
    }

    internal void RegisterRollbackActions(WorldTransaction transaction)
    {
        EnsureActive();
        for (var index = 0; index < _rollbackActionCount; index++)
        {
            transaction.OnRollback(_rollbackActions[index]!);
            _rollbackActions[index] = null;
        }

        _rollbackActionCount = 0;
    }

    internal void ClearRollbackActions()
    {
        Array.Clear(_rollbackActions, 0, _rollbackActionCount);
        _rollbackActionCount = 0;
    }

    internal WorldCommandBuffer CreateWriter()
    {
        EnsureActive();
        return new WorldCommandBuffer(this, _generation);
    }

    internal void ValidateWriter(long generation)
    {
        EnsureActive();
        if (generation != _generation)
        {
            throw new InvalidOperationException("The scheduled command writer belongs to an earlier execution.");
        }
    }

    internal void Activate()
    {
        _generation = checked(_generation + 1);
        _active = true;
    }

    internal void Deactivate()
    {
        if (_executionScoped)
        {
            _active = false;
        }
    }

    public void SetProperty(int entityId, string property, double value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(entityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(property);
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        Append(new WorldCommand(WorldCommandKind.SetProperty, entityId, null, property, value, default));
    }

    public void Add(Entity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        Append(new WorldCommand(WorldCommandKind.AddEntity, 0, entity, null, 0, default));
    }

    public void Remove(int entityId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(entityId);
        Append(new WorldCommand(WorldCommandKind.RemoveEntity, entityId, null, null, 0, default));
    }

    public void AddTag(int entityId, string tag) => AppendEntityName(WorldCommandKind.AddTag, entityId, tag);

    public void RemoveTag(int entityId, string tag) => AppendEntityName(WorldCommandKind.RemoveTag, entityId, tag);

    public void AddCapability(int entityId, string capability) =>
        AppendEntityName(WorldCommandKind.AddCapability, entityId, capability);

    public void RemoveCapability(int entityId, string capability) =>
        AppendEntityName(WorldCommandKind.RemoveCapability, entityId, capability);

    public void SetLink(int entityId, string link, int targetEntityId)
    {
        ValidateEntityAndName(entityId, link);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetEntityId);
        Append(new WorldCommand(WorldCommandKind.SetLink, entityId, Name: link, TargetEntityId: targetEntityId));
    }

    public void RemoveLink(int entityId, string link) => AppendEntityName(WorldCommandKind.RemoveLink, entityId, link);

    public void SetModifier(
        string source,
        int entityId,
        string property,
        ModifierOperation operation,
        double value,
        int remainingTicks = -1,
        int priority = 0,
        ModifierStage stage = ModifierStage.Standard)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ValidateEntityAndName(entityId, property);
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(remainingTicks, -1);
        if (!Enum.IsDefined(operation) || !Enum.IsDefined(stage))
        {
            throw new ArgumentOutOfRangeException(nameof(operation));
        }

        Append(new WorldCommand(WorldCommandKind.SetModifier, entityId, Name: property, Source: source,
            Value: value, RemainingTicks: remainingTicks, Priority: priority, Operation: operation, Stage: stage));
    }

    public void RemoveModifier(string source, int entityId, string property)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ValidateEntityAndName(entityId, property);
        Append(new WorldCommand(WorldCommandKind.RemoveModifier, entityId, Name: property, Source: source));
    }

    public void Grant(string source, int entityId, GrantKind kind, string name, int remainingTicks = -1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ValidateEntityAndName(entityId, name);
        ArgumentOutOfRangeException.ThrowIfLessThan(remainingTicks, -1);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Append(new WorldCommand(WorldCommandKind.Grant, entityId, Name: name, Source: source,
            RemainingTicks: remainingTicks, GrantKind: kind));
    }

    public void Revoke(string source, int entityId, GrantKind kind, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ValidateEntityAndName(entityId, name);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Append(new WorldCommand(WorldCommandKind.Revoke, entityId, Name: name, Source: source, GrantKind: kind));
    }

    public void SetSpatialBounds(int entityId, Bounds2 bounds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(entityId);
        if (!bounds.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(bounds));
        }

        Append(new WorldCommand(WorldCommandKind.SetSpatialBounds, entityId, null, null, 0, bounds));
    }

    public void RemoveSpatialBounds(int entityId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(entityId);
        Append(new WorldCommand(WorldCommandKind.RemoveSpatialBounds, entityId));
    }

    public void Clear() => _count = 0;

    /// <summary>Applies queued commands in insertion order; schedule barriers call this automatically.</summary>
    public void Flush(World world)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(world);
        for (var index = 0; index < _count; index++)
        {
            var command = _commands[index];
            switch (command.Kind)
            {
                case WorldCommandKind.SetProperty:
                    {
                        var entity = RequireEntity(world, command.EntityId);
                        if (!entity.TrySetProperty(world, command.Name!, command.Value))
                        {
                            throw new InvalidOperationException($"Entity {entity.Id} has no writable property '{command.Name}'.");
                        }

                        break;
                    }
                case WorldCommandKind.AddEntity:
                    world.Add(command.Entity!);
                    break;
                case WorldCommandKind.RemoveEntity:
                    RequireEntity(world, command.EntityId).ToRemove = true;
                    break;
                case WorldCommandKind.SetSpatialBounds:
                    world.SetSpatialBounds(RequireEntity(world, command.EntityId), command.Bounds);
                    break;
                case WorldCommandKind.AddTag:
                    RequireEntity(world, command.EntityId).AddTag(command.Name!);
                    break;
                case WorldCommandKind.RemoveTag:
                    RequireEntity(world, command.EntityId).RemoveTag(command.Name!);
                    break;
                case WorldCommandKind.AddCapability:
                    RequireEntity(world, command.EntityId).AddCapability(command.Name!);
                    break;
                case WorldCommandKind.RemoveCapability:
                    RequireEntity(world, command.EntityId).RemoveCapability(command.Name!);
                    break;
                case WorldCommandKind.SetLink:
                    RequireEntity(world, command.EntityId).SetLink(command.Name!, command.TargetEntityId);
                    break;
                case WorldCommandKind.RemoveLink:
                    RequireEntity(world, command.EntityId).RemoveLink(command.Name!);
                    break;
                case WorldCommandKind.SetModifier:
                    world.SetModifier(command.Source!, RequireEntity(world, command.EntityId), command.Name!,
                        command.Operation, command.Value, command.RemainingTicks, command.Priority, command.Stage);
                    break;
                case WorldCommandKind.RemoveModifier:
                    world.RemoveModifier(command.Source!, RequireEntity(world, command.EntityId), command.Name!);
                    break;
                case WorldCommandKind.Grant:
                    world.Grant(command.Source!, RequireEntity(world, command.EntityId), command.GrantKind,
                        command.Name!, command.RemainingTicks);
                    break;
                case WorldCommandKind.Revoke:
                    world.Revoke(command.Source!, RequireEntity(world, command.EntityId), command.GrantKind,
                        command.Name!);
                    break;
                case WorldCommandKind.RemoveSpatialBounds:
                    world.RemoveSpatialBounds(RequireEntity(world, command.EntityId));
                    break;
                default:
                    throw new InvalidOperationException("Unknown deferred World command.");
            }
        }

        Clear();
    }

    private static Entity RequireEntity(World world, int entityId) => world.ById(entityId)
        ?? throw new InvalidOperationException($"Deferred World command references unknown entity {entityId}.");

    private void Append(WorldCommand command)
    {
        EnsureActive();
        EnsureCapacity(checked(_count + 1));
        _commands[_count++] = command;
    }

    private void EnsureActive()
    {
        if (_executionScoped && !_active)
        {
            throw new InvalidOperationException("The scheduled system is not executing.");
        }
    }

    private void AppendEntityName(WorldCommandKind kind, int entityId, string name)
    {
        ValidateEntityAndName(entityId, name);
        Append(new WorldCommand(kind, entityId, Name: name));
    }

    private static void ValidateEntityAndName(int entityId, string name)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(entityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _commands.Length)
        {
            return;
        }

        var capacity = _commands.Length == 0 ? 8 : _commands.Length;
        while (capacity < required)
        {
            capacity = checked(capacity * 2);
        }

        Array.Resize(ref _commands, capacity);
    }

    private enum WorldCommandKind : byte
    {
        SetProperty,
        AddEntity,
        RemoveEntity,
        SetSpatialBounds,
        RemoveSpatialBounds,
        AddTag,
        RemoveTag,
        AddCapability,
        RemoveCapability,
        SetLink,
        RemoveLink,
        SetModifier,
        RemoveModifier,
        Grant,
        Revoke,
    }

    private readonly record struct WorldCommand(
        WorldCommandKind Kind,
        int EntityId = 0,
        Entity? Entity = null,
        string? Name = null,
        double Value = 0,
        Bounds2 Bounds = default,
        string? Source = null,
        int TargetEntityId = 0,
        int RemainingTicks = -1,
        int Priority = 0,
        ModifierOperation Operation = ModifierOperation.Add,
        ModifierStage Stage = ModifierStage.Standard,
        GrantKind GrantKind = GrantKind.Tag);
}
