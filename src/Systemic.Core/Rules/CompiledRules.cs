using System.Collections.Immutable;
using Systemic.Kernel;

namespace Systemic.Rules;

public enum RuleSubject
{
    Source,
    Target,
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
