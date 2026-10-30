using System.Numerics;
using Systemic.Kernel;

namespace Systemic.Gameplay2D;

public sealed record ActorDefinition(
    string Id,
    IReadOnlyCollection<string> Tags,
    IReadOnlyCollection<string> Capabilities,
    IReadOnlyDictionary<string, double> Properties,
    float HalfWidth,
    float HalfHeight);

public enum StatusStackingMode
{
    Refresh,
    Stack,
}

public sealed record StatusEffectDefinition(
    string Id,
    string Tag,
    string Property,
    ModifierOperation Operation,
    double Value,
    int DurationTicks,
    ModifierStage Stage = ModifierStage.Standard,
    StatusStackingMode Stacking = StatusStackingMode.Refresh,
    int MaximumStacks = 1);

/// <summary>Optional, engine-neutral 2D operations for authored entities and deterministic simulation systems.</summary>
public static class Gameplay2D
{
    public const string ActorTag = "gameplay2d.actor";
    public const string ProjectileTag = "gameplay2d.projectile";
    public const string PositionX = "position.x";
    public const string PositionY = "position.y";
    public const string VelocityX = "velocity.x";
    public const string VelocityY = "velocity.y";
    public const string RemainingTicks = "lifetime.remainingTicks";
    public const string HalfWidth = "bounds.halfWidth";
    public const string HalfHeight = "bounds.halfHeight";

    public static Entity SpawnActor(World world, ActorDefinition definition, Point2 position)
    {
        ArgumentNullException.ThrowIfNull(world);
        ValidateActorDefinition(definition, position);
        return Spawn(world, definition, position, extraProperties: [], extraTags: []);
    }

