using System.Collections.Immutable;
using System.Text.Json;
using Systemic.Content;

namespace Systemic.Incremental;

internal static class IncrementalContentCompiler
{
    private const string IncrementalSchemaId = "https://press-the-button.dev/systemic/incremental.schema.json";
    private const string AdvancementsSchemaId = "https://press-the-button.dev/systemic/advancements.schema.json";
    private static readonly string s_incrementalSchema = ReadSchema("incremental.schema.json");
    private static readonly string s_advancementsSchema = ReadSchema("advancements.schema.json");

    public static IncrementalModuleContent Compile(ContentModuleContext context, JsonElement declaration)
    {
        var modulePath = "$.modules.systemic.incremental";
        var incremental = context.ReadFile<IncrementalFile>(declaration, "incremental", modulePath,
            IncrementalSchemaId, s_incrementalSchema, out _);
        var advancements = context.ReadFile<AdvancementsFile>(declaration, "advancements", modulePath,
            AdvancementsSchemaId, s_advancementsSchema, out var advancementPath);
        var catalog = CompileIncremental(incremental, context.Entities);
        var choices = CompileBuildChoices(advancements, context.Entities, catalog);
        foreach (var choice in choices.Choices)
        {
            context.References.Add("choice", choice.Id, advancementPath);
        }

        return new IncrementalModuleContent(catalog, choices);
    }

