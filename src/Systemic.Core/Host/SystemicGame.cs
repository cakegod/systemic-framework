using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Systemic.Kernel;
using Systemic.Content;
using Systemic.Extensions;
using Systemic.Rules;
using Systemic.Save;
using Systemic.Ui;

namespace Systemic.Host;

/// <summary>Typed system phase for orchestration outside the bounded JSON rule vocabulary.</summary>
public interface ISystemicSystem
{
    /// <summary>Stable identifier used for scheduler diagnostics and tick timing.</summary>
    string Id { get; }
    /// <summary>Sets registration priority for conflicting systems and their barrier order.</summary>
    /// <remarks>Compatible systems may share a concurrent wave, so this value does not order independent systems.</remarks>
    int Order => 0;
    /// <summary>
    /// Declares conflicts for the deterministic system schedule. Systems that do not opt in to
    /// parallel execution run as exclusive World writers.
    /// </summary>
    IReadOnlyList<SystemAccess> Access => SystemicSystemDefaults.ExclusiveWorldWrite;

    /// <summary>Runs the system for the current host tick.</summary>
    /// <param name="context">Execution-scoped host and Kernel operations for this system.</param>
    void Step(SystemicSystemContext context);
}

internal static class SystemicSystemDefaults
{
    public static IReadOnlyList<SystemAccess> ExclusiveWorldWrite { get; } =
        Array.AsReadOnly([SystemAccess.WriteWorld]);
}

public interface ISystemicStatefulExtension
{
    string Id { get; }
    JsonElement CaptureState();
    void RestoreState(JsonElement state);
}

/// <summary>Marks state that World transactions already journal, so the host does not snapshot it every tick.</summary>
/// <remarks>
/// Save and load still use <see cref="ISystemicStatefulExtension.CaptureState"/>; only the per-tick
/// rollback snapshot is skipped. Implement this only when every mutation made during a tick is
/// either World state covered by the journal or registers its own rollback on a World transaction.
/// </remarks>
public interface ISaveOnlyStateExtension : ISystemicStatefulExtension
{
}

/// <summary>Optional in-memory snapshot path for stateful extensions with a cheaper tick snapshot.</summary>
/// <remarks>The returned snapshot must remain immutable until the enclosing tick commits or rolls back.</remarks>
public interface ISystemicTransactionalState : ISystemicStatefulExtension
{
    object CaptureTransactionState();
    void RestoreTransactionState(object state);
}

/// <summary>A reusable snapshot retains its captured state until exactly one callback is invoked.</summary>
public interface ISystemicTransactionSnapshot
{
    Action OnCommit { get; }
    Action OnRollback { get; }
}

/// <summary>Optional pooled snapshot path for systems with allocation-sensitive fixed ticks.</summary>
public interface ISystemicReusableTransactionalState : ISystemicStatefulExtension
{
    ISystemicTransactionSnapshot CaptureTransactionSnapshot();
}

/// <summary>Execution-scoped host and Kernel operations for one system.</summary>
public sealed class SystemicSystemContext
{
    private readonly World _world;
    private readonly RuleEngine _contentRules;
    private readonly IReadOnlyDictionary<Type, object> _services;
    private readonly SimulationFactObserver? _observeFact;
    private readonly WorldCommandBuffer _standaloneCommands;
    private ScheduledSystemContext? _kernelContext;
    private readonly int _standaloneTick;
    private readonly bool _standalone;
    private bool _canWriteWorld;
    private bool _canDeferWorldWrites;
    private DeferredFactQueue? _deferredFacts;

