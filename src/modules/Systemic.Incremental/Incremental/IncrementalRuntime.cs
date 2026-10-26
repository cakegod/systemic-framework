using System.Collections.Immutable;
using Systemic.Kernel;
using Systemic.Host;
using Systemic.Rules;

namespace Systemic.Incremental;

public readonly record struct IncrementalCommandResult(
    bool Accepted,
    string? Reason = null,
    RuleDispatchResult? Reactions = null);

/// <summary>Runs resource production, upgrade purchases, and role assignment over generic kernel entities.</summary>
public sealed class IncrementalRuntime : ISystemicSystem
{
    private readonly IncrementalCatalog _catalog;
    private readonly World _world;
    private readonly ImmutableDictionary<string, int> _entityIds;
    private readonly Dictionary<string, ResourceDefinition> _resources;
    private readonly Dictionary<string, int> _resourceEntityIds;
    private readonly Dictionary<string, int> _producerEntityIds;
    private readonly Dictionary<string, ActorRoleDefinition> _roles;
    private readonly Dictionary<string, UpgradeDefinition> _upgrades;
    private readonly Dictionary<string, int> _upgradeEntityIds;

    public IncrementalRuntime(
        IncrementalCatalog catalog,
        World world,
        IReadOnlyDictionary<string, int> entityIds,
        int order = 100,
        bool initialize = true)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(entityIds);
        _catalog = catalog;
        _world = world;
        _entityIds = entityIds.ToImmutableDictionary(StringComparer.Ordinal);
        _resources = catalog.Resources.ToDictionary(definition => definition.Id, StringComparer.Ordinal);
        _resourceEntityIds = _resources.ToDictionary(pair => pair.Key,
            pair => ResolveEntityId(pair.Value.EntityId), StringComparer.Ordinal);
        _producerEntityIds = catalog.Producers.ToDictionary(definition => definition.Id,
            definition => ResolveEntityId(definition.EntityId), StringComparer.Ordinal);
        _roles = catalog.Roles.ToDictionary(definition => definition.Id, StringComparer.Ordinal);
        _upgrades = catalog.Upgrades.ToDictionary(definition => definition.Id, StringComparer.Ordinal);
        _upgradeEntityIds = _upgrades.ToDictionary(pair => pair.Key,
            pair => ResolveEntityId(pair.Value.EntityId), StringComparer.Ordinal);
        Order = order;

