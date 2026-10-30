using Systemic.Kernel;

namespace Systemic.Rules;

/// <summary>How a selector compares an entity property with its authored value.</summary>
internal enum QueryComparison
{
    AtLeast,
    AtMost,
    Equals,
}

/// <summary>How a selector orders its matches before the query limit is applied.</summary>
internal enum QueryOrder
{
    EntityId,
    Nearest,
}

/// <summary>Which value a query derives from the selected entities.</summary>
internal enum QueryAggregateKind
{
    Count,
    Sum,
    Minimum,
    Maximum,
    Average,
    NearestDistance,
}

/// <summary>
/// A bounded, deterministic entity selector. Every field is optional, so an empty selector
/// selects every live entity.
/// </summary>
/// <param name="Tag">The tag every match must carry.</param>
/// <param name="Property">The property every match must carry, compared with <paramref name="PropertyComparison"/>.</param>
/// <param name="PropertyComparison">How <paramref name="PropertyValue"/> is compared.</param>
/// <param name="PropertyValue">The property threshold.</param>
/// <param name="Radius">The maximum distance from <paramref name="Origin"/>, or null for no spatial filter.</param>
/// <param name="Origin">The entity whose bounds center the spatial filter and nearest ordering.</param>
/// <param name="Order">The order applied before <paramref name="Limit"/>.</param>
/// <param name="Limit">The maximum number of matches, or zero for no limit.</param>
internal sealed record EntitySelector(
    string? Tag,
    string? Property,
    QueryComparison PropertyComparison,
    double PropertyValue,
    double? Radius,
    RuleSubject Origin,
    QueryOrder Order,
    int Limit)
{
    /// <summary>Gets a selector that matches every live entity in ascending ID order.</summary>
    public static EntitySelector All { get; } = new(null, null, QueryComparison.AtLeast, 0, null,
        RuleSubject.Source, QueryOrder.EntityId, 0);
}

/// <summary>A selector plus the aggregate and test a rule applies to its result.</summary>
/// <param name="Selector">The entities to select.</param>
/// <param name="Aggregate">The value derived from the selected entities.</param>
/// <param name="AggregateProperty">The property aggregated by sum, minimum, maximum, or average.</param>
/// <param name="Comparison">The comparison applied to the aggregate when <paramref name="Formula"/> is absent.</param>
/// <param name="Threshold">The value compared with the aggregate.</param>
/// <param name="Formula">An optional boolean expression over <c>query.value</c>, <c>query.count</c>, <c>query.nearest</c>, and <c>query.matched</c>.</param>
internal sealed record CompiledQuery(
    EntitySelector Selector,
    QueryAggregateKind Aggregate,
    string? AggregateProperty,
    QueryComparison Comparison,
    double Threshold,
    CompiledExpression? Formula = null);

/// <summary>The deterministic result of one selector evaluation.</summary>
internal readonly record struct QueryResult(int Count, bool HasValue, double Value, double NearestDistance)
{
    public static QueryResult Empty { get; } = new(0, false, 0, 0);
}

internal static class EntityQueryEvaluator
{
    [ThreadStatic]
    private static List<int>? t_candidates;