    /// <summary>Creates a single-use context for a direct system test or standalone harness.</summary>
    public SystemicSystemContext(World world, RuleEngine contentRules, int tick)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _contentRules = contentRules ?? throw new ArgumentNullException(nameof(contentRules));
        _services = ImmutableDictionary<Type, object>.Empty;
        _standaloneCommands = new WorldCommandBuffer();
        _standaloneTick = tick;
        _standalone = true;
        _canWriteWorld = true;
    }

    internal SystemicSystemContext(
        World world,
        RuleEngine contentRules,
        IReadOnlyDictionary<Type, object> services,
        SimulationFactObserver? observeFact)
    {
        _world = world;
        _contentRules = contentRules;
        _services = services;
        _observeFact = observeFact;
        _standaloneCommands = default;
    }

    /// <summary>Gets the logical tick currently executing.</summary>
    public int Tick => _kernelContext?.Tick ?? (_standalone
        ? _standaloneTick
        : throw new InvalidOperationException("System context operations are available only while the system executes."));

    /// <summary>Gets the worker limit for this execution; standalone contexts use one worker.</summary>
    public int WorkerCount => _kernelContext?.WorkerCount ?? (_standalone ? 1 :
        throw new InvalidOperationException("System context operations are available only while the system executes."));

    /// <summary>Gets a read-only view of the current World.</summary>
    public ReadOnlyWorldView World => _kernelContext?.World ?? (_standalone
        ? new ReadOnlyWorldView(_world)
        : throw new InvalidOperationException("System context operations are available only while the system executes."));

    /// <summary>Gets this system's execution-scoped deferred World command writer.</summary>
    public WorldCommandBuffer Commands => _kernelContext?.Commands ?? (_standalone
        ? _standaloneCommands
        : throw new InvalidOperationException("System context operations are available only while the system executes."));

    /// <summary>Gets whether the context is running under a host schedule.</summary>
    public bool IsScheduledExecution => _kernelContext is not null;

    /// <summary>Gets a registered host service by its registration type.</summary>
    /// <typeparam name="TService">Service type registered with the game builder.</typeparam>
    /// <returns>The registered service instance.</returns>
    /// <exception cref="KeyNotFoundException">The service type has not been registered.</exception>
    public TService GetService<TService>() where TService : notnull =>
        _services.TryGetValue(typeof(TService), out var service)
            ? (TService)service
            : throw new KeyNotFoundException($"Service '{typeof(TService).FullName}' is not registered.");

    /// <summary>Checks whether this context belongs to the supplied World instance.</summary>
    public bool IsForWorld(World world) => ReferenceEquals(_world, world);

    /// <summary>Returns mutable World access only to systems declaring <see cref="SystemAccess.WriteWorld"/>.</summary>
    /// <exception cref="InvalidOperationException">The scheduled system did not declare immediate World write access.</exception>
    public World GetWritableWorld()
    {
        EnsureImmediateWorldWriteAccess();
        FlushWorldCommands();
        return _world;
    }

    /// <summary>Sets a writable numeric entity property through immediate World access.</summary>
    /// <exception cref="InvalidOperationException">The system lacks immediate World write access or the property is not writable.</exception>
    public void SetProperty(Entity entity, string property, double value)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var world = GetWritableWorld();
        if (!entity.TrySetProperty(world, property, value))
        {
            throw new InvalidOperationException($"Entity {entity.Id} has no writable property '{property}'.");
        }
    }

    /// <summary>Queues a fact for dispatch after every system has run this tick.</summary>
    /// <remarks>Requires <see cref="SystemAccess.DeferWorldWrites"/> or <see cref="SystemAccess.WriteWorld"/>. Queued facts dispatch serially in system registration order after the schedule, so no other system observes their effects during the same tick. A standalone context dispatches immediately.</remarks>
    /// <exception cref="InvalidOperationException">The system lacks deferred World write access.</exception>
    public void DeferFact(SimulationFact fact)
    {
        if (_kernelContext is not null)
        {
            if (!_canWriteWorld && !_canDeferWorldWrites)
            {
                throw new InvalidOperationException(
                    "Deferred facts require SystemAccess.DeferWorldWrites or SystemAccess.WriteWorld; deferred World commands remain available without them.");
            }

            (_deferredFacts ?? throw new InvalidOperationException("The deferred fact queue is unavailable during this execution."))
                .Facts.Add(fact);
            return;
        }

        if (_standalone)
        {
            _contentRules.Dispatch(_world, fact, _observeFact);
            return;
        }

        throw new InvalidOperationException("System context operations are available only while the system executes.");
    }

    /// <summary>Dispatches a simulation fact against the current World.</summary>
    /// <exception cref="InvalidOperationException">The system did not declare immediate World write access.</exception>
    public RuleDispatchResult Publish(SimulationFact fact)
    {
        EnsureImmediateWorldWriteAccess();
        FlushWorldCommands();
        return _contentRules.Dispatch(_world, fact, _observeFact);
    }

    /// <summary>Registers recovery for external state if the enclosing scheduled tick rolls back.</summary>
    /// <param name="restoreExternalState">Action that restores state changed by this system.</param>
    /// <exception cref="InvalidOperationException">The context is not executing in the host schedule.</exception>
    public void OnRollback(Action restoreExternalState)
    {
        ArgumentNullException.ThrowIfNull(restoreExternalState);
        if (_kernelContext is not { } kernelContext)
            throw new InvalidOperationException("Rollback actions can be registered only during a host-scheduled execution.");
        kernelContext.OnRollback(restoreExternalState);
    }

    /// <summary>Runs work over non-overlapping portions of a logical row range.</summary>
    /// <typeparam name="TState">State shared read-only by the range callbacks.</typeparam>
    /// <param name="state">State passed to each callback.</param>
    /// <param name="rowCount">Number of rows to partition.</param>
    /// <param name="executeRange">Callback receiving the state, partition index, and assigned rows.</param>
    /// <remarks>Scheduled callbacks may run concurrently. Standalone contexts call the callback once for the whole range.</remarks>
    public void ForEachRange<TState>(TState state, int rowCount, Action<TState, int, PopulationRange> executeRange)
    {
        if (_kernelContext is { } kernelContext)
        {
            kernelContext.ForEachRange(state, rowCount, executeRange);
            return;
        }

        ArgumentOutOfRangeException.ThrowIfNegative(rowCount);
        ArgumentNullException.ThrowIfNull(executeRange);
        if (_standalone && rowCount > 0)
            executeRange(state, 0, new PopulationRange(0, rowCount));
        else if (!_standalone)
            throw new InvalidOperationException("System context operations are available only while the system executes.");
    }

    /// <summary>Runs work with callback-scoped access to a population's declared columns.</summary>
    /// <typeparam name="TKind">The population schema.</typeparam>
    /// <typeparam name="TState">Caller-owned state passed to each callback.</typeparam>
    /// <param name="lease">The population lease acquired before dispatch.</param>
    /// <param name="state">State passed to each callback.</param>
    /// <param name="executeRange">Callback receiving a scoped view of its assigned rows.</param>
    public void ForEachPopulationRange<TKind, TState>(
        PopulationLease<TKind> lease,
        TState state,
        PopulationRangeAction<TKind, TState> executeRange)
        where TKind : IPopulationKind
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(executeRange);
        if (_kernelContext is { } kernelContext)
        {
            kernelContext.ForEachPopulationRange(lease, state, executeRange);
            return;
        }

        if (!_standalone)
            throw new InvalidOperationException("System context operations are available only while the system executes.");
        if (!ReferenceEquals(lease.WorldIdentity, _world))
            throw new ArgumentException("The population lease belongs to a different world.", nameof(lease));

        var range = new PopulationRange(0, lease.Count);
        if (range.Length == 0)
            return;

        lease.WithAccess(range, new StandalonePopulationRangeState<TKind, TState>(state, executeRange),
            static (callbackState,
                scoped ref scope) =>
                callbackState.Callback(callbackState.State, 0, ref scope));
    }

    /// <summary>Runs work with callback-scoped access without allocating a separate population lease.</summary>
    /// <typeparam name="TKind">The population schema.</typeparam>
    /// <typeparam name="TState">Caller-owned state passed to each callback.</typeparam>
    /// <param name="population">The population whose rows are partitioned.</param>
    /// <param name="state">State passed to each callback.</param>
    /// <param name="executeRange">Callback receiving a scoped view of its assigned rows.</param>
    public void ForEachPopulationRange<TKind, TState>(
        Population<TKind> population,
        TState state,
        PopulationRangeAction<TKind, TState> executeRange)
        where TKind : IPopulationKind
    {
        ArgumentNullException.ThrowIfNull(population);
        ArgumentNullException.ThrowIfNull(executeRange);
        if (_kernelContext is { } kernelContext)
        {
            kernelContext.ForEachPopulationRange(population, state, executeRange);
            return;
        }

        if (!_standalone)
            throw new InvalidOperationException("System context operations are available only while the system executes.");
        if (!ReferenceEquals(population.World, _world))
            throw new ArgumentException("The population belongs to a different world.", nameof(population));

        var range = new PopulationRange(0, population.Count);
        if (range.Length == 0)
            return;

        population.WithAccess(range, new StandalonePopulationRangeState<TKind, TState>(state, executeRange),
            static (callbackState,
                scoped ref scope) =>
                callbackState.Callback(callbackState.State, 0, ref scope));
    }

    /// <summary>Maps fixed partitions and merges their results in partition order.</summary>
    /// <typeparam name="TState">State read by the map callback.</typeparam>
    /// <typeparam name="TResult">Mapped value merged into the result.</typeparam>
    /// <param name="state">State passed to each map callback.</param>
    /// <param name="rowCount">Number of rows to partition.</param>
    /// <param name="partitionCount">Logical partition count, independent of worker count.</param>
    /// <param name="identity">Initial value supplied to the merge callback.</param>
    /// <param name="map">Maps one partition to a result.</param>
    /// <param name="merge">Combines results in ascending partition order.</param>
    /// <remarks>Portable results still depend on deterministic map and merge operations.</remarks>
    public TResult ReduceRanges<TState, TResult>(
        TState state,
        int rowCount,
        int partitionCount,
        TResult identity,
        Func<TState, int, PopulationRange, TResult> map,
        Func<TResult, TResult, TResult> merge)
    {
        if (_kernelContext is { } kernelContext)
            return kernelContext.ReduceRanges(state, rowCount, partitionCount, identity, map, merge);
        if (_standalone)
            return ReduceStandalone(state, rowCount, partitionCount, identity, map, merge);
        throw new InvalidOperationException("System context operations are available only while the system executes.");
    }

    /// <summary>Leases a population's row identity and column buffers for this system.</summary>
    /// <typeparam name="TKind">Population schema type.</typeparam>
    /// <param name="population">Population owned by the context's World.</param>
    /// <returns>A lease that must outlive every view obtained from it.</returns>
    public PopulationLease<TKind> BorrowPopulation<TKind>(Population<TKind> population)
        where TKind : IPopulationKind
    {
        ArgumentNullException.ThrowIfNull(population);
        if (!ReferenceEquals(population.World, _world))
            throw new ArgumentException("The population belongs to a different world.", nameof(population));
        if (_kernelContext is { } kernelContext)
            return kernelContext.BorrowPopulation(population);
        if (_standalone)
            return population.Borrow();
        throw new InvalidOperationException("System context operations are available only while the system executes.");
    }

    /// <summary>Creates partition-local population buffers applied at the schedule's wave barrier.</summary>
    /// <typeparam name="TKind">Generated population schema type.</typeparam>
    /// <param name="population">Population owned by the context's World.</param>
    /// <param name="partitionCount">Number of buffers assigned to range partitions.</param>
    /// <returns>Execution-scoped buffers that expire after the system call.</returns>
    /// <remarks>Declare <see cref="SystemAccess.DeferWorldWrites"/> or <see cref="SystemAccess.WriteWorld"/> and do not flush these buffers directly.</remarks>
    public PopulationCommandBuffer<TKind>[] DeferPopulationCommands<TKind>(
        Population<TKind> population,
        int partitionCount)
        where TKind : unmanaged, IPopulationKind, IPopulationSchema<TKind>
    {
        if (_kernelContext is { } kernelContext)
            return kernelContext.DeferPopulationCommands(population, partitionCount);
        if (!_standalone)
            throw new InvalidOperationException("System context operations are available only while the system executes.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(partitionCount);
        var buffers = new PopulationCommandBuffer<TKind>[partitionCount];
        for (var index = 0; index < buffers.Length; index++)
            buffers[index] = new PopulationCommandBuffer<TKind>(population);
        return buffers;
    }

    /// <summary>Applies deferred World commands for a direct test or standalone harness.</summary>
    /// <remarks>
    /// A host schedule applies each system's commands at its wave barrier. A standalone context has
    /// no barrier, so a caller that inspects the World after <c>Step</c> applies them explicitly.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The context is executing inside the host schedule.</exception>
    public void ApplyDeferredCommands()
    {
        if (_kernelContext is not null)
        {
            throw new InvalidOperationException(
                "Deferred World commands are applied by the schedule at its wave barrier; a scheduled system must not apply them.");
        }

        FlushWorldCommands();
    }

    internal void Prepare(
        ScheduledSystemContext kernelContext,
        bool canWriteWorld,
        bool canDeferWorldWrites,
        DeferredFactQueue deferredFacts)
    {
        _kernelContext = kernelContext;
        _canWriteWorld = canWriteWorld;
        _canDeferWorldWrites = canDeferWorldWrites;
        _deferredFacts = deferredFacts;
    }

    internal void Finish()
    {
        _kernelContext = null;
        _canWriteWorld = false;
        _canDeferWorldWrites = false;
        _deferredFacts = null;
    }

    private void FlushWorldCommands()
    {
        if (_kernelContext is { } kernelContext)
        {
            var commands = kernelContext.Commands;
            if (commands.Count > 0) kernelContext.FlushCommands(_world);
        }
        else if (_standalone)
        {
            if (_standaloneCommands.Count > 0) _standaloneCommands.Flush(_world);
        }
    }

    private static TResult ReduceStandalone<TState, TResult>(
        TState state,
        int rowCount,
        int partitionCount,
        TResult identity,
        Func<TState, int, PopulationRange, TResult> map,
        Func<TResult, TResult, TResult> merge)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rowCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(partitionCount);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(merge);
        if (rowCount == 0) return identity;
        var count = Math.Min(rowCount, partitionCount);
        var result = identity;
        for (var partition = 0; partition < count; partition++)
        {
            // WHY: the scheduled range split assigns the remainder to the later partitions. A direct
            // reduction must give the map the same boundaries, or the two paths can disagree.
            var start = (int)((long)rowCount * partition / count);
            var end = (int)((long)rowCount * (partition + 1) / count);
            result = merge(result, map(state, partition, new PopulationRange(start, end - start)));
        }

        return result;
    }

    private readonly record struct StandalonePopulationRangeState<TKind, TState>(
        TState State,
        PopulationRangeAction<TKind, TState> Callback)
        where TKind : IPopulationKind;

    private void EnsureImmediateWorldWriteAccess()
    {
        if (!_canWriteWorld)
        {
            throw new InvalidOperationException(
                "Immediate World writes and rule dispatch require SystemAccess.WriteWorld; deferred World commands remain available without it.");
        }
    }
}

