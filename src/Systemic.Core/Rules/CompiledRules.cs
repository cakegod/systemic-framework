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
    Query,
}

public enum RuleEffectKind
{
    AdjustProperty,
    SetModifier,
    AddTag,
    EmitEvent,
    RemoveTag,
    Despawn,
    Transfer,
    Spawn,
    Schedule,
    InvokeAction,
}

/// <summary>How an adjustment combines its amount with the property's current value.</summary>
public enum QuantityOperation
{
    /// <summary>Adds the amount.</summary>
    Add,

    /// <summary>Replaces the value with the amount.</summary>
    Set,

    /// <summary>Multiplies the value by the amount.</summary>
    Multiply,

    /// <summary>Keeps the smaller of the value and the amount.</summary>
    Min,

    /// <summary>Keeps the larger of the value and the amount.</summary>
    Max,
}

/// <summary>An authored entity shape that a spawn effect instantiates.</summary>
/// <remarks>
/// The shape carries no identity of its own: each spawn creates a new entity with the template's
/// tags, capabilities, properties, and half extents. Spatial bounds are centred on the fact position
/// when one is present, so content decides where a spawn lands.
/// </remarks>
public sealed record SpawnTemplate(
    string Id,
    ImmutableArray<string> Tags,
    ImmutableArray<string> Capabilities,
    ImmutableDictionary<string, double> Properties,
    Bounds2? SpatialBounds = null);

/// <summary>State handed to a registered game action when a rule invokes it.</summary>
/// <param name="World">The world the fact is being dispatched in.</param>
/// <param name="Fact">The fact whose rule invoked the action.</param>
/// <param name="Source">The resolved effect subject, or null when it no longer exists.</param>
/// <param name="Target">The resolved fact target, or null when it no longer exists.</param>
/// <param name="Effect">The authored effect, including its amount and payload.</param>
public readonly record struct RuleActionContext(
    World World,
    SimulationFact Fact,
    Entity? Source,
    Entity? Target,
    RuleEffect Effect);

/// <summary>A game action a rule may invoke by name.</summary>
/// <remarks>
/// Actions are domain verbs the game registers, such as strike or deposit. They run inside the
/// dispatch transaction, so a write they make rolls back with the rest of the fact. An action never
/// gets a rule of its own: rules invoke it, and many rules may share one action.
/// </remarks>
public delegate void RuleActionHandler(in RuleActionContext context);

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

    internal CompiledQuery? Query { get; init; }

    internal static RuleCondition FromQuery(CompiledQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return new(RuleConditionKind.Query, RuleSubject.Target, string.Empty, 0) { Query = query };
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
    /// <summary>Gets the typed payload carried by an emitted fact, when one is declared.</summary>
    public SimulationFactPayload? Payload { get; init; }

    /// <summary>Gets how an <see cref="RuleEffectKind.AdjustProperty"/> effect combines its amount.</summary>
    public QuantityOperation Quantity { get; init; } = QuantityOperation.Add;

    /// <summary>Gets the subject a transfer moves its quantity to, when one is set.</summary>
    public RuleSubject? Destination { get; init; }

    /// <summary>Gets the property a transfer writes on its destination.</summary>
    public string? TargetProperty { get; init; }

    /// <summary>Gets the entity shape a spawn effect instantiates.</summary>
    public SpawnTemplate? Template { get; init; }

    /// <summary>Gets how many ticks a schedule effect waits before it dispatches its fact.</summary>
    public int DelayTicks { get; init; }

    /// <summary>Gets the name of the registered game action an invocation effect runs.</summary>
    public string? ActionName { get; init; }

    /// <summary>Gets the property a rank-aware modifier reads instead of a fixed amount.</summary>
    public string? SourceProperty { get; init; }

    public static RuleEffect AdjustProperty(RuleSubject subject, string property, double amount) =>
        new(RuleEffectKind.AdjustProperty, subject, property, amount);

    public static RuleEffect AdjustProperty(RuleSubject subject, string property, double amount,
        QuantityOperation quantity) =>
        new(RuleEffectKind.AdjustProperty, subject, property, amount) { Quantity = quantity };

    public static RuleEffect RemoveTag(RuleSubject subject, string tag) =>
        new(RuleEffectKind.RemoveTag, subject, tag, 0);

    public static RuleEffect Despawn(RuleSubject subject) =>
        new(RuleEffectKind.Despawn, subject, string.Empty, 0);

    /// <summary>Moves up to <paramref name="amount"/> from one subject to the fact's target.</summary>
    /// <param name="subject">The subject the quantity leaves.</param>
    /// <param name="property">The property the quantity leaves.</param>
    /// <param name="targetProperty">The property the quantity arrives in, on the fact's target.</param>
    /// <param name="amount">The maximum quantity to move; the source may hold less.</param>
    public static RuleEffect Transfer(RuleSubject subject, string property, string targetProperty,
        double amount) =>
        new(RuleEffectKind.Transfer, subject, property, amount)
        {
            Destination = RuleSubject.Target,
            TargetProperty = targetProperty,
        };

    public static RuleEffect Spawn(RuleSubject subject, SpawnTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return new RuleEffect(RuleEffectKind.Spawn, subject, template.Id, 0) { Template = template };
    }

    public static RuleEffect Schedule(string eventType, int delayTicks,
        SimulationFactPayload? payload = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(delayTicks);
        return new RuleEffect(RuleEffectKind.Schedule, RuleSubject.Target, eventType, 0)
        {
            DelayTicks = delayTicks,
            Payload = payload,
        };
    }

    public static RuleEffect InvokeAction(string actionName, RuleSubject subject) =>
        InvokeAction(actionName, subject, 0, null);

    /// <summary>Creates an action invocation that carries the authored amount and payload.</summary>
    public static RuleEffect InvokeAction(string actionName, RuleSubject subject, double amount,
        SimulationFactPayload? payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionName);
        return new RuleEffect(RuleEffectKind.InvokeAction, subject, actionName, amount)
        {
            ActionName = actionName,
            Payload = payload,
        };
    }

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

    /// <summary>Creates a modifier whose amount follows another property, such as an upgrade rank.</summary>
    public static RuleEffect SetModifierFromProperty(
        RuleSubject subject,
        string property,
        ModifierOperation operation,
        double value,
        string sourceProperty,
        ModifierStage stage,
        int durationTicks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceProperty);
        return new RuleEffect(RuleEffectKind.SetModifier, subject, property, value, operation, stage,
            durationTicks)
        {
            SourceProperty = sourceProperty,
        };
    }

    public static RuleEffect Emit(string eventType) => Emit(eventType, null);

    public static RuleEffect Emit(string eventType, SimulationFactPayload? payload) =>
        new(RuleEffectKind.EmitEvent, RuleSubject.Target, eventType, 0) { Payload = payload };
}