    private static string ReadSchema(string fileName)
    {
        using var stream = typeof(IncrementalContentCompiler).Assembly.GetManifestResourceStream(
            $"Systemic.Incremental.Schemas.{fileName}")
            ?? throw new InvalidOperationException($"Embedded incremental schema '{fileName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static IncrementalCatalog CompileIncremental(IncrementalFile file, CompiledEntities entities)
    {
        var sourceResources = file.Resources!;
        var sourceProducers = file.Producers!;
        var sourceUpgrades = file.Upgrades!;
        var sourceRoles = file.Roles!;

        var entitiesById = entities.Definitions.ToDictionary(definition => definition.Id, StringComparer.Ordinal);
        var resources = ImmutableArray.CreateBuilder<ResourceDefinition>(sourceResources.Length);
        var resourceIds = new HashSet<string>(StringComparer.Ordinal);
        var resourceEntityIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < sourceResources.Length; index++)
        {
            var resourcePath = $"$.resources[{index}]";
            var resource = sourceResources[index];
            Require(resourceIds.Add(resource.Id!), $"Duplicate resource id '{resource.Id}'.", $"{resourcePath}.id");
            var resourceEntityId = resource.EntityId!;
            if (!entitiesById.TryGetValue(resourceEntityId, out var entity))
            {
                throw new ContentPackException($"Resource '{resource.Id}' references unknown entity '{resourceEntityId}'.",
                    $"{resourcePath}.entityId");
            }

            Require(resourceEntityIds.Add(resourceEntityId), $"Entity '{resourceEntityId}' is used by more than one resource definition.",
                $"{resourcePath}.entityId");
            Require(entity.Tags.Contains("resource", StringComparer.Ordinal),
                $"Resource entity '{resourceEntityId}' must have the 'resource' tag.", $"{resourcePath}.entityId");
            Require(entity.Properties.ContainsKey(resource.BalanceProperty!),
                $"Resource entity '{resourceEntityId}' must declare property '{resource.BalanceProperty}'.",
                $"{resourcePath}.balanceProperty");
            Require(double.IsFinite(resource.InitialValue),
                $"Resource '{resource.Id}' initialValue must be finite.", $"{resourcePath}.initialValue");
            if (resource.Capacity is { } capacity)
            {
                Require(double.IsFinite(capacity) && capacity >= resource.InitialValue,
                    $"Resource '{resource.Id}' capacity must be finite and at least initialValue.", $"{resourcePath}.capacity");
            }

            resources.Add(new ResourceDefinition(resource.Id!, resourceEntityId, resource.BalanceProperty!,
                resource.InitialValue, resource.Capacity));
        }

        var producers = ImmutableArray.CreateBuilder<ProducerDefinition>(sourceProducers.Length);
        var producerIds = new HashSet<string>(StringComparer.Ordinal);
        var producerEntityIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < sourceProducers.Length; index++)
        {
            var producerPath = $"$.producers[{index}]";
            var producer = sourceProducers[index];
            Require(producerIds.Add(producer.Id!), $"Duplicate producer id '{producer.Id}'.", $"{producerPath}.id");
            var producerEntityId = producer.EntityId!;
            if (!entitiesById.TryGetValue(producerEntityId, out var entity))
            {
                throw new ContentPackException($"Producer '{producer.Id}' references unknown entity '{producerEntityId}'.",
                    $"{producerPath}.entityId");
            }

            Require(producerEntityIds.Add(producerEntityId), $"Entity '{producerEntityId}' is used by more than one producer definition.",
                $"{producerPath}.entityId");
            Require(resourceIds.Contains(producer.ResourceId!),
                $"Producer '{producer.Id}' references unknown resource '{producer.ResourceId}'.", $"{producerPath}.resourceId");
            Require(entity.Properties.ContainsKey(producer.RateProperty!),
                $"Producer entity '{producerEntityId}' must declare property '{producer.RateProperty}'.",
                $"{producerPath}.rateProperty");
            Require(double.IsFinite(producer.BaseRate),
                $"Producer '{producer.Id}' baseRate must be finite.", $"{producerPath}.baseRate");
            var eventType = producer.EventType ?? "incremental.production";
            producers.Add(new ProducerDefinition(producer.Id!, producerEntityId, producer.ResourceId!,
                producer.RateProperty!, producer.BaseRate, producer.IntervalTicks, eventType));
        }

        var upgrades = ImmutableArray.CreateBuilder<UpgradeDefinition>(sourceUpgrades.Length);
        var upgradeIds = new HashSet<string>(StringComparer.Ordinal);
        var upgradeEntityIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < sourceUpgrades.Length; index++)
        {
            var upgradePath = $"$.upgrades[{index}]";
            var upgrade = sourceUpgrades[index];
            Require(upgradeIds.Add(upgrade.Id!), $"Duplicate upgrade id '{upgrade.Id}'.", $"{upgradePath}.id");
            var upgradeEntityId = upgrade.EntityId!;
            if (!entitiesById.TryGetValue(upgradeEntityId, out var entity))
            {
                throw new ContentPackException($"Upgrade '{upgrade.Id}' references unknown entity '{upgradeEntityId}'.",
                    $"{upgradePath}.entityId");
            }

            Require(upgradeEntityIds.Add(upgradeEntityId), $"Entity '{upgradeEntityId}' is used by more than one upgrade definition.",
                $"{upgradePath}.entityId");
            Require(entity.Properties.ContainsKey(upgrade.PurchaseCountProperty!),
                $"Upgrade entity '{upgradeEntityId}' must declare property '{upgrade.PurchaseCountProperty}'.",
                $"{upgradePath}.purchaseCountProperty");
            var costs = upgrade.Costs!;
            var costIds = new HashSet<string>(StringComparer.Ordinal);
            var compiledCosts = ImmutableArray.CreateBuilder<ResourceCost>(costs.Length);
            for (var costIndex = 0; costIndex < costs.Length; costIndex++)
            {
                var costPath = $"{upgradePath}.costs[{costIndex}]";
                var cost = costs[costIndex];
                Require(resourceIds.Contains(cost.ResourceId!),
                    $"Upgrade '{upgrade.Id}' references unknown resource '{cost.ResourceId}'.", $"{costPath}.resourceId");
                Require(costIds.Add(cost.ResourceId!), $"Upgrade '{upgrade.Id}' has duplicate cost for resource '{cost.ResourceId}'.",
                    $"{costPath}.resourceId");
                Require(double.IsFinite(cost.Amount),
                    $"Upgrade '{upgrade.Id}' cost for '{cost.ResourceId}' must be finite.", $"{costPath}.amount");
                compiledCosts.Add(new ResourceCost(cost.ResourceId!, cost.Amount));
            }

            var eventType = upgrade.PurchasedEvent ?? "incremental.upgrade.purchased";
            upgrades.Add(new UpgradeDefinition(upgrade.Id!, upgradeEntityId, compiledCosts.MoveToImmutable(),
                upgrade.MaxPurchases, upgrade.PurchaseCountProperty!, eventType));
        }

