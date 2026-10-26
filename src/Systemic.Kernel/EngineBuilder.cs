namespace Systemic.Kernel;

public sealed class EngineBuilder(int maxEffectsPerCommand = EngineLimits.MaxEffectsPerCommand)
{
    private readonly int _maxEffectsPerCommand = maxEffectsPerCommand;
    private readonly List<IKernelTrigger> _triggers = [];

    public EngineBuilder On<TEvent>(
        string id,
        TriggerEffect<TEvent> effect,
        int priority = 0,
        TriggerScope scope = TriggerScope.World,
        string[]? tags = null,
        string[]? capabilities = null,
        PropertyRequirement[]? quantities = null,
        LinkRequirement[]? links = null,
        bool transactional = true)
        where TEvent : KernelEvent
    {
        _triggers.Add(Trigger<TEvent>.FromEffect(id, effect, priority, scope,
            tags, capabilities, quantities, links, transactional));

        return this;
    }

    public EngineBuilder On<TEvent>(
        EventType<TEvent> eventType,
        string id,
        TriggerEffect<TEvent> effect,
        int priority = 0,
        TriggerScope scope = TriggerScope.World,
        string[]? tags = null,
        string[]? capabilities = null,
        PropertyRequirement[]? quantities = null,
        LinkRequirement[]? links = null,
        bool transactional = true)
        where TEvent : KernelEvent =>
        On<TEvent>(id, effect, priority, scope, tags, capabilities, quantities, links, transactional);

    public EngineBuilder OnBatch<TEvent, TEntity>(
        string id,
        EntityQuery<TEntity> query,
        BatchTriggerEffect<TEvent, TEntity> effect,
        int priority = 0,
        bool transactional = true)
        where TEvent : KernelEvent
        where TEntity : Entity
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(effect);
        _triggers.Add(new BatchTrigger<TEvent, TEntity>(id, query, effect, priority, transactional));

        return this;
    }

    public EngineBuilder OnAction<TEvent>(
        string id,
        ActionDefinition action,
        int priority = 0,
        TriggerScope scope = TriggerScope.World)
        where TEvent : KernelEvent
    {
        _triggers.Add(Trigger<TEvent>.FromAction(id, action, priority, scope));

        return this;
    }

    public EngineBuilder OnAction<TEvent>(
        string id,
        IActionOutcomeResolver outcome,
        params IActionEffect[] effects)
        where TEvent : KernelEvent
    {
        _triggers.Add(Trigger<TEvent>.FromAction(id,
            new ActionDefinition(id, outcome, [.. effects])));

        return this;
    }

    public EventEngine Build() => new(_triggers, _maxEffectsPerCommand);

    public IReadOnlyList<IKernelTrigger> Triggers => _triggers;
}

public static class Trigger
{
    public static Trigger<TEvent> FromEffect<TEvent>(
        EventType<TEvent> eventType,
        string id,
        TriggerEffect<TEvent> effect,
        int priority = 0,
        TriggerScope scope = TriggerScope.World,
        string[]? tags = null,
        string[]? capabilities = null,
        PropertyRequirement[]? quantities = null,
        LinkRequirement[]? links = null,
        bool transactional = true)
        where TEvent : KernelEvent =>
        FromEffect<TEvent>(id, effect, priority, scope, tags, capabilities, quantities, links, transactional);

    public static Trigger<TEvent> FromEffect<TEvent>(
        string id,
        TriggerEffect<TEvent> effect,
        int priority = 0,
        TriggerScope scope = TriggerScope.World,
        string[]? tags = null,
        string[]? capabilities = null,
        PropertyRequirement[]? quantities = null,
        LinkRequirement[]? links = null,
        bool transactional = true)
        where TEvent : KernelEvent =>
        Trigger<TEvent>.FromEffect(id, effect, priority, scope,
            tags, capabilities, quantities, links, transactional);

    public static Trigger<TEvent> FromAction<TEvent>(
        string id,
        ActionDefinition action,
        int priority = 0,
        TriggerScope scope = TriggerScope.World)
        where TEvent : KernelEvent => Trigger<TEvent>.FromAction(id, action, priority, scope);

    public static Trigger<TEvent> FromAction<TEvent>(
        EventType<TEvent> eventType,
        string id,
        ActionDefinition action,
        int priority = 0,
        TriggerScope scope = TriggerScope.World)
        where TEvent : KernelEvent => Trigger<TEvent>.FromAction(id, action, priority, scope);
}
