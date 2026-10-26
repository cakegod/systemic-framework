namespace Systemic.Kernel;

public readonly record struct DiagnosticRecord(
    string Rule,
    int SourceId,
    int TargetId,
    double Cost,
    string Result,
    int Tick,
    long CauseId,
    int ChainId);

public readonly record struct CutoffRecord(
    int Tick,
    int ChainId,
    string Reason,
    string EventType = "",
    string Rule = "",
    long CauseId = 0,
    int SourceId = 0,
    int TargetId = 0,
    int Depth = 0,
    int EffectsUsed = 0,
    int EffectBudget = 0);

public sealed record ProgressFact(long Serial, long CauseId, int SourceId, int Delta, int NewValue);

public enum CompareOp
{
    Equal,
    NotEqual,
    LessThan,
    LessOrEqual,
    GreaterThan,
    GreaterOrEqual,
}

public enum TriggerScope
{
    World,
    Source,
    Target,
    AllTagged,
}

public sealed record PropertyRequirement(string Property, CompareOp Comparison, double Value);

public sealed record LinkRequirement(string Link, string Tag, string Capability = "");

public interface IKernelTrigger
{
    Type EventType { get; }
    string Id { get; }
    TriggerScope Scope { get; }
    int Priority { get; }
    ActionDefinition? Action { get; }
    bool Transactional { get; }
    string[] Tags { get; }
    string[] Capabilities { get; }
    bool HasExactlyOneBody { get; }
    bool Matches(World world, Entity entity);
    void Invoke(EventEngine engine, World world, KernelEvent domainEvent, Entity? target);
}

public sealed class Trigger<TEvent> : IKernelTrigger
    where TEvent : KernelEvent
{
    public required string Id { get; init; }
    public TriggerEffect<TEvent>? Effect { get; init; }
    public ActionDefinition? Action { get; init; }
    public int Priority { get; init; }
    public TriggerScope Scope { get; init; } = TriggerScope.World;
    // WHY: a non-transactional effect must never call Reject; it skips the branch clone.
    public bool Transactional { get; init; } = true;
    public string[] Tags { get; init; } = [];
    public string[] Capabilities { get; init; } = [];
    public PropertyRequirement[] Quantities { get; init; } = [];
    public LinkRequirement[] Links { get; init; } = [];
    public Type EventType => typeof(TEvent);
    public bool HasExactlyOneBody => (Effect is null) != (Action is null);

    public bool Matches(World world, Entity entity)
    {
        foreach (var tag in Tags)
        {
            if (!world.HasTag(entity, tag))
            {
                return false;
            }
        }

        foreach (var capability in Capabilities)
        {
            if (!world.HasCapability(entity, capability))
            {
                return false;
            }
        }

        foreach (var requirement in Quantities)
        {
            if (!MatchesQuantity(world, entity, requirement))
            {
                return false;
            }
        }

        foreach (var requirement in Links)
        {
            if (!entity.Links.TryGetValue(requirement.Link, out var id)
                || world.ById(id) is not { } linked
                || !world.HasTag(linked, requirement.Tag)
                || (requirement.Capability.Length != 0
                    && !world.HasCapability(linked, requirement.Capability)))
            {
                return false;
            }
        }

        return true;
    }

    public static Trigger<TEvent> FromEffect(
        string id,
        TriggerEffect<TEvent> effect,
        int priority = 0,
        TriggerScope scope = TriggerScope.World,
        string[]? tags = null,
        string[]? capabilities = null,
        PropertyRequirement[]? quantities = null,
        LinkRequirement[]? links = null,
        bool transactional = true) => new()
        {
            Id = id,
            Effect = effect,
            Priority = priority,
            Scope = scope,
            Transactional = transactional,
            Tags = tags ?? [],
            Capabilities = capabilities ?? [],
            Quantities = quantities ?? [],
            Links = links ?? [],
        };

    public static Trigger<TEvent> FromAction(
        string id,
        ActionDefinition action,
        int priority = 0,
        TriggerScope scope = TriggerScope.World) => new()
        {
            Id = id,
            Action = action,
            Priority = priority,
            Scope = scope,
        };

    private static bool MatchesQuantity(World world, Entity entity, PropertyRequirement requirement) =>
        world.TryGetEffectiveProperty(entity, requirement.Property, out var actual)
        && Compare(actual, requirement.Value, requirement.Comparison);

    private static bool Compare(double actual, double expected, CompareOp comparison) => comparison switch
    {
        CompareOp.Equal => actual == expected,
        CompareOp.NotEqual => actual != expected,
        CompareOp.LessThan => actual < expected,
        CompareOp.LessOrEqual => actual <= expected,
        CompareOp.GreaterThan => actual > expected,
        CompareOp.GreaterOrEqual => actual >= expected,
        _ => false,
    };

    public void Invoke(EventEngine engine, World world, KernelEvent domainEvent, Entity? target)
    {
        if (domainEvent is not TEvent typedEvent)
        {
            throw new InvalidOperationException(
                $"Typed rule '{Id}' expected {typeof(TEvent).Name}, but received {domainEvent.GetType().Name}.");
        }

        engine.Invoke(this, world, typedEvent, target);
    }
}

