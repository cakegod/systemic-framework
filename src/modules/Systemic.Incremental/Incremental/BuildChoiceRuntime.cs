using System.Collections.Immutable;
using System.Text.Json;
using Systemic.Kernel;
using Systemic.Host;
using Systemic.Rules;

namespace Systemic.Incremental;

public sealed class BuildChoiceRuntime : ISystemicSystem, ISystemicStatefulExtension
{
    private const int MaximumAdditionalOwners = 1024;
    private const int MaximumAdditionalChoiceInstances = 65_536;
    private readonly BuildChoiceCatalog _catalog;
    private readonly World _world;
    private readonly ImmutableDictionary<string, int> _entityIds;
    private readonly Dictionary<string, BuildChoiceGroupDefinition> _groups;
    private readonly Dictionary<string, BuildChoiceDefinition> _choices;
    private readonly Dictionary<string, int> _choiceEntityIds;
    private readonly Dictionary<string, int> _ownerEntityIds;
    private readonly Dictionary<string, int> _progressEntityIds;
    private readonly Dictionary<(string ChoiceId, int OwnerEntityId), int> _choiceInstanceEntityIds = [];
    private readonly Dictionary<(string GroupId, int OwnerEntityId), int> _progressEntityIdsByOwner = [];
    private readonly Dictionary<string, List<int>> _ownersByGroup;
    private readonly Dictionary<string, HashSet<int>> _ownerIdsByGroup;
    private readonly Dictionary<string, ImmutableArray<BuildChoiceDefinition>> _choicesByGroup;
    private readonly Dictionary<string, ImmutableArray<BuildChoiceDefinition>> _choicesByGroupIdOrder;
    private readonly Dictionary<string, ImmutableArray<BuildChoiceDefinition>> _selectableChoicesByGroup;
    private readonly Dictionary<string, ImmutableArray<BuildChoiceDefinition>> _exclusionNeighborsByChoice;
    private readonly Dictionary<string, int> _resourceEntityIds;
    private readonly Dictionary<string, ResourceDefinition> _resources;
    private readonly Dictionary<string, ImmutableArray<BuildChoiceDefinition>> _choicesByTriggerEvent;

    public BuildChoiceRuntime(
        BuildChoiceCatalog catalog,
        IncrementalCatalog? incremental,
        World world,
        IReadOnlyDictionary<string, int> entityIds,
        int order = 90,
        bool initialize = true)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(entityIds);
        _catalog = catalog;
        _world = world;
        _entityIds = entityIds.ToImmutableDictionary(StringComparer.Ordinal);
        _groups = catalog.Groups.ToDictionary(group => group.Id, StringComparer.Ordinal);
        _choices = catalog.Choices.ToDictionary(choice => choice.Id, StringComparer.Ordinal);
        _choicesByGroup = catalog.Groups.ToDictionary(group => group.Id,
            group => catalog.Choices.Where(choice => choice.GroupId == group.Id).ToImmutableArray(),
            StringComparer.Ordinal);
        _choicesByGroupIdOrder = _choicesByGroup.ToDictionary(pair => pair.Key,
            pair => pair.Value.OrderBy(choice => choice.Id, StringComparer.Ordinal).ToImmutableArray(),
            StringComparer.Ordinal);
        _selectableChoicesByGroup = _choicesByGroup.ToDictionary(pair => pair.Key,
            pair => pair.Value.Where(choice => !choice.IsFree).ToImmutableArray(), StringComparer.Ordinal);
        _exclusionNeighborsByChoice = BuildExclusionNeighbors(catalog.Choices);
        _choiceEntityIds = _choices.ToDictionary(pair => pair.Key,
            pair => ResolveEntityId(pair.Value.EntityId), StringComparer.Ordinal);
        _ownerEntityIds = _groups.ToDictionary(pair => pair.Key,
            pair => ResolveEntityId(pair.Value.OwnerEntityId), StringComparer.Ordinal);
        _ownersByGroup = _groups.ToDictionary(pair => pair.Key,
            pair => new List<int> { _ownerEntityIds[pair.Key] }, StringComparer.Ordinal);
        _ownerIdsByGroup = _groups.ToDictionary(pair => pair.Key,
            pair => new HashSet<int> { _ownerEntityIds[pair.Key] }, StringComparer.Ordinal);
        _progressEntityIds = _groups.ToDictionary(pair => pair.Key,
            pair => ResolveEntityId(pair.Value.ProgressEntityId), StringComparer.Ordinal);
        foreach (var group in catalog.Groups)
        {
            _progressEntityIdsByOwner.Add((group.Id, _ownerEntityIds[group.Id]), _progressEntityIds[group.Id]);
        }

