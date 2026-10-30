using System.Collections.Immutable;
using Systemic.Kernel;
using Systemic.Rules;
using Systemic.Ui;

namespace Systemic.Presentation;

public sealed class UiBindingContext
{
    public UiBindingContext(
        World world,
        IReadOnlyDictionary<string, int> entityIds,
        IReadOnlyDictionary<string, double>? selection = null,
        SimulationFact? eventFact = null,
        IReadOnlyList<ISystemicUiBindingProvider>? providers = null,
        int? choiceOwnerEntityId = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(entityIds);
        World = world;
        EntityIds = entityIds.ToImmutableDictionary(StringComparer.Ordinal);
        Selection = (selection ?? ImmutableDictionary<string, double>.Empty)
            .ToImmutableDictionary(StringComparer.Ordinal);
        EventFact = eventFact;
        Providers = providers ?? [];
        ChoiceOwnerEntityId = choiceOwnerEntityId;
        ProviderContext = new SystemicUiBindingContext(World, EntityIds, Selection, EventFact, ChoiceOwnerEntityId);
    }

    public World World { get; }
    public ImmutableDictionary<string, int> EntityIds { get; }
    public ImmutableDictionary<string, double> Selection { get; }
    public SimulationFact? EventFact { get; }
    public IReadOnlyList<ISystemicUiBindingProvider> Providers { get; }
    public int? ChoiceOwnerEntityId { get; }
    internal SystemicUiBindingContext ProviderContext { get; }

    /// <summary>Gets the entity one list item is evaluated against, when this context is an item scope.</summary>
    internal int? ItemEntityId { get; init; }

    internal UiBindingContext WithItem(int entityId) =>
        new(World, EntityIds, Selection, EventFact, Providers, ChoiceOwnerEntityId) { ItemEntityId = entityId };
}

public sealed record UiNodeState(
    string Type,
    string? Text,
    string? Action,
    string? VisualRecipeId,
    bool HasValue,
    double? NumberValue,
    string? TextValue,
    double? Progress,
    string? VisualState,
    ImmutableArray<UiNodeState> Children,
    string? Tooltip = null,
    int? Amount = null,
    bool IsOn = false,
    int? ItemEntityId = null);

