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
    private readonly RuleEngine? _contentRules;
    private readonly IReadOnlyDictionary<Type, object> _services;
    private readonly SimulationFactObserver? _observeFact;
    private readonly WorldCommandBuffer _standaloneCommands;
    private ScheduledSystemContext? _kernelContext;
    private readonly int _standaloneTick;
    private readonly bool _standalone;
    private bool _canWriteWorld;
    private bool _canDeferWorldWrites;
    private DeferredFactQueue? _deferredFacts;
    private SystemicEventQueue? _events;

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
        RuleEngine? contentRules,
        IReadOnlyDictionary<Type, object> services,
        SimulationFactObserver? observeFact)
    {
        _world = world;
        _contentRules = contentRules;
        _services = services;
        _observeFact = observeFact;
        _standaloneCommands = default;
    }

    internal SystemicSystemContext(
        World world,
        RuleEngine? contentRules,
        IReadOnlyDictionary<Type, object> services,
        int tick)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _contentRules = contentRules;
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _standaloneCommands = new WorldCommandBuffer();
        _standaloneTick = tick;
        _standalone = true;
        _canWriteWorld = true;
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
        SimulationFactValidation.Validate(fact);
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
            if (_contentRules is null)
            {
                _observeFact?.Invoke(fact, new SimulationFactEmitter(commitImmediately: true));
                return;
            }

            _contentRules.Dispatch(_world, fact, _observeFact);
            return;
        }

        throw new InvalidOperationException("System context operations are available only while the system executes.");
    }

    /// <summary>Dispatches a simulation fact against the current World.</summary>
    /// <exception cref="InvalidOperationException">The system did not declare immediate World write access.</exception>
    public RuleDispatchResult Publish(SimulationFact fact)
    {
        SimulationFactValidation.Validate(fact);
        if (_contentRules is null)
        {
            // WHY: a rules-free host has no writes to flush and no reactions, but observers still
            // need the fact, so the notification path must not demand SystemAccess.WriteWorld.
            _observeFact?.Invoke(fact, new SimulationFactEmitter(commitImmediately: true));
            return new RuleDispatchResult(FactsProcessed: 1, RulesMatched: 0, EffectsApplied: 0, CutOff: false);
        }

        EnsureImmediateWorldWriteAccess();
        FlushWorldCommands();
        return _contentRules.Dispatch(_world, fact, _observeFact);
    }

    /// <summary>Queues a typed event for dispatch after every system has run this tick.</summary>
    /// <param name="event">The event payload.</param>
    /// <remarks>
    /// Events dispatch in system registration order, then queue order, after the tick's deferred
    /// facts. A rolled-back tick discards its queue, so no handler observes a failed tick.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The system is not running under the host schedule.</exception>
    public void EmitEvent<TEvent>(TEvent @event)
        where TEvent : ISystemicEvent
    {
        ArgumentNullException.ThrowIfNull(@event);
        if (_kernelContext is null)
        {
            throw new InvalidOperationException(
                "Typed events can be emitted only while the system executes under the host schedule.");
        }

        (_events ?? throw new InvalidOperationException("The event queue is unavailable during this execution."))
            .Events.Add(@event);
    }

    /// <summary>Registers recovery for external state if the enclosing scheduled tick rolls back.</summary>
    /// <param name="restoreExternalState">Action that restores state changed by this system.</param>
    /// <exception cref="InvalidOperationException">The context is not executing in the host schedule.</exception>
    public void OnRollback(Action restoreExternalState)
    {
        ArgumentNullException.ThrowIfNull(restoreExternalState);
        if (_kernelContext is not { } kernelContext)
        {
            throw new InvalidOperationException("Rollback actions can be registered only during a host-scheduled execution.");
        }

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
        {
            executeRange(state, 0, new PopulationRange(0, rowCount));
        }
        else if (!_standalone)
        {
            throw new InvalidOperationException("System context operations are available only while the system executes.");
        }
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
        {
            throw new InvalidOperationException("System context operations are available only while the system executes.");
        }

        if (!ReferenceEquals(lease.WorldIdentity, _world))
        {
            throw new ArgumentException("The population lease belongs to a different world.", nameof(lease));
        }

        var range = new PopulationRange(0, lease.Count);
        if (range.Length == 0)
        {
            return;
        }

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
        {
            throw new InvalidOperationException("System context operations are available only while the system executes.");
        }

        if (!ReferenceEquals(population.World, _world))
        {
            throw new ArgumentException("The population belongs to a different world.", nameof(population));
        }

        var range = new PopulationRange(0, population.Count);
        if (range.Length == 0)
        {
            return;
        }

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
        {
            return kernelContext.ReduceRanges(state, rowCount, partitionCount, identity, map, merge);
        }

        if (_standalone)
        {
            return ReduceStandalone(state, rowCount, partitionCount, identity, map, merge);
        }

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
        {
            throw new ArgumentException("The population belongs to a different world.", nameof(population));
        }

        if (_kernelContext is { } kernelContext)
        {
            return kernelContext.BorrowPopulation(population);
        }

        if (_standalone)
        {
            return population.Borrow();
        }

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
        {
            return kernelContext.DeferPopulationCommands(population, partitionCount);
        }

        if (!_standalone)
        {
            throw new InvalidOperationException("System context operations are available only while the system executes.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(partitionCount);
        var buffers = new PopulationCommandBuffer<TKind>[partitionCount];
        for (var index = 0; index < buffers.Length; index++)
        {
            buffers[index] = new PopulationCommandBuffer<TKind>(population);
        }

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
        DeferredFactQueue deferredFacts,
        SystemicEventQueue events)
    {
        _kernelContext = kernelContext;
        _canWriteWorld = canWriteWorld;
        _canDeferWorldWrites = canDeferWorldWrites;
        _deferredFacts = deferredFacts;
        _events = events;
    }

    internal void Finish()
    {
        _kernelContext = null;
        _canWriteWorld = false;
        _canDeferWorldWrites = false;
        _deferredFacts = null;
        _events = null;
        _standaloneCommands.Expire();
    }

    private void FlushWorldCommands()
    {
        if (_kernelContext is { } kernelContext)
        {
            var commands = kernelContext.Commands;
            if (commands.Count > 0)
            {
                kernelContext.FlushCommands(_world);
            }
        }
        else if (_standalone)
        {
            if (_standaloneCommands.Count > 0)
            {
                _standaloneCommands.Flush(_world);
            }
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
        if (rowCount == 0)
        {
            return identity;
        }

        var count = Math.Min(rowCount, partitionCount);
        var result = identity;
        for (var partition = 0; partition < count; partition++)
        {
            result = merge(result, map(state, partition, PopulationRange.Partition(rowCount, count, partition)));
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
    private readonly Dictionary<string, RuleActionHandler> _ruleActions = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, object> _services = [];
    private readonly Dictionary<Type, ICommandHandlerRegistration> _commandHandlers = [];
    private readonly List<EventRegistration> _eventHandlers = [];
    private readonly Dictionary<string, UiCommandBinding> _uiCommandBindings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _moduleVersions = new(StringComparer.Ordinal);
    private readonly ExtensionModuleCatalog _extensions = new();
    private readonly ImmutableDictionary<string, JsonElement>? _extensionStates;
    private ImmutableDictionary<string, int> _savedModuleVersions = ImmutableDictionary<string, int>.Empty;
    private bool _built;
    private bool _failed;
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
        _savedModuleVersions = materialized.ModuleVersions;
    }

    public ContentPack Content { get; }
    public World World { get; }
    public ImmutableDictionary<string, int> EntityIds { get; }
    public bool IsRestoringWorld => _extensionStates is not null;

    /// <summary>Registers a game module and runs its composition root.</summary>
    /// <param name="module">The game module to compose.</param>
    /// <remarks>A module is also an extension module, so it may own systems and saved state through the same registration path.</remarks>
    public SystemicGameBuilder AddModule(ISystemicGameModule module)
    {
        EnsureNotBuilt();
        ArgumentNullException.ThrowIfNull(module);
        ArgumentException.ThrowIfNullOrWhiteSpace(module.Id);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(module.Version);
        if (_moduleVersions.ContainsKey(module.Id))
        {
            throw new InvalidOperationException($"Game module '{module.Id}' is already registered.");
        }

        // WHY: AddExtension performs the save-version check for every module kind, so a game module
        // cannot take a different path from an extension module.

        // WHY: record the ID before composing, so a module that composes a duplicate fails before it
        // can leave half-registered systems behind, and poison the builder if composition throws.
        _moduleVersions.Add(module.Id, module.Version);
        try
        {
            AddExtension(module);
            module.Compose(this);
        }
        catch
        {
            _failed = true;
            throw;
        }

        return this;
    }

    /// <summary>Registers a game module by type.</summary>
    public SystemicGameBuilder AddModule<TModule>()
        where TModule : ISystemicGameModule, new() => AddModule(new TModule());

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

    /// <summary>Registers the handler for one typed command.</summary>
    /// <param name="handler">The handler invoked by <see cref="SystemicGame.Execute{TCommand}(TCommand)"/>.</param>
    /// <exception cref="InvalidOperationException">The command type already has a handler.</exception>
    public SystemicGameBuilder AddCommand<TCommand>(ISystemicCommandHandler<TCommand> handler)
        where TCommand : ISystemicCommand
    {
        EnsureNotBuilt();
        ArgumentNullException.ThrowIfNull(handler);
        if (!_commandHandlers.TryAdd(typeof(TCommand), new CommandHandlerRegistration<TCommand>(handler)))
        {
            throw new InvalidOperationException($"Command '{typeof(TCommand).FullName}' already has a handler.");
        }

        return this;
    }

    /// <summary>Registers a typed command handler by type.</summary>
    public SystemicGameBuilder AddCommand<TCommand, THandler>()
        where TCommand : ISystemicCommand
        where THandler : ISystemicCommandHandler<TCommand>, new() => AddCommand(new THandler());

    /// <summary>Registers a typed event handler. Handlers run in registration order.</summary>
    public SystemicGameBuilder AddEvent<TEvent>(ISystemicEventHandler<TEvent> handler)
        where TEvent : ISystemicEvent
    {
        EnsureNotBuilt();
        ArgumentNullException.ThrowIfNull(handler);
        var registration = _eventHandlers.Find(entry => entry.EventType == typeof(TEvent));
        if (registration is null)
        {
            registration = new EventRegistration(typeof(TEvent));
            _eventHandlers.Add(registration);
        }

        registration.Handlers.Add(new EventInvoker<TEvent>(handler));
        return this;
    }

    /// <summary>Registers a typed event handler by type.</summary>
    public SystemicGameBuilder AddEvent<TEvent, THandler>()
        where TEvent : ISystemicEvent
        where THandler : ISystemicEventHandler<TEvent>, new() => AddEvent(new THandler());

    /// <summary>Binds a declared UI action to a typed command before the game builds.</summary>
    /// <param name="actionId">The action ID declared by a UI layout.</param>
    /// <param name="createCommand">Creates the command from the layout, owner, and clicked amount.</param>
    /// <exception cref="InvalidOperationException">The action is already bound.</exception>
    public SystemicGameBuilder BindUiAction<TCommand>(
        string actionId,
        Func<SystemicUiAction, TCommand> createCommand)
        where TCommand : ISystemicCommand
    {
        EnsureNotBuilt();
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        ArgumentNullException.ThrowIfNull(createCommand);
        if (!_uiCommandBindings.TryAdd(actionId,
            new UiCommandBinding(typeof(TCommand), action => createCommand(action))))
        {
            throw new InvalidOperationException($"UI action '{actionId}' is already bound.");
        }

        return this;
    }

    /// <summary>Registers a game action that content rules may invoke by name.</summary>
    /// <param name="name">The name a rule's action effect uses.</param>
    /// <param name="handler">The handler that runs inside the dispatch transaction.</param>
    /// <remarks>
    /// A pack arrived from disk has no handlers, because handlers are code. Registering one here
    /// merges it into the loaded rule set before the engine binds, so a rule that invokes an action
    /// works through the normal directory load path.
    /// </remarks>
    public SystemicGameBuilder AddRuleAction(string name, RuleActionHandler handler)
    {
        EnsureNotBuilt();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(handler);
        if (!_ruleActions.TryAdd(name, handler))
        {
            throw new InvalidOperationException($"Rule action '{name}' is already registered.");
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
        ArgumentException.ThrowIfNullOrWhiteSpace(module.Id);
        if (module is ISystemicSystem system
            && _systems.Any(existing => string.Equals(existing.Id, system.Id, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"System '{system.Id}' is already registered.");
        }

        // Both directions matter: a changed version is unsafe, and a module the save never recorded
        // is new behavior that the saved state cannot describe.
        if (_savedModuleVersions.Count > 0)
        {
            if (!_savedModuleVersions.TryGetValue(module.Id, out var savedVersion))
            {
                throw new InvalidOperationException(
                    $"Save was created without module '{module.Id}'. Migrate the save or create a new one.");
            }

            if (savedVersion != module.Version)
            {
                throw new InvalidOperationException(
                    $"Save was created with module '{module.Id}' version {savedVersion}, but this build composes version {module.Version}. Migrate the save or create a new one.");
            }
        }

        _moduleVersions.TryAdd(module.Id, module.Version);
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
        // A restore must compose every module the save was produced with; a missing one is a
        // composition change, not an empty extension list.
        foreach (var (savedId, _) in _savedModuleVersions)
        {
            if (!_moduleVersions.ContainsKey(savedId))
            {
                throw new InvalidOperationException(
                    $"Save requires module '{savedId}', but this build does not compose it. Add the module or create a new save.");
            }
        }

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

        var rules = _ruleActions.Count == 0 ? Content.Rules : Content.Rules.WithActions(_ruleActions);
        // WHY: a pack with no rules, schedules, or rule actions runs no legacy engine at all, so a
        // data-only pack has a data-only runtime and the eventual engine deletion is a subtraction.
        RuleEngine? contentRules = rules.Rules.IsEmpty && rules.Schedules.IsEmpty && _ruleActions.Count == 0
            ? null
            : new RuleEngine(rules, tickRateHz: Content.Manifest.TickRateHz);
        var uiActionCatalogs = Content.Modules.IsDefaultOrEmpty
            ? Array.Empty<ISystemicUiActionCatalog>()
            : Content.Modules
                .Select(module => module.Content)
                .OfType<ISystemicUiActionCatalog>()
                .ToArray();
        foreach (var (actionId, binding) in _uiCommandBindings.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!_commandHandlers.ContainsKey(binding.CommandType))
            {
                throw new InvalidOperationException(
                    $"UI action '{actionId}' is bound to command '{binding.CommandType.FullName}' but no handler is registered.");
            }

            // WHY: without a catalog there is nothing to validate the action ID against, so a
            // typo'd binding would silently produce a dead button. Fail at startup instead.
            if (uiActionCatalogs.Length == 0)
            {
                throw new InvalidOperationException(
                    $"UI action '{actionId}' is bound to command '{binding.CommandType.FullName}', but no loaded content module declares UI actions. Load the module that owns the layout, usually the Presentation module.");
            }

            if (!uiActionCatalogs.Any(catalog => catalog.DeclaresAction(actionId)))
            {
                throw new InvalidOperationException(
                    $"UI action '{actionId}' is not declared by any loaded UI layout.");
            }
        }

        // Only a build that passes validation consumes the builder, so a caller can fix the
        // composition and retry instead of recreating the builder.
        _built = true;
        var commandHandlers = new Dictionary<Type, ICommandHandlerRegistration>(_commandHandlers);
        var eventHandlers = _eventHandlers
            .Select(entry => new EventRegistration(entry.EventType, entry.Handlers.ToArray()))
            .ToArray();
        var uiCommandBindings = _uiCommandBindings.ToDictionary(
            pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var moduleVersions = _moduleVersions.ToImmutableDictionary(StringComparer.Ordinal);
        var clock = new FixedStepClock(Content.Manifest.TickRateHz, Content.Manifest.MaxCatchUpSteps);
        var hasSavedPopulationState = _extensionStates?.ContainsKey(PopulationStatefulExtension.ExtensionId) == true;
        var populationStateExtension = World.HasPopulations || hasSavedPopulationState
            ? new PopulationStatefulExtension(World)
            : null;
        var commandJournal = new CommandJournalStatefulExtension();
        var statefulExtensions = systems.OfType<ISystemicStatefulExtension>()
            .Concat(_statefulExtensions)
            .Append(new ClockStatefulExtension(clock))
            .Concat(contentRules is null
                ? Array.Empty<ISystemicStatefulExtension>()
                : [new RuleHistoryStatefulExtension(contentRules, () => World.LogicalTick)])
            .Append(commandJournal)
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
                new DeferredFactQueue(),
                new SystemicEventQueue()))
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
            EntityIds, statefulExtensions, new Dictionary<Type, object>(_services), commandHandlers,
            eventHandlers, uiCommandBindings, moduleVersions, factObservers, clock, tickStats,
            [.. _uiBindingProviders], [.. systemBridges.Select(bridge => bridge.DeferredFacts)],
            [.. systemBridges.Select(bridge => bridge.Events)], commandJournal);
    }

    private void EnsureNotBuilt()
    {
        if (_failed)
        {
            throw new InvalidOperationException(
                "This builder is unusable because module composition failed after registering systems or services. Create a new builder.");
        }

        if (_built)
        {
            throw new InvalidOperationException("A systemic game builder can only build once.");
        }
    }
}

public sealed class SystemicGame : IDisposable
{
    private readonly DeterministicSystemSchedule? _schedule;
    private readonly RuleEngine? _contentRules;
    private readonly IReadOnlyDictionary<Type, object> _services;
    private readonly IReadOnlyDictionary<Type, ICommandHandlerRegistration> _commandHandlers;
    private readonly IReadOnlyList<EventRegistration> _eventHandlers;
    private readonly IReadOnlyDictionary<string, UiCommandBinding> _uiCommandBindings;
    private readonly SimulationFactObserver[] _factObservers;
    private int _disposed;

    internal SystemicGame(
        ContentPack content,
        World world,
        DeterministicSystemSchedule? schedule,
        RuleEngine? contentRules,
        ImmutableDictionary<string, int> entityIds,
        ISystemicStatefulExtension[] statefulExtensions,
        IReadOnlyDictionary<Type, object> services,
        IReadOnlyDictionary<Type, ICommandHandlerRegistration> commandHandlers,
        IReadOnlyList<EventRegistration> eventHandlers,
        IReadOnlyDictionary<string, UiCommandBinding> uiCommandBindings,
        ImmutableDictionary<string, int> moduleVersions,
        SimulationFactObserver[] factObservers,
        FixedStepClock clock,
        SystemicTickStats tickStats,
        ISystemicUiBindingProvider[] uiBindingProviders,
        DeferredFactQueue[] deferredFactQueues,
        SystemicEventQueue[] eventQueues,
        CommandJournalStatefulExtension commandJournal)
    {
        Content = content;
        World = world;
        _schedule = schedule;
        _contentRules = contentRules;
        EntityIds = entityIds;
        StatefulExtensions = statefulExtensions;
        _services = services;
        _commandHandlers = commandHandlers;
        _eventHandlers = eventHandlers;
        _uiCommandBindings = uiCommandBindings;
        ModuleVersions = moduleVersions;
        _factObservers = factObservers;
        UiBindingProviders = uiBindingProviders;
        Clock = clock;
        TickStats = tickStats;
        _deferredFactQueues = deferredFactQueues;
        _eventQueues = eventQueues;
        _commandJournal = commandJournal;
    }

    private readonly DeferredFactQueue[] _deferredFactQueues;
    private readonly SystemicEventQueue[] _eventQueues;
    private readonly CommandJournalStatefulExtension _commandJournal;
    private readonly List<SimulationFact> _factBatch = new(64);

    public static SystemicGameBuilder FromDirectory(string rootDirectory, int seed, double valueScale = 1,
        ContentModuleRegistry? moduleRegistry = null)
    {
        var content = ContentPackLoader.Load(rootDirectory, moduleRegistry);
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
    public ImmutableDictionary<string, int> ModuleVersions { get; }

    /// <summary>Gets whether this host composes a rule engine for legacy rule content.</summary>
    /// <remarks>A pack with no rules, schedules, or registered rule actions reports false, and its runtime carries no rule state.</remarks>
    public bool SupportsRules => _contentRules is not null;

    /// <summary>Gets the ordered journal of accepted and rejected typed commands.</summary>
    /// <remarks>
    /// Each record carries the command type, its serialized payload, the last completed tick, and
    /// whether the command committed. The journal is saved with the host and keeps the most recent
    /// 4096 records, so a long session does not grow every later save without bound.
    /// </remarks>
    public IReadOnlyList<SystemicCommandRecord> CommandJournal => _commandJournal.Records;

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
        SimulationFactValidation.Validate(fact);
        if (_contentRules is null)
        {
            // WHY: a rules-free host has no reactions, but presentation and telemetry observers
            // still need the fact, so facts remain a notification channel without the engine.
            CombineFactObservers(null)?.Invoke(fact, new SimulationFactEmitter(commitImmediately: true));
            return new RuleDispatchResult(FactsProcessed: 1, RulesMatched: 0, EffectsApplied: 0, CutOff: false);
        }

        return _contentRules.Dispatch(World, fact, CombineFactObservers(null));
    }

    public RuleDispatchTrace Trace(SimulationFact fact)
    {
        ThrowIfDisposed();
        SimulationFactValidation.Validate(fact);
        if (_contentRules is null)
        {
            // A rules-free host has nothing to trace, and tracing must not notify observers.
            return new RuleDispatchTrace(
                new RuleDispatchResult(FactsProcessed: 0, RulesMatched: 0, EffectsApplied: 0, CutOff: false),
                ImmutableArray<RuleTraceFact>.Empty);
        }

        return _contentRules.DispatchWithTrace(World, fact, CombineFactObservers(null));
    }

    /// <summary>Runs one typed command in its own deterministic World transaction.</summary>
    /// <typeparam name="TCommand">The registered command type.</typeparam>
    /// <param name="command">The command payload.</param>
    /// <exception cref="InvalidOperationException">No handler is registered for the command.</exception>
    public SystemicCommandOutcome Execute<TCommand>(TCommand command)
        where TCommand : ISystemicCommand
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(command);
        return ExecuteCore(typeof(TCommand), command);
    }

    /// <summary>Runs one typed command and reports whether the handler accepted or rejected it.</summary>
    /// <typeparam name="TCommand">The registered command type.</typeparam>
    /// <param name="command">The command payload.</param>
    /// <param name="outcome">The handled, accepted, and rejection details.</param>
    /// <returns>True when a handler accepted and committed the command.</returns>
    public bool TryExecute<TCommand>(TCommand command, out SystemicCommandOutcome outcome)
        where TCommand : ISystemicCommand
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(command);
        outcome = ExecuteCore(typeof(TCommand), command);
        return outcome.Accepted;
    }

    /// <summary>Runs a typed command by its runtime type.</summary>
    /// <param name="command">The registered command payload.</param>
    /// <exception cref="InvalidOperationException">No handler is registered for the command.</exception>
    public SystemicCommandOutcome Execute(ISystemicCommand command)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(command);
        return ExecuteCore(command.GetType(), command);
    }

    /// <summary>Runs the typed command bound to a UI action, or returns false when the action is rule-backed.</summary>
    /// <param name="action">The layout, action, owner, and clicked amount.</param>
    /// <exception cref="ArgumentException">The action has no action ID.</exception>
    public bool TryExecuteUiAction(SystemicUiAction action) =>
        TryExecuteUiAction(action, out _);

    /// <summary>Runs the typed command bound to a UI action and reports its outcome.</summary>
    /// <param name="action">The layout, action, owner, and clicked amount.</param>
    /// <param name="outcome">The handled, accepted, and rejection details.</param>
    /// <returns>True when a bound command was accepted and committed.</returns>
    public bool TryExecuteUiAction(SystemicUiAction action, out SystemicCommandOutcome outcome)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(action.ActionId);
        if (!_uiCommandBindings.TryGetValue(action.ActionId, out var binding))
        {
            outcome = SystemicCommandOutcome.Unhandled;
            return false;
        }

        var command = binding.CreateCommand(action)
            ?? throw new InvalidOperationException($"UI action '{action.ActionId}' created a null command.");
        outcome = ExecuteCore(binding.CommandType, command);
        return outcome.Accepted;
    }

    /// <summary>Dispatches one typed event to every registered handler in registration order.</summary>
    /// <typeparam name="TEvent">The registered event type.</typeparam>
    /// <param name="event">The event payload.</param>
    public void Emit<TEvent>(TEvent @event)
        where TEvent : ISystemicEvent
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(@event);
        if (!HasEventHandlers(@event.GetType()))
        {
            return;
        }

        var context = new SystemicEventContext(
            new SystemicSystemContext(World, _contentRules, _services, World.LogicalTick));
        try
        {
            using var transaction = World.BeginTransaction();
            RegisterStatefulRollback(transaction);
            try
            {
                DispatchEvent(@event, context);
                transaction.Commit();
            }
            catch (Exception failure)
            {
                RollBackAfterFailure(transaction, failure, "The event failed and rollback also failed.");
                throw;
            }
        }
        finally
        {
            context.Finish();
        }
    }

    private bool HasEventHandlers(Type eventType)
    {
        for (var index = 0; index < _eventHandlers.Count; index++)
        {
            if (_eventHandlers[index].EventType.IsAssignableFrom(eventType))
            {
                return true;
            }
        }

        return false;
    }

    private void DispatchEvent(ISystemicEvent @event, SystemicEventContext context)
    {
        // A handler registered for a base event type or the event interface receives every
        // assignable event; handler-type registration order decides the dispatch order.
        var eventType = @event.GetType();
        for (var index = 0; index < _eventHandlers.Count; index++)
        {
            var registration = _eventHandlers[index];
            if (!registration.EventType.IsAssignableFrom(eventType))
            {
                continue;
            }

            for (var handlerIndex = 0; handlerIndex < registration.Handlers.Count; handlerIndex++)
            {
                registration.Handlers[handlerIndex].Invoke(@event, context);
            }
        }
    }

    private SystemicCommandOutcome ExecuteCore(Type commandType, ISystemicCommand command)
    {
        var handler = FindCommandHandler(commandType)
            ?? throw new InvalidOperationException($"No handler is registered for command '{commandType.FullName}'.");

        var context = new SystemicCommandContext(
            new SystemicSystemContext(World, _contentRules, _services, World.LogicalTick));
        try
        {
            using var transaction = World.BeginTransaction();
            RegisterStatefulRollback(transaction);
            try
            {
                handler.Invoke(command, context);
                if (context.IsRejected)
                {
                    // Rejection is normal play: discard the command's mutations and report why.
                    transaction.Rollback();
                    RecordCommand(commandType, command, accepted: false);
                    return SystemicCommandOutcome.Rejected(context.RejectionReason!);
                }

                context.ApplyDeferredCommands();
                DispatchCommandEvents(context);
                transaction.Commit();
            }
            catch (Exception failure)
            {
                RollBackAfterFailure(transaction, failure, "The command failed and rollback also failed.");
                throw;
            }

            RecordCommand(commandType, command, accepted: true);
            return SystemicCommandOutcome.Succeeded;
        }
        finally
        {
            context.Finish();
        }
    }

    private ICommandHandlerRegistration? FindCommandHandler(Type commandType)
    {
        if (_commandHandlers.TryGetValue(commandType, out var exact))
        {
            return exact;
        }

        // A handler registered for a base class or interface handles derived commands, matching
        // event dispatch. The nearest registration wins, and an ordinal name tie-break keeps the
        // result independent of dictionary enumeration order.
        ICommandHandlerRegistration? match = null;
        var bestDistance = int.MaxValue;
        var bestName = string.Empty;
        foreach (var (registeredType, registration) in _commandHandlers)
        {
            if (!registeredType.IsAssignableFrom(commandType))
            {
                continue;
            }

            var distance = TypeDistance(commandType, registeredType);
            var name = registeredType.FullName ?? registeredType.Name;
            if (distance < bestDistance
                || (distance == bestDistance && string.CompareOrdinal(name, bestName) < 0))
            {
                match = registration;
                bestDistance = distance;
                bestName = name;
            }
        }

        return match;
    }

    private static int TypeDistance(Type commandType, Type registeredType)
    {
        if (registeredType.IsInterface)
        {
            var pending = new Queue<(Type Type, int Distance)>();
            foreach (var implemented in commandType.GetInterfaces())
            {
                pending.Enqueue((implemented, 1));
            }

            var visited = new HashSet<Type>();
            while (pending.Count > 0)
            {
                var (type, distance) = pending.Dequeue();
                if (type == registeredType)
                {
                    return distance;
                }

                if (!visited.Add(type))
                {
                    continue;
                }

                foreach (var parent in type.GetInterfaces())
                {
                    pending.Enqueue((parent, distance + 1));
                }
            }

            return int.MaxValue;
        }

        var depth = 1;
        for (var type = commandType.BaseType; type is not null; type = type.BaseType, depth++)
        {
            if (type == registeredType)
            {
                return depth;
            }
        }

        return int.MaxValue;
    }

    private void DispatchCommandEvents(SystemicCommandContext command)
    {
        if (command.PendingEvents.Count == 0)
        {
            return;
        }

        var context = new SystemicEventContext(
            new SystemicSystemContext(World, _contentRules, _services, World.LogicalTick));
        try
        {
            for (var index = 0; index < command.PendingEvents.Count; index++)
            {
                DispatchEvent(command.PendingEvents[index], context);
            }
        }
        finally
        {
            context.Finish();
        }
    }

    private void RecordCommand(Type commandType, ISystemicCommand command, bool accepted) =>
        _commandJournal.Append(World.LogicalTick, commandType.FullName ?? commandType.Name,
            JsonSerializer.SerializeToElement(command, commandType), accepted);

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

    private static void RollBackTick(WorldTransaction transaction, Exception failure) =>
        RollBackAfterFailure(transaction, failure, "The tick failed and rollback also failed.");

    private static void RollBackAfterFailure(WorldTransaction transaction, Exception failure, string message)
    {
        try
        {
            transaction.Rollback();
        }
        catch (Exception rollbackFailure)
        {
            // A failing rollback must not hide the failure that caused it.
            var combined = new AggregateException(message, failure, rollbackFailure);
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
        // A failed tick must not replay facts or events queued before its rollback.
        ClearDeferredFacts();
        ClearDeferredEvents();
        RegisterStatefulRollback(transaction);
        World.LogicalTick = checked(World.LogicalTick + 1);
        _schedule?.Execute(World, World.LogicalTick);
        DispatchDeferredFacts();
        DispatchDeferredEvents();
        // Delayed facts belong to the tick that reached their due tick, with the same observers and
        // the same enclosing transaction as any other fact this tick dispatches.
        if (_contentRules is not null && _contentRules.HasScheduledFacts)
        {
            _contentRules.DispatchDue(World, CombineFactObservers(null));
        }

        World.AdvanceModifiers();
    }

    private void ClearDeferredFacts()
    {
        for (var index = 0; index < _deferredFactQueues.Length; index++)
        {
            _deferredFactQueues[index].Facts.Clear();
        }
    }

    private void ClearDeferredEvents()
    {
        for (var index = 0; index < _eventQueues.Length; index++)
        {
            _eventQueues[index].Events.Clear();
        }
    }

    private void DispatchDeferredEvents()
    {
        var pending = false;
        for (var index = 0; index < _eventQueues.Length; index++)
        {
            if (_eventQueues[index].Events.Count > 0)
            {
                pending = true;
                break;
            }
        }

        if (!pending)
        {
            return;
        }

        var context = new SystemicEventContext(
            new SystemicSystemContext(World, _contentRules, _services, World.LogicalTick));
        try
        {
            try
            {
                // System order, then queue order: independent of worker count and wave layout.
                for (var index = 0; index < _eventQueues.Length; index++)
                {
                    var queued = _eventQueues[index].Events;
                    for (var eventIndex = 0; eventIndex < queued.Count; eventIndex++)
                    {
                        DispatchEvent(queued[eventIndex], context);
                    }
                }
            }
            finally
            {
                ClearDeferredEvents();
            }
        }
        finally
        {
            context.Finish();
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

            if (_contentRules is null)
            {
                for (var index = 0; index < _factBatch.Count; index++)
                {
                    observer?.Invoke(_factBatch[index], new SimulationFactEmitter(commitImmediately: true));
                }
            }
            else
            {
                _contentRules.DispatchBatch(World, CollectionsMarshal.AsSpan(_factBatch), observer);
            }
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

    private void RegisterStatefulRollback(WorldTransaction transaction)
    {
        for (var index = 0; index < StatefulExtensions.Count; index++)
        {
            var extension = StatefulExtensions[index];
            // Kernel populations, rule history, the clock and the event engine journal their own
            // changes, so only domain state that is not journalled needs a snapshot.
            if (extension is ISaveOnlyStateExtension)
            {
                continue;
            }

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
    DeferredFactQueue deferredFacts,
    SystemicEventQueue events) : IScheduledKernelSystem
{
    private readonly ISystemicSystem _system = system;
    private readonly SystemicSystemContext _stepContext = stepContext;
    private readonly bool _canWriteWorld = system.Access.Contains(SystemAccess.WriteWorld);
    private readonly bool _canDeferWorldWrites = system.Access.Contains(SystemAccess.DeferWorldWrites);

    public string Id => _system.Id;
    public IReadOnlyList<SystemAccess> Access => _system.Access;
    public DeferredFactQueue DeferredFacts => deferredFacts;
    public SystemicEventQueue Events => events;

    public void Execute(ScheduledSystemContext context)
    {
        _stepContext.Prepare(context, _canWriteWorld, _canDeferWorldWrites, deferredFacts, events);
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

/// <summary>One system's typed events waiting for the end-of-tick dispatch.</summary>
internal sealed class SystemicEventQueue
{
    public List<ISystemicEvent> Events { get; } = [];
}

/// <summary>Ordered command input carried by a save: type, payload, tick, and acceptance.</summary>
/// <remarks>
/// Commands execute only between ticks, so records stamped with the same tick belong to one
/// inter-tick window and their sequence orders them within it. The journal keeps the most recent
/// <see cref="Capacity"/> records so save size stays bounded.
/// </remarks>
internal sealed class CommandJournalStatefulExtension : ISaveOnlyStateExtension
{
    public const string ExtensionId = "systemic.commands";
    internal const int Capacity = 4096;
    private readonly List<SystemicCommandRecord> _records = [];
    private int _nextSequence;

    public string Id => ExtensionId;
    public IReadOnlyList<SystemicCommandRecord> Records => _records;

    public void Append(int tick, string commandType, JsonElement payload, bool accepted)
    {
        if (_records.Count == Capacity)
        {
            _records.RemoveAt(0);
        }

        _records.Add(new SystemicCommandRecord(tick, _nextSequence++, commandType, accepted, payload));
    }

    public JsonElement CaptureState() => JsonSerializer.SerializeToElement(
        _records.ToArray(), SystemicSaveJsonContext.Default.SystemicCommandRecordArray);

    public void RestoreState(JsonElement state)
    {
        _records.Clear();
        _records.AddRange(state.Deserialize(SystemicSaveJsonContext.Default.SystemicCommandRecordArray)
            ?? []);
        _nextSequence = _records.Count == 0 ? 0 : _records[^1].Sequence + 1;
    }
}
