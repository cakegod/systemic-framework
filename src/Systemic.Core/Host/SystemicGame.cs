using System.Collections.Immutable;
using System.Text.Json;
using Systemic.Kernel;
using Systemic.Content;
using Systemic.Extensions;
using Systemic.Rules;
using Systemic.Save;
using Systemic.Ui;

namespace Systemic.Host;

/// <summary>Typed C# phase for orchestration that is outside the bounded JSON rule vocabulary.</summary>
public interface ISystemicSystem
{
    string Id { get; }
    int Order { get; }
    void Step(SystemicStepContext context);
}

/// <summary>Opt-in kernel-scheduled system; World mutations go through the per-system command buffer.</summary>
public interface ISystemicScheduledSystem
{
    string Id { get; }
    IReadOnlyList<SystemAccess> Access { get; }
    void Step(ScheduledSystemContext kernelContext, SystemicScheduledContext gameContext);
}

public interface ISystemicStatefulExtension
{
    string Id { get; }
    JsonElement CaptureState();
    void RestoreState(JsonElement state);
}

public sealed class SystemicStepContext(
    World world,
    RuleEngine contentRules,
    EventEngine extensionEngine,
    int tick,
    SimulationFactObserver? observeFact = null)
{
    public World World { get; } = world;
    public int Tick { get; private set; } = tick;

    public RuleDispatchResult Publish(SimulationFact fact) => contentRules.Dispatch(World, fact, observeFact);

    public CommandExecutionResult Execute(KernelEvent command) => extensionEngine.ExecuteDetailed(World, command);

    public void SetProperty(Entity entity, string property, double value)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (!entity.TrySetProperty(World, property, value))
        {
            throw new InvalidOperationException($"Entity {entity.Id} has no writable property '{property}'.");
        }
    }

    internal void Prepare(int tick)
    {
        Tick = tick;
    }
}

/// <summary>Game-layer services for a scheduled system; World reads are exposed through a read-only view.</summary>
public sealed class SystemicScheduledContext(
    World world,
    RuleEngine contentRules,
    EventEngine extensionEngine,
    int tick,
    SimulationFactObserver? observeFact = null)
{
    private ScheduledSystemContext? _kernelContext;
    private WorldCommandBuffer? _commands;
    private bool _canWriteWorld;

    public int Tick { get; private set; } = tick;
    public ReadOnlyWorldView World { get; } = new(world);
    public WorldCommandBuffer Commands => _commands
        ?? throw new InvalidOperationException("Scheduled commands are available only while the system executes.");

    public void OnRollback(Action restoreExternalState)
    {
        ArgumentNullException.ThrowIfNull(restoreExternalState);
        (_kernelContext ?? throw new InvalidOperationException(
            "Rollback actions can be registered only while the system executes.")).OnRollback(restoreExternalState);
    }

    public RuleDispatchResult Publish(SimulationFact fact)
    {
        EnsureImmediateWorldWriteAccess();
        Commands.Flush(world);
        return contentRules.Dispatch(world, fact, observeFact);
    }

    public CommandExecutionResult Execute(KernelEvent command)
    {
        EnsureImmediateWorldWriteAccess();
        Commands.Flush(world);
        return extensionEngine.ExecuteDetailed(world, command);
    }

    internal void Prepare(ScheduledSystemContext context, bool canWriteWorld)
    {
        _kernelContext = context;
        Tick = context.Tick;
        _commands = context.Commands;
        _canWriteWorld = canWriteWorld;
    }

    internal void Finish()
    {
        _kernelContext = null;
        _commands = null;
        _canWriteWorld = false;
    }

    private void EnsureImmediateWorldWriteAccess()
    {
        if (!_canWriteWorld)
        {
            throw new InvalidOperationException(
                "Immediate rule or event dispatch requires SystemAccess.WriteWorld; deferred World commands remain available without it.");
        }
    }
}

/// <summary>Builds a Core host and configures only modules compiled from the manifest.</summary>
public sealed class SystemicGameBuilder
{
    private readonly List<ISystemicSystem> _systems = [];
    private readonly List<ISystemicScheduledSystem> _scheduledSystems = [];
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

    public SystemicGameBuilder AddScheduledSystem(ISystemicScheduledSystem system)
    {
        EnsureNotBuilt();
        ArgumentNullException.ThrowIfNull(system);
        ArgumentException.ThrowIfNullOrWhiteSpace(system.Id);
        if (_scheduledSystems.Any(existing => string.Equals(existing.Id, system.Id, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Scheduled system '{system.Id}' is already registered.");
        }

        _scheduledSystems.Add(system);
        return this;
    }

    public SystemicGameBuilder WithScheduledWorkerCount(int workerCount)
    {
        EnsureNotBuilt();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workerCount);
        _scheduledWorkerCount = workerCount;
        return this;
    }

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

        if (module is ISystemicScheduledSystem scheduledSystem)
        {
            AddScheduledSystem(scheduledSystem);
        }

        if (module is ISystemicStatefulExtension stateful)
        {
            RegisterStatefulExtension(stateful);
        }

        return this;
    }

    public SystemicGameBuilder Use<TModule>()
        where TModule : ISystemicExtensionModule, new() => AddExtension(new TModule());