public sealed record CompiledRule(
    string Id,
    string EventType,
    int Priority,
    ImmutableArray<RuleCondition> Conditions,
    ImmutableArray<RuleEffect> Effects)
{
    /// <summary>Gets the bundle that must be enabled for this rule to match, when one is set.</summary>
    public string? BundleId { get; init; }

    /// <summary>Gets the minimum number of ticks between two matches of this rule.</summary>
    public int CooldownTicks { get; init; }
}

public sealed record RuleScheduleDefinition(
    string Id,
    string EventType,
    int EveryTicks,
    int OffsetTicks,
    string? SourceEntityId,
    string? TargetEntityId,
    double Amount)
{
    /// <summary>Gets the typed payload carried by the scheduled fact, when one is declared.</summary>
    public SimulationFactPayload? Payload { get; init; }
}

/// <summary>One typed field in a declared fact payload.</summary>
public readonly record struct FactFieldDefinition(string Key, SimulationFactValueKind Kind);

/// <summary>A named fact and the typed payload its authors may attach.</summary>
public sealed record FactDefinition(string Id, ImmutableArray<FactFieldDefinition> Fields);

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
            .SelectMany(condition => condition.Formula is { } formula ? [formula]
                : condition.Query?.Formula is { } queryFormula ? [queryFormula]
                : Array.Empty<CompiledExpression>())
            .ToArray();
        EventTypes = [.. formulas.SelectMany(formula => formula.EventTypes)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        MaximumEventWindowTicks = formulas.Select(formula => formula.MaximumEventWindowTicks)
            .DefaultIfEmpty(0).Max();
        Schedules = [.. (schedules ?? []).OrderBy(schedule => schedule.Id, StringComparer.Ordinal)];
    }

    public ImmutableArray<CompiledRule> Rules { get; }

    /// <summary>Gets the declared fact contracts, ordered by ID.</summary>
    public ImmutableArray<FactDefinition> Facts { get; init; } = [];

    /// <summary>Gets the entity shapes spawn effects may instantiate.</summary>
    public ImmutableDictionary<string, SpawnTemplate> Templates { get; init; } =
        ImmutableDictionary<string, SpawnTemplate>.Empty;

    /// <summary>Gets the game actions rules may invoke by name.</summary>
    public ImmutableDictionary<string, RuleActionHandler> Actions { get; init; } =
        ImmutableDictionary<string, RuleActionHandler>.Empty;

    /// <summary>Gets each bundle's enabling tag, keyed by bundle ID.</summary>
    public ImmutableDictionary<string, string> Bundles { get; init; } =
        ImmutableDictionary.Create<string, string>(StringComparer.Ordinal);

    public ImmutableArray<string> EventTypes { get; }

    public int MaximumEventWindowTicks { get; }

    public ImmutableArray<RuleScheduleDefinition> Schedules { get; }

    public ImmutableArray<CompiledRule> ForEvent(string eventType) =>
        _byEvent.TryGetValue(eventType, out var rules) ? rules : [];

    /// <summary>Returns a copy of this rule set with the supplied game actions merged in.</summary>
    /// <param name="actions">Action handlers keyed by the name rules invoke.</param>
    /// <remarks>
    /// A loaded pack has no action handlers, because handlers are code. A host registers them through
    /// this copy instead of rebuilding the rule set, so facts, templates, bundles, and event windows
    /// stay exactly as the loader compiled them.
    /// </remarks>
    public CompiledRuleSet WithActions(IReadOnlyDictionary<string, RuleActionHandler> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        return new CompiledRuleSet(Rules, Schedules)
        {
            Facts = Facts,
            Templates = Templates,
            Bundles = Bundles,
            Actions = Actions.SetItems(actions),
        };
    }
}
