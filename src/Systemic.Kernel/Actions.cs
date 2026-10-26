using System.Collections.Immutable;

namespace Systemic.Kernel;

public interface IPayment
{
    IReadOnlyList<ResourcePayment>? TryPay(World world);
}

public sealed record ResourcePayment(int PayerId, double Amount) : IPayment
{
    public IReadOnlyList<ResourcePayment>? TryPay(World world) =>
        double.IsFinite(Amount)
        && world.ById(PayerId) is ResourceEntity resource
        && world.Edit(resource).TrySpend(world, Amount)
            ? [this]
            : null;
}

public sealed record TaggedResourcePayment(string Tag, string Capability, double Amount) : IPayment
{
    private const double PaymentTolerance = 1e-9;

    public IReadOnlyList<ResourcePayment>? TryPay(World world)
    {
        if (!double.IsFinite(Amount) || Amount < 0)
        {
            return null;
        }

        if (Amount == 0)
        {
            return [];
        }

        var candidates = world.AllTaggedCandidates(Tag, Capability, EntityCapabilities.BeSpent);
        var available = 0.0;
        foreach (var candidateId in candidates.Span)
        {
            if (world.ById(candidateId) is ResourceEntity resource
                && world.HasTag(resource, Tag)
                && world.HasCapability(resource, Capability)
                && world.HasCapability(resource, EntityCapabilities.BeSpent))
            {
                available += resource.Amount;
            }
        }

        if (available + PaymentTolerance < Amount)
        {
            return null;
        }

        var remaining = Amount;
        var plan = new List<ResourcePayment>();
        foreach (var candidateId in candidates.Span)
        {
            if (world.ById(candidateId) is not ResourceEntity resource
                || !world.HasTag(resource, Tag)
                || !world.HasCapability(resource, Capability)
                || !world.HasCapability(resource, EntityCapabilities.BeSpent))
            {
                continue;
            }

            var amount = Math.Min(resource.Amount, remaining);
            if (amount <= 0)
            {
                continue;
            }

            plan.Add(new ResourcePayment(resource.Id, amount));
            remaining -= amount;
            if (remaining <= PaymentTolerance)
            {
                remaining = 0;
                break;
            }
        }

        if (remaining > 0)
        {
            return null;
        }

        foreach (var payment in plan)
        {
            if (world.ById(payment.PayerId) is not ResourceEntity resource
                || !world.HasTag(resource, Tag)
                || !world.HasCapability(resource, Capability)
                || !world.HasCapability(resource, EntityCapabilities.BeSpent)
                || !world.Edit(resource).TrySpend(world, payment.Amount))
            {
                return null;
            }
        }

        return plan;
    }
}

public readonly record struct OutcomeValue(string Channel, double Amount);

