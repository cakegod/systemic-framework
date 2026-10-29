using System.Runtime.ExceptionServices;
using System.Collections.Immutable;

namespace Systemic.Kernel;

/// <summary>Describes how a system uses a declared schedule resource.</summary>
public enum SystemAccessMode
{
    /// <summary>Reads the resource without modifying it.</summary>
    Read,
    /// <summary>Mutates the resource during system execution.</summary>
    Write,
    /// <summary>Queues a mutation for the ordered wave barrier.</summary>
    DeferredWorldWrite,
}

/// <summary>A declared dependency used to build deterministic parallel schedule waves.</summary>
public readonly record struct SystemAccess(string Resource, SystemAccessMode Mode)
{
    private const string WorldResource = "$world";

    /// <summary>Declares read-only access to the World.</summary>
    public static SystemAccess ReadWorld { get; } = new(WorldResource, SystemAccessMode.Read);
    /// <summary>Exclusive access for operations that mutate World immediately, such as rule dispatch.
    /// A system declaring this access runs in a wave by itself, but may still use parallel row ranges.</summary>
    public static SystemAccess WriteWorld { get; } = new(WorldResource, SystemAccessMode.Write);
    /// <summary>Declares buffered World writes that apply at the end of a schedule wave.</summary>
    public static SystemAccess DeferWorldWrites { get; } = new(WorldResource, SystemAccessMode.DeferredWorldWrite);

    /// <summary>Declares read access to a named application resource.</summary>
    public static SystemAccess ReadResource(string resource) => Create(resource, SystemAccessMode.Read);

    /// <summary>Declares write access to a named application resource.</summary>
    public static SystemAccess WriteResource(string resource) => Create(resource, SystemAccessMode.Write);

    /// <summary>Declares read access to a population column.</summary>
    public static SystemAccess ReadColumn<TKind, TValue>(PopulationColumnKey<TValue> column)
        where TKind : IPopulationKind
        where TValue : unmanaged => CreateColumnAccess<TKind, TValue>(column, SystemAccessMode.Read);

    /// <summary>Declares write access to a population column.</summary>
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

    /// <summary>Reports whether two declared access sets cannot run at the same time.</summary>
    /// <remarks>
    /// The schedule uses this to build waves, and a population storage uses it to admit overlapping
    /// scopes of the same population, so both agree on what "compatible" means.
    /// </remarks>
    internal static bool Conflicts(ImmutableArray<SystemAccess> left, ImmutableArray<SystemAccess> right)
    {
        // An immediate World writer excludes every other system, including systems that
        // declare no World access (or no resources at all).
        if (left.Contains(WriteWorld) || right.Contains(WriteWorld))
        {
            return true;
        }

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

                // A reader only observes pre-barrier state, so a buffered writer in the same wave
                // cannot race it: the buffered commands land at the barrier, after every reader.
                if ((first.Mode == SystemAccessMode.Read && second.Mode == SystemAccessMode.DeferredWorldWrite)
                    || (first.Mode == SystemAccessMode.DeferredWorldWrite && second.Mode == SystemAccessMode.Read))
                {
                    continue;
                }

                return true;
            }
        }

        return false;
    }
}

/// <summary>Opt-in simulation work. Direct population writes must match the declared access set.</summary>
public interface IScheduledKernelSystem
{
    /// <summary>Gets the stable identifier used by schedule diagnostics.</summary>
    string Id { get; }

    /// <summary>Gets the resource reads and writes used to order this system.</summary>
    IReadOnlyList<SystemAccess> Access { get; }

    /// <summary>Executes this system for the supplied logical tick.</summary>
    void Execute(ScheduledSystemContext context);
}

/// <summary>Builds a deterministic schedule; conflicting systems keep registration order.</summary>
public sealed class DeterministicSystemScheduleBuilder
{
    private readonly List<SystemNode> _systems = [];
    private readonly int _workerCount;
    private bool _built;

