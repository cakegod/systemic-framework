using Systemic.Kernel;

namespace Systemic.Rules;

internal readonly struct WorldExpressionScope(
    World world,
    SimulationFact fact,
    RuleEngine rules) : IExpressionScope
{
    public double ReadNumber(string name)
    {
        if (name is "tick" or "world.tick")
        {
            return world.LogicalTick;
        }

        if (name == "event.amount")
        {
            return fact.Amount;
        }

        if (name == "event.depth")
        {
            return fact.Depth;
        }

        if (TryReadSubjectProperty(name, "source.", fact.SourceId, out var value)
            || TryReadSubjectProperty(name, "target.", fact.TargetId, out value))
        {
            return value;
        }

        throw new ExpressionEvaluationException($"Unknown numeric value '{name}'.");
    }

    public double Count(string selector)
    {
        var tag = selector.StartsWith("tag:", StringComparison.Ordinal)
            ? selector[4..]
            : selector;
        if (string.IsNullOrWhiteSpace(tag))
        {
            throw new ExpressionEvaluationException("Count selector must name a tag.");
        }

        return world.LiveCount(tag);
    }

    public double Rate(string selector, int windowTicks) =>
        rules.EventRate(selector, windowTicks, world.LogicalTick);

    public double EventCount(string eventType, int windowTicks) =>
        rules.EventCount(eventType, windowTicks, world.LogicalTick);

    private bool TryReadSubjectProperty(string name, string prefix, int entityId, out double value)
    {
        value = 0;
        if (!name.StartsWith(prefix, StringComparison.Ordinal)
            || name.Length == prefix.Length
            || world.ById(entityId) is not { } entity)
        {
            return false;
        }

        var property = name[prefix.Length..];
        return world.TryGetEffectiveProperty(entity, property, out value);
    }
}