// WHY: outcomes are hot-path data; four inline channels keep production allocation-free while the overflow
// array still supports arbitrary authoring-time channel sets.
public readonly record struct OutcomeSet
{
    private const int InlineCapacity = 4;

    private readonly OutcomeValue _first;
    private readonly OutcomeValue _second;
    private readonly OutcomeValue _third;
    private readonly OutcomeValue _fourth;
    private readonly ImmutableArray<OutcomeValue> _overflow;

    public OutcomeSet(OutcomeValue first) : this()
    {
        _first = first;
        Count = 1;
    }

    public OutcomeSet(OutcomeValue first, OutcomeValue second) : this(first)
    {
        _second = second;
        Count = 2;
    }

    public OutcomeSet(OutcomeValue first, OutcomeValue second, OutcomeValue third)
        : this(first, second)
    {
        _third = third;
        Count = 3;
    }

    public OutcomeSet(
        OutcomeValue first, OutcomeValue second, OutcomeValue third, OutcomeValue fourth)
        : this(first, second, third)
    {
        _fourth = fourth;
        Count = 4;
    }

    public OutcomeSet(ImmutableArray<OutcomeValue> values)
    {
        this = default;

        if (values.IsDefaultOrEmpty)
        {
            return;
        }

        Count = values.Length;
        _first = values[0];
        if (values.Length > 1)
        {
            _second = values[1];
        }

        if (values.Length > 2)
        {
            _third = values[2];
        }

        if (values.Length > 3)
        {
            _fourth = values[3];
        }

        if (values.Length > InlineCapacity)
        {
            _overflow = values[InlineCapacity..];
        }
    }

    public int Count { get; }

    public double Value(string channel)
    {
        if (Count > 0 && _first.Channel == channel)
        {
            return _first.Amount;
        }

        if (Count > 1 && _second.Channel == channel)
        {
            return _second.Amount;
        }

        if (Count > 2 && _third.Channel == channel)
        {
            return _third.Amount;
        }

        if (Count > 3 && _fourth.Channel == channel)
        {
            return _fourth.Amount;
        }

        if (!_overflow.IsDefault)
        {
            foreach (var value in _overflow)
            {
                if (value.Channel == channel)
                {
                    return value.Amount;
                }
            }
        }

        return 0.0;
    }

    public bool AllFinite()
    {
        if (Count > 0 && !double.IsFinite(_first.Amount))
        {
            return false;
        }

        if (Count > 1 && !double.IsFinite(_second.Amount))
        {
            return false;
        }

        if (Count > 2 && !double.IsFinite(_third.Amount))
        {
            return false;
        }

        if (Count > 3 && !double.IsFinite(_fourth.Amount))
        {
            return false;
        }

        if (!_overflow.IsDefault)
        {
            foreach (var value in _overflow)
            {
                if (!double.IsFinite(value.Amount))
                {
                    return false;
                }
            }
        }

        return true;
    }
}

public readonly record struct ActionOutcome(
    string Kind,
    OutcomeSet Values,
    int Count = 1,
    double PopulationScale = 1)
{
    public ActionOutcome(
        string kind,
        ImmutableArray<OutcomeValue> values,
        int count = 1,
        double populationScale = 1)
        : this(kind, new OutcomeSet(values), count, populationScale)
    {
    }

    public static ActionOutcome Of(string kind, (string Channel, double Amount) first,
        double populationScale = 1) =>
        new(kind, new OutcomeSet(new OutcomeValue(first.Channel, first.Amount)),
            PopulationScale: populationScale);

    public static ActionOutcome Of(string kind, (string Channel, double Amount) first,
        (string Channel, double Amount) second, double populationScale = 1) =>
        new(kind, new OutcomeSet(
                new OutcomeValue(first.Channel, first.Amount),
                new OutcomeValue(second.Channel, second.Amount)),
            PopulationScale: populationScale);

    public static ActionOutcome Of(string kind, (string Channel, double Amount) first,
        (string Channel, double Amount) second, (string Channel, double Amount) third,
        double populationScale = 1) =>
        new(kind, new OutcomeSet(
                new OutcomeValue(first.Channel, first.Amount),
                new OutcomeValue(second.Channel, second.Amount),
                new OutcomeValue(third.Channel, third.Amount)),
            PopulationScale: populationScale);

    public static ActionOutcome Of(string kind, (string Channel, double Amount) first,
        (string Channel, double Amount) second, (string Channel, double Amount) third,
        (string Channel, double Amount) fourth, double populationScale = 1) =>
        new(kind, new OutcomeSet(
                new OutcomeValue(first.Channel, first.Amount),
                new OutcomeValue(second.Channel, second.Amount),
                new OutcomeValue(third.Channel, third.Amount),
                new OutcomeValue(fourth.Channel, fourth.Amount)),
            PopulationScale: populationScale);

    public double Value(string channel) => Values.Value(channel);
}