        foreach (var choice in catalog.Choices)
        {
            _choiceInstanceEntityIds.Add((choice.Id, _ownerEntityIds[choice.GroupId]), _choiceEntityIds[choice.Id]);
        }
        _resources = (incremental?.Resources ?? []).ToDictionary(resource => resource.Id, StringComparer.Ordinal);
        _resourceEntityIds = _resources.ToDictionary(pair => pair.Key,
            pair => ResolveEntityId(pair.Value.EntityId), StringComparer.Ordinal);
        _choicesByTriggerEvent = catalog.Choices
            .Where(choice => choice.ActivationMode == ChoiceActivationMode.Triggered && choice.TriggerEvent is not null)
            .GroupBy(choice => choice.TriggerEvent!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.OrderBy(choice => choice.Id, StringComparer.Ordinal).ToImmutableArray(),
                StringComparer.Ordinal);
        Order = order;

        if (initialize)
        {
            InitializeChoices();
        }
    }

    public string Id => "systemic.build-choices";
    public int Order { get; }

    public bool TryGetState(string choiceId, out BuildChoiceState state)
    {
        if (!_choices.TryGetValue(choiceId, out var choice))
        {
            state = default;
            return false;
        }

        return TryGetState(choiceId, _ownerEntityIds[choice.GroupId], out state);
    }

    /// <summary>Reads the state for a registered owner, or returns false when that owner has no group instance.</summary>
    public bool TryGetState(string choiceId, int ownerEntityId, out BuildChoiceState state)
    {
        if (!_choices.TryGetValue(choiceId, out var choice) || !IsOwnerRegistered(choice.GroupId, ownerEntityId))
        {
            state = default;
            return false;
        }

        var selected = IsSelected(choice, ownerEntityId);
        var activeUntil = ReadChoiceValue(choice, ownerEntityId, choice.ActiveUntilProperty);
        var cooldownUntil = ReadChoiceValue(choice, ownerEntityId, choice.CooldownUntilProperty);
        var activeRemaining = RemainingTicks(activeUntil, _world.LogicalTick);
        var cooldownStart = Math.Max((double)_world.LogicalTick, activeUntil);
        var cooldownRemaining = RemainingTicks(cooldownUntil, cooldownStart);
        var cooldownProgress = choice.CooldownTicks == 0
            ? 1
            : Math.Clamp(1 - (cooldownRemaining / (double)choice.CooldownTicks), 0, 1);
        var locked = IsLocked(choice, ownerEntityId);
        var available = !selected && !locked && !HasSelectedExclusion(choice, ownerEntityId)
            && SelectedCount(_groups[choice.GroupId], ownerEntityId) < _groups[choice.GroupId].SelectionLimit
            && CanAfford(choice);
        var isActive = selected && (choice.ActivationMode == ChoiceActivationMode.Passive
            || activeUntil > _world.LogicalTick);
        state = new BuildChoiceState(selected, isActive, activeRemaining, cooldownRemaining,
            cooldownProgress, locked, available);
        return true;
    }

    /// <summary>Creates an independent, saved choice instance set for another live group owner.</summary>
    public IncrementalCommandResult RegisterOwner(string groupId, int ownerEntityId, int? progressEntityId = null)
    {
        if (!_groups.TryGetValue(groupId, out var group))
        {
            return new(false, $"Unknown build choice group '{groupId}'.");
        }

        var owner = _world.ById(ownerEntityId);
        if (owner is not { ToRemove: false })
        {
            return new(false, $"Choice owner entity {ownerEntityId} is missing or removed.");
        }

        if (IsOwnerRegistered(groupId, ownerEntityId))
        {
            return new(false, $"Entity {ownerEntityId} already owns group '{groupId}'.");
        }

        if (_ownersByGroup[groupId].Count > MaximumAdditionalOwners)
        {
            return new(false, $"Group '{groupId}' reached its {MaximumAdditionalOwners}-owner limit.");
        }

        var resolvedProgressId = progressEntityId
            ?? (owner.TryGetProperty(_world, group.ProgressProperty, out _)
                ? ownerEntityId
                : _progressEntityIds[groupId]);
        if (_world.ById(resolvedProgressId) is not { ToRemove: false } progress
            || !progress.TryGetProperty(_world, group.ProgressProperty, out var progressValue)
            || !double.IsFinite(progressValue))
        {
            return new(false, $"Choice owner {ownerEntityId} has no live progress entity with property '{group.ProgressProperty}'.");
        }

        var groupChoices = _choicesByGroup[groupId];
        if (_choiceInstanceEntityIds.Count - _choiceEntityIds.Count + groupChoices.Length > MaximumAdditionalChoiceInstances)
        {
            return new(false, $"Build choices reached the {MaximumAdditionalChoiceInstances}-instance limit.");
        }

        var clonedInstances = new Dictionary<string, int>(groupChoices.Length, StringComparer.Ordinal);
        using var transaction = _world.BeginTransaction();
        foreach (var choice in groupChoices)
        {
            clonedInstances.Add(choice.Id, CloneChoiceEntity(choice));
        }

        transaction.Commit();
        _ownersByGroup[groupId].Add(ownerEntityId);
        _ownerIdsByGroup[groupId].Add(ownerEntityId);
        _progressEntityIdsByOwner.Add((groupId, ownerEntityId), resolvedProgressId);
        foreach (var (choiceId, entityId) in clonedInstances)
        {
            _choiceInstanceEntityIds.Add((choiceId, ownerEntityId), entityId);
        }

        return new(true);
    }

    public JsonElement CaptureState()
    {
        var owners = _ownersByGroup.SelectMany(pair => pair.Value
                .Where(ownerId => ownerId != _ownerEntityIds[pair.Key])
                .Order()
                .Select(ownerId => new BuildChoiceOwnerInstanceState(pair.Key, ownerId,
                    _progressEntityIdsByOwner[(pair.Key, ownerId)],
                    [.. _choicesByGroupIdOrder[pair.Key]
                        .Select(choice => new BuildChoiceEntityInstanceState(choice.Id,
                            _choiceInstanceEntityIds[(choice.Id, ownerId)]))])))
            .OrderBy(owner => owner.GroupId, StringComparer.Ordinal)
            .ThenBy(owner => owner.OwnerEntityId)
            .ToArray();
        return JsonSerializer.SerializeToElement(new BuildChoiceRuntimeState([.. owners]));
    }

    public void RestoreState(JsonElement state)
    {
        var restored = state.Deserialize<BuildChoiceRuntimeState>()
            ?? throw new InvalidOperationException("Saved build-choice owner state is empty.");
        if (restored.Owners.Length > MaximumAdditionalOwners * Math.Max(1, _groups.Count))
        {
            throw new InvalidOperationException("Saved build-choice owner state exceeds its configured limit.");
        }

        var seenOwners = new HashSet<(string GroupId, int OwnerEntityId)>();
        var seenInstances = new HashSet<int>(_choiceEntityIds.Values);
        var ownerCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var additionalInstanceCount = 0;
        foreach (var ownerState in restored.Owners)
        {
            if (!_groups.TryGetValue(ownerState.GroupId, out var group)
                || ownerState.OwnerEntityId == _ownerEntityIds.GetValueOrDefault(ownerState.GroupId)
                || !seenOwners.Add((ownerState.GroupId, ownerState.OwnerEntityId))
                || _world.ById(ownerState.OwnerEntityId) is not { ToRemove: false }
                || _world.ById(ownerState.ProgressEntityId) is not { ToRemove: false } progress
                || !progress.TryGetProperty(_world, group.ProgressProperty, out var progressValue)
                || !double.IsFinite(progressValue))
            {
                throw new InvalidOperationException("Saved build-choice owner state contains an invalid owner or progress entity.");
            }

            ownerCounts.TryGetValue(group.Id, out var ownersForGroup);
            if (ownersForGroup >= MaximumAdditionalOwners)
            {
                throw new InvalidOperationException($"Saved build-choice group '{group.Id}' exceeds its owner limit.");
            }

            ownerCounts[group.Id] = ownersForGroup + 1;

            var groupChoices = _choicesByGroupIdOrder[group.Id];
            if (ownerState.Choices.Length != groupChoices.Length)
            {
                throw new InvalidOperationException($"Saved choice owner {ownerState.OwnerEntityId} does not contain the complete group '{group.Id}'.");
            }

            for (var index = 0; index < groupChoices.Length; index++)
            {
                var choice = groupChoices[index];
                var instance = ownerState.Choices[index];
                if (instance.ChoiceId != choice.Id || !seenInstances.Add(instance.EntityId)
                    || _world.ById(instance.EntityId) is not DataEntity entity
                    || !entity.Properties.ContainsKey(choice.SelectedProperty)
                    || choice.ActivationMode != ChoiceActivationMode.Passive
                        && (!entity.Properties.ContainsKey(choice.ActiveUntilProperty)
                            || !entity.Properties.ContainsKey(choice.CooldownUntilProperty)))
                {
                    throw new InvalidOperationException("Saved build-choice state references an invalid choice instance.");
                }

                additionalInstanceCount++;
                if (additionalInstanceCount > MaximumAdditionalChoiceInstances)
                {
                    throw new InvalidOperationException("Saved build-choice state exceeds its instance limit.");
                }
            }
        }

        foreach (var ownerState in restored.Owners)
        {
            _ownersByGroup[ownerState.GroupId].Add(ownerState.OwnerEntityId);
            _ownerIdsByGroup[ownerState.GroupId].Add(ownerState.OwnerEntityId);
            _progressEntityIdsByOwner.Add((ownerState.GroupId, ownerState.OwnerEntityId), ownerState.ProgressEntityId);
            foreach (var instance in ownerState.Choices)
            {
                _choiceInstanceEntityIds.Add((instance.ChoiceId, ownerState.OwnerEntityId), instance.EntityId);
            }
        }
    }

    public IncrementalCommandResult SelectChoice(
        string choiceId,
        int purchaserId,
        Func<SimulationFact, RuleDispatchResult> publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        if (!_choices.TryGetValue(choiceId, out var choice))
        {
            return new(false, $"Unknown build choice '{choiceId}'.");
        }

        var group = _groups[choice.GroupId];
        if (!IsOwnerRegistered(group.Id, purchaserId))
        {
            return new(false, $"Entity {purchaserId} is not registered as an owner of choice group '{group.Id}'.");
        }

        if (IsLocked(choice, purchaserId))
        {
            return new(false, $"Build choice '{choice.Id}' is locked by its group or prerequisites.");
        }

        if (choice.IsFree)
        {
            return new(false, $"Free build choice '{choiceId}' is activated automatically when its group unlocks.");
        }

        return SelectCore(choice, group, purchaserId, publish, isAutomatic: false);
    }

    public IncrementalCommandResult ActivateChoice(
        string choiceId,
        int actorId,
        Func<SimulationFact, RuleDispatchResult> publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        if (!_choices.TryGetValue(choiceId, out var choice))
        {
            return new(false, $"Unknown build choice '{choiceId}'.");
        }

        if (choice.ActivationMode != ChoiceActivationMode.Timed)
        {
            return new(false, $"Build choice '{choiceId}' is not manually activated.");
        }

        var group = _groups[choice.GroupId];
        if (!IsOwnerRegistered(group.Id, actorId))
        {
            return new(false, $"Entity {actorId} is not registered as an owner of choice group '{group.Id}'.");
        }

        if (IsLocked(choice, actorId) || !IsSelected(choice, actorId))
        {
            return new(false, $"Build choice '{choiceId}' is locked or not selected.");
        }

        if (!CanActivate(choice, actorId))
        {
            return new(false, $"Build choice '{choiceId}' is on cooldown.");
        }

        using var transaction = _world.BeginTransaction();
        ActivateState(choice, actorId);
        var result = publish(new SimulationFact(choice.ActivationEvent!, actorId,
            ChoiceEntityId(choice, actorId)));
        if (result.RolledBack)
        {
            transaction.Rollback();
            return new(false, "Build choice reactions produced invalid state; activation was rolled back.", result);
        }

        transaction.Commit();
        return new(true, Reactions: result);
    }

    public void ObserveFact(SimulationFact fact, SimulationFactEmitter emitter)
    {
        if (!_choicesByTriggerEvent.TryGetValue(fact.Type, out var choices))
        {
            return;
        }

        foreach (var choice in choices)
        {
            var group = _groups[choice.GroupId];
            foreach (var ownerId in _ownersByGroup[group.Id])
            {
                if (!IsLocked(choice, ownerId) && IsSelected(choice, ownerId) && CanActivate(choice, ownerId))
                {
                    ActivateState(choice, ownerId);
                    emitter.Enqueue(new SimulationFact(choice.ActivationEvent!, fact.SourceId,
                        ChoiceEntityId(choice, ownerId), fact.Depth + 1));
                }
            }
        }
    }

    public void Step(SystemicSystemContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.IsForWorld(_world))
        {
            throw new InvalidOperationException("Build choice runtime cannot step a different world than the one it was initialized with.");
        }

        using var transaction = _world.BeginTransaction();
        foreach (var group in _catalog.Groups)
        {
            if (group.FreeChoiceId is not { } freeChoiceId)
            {
                continue;
            }

            var choice = _choices[freeChoiceId];
            foreach (var ownerId in _ownersByGroup[group.Id])
            {
                if (!IsSelected(choice, ownerId) && !IsLocked(choice, ownerId))
                {
                    var result = SelectCore(choice, group, ownerId, context.Publish, isAutomatic: true);
                    if (!result.Accepted || result.Reactions is { RolledBack: true })
                    {
                        transaction.Rollback();
                        return;
                    }
                }
            }
        }

        foreach (var choice in _catalog.Choices)
        {
            if (choice.ActivationMode == ChoiceActivationMode.Passive)
            {
                continue;
            }

            foreach (var ownerId in _ownersByGroup[choice.GroupId])
            {
                var activeUntil = ReadChoiceValue(choice, ownerId, choice.ActiveUntilProperty);
                if (activeUntil > 0 && context.Tick >= activeUntil)
                {
                    WriteChoiceValue(choice, ownerId, choice.ActiveUntilProperty, 0);
                    _world.ById(ownerId)!.RemoveTag(ActiveTag(choice));
                    _world.ById(ChoiceEntityId(choice, ownerId))!.RemoveTag(ActiveTag(choice));
                }
            }
        }

        transaction.Commit();
    }

    private IncrementalCommandResult SelectCore(
        BuildChoiceDefinition choice,
        BuildChoiceGroupDefinition group,
        int purchaserId,
        Func<SimulationFact, RuleDispatchResult> publish,
        bool isAutomatic)
    {
        if (!IsSelected(choice, purchaserId) && SelectedCount(group, purchaserId) >= group.SelectionLimit)
        {
            return new(false, $"Build choice group '{group.Id}' has reached its selection limit.");
        }

        if (IsSelected(choice, purchaserId))
        {
            return new(false, $"Build choice '{choice.Id}' is already selected.");
        }

        if (IsLocked(choice, purchaserId))
        {
            return new(false, $"Build choice '{choice.Id}' is locked by its group or prerequisites.");
        }

        if (HasSelectedExclusion(choice, purchaserId))
        {
            return new(false, $"Build choice '{choice.Id}' conflicts with a selected choice.");
        }

        Entity? resourceEntity = null;
        ResourceDefinition? resource = null;
        var nextBalance = 0d;
        if (!isAutomatic && choice.CostResourceId is { } resourceId)
        {
            resource = _resources[resourceId];
            resourceEntity = _world.ById(_resourceEntityIds[resourceId])!;
            if (!resourceEntity.TryGetProperty(_world, resource.BalanceProperty, out var balance)
                || !double.IsFinite(balance) || balance < choice.CostAmount)
            {
                return new(false, $"Insufficient '{resourceId}' to select '{choice.Id}'.");
            }

            nextBalance = balance - choice.CostAmount;
        }

        using var transaction = _world.BeginTransaction();
        if (resourceEntity is not null && resource is not null)
        {
            resourceEntity.TrySetProperty(_world, resource.BalanceProperty, nextBalance);
        }

        WriteChoiceValue(choice, purchaserId, choice.SelectedProperty, 1);
        _world.ById(purchaserId)!.AddTag(choice.ChoiceTag);
        var result = publish(new SimulationFact("incremental.choice.selected", purchaserId,
            ChoiceEntityId(choice, purchaserId)));
        if (result.RolledBack)
        {
            transaction.Rollback();
            return new(false, "Build choice reactions produced invalid state; selection was rolled back.", result);
        }

        transaction.Commit();
        return new(true, Reactions: result);
    }

    private bool IsUnlocked(BuildChoiceGroupDefinition group, int ownerEntityId)
    {
        var progress = _world.ById(_progressEntityIdsByOwner[(group.Id, ownerEntityId)])!;
        return progress.TryGetProperty(_world, group.ProgressProperty, out var value)
            && double.IsFinite(value) && value >= group.UnlockAt;
    }

    private bool IsLocked(BuildChoiceDefinition choice, int ownerEntityId)
    {
        if (!IsUnlocked(_groups[choice.GroupId], ownerEntityId))
        {
            return true;
        }

        if (GetReferences(choice.Requires).Any(required => !_choices.TryGetValue(required, out var requirement)
                || !IsSelected(requirement, ChoiceOwnerForGroup(requirement.GroupId, ownerEntityId))))
        {
            return true;
        }

        var requiresAny = GetReferences(choice.RequiresAny);
        return requiresAny.Length > 0
            && !requiresAny.Any(required => _choices.TryGetValue(required, out var requirement)
                && IsSelected(requirement, ChoiceOwnerForGroup(requirement.GroupId, ownerEntityId)));
    }

    private bool HasSelectedExclusion(BuildChoiceDefinition choice, int ownerEntityId)
    {
        foreach (var candidate in _exclusionNeighborsByChoice[choice.Id])
        {
            if (IsSelected(candidate, ChoiceOwnerForGroup(candidate.GroupId, ownerEntityId)))
            {
                return true;
            }
        }

        return false;
    }

    private bool CanAfford(BuildChoiceDefinition choice)
    {
        if (choice.CostResourceId is not { } resourceId)
        {
            return true;
        }

        var resource = _resources[resourceId];
        return _world.ById(_resourceEntityIds[resourceId]) is { } entity
            && entity.TryGetProperty(_world, resource.BalanceProperty, out var balance)
            && double.IsFinite(balance) && balance >= choice.CostAmount;
    }

    private static ImmutableArray<string> GetReferences(ImmutableArray<string> references) =>
        references.IsDefault ? [] : references;

    private bool IsSelected(BuildChoiceDefinition choice, int ownerEntityId) =>
        ReadChoiceValue(choice, ChoiceOwnerForGroup(choice.GroupId, ownerEntityId), choice.SelectedProperty) > 0;

    private int SelectedCount(BuildChoiceGroupDefinition group, int ownerEntityId)
    {
        var count = 0;
        foreach (var choice in _selectableChoicesByGroup[group.Id])
        {
            if (IsSelected(choice, ownerEntityId))
            {
                count++;
            }
        }

        return count;
    }

    private bool CanActivate(BuildChoiceDefinition choice, int ownerEntityId) =>
        _world.LogicalTick >= ReadChoiceValue(choice, ownerEntityId, choice.CooldownUntilProperty);

    private void ActivateState(BuildChoiceDefinition choice, int ownerEntityId)
    {
        var activeUntil = checked((long)_world.LogicalTick + choice.ActiveTicks);
        var cooldownUntil = checked(activeUntil + choice.CooldownTicks);
        WriteChoiceValue(choice, ownerEntityId, choice.ActiveUntilProperty, activeUntil);
        WriteChoiceValue(choice, ownerEntityId, choice.CooldownUntilProperty, cooldownUntil);
        _world.ById(ownerEntityId)!.AddTag(ActiveTag(choice));
        _world.ById(ChoiceEntityId(choice, ownerEntityId))!.AddTag(ActiveTag(choice));
    }

    private double ReadChoiceValue(BuildChoiceDefinition choice, int ownerEntityId, string property)
    {
        _world.ById(ChoiceEntityId(choice, ownerEntityId))!.TryGetProperty(_world, property, out var value);
        return value;
    }

    private void WriteChoiceValue(BuildChoiceDefinition choice, int ownerEntityId, string property, double value)
    {
        if (!_world.ById(ChoiceEntityId(choice, ownerEntityId))!.TrySetProperty(_world, property, value))
        {
            throw new InvalidOperationException($"Build choice '{choice.Id}' requires property '{property}'.");
        }
    }

    private void InitializeChoices()
    {
        foreach (var choice in _catalog.Choices)
        {
            var ownerId = _ownerEntityIds[choice.GroupId];
            WriteChoiceValue(choice, ownerId, choice.SelectedProperty, 0);
            if (choice.ActivationMode != ChoiceActivationMode.Passive)
            {
                WriteChoiceValue(choice, ownerId, choice.ActiveUntilProperty, 0);
                WriteChoiceValue(choice, ownerId, choice.CooldownUntilProperty, 0);
            }
        }
    }

    private bool IsOwnerRegistered(string groupId, int ownerEntityId) =>
        _ownerIdsByGroup.TryGetValue(groupId, out var owners) && owners.Contains(ownerEntityId);

    private int ChoiceOwnerForGroup(string groupId, int ownerEntityId) =>
        IsOwnerRegistered(groupId, ownerEntityId) ? ownerEntityId : _ownerEntityIds[groupId];

    private int ChoiceEntityId(BuildChoiceDefinition choice, int ownerEntityId) =>
        _choiceInstanceEntityIds[(choice.Id, ChoiceOwnerForGroup(choice.GroupId, ownerEntityId))];

    private int CloneChoiceEntity(BuildChoiceDefinition choice)
    {
        var template = _world.ById(_choiceEntityIds[choice.Id])!;
        if (template is not DataEntity dataEntity)
        {
            throw new InvalidOperationException($"Build choice '{choice.Id}' must use a data entity to create owner instances.");
        }

        var properties = dataEntity.Properties.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        properties[choice.SelectedProperty] = 0;
        if (choice.ActivationMode != ChoiceActivationMode.Passive)
        {
            properties[choice.ActiveUntilProperty] = 0;
            properties[choice.CooldownUntilProperty] = 0;
        }

        var clone = new DataEntity(template.Tags.Where(tag => tag != ActiveTag(choice)), template.Capabilities,
            [.. properties.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => (pair.Key, pair.Value))]);
        using var transaction = _world.BeginTransaction();
        _world.Add(clone);
        foreach (var (link, targetId) in template.Links.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            clone.SetLink(link, targetId);
        }

        transaction.Commit();
        return clone.Id;
    }

    private int ResolveEntityId(string contentId)
    {
        if (!_entityIds.TryGetValue(contentId, out var id) || _world.ById(id) is null)
        {
            throw new InvalidOperationException($"Build choice definition references missing entity '{contentId}'.");
        }

        return id;
    }

    private static Dictionary<string, ImmutableArray<BuildChoiceDefinition>> BuildExclusionNeighbors(
        ImmutableArray<BuildChoiceDefinition> choices)
    {
        var choiceIndexes = new Dictionary<string, int>(choices.Length, StringComparer.Ordinal);
        for (var index = 0; index < choices.Length; index++)
        {
            choiceIndexes.Add(choices[index].Id, index);
        }

        var neighbors = new HashSet<int>[choices.Length];
        for (var index = 0; index < neighbors.Length; index++)
        {
            neighbors[index] = [];
        }

        for (var index = 0; index < choices.Length; index++)
        {
            foreach (var excludedChoiceId in GetReferences(choices[index].Excludes))
            {
                if (excludedChoiceId == choices[index].Id
                    || !choiceIndexes.TryGetValue(excludedChoiceId, out var excludedIndex))
                {
                    continue;
                }

                neighbors[index].Add(excludedIndex);
                neighbors[excludedIndex].Add(index);
            }
        }

        var result = new Dictionary<string, ImmutableArray<BuildChoiceDefinition>>(choices.Length, StringComparer.Ordinal);
        for (var index = 0; index < choices.Length; index++)
        {
            result.Add(choices[index].Id,
                [.. neighbors[index].Order().Select(neighborIndex => choices[neighborIndex])]);
        }

        return result;
    }

    private static string ActiveTag(BuildChoiceDefinition choice) => $"{choice.ChoiceTag}.active";

    private static int RemainingTicks(double untilTick, double currentTick)
    {
        var remaining = untilTick - currentTick;
        if (!double.IsFinite(remaining) || remaining <= 0)
        {
            return 0;
        }

        return remaining >= int.MaxValue ? int.MaxValue : (int)Math.Ceiling(remaining);
    }
}
