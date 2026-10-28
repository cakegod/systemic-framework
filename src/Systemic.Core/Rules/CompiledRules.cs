using System.Collections.Immutable;
using Systemic.Kernel;

namespace Systemic.Rules;

/// <summary>How a <see cref="RuleSubject"/> is addressed.</summary>
public enum RuleSubjectKind
{
    /// <summary>The fact's source entity.</summary>
    Source,

    /// <summary>The fact's target entity.</summary>
    Target,

    /// <summary>One entity named by ID, independent of the fact.</summary>
    Entity,

    /// <summary>A row of a named population schema, independent of the fact.</summary>
    Row,

    /// <summary>The population row the fact is about, resolved by generation at dispatch.</summary>
    FactRow,
}

/// <summary>Addresses the entity or population row a condition or effect applies to.</summary>
public readonly record struct RuleSubject
{
    private RuleSubject(RuleSubjectKind kind, string populationId, int slot)
    {
        Kind = kind;
        PopulationId = populationId;
        Slot = slot;
    }

    /// <summary>Gets the fact's source entity.</summary>
    public static RuleSubject Source { get; } = new(RuleSubjectKind.Source, string.Empty, 0);

    /// <summary>Gets the fact's target entity.</summary>
    public static RuleSubject Target { get; } = new(RuleSubjectKind.Target, string.Empty, 0);

    /// <summary>Addresses one entity by ID, independent of the fact being dispatched.</summary>
    /// <param name="entityId">The entity's ID.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="entityId"/> is negative.</exception>
    public static RuleSubject Entity(int entityId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(entityId);
        return new RuleSubject(RuleSubjectKind.Entity, string.Empty, entityId);
    }

    /// <summary>Addresses one row of the population schema with the given stable ID.</summary>
    /// <param name="populationId">The population schema ID, for example <c>IPopulationKind.Id</c>.</param>
    /// <param name="slot">The row slot within that population.</param>
    /// <exception cref="ArgumentException"><paramref name="populationId"/> is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is negative.</exception>
    public static RuleSubject Row(string populationId, int slot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(populationId);
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        return new RuleSubject(RuleSubjectKind.Row, populationId, slot);
    }

    /// <summary>Addresses the population row the dispatched fact is about.</summary>
    /// <param name="populationId">The population schema the fact's row must belong to.</param>
    /// <exception cref="ArgumentException"><paramref name="populationId"/> is blank.</exception>
    /// <remarks>
    /// The row is resolved from the fact's generation-checked reference at dispatch, so a row that
    /// despawns before the fact is handled no longer matches: the reference does not fall back to
    /// whichever row now occupies the slot.
    /// </remarks>
    public static RuleSubject FactRow(string populationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(populationId);
        return new RuleSubject(RuleSubjectKind.FactRow, populationId, 0);
    }

    /// <summary>Gets how this subject is addressed.</summary>
    public RuleSubjectKind Kind { get; }

    /// <summary>Gets the population schema ID for a row subject, or an empty string otherwise.</summary>
    /// <remarks>A row rule's property name is the column's stable key ID.</remarks>
    public string PopulationId { get; }

    /// <summary>Gets the row slot for a row subject.</summary>
    public int Slot { get; }

    /// <summary>Gets whether this subject addresses one fixed population row.</summary>
    public bool IsRow => Kind == RuleSubjectKind.Row;

    /// <summary>Gets whether this subject addresses the row the fact is about.</summary>
    public bool IsFactRow => Kind == RuleSubjectKind.FactRow;

    /// <summary>Gets whether this subject addresses a population row, fixed or fact-carried.</summary>
    public bool AddressesRow => Kind is RuleSubjectKind.Row or RuleSubjectKind.FactRow;

    public override string ToString() => Kind switch
    {
        RuleSubjectKind.Row => $"Row({PopulationId}, {Slot})",
        RuleSubjectKind.FactRow => $"FactRow({PopulationId})",
        RuleSubjectKind.Entity => $"Entity({Slot})",
        _ => Kind.ToString(),
    };
}

public enum RuleConditionKind
{
    HasTag,
    PropertyAtLeast,
    Expression,
    Chance,
}

public enum RuleEffectKind
{
    AdjustProperty,
    SetModifier,
    AddTag,
    EmitEvent,
}