        if (initialize)
        {
            InitializeEntities();
        }
    }

    public string Id => "systemic.incremental";
    public int Order { get; }

    public IncrementalCommandResult AssignRole(int entityId, string roleId)
    {
        if (!_roles.TryGetValue(roleId, out var role))
        {
            return new(false, $"Unknown actor role '{roleId}'.");
        }

        var entity = _world.ById(entityId);
        if (entity is null)
        {
            return new(false, $"Entity {entityId} does not exist.");
        }

        using var transaction = _world.BeginTransaction();
        foreach (var tag in role.Tags)
        {
            entity.AddTag(tag);
        }

        foreach (var capability in role.Capabilities)
        {
            entity.AddCapability(capability);
        }

        transaction.Commit();
        return new(true);
    }

    public IncrementalCommandResult PurchaseUpgrade(
        string upgradeId,
        int purchaserId,
        Func<SimulationFact, RuleDispatchResult> publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        if (!_upgrades.TryGetValue(upgradeId, out var upgrade))
        {
            return new(false, $"Unknown upgrade '{upgradeId}'.");
        }

        var purchaser = _world.ById(purchaserId);
        if (purchaser is null)
        {
            return new(false, $"Purchasing entity {purchaserId} does not exist.");
        }

        var upgradeEntity = _world.ById(_upgradeEntityIds[upgradeId])!;
        if (!upgradeEntity.TryGetProperty(_world, upgrade.PurchaseCountProperty, out var purchases)
            || !double.IsFinite(purchases) || purchases < 0 || purchases >= upgrade.MaxPurchases)
        {
            return new(false, $"Upgrade '{upgradeId}' has reached its purchase limit or has invalid purchase state.");
        }

        var costs = new List<(Entity Resource, string Property, double NewBalance)>(upgrade.Costs.Length);
        foreach (var cost in upgrade.Costs)
        {
            var resourceEntity = _world.ById(_resourceEntityIds[cost.ResourceId])!;
            var resource = _resources[cost.ResourceId];
            if (!resourceEntity.TryGetProperty(_world, resource.BalanceProperty, out var balance)
                || !double.IsFinite(balance) || balance < cost.Amount)
            {
                return new(false, $"Insufficient '{cost.ResourceId}' to purchase '{upgradeId}'.");
            }

            costs.Add((resourceEntity, resource.BalanceProperty, balance - cost.Amount));
        }

        using var transaction = _world.BeginTransaction();
        foreach (var (resourceEntity, property, newBalance) in costs)
        {
            resourceEntity.TrySetProperty(_world, property, newBalance);
        }

        upgradeEntity.TrySetProperty(_world, upgrade.PurchaseCountProperty, purchases + 1);
        var reactions = publish(new SimulationFact(upgrade.PurchasedEvent, purchaserId, upgradeEntity.Id));
        if (reactions.RolledBack)
        {
            transaction.Rollback();
            return new(false, "Upgrade reactions produced invalid state; purchase was rolled back.", reactions);
        }

        transaction.Commit();
        return new(true, Reactions: reactions);
    }

    public void Step(SystemicStepContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!ReferenceEquals(context.World, _world))
        {
            throw new InvalidOperationException("Incremental runtime cannot step a different world than the one it was initialized with.");
        }

        using var transaction = _world.BeginTransaction();
        foreach (var producer in _catalog.Producers)
        {
            if (context.Tick % producer.IntervalTicks != 0)
            {
                continue;
            }

            var producerEntity = _world.ById(_producerEntityIds[producer.Id])!;
            var resource = _resources[producer.ResourceId];
            var resourceEntity = _world.ById(_resourceEntityIds[producer.ResourceId])!;
            producerEntity.TryGetProperty(_world, producer.RateProperty, out var rate);
            resourceEntity.TryGetProperty(_world, resource.BalanceProperty, out var balance);
            var nextBalance = balance + rate;
            if (!double.IsFinite(rate) || !double.IsFinite(balance) || !double.IsFinite(nextBalance))
            {
                throw new InvalidOperationException($"Producer '{producer.Id}' produced non-finite state.");
            }

            nextBalance = Math.Max(0, nextBalance);
            if (resource.Capacity is { } capacity)
            {
                nextBalance = Math.Min(capacity, nextBalance);
            }

            resourceEntity.TrySetProperty(_world, resource.BalanceProperty, nextBalance);
            var result = context.Publish(new SimulationFact(producer.EventType, producerEntity.Id, resourceEntity.Id));
            if (result.RolledBack)
            {
                transaction.Rollback();
                return;
            }

            if (!resourceEntity.TryGetProperty(_world, resource.BalanceProperty, out var reactedBalance)
                || !double.IsFinite(reactedBalance))
            {
                throw new InvalidOperationException($"Production reactions left resource '{resource.Id}' in invalid state.");
            }

            var boundedBalance = Math.Max(0, reactedBalance);
            if (resource.Capacity is { } maxBalance)
            {
                boundedBalance = Math.Min(maxBalance, boundedBalance);
            }

            if (boundedBalance != reactedBalance)
            {
                resourceEntity.TrySetProperty(_world, resource.BalanceProperty, boundedBalance);
            }
        }

        transaction.Commit();
    }

    private int ResolveEntityId(string contentId)
    {
        if (!_entityIds.TryGetValue(contentId, out var id) || _world.ById(id) is null)
        {
            throw new InvalidOperationException($"Incremental definition references missing entity '{contentId}'.");
        }

        return id;
    }

    private void InitializeEntities()
    {
        foreach (var resource in _catalog.Resources)
        {
            var entity = _world.ById(_resourceEntityIds[resource.Id])!;
            if (!entity.TrySetProperty(_world, resource.BalanceProperty, resource.InitialValue))
            {
                throw new InvalidOperationException($"Resource '{resource.Id}' requires property '{resource.BalanceProperty}'.");
            }
        }

        foreach (var producer in _catalog.Producers)
        {
            var entity = _world.ById(_producerEntityIds[producer.Id])!;
            if (!entity.TrySetProperty(_world, producer.RateProperty, producer.BaseRate))
            {
                throw new InvalidOperationException($"Producer '{producer.Id}' requires property '{producer.RateProperty}'.");
            }
        }

        foreach (var upgrade in _catalog.Upgrades)
        {
            var entity = _world.ById(_upgradeEntityIds[upgrade.Id])!;
            if (!entity.TrySetProperty(_world, upgrade.PurchaseCountProperty, 0))
            {
                throw new InvalidOperationException($"Upgrade '{upgrade.Id}' requires property '{upgrade.PurchaseCountProperty}'.");
            }
        }
    }
}