/// <summary>Builds a Core host and configures only modules compiled from the manifest.</summary>
public sealed class SystemicGameBuilder
{
    private readonly List<ISystemicSystem> _systems = [];
    private readonly List<ISystemicStatefulExtension> _statefulExtensions = [];
    private readonly List<FactObserverRegistration> _factObservers = [];
    private readonly List<ISystemicUiBindingProvider> _uiBindingProviders = [];
    private readonly Dictionary<Type, object> _services = [];
    private readonly ExtensionModuleCatalog _extensions = new();
    private readonly ImmutableDictionary<string, JsonElement>? _extensionStates;
    private bool _built;
    private int _scheduledWorkerCount;

    public SystemicGameBuilder(
        ContentPack content,
        World world,
        IReadOnlyDictionary<string, int>? entityIds = null,
        ImmutableDictionary<string, JsonElement>? extensionStates = null)
    {
        Content = content ?? throw new ArgumentNullException(nameof(content));
        World = world ?? throw new ArgumentNullException(nameof(world));
        EntityIds = (entityIds ?? ImmutableDictionary<string, int>.Empty)
            .ToImmutableDictionary(StringComparer.Ordinal);
        _extensionStates = extensionStates;

        if (!content.Modules.IsDefaultOrEmpty)
        {
            foreach (var module in content.Modules)
            {
                module.Module.Configure(this, module.Content);
            }
        }
    }