public readonly record struct RuleCondition(
    RuleConditionKind Kind,
    RuleSubject Subject,
    string Name,
    double Threshold,
    CompiledExpression? Formula = null)
{
    public static RuleCondition HasTag(RuleSubject subject, string tag) =>
        new(RuleConditionKind.HasTag, subject, tag, 0);

    public static RuleCondition PropertyAtLeast(RuleSubject subject, string property, double value) =>
        new(RuleConditionKind.PropertyAtLeast, subject, property, value);

    public static RuleCondition Chance(double probability)
    {
        if (!double.IsFinite(probability) || probability is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(probability), "Chance probability must be between zero and one.");
        }

        return new(RuleConditionKind.Chance, RuleSubject.Target, string.Empty, probability);
    }

    public static RuleCondition FromExpression(CompiledExpression formula)
    {
        ArgumentNullException.ThrowIfNull(formula);
        if (formula.ResultKind != ExpressionResultKind.Boolean)
        {
            throw new ArgumentException("Rule condition expressions must produce a boolean.", nameof(formula));
        }

        return new(RuleConditionKind.Expression, RuleSubject.Target, string.Empty, 0, formula);
    }
}

public readonly record struct RuleEffect(
    RuleEffectKind Kind,
    RuleSubject Subject,
    string Name,
    double Value,
    ModifierOperation Operation = ModifierOperation.Add,
    ModifierStage Stage = ModifierStage.Standard,
    int DurationTicks = -1)
{
    public static RuleEffect AdjustProperty(RuleSubject subject, string property, double amount) =>
        new(RuleEffectKind.AdjustProperty, subject, property, amount);

    public static RuleEffect AddTag(RuleSubject subject, string tag) =>
        new(RuleEffectKind.AddTag, subject, tag, 0);

    public static RuleEffect SetModifier(
        RuleSubject subject,
        string property,
        ModifierOperation operation,
        double value,
        ModifierStage stage = ModifierStage.Standard,
        int durationTicks = -1) =>
        new(RuleEffectKind.SetModifier, subject, property, value, operation, stage, durationTicks);

    public static RuleEffect Emit(string eventType) =>
        new(RuleEffectKind.EmitEvent, RuleSubject.Target, eventType, 0);
}

public sealed record CompiledRule(
    string Id,
    string EventType,
    int Priority,
    ImmutableArray<RuleCondition> Conditions,
    ImmutableArray<RuleEffect> Effects);

public sealed record RuleScheduleDefinition(
    string Id,
    string EventType,
    int EveryTicks,
    int OffsetTicks,
    string? SourceEntityId,
    string? TargetEntityId,
    double Amount);

/// <summary>Rules sorted once during content compilation, ready for deterministic event dispatch.</summary>
public sealed class CompiledRuleSet
{
    private readonly ImmutableDictionary<string, ImmutableArray<CompiledRule>> _byEvent;

    public CompiledRuleSet(IEnumerable<CompiledRule> rules, IEnumerable<RuleScheduleDefinition>? schedules = null)
    {
        Rules = [.. rules.OrderBy(rule => rule.EventType, StringComparer.Ordinal)
            .ThenByDescending(rule => rule.Priority)
            .ThenBy(rule => rule.Id, StringComparer.Ordinal)];
        _byEvent = Rules.GroupBy(rule => rule.EventType, StringComparer.Ordinal)
            .ToImmutableDictionary(group => group.Key, group => group.ToImmutableArray(), StringComparer.Ordinal);
        var formulas = Rules.SelectMany(rule => rule.Conditions)
            .Where(condition => condition.Formula is not null)
            .Select(condition => condition.Formula!)
            .ToArray();
        EventTypes = [.. formulas.SelectMany(formula => formula.EventTypes)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        MaximumEventWindowTicks = formulas.Select(formula => formula.MaximumEventWindowTicks)
            .DefaultIfEmpty(0).Max();
        Schedules = [.. (schedules ?? []).OrderBy(schedule => schedule.Id, StringComparer.Ordinal)];
    }

    public ImmutableArray<CompiledRule> Rules { get; }

    public ImmutableArray<string> EventTypes { get; }

    public int MaximumEventWindowTicks { get; }

    public ImmutableArray<RuleScheduleDefinition> Schedules { get; }

    public ImmutableArray<CompiledRule> ForEvent(string eventType) =>
        _byEvent.TryGetValue(eventType, out var rules) ? rules : [];
}