    public SystemicGame Build()
    {
        EnsureNotBuilt();
        _built = true;
        var systemsToRun = new List<ISystemicSystem>(_systems);
        if (!Content.Rules.Schedules.IsDefaultOrEmpty)
        {
            systemsToRun.Add(new ScheduledEventSystem(Content.Rules.Schedules, World, EntityIds));
        }

        var systems = systemsToRun.OrderBy(system => system.Order)
            .ThenBy(system => system.Id, StringComparer.Ordinal)
            .ToArray();
        var duplicateSystem = systems.GroupBy(system => system.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateSystem is not null)
        {
            throw new InvalidOperationException($"System ID '{duplicateSystem.Key}' is registered more than once.");
        }

        var scheduledSystemIds = new HashSet<string>(systems.Select(system => system.Id), StringComparer.Ordinal);
        foreach (var system in _scheduledSystems)
        {
            if (!scheduledSystemIds.Add(system.Id))
            {
                throw new InvalidOperationException($"System ID '{system.Id}' is registered more than once.");
            }
        }

        var contentRules = new RuleEngine(Content.Rules, tickRateHz: Content.Manifest.TickRateHz);
        var extensionEngine = _extensions.BuildEngine();
        var hasSavedPopulationState = _extensionStates?.ContainsKey(PopulationStatefulExtension.ExtensionId) == true;
        var populationStateExtension = World.HasPopulations || hasSavedPopulationState
            ? new PopulationStatefulExtension(World)
            : null;
        var statefulExtensions = systems.OfType<ISystemicStatefulExtension>()
            .Concat(_scheduledSystems.OfType<ISystemicStatefulExtension>())
            .Concat(_statefulExtensions)
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
            foreach (var (extensionId, state) in _extensionStates)
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
        var scheduledBridges = _scheduledSystems
            .Select(system => new SystemicScheduledSystemBridge(system,
                new SystemicScheduledContext(World, contentRules, extensionEngine, 0, systemObserver)))
            .ToArray();
        DeterministicSystemSchedule? schedule = null;
        if (scheduledBridges.Length > 0)
        {
            var scheduleBuilder = new DeterministicSystemScheduleBuilder(_scheduledWorkerCount);
            foreach (var bridge in scheduledBridges)
            {
                scheduleBuilder.Add(bridge);
            }

            schedule = scheduleBuilder.Build();
        }

        return new SystemicGame(Content, World, systems, scheduledBridges, schedule, contentRules, extensionEngine,
            EntityIds, statefulExtensions, new Dictionary<Type, object>(_services), factObservers,
            [.. _uiBindingProviders]);
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
    private readonly ISystemicSystem[] _systems;
    private readonly SystemicScheduledSystemBridge[] _scheduledBridges;
    private readonly DeterministicSystemSchedule? _schedule;
    private readonly RuleEngine _contentRules;
    private readonly EventEngine _extensionEngine;
    private readonly IReadOnlyDictionary<Type, object> _services;
    private readonly SimulationFactObserver[] _factObservers;
    private readonly SystemicStepContext _stepContext;
    private int _disposed;

    internal SystemicGame(
        ContentPack content,
        World world,
        ISystemicSystem[] systems,
        SystemicScheduledSystemBridge[] scheduledBridges,
        DeterministicSystemSchedule? schedule,
        RuleEngine contentRules,
        EventEngine extensionEngine,
        ImmutableDictionary<string, int> entityIds,
        ISystemicStatefulExtension[] statefulExtensions,
        IReadOnlyDictionary<Type, object> services,
        SimulationFactObserver[] factObservers,
        ISystemicUiBindingProvider[] uiBindingProviders)
    {
        Content = content;
        World = world;
        _systems = systems;
        _scheduledBridges = scheduledBridges;
        _schedule = schedule;
        _contentRules = contentRules;
        _extensionEngine = extensionEngine;
        EntityIds = entityIds;
        StatefulExtensions = statefulExtensions;
        _services = services;
        _factObservers = factObservers;
        UiBindingProviders = uiBindingProviders;
        _stepContext = new SystemicStepContext(world, contentRules, extensionEngine, 0, CombineFactObservers(null));
        Clock = new FixedStepClock(content.Manifest.TickRateHz, content.Manifest.MaxCatchUpSteps);
    }

    public static SystemicGameBuilder FromDirectory(string rootDirectory, int seed, double valueScale = 1)
    {
        var content = ContentPackLoader.Load(rootDirectory);
        var materialized = content.CreateWorld(seed, valueScale);
        return new SystemicGameBuilder(content, materialized);
    }

    public ContentPack Content { get; }
    public World World { get; }
    public FixedStepClock Clock { get; }
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

    public CommandExecutionResult Execute(KernelEvent command)
    {
        ThrowIfDisposed();
        return _extensionEngine.ExecuteDetailed(World, command);
    }

    public void Step()
    {
        ThrowIfDisposed();
        using var transaction = World.BeginTransaction();
        World.LogicalTick = checked(World.LogicalTick + 1);
        _stepContext.Prepare(World.LogicalTick);
        foreach (var system in _systems)
        {
            system.Step(_stepContext);
        }

        _schedule?.Execute(World, World.LogicalTick);

        World.AdvanceModifiers();
        transaction.Commit();
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

internal sealed class SystemicScheduledSystemBridge(
    ISystemicScheduledSystem system,
    SystemicScheduledContext stepContext) : IScheduledKernelSystem
{
    private readonly ISystemicScheduledSystem _system = system;
    private readonly SystemicScheduledContext _stepContext = stepContext;
    private readonly bool _canWriteWorld = system.Access.Contains(SystemAccess.WriteWorld);

    public string Id => _system.Id;
    public IReadOnlyList<SystemAccess> Access => _system.Access;

    public void Execute(ScheduledSystemContext context)
    {
        _stepContext.Prepare(context, _canWriteWorld);
        try
        {
            _system.Step(context, _stepContext);
        }
        finally
        {
            _stepContext.Finish();
        }
    }
}

internal sealed record FactObserverRegistration(int Order, int RegistrationOrder, SimulationFactObserver Observer);