    public static Entity SpawnProjectile(
        World world,
        ActorDefinition definition,
        Point2 position,
        Vector2 velocity,
        int lifetimeTicks)
    {
        ArgumentNullException.ThrowIfNull(world);
        ValidateActorDefinition(definition, position);
        if (!float.IsFinite(velocity.X) || !float.IsFinite(velocity.Y)
            || lifetimeTicks is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetimeTicks), "Projectile velocity and lifetime must be finite and bounded.");
        }

        ValidateProjectileTrajectory(world, definition, position, velocity, lifetimeTicks);

        var extraProperties = new (string Name, double Value)[]
        {
            (VelocityX, velocity.X),
            (VelocityY, velocity.Y),
            (RemainingTicks, lifetimeTicks),
        };
        return Spawn(world, definition, position, extraProperties, [ProjectileTag]);
    }

    private static void ValidateProjectileTrajectory(
        World world,
        ActorDefinition definition,
        Point2 position,
        Vector2 velocity,
        int lifetimeTicks)
    {
        var finalX = position.X + ((double)velocity.X * (lifetimeTicks - 1));
        var finalY = position.Y + ((double)velocity.Y * (lifetimeTicks - 1));
        if (!double.IsFinite(finalX) || !double.IsFinite(finalY)
            || Math.Abs(finalX) > float.MaxValue || Math.Abs(finalY) > float.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(velocity), "Projectile trajectory exceeds the supported spatial range.");
        }

        world.ValidateSpatialBounds(new Bounds2((float)finalX, (float)finalY,
            definition.HalfWidth, definition.HalfHeight));
    }

    public static Entity? SelectNearestTarget(
        World world,
        Point2 position,
        float radius,
        string requiredTag,
        List<int> candidates) =>
        SelectTarget(world, position, radius, requiredTag, candidates, farthest: false);

    public static Entity? SelectFarthestTarget(
        World world,
        Point2 position,
        float radius,
        string requiredTag,
        List<int> candidates) =>
        SelectTarget(world, position, radius, requiredTag, candidates, farthest: true);

    private static Entity? SelectTarget(
        World world,
        Point2 position,
        float radius,
        string requiredTag,
        List<int> candidates,
        bool farthest)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredTag);
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y)
            || !float.IsFinite(radius) || radius < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), "Spatial target query must have finite coordinates and a non-negative radius.");
        }

        candidates.Clear();
        world.QuerySpatialNearby(position, radius, candidates);
        Entity? selected = null;
        var selectedDistance = farthest ? double.NegativeInfinity : (double)radius * radius;
        foreach (var entityId in candidates)
        {
            if (world.ById(entityId) is not { ToRemove: false } entity || !world.HasTag(entity, requiredTag)
                || !world.TryGetSpatialBounds(entityId, out var bounds))
            {
                continue;
            }

            var dx = Math.Max(Math.Max((double)bounds.Left - position.X, 0), (double)position.X - bounds.Right);
            var dy = Math.Max(Math.Max((double)bounds.Top - position.Y, 0), (double)position.Y - bounds.Bottom);
            var distance = (dx * dx) + (dy * dy);
            var betterDistance = farthest ? distance > selectedDistance : distance < selectedDistance;
            if (betterDistance || distance == selectedDistance && (selected is null || entity.Id < selected.Id))
            {
                selected = entity;
                selectedDistance = distance;
            }
        }

        return selected;
    }

    public static bool TryTransferResource(
        World world,
        int sourceId,
        int targetId,
        string property,
        double amount,
        string? capacityProperty = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentException.ThrowIfNullOrWhiteSpace(property);
        if (sourceId == targetId || !double.IsFinite(amount) || amount <= 0
            || capacityProperty is not null && string.IsNullOrWhiteSpace(capacityProperty))
        {
            return false;
        }

        using var transaction = world.BeginTransaction();
        var source = world.ById(sourceId);
        var target = world.ById(targetId);
        if (source is not { ToRemove: false } || target is not { ToRemove: false }
            || !source.TryGetProperty(world, property, out var available)
            || !target.TryGetProperty(world, property, out var current)
            || !double.IsFinite(available) || !double.IsFinite(current) || available < amount)
        {
            transaction.Rollback();
            return false;
        }

        var nextSource = available - amount;
        var nextTarget = current + amount;
        if (!double.IsFinite(nextSource) || !double.IsFinite(nextTarget))
        {
            transaction.Rollback();
            return false;
        }

        if (capacityProperty is { } capacityName
            && (!target.TryGetProperty(world, capacityName, out var capacity)
                || !double.IsFinite(capacity) || nextTarget > capacity))
        {
            transaction.Rollback();
            return false;
        }

        if (!source.TrySetProperty(world, property, nextSource)
            || !target.TrySetProperty(world, property, nextTarget))
        {
            transaction.Rollback();
            return false;
        }

        transaction.Commit();
        return true;
    }

    public static int ApplyStatus(World world, Entity target, StatusEffectDefinition status)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(status);
        ValidateStatus(status);
        if (!ReferenceEquals(target.OwnerWorld, world) || target.ToRemove
            || !target.TryGetProperty(world, status.Property, out var propertyValue)
            || !double.IsFinite(propertyValue))
        {
            throw new InvalidOperationException("A status requires a live target that exposes its modified property.");
        }

        var prefix = $"gameplay2d.status.{status.Id}.{target.Id}.";
        var activeIndices = world.Modifiers
            .Where(modifier => modifier.EntityId == target.Id && modifier.Property == status.Property
                && modifier.Source.StartsWith(prefix, StringComparison.Ordinal))
            .Select(modifier => int.TryParse(modifier.Source.AsSpan(prefix.Length), out var index) ? index : -1)
            .Where(index => index >= 0)
            .ToHashSet();
        var stackIndex = status.Stacking == StatusStackingMode.Refresh
            ? 0
            : Enumerable.Range(0, status.MaximumStacks).FirstOrDefault(index => !activeIndices.Contains(index), -1);
        if (stackIndex < 0)
        {
            stackIndex = 0;
        }

        var source = prefix + stackIndex;
        using var transaction = world.BeginTransaction();
        world.SetModifier(source, target, status.Property, status.Operation, status.Value,
            status.DurationTicks, stage: status.Stage);
        if (!double.IsFinite(world.EffectiveProperty(target, status.Property)))
        {
            throw new InvalidOperationException(
                $"Status '{status.Id}' would produce a non-finite effective '{status.Property}' value.");
        }

        world.Grant(source, target, GrantKind.Tag, status.Tag, status.DurationTicks);
        transaction.Commit();
        return Math.Min(activeIndices.Count + (activeIndices.Contains(stackIndex) ? 0 : 1), status.MaximumStacks);
    }

    internal static bool TryReadProjectile(int entityId, ReadOnlyWorldView world, out ProjectileState state)
    {
        if (world.TryGetBaseProperty(entityId, PositionX, out var x)
            && world.TryGetBaseProperty(entityId, PositionY, out var y)
            && world.TryGetBaseProperty(entityId, VelocityX, out var velocityX)
            && world.TryGetBaseProperty(entityId, VelocityY, out var velocityY)
            && world.TryGetBaseProperty(entityId, RemainingTicks, out var remainingTicks)
            && world.TryGetBaseProperty(entityId, HalfWidth, out var halfWidth)
            && world.TryGetBaseProperty(entityId, HalfHeight, out var halfHeight)
            && double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(velocityX)
            && double.IsFinite(velocityY) && double.IsFinite(remainingTicks)
            && double.IsFinite(halfWidth) && double.IsFinite(halfHeight))
        {
            state = new ProjectileState(x, y, velocityX, velocityY, remainingTicks, halfWidth, halfHeight);
            return true;
        }

        state = default;
        return false;
    }

    internal static bool TryReadProjectile(Entity entity, World world, out ProjectileState state)
    {
        if (entity.TryGetProperty(world, PositionX, out var x)
            && entity.TryGetProperty(world, PositionY, out var y)
            && entity.TryGetProperty(world, VelocityX, out var velocityX)
            && entity.TryGetProperty(world, VelocityY, out var velocityY)
            && entity.TryGetProperty(world, RemainingTicks, out var remainingTicks)
            && entity.TryGetProperty(world, HalfWidth, out var halfWidth)
            && entity.TryGetProperty(world, HalfHeight, out var halfHeight)
            && double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(velocityX)
            && double.IsFinite(velocityY) && double.IsFinite(remainingTicks)
            && double.IsFinite(halfWidth) && double.IsFinite(halfHeight))
        {
            state = new ProjectileState(x, y, velocityX, velocityY, remainingTicks, halfWidth, halfHeight);
            return true;
        }

        state = default;
        return false;
    }

    private static DataEntity Spawn(
        World world,
        ActorDefinition definition,
        Point2 position,
        IEnumerable<(string Name, double Value)> extraProperties,
        IEnumerable<string> extraTags)
    {
        var properties = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            [PositionX] = position.X,
            [PositionY] = position.Y,
            [HalfWidth] = definition.HalfWidth,
            [HalfHeight] = definition.HalfHeight,
        };
        foreach (var (name, value) in definition.Properties)
        {
            if (IsReservedProperty(name) || string.IsNullOrWhiteSpace(name) || !double.IsFinite(value)
                || !properties.TryAdd(name, value))
            {
                throw new ArgumentException($"Actor blueprint '{definition.Id}' contains invalid or reserved property '{name}'.", nameof(definition));
            }
        }

        foreach (var (name, value) in extraProperties)
        {
            if (!properties.TryAdd(name, value))
            {
                throw new ArgumentException($"Actor blueprint '{definition.Id}' contains reserved projectile property '{name}'.", nameof(definition));
            }
        }

        var tags = definition.Tags.Append(ActorTag).Append($"gameplay2d.blueprint.{definition.Id}")
            .Concat(extraTags).Distinct(StringComparer.Ordinal).ToArray();
        var entity = new DataEntity(tags, definition.Capabilities,
            [.. properties.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => (pair.Key, pair.Value))]);
        using var transaction = world.BeginTransaction();
        world.Add(entity);
        world.SetSpatialBounds(entity, new Bounds2(position.X, position.Y, definition.HalfWidth, definition.HalfHeight));
        transaction.Commit();
        return entity;
    }

    private static void ValidateActorDefinition(ActorDefinition definition, Point2 position)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Id);
        ArgumentNullException.ThrowIfNull(definition.Tags);
        ArgumentNullException.ThrowIfNull(definition.Capabilities);
        ArgumentNullException.ThrowIfNull(definition.Properties);
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y)
            || !float.IsFinite(definition.HalfWidth) || !float.IsFinite(definition.HalfHeight)
            || definition.HalfWidth < 0 || definition.HalfHeight < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(position), "Actor position and bounds must be finite and non-negative.");
        }
    }

    private static void ValidateStatus(StatusEffectDefinition status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(status.Tag);
        ArgumentException.ThrowIfNullOrWhiteSpace(status.Property);
        if (!double.IsFinite(status.Value) || status.DurationTicks is < 1 or > 1_000_000
            || status.MaximumStacks is < 1 or > 64
            || status.Stacking is not (StatusStackingMode.Refresh or StatusStackingMode.Stack)
            || status.Operation is not (ModifierOperation.Add or ModifierOperation.Multiply or ModifierOperation.Set)
            || status.Stage is not (ModifierStage.Standard or ModifierStage.Foundation or ModifierStage.Additive
                or ModifierStage.Multiplicative or ModifierStage.Override or ModifierStage.Final))
        {
            throw new ArgumentException("Status values, duration, stacking, and modifier stage are invalid.", nameof(status));
        }
    }

    private static bool IsReservedProperty(string name) => name is PositionX or PositionY or HalfWidth or HalfHeight
        or VelocityX or VelocityY or RemainingTicks;
}

internal readonly record struct ProjectileState(
    double X,
    double Y,
    double VelocityX,
    double VelocityY,
    double RemainingTicks,
    double HalfWidth,
    double HalfHeight);