    public SystemicGameBuilder(ContentPack content, MaterializedContentWorld materialized)
        : this(content, materialized.World, materialized.EntityIds, materialized.ExtensionStates)
    {
    }

    public ContentPack Content { get; }
    public World World { get; }
    public ImmutableDictionary<string, int> EntityIds { get; }
    public bool IsRestoringWorld => _extensionStates is not null;

    /// <summary>Registers a shared service for the host and every system.</summary>
    /// <remarks>A service that implements <see cref="ISystemicStatefulExtension"/> is also registered for save and rollback snapshots; that is intentional, so state carried by a service survives a rolled-back tick.</remarks>
    public SystemicGameBuilder AddService<TService>(TService service)
        where TService : notnull
    {
        EnsureNotBuilt();
        ArgumentNullException.ThrowIfNull(service);
        if (!_services.TryAdd(typeof(TService), service))
        {
            throw new InvalidOperationException($"Service '{typeof(TService).FullName}' is already registered.");
        }

        if (service is ISystemicStatefulExtension extension)
        {
            RegisterStatefulExtension(extension);
        }

        return this;
    }

    public TService GetService<TService>()
        where TService : notnull => _services.TryGetValue(typeof(TService), out var service)
        ? (TService)service
        : throw new KeyNotFoundException($"Service '{typeof(TService).FullName}' is not registered.");