    public static QueryResult Evaluate(World world, CompiledQuery query, SimulationFact fact)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(query);
        var selector = query.Selector;
        var candidates = t_candidates ??= [];
        candidates.Clear();
        try
        {
            if (!Collect(world, selector, fact, candidates))
            {
                return QueryResult.Empty;
            }

            Filter(world, selector, candidates);

            if (selector.Order == QueryOrder.Nearest)
            {
                if (!TryGetOriginCenter(world, fact, selector.Origin, out var center))
                {
                    return QueryResult.Empty;
                }

                candidates.Sort((left, right) =>
                {
                    var leftDistance = DistanceSquared(world, left, center);
                    var rightDistance = DistanceSquared(world, right, center);
                    var comparison = leftDistance.CompareTo(rightDistance);
                    return comparison != 0 ? comparison : left.CompareTo(right);
                });
            }

            if (selector.Limit > 0 && candidates.Count > selector.Limit)
            {
                candidates.RemoveRange(selector.Limit, candidates.Count - selector.Limit);
            }

            return Aggregate(world, query, fact, candidates);
        }
        finally
        {
            candidates.Clear();
        }
    }

    private static bool Collect(World world, EntitySelector selector, SimulationFact fact, List<int> results)
    {
        if (selector.Radius is { } radius)
        {
            if (!TryGetOriginCenter(world, fact, selector.Origin, out var center))
            {
                return false;
            }

            if (radius > float.MaxValue)
            {
                return false;
            }

            world.QuerySpatialNearby(center, (float)radius, results);
            return true;
        }

        if (selector.Tag is { } tag)
        {
            var tagged = world.EntitiesWithTag(tag).Span;
            for (var index = 0; index < tagged.Length; index++)
            {
                var entity = world.ById(tagged[index]);
                if (entity is { ToRemove: false })
                {
                    results.Add(entity.Id);
                }
            }

            return true;
        }

        foreach (var entity in world.Entities)
        {
            if (!entity.ToRemove)
            {
                results.Add(entity.Id);
            }
        }

        return true;
    }

    private static void Filter(World world, EntitySelector selector, List<int> candidates)
    {
        for (var index = candidates.Count - 1; index >= 0; index--)
        {
            var entity = world.ById(candidates[index]);
            if (entity is null || entity.ToRemove)
            {
                candidates.RemoveAt(index);
                continue;
            }

            if (selector.Tag is { } tag && !world.HasTag(entity, tag))
            {
                candidates.RemoveAt(index);
                continue;
            }

            if (selector.Property is { } property)
            {
                if (!world.TryGetEffectiveProperty(entity, property, out var value)
                    || !Compare(value, selector.PropertyValue, selector.PropertyComparison))
                {
                    candidates.RemoveAt(index);
                }
            }
        }
    }

    private static QueryResult Aggregate(World world, CompiledQuery query, SimulationFact fact, List<int> candidates)
    {
        var count = candidates.Count;
        if (query.Aggregate == QueryAggregateKind.Count)
        {
            return new QueryResult(count, true, count, NearestDistance(world, query.Selector, fact, candidates));
        }

        if (count == 0)
        {
            return QueryResult.Empty;
        }

        if (query.Aggregate == QueryAggregateKind.NearestDistance)
        {
            var nearest = NearestDistance(world, query.Selector, fact, candidates);
            return double.IsFinite(nearest)
                ? new QueryResult(count, true, nearest, nearest)
                : QueryResult.Empty;
        }

        if (query.AggregateProperty is not { } property)
        {
            return QueryResult.Empty;
        }

        var matched = 0;
        var sum = 0.0;
        var minimum = double.PositiveInfinity;
        var maximum = double.NegativeInfinity;
        foreach (var id in candidates)
        {
            var entity = world.ById(id);
            if (entity is null || !world.TryGetEffectiveProperty(entity, property, out var value)
                || !double.IsFinite(value))
            {
                continue;
            }

            matched++;
            sum += value;
            minimum = Math.Min(minimum, value);
            maximum = Math.Max(maximum, value);
        }

        if (matched == 0)
        {
            return QueryResult.Empty;
        }

        var aggregate = query.Aggregate switch
        {
            QueryAggregateKind.Sum => sum,
            QueryAggregateKind.Minimum => minimum,
            QueryAggregateKind.Maximum => maximum,
            QueryAggregateKind.Average => sum / matched,
            _ => throw new InvalidOperationException($"Unknown query aggregate '{query.Aggregate}'."),
        };
        return new QueryResult(count, true, aggregate, NearestDistance(world, query.Selector, fact, candidates));
    }

    private static double NearestDistance(World world, EntitySelector selector, SimulationFact fact, List<int> candidates)
    {
        if (candidates.Count == 0 || !TryGetOriginCenter(world, fact, selector.Origin, out var center))
        {
            return 0;
        }

        var nearest = double.PositiveInfinity;
        foreach (var id in candidates)
        {
            nearest = Math.Min(nearest, DistanceSquared(world, id, center));
        }

        return double.IsFinite(nearest) ? Math.Sqrt(nearest) : 0;
    }

    private static bool TryGetOriginCenter(World world, SimulationFact fact, RuleSubject origin, out Point2 center)
    {
        var entity = origin.Kind switch
        {
            RuleSubjectKind.Source => world.ById(fact.SourceId),
            RuleSubjectKind.Target => world.ById(fact.TargetId),
            RuleSubjectKind.Entity => world.ById(origin.Slot),
            _ => null,
        };
        if (entity is not null && world.TryGetSpatialBounds(entity.Id, out var bounds))
        {
            center = new Point2(bounds.X, bounds.Y);
            return true;
        }

        center = default;
        return false;
    }

    private static double DistanceSquared(World world, int entityId, Point2 center)
    {
        if (!world.TryGetSpatialBounds(entityId, out var bounds))
        {
            return double.PositiveInfinity;
        }

        var dx = Math.Max(Math.Max((double)bounds.Left - center.X, 0), (double)center.X - bounds.Right);
        var dy = Math.Max(Math.Max((double)bounds.Top - center.Y, 0), (double)center.Y - bounds.Bottom);
        return (dx * dx) + (dy * dy);
    }

    internal static bool Compare(double actual, double expected, QueryComparison comparison) => comparison switch
    {
        QueryComparison.AtLeast => actual >= expected,
        QueryComparison.AtMost => actual <= expected,
        QueryComparison.Equals => actual.Equals(expected),
        _ => throw new InvalidOperationException($"Unknown query comparison '{comparison}'."),
    };
}

internal readonly struct QueryExpressionScope(
    World world,
    SimulationFact fact,
    RuleEngine rules,
    QueryResult result) : IExpressionScope
{
    public double ReadNumber(string name) => name switch
    {
        "query.count" => result.Count,
        "query.value" => result.Value,
        "query.nearest" => result.NearestDistance,
        "query.matched" => result.HasValue ? 1 : 0,
        _ => new WorldExpressionScope(world, fact, rules).ReadNumber(name),
    };

    public double Count(string selector) => new WorldExpressionScope(world, fact, rules).Count(selector);

    public double Rate(string selector, int windowTicks) =>
        new WorldExpressionScope(world, fact, rules).Rate(selector, windowTicks);

    public double EventCount(string eventType, int windowTicks) =>
        new WorldExpressionScope(world, fact, rules).EventCount(eventType, windowTicks);
}