/// <summary>Evaluates declarative UI bindings into immutable data for engine adapters to render.</summary>
public static class DeclarativeUiEvaluator
{
    public static UiNodeState Evaluate(UiLayout layout, UiBindingContext context)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(context);
        return EvaluateNode(layout.Root, context, UiBindingPlan.For(layout).Root, new UiEvaluationBudget());
    }

    /// <summary>The most nodes one layout may expand to, so nested lists cannot multiply without bound.</summary>
    private const int MaximumEvaluatedNodes = 16_384;

    private static UiNodeState EvaluateNode(
        UiNode node,
        UiBindingContext context,
        UiBindingNodePlan plan,
        UiEvaluationBudget budget)
    {
        if (++budget.Nodes > MaximumEvaluatedNodes)
        {
            throw new InvalidOperationException(
                $"UI evaluation exceeded {MaximumEvaluatedNodes} nodes; nested lists must not multiply.");
        }

        var descriptor = plan.Binding;
        if (node.Type == "list" && descriptor is { Kind: UiBindingKind.List, ValuePath: { } listTag })
        {
            // The authored template repeats once per live entity carrying the tag, in ascending entity
            // ID order, so a choice list and an upgrade list render through the same node tree.
            var items = ImmutableArray.CreateBuilder<UiNodeState>();
            if (node.Children.Length == 1)
            {
                foreach (var entity in context.World.Entities)
                {
                    if (entity.ToRemove || !context.World.HasTag(entity, listTag))
                    {
                        continue;
                    }

                    items.Add(EvaluateNode(node.Children[0], context.WithItem(entity.Id), plan.Children[0], budget));
                }
            }

            return new UiNodeState(node.Type, node.Text, node.Action, node.VisualRecipeId, false, null, null,
                null, null, items.ToImmutable(), node.Tooltip, node.Amount, ItemEntityId: context.ItemEntityId);
        }

        var value = descriptor is null ? default : Resolve(context, descriptor);
        double? progress = null;
        if (node.Type == "bar" && value.Number is { } number)
        {
            var minimum = node.Minimum ?? 0;
            var maximum = node.Maximum ?? 1;
            progress = Math.Clamp((number - minimum) / (maximum - minimum), 0, 1);
        }

        var children = ImmutableArray.CreateBuilder<UiNodeState>(node.Children.Length);
        for (var index = 0; index < node.Children.Length; index++)
        {
            children.Add(EvaluateNode(node.Children[index], context, plan.Children[index], budget));
        }

        // A toggle reads one binding as a boolean threshold, so the same provider that feeds a
        // choice list also drives the switch without a second binding kind.
        var isOn = node.Type == "toggle" && value.Number is > 0.5;
        // The item ID travels with every node rendered inside a list item, so a renderer can attach a
        // button click to the entity that produced it without repeating the list query.
        return new UiNodeState(node.Type, node.Text, node.Action, node.VisualRecipeId, value.HasValue,
            value.Number, value.Text, progress, ResolveVisualState(node, descriptor, context),
            children.MoveToImmutable(), node.Tooltip, node.Amount, isOn, context.ItemEntityId);
    }

    private static string? ResolveVisualState(UiNode node, UiBindingDescriptor? descriptor, UiBindingContext context)
    {
        if (node.Type != "icon" || descriptor is not { HasChoicePrefix: true } binding)
        {
            return null;
        }

        foreach (var provider in context.Providers)
        {
            if (provider.ResolveVisualState(binding.Binding, context.ProviderContext) is { } state)
            {
                return state;
            }
        }

        return null;
    }

    private static BindingValue Resolve(UiBindingContext context, UiBindingDescriptor binding)
    {
        if (binding.Kind == UiBindingKind.WorldTick)
        {
            return BindingValue.FromNumber(context.World.LogicalTick);
        }

        if (binding.Kind == UiBindingKind.World
            && TryResolveWorld(context, binding.WorldCandidates, out var worldValue))
        {
            return worldValue;
        }

        if (binding.Kind == UiBindingKind.Selection
            && context.Selection.TryGetValue(binding.ValuePath!, out var selectionValue)
            && double.IsFinite(selectionValue))
        {
            return BindingValue.FromNumber(selectionValue);
        }

        if (binding.Kind == UiBindingKind.Item && context.ItemEntityId is { } itemId
            && context.World.ById(itemId) is { } item)
        {
            var itemPath = binding.ValuePath!;
            if (itemPath.StartsWith("tag.", StringComparison.Ordinal))
            {
                return BindingValue.FromNumber(
                    context.World.HasTag(item, itemPath["tag.".Length..]) ? 1 : 0);
            }

            if (context.World.TryGetEffectiveProperty(item, itemPath, out var itemValue)
                && double.IsFinite(itemValue))
            {
                return BindingValue.FromNumber(itemValue);
            }
        }

        if (binding.Kind == UiBindingKind.Event && context.EventFact is { } fact)
        {
            return binding.ValuePath switch
            {
                "type" => BindingValue.FromText(fact.Type),
                "sourceId" => BindingValue.FromNumber(fact.SourceId),
                "targetId" => BindingValue.FromNumber(fact.TargetId),
                "depth" => BindingValue.FromNumber(fact.Depth),
                _ => default,
            };
        }

        foreach (var provider in context.Providers)
        {
            if (provider.TryResolve(binding.Binding, context.ProviderContext, out var provided)
                && provided.HasValue
                && (provided.Number is null || double.IsFinite(provided.Number.Value)))
            {
                return new BindingValue(true, provided.Number, provided.Text);
            }
        }

        return default;
    }

    private static bool TryResolveWorld(
        UiBindingContext context,
        ImmutableArray<UiWorldBindingCandidate> candidates,
        out BindingValue value)
    {
        foreach (var candidate in candidates)
        {
            if (!context.EntityIds.TryGetValue(candidate.EntityPath, out var entityId)
                || context.World.ById(entityId) is not { } entity)
            {
                continue;
            }

            if (candidate.Tag is { } tag)
            {
                if (tag.Length > 0)
                {
                    value = BindingValue.FromNumber(context.World.HasTag(entity, tag) ? 1 : 0);
                    return true;
                }
            }
            else if (context.World.TryGetEffectiveProperty(entity, candidate.MemberPath, out var number)
                && double.IsFinite(number))
            {
                value = BindingValue.FromNumber(number);
                return true;
            }
        }

        value = default;
        return false;
    }

    private sealed class UiEvaluationBudget
    {
        public int Nodes { get; set; }
    }

    private readonly record struct BindingValue(bool HasValue, double? Number, string? Text)
    {
        public static BindingValue FromNumber(double value) => new(true, value, null);
        public static BindingValue FromText(string value) => new(true, null, value);
    }
}
