using System.Collections.Immutable;

namespace Systemic.Incremental;

public sealed record ResourceDefinition(
    string Id,
    string EntityId,
    string BalanceProperty,
    double InitialValue,
    double? Capacity);

public sealed record ProducerDefinition(
    string Id,
    string EntityId,
    string ResourceId,
    string RateProperty,
    double BaseRate,
    int IntervalTicks,
    string EventType);

public sealed record ResourceCost(string ResourceId, double Amount);

public sealed record UpgradeDefinition(
    string Id,
    string EntityId,
    ImmutableArray<ResourceCost> Costs,
    int MaxPurchases,
    string PurchaseCountProperty,
    string PurchasedEvent);

public sealed record ActorRoleDefinition(
    string Id,
    ImmutableArray<string> Tags,
    ImmutableArray<string> Capabilities);

public sealed record IncrementalCatalog(
    ImmutableArray<ResourceDefinition> Resources,
    ImmutableArray<ProducerDefinition> Producers,
    ImmutableArray<UpgradeDefinition> Upgrades,
    ImmutableArray<ActorRoleDefinition> Roles);

/// <summary>Small fluent API for composing reusable incremental definitions in C#.</summary>
public sealed class IncrementalCatalogBuilder
{
    private readonly List<ResourceDefinition> _resources = [];
    private readonly List<ProducerDefinition> _producers = [];
    private readonly List<UpgradeDefinition> _upgrades = [];
    private readonly List<ActorRoleDefinition> _roles = [];

    public IncrementalCatalogBuilder Resource(
        string id,
        string entityId,
        double initialValue = 0,
        double? capacity = null,
        string balanceProperty = "balance")
    {
        _resources.Add(new ResourceDefinition(id, entityId, balanceProperty, initialValue, capacity));
        return this;
    }

    public IncrementalCatalogBuilder Producer(
        string id,
        string entityId,
        string resourceId,
        double baseRate,
        int intervalTicks = 1,
        string rateProperty = "rate",
        string eventType = "incremental.production")
    {
        _producers.Add(new ProducerDefinition(id, entityId, resourceId, rateProperty, baseRate,
            intervalTicks, eventType));
        return this;
    }

    public IncrementalCatalogBuilder Upgrade(
        string id,
        string entityId,
        IEnumerable<ResourceCost> costs,
        int maxPurchases = 1,
        string purchaseCountProperty = "purchases",
        string purchasedEvent = "incremental.upgrade.purchased")
    {
        ArgumentNullException.ThrowIfNull(costs);
        _upgrades.Add(new UpgradeDefinition(id, entityId, [.. costs], maxPurchases,
            purchaseCountProperty, purchasedEvent));
        return this;
    }

    public IncrementalCatalogBuilder Role(
        string id,
        IEnumerable<string>? tags = null,
        IEnumerable<string>? capabilities = null)
    {
        _roles.Add(new ActorRoleDefinition(id, [.. tags ?? []], [.. capabilities ?? []]));
        return this;
    }

    public IncrementalCatalog Build() => new(
        [.. _resources.OrderBy(definition => definition.Id, StringComparer.Ordinal)],
        [.. _producers.OrderBy(definition => definition.Id, StringComparer.Ordinal)],
        [.. _upgrades.OrderBy(definition => definition.Id, StringComparer.Ordinal)],
        [.. _roles.OrderBy(definition => definition.Id, StringComparer.Ordinal)]);
}