public sealed record CommittedAction(
    long Serial,
    long CauseId,
    string Action,
    int SourceId,
    int TargetId,
    ActionOutcome Outcome,
    ImmutableArray<ResourcePayment> PaidResources)
{
    public ImmutableArray<CommittedEffect> Effects { get; set; } = [];

    public int Repetitions { get; set; } = 1;

    public long OriginActionId { get; init; }

    // WHY: one fact may aggregate a batch; content owns the opaque detail without teaching the kernel its shape.
    public object? Detail { get; set; }
}

public readonly record struct ReactionOutcome(string Reaction, long CauseId, bool Applied, string Reason);

public readonly record struct CommittedEffect(string Kind, int TargetId, double Amount);

public sealed record CommittedReaction(
    string Reaction,
    long CauseId,
    int SourceId,
    int TargetId,
    ImmutableArray<ResourcePayment> PaidResources,
    ImmutableArray<CommittedEffect> Effects);

public readonly record struct ActionIntent(ActionDefinition Definition, int SourceId, int TargetId, long CauseId = 0,
    int Count = 1);

public sealed record ActionDefinition(
    string Id,
    IActionOutcomeResolver Resolver,
    ImmutableArray<IActionEffect> Consequences,
    ImmutableArray<IPayment> Costs = default,
    ImmutableArray<ActionReaction> Reactions = default);

public sealed record ActionReaction(
    string Id,
    ImmutableArray<IActionEffect> Effects,
    ImmutableArray<IPayment> Costs = default);

public interface IActionOutcomeResolver
{
    bool CanResolve(World world, ActionIntent intent);
    ActionOutcome Resolve(World world, ActionIntent intent);
}

public interface IActionEffect
{
    bool CanApply(World world, CommittedAction fact);
    CommittedEffect Apply(World world, CommittedAction fact);
}

public readonly record struct ActionResolution(
    World World,
    bool Accepted,
    CommittedAction? Fact,
    ImmutableArray<ReactionOutcome> Reactions,
    int EffectsApplied)
{
    public ImmutableArray<CommittedReaction> CommittedReactions { get; init; }

    public int ChargeableEffects { get; init; }
}

public static class ActionPipeline
{
    public static ActionResolution Resolve(
        World world,
        ActionIntent intent,
        long serial = 1,
        int effectBudget = EngineLimits.MaxEffectsPerCommand)
    {
        var result = ResolveInPlace(world.Clone(), intent, serial, effectBudget);

        return result.Accepted ? result : result with { World = world };
    }

    internal static ActionResolution ResolveInPlace(
        World world,
        ActionIntent intent,
        long serial = 1,
        int effectBudget = EngineLimits.MaxEffectsPerCommand,
        bool ordinary = false,
        ActionOutcome? resolved = null,
        int repetitions = 1,
        object? detail = null)
    {
        using var scope = world.BeginScope();
        var definition = intent.Definition;
        // WHY: an aggregated outcome is already validated per activation, and its total must be applied once.
        if (definition.Consequences.IsDefault
            || (!ordinary && definition.Consequences.Length > effectBudget)
            || (resolved is null && !definition.Resolver.CanResolve(world, intent)))
        {
            scope.Rollback();

            return new(world, false, null, [], 0);
        }

        var paid = Pay(world, definition.Costs);
        if (paid is null)
        {
            scope.Rollback();

            return new(world, false, null, [], 0);
        }

        var outcome = resolved ?? definition.Resolver.Resolve(world, intent);
        var appliedCount = resolved is null ? outcome.Count : 1;
        var repetitionsApplied = resolved is null ? outcome.Count : repetitions;
        if (!outcome.Values.AllFinite()
            || !double.IsFinite(outcome.PopulationScale) || outcome.PopulationScale <= 0
            || appliedCount <= 0 || repetitionsApplied <= 0
            || (!ordinary && (long)definition.Consequences.Length * appliedCount > effectBudget))
        {
            scope.Rollback();

            return new(world, false, null, [], 0);
        }

