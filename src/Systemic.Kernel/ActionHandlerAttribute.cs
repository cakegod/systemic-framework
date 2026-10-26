namespace Systemic.ActionAuthoring;

[AttributeUsage(AttributeTargets.Method)]
public sealed class ActionHandlerAttribute(Type eventType, string id, int priority = 0, bool optional = false,
    string? actionId = null) : Attribute
{
    public Type EventType { get; } = eventType;

    public string Id { get; } = id;

    public int Priority { get; } = priority;

    public bool Optional { get; } = optional;

    public string? ActionId { get; } = actionId;
}