public delegate void TriggerEffect<TEvent>(TriggerContext<TEvent> context)
    where TEvent : KernelEvent;

public class TriggerContext
{
    private int? _targetId;

    public World World { get; private set; } = null!;
    public KernelEvent Event { get; private set; } = null!;
    public Entity? Target => _targetId is { } id ? World.ById(id) : null;
    public IKernelTrigger Trigger { get; private set; } = null!;
    public EventEngine Engine { get; }
    internal bool Rejected { get; private set; }

    internal TriggerContext(EventEngine engine) => Engine = engine;

    internal void Reset(World world, KernelEvent domainEvent, Entity? target, IKernelTrigger trigger)
    {
        World = world;
        Event = domainEvent;
        _targetId = target?.Id;
        Trigger = trigger;
        Rejected = false;
    }

    public void Record(int sourceId, int targetId, double cost, string result) =>
        Engine.Record(World, Trigger.Id, sourceId, targetId, cost, result);

    public void Emit(KernelEvent child) => Engine.Emit(World, child);

    public void EmitNow(KernelEvent child) => Engine.EmitNow(World, child);

    public ActionContext BeginAction(bool optional = false) =>
        Engine.BeginAction(World, Event, Trigger.Id, optional);

    public ActionContext BeginAction(bool optional, string? actionId) =>
        Engine.BeginAction(World, Event, actionId ?? Trigger.Id, optional);

    public ActionContext BeginBatch(KernelEvent command, string actionId) =>
        Engine.BeginBatch(World, command, actionId);

    public void Reject(string? reason = null)
    {
        reason ??= $"{Trigger.Id} rejected";
        Rejected = true;
        if (Event.Causality.Depth == 0 && Engine.CommittedActions.Count == 0)
        {
            Engine.Reject(reason);
            return;
        }

        Engine.RecordRejectedReaction(World, Trigger.Id, Event, reason);
    }
}

public sealed class TriggerContext<TEvent> : TriggerContext
    where TEvent : KernelEvent
{
    public new TEvent Event => (TEvent)base.Event;
    public new Trigger<TEvent> Trigger => (Trigger<TEvent>)base.Trigger;

    internal TriggerContext(EventEngine engine) : base(engine) { }

    internal static TriggerContext<TEvent> Create(EventEngine engine) => new(engine);

    internal void Reset(World world, TEvent domainEvent, Entity? target, Trigger<TEvent> trigger) =>
        base.Reset(world, domainEvent, target, trigger);

    public new ActionContext<TEvent> BeginAction(bool optional = false) =>
        Engine.BeginAction(World, Event, Trigger.Id, optional);

    public new ActionContext<TEvent> BeginAction(bool optional, string? actionId) =>
        Engine.BeginAction(World, Event, actionId ?? Trigger.Id, optional);

    public ActionContext<TEvent> BeginBatch(TEvent command, string actionId) =>
        Engine.BeginBatch(World, command, actionId);
}

public static class EngineLimits
{
    public const int MaxDepth = 8;

    public const int MaxBatchWorkItems = 65_536;

    // WHY: a logical tick can contain thousands of valid clicks, so the cascade bound is per top-level command.
    // WHY: one effect is one trigger application; record lines are diagnostic and do not charge.
    public const int MaxEffectsPerCommand = 1024;
    public const int MaxDiagnosticRecordsPerCommand = 1024;

    // WHY: snapshots expose a recent window, not the full run; one full budgeted command plus sparse cutoffs is enough for presentation.
    public const int MaxRetainedDiagnostics = MaxDiagnosticRecordsPerCommand;
    public const int MaxRetainedCutoffs = 64;
}