        var fact = new CommittedAction(serial, intent.CauseId, definition.Id,
            intent.SourceId, intent.TargetId, outcome, paid.Value)
        {
            Repetitions = repetitionsApplied,
            Detail = detail,
        };
        var payload = ImmutableArray.CreateBuilder<CommittedEffect>(definition.Consequences.Length);
        var baseResult = Apply(world, fact, definition.Consequences, payload, appliedCount);
        if (baseResult.World is null)
        {
            scope.Rollback();

            return new(world, false, null, [], 0);
        }

        fact.Effects = payload.ToImmutable();
        var effects = baseResult.Applied;
        var charged = ordinary ? 0 : baseResult.Applied;
        var reactionDefinitions = OrEmpty(definition.Reactions);
        var reactions = reactionDefinitions.Length == 0
            ? null : ImmutableArray.CreateBuilder<ReactionOutcome>();
        var committedReactions = reactionDefinitions.Length == 0
            ? null : ImmutableArray.CreateBuilder<CommittedReaction>();
        foreach (var reaction in reactionDefinitions)
        {
            if (reaction.Effects.IsDefault || reaction.Effects.Length + 1 > effectBudget - charged)
            {
                reactions!.Add(new(reaction.Id, fact.Serial, false, "budget"));

                break;
            }

            charged++;
            effects++;
            using var reactionScope = world.BeginScope();
            var reactionPayment = Pay(world, reaction.Costs);
            if (reactionPayment is null)
            {
                reactionScope.Rollback();
                reactions!.Add(new(reaction.Id, fact.Serial, false, "payment"));

                continue;
            }

            var reactionPayload = ImmutableArray.CreateBuilder<CommittedEffect>(reaction.Effects.Length);
            var applied = Apply(world, fact, reaction.Effects, reactionPayload);
            effects += applied.Applied;
            charged += applied.Applied;
            if (applied.World is null)
            {
                reactionScope.Rollback();
                reactions!.Add(new(reaction.Id, fact.Serial, false, "effect"));

                continue;
            }

            reactions!.Add(new(reaction.Id, fact.Serial, true, "applied"));
            committedReactions!.Add(new(reaction.Id, fact.Serial, fact.SourceId, fact.TargetId,
                reactionPayment.Value, reactionPayload.ToImmutable()));
            reactionScope.Commit();
        }

        scope.Commit();

        return new(world, true, fact, reactions?.ToImmutable() ?? [], effects)
        {
            CommittedReactions = committedReactions?.ToImmutable() ?? [],
            ChargeableEffects = charged,
        };
    }

    private static ImmutableArray<ResourcePayment>? Pay(
        World world,
        ImmutableArray<IPayment> costs)
    {
        var required = OrEmpty(costs);
        if (required.Length == 0)
        {
            return [];
        }

        using var scope = world.BeginScope();
        var paid = ImmutableArray.CreateBuilder<ResourcePayment>();
        foreach (var cost in required)
        {
            var records = cost.TryPay(world);
            if (records is null)
            {
                scope.Rollback();

                return null;
            }

            paid.AddRange(records);
        }

        scope.Commit();

        return paid.ToImmutable();
    }

    private static (World? World, int Applied) Apply(
        World world,
        CommittedAction fact,
        ImmutableArray<IActionEffect> effects,
        ImmutableArray<CommittedEffect>.Builder? payload = null,
        int count = 1)
    {
        using var scope = world.BeginScope();
        var applied = 0;
        for (var repetition = 0; repetition < count; repetition++)
        {
            for (var index = 0; index < effects.Length; index++)
            {
                var effect = effects[index];
                if (!effect.CanApply(world, fact))
                {
                    scope.Rollback();

                    return (null, applied);
                }

                var delta = effect.Apply(world, fact);
                if (payload is not null)
                {
                    if (count == 1)
                    {
                        payload.Add(delta);
                    }
                    else
                    {
                        Accumulate(payload, delta);
                    }
                }
                applied++;
            }
        }

        scope.Commit();

        return (world, applied);
    }