    /// <summary>Registers a typed system for each host tick.</summary>
    /// <remarks>System order and access declarations determine its position and conflicts in the deterministic schedule.</remarks>
    public SystemicGameBuilder AddSystem(ISystemicSystem system)
    {
        EnsureNotBuilt();
        ArgumentNullException.ThrowIfNull(system);
        ArgumentException.ThrowIfNullOrWhiteSpace(system.Id);
        if (_systems.Any(existing => string.Equals(existing.Id, system.Id, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"System '{system.Id}' is already registered.");
        }

        _systems.Add(system);
        return this;
    }

    /// <summary>Sets the number of workers available to scheduled systems and their range work.</summary>
    /// <param name="workerCount">Positive worker count.</param>
    public SystemicGameBuilder WithScheduledWorkerCount(int workerCount)
    {
        EnsureNotBuilt();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workerCount);
        _scheduledWorkerCount = workerCount;
        return this;
    }

    /// <summary>Adds state that the host snapshots before each tick and restores when it rolls back.</summary>
    /// <remarks>Systems and services implementing <see cref="ISystemicStatefulExtension"/> are registered automatically; call this only for standalone state objects.</remarks>
    public SystemicGameBuilder RegisterStatefulExtension(ISystemicStatefulExtension extension)
    {
        EnsureNotBuilt();
        ArgumentNullException.ThrowIfNull(extension);
        ArgumentException.ThrowIfNullOrWhiteSpace(extension.Id);
        if (!_statefulExtensions.Contains(extension, ReferenceEqualityComparer.Instance))
        {
            _statefulExtensions.Add(extension);
        }

        return this;
    }

    public SystemicGameBuilder AddFactObserver(int order, SimulationFactObserver observer)
    {
        EnsureNotBuilt();
        ArgumentNullException.ThrowIfNull(observer);
        _factObservers.Add(new FactObserverRegistration(order, _factObservers.Count, observer));
        return this;
    }

    public SystemicGameBuilder AddUiBindingProvider(ISystemicUiBindingProvider provider)
    {
        EnsureNotBuilt();
        ArgumentNullException.ThrowIfNull(provider);
        if (!_uiBindingProviders.Contains(provider, ReferenceEqualityComparer.Instance))
        {
            _uiBindingProviders.Add(provider);
        }

        return this;
    }