        var roles = ImmutableArray.CreateBuilder<ActorRoleDefinition>(sourceRoles.Length);
        var roleIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < sourceRoles.Length; index++)
        {
            var rolePath = $"$.roles[{index}]";
            var role = sourceRoles[index];
            Require(roleIds.Add(role.Id!), $"Duplicate actor role id '{role.Id}'.", $"{rolePath}.id");
            roles.Add(new ActorRoleDefinition(role.Id!, [.. role.Tags ?? []], [.. role.Capabilities ?? []]));
        }

        return new IncrementalCatalog(resources.MoveToImmutable(), producers.MoveToImmutable(),
            upgrades.MoveToImmutable(), roles.MoveToImmutable());
    }


    private static BuildChoiceCatalog CompileBuildChoices(
        AdvancementsFile file,
        CompiledEntities entities,
        IncrementalCatalog? incremental)
    {
        var sourceGroups = file.Groups!;
        var sourceChoices = file.Choices!;

        var entitiesById = entities.Definitions.ToDictionary(definition => definition.Id, StringComparer.Ordinal);
        var groups = ImmutableArray.CreateBuilder<BuildChoiceGroupDefinition>(sourceGroups.Length);
        var groupsById = new Dictionary<string, BuildChoiceGroupDefinition>(sourceGroups.Length, StringComparer.Ordinal);
        var groupPathsById = new Dictionary<string, string>(sourceGroups.Length, StringComparer.Ordinal);
        for (var index = 0; index < sourceGroups.Length; index++)
        {
            var groupPath = $"$.groups[{index}]";
            var group = sourceGroups[index];
            Require(entitiesById.ContainsKey(group.OwnerEntityId!),
                $"Build choice group '{group.Id}' references unknown owner entity '{group.OwnerEntityId}'.",
                $"{groupPath}.ownerEntityId");
            var progressEntityId = group.ProgressEntityId!;
            if (!entitiesById.TryGetValue(progressEntityId, out var progressEntity))
            {
                throw new ContentPackException($"Build choice group '{group.Id}' references unknown progress entity '{progressEntityId}'.",
                    $"{groupPath}.progressEntityId");
            }
            Require(!string.IsNullOrWhiteSpace(group.ProgressProperty)
                && progressEntity!.Properties.ContainsKey(group.ProgressProperty),
                $"Build choice group '{group.Id}' progress entity must declare property '{group.ProgressProperty}'.",
                $"{groupPath}.progressProperty");
            Require(double.IsFinite(group.UnlockAt),
                $"Build choice group '{group.Id}' unlockAt must be finite.", $"{groupPath}.unlockAt");

            var compiled = new BuildChoiceGroupDefinition(group.Id!, group.OwnerEntityId!, group.ProgressEntityId!,
                group.ProgressProperty!, group.UnlockAt, group.SelectionLimit, group.FreeChoiceId);
            Require(groupsById.TryAdd(compiled.Id, compiled), $"Duplicate build choice group id '{compiled.Id}'.", $"{groupPath}.id");
            groupPathsById.Add(compiled.Id, groupPath);
            groups.Add(compiled);
        }

        var choices = ImmutableArray.CreateBuilder<BuildChoiceDefinition>(sourceChoices.Length);
        var choicesById = new Dictionary<string, BuildChoiceDefinition>(sourceChoices.Length, StringComparer.Ordinal);
        var choicePathsById = new Dictionary<string, string>(sourceChoices.Length, StringComparer.Ordinal);
        var choiceEntities = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < sourceChoices.Length; index++)
        {
            var choicePath = $"$.choices[{index}]";
            var choice = sourceChoices[index];
            Require(groupsById.ContainsKey(choice.GroupId!),
                $"Build choice '{choice.Id}' references unknown group '{choice.GroupId}'.", $"{choicePath}.groupId");
            var choiceEntityId = choice.EntityId!;
            if (!entitiesById.TryGetValue(choiceEntityId, out var choiceEntity))
            {
                throw new ContentPackException($"Build choice '{choice.Id}' references unknown entity '{choiceEntityId}'.",
                    $"{choicePath}.entityId");
            }
            Require(choiceEntities.Add(choiceEntityId), $"Entity '{choiceEntityId}' is used by more than one build choice.",
                $"{choicePath}.entityId");
            var mode = choice.ActivationMode switch
            {
                "passive" => ChoiceActivationMode.Passive,
                "triggered" => ChoiceActivationMode.Triggered,
                "timed" => ChoiceActivationMode.Timed,
                _ => throw new ContentPackException($"Build choice '{choice.Id}' has unknown activationMode '{choice.ActivationMode}'.",
                    $"{choicePath}.activationMode"),
            };
            var choiceTag = choice.ChoiceTag ?? choice.Id!;
            var selectedProperty = choice.SelectedProperty ?? "selected";
            var activeUntilProperty = choice.ActiveUntilProperty ?? "activeUntilTick";
            var cooldownUntilProperty = choice.CooldownUntilProperty ?? "cooldownUntilTick";
            Require(!string.IsNullOrWhiteSpace(selectedProperty)
                && choiceEntity!.Properties.ContainsKey(selectedProperty),
                $"Build choice entity '{choice.EntityId}' must declare property '{selectedProperty}'.",
                $"{choicePath}.selectedProperty");
            if (mode != ChoiceActivationMode.Passive)
            {
                Require(!string.IsNullOrWhiteSpace(activeUntilProperty)
                    && choiceEntity!.Properties.ContainsKey(activeUntilProperty),
                    $"Build choice entity '{choice.EntityId}' must declare property '{activeUntilProperty}'.",
                    $"{choicePath}.activeUntilProperty");
                Require(!string.IsNullOrWhiteSpace(cooldownUntilProperty)
                    && choiceEntity!.Properties.ContainsKey(cooldownUntilProperty),
                    $"Build choice entity '{choice.EntityId}' must declare property '{cooldownUntilProperty}'.",
                    $"{choicePath}.cooldownUntilProperty");
            }

            if (choice.CostResourceId is { } resourceId)
            {
                Require(incremental is not null && incremental.Resources.Any(resource => resource.Id == resourceId),
                    $"Build choice '{choice.Id}' references unknown resource '{resourceId}'.", $"{choicePath}.costResourceId");
                Require(double.IsFinite(choice.CostAmount),
                    $"Build choice '{choice.Id}' costAmount must be finite.", $"{choicePath}.costAmount");
            }
            else
            {
                Require(choice.CostAmount == 0, $"Build choice '{choice.Id}' costAmount requires costResourceId.",
                    $"{choicePath}.costAmount");
            }

            var requires = CompileChoiceReferences(choice.Requires, choice.Id!, "requires", choicePath);
            var requiresAny = CompileChoiceReferences(choice.RequiresAny, choice.Id!, "requiresAny", choicePath);
            var excludes = CompileChoiceReferences(choice.Excludes, choice.Id!, "excludes", choicePath);

            var compiled = new BuildChoiceDefinition(choice.Id!, choice.GroupId!, choice.EntityId!, mode,
                choiceTag, selectedProperty, activeUntilProperty, cooldownUntilProperty, choice.CostResourceId,
                choice.CostAmount, choice.ActiveTicks, choice.CooldownTicks, choice.TriggerEvent, choice.ActivationEvent,
                choice.IsFree, requires, requiresAny, excludes);
            Require(choicesById.TryAdd(compiled.Id, compiled), $"Duplicate build choice id '{compiled.Id}'.", $"{choicePath}.id");
            choicePathsById.Add(compiled.Id, choicePath);
            choices.Add(compiled);
        }

        ValidateChoiceReferences(choices, choicePathsById, groupsById);

        for (var index = 0; index < groups.Count; index++)
        {
            var group = groups[index];
            var groupPath = groupPathsById[group.Id];
            var groupChoices = choices.Where(choice => choice.GroupId == group.Id).ToArray();
            var freeChoices = groupChoices.Where(choice => choice.IsFree).ToArray();
            Require(group.FreeChoiceId is null
                ? freeChoices.Length == 0
                : freeChoices.Length == 1 && freeChoices[0].Id == group.FreeChoiceId,
                $"Build choice group '{group.Id}' freeChoiceId must name its single free choice.", $"{groupPath}.freeChoiceId");
            if (group.FreeChoiceId is { } freeId)
            {
                if (!choicesById.TryGetValue(freeId, out var freeChoice))
                {
                    throw new ContentPackException($"Build choice group '{group.Id}' references unknown freeChoiceId '{freeId}'.",
                        $"{groupPath}.freeChoiceId");
                }

                Require(freeChoice.GroupId == group.Id && freeChoice.ActivationMode == ChoiceActivationMode.Passive
                    && freeChoice.CostResourceId is null,
                    $"Build choice '{freeId}' must be a free, passive choice in group '{group.Id}'.",
                    $"{choicePathsById[freeId]}.isFree");
            }
        }

        return new BuildChoiceCatalog(groups.MoveToImmutable(), choices.MoveToImmutable());
    }


    private static ImmutableArray<string> CompileChoiceReferences(
        string[]? references,
        string choiceId,
        string field,
        string choicePath)
    {
        if (references is null)
        {
            return [];
        }

        for (var index = 0; index < references.Length; index++)
        {
            var reference = references[index];
            Require(reference != choiceId, $"Build choice '{choiceId}' cannot reference itself in {field}.",
                $"{choicePath}.{field}[{index}]");
        }

        return [.. references.Order(StringComparer.Ordinal)];
    }


    private static void ValidateChoiceReferences(
        ImmutableArray<BuildChoiceDefinition>.Builder choices,
        Dictionary<string, string> paths,
        Dictionary<string, BuildChoiceGroupDefinition> groups)
    {
        var choicesById = choices.ToDictionary(choice => choice.Id, StringComparer.Ordinal);
        foreach (var choice in choices)
        {
            ValidateReferences(choice, choice.Requires, "requires");
            ValidateReferences(choice, choice.RequiresAny, "requiresAny");
            ValidateReferences(choice, choice.Excludes, "excludes");
            var excluded = choice.Excludes.ToHashSet(StringComparer.Ordinal);
            Require(!choice.Requires.Any(excluded.Contains),
                $"Build choice '{choice.Id}' cannot require a choice it excludes.", $"{paths[choice.Id]}.requires");
            if (!choice.RequiresAny.IsDefaultOrEmpty)
            {
                Require(choice.RequiresAny.Any(reference => !excluded.Contains(reference)),
                    $"Build choice '{choice.Id}' excludes every alternative prerequisite.",
                    $"{paths[choice.Id]}.requiresAny");
            }

            var requiredChoices = choice.Requires.Select(reference => choicesById[reference]).ToArray();
            Require(!HasMutualExclusion(requiredChoices),
                $"Build choice '{choice.Id}' requires choices that exclude one another.", $"{paths[choice.Id]}.requires");
            if (!choice.RequiresAny.IsDefaultOrEmpty)
            {
                Require(choice.RequiresAny.Select(reference => choicesById[reference]).Any(alternative =>
                        !excluded.Contains(alternative.Id)
                        && !requiredChoices.Any(required => Excludes(required, alternative))),
                    $"Build choice '{choice.Id}' has no alternative prerequisite compatible with its requirements.",
                    $"{paths[choice.Id]}.requiresAny");
            }

            void ValidateReferences(BuildChoiceDefinition owner, ImmutableArray<string> references, string field)
            {
                foreach (var reference in references)
                {
                    Require(choicesById.ContainsKey(reference),
                        $"Build choice '{owner.Id}' references unknown choice '{reference}'.", $"{paths[owner.Id]}.{field}");
                }
            }
        }

        const int MaximumClosuresPerChoice = 256;
        const int MaximumClosureSearchSteps = 1_000_000;
        var selectionClosures = choices.ToDictionary(
            choice => choice.Id,
            _ => new List<HashSet<string>>(),
            StringComparer.Ordinal);
        var dependents = choices.ToDictionary(
            choice => choice.Id,
            _ => new List<BuildChoiceDefinition>(),
            StringComparer.Ordinal);
        foreach (var choice in choices)
        {
            foreach (var prerequisiteId in choice.Requires.Concat(choice.RequiresAny).Distinct(StringComparer.Ordinal))
            {
                dependents[prerequisiteId].Add(choice);
            }
        }

        var pending = new Queue<BuildChoiceDefinition>(choices);
        var queued = choices.Select(choice => choice.Id).ToHashSet(StringComparer.Ordinal);
        var searchSteps = 0;
        while (pending.TryDequeue(out var choice))
        {
            queued.Remove(choice.Id);
            if (groups[choice.GroupId].SelectionLimit == 0
                || choice.Requires.Any(id => selectionClosures[id].Count == 0)
                || !choice.RequiresAny.IsDefaultOrEmpty
                    && choice.RequiresAny.All(id => selectionClosures[id].Count == 0))
            {
                continue;
            }

            EnumerateRequiredClosures(0, []);

            void EnumerateRequiredClosures(int requiredIndex, HashSet<string> selected)
            {
                if (requiredIndex == choice.Requires.Length)
                {
                    EnumerateAlternativeClosures(selected);
                    return;
                }

                foreach (var prerequisiteClosure in selectionClosures[choice.Requires[requiredIndex]])
                {
                    CountClosureSearchStep();
                    var merged = new HashSet<string>(selected, StringComparer.Ordinal);
                    merged.UnionWith(prerequisiteClosure);
                    if (IsCompatibleSelection(merged))
                    {
                        EnumerateRequiredClosures(requiredIndex + 1, merged);
                    }
                }
            }

            void EnumerateAlternativeClosures(HashSet<string> selected)
            {
                if (choice.RequiresAny.IsDefaultOrEmpty)
                {
                    TryAddClosure(selected);
                    return;
                }

                foreach (var alternativeId in choice.RequiresAny)
                {
                    foreach (var alternativeClosure in selectionClosures[alternativeId])
                    {
                        CountClosureSearchStep();
                        var merged = new HashSet<string>(selected, StringComparer.Ordinal);
                        merged.UnionWith(alternativeClosure);
                        TryAddClosure(merged);
                    }
                }
            }

            void TryAddClosure(HashSet<string> selected)
            {
                CountClosureSearchStep();
                var candidate = new HashSet<string>(selected, StringComparer.Ordinal) { choice.Id };
                if (!IsCompatibleSelection(candidate))
                {
                    return;
                }

                var closures = selectionClosures[choice.Id];
                if (closures.Any(existing => existing.IsSubsetOf(candidate)))
                {
                    return;
                }

                closures.RemoveAll(existing => candidate.IsSubsetOf(existing));
                if (closures.Count >= MaximumClosuresPerChoice)
                {
                    throw new ContentPackException(
                        $"Build choice '{choice.Id}' has too many compatible prerequisite alternatives to validate safely.",
                        $"{paths[choice.Id]}.requires");
                }

                closures.Add(candidate);
                foreach (var dependent in dependents[choice.Id])
                {
                    if (queued.Add(dependent.Id))
                    {
                        pending.Enqueue(dependent);
                    }
                }
            }

            void CountClosureSearchStep()
            {
                searchSteps++;
                if (searchSteps > MaximumClosureSearchSteps)
                {
                    throw new ContentPackException(
                        "Build choice prerequisite validation exceeded its bounded search budget.",
                        $"{paths[choice.Id]}.requires");
                }
            }
        }

        if (selectionClosures.Any(pair => pair.Value.Count == 0))
        {
            var choiceId = choices.Select(choice => choice.Id)
                .Where(id => selectionClosures[id].Count == 0).Order(StringComparer.Ordinal).First();
            throw new ContentPackException(
                $"Build choice '{choiceId}' is in a prerequisite cycle or its prerequisites exceed a group selection limit or exclusion.",
                $"{paths[choiceId]}.requires");
        }

        bool IsCompatibleSelection(HashSet<string> selectedIds)
        {
            var selectedPerGroup = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var selectedId in selectedIds)
            {
                var selected = choicesById[selectedId];
                foreach (var excludedId in selected.Excludes)
                {
                    if (selectedIds.Contains(excludedId))
                    {
                        return false;
                    }
                }

                if (!selected.IsFree)
                {
                    var selectedCount = selectedPerGroup.GetValueOrDefault(selected.GroupId) + 1;
                    if (selectedCount > groups[selected.GroupId].SelectionLimit)
                    {
                        return false;
                    }

                    selectedPerGroup[selected.GroupId] = selectedCount;
                }
            }

            return true;
        }

        static bool HasMutualExclusion(IReadOnlyList<BuildChoiceDefinition> requiredChoices)
        {
            for (var first = 0; first < requiredChoices.Count; first++)
            {
                for (var second = first + 1; second < requiredChoices.Count; second++)
                {
                    if (Excludes(requiredChoices[first], requiredChoices[second]))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        static bool Excludes(BuildChoiceDefinition first, BuildChoiceDefinition second) =>
            first.Excludes.Contains(second.Id, StringComparer.Ordinal)
            || second.Excludes.Contains(first.Id, StringComparer.Ordinal);
    }


    private static void Require(bool condition, string message, string? fieldPath = null)
    {
        if (!condition)
        {
            if (fieldPath is not null)
            {
                throw new ContentPackException(message, fieldPath);
            }

            throw new ContentPackException(message);
        }
    }

    private sealed record IncrementalFile(
        int SchemaVersion,
        ResourceFile[]? Resources = null,
        ProducerFile[]? Producers = null,
        UpgradeFile[]? Upgrades = null,
        ActorRoleFile[]? Roles = null);

    private sealed record ResourceFile(
        string Id,
        string? EntityId,
        double InitialValue = 0,
        double? Capacity = null,
        string? BalanceProperty = "balance");

    private sealed record ProducerFile(
        string Id,
        string? EntityId,
        string? ResourceId,
        double BaseRate,
        int IntervalTicks = 1,
        string? RateProperty = "rate",
        string? EventType = null);

    private sealed record UpgradeFile(
        string Id,
        string? EntityId,
        UpgradeCostFile[]? Costs = null,
        int MaxPurchases = 1,
        string? PurchaseCountProperty = "purchases",
        string? PurchasedEvent = null);

    private sealed record UpgradeCostFile(string? ResourceId, double Amount);

    private sealed record ActorRoleFile(string Id, string[]? Tags = null, string[]? Capabilities = null);

    private sealed record AdvancementsFile(
        int SchemaVersion,
        BuildChoiceGroupFile[]? Groups = null,
        BuildChoiceFile[]? Choices = null);

    private sealed record BuildChoiceGroupFile(
        string Id,
        string? OwnerEntityId,
        string? ProgressEntityId,
        string? ProgressProperty,
        double UnlockAt,
        int SelectionLimit,
        string? FreeChoiceId = null);

    private sealed record BuildChoiceFile(
        string Id,
        string? GroupId,
        string? EntityId,
        string ActivationMode,
        string? ChoiceTag = null,
        string? SelectedProperty = null,
        string? ActiveUntilProperty = null,
        string? CooldownUntilProperty = null,
        string? CostResourceId = null,
        double CostAmount = 0,
        int ActiveTicks = 1,
        int CooldownTicks = 0,
        string? TriggerEvent = null,
        string? ActivationEvent = null,
        bool IsFree = false,
        string[]? Requires = null,
        string[]? RequiresAny = null,
        string[]? Excludes = null);

}