    private static void Accumulate(ImmutableArray<CommittedEffect>.Builder payload, CommittedEffect delta)
    {
        for (var index = 0; index < payload.Count; index++)
        {
            var previous = payload[index];
            if (previous.Kind == delta.Kind && previous.TargetId == delta.TargetId)
            {
                payload[index] = previous with { Amount = previous.Amount + delta.Amount };

                return;
            }
        }

        payload.Add(delta);
    }

    private static ImmutableArray<T> OrEmpty<T>(ImmutableArray<T> values) => values.IsDefault ? [] : values;
}

public sealed record FixedOutcome(ActionOutcome Outcome) : IActionOutcomeResolver
{
    public bool CanResolve(World world, ActionIntent intent) =>
        world.ById(intent.SourceId) is { ToRemove: false }
        && world.ById(intent.TargetId) is { ToRemove: false };

    public ActionOutcome Resolve(World world, ActionIntent intent) => Outcome;
}

public enum SelectorSubject
{
    Source,
    Target,
    Link,
    Explicit,
    FirstTagged,
}

public sealed record EntitySelector(
    SelectorSubject Subject,
    string Name = "",
    string Capability = "",
    int EntityId = 0)
{
    public int Resolve(World world, CommittedAction fact) => Subject switch
    {
        SelectorSubject.Source => fact.SourceId,
        SelectorSubject.Target => fact.TargetId,
        SelectorSubject.Explicit => EntityId,
        SelectorSubject.Link => world.ById(fact.SourceId) is { } source
            ? source.Links.GetValueOrDefault(Name)
            : 0,
        SelectorSubject.FirstTagged => world.FirstTagged(Name, Capability),
        _ => 0,
    };
}

public static class Selectors
{
    public static readonly EntitySelector Source = new(SelectorSubject.Source);
    public static readonly EntitySelector Target = new(SelectorSubject.Target);

    public static EntitySelector Link(string link) => new(SelectorSubject.Link, link);

    public static EntitySelector Explicit(int entityId) => new(SelectorSubject.Explicit, EntityId: entityId);

    public static EntitySelector FirstTagged(string tag, string capability = "") =>
        new(SelectorSubject.FirstTagged, tag, capability);
}

public sealed record AdjustProperty(EntitySelector Selector, string Property, double Amount) : IActionEffect
{
    public bool CanApply(World world, CommittedAction fact)
    {
        var id = Selector.Resolve(world, fact);

        return world.ById(id) is { ToRemove: false } entity
            && world.TryGetEffectiveProperty(entity, Property, out var value)
            && double.IsFinite(value + Amount);
    }

    public CommittedEffect Apply(World world, CommittedAction fact)
    {
        var id = Selector.Resolve(world, fact);
        var entity = world.Edit(world.ById(id)!);
        world.TryGetEffectiveProperty(entity, Property, out var before);
        entity.TrySetProperty(world, Property, before + Amount);
        world.TryGetEffectiveProperty(entity, Property, out var after);

        return new("property", id, after - before);
    }
}

public sealed record ProduceResource(EntitySelector Recipient, string Channel = "production") : IActionEffect
{
    public bool CanApply(World world, CommittedAction fact)
    {
        var id = Recipient.Resolve(world, fact);

        return world.ById(id) is ResourceEntity resource
            && !resource.ToRemove
            && world.HasCapability(resource, EntityCapabilities.BeProduced)
            && resource.CanProduce(world, fact.Outcome.Value(Channel), out _);
    }

    public CommittedEffect Apply(World world, CommittedAction fact)
    {
        var id = Recipient.Resolve(world, fact);
        var resource = (ResourceEntity)world.Edit(world.ById(id)!);
        var before = resource.Amount;
        resource.TryProduce(world, fact.Outcome.Value(Channel), fact.Repetitions);

        return new("production", id, resource.Amount - before);
    }
}