    /// <summary>Registers an extension module in the catalog.</summary>
    /// <remarks>If the module also implements <see cref="ISystemicSystem"/> it joins the tick schedule, and if it implements <see cref="ISystemicStatefulExtension"/> it joins save and rollback snapshots. Modules are not instantiated for you.</remarks>
    public SystemicGameBuilder AddExtension(ISystemicExtensionModule module)
    {
        EnsureNotBuilt();
        ArgumentNullException.ThrowIfNull(module);
        if (module is ISystemicSystem system
            && _systems.Any(existing => string.Equals(existing.Id, system.Id, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"System '{system.Id}' is already registered.");
        }

        _extensions.Add(module);
        if (module is ISystemicSystem addedSystem)
        {
            _systems.Add(addedSystem);
        }

        if (module is ISystemicStatefulExtension stateful)
        {
            RegisterStatefulExtension(stateful);
        }

        return this;
    }

    public SystemicGameBuilder Use<TModule>()
        where TModule : ISystemicExtensionModule, new() => AddExtension(new TModule());

    /// <summary>Builds the host and its deterministic schedule.</summary>
    public SystemicGame Build()
    {
        EnsureNotBuilt();
        _built = true;
        var systemsToRun = new List<ISystemicSystem>(_systems);
        if (!Content.Rules.Schedules.IsDefaultOrEmpty)
        {
            systemsToRun.Add(new ScheduledEventSystem(Content.Rules.Schedules, World, EntityIds));
        }

        // OrderBy is stable: systems with the same Order retain their registration order.
        var systems = systemsToRun.OrderBy(system => system.Order).ToArray();
        var duplicateSystem = systems.GroupBy(system => system.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateSystem is not null)
        {
            throw new InvalidOperationException($"System ID '{duplicateSystem.Key}' is registered more than once.");
        }

        var contentRules = new RuleEngine(Content.Rules, tickRateHz: Content.Manifest.TickRateHz);
        var clock = new FixedStepClock(Content.Manifest.TickRateHz, Content.Manifest.MaxCatchUpSteps);
        var hasSavedPopulationState = _extensionStates?.ContainsKey(PopulationStatefulExtension.ExtensionId) == true;
        var populationStateExtension = World.HasPopulations || hasSavedPopulationState
            ? new PopulationStatefulExtension(World)
            : null;
        var statefulExtensions = systems.OfType<ISystemicStatefulExtension>()
            .Concat(_statefulExtensions)
            .Append(new ClockStatefulExtension(clock))
            .Append(new RuleHistoryStatefulExtension(contentRules, () => World.LogicalTick))
            .Concat(populationStateExtension is null ? [] : [populationStateExtension])
            .Distinct<ISystemicStatefulExtension>(ReferenceEqualityComparer.Instance)
            .OrderBy(extension => extension.Id, StringComparer.Ordinal)
            .ToArray();
        var duplicateStateId = statefulExtensions.GroupBy(extension => extension.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateStateId is not null)
        {
            throw new InvalidOperationException($"Stateful extension ID '{duplicateStateId.Key}' is registered more than once.");
        }

        if (_extensionStates is not null)
        {
            var extensionsById = statefulExtensions.ToDictionary(extension => extension.Id, StringComparer.Ordinal);
            foreach (var (extensionId, state) in _extensionStates
                .OrderBy(pair => pair.Key == PopulationStatefulExtension.ExtensionId ? 0 : 1)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (!extensionsById.TryGetValue(extensionId, out var extension))
                {
                    throw new InvalidOperationException($"Saved state references unregistered extension '{extensionId}'.");
                }

                extension.RestoreState(state);
            }
        }

        var factObservers = _factObservers
            .OrderBy(observer => observer.Order)
            .ThenBy(observer => observer.RegistrationOrder)
            .Select(observer => observer.Observer)
            .ToArray();
        SimulationFactObserver? systemObserver = factObservers.Length == 0
            ? null
            : (fact, emitter) =>
            {
                foreach (var observer in factObservers)
                {
                    observer(fact, emitter);
                }
            };
        var tickStats = new SystemicTickStats(systems.Select(system => system.Id));
        var systemBridges = systems
            .Select(system => new SystemicSystemBridge(system,
                new SystemicSystemContext(World, contentRules,
                    new Dictionary<Type, object>(_services), systemObserver),
                tickStats.Get(system.Id),
                new DeferredFactQueue()))
            .ToArray();
        DeterministicSystemSchedule? schedule = null;
        if (systemBridges.Length > 0)
        {
            var scheduleBuilder = new DeterministicSystemScheduleBuilder(_scheduledWorkerCount);
            foreach (var bridge in systemBridges)
            {
                scheduleBuilder.Add(bridge);
            }

            schedule = scheduleBuilder.Build();
        }

        return new SystemicGame(Content, World, schedule, contentRules,
            EntityIds, statefulExtensions, new Dictionary<Type, object>(_services), factObservers, clock, tickStats,
            [.. _uiBindingProviders], [.. systemBridges.Select(bridge => bridge.DeferredFacts)]);
    }

    private void EnsureNotBuilt()
    {
        if (_built)
        {
            throw new InvalidOperationException("A systemic game builder can only build once.");
        }
    }
}

public sealed class SystemicGame : IDisposable
{
    private readonly DeterministicSystemSchedule? _schedule;
    private readonly RuleEngine _contentRules;
    private readonly IReadOnlyDictionary<Type, object> _services;
    private readonly SimulationFactObserver[] _factObservers;
    private int _disposed;