    /// <summary>Creates a builder for a deterministic access-declared schedule.</summary>
    /// <param name="workerCount">Worker count, or zero to choose a default capped at four.</param>
    public DeterministicSystemScheduleBuilder(int workerCount = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(workerCount);

        // Four logical workers match common low-power development hardware without creating a
        // large worker fleet on high-core-count hosts. Callers can still choose a different cap.
        _workerCount = workerCount == 0 ? Math.Clamp(Environment.ProcessorCount, 1, 4) : workerCount;
    }

    /// <summary>Adds a system to the schedule in registration order.</summary>
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
        ImmutableArray<SystemAccess> access = [.. system.Access];
        if (access.Any(item => string.IsNullOrWhiteSpace(item.Resource)
            || !Enum.IsDefined(item.Mode)))
        {
            throw new ArgumentException($"System '{system.Id}' has an invalid access declaration.", nameof(system));
        }

        _systems.Add(new SystemNode(system, access, _systems.Count));
        return this;
    }

    /// <summary>Builds the schedule once from the registered access declarations.</summary>
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

    private static bool Conflicts(ImmutableArray<SystemAccess> left, ImmutableArray<SystemAccess> right) =>
        SystemAccess.Conflicts(left, right);

    internal sealed record SystemNode(IScheduledKernelSystem System, ImmutableArray<SystemAccess> Access, int Order);
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
        Waves = [.. waves.Select(wave => ImmutableArray.CreateRange(wave.Select(node => node.System.Id)))];
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

    /// <summary>Gets the worker count selected when the schedule was built.</summary>
    public int WorkerCount => _workerCount;

    /// <summary>Gets the number of registered systems.</summary>
    public int SystemCount => _contexts.Length;

    /// <summary>Stable system IDs grouped by their immutable execution wave.</summary>
    public ImmutableArray<ImmutableArray<string>> Waves { get; }

    /// <summary>Executes every schedule wave inside one World transaction.</summary>
    /// <param name="world">World used by each scheduled system.</param>
    /// <param name="tick">Logical tick supplied to each system.</param>
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
            var transaction = world.BeginTransaction();
            try
            {
                foreach (var wave in _waves)
                {
                    Array.Clear(_failures);
                    try
                    {
                        RunWave(wave, world, tick);
                        ThrowFirstFailure(wave);
                        foreach (var node in wave) _contexts[node.Order].ReleasePopulationLeases();
                        FlushDeferredPopulationCommands(wave);
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
            catch (Exception failure)
            {
                try
                {
                    transaction.Rollback();
                }
                catch (Exception rollbackFailure)
                {
                    // Disposal would otherwise replace the system failure with the rollback failure.
                    throw new AggregateException("The tick failed and rollback also failed.",
                        failure, rollbackFailure);
                }

                throw;
            }
            finally
            {
                transaction.Dispose();
            }
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

    /// <summary>Stops the reusable workers owned by this schedule.</summary>
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
            _contexts[node.Order].Prepare(world, tick, systemWorkerCount, node.Access);
            node.System.Execute(_contexts[node.Order]);
        }
        catch (Exception exception)
        {
            _failures[node.Order] = ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            _contexts[node.Order].EndScheduledSystemAccess();
        }
    }

    private void ThrowFirstFailure(DeterministicSystemScheduleBuilder.SystemNode[] wave)
    {
        foreach (var node in wave)
        {
            _failures[node.Order]?.Throw();
        }
    }

    private void FlushDeferredPopulationCommands(DeterministicSystemScheduleBuilder.SystemNode[] wave)
    {
        Dictionary<object, IDeferredPopulationCommands>? byPopulation = null;
        List<IDeferredPopulationCommands>? ordered = null;
        foreach (var node in wave)
        {
            foreach (var commands in _contexts[node.Order].DeferredPopulationCommands)
            {
                byPopulation ??= new Dictionary<object, IDeferredPopulationCommands>(ReferenceEqualityComparer.Instance);
                ordered ??= [];
                if (byPopulation.TryGetValue(commands.PopulationIdentity, out var group))
                {
                    group.MergeFrom(commands);
                }
                else
                {
                    byPopulation.Add(commands.PopulationIdentity, commands);
                    ordered.Add(commands);
                }
            }
        }

        if (ordered is not null)
        {
            foreach (var commands in ordered) commands.FlushAtBarrier();
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
internal interface IReusableDynamicBatch : IReusableWorkBatch;

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
    private long _nextPartition;
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

    public void Run(IReusableWorkBatch batch, int maxWorkers = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0)
        {
            return;
        }

        var activeWorkers = Math.Min(Math.Min(_workerCount, maxWorkers), batch.Count);
        if (activeWorkers == 1)
        {
            ThrowIfDisposed();
            for (var partition = 0; partition < batch.Count; partition++)
            {
                batch.Execute(partition);
            }
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
            _nextPartition = -1;
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
            if (batch is IReusableDynamicBatch)
            {
                long partition;
                while ((partition = Interlocked.Increment(ref _nextPartition)) < batch.Count)
                {
                    batch.Execute((int)partition);
                }

                return;
            }

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
    private bool _canDeferPopulation;
    private int _executionThreadId;
    private int _dispatchingRanges;
    private readonly ReusableWorkerPool _workerPool;
    private readonly WorldCommandStorage _commands;
    private readonly Dictionary<Type, IReusableRangeBatch> _rangeBatches = [];
    private readonly Dictionary<(Type Kind, Type State), IReusableRangeBatch> _populationRangeBatches = [];
    private readonly Dictionary<(Type State, Type Result), IReusableDynamicBatch> _reductionBatches = [];
    private readonly List<IDeferredPopulationCommands> _deferredPopulationCommands = [];
    private readonly List<IDisposable> _populationLeases = [];
    private ImmutableArray<SystemAccess> _access = [];
    private World? _previousSystemWorld;
    private ImmutableArray<SystemAccess>? _previousSystemAccess;
    private bool _systemAccessEntered;

    internal ScheduledSystemContext(WorldCommandStorage commands, ReusableWorkerPool workerPool)
    {
        _commands = commands;
        _workerPool = workerPool;
    }

    /// <summary>Gets the logical tick for the current execution.</summary>
    public int Tick { get; private set; }

    /// <summary>Gets a read-only view of the World for the current execution.</summary>
    public ReadOnlyWorldView World => new(_world ?? throw new InvalidOperationException("The system is not executing."));

    /// <summary>Gets the system's execution-scoped deferred World command writer.</summary>
    public WorldCommandBuffer Commands => _world is null
        ? throw new InvalidOperationException("The system is not executing.")
        : _commands.CreateWriter();
    /// <summary>Gets the worker count available to this system's range work.</summary>
    public int WorkerCount => _workerCount;
    internal IReadOnlyList<IDeferredPopulationCommands> DeferredPopulationCommands => _deferredPopulationCommands;

    /// <summary>Leases population row identity until disposal or the end of the system's wave.</summary>
    /// <remarks>
    /// The schedule releases this lease after all wave workers finish and also on failure before rollback.
    /// Column access is available only through synchronous scoped callbacks. Declare the actual columns the system reads or writes.
    /// </remarks>
    public PopulationLease<TKind> BorrowPopulation<TKind>(Population<TKind> population) where TKind : IPopulationKind
    {
        var world = _world ?? throw new InvalidOperationException("The system is not executing.");
        if (Environment.CurrentManagedThreadId != _executionThreadId || Volatile.Read(ref _dispatchingRanges) != 0)
            throw new InvalidOperationException("Borrow populations before dispatching range workers.");
        ArgumentNullException.ThrowIfNull(population);
        if (!ReferenceEquals(population.World, world))
            throw new ArgumentException("The population belongs to a different world.", nameof(population));

        var lease = population.Borrow();
        try
        {
            lease.BindSystemAccess(_access, _executionThreadId);
            _populationLeases.Add(lease);
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    /// <summary>Creates partition-local population commands applied together at this wave's barrier.</summary>
    /// <remarks>
    /// Declare <see cref="SystemAccess.DeferWorldWrites"/> for the system. Fill each buffer from at most one
    /// partition and finish all range work before returning. Removals from every system in the wave refer to the
    /// rows at the barrier before any queued removals; spawns merge in system registration and partition order.
    /// Buffers expire when this execution ends and cannot be flushed directly. Population commands apply before
    /// deferred World commands at the barrier. Complete all population leases before returning from the system.
    /// </remarks>
    public PopulationCommandBuffer<TKind>[] DeferPopulationCommands<TKind>(Population<TKind> population, int partitionCount)
        where TKind : unmanaged, IPopulationKind, IPopulationSchema<TKind>
    {
        var world = _world ?? throw new InvalidOperationException("The system is not executing.");
        if (Environment.CurrentManagedThreadId != _executionThreadId || Volatile.Read(ref _dispatchingRanges) != 0)
            throw new InvalidOperationException("Create deferred population buffers before dispatching range workers.");
        if (!_canDeferPopulation)
            throw new InvalidOperationException("Deferred population commands require declared World write access.");
        ArgumentNullException.ThrowIfNull(population);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(partitionCount);
        if (!ReferenceEquals(population.World, world))
            throw new ArgumentException("The population belongs to a different world.", nameof(population));

        var buffers = new PopulationCommandBuffer<TKind>[partitionCount];
        for (var index = 0; index < partitionCount; index++)
        {
            buffers[index] = new PopulationCommandBuffer<TKind>(population, scheduled: true);
            _deferredPopulationCommands.Add(buffers[index]);
        }

        return buffers;
    }

    /// <summary>Registers recovery for external state if the schedule transaction rolls back.</summary>
    public void OnRollback(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _ = _world ?? throw new InvalidOperationException("The system is not executing.");
        _commands.OnRollback(action);
    }

    /// <summary>Runs callbacks over non-overlapping row ranges, using the schedule's reusable workers.</summary>
    /// <typeparam name="TState">State shared read-only by the range callbacks.</typeparam>
    /// <param name="state">State passed to every callback.</param>
    /// <param name="rowCount">Number of rows to partition.</param>
    /// <param name="executeRange">Callback receiving the state, partition index, and assigned range.</param>
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

        BeginRangeDispatch();
        try
        {
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
        finally
        {
            Volatile.Write(ref _dispatchingRanges, 0);
        }
    }

    /// <summary>Runs fixed disjoint partitions with callback-scoped access to declared population columns.</summary>
    /// <typeparam name="TKind">The population schema.</typeparam>
    /// <typeparam name="TState">Caller-owned state passed to each partition.</typeparam>
    /// <param name="lease">The population lease acquired before dispatch.</param>
    /// <param name="state">State passed to every callback.</param>
    /// <param name="executeRange">Callback for each scheduler-assigned partition.</param>
    public void ForEachPopulationRange<TKind, TState>(
        PopulationLease<TKind> lease,
        TState state,
        PopulationRangeAction<TKind, TState> executeRange)
        where TKind : IPopulationKind
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(executeRange);
        var world = _world ?? throw new InvalidOperationException("The system is not executing.");
        if (!ReferenceEquals(lease.WorldIdentity, world))
            throw new ArgumentException("The population lease belongs to a different world.", nameof(lease));

        var rowCount = lease.Count;
        if (rowCount == 0)
            return;

        BeginRangeDispatch();
        try
        {
            var key = (typeof(TKind), typeof(TState));
            if (!_populationRangeBatches.TryGetValue(key, out var existing))
            {
                existing = new PopulationRangeBatch<TKind, TState>();
                _populationRangeBatches.Add(key, existing);
            }

            var batch = (PopulationRangeBatch<TKind, TState>)existing;
            batch.Configure(lease, state, rowCount, Math.Min(rowCount, _workerCount), _access, executeRange);
            _workerPool.Run(batch);
            batch.ThrowFirstFailure();
        }
        finally
        {
            Volatile.Write(ref _dispatchingRanges, 0);
        }
    }

    /// <summary>Runs fixed disjoint partitions without allocating a separate population lease.</summary>
    /// <typeparam name="TKind">The population schema.</typeparam>
    /// <typeparam name="TState">Caller-owned state passed to each partition.</typeparam>
    /// <param name="population">The population whose rows are partitioned.</param>
    /// <param name="state">State passed to every callback.</param>
    /// <param name="executeRange">Callback for each scheduler-assigned partition.</param>
    public void ForEachPopulationRange<TKind, TState>(
        Population<TKind> population,
        TState state,
        PopulationRangeAction<TKind, TState> executeRange)
        where TKind : IPopulationKind
    {
        ArgumentNullException.ThrowIfNull(population);
        ArgumentNullException.ThrowIfNull(executeRange);
        var world = _world ?? throw new InvalidOperationException("The system is not executing.");
        if (!ReferenceEquals(population.World, world))
            throw new ArgumentException("The population belongs to a different world.", nameof(population));

        BeginRangeDispatch();
        var populationLeased = false;
        try
        {
            var rowCount = population.BeginScheduledRangeAccess();
            populationLeased = true;
            if (rowCount == 0)
                return;

            var key = (typeof(TKind), typeof(TState));
            if (!_populationRangeBatches.TryGetValue(key, out var existing))
            {
                existing = new PopulationRangeBatch<TKind, TState>();
                _populationRangeBatches.Add(key, existing);
            }

            var batch = (PopulationRangeBatch<TKind, TState>)existing;
            batch.Configure(population, state, rowCount, Math.Min(rowCount, _workerCount), _access, executeRange);
            _workerPool.Run(batch);
            batch.ThrowFirstFailure();
        }
        finally
        {
            if (populationLeased)
                population.EndScheduledRangeAccess();
            Volatile.Write(ref _dispatchingRanges, 0);
        }
    }

    /// <summary>Maps fixed logical row partitions concurrently, then merges their results in partition order.</summary>
    /// <remarks>
    /// Partition count is independent of worker count, so changing workers does not change the reduction order.
    /// Map functions must avoid overlapping writes and order-dependent shared state. Floating-point portability
    /// still depends on the map and merge operations and the execution platform.
    /// </remarks>
    public TResult ReduceRanges<TState, TResult>(
        TState state,
        int rowCount,
        int partitionCount,
        TResult identity,
        Func<TState, int, PopulationRange, TResult> map,
        Func<TResult, TResult, TResult> merge)
    {
        _ = _world ?? throw new InvalidOperationException("The system is not executing.");
        ArgumentOutOfRangeException.ThrowIfNegative(rowCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(partitionCount);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(merge);
        if (rowCount == 0) return identity;

        BeginRangeDispatch();
        try
        {
            var key = (typeof(TState), typeof(TResult));
            if (!_reductionBatches.TryGetValue(key, out var existing))
            {
                existing = new ReductionBatch<TState, TResult>();
                _reductionBatches.Add(key, existing);
            }

            var batch = (ReductionBatch<TState, TResult>)existing;
            batch.Configure(state, rowCount, Math.Min(rowCount, partitionCount), map);
            try
            {
                _workerPool.Run(batch, _workerCount);
                return batch.Merge(identity, merge);
            }
            finally
            {
                batch.Clear();
            }
        }
        finally
        {
            Volatile.Write(ref _dispatchingRanges, 0);
        }
    }

    private void BeginRangeDispatch()
    {
        if (_world is null || Environment.CurrentManagedThreadId != _executionThreadId
            || Interlocked.CompareExchange(ref _dispatchingRanges, 1, 0) != 0)
            throw new InvalidOperationException("Range dispatch belongs to the executing system thread and cannot be nested.");
    }

    internal void Prepare(World world, int tick, int workerCount, ImmutableArray<SystemAccess> access)
    {
        _world = world;
        Tick = tick;
        _workerCount = workerCount;
        _access = access;
        _executionThreadId = Environment.CurrentManagedThreadId;
        _canDeferPopulation = access.Contains(SystemAccess.DeferWorldWrites) || access.Contains(SystemAccess.WriteWorld);
        _commands.Activate();
        world.EnterScheduledSystem(access, out _previousSystemWorld, out _previousSystemAccess);
        _systemAccessEntered = true;
    }

    internal void RegisterRollbackActions(WorldTransaction transaction) =>
        _commands.RegisterRollbackActions(transaction);

    internal void FlushCommands(World world) => _commands.FlushAtBarrier(world);

    internal void ReleasePopulationLeases()
    {
        foreach (var lease in _populationLeases) lease.Dispose();
        _populationLeases.Clear();
    }

    internal void EndScheduledSystemAccess()
    {
        if (!_systemAccessEntered)
            return;

        _world!.ExitScheduledSystem(_previousSystemWorld, _previousSystemAccess);
        _systemAccessEntered = false;
        _previousSystemWorld = null;
        _previousSystemAccess = null;
    }

    internal void EndExecution()
    {
        try
        {
            ReleasePopulationLeases();
            foreach (var commands in _deferredPopulationCommands) commands.Deactivate();
            _deferredPopulationCommands.Clear();
            _commands.Deactivate();
        }
        finally
        {
            EndScheduledSystemAccess();

            _world = null;
            _workerCount = 1;
            _executionThreadId = 0;
            _canDeferPopulation = false;
            _access = [];
            _previousSystemWorld = null;
            _previousSystemAccess = null;
        }
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

    private sealed class PopulationRangeBatch<TKind, TState> : IReusableRangeBatch
        where TKind : IPopulationKind
    {
        private readonly object _group = new();
        private PopulationLease<TKind>? _lease;
        private Population<TKind>? _population;
        private TState _state = default!;
        private int _rowCount;
        private ImmutableArray<SystemAccess> _access = [];
        private PopulationRangeAction<TKind, TState> _executeRange = null!;
        private PopulationScopeLifetime[] _lifetimes = [];
        private ExceptionDispatchInfo?[] _failures = [];

        public int Count { get; private set; }

        public void Configure(
            PopulationLease<TKind> lease,
            TState state,
            int rowCount,
            int partitionCount,
            ImmutableArray<SystemAccess> access,
            PopulationRangeAction<TKind, TState> executeRange)
        {
            _lease = lease;
            _population = null;
            ConfigureShared(state, rowCount, partitionCount, access, executeRange);
        }

        public void Configure(
            Population<TKind> population,
            TState state,
            int rowCount,
            int partitionCount,
            ImmutableArray<SystemAccess> access,
            PopulationRangeAction<TKind, TState> executeRange)
        {
            _population = population;
            _lease = null;
            ConfigureShared(state, rowCount, partitionCount, access, executeRange);
        }

        private void ConfigureShared(
            TState state,
            int rowCount,
            int partitionCount,
            ImmutableArray<SystemAccess> access,
            PopulationRangeAction<TKind, TState> executeRange)
        {
            _state = state;
            _rowCount = rowCount;
            _access = access;
            _executeRange = executeRange;
            Count = partitionCount;
            if (_lifetimes.Length < partitionCount)
            {
                var previousLength = _lifetimes.Length;
                Array.Resize(ref _lifetimes, partitionCount);
                for (var index = previousLength; index < partitionCount; index++)
                    _lifetimes[index] = new PopulationScopeLifetime();
            }

            if (_failures.Length < partitionCount)
                Array.Resize(ref _failures, partitionCount);
            Array.Clear(_failures, 0, partitionCount);
        }

        public void Execute(int partition)
        {
            var start = (int)((long)_rowCount * partition / Count);
            var end = (int)((long)_rowCount * (partition + 1) / Count);
            var range = new PopulationRange(start, end - start);
            var lifetime = _lifetimes[partition];
            var generation = 0L;
            var scopeStarted = false;
            try
            {
                if (_population is { } population)
                    generation = population.BeginScheduledRangeScope(range, lifetime, _access, _group);
                else
                    generation = _lease!.BeginScopedAccess(range, lifetime, _access, parallel: true, _group);
                scopeStarted = true;
                var scope = _population is { } owner
                    ? new PopulationAccessScope<TKind>(owner, lifetime, generation)
                    : new PopulationAccessScope<TKind>(_lease!, lifetime, generation);
                _executeRange(_state, partition, ref scope);
            }
            catch (Exception exception)
            {
                _failures[partition] = ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                if (scopeStarted)
                {
                    if (_population is { } population)
                        population.EndScheduledRangeScope(lifetime, generation, _group);
                    else
                        _lease!.EndScopedAccess(lifetime, generation, parallel: true, _group);
                }
            }
        }

        public void ThrowFirstFailure()
        {
            for (var partition = 0; partition < Count; partition++)
                _failures[partition]?.Throw();
        }
    }

    private sealed class ReductionBatch<TState, TResult> : IReusableDynamicBatch
    {
        private TState _state = default!;
        private int _rowCount;
        private Func<TState, int, PopulationRange, TResult> _map = null!;
        private TResult[] _results = [];
        private ExceptionDispatchInfo?[] _failures = [];
        public int Count { get; private set; }

        public void Configure(TState state, int rowCount, int partitionCount,
            Func<TState, int, PopulationRange, TResult> map)
        {
            _state = state;
            _rowCount = rowCount;
            _map = map;
            Count = partitionCount;
            if (_results.Length < Count) Array.Resize(ref _results, Count);
            if (_failures.Length < Count) Array.Resize(ref _failures, Count);
        }

        public void Execute(int partition)
        {
            var start = (int)((long)_rowCount * partition / Count);
            var end = (int)((long)_rowCount * (partition + 1) / Count);
            try
            {
                _results[partition] = _map(_state, partition, new PopulationRange(start, end - start));
            }
            catch (Exception exception)
            {
                _failures[partition] = ExceptionDispatchInfo.Capture(exception);
            }
        }

        public TResult Merge(TResult identity, Func<TResult, TResult, TResult> merge)
        {
            for (var partition = 0; partition < Count; partition++) _failures[partition]?.Throw();
            for (var partition = 0; partition < Count; partition++) identity = merge(identity, _results[partition]);
            return identity;
        }

        public void Clear()
        {
            Array.Clear(_results, 0, Count);
            Array.Clear(_failures, 0, Count);
            _state = default!;
            _map = null!;
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

    /// <summary>Reads the stored property value without applying modifiers.</summary>
    /// <param name="entityId">Entity to read.</param>
    /// <param name="property">Property name.</param>
    /// <param name="value">Stored value when the property exists.</param>
    /// <returns><see langword="true"/> when the entity exists and stores the property.</returns>
    /// <exception cref="ArgumentException"><paramref name="property"/> is null, empty or whitespace.</exception>
    public bool TryGetBaseProperty(int entityId, string property, out double value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(property);
        var entity = world.ById(entityId);
        if (entity is { ToRemove: false })
        {
            return entity.TryGetProperty(world, property, out value);
        }

        value = 0;
        return false;
    }

    /// <summary>Returns the entity IDs that currently carry the tag.</summary>
    /// <param name="tag">Tag to enumerate.</param>
    /// <returns>The matching entity IDs in ascending order, or empty memory when none match.</returns>
    /// <exception cref="ArgumentException"><paramref name="tag"/> is null, empty or whitespace.</exception>
    public ReadOnlyMemory<int> EntitiesWithTag(string tag) => world.EntitiesWithTag(tag);

    /// <summary>Checks that bounds fit the spatial index without changing world state.</summary>
    /// <param name="bounds">Bounds to validate.</param>
    /// <exception cref="ArgumentOutOfRangeException">The bounds cannot be indexed.</exception>
    public void ValidateSpatialBounds(Bounds2 bounds) => world.ValidateSpatialBounds(bounds);
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
        if (_executionScoped)
        {
            throw new InvalidOperationException("Scheduled World commands flush only at their barrier.");
        }

        FlushCore(world);
    }

    internal void FlushAtBarrier(World world)
    {
        EnsureActive();
        if (!_executionScoped)
        {
            throw new InvalidOperationException("Only scheduled World commands can flush at a schedule barrier.");
        }

        FlushCore(world);
    }

    private void FlushCore(World world)
    {
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
                    world.Remove(RequireEntity(world, command.EntityId));
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
