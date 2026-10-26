namespace Systemic.Kernel;

public delegate void BatchTriggerEffect<TEvent, TEntity>(BatchContext<TEvent, TEntity> context)
    where TEvent : KernelEvent
    where TEntity : Entity;

internal interface IBatchKernelTrigger
{
    IEntityQuery Query { get; }

    void InvokeBatch(EventEngine engine, World world, KernelEvent domainEvent, EntityQuerySnapshot targetIds);
}

internal sealed class BatchTrigger<TEvent, TEntity>(
    string id,
    EntityQuery<TEntity> query,
    BatchTriggerEffect<TEvent, TEntity> effect,
    int priority,
    bool transactional) : IKernelTrigger, IBatchKernelTrigger
    where TEvent : KernelEvent
    where TEntity : Entity
{
    public Type EventType => typeof(TEvent);

    public string Id { get; } = id;

    public TriggerScope Scope => TriggerScope.AllTagged;

    public int Priority { get; } = priority;

    public ActionDefinition? Action => null;

    public bool Transactional { get; } = transactional;

    public string[] Tags => [.. ((IEntityQuery)query).Tags];

    public string[] Capabilities => [.. ((IEntityQuery)query).Capabilities];

    public bool HasExactlyOneBody => true;

    IEntityQuery IBatchKernelTrigger.Query => query;

    public bool Matches(World world, Entity entity) => ((IEntityQuery)query).Matches(world, entity);

    public void Invoke(EventEngine engine, World world, KernelEvent domainEvent, Entity? target) =>
        throw new InvalidOperationException($"Batch trigger '{Id}' must be invoked through batch dispatch.");

    void IBatchKernelTrigger.InvokeBatch(
        EventEngine engine, World world, KernelEvent domainEvent, EntityQuerySnapshot targetIds)
    {
        if (domainEvent is not TEvent typedEvent)
        {
            throw new InvalidOperationException(
                $"Typed batch rule '{Id}' expected {typeof(TEvent).Name}, but received {domainEvent.GetType().Name}.");
        }

        engine.InvokeBatch(this, world, typedEvent, targetIds);
    }

    internal EntityQuery<TEntity> TypedQuery => query;

    internal BatchTriggerEffect<TEvent, TEntity> Effect => effect;
}

/// <summary>Context for one indexed batch invocation, with target-level revalidation.</summary>
public sealed class BatchContext<TEvent, TEntity> : TriggerContext
    where TEvent : KernelEvent
    where TEntity : Entity
{
    private EntityQuery<TEntity>? _query;
    private EntityQuerySnapshot _targetIds;
    private bool _hasTraversedTargets;

    internal BatchContext(EventEngine engine) : base(engine) { }

    public new TEvent Event => (TEvent)base.Event;

    public int CandidateCount => _targetIds.Length;

    internal void Reset(
        World world,
        TEvent domainEvent,
        BatchTrigger<TEvent, TEntity> trigger,
        EntityQuery<TEntity> query,
        EntityQuerySnapshot targetIds)
    {
        base.Reset(world, domainEvent, target: null, trigger);
        _query = query;
        _targetIds = targetIds;
        _hasTraversedTargets = false;
    }

    internal bool RecordTargetTraversal()
    {
        var repeated = _hasTraversedTargets;
        _hasTraversedTargets = true;

        return repeated;
    }

    /// <summary>
    /// Visits the initial query snapshot in ascending ID order, rechecking each target immediately before use.
    /// Each traversal after the first also consumes the command's batch work budget.
    /// </summary>
    public void ForEachTarget(Action<TEntity> effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        Engine.ForEachBatchTarget(this, _query!, _targetIds, effect);
    }
}