    internal SystemicGame(
        ContentPack content,
        World world,
        DeterministicSystemSchedule? schedule,
        RuleEngine contentRules,
        ImmutableDictionary<string, int> entityIds,
        ISystemicStatefulExtension[] statefulExtensions,
        IReadOnlyDictionary<Type, object> services,
        SimulationFactObserver[] factObservers,
        FixedStepClock clock,
        SystemicTickStats tickStats,
        ISystemicUiBindingProvider[] uiBindingProviders,
        DeferredFactQueue[] deferredFactQueues)
    {
        Content = content;
        World = world;
        _schedule = schedule;
        _contentRules = contentRules;
        EntityIds = entityIds;
        StatefulExtensions = statefulExtensions;
        _services = services;
        _factObservers = factObservers;
        UiBindingProviders = uiBindingProviders;
        Clock = clock;
        TickStats = tickStats;
        _deferredFactQueues = deferredFactQueues;
    }

    private readonly DeferredFactQueue[] _deferredFactQueues;
    private readonly List<SimulationFact> _factBatch = new(64);

    public static SystemicGameBuilder FromDirectory(string rootDirectory, int seed, double valueScale = 1)
    {
        var content = ContentPackLoader.Load(rootDirectory);
        var materialized = content.CreateWorld(seed, valueScale);
        return new SystemicGameBuilder(content, materialized);
    }

    public ContentPack Content { get; }
    public World World { get; }
    public FixedStepClock Clock { get; }
    public SystemicTickStats TickStats { get; }
    public ImmutableArray<ImmutableArray<string>> SchedulerWaves =>
        _schedule?.Waves ?? [];
    public int SchedulerWorkerCount => _schedule?.WorkerCount ?? 0;
    public ImmutableDictionary<string, int> EntityIds { get; }
    public IReadOnlyList<ISystemicStatefulExtension> StatefulExtensions { get; }
    public IReadOnlyList<ISystemicUiBindingProvider> UiBindingProviders { get; }

    public int EntityId(string contentId) => EntityIds.TryGetValue(contentId, out var id)
        ? id
        : throw new KeyNotFoundException($"Content entity '{contentId}' is not part of the materialized world.");

    public TService GetService<TService>()
        where TService : notnull => _services.TryGetValue(typeof(TService), out var service)
        ? (TService)service
        : throw new KeyNotFoundException($"Service '{typeof(TService).FullName}' is not registered.");

    public FixedStepResult Advance(TimeSpan elapsed)
    {
        ThrowIfDisposed();
        return Clock.Advance(elapsed, Step);
    }

    public RuleDispatchResult Publish(SimulationFact fact)
    {
        ThrowIfDisposed();
        return _contentRules.Dispatch(World, fact, CombineFactObservers(null));
    }

    public RuleDispatchTrace Trace(SimulationFact fact)
    {
        ThrowIfDisposed();
        return _contentRules.DispatchWithTrace(World, fact, CombineFactObservers(null));
    }

    public void Step()
    {
        ThrowIfDisposed();
        using var transaction = World.BeginTransaction();
        try
        {
            StepWithinTransaction(transaction);
        }
        catch (WorldCommitCallbackException failure)
        {
            // A nested world committed, but this tick has not: the enclosing transaction can still
            // undo it, so mark the failure and report the tick as rolled back.
            RollBackTick(transaction, failure);
            SystemicTickCommitState.MarkNestedCommit(failure);
            throw;
        }
        catch (Exception failure)
        {
            RollBackTick(transaction, failure);
            throw;
        }

        try
        {
            // Only this commit belongs to this tick. A failure here left the world committed,
            // so it must not be rolled back or retried.
            transaction.Commit();
        }
        catch (WorldCommitCallbackException)
        {
            throw;
        }
        catch (Exception failure)
        {
            RollBackTick(transaction, failure);
            throw;
        }
    }

    private static void RollBackTick(WorldTransaction transaction, Exception failure)
    {
        try
        {
            transaction.Rollback();
        }
        catch (Exception rollbackFailure)
        {
            var combined = new AggregateException("The tick failed and rollback also failed.", failure, rollbackFailure);
            CopySystemId(failure, combined);
            throw combined;
        }
    }

    private static void CopySystemId(Exception source, Exception target)
    {
        if (source.Data["Systemic.SystemId"] is { } systemId)
        {
            target.Data["Systemic.SystemId"] = systemId;
        }
    }

    private void StepWithinTransaction(WorldTransaction transaction)
    {
        // A failed tick must not replay facts queued before its rollback.
        ClearDeferredFacts();
        for (var index = 0; index < StatefulExtensions.Count; index++)
        {
            var extension = StatefulExtensions[index];
            // Kernel populations, rule history, the clock and the event engine journal their own
            // changes, so only domain state that is not journalled needs a tick snapshot.
            if (extension is ISaveOnlyStateExtension) continue;
            if (extension is ISystemicReusableTransactionalState reusable)
            {
                var snapshot = reusable.CaptureTransactionSnapshot();
                transaction.OnRollback(snapshot.OnRollback);
                transaction.OnCommit(snapshot.OnCommit);
            }
            else if (extension is ISystemicTransactionalState participant)
            {
                RegisterInMemoryRollback(transaction, participant);
            }
            else
            {
                RegisterJsonRollback(transaction, extension);
            }
        }
        World.LogicalTick = checked(World.LogicalTick + 1);
        _schedule?.Execute(World, World.LogicalTick);
        DispatchDeferredFacts();

        World.AdvanceModifiers();
    }

    private void ClearDeferredFacts()
    {
        for (var index = 0; index < _deferredFactQueues.Length; index++)
        {
            _deferredFactQueues[index].Facts.Clear();
        }
    }

    private void DispatchDeferredFacts()
    {
        var pending = false;
        for (var index = 0; index < _deferredFactQueues.Length; index++)
        {
            if (_deferredFactQueues[index].Facts.Count > 0)
            {
                pending = true;
                break;
            }
        }

        if (!pending)
        {
            return;
        }

        var observer = CombineFactObservers(null);
        try
        {
            // System order, then queue order: independent of worker count and wave layout. One batch
            // shares a single transaction, so the tick pays journal setup once instead of per fact.
            _factBatch.Clear();
            for (var index = 0; index < _deferredFactQueues.Length; index++)
            {
                _factBatch.AddRange(_deferredFactQueues[index].Facts);
            }

            _contentRules.DispatchBatch(World, CollectionsMarshal.AsSpan(_factBatch), observer);
        }
        finally
        {
            _factBatch.Clear();
            ClearDeferredFacts();
        }
    }

    /// <summary>Runs a tick with an error boundary and reports whether a failing callback followed commit.</summary>
    public SystemicTickResult TryStep()
    {
        var attemptedTick = checked(World.LogicalTick + 1);
        try { Step(); return new(true, null); }
        catch (Exception exception)
        {
            return new(false, new SystemicTickFailure(exception.Data["Systemic.SystemId"] as string,
                attemptedTick, exception is WorldCommitCallbackException
                    && !SystemicTickCommitState.IsNestedCommit(exception), exception));
        }
    }

    private static void RegisterInMemoryRollback(WorldTransaction transaction, ISystemicTransactionalState participant)
    {
        var snapshot = participant.CaptureTransactionState();
        transaction.OnRollback(() => participant.RestoreTransactionState(snapshot));
    }

    private static void RegisterJsonRollback(WorldTransaction transaction, ISystemicStatefulExtension extension)
    {
        var snapshot = extension.CaptureState().Clone();
        transaction.OnRollback(() => extension.RestoreState(snapshot));
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
        {
            try
            {
                _schedule?.Dispose();
            }
            catch
            {
                Volatile.Write(ref _disposed, 0);
                throw;
            }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(
        Volatile.Read(ref _disposed) != 0, this);

    private SimulationFactObserver? CombineFactObservers(SimulationFactObserver? observer)
    {
        if (_factObservers.Length == 0)
        {
            return observer;
        }

        return (fact, emitter) =>
        {
            observer?.Invoke(fact, emitter);
            foreach (var registered in _factObservers)
            {
                registered(fact, emitter);
            }
        };
    }
}

internal sealed class SystemicSystemBridge(
    ISystemicSystem system,
    SystemicSystemContext stepContext,
    SystemicTickStats.Measurement measurement,
    DeferredFactQueue deferredFacts) : IScheduledKernelSystem
{
    private readonly ISystemicSystem _system = system;
    private readonly SystemicSystemContext _stepContext = stepContext;
    private readonly bool _canWriteWorld = system.Access.Contains(SystemAccess.WriteWorld);
    private readonly bool _canDeferWorldWrites = system.Access.Contains(SystemAccess.DeferWorldWrites);

    public string Id => _system.Id;
    public IReadOnlyList<SystemAccess> Access => _system.Access;
    public DeferredFactQueue DeferredFacts => deferredFacts;

    public void Execute(ScheduledSystemContext context)
    {
        _stepContext.Prepare(context, _canWriteWorld, _canDeferWorldWrites, deferredFacts);
        var start = Stopwatch.GetTimestamp();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            _system.Step(_stepContext);
        }
        catch (Exception exception)
        {
            exception.Data["Systemic.SystemId"] = _system.Id;
            throw;
        }
        finally
        {
            measurement.Record(Stopwatch.GetTimestamp() - start, GC.GetAllocatedBytesForCurrentThread() - allocated);
            _stepContext.Finish();
        }
    }
}

internal sealed record FactObserverRegistration(int Order, int RegistrationOrder, SimulationFactObserver Observer);

/// <summary>One system's facts waiting for the end-of-tick flush.</summary>
internal sealed class DeferredFactQueue
{
    public List<SimulationFact> Facts { get; } = [];
}
