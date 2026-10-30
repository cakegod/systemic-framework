using System.Collections.Immutable;
using Systemic.Kernel;

namespace Systemic.Rules;

public readonly record struct SimulationFact(
    string Type,
    int SourceId,
    int TargetId,
    int Depth = 0,
    double Amount = 0,
    SimulationFactPosition? Position = null,
    SimulationFactPayload? Payload = null,
    PopulationRowReference? Row = null)
{
    public SimulationFact(FactTypeId Type, int SourceId, int TargetId, int Depth = 0, double Amount = 0,
        SimulationFactPosition? Position = null, SimulationFactPayload? Payload = null)
        : this(Type.Value, SourceId, TargetId, Depth, Amount, Position, Payload) { }

    /// <summary>Creates a fact about one population row rather than about entities.</summary>
    /// <param name="type">The authored event name.</param>
    /// <param name="row">A generation-checked reference to the row the fact is about.</param>
    /// <param name="depth">Reaction depth; zero for a fact dispatched by a system.</param>
    /// <param name="amount">The authored amount.</param>
    /// <remarks>The fact carries no entity IDs, so only row-addressed rules can act on it.</remarks>
    public static SimulationFact ForRow(string type, PopulationRowReference row, int depth = 0, double amount = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        return new SimulationFact(type, -1, -1, depth, amount, Row: row);
    }

    /// <summary>A strongly typed view of the authored event name.</summary>
    public FactTypeId TypeId => string.IsNullOrWhiteSpace(Type) ? default : new FactTypeId(Type);

    // Preserve the original five-value deconstruction when position and payload were added.
    public void Deconstruct(out string Type, out int SourceId, out int TargetId, out int Depth, out double Amount)
    {
        Type = this.Type;
        SourceId = this.SourceId;
        TargetId = this.TargetId;
        Depth = this.Depth;
        Amount = this.Amount;
    }

    /// <summary>Gets whether the fact names exactly one subject: an entity pair or one population row.</summary>
    /// <remarks>
    /// A row fact carries no entity IDs, so the two forms cannot be confused: an entity fact needs both
    /// IDs non-negative, and a row fact needs both at -1 with a generation-checked row reference.
    /// </remarks>
    internal bool HasValidAddress => Row is null
        ? SourceId >= 0 && TargetId >= 0
        : SourceId == -1 && TargetId == -1 && Row.Value.Slot >= 0 && Row.Value.Generation != 0
            && !string.IsNullOrWhiteSpace(Row.Value.PopulationId);
}

public readonly struct SimulationFactEmitter
{
    private readonly RuleEngine? _engine;
    private readonly long _generation;

    internal SimulationFactEmitter(RuleEngine engine, long generation)
    {
        _engine = engine;
        _generation = generation;
    }

    public bool Enqueue(SimulationFact fact) => _engine?.TryEnqueue(_generation, fact) ?? false;

    /// <summary>Schedules a side effect only if the enclosing fact dispatch commits.</summary>
    public bool OnCommit(Action action) => _engine?.TryRegisterCommitCallback(_generation, action) ?? false;

}

public delegate void SimulationFactObserver(SimulationFact fact, SimulationFactEmitter emitter);

/// <summary>Counts from one per-row rule scan.</summary>
/// <param name="RowsEvaluated">Rows the scan visited.</param>
/// <param name="RulesMatched">Rule matches across all rows.</param>
/// <param name="EffectsApplied">Effects the matches applied.</param>
/// <param name="RolledBack">Whether the scan's transaction was rolled back.</param>
/// <param name="CutoffReason">Why the scan stopped early, when it did.</param>
public readonly record struct RuleRowScanResult(
    int RowsEvaluated,
    int RulesMatched,
    int EffectsApplied,
    bool RolledBack = false,
    string? CutoffReason = null);

public readonly record struct RuleDispatchResult(
    int FactsProcessed,
    int RulesMatched,
    int EffectsApplied,
    bool CutOff,
    string? CutoffReason = null,
    bool RolledBack = false);

public readonly record struct RuleEventHistorySample(int Tick, string EventType, double Amount);

public sealed record RuleTraceFact(int Sequence, SimulationFact Fact, ImmutableArray<string> MatchedRules);

public sealed record RuleDispatchTrace(RuleDispatchResult Result, ImmutableArray<RuleTraceFact> Facts);

/// <summary>Runs a bounded, deterministic set of compiled content rules over committed facts.</summary>
public sealed class RuleEngine
{
    private const int MaximumEventHistoryFacts = 65_536;
    private readonly List<SimulationFact> _queue = new(32);
    private readonly List<EventSample> _eventHistory = [];
    private readonly List<EventSample> _pendingEventHistory = [];
    private readonly Dictionary<string, List<EventSample>> _eventHistoryByType = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<EventSample>> _pendingEventHistoryByType = new(StringComparer.Ordinal);
    private readonly Dictionary<EventQueryKey, double> _eventQueryCache = [];
    private readonly HashSet<string> _eventTypeSet;
    private readonly CompiledRuleSet _rules;
    private readonly int _maxEffects;
    private readonly int _maxDepth;
    private readonly int _maxFacts;
    private readonly int _tickRateHz;
    private long _dispatchGeneration;
    private bool _dispatchActive;
    private const int MaximumScheduledFacts = 65_536;
    private const int MaximumDueFactsPerCall = 65_536;
    private const int MaximumCooldownEntries = 100_000;
    private readonly List<ScheduledFactEntry> _scheduled = [];
    private long _scheduledSequence;
    private readonly Dictionary<(string Rule, int Source, int Target), int> _lastRuleFire = [];
    private readonly List<CooldownChange> _cooldownChanges = [];
    private bool _factBudgetExceeded;
    private bool _invalidFact;
    private bool _eventHistoryIndexDirty;
    // Rollback netcode clones the world per tick, so bindings are cached per world: rebinding
    // one world's columns would write into the wrong clone. The cap keeps a hot but bounded set.
    private readonly Dictionary<World, Dictionary<(string Population, string Name, RowTargetKind Kind), RowBinding>>
        _rowBindings = [];

    // A row scan resolves its bindings once per world, event and population, because doing it per row
    // would hash every name a million times a tick. Cleared whenever the bindings are rebuilt.
    private readonly Dictionary<(World World, string EventType, string PopulationId), ResolvedRowRule[]>
        _rowScanPlans = [];

    private readonly Dictionary<World, long> _boundRevisions = [];

    private WorldTransaction? _activeTransaction;
    private WorldTransaction? _batchTransaction;

    public RuleEngine(
        CompiledRuleSet rules,
        int maxEffects = 1024,
        int maxDepth = 8,
        int? maxFacts = null,
        int tickRateHz = 60)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (maxEffects is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEffects));
        }

        if (maxDepth is < 0 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDepth));
        }

        var factLimit = maxFacts ?? maxEffects + 1;
        if (factLimit is < 1 or > 65_537)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFacts));
        }

        if (tickRateHz is < 1 or > 240)
        {
            throw new ArgumentOutOfRangeException(nameof(tickRateHz));
        }

        _rules = rules;
        _eventTypeSet = new HashSet<string>(rules.EventTypes, StringComparer.Ordinal);
        _maxEffects = maxEffects;
        _maxDepth = maxDepth;
        _maxFacts = factLimit;
        _tickRateHz = tickRateHz;
    }

    public ImmutableArray<RuleEventHistorySample> CaptureEventHistory() =>
        [.. _eventHistory.Select(sample => new RuleEventHistorySample(sample.Tick, sample.EventType, sample.Amount))];

    public void RestoreEventHistory(IEnumerable<RuleEventHistorySample> samples, int currentTick)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (_dispatchActive)
        {
            throw new InvalidOperationException("Rule event history cannot be restored during dispatch.");
        }

        var restored = samples.ToArray();
        if (restored.Length > MaximumEventHistoryFacts)
        {
            throw new ArgumentException("Rule event history exceeds its configured limit.", nameof(samples));
        }

        var firstTick = currentTick - _rules.MaximumEventWindowTicks + 1;
        var previousTick = -1;
        foreach (var sample in restored)
        {
            if (sample.Tick < firstTick || sample.Tick > currentTick || sample.Tick < previousTick
                || string.IsNullOrWhiteSpace(sample.EventType)
                || !_eventTypeSet.Contains(sample.EventType)
                || !double.IsFinite(sample.Amount))
            {
                throw new ArgumentException("Rule event history contains invalid or expired samples.", nameof(samples));
            }

            previousTick = sample.Tick;
        }

        _eventHistory.Clear();
        _eventHistory.AddRange(restored.Select(sample => new EventSample(sample.Tick, sample.EventType, sample.Amount)));
        _eventHistoryIndexDirty = true;
    }

    /// <summary>Compiles and validates the rule set's row-addressed conditions and effects against a world.</summary>
    /// <param name="world">World whose populations the rules address.</param>
    /// <remarks>
    /// Call this once while loading, before the first dispatch; the first dispatch into a world binds
    /// implicitly when the caller does not. A rule naming a population, column, or column value type
    /// this world does not provide fails here rather than mid-dispatch. A row rule's property name is
    /// the column's stable key ID, so it is checked against the schema rather than guessed at runtime.
    /// Row subjects support property comparisons, schema tags, property adjustments, and additive
    /// modifiers. Expressions stay on the entity path. A row property comparison reads the column's
    /// effective value, so a row modifier declared for that property is part of the comparison.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="world"/> is null.</exception>
    /// <exception cref="InvalidOperationException">A row rule cannot be compiled against this world.</exception>
    public void Bind(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        // Validate into a local table first: a rejected rule set must not leave a half-bound
        // entry that later dispatches would treat as successfully bound.
        var rowBindings = new Dictionary<(string Population, string Name, RowTargetKind Kind), RowBinding>();
        foreach (var rule in _rules.Rules)
        {
            if ((rule.BundleId is not null || rule.CooldownTicks > 0)
                && (rule.Conditions.Any(condition => condition.Subject.AddressesRow)
                    || rule.Effects.Any(effect => effect.Subject.AddressesRow)))
            {
                throw new InvalidOperationException(
                    $"Rule '{rule.Id}' combines bundle or cooldown settings with a population row subject; "
                    + "a row scan evaluates neither.");
            }

            foreach (var condition in rule.Conditions)
            {
                if (!condition.Subject.AddressesRow)
                {
                    continue;
                }

                switch (condition.Kind)
                {
                    case RuleConditionKind.PropertyAtLeast:
                        BindColumn(world, rowBindings, rule, condition.Subject, condition.Name);
                        break;
                    case RuleConditionKind.HasTag:
                        BindTag(world, rowBindings, rule, condition.Subject, condition.Name);
                        break;
                    case RuleConditionKind.Query:
                        throw new InvalidOperationException(
                            $"Rule '{rule.Id}' applies a query condition to {condition.Subject}; "
                            + "queries select entities and cannot be addressed to a population row.");
                    default:
                        throw new InvalidOperationException(
                            $"Rule '{rule.Id}' uses a {condition.Kind} condition on {condition.Subject}; "
                            + "row subjects support property comparisons and schema tags only.");
                }
            }

            foreach (var effect in rule.Effects)
            {
                if (!effect.Subject.AddressesRow)
                {
                    continue;
                }

                switch (effect.Kind)
                {
                    case RuleEffectKind.AdjustProperty:
                        var column = BindColumn(world, rowBindings, rule, effect.Subject, effect.Name);
                        if (PopulationNumeric.IsIntegral(column.ValueType) && effect.Value != Math.Truncate(effect.Value))
                        {
                            throw new InvalidOperationException(
                                $"Rule '{rule.Id}' adjusts integer column '{effect.Name}' of '{effect.Subject.PopulationId}' by {effect.Value}; "
                                + "a whole-number amount is required so the write stays exact.");
                        }

                        if (effect.Quantity != QuantityOperation.Add)
                        {
                            throw new InvalidOperationException(
                                $"Rule '{rule.Id}' applies quantity '{effect.Quantity}' to row '{effect.Subject}'; "
                                + "a row scan supports add only, so declare the operation as content on an entity instead.");
                        }

                        break;
                    case RuleEffectKind.AddTag:
                    case RuleEffectKind.RemoveTag:
                        BindTag(world, rowBindings, rule, effect.Subject, effect.Name);
                        break;
                    case RuleEffectKind.SetModifier:
                        if (effect.SourceProperty is { } sourceProperty)
                        {
                            throw new InvalidOperationException(
                                $"Rule '{rule.Id}' reads rank property '{sourceProperty}' for row '{effect.Subject}'; "
                                + "a row modifier stores a fixed amount.");
                        }

                        BindModifier(world, rowBindings, rule, effect);
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Rule '{rule.Id}' applies {effect.Kind} '{effect.Name}' to {effect.Subject}; "
                            + "row subjects support property adjustments, schema tags and additive modifiers only.");
                }
            }
        }

        foreach (var rule in _rules.Rules)
        {
            foreach (var effect in rule.Effects)
            {
                if (effect.Kind == RuleEffectKind.Spawn
                    && (effect.Template is not { } template || !_rules.Templates.ContainsKey(template.Id)))
                {
                    throw new InvalidOperationException(
                        $"Rule '{rule.Id}' spawns template '{effect.Template?.Id ?? effect.Name}', "
                        + "which this rule set does not define.");
                }

                if (effect.Kind == RuleEffectKind.InvokeAction && !_rules.Actions.ContainsKey(effect.Name))
                {
                    throw new InvalidOperationException(
                        $"Rule '{rule.Id}' invokes action '{effect.Name}', which the game has not registered.");
                }
            }
        }

        if (_rowBindings.Count >= 8)
        {
            _rowBindings.Clear();
            _boundRevisions.Clear();
            _rowScanPlans.Clear();
        }

        _rowBindings[world] = rowBindings;
        _boundRevisions[world] = world.PopulationDefinitionRevision;
        _rowScanPlans.Clear();
    }

    private static PopulationColumnBase BindColumn(World world,
        Dictionary<(string Population, string Name, RowTargetKind Kind), RowBinding> rowBindings,
        CompiledRule rule,
        RuleSubject subject,
        string columnId)
    {
        var column = RequireNumericColumn(world, rule, subject, columnId);
        // A schema that declares this property for modifiers gets the effective value on every read,
        // so a rule's condition sees the same number its modifier effects write.
        var modifiers = default(PopulationModifierAccess);
        var modifierKey = RowModifierSlot.EmptyKey;
        if (world.TryGetPopulationModifiers(subject.PopulationId, out var access)
            && access.TryGetKey(columnId, out var key))
        {
            modifiers = access;
            modifierKey = key;
        }

        rowBindings[(subject.PopulationId, columnId, RowTargetKind.Column)] =
            RowBinding.ForColumn(column, modifiers, modifierKey);
        return column;
    }

    private static PopulationColumnBase RequireNumericColumn(World world, CompiledRule rule,
        RuleSubject subject, string columnId)
    {
        if (!world.TryGetPopulationColumn(subject.PopulationId, columnId, out var column))
        {
            throw new InvalidOperationException(
                $"Rule '{rule.Id}' references column '{columnId}' of population '{subject.PopulationId}', "
                + "which this world does not define.");
        }

        if (!PopulationNumeric.IsNumeric(column.ValueType))
        {
            throw new InvalidOperationException(
                $"Rule '{rule.Id}' references column '{columnId}' of population '{subject.PopulationId}', "
                + $"which holds '{column.ValueType.Name}'; row rules need a numeric column.");
        }

        // WHY: a column without row-bridge support declines every read and write, so a rule bound to
        // it would report applied effects that never change the simulated column.
        if (!column.SupportsRowBridge)
        {
            throw new InvalidOperationException(
                $"Rule '{rule.Id}' references double-buffered column '{columnId}' of population "
                + $"'{subject.PopulationId}'; row rules address the simulated (unbuffered) column.");
        }

        return column;
    }

    private static void BindModifier(World world,
        Dictionary<(string Population, string Name, RowTargetKind Kind), RowBinding> rowBindings,
        CompiledRule rule,
        RuleEffect effect)
    {
        var subject = effect.Subject;
        if (effect.Operation != ModifierOperation.Add)
        {
            throw new InvalidOperationException(
                $"Rule '{rule.Id}' applies a {effect.Operation} modifier to {subject}; "
                + "a row modifier slot stores an additive amount, so only Add is supported.");
        }

        if (effect.Stage != ModifierStage.Standard)
        {
            throw new InvalidOperationException(
                $"Rule '{rule.Id}' applies a {effect.Stage} modifier to {subject}; "
                + "a row modifier slot has no stage, so only Standard is supported.");
        }

        if (!double.IsFinite(effect.Value) || !float.IsFinite((float)effect.Value))
        {
            throw new InvalidOperationException(
                $"Rule '{rule.Id}' applies modifier value {effect.Value} to '{effect.Name}' of '{subject.PopulationId}'; "
                + "a row modifier value must be a finite single-precision number.");
        }

        if (!world.TryGetPopulationModifiers(subject.PopulationId, out var modifiers))
        {
            throw new InvalidOperationException(
                $"Rule '{rule.Id}' sets a modifier on '{effect.Name}' of population '{subject.PopulationId}', "
                + "which this world does not define.");
        }

        if (!modifiers.TryGetKey(effect.Name, out var key))
        {
            throw new InvalidOperationException(
                $"Rule '{rule.Id}' sets a modifier on '{effect.Name}' of population '{subject.PopulationId}', "
                + $"which does not declare it. Declared modifier properties: '{string.Join("', '", modifiers.Properties.Span)}'. "
                + "Declare the property on the schema so its slot key is part of the saved state.");
        }

        var column = RequireNumericColumn(world, rule, subject, effect.Name);
        rowBindings[(subject.PopulationId, effect.Name, RowTargetKind.Modifier)] =
            RowBinding.ForModifier(modifiers, key, column);
    }

    private static void BindTag(World world,
        Dictionary<(string Population, string Name, RowTargetKind Kind), RowBinding> rowBindings,
        CompiledRule rule,
        RuleSubject subject,
        string tag)
    {
        if (!world.TryGetPopulationTags(subject.PopulationId, out var tags))
        {
            throw new InvalidOperationException(
                $"Rule '{rule.Id}' references tag '{tag}' of population '{subject.PopulationId}', "
                + "which this world does not define.");
        }

        if (!tags.TryGetBit(tag, out var bit))
        {
            throw new InvalidOperationException(
                $"Rule '{rule.Id}' references tag '{tag}' of population '{subject.PopulationId}', "
                + $"which does not declare it. Declared tags: '{string.Join("', '", tags.Names.Span)}'. "
                + "Declare the tag on the schema so its mask bit is part of the saved state.");
        }

        // The scan binds the mask column directly: it tests the same bit on every row, so it must
        // not re-resolve the population, revalidate the tag, or check the world per row.
        _ = world.TryGetPopulationTagMask(subject.PopulationId, tag, out var mask, out _);
        rowBindings[(subject.PopulationId, tag, RowTargetKind.Tag)] = RowBinding.ForTag(mask, tags, bit);
    }

    // Populations are usually defined during world initialization, after the game is built, so a row
    // rule set binds against a world on its first dispatch instead of requiring a build-order
    // assumption. A missing column still fails before any effect applies.
    private void EnsureBound(World world)
    {
        // A definition created or discarded inside a transaction changes which columns the world
        // owns, and cached bindings would keep pointing at detached ones. The revision is one read.
        if (_rowBindings.ContainsKey(world)
            && _boundRevisions.TryGetValue(world, out var revision)
            && revision == world.PopulationDefinitionRevision)
        {
            return;
        }

        Bind(world);
    }

    // A fixed row subject names its slot directly; a fact row subject resolves the fact's reference,
    // which fails once the row is gone. Resolution is the only place the slot is decided.
    private static bool TryResolveRowSlot(World world, SimulationFact fact, RuleSubject subject, out int slot)
    {
        if (!subject.IsFactRow)
        {
            slot = subject.Slot;
            return true;
        }

        slot = -1;
        return fact.Row is { } row
            && string.Equals(row.PopulationId, subject.PopulationId, StringComparison.Ordinal)
            && world.TryResolvePopulationRow(subject.PopulationId, row.Slot, row.Generation, out slot);
    }

    private RowBinding ResolveRowBinding(World world, string ruleId, RuleSubject subject, string name,
        RowTargetKind kind, string factType)
    {
        if (!_rowBindings.TryGetValue(world, out var rowBindings)
            || !rowBindings.TryGetValue((subject.PopulationId, name, kind), out var binding))
        {
            throw new InvalidOperationException(
                $"Rule '{ruleId}' addresses '{name}' of population '{subject.PopulationId}' "
                + $"but the engine is not bound to this world; call {nameof(RuleEngine)}.{nameof(Bind)} during load. "
                + $"Fact '{factType}' was not applied.");
        }

        return binding;
    }

    // What one resolved row target is: a numeric column (read through its modifier slots when the
    // schema declares them), a schema tag bit, or a modifier slot key. Everything a dispatch needs
    // is resolved here, so dispatch never hashes a name or looks the population up again.
    private enum RowTargetKind
    {
        Column,
        Tag,
        Modifier,
    }

    private readonly struct RowBinding
    {
        private RowBinding(RowTargetKind kind, PopulationColumnBase? column, PopulationTagAccess tags,
            PopulationColumn<ulong>? maskColumn, int tagBit, PopulationModifierAccess modifiers,
            ushort modifierKey)
        {
            Kind = kind;
            Column = column;
            Tags = tags;
            MaskColumn = maskColumn;
            TagBit = tagBit;
            Modifiers = modifiers;
            ModifierKey = modifierKey;
        }

        public RowTargetKind Kind { get; }

        public PopulationColumnBase? Column { get; }

        public PopulationTagAccess Tags { get; }

        public PopulationColumn<ulong>? MaskColumn { get; }

        public int TagBit { get; }

        public PopulationModifierAccess Modifiers { get; }

        public ushort ModifierKey { get; }

        public bool HasModifierKey => ModifierKey != RowModifierSlot.EmptyKey;

        public static RowBinding ForColumn(PopulationColumnBase column, PopulationModifierAccess modifiers,
            ushort modifierKey) =>
            new(RowTargetKind.Column, column, default, null, 0, modifiers, modifierKey);

        public static RowBinding ForTag(PopulationColumn<ulong>? maskColumn, PopulationTagAccess tags,
            int tagBit) =>
            new(RowTargetKind.Tag, null, tags, maskColumn, tagBit, default, RowModifierSlot.EmptyKey);

        public static RowBinding ForModifier(PopulationModifierAccess modifiers, ushort modifierKey,
            PopulationColumnBase column) =>
            new(RowTargetKind.Modifier, column, default, null, 0, modifiers, modifierKey);
    }

    public RuleDispatchResult Dispatch(
        World world,
        SimulationFact initialFact,
        SimulationFactObserver? beforeEachFact = null) =>
        DispatchCore(world, initialFact, beforeEachFact, trace: null);

    public RuleDispatchTrace DispatchWithTrace(
        World world,
        SimulationFact initialFact,
        SimulationFactObserver? beforeEachFact = null)
    {
        var facts = new List<RuleTraceFact>();
        var result = DispatchCore(world, initialFact, beforeEachFact, facts);
        return new RuleDispatchTrace(result, [.. facts]);
    }

    /// <summary>Evaluates one event's row rules against every row of a population range.</summary>
    /// <param name="world">World that receives the effects.</param>
    /// <param name="eventType">The event whose rules to evaluate.</param>
    /// <param name="populationId">The population to scan; every rule subject must be its row.</param>
    /// <param name="range">The rows to evaluate.</param>
    /// <returns>Counts for the scan, and whether it rolled back.</returns>
    /// <remarks>
    /// This is the crowd path: one authored rule evaluated per row, one transaction for the whole
    /// range, and no fact per row. It is deliberately narrower than dispatch — a scanned rule may
    /// only address the row being scanned with row subjects, so an entity subject or an
    /// <c>emitEvent</c> effect is rejected before the first row instead of producing a fact per row.
    /// The population must be defined with handles, because each row is addressed by a
    /// generation-checked reference.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="world"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The range is not inside the population.</exception>
    /// <exception cref="InvalidOperationException">The population, its handles, or the rule shape
    /// cannot support a row scan, or a dispatch is already active.</exception>
    public RuleRowScanResult EvaluateRowEvent(World world, string eventType, string populationId,
        PopulationRange range)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(populationId);
        if (_dispatchActive)
        {
            throw new InvalidOperationException("RuleEngine does not support nested or concurrent dispatches.");
        }

        if (!world.TryGetPopulationRows(populationId, out var rows))
        {
            throw new InvalidOperationException(
                $"Row scan for event '{eventType}' references population '{populationId}', "
                + "which this world does not define.");
        }

        if (!rows.HandlesEnabled)
        {
            throw new InvalidOperationException(
                $"Row scan for population '{populationId}' requires handles so every row is addressed by a "
                + "generation-checked reference; define the population with enableHandles: true.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(range.Start);
        ArgumentOutOfRangeException.ThrowIfNegative(range.Length);
        if (range.Start > rows.RowCount || range.Length > rows.RowCount - range.Start)
        {
            throw new ArgumentOutOfRangeException(nameof(range), range,
                $"The range must fit inside population '{populationId}', which has {rows.RowCount} rows.");
        }

        var rules = _rules.ForEvent(eventType);
        foreach (var rule in rules)
        {
            foreach (var condition in rule.Conditions)
            {
                if (!IsScannableSubject(condition.Subject, populationId))
                {
                    throw NotScannable(rule, condition.Subject, populationId, "condition");
                }
            }

            foreach (var effect in rule.Effects)
            {
                if (!IsScannableSubject(effect.Subject, populationId))
                {
                    throw NotScannable(rule, effect.Subject, populationId, effect.Kind.ToString());
                }
            }
        }

        EnsureBound(world);
        _dispatchActive = true;
        try
        {
            // A scope, not a WorldTransaction: the scan runs every tick and a transaction object per
            // tick would be the only allocation in an otherwise allocation-free pass. `using` rolls
            // the scope back when an effect throws mid-scan, so no partial writes survive.
            using var scope = world.BeginScope();
            var evaluated = 0;
            var matched = 0;
            var applied = 0;
            var cutOff = false;
            string? cutoffReason = null;
            var plans = GetRowScanPlan(world, eventType, populationId);
            var end = range.End;
            if (plans.Length == 1 && plans[0].PrefilterMask is { } maskColumn)
            {
                // One rule per event is the authored crowd shape, and with a single plan the tag
                // prefilter is a packed bitset read: one bit per row instead of the eight-byte mask
                // word, and the rule still runs in full for rows that carry it. The mask is only a
                // filter, so no other rule can observe an effect of this one.
                ref readonly var plan = ref plans[0];
                var limit = Math.Min(end, rows.RowCount);
                if (plan.PrefilterTags.TryGetRowBits(plan.PrefilterBit, out var packed))
                {
                    // One bit per row instead of one mask word: sixty-four rows per eight-byte load.
                    // A tag write during the scan lands in this same bitset, so a later row still
                    // sees it exactly as it would have seen the mask column.
                    var scanned = range.Start;
                    var wordIndex = range.Start >> 6;
                    while (wordIndex < packed.Length && (wordIndex << 6) < limit && !cutOff)
                    {
                        var wordStart = wordIndex << 6;
                        var word = packed[wordIndex];
                        if (wordStart < range.Start)
                        {
                            word &= ulong.MaxValue << (range.Start - wordStart);
                        }

                        while (word != 0)
                        {
                            var offset = System.Numerics.BitOperations.TrailingZeroCount(word);
                            var candidate = wordStart + offset;
                            if (candidate >= limit)
                            {
                                break;
                            }

                            evaluated += candidate + 1 - scanned;
                            scanned = candidate + 1;
                            var appliedHere = MatchesResolvedRow(in plan, candidate);
                            if (appliedHere)
                            {
                                matched += ApplyRowRule(in plan, candidate, ref applied);
                                if (cutOff)
                                {
                                    break;
                                }
                            }

                            // Re-reading the word after a rule ran keeps the scan live: a tag write
                            // for a later row in this word must be seen, exactly as the per-row mask
                            // read would have seen it. Bits already visited stay masked off.
                            word = appliedHere
                                ? packed[wordIndex] & ~(((1UL << offset) << 1) - 1)
                                : word & ~(1UL << offset);
                        }

                        wordIndex++;
                    }

                    if (!cutOff)
                    {
                        evaluated += limit - scanned;
                    }
                }
                else
                {
                    var masks = maskColumn.ReadOnlySpan;
                    var bit = 1UL << plan.PrefilterBit;
                    limit = Math.Min(limit, masks.Length);
                    for (var row = range.Start; row < limit && !cutOff; row++)
                    {
                        evaluated++;
                        if ((masks[row] & bit) != 0 && MatchesResolvedRow(in plan, row))
                        {
                            matched += ApplyRowRule(in plan, row, ref applied);
                        }
                    }
                }
            }
            else
            {
                for (var row = range.Start; row < end && !cutOff; row++)
                {
                    evaluated++;
                    for (var planIndex = 0; planIndex < plans.Length; planIndex++)
                    {
                        ref readonly var plan = ref plans[planIndex];
                        if (!PassesPrefilter(in plan, row) || !MatchesResolvedRow(in plan, row))
                        {
                            continue;
                        }

                        matched += ApplyRowRule(in plan, row, ref applied);
                    }
                }
            }

            var rolledBack = cutoffReason is "non_finite_property" or "invalid_fact";
            if (rolledBack)
            {
                scope.Rollback();
            }
            else
            {
                scope.Commit();
            }

            return new RuleRowScanResult(evaluated, matched, applied, rolledBack, cutoffReason);
        }
        finally
        {
            _dispatchActive = false;
            _activeTransaction = null;
        }
    }

    private ResolvedRowRule[] GetRowScanPlan(World world, string eventType, string populationId)
    {
        var key = (world, eventType, populationId);
        if (_rowScanPlans.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var rules = _rules.ForEvent(eventType);
        var plans = new ResolvedRowRule[rules.Length];
        for (var ruleIndex = 0; ruleIndex < rules.Length; ruleIndex++)
        {
            var rule = rules[ruleIndex];
            var conditions = new RowBinding[rule.Conditions.Length];
            for (var index = 0; index < conditions.Length; index++)
            {
                var condition = rule.Conditions[index];
                conditions[index] = ResolveRowBinding(world, rule.Id, condition.Subject, condition.Name,
                    condition.Kind == RuleConditionKind.HasTag ? RowTargetKind.Tag : RowTargetKind.Column,
                    eventType);
            }

            var effects = new RowBinding[rule.Effects.Length];
            for (var index = 0; index < effects.Length; index++)
            {
                var effect = rule.Effects[index];
                effects[index] = ResolveRowBinding(world, rule.Id, effect.Subject, effect.Name,
                    effect.Kind switch
                    {
                        RuleEffectKind.AddTag => RowTargetKind.Tag,
                        RuleEffectKind.SetModifier => RowTargetKind.Modifier,
                        _ => RowTargetKind.Column,
                    }, eventType);
            }

            // A leading tag condition is a mask test, so the scan reads one bit per row and only
            // walks the rest of the rule for rows that carry it. Every other condition still runs in
            // authored order, and a tag test has no side effects, so the skip cannot change results.
            var prefilter = conditions.Length > 0 && rule.Conditions[0].Kind == RuleConditionKind.HasTag
                ? conditions[0]
                : default;
            plans[ruleIndex] = new ResolvedRowRule(rule, conditions, effects,
                prefilter.MaskColumn, prefilter.TagBit, prefilter.Tags);
        }

        _rowScanPlans[key] = plans;
        return plans;
    }

    // A rule whose first condition is a tag only matches rows whose mask bit is set, so the scan
    // tests the bit before it touches the condition list. Rules without that shape pass through.
    private static bool PassesPrefilter(in ResolvedRowRule plan, int row) =>
        plan.PrefilterMask is not { } maskColumn
        || (maskColumn.TryReadRow(row, out var mask) && (mask & (1UL << plan.PrefilterBit)) != 0);

    // Applies one matched row's effects and reports the match. Shared by both scan loops so the
    // effect budget and the rollback path are identical whichever one ran.
    private static int ApplyRowRule(in ResolvedRowRule plan, int row, ref int applied)
    {
        for (var index = 0; index < plan.Effects.Length; index++)
        {
            applied++;
            ApplyRowEffect(plan.Effects[index], plan.Rule.Effects[index], row);
        }

        return 1;
    }

    private static bool MatchesResolvedRow(in ResolvedRowRule plan, int row)
    {
        for (var index = 0; index < plan.Conditions.Length; index++)
        {
            if (!RowMatches(plan.Conditions[index], plan.Rule.Conditions[index], row))
            {
                return false;
            }
        }

        return true;
    }

    // One row condition against an already-resolved binding: a tag bit test or a numeric read.
    private static bool RowMatches(in RowBinding binding, RuleCondition condition, int row)
    {
        if (binding.Kind == RowTargetKind.Tag)
        {
            // One mask read against the bound column: no population lookup, tag validation or
            // world check per row, which is what a million-row scan cannot afford.
            return binding.MaskColumn is { } maskColumn
                && maskColumn.TryReadRow(row, out var mask)
                && (mask & (1UL << binding.TagBit)) != 0;
        }

        double value;
        var read = binding.HasModifierKey
            ? binding.Modifiers.TryGetEffectiveValue(row, binding.Column!, binding.ModifierKey, out value)
            : binding.Column!.TryGetRowValue(row, out value);
        return read && value >= condition.Threshold;
    }

    // One row effect against an already-resolved binding. A slot past the live row count is a
    // despawned row: the effect is accepted and has nowhere to land, like a missing entity.
    private static void ApplyRowEffect(in RowBinding binding, RuleEffect effect, int row)
    {
        switch (binding.Kind)
        {
            case RowTargetKind.Tag:
                binding.Tags.TrySetTagBit(row, binding.TagBit,
                    value: effect.Kind == RuleEffectKind.AddTag);
                break;
            case RowTargetKind.Modifier:
                binding.Modifiers.TrySet(row, binding.ModifierKey, effect.Value, effect.DurationTicks);
                break;
            default:
                binding.Column!.TryAddRowValue(row, effect.Value);
                break;
        }
    }

    private readonly record struct ResolvedRowRule(CompiledRule Rule, RowBinding[] Conditions, RowBinding[] Effects,
        PopulationColumn<ulong>? PrefilterMask, int PrefilterBit, PopulationTagAccess PrefilterTags);

    // A row scan walks rows, not the fact queue, so anything that needs an entity or would queue a
    // fact is refused: one fact per row is exactly what the sink exists to prevent. The message is
    // built by the throwing method, not by the test, so the success path formats nothing.
    private static bool IsScannableSubject(RuleSubject subject, string populationId) =>
        subject.IsFactRow && string.Equals(subject.PopulationId, populationId, StringComparison.Ordinal);

    private static InvalidOperationException NotScannable(CompiledRule rule, RuleSubject subject,
        string populationId, string member) =>
        new($"Rule '{rule.Id}' uses {subject} in its {member}, but a row scan of population "
            + $"'{populationId}' only accepts RuleSubject.FactRow('{populationId}'); "
            + "entity subjects and emitted events would act once per row.");

    /// <summary>Dispatches a batch of facts within a single World transaction.</summary>
    /// <param name="world">World that receives the effects of every fact in the batch.</param>
    /// <param name="facts">Facts to dispatch in span order.</param>
    /// <param name="beforeEachFact">Observer invoked before each rule match, as for a single dispatch.</param>
    /// <returns>Aggregate counters for every fact the batch processed.</returns>
    /// <remarks>
    /// A batch shares one transaction and one journal scope, so a tick that queues many facts pays
    /// transaction setup once. If any fact in the batch rolls back, the whole batch rolls back: no
    /// fact in it is committed, and <see cref="RuleDispatchResult.RolledBack"/> reports the failure.
    /// The fact and effect budgets apply per fact, matching single dispatch.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="world"/> is null.</exception>
    /// <exception cref="InvalidOperationException">A dispatch is already active.</exception>
    public RuleDispatchResult DispatchBatch(
        World world,
        ReadOnlySpan<SimulationFact> facts,
        SimulationFactObserver? beforeEachFact = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (facts.Length == 0)
        {
            return default;
        }

        EnsureBound(world);
        if (facts.Length == 1)
        {
            return Dispatch(world, facts[0], beforeEachFact);
        }

        if (_dispatchActive)
        {
            throw new InvalidOperationException("RuleEngine does not support nested or concurrent dispatches.");
        }

        _dispatchActive = true;
        try
        {
            using var transaction = world.BeginTransaction();
            _batchTransaction = transaction;
            var factsProcessed = 0;
            var rulesMatched = 0;
            var effectsApplied = 0;
            var cutOff = false;
            string? cutoffReason = null;
            var rolledBack = false;
            var batchScheduledStart = _scheduled.Count;
            _cooldownChanges.Clear();
            for (var index = 0; index < facts.Length; index++)
            {
                // Emitters are single-dispatch, so each fact gets its own generation.
                _dispatchGeneration = checked(_dispatchGeneration + 1);
                _factBudgetExceeded = false;
                _invalidFact = false;
                var result = DispatchCoreActive(world, facts[index], beforeEachFact, trace: null);
                factsProcessed += result.FactsProcessed;
                rulesMatched += result.RulesMatched;
                effectsApplied += result.EffectsApplied;
                if (result.CutOff)
                {
                    cutOff = true;
                    cutoffReason ??= result.CutoffReason;
                }

                if (result.RolledBack)
                {
                    rolledBack = true;
                    cutoffReason = result.CutoffReason ?? cutoffReason;
                    break;
                }
            }

            if (rolledBack)
            {
                // The world transaction does not journal engine tables, so the whole batch has to
                // drop the delayed work and cooldown records it added before the failure.
                if (_scheduled.Count > batchScheduledStart)
                {
                    _scheduled.RemoveRange(batchScheduledStart, _scheduled.Count - batchScheduledStart);
                }

                RestoreCooldowns();
                transaction.Rollback();
            }
            else
            {
                transaction.Commit();
            }

            return new RuleDispatchResult(factsProcessed, rulesMatched, effectsApplied, cutOff, cutoffReason, rolledBack);
        }
        finally
        {
            _batchTransaction = null;
            _activeTransaction = null;
            _dispatchActive = false;
        }
    }

    private RuleDispatchResult DispatchCore(
        World world,
        SimulationFact initialFact,
        SimulationFactObserver? beforeEachFact,
        List<RuleTraceFact>? trace)
    {
        if (_dispatchActive)
        {
            throw new InvalidOperationException("RuleEngine does not support nested or concurrent dispatches.");
        }

        _dispatchActive = true;
        _dispatchGeneration = checked(_dispatchGeneration + 1);
        _factBudgetExceeded = false;
        _invalidFact = false;

        try
        {
            return DispatchCoreActive(world, initialFact, beforeEachFact, trace);
        }
        finally
        {
            _dispatchActive = false;
            _activeTransaction = null;
        }
    }

    private RuleDispatchResult DispatchCoreActive(
        World world,
        SimulationFact initialFact,
        SimulationFactObserver? beforeEachFact,
        List<RuleTraceFact>? trace)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentException.ThrowIfNullOrWhiteSpace(initialFact.Type);
        if (initialFact.Depth is < 0 || initialFact.Depth > _maxDepth)
        {
            throw new ArgumentOutOfRangeException(nameof(initialFact), initialFact.Depth,
                $"Initial fact depth must be between 0 and the configured maximum of {_maxDepth}.");
        }

        if (!initialFact.HasValidAddress)
        {
            throw new ArgumentOutOfRangeException(nameof(initialFact),
                "A fact must name both a source and a target entity, or exactly one live population row.");
        }

        if (!double.IsFinite(initialFact.Amount))
        {
            throw new ArgumentOutOfRangeException(nameof(initialFact), initialFact.Amount,
                "Fact amount must be finite.");
        }

        EnsureBound(world);

        // A batch owns the transaction, so one scope covers every fact it dispatches.
        using var transaction = _batchTransaction is { } batch ? batch.AsBatchMember() : world.BeginTransaction();
        _activeTransaction = transaction;
        EnsureEventHistoryIndex();
        _eventQueryCache.Clear();
        _pendingEventHistory.Clear();
        ClearPendingEventHistoryIndex();
        var prunedEventHistory = PruneEventHistory(world.LogicalTick);
        if (prunedEventHistory.Length > 0)
        {
            RegisterPrunedHistoryRollback(transaction, prunedEventHistory);
        }

        _queue.Clear();
        _queue.Add(initialFact);
        var scheduledCount = _scheduled.Count;
        if (_batchTransaction is null)
        {
            _cooldownChanges.Clear();
        }

        var factsProcessed = 0;
        var rulesMatched = 0;
        var effectsApplied = 0;
        var cutOff = false;
        string? cutoffReason = null;
        var emitter = new SimulationFactEmitter(this, _dispatchGeneration);
        // Built-in effects can change entities, but cannot replace IDs; observers still get a fresh cache per fact.
        var resolvedEntities = new FactEntityCache();
        string? cachedEventType = null;
        var cachedEventRules = ImmutableArray<CompiledRule>.Empty;
        for (var readIndex = 0; readIndex < _queue.Count && !cutOff; readIndex++)
        {
            var fact = _queue[readIndex];
            if (fact.Depth > _maxDepth)
            {
                cutOff = true;
                cutoffReason = "reaction_depth";
                break;
            }

            factsProcessed++;
            if (_rules.MaximumEventWindowTicks > 0 && _eventTypeSet.Contains(fact.Type))
            {
                AddPendingEventSample(new EventSample(world.LogicalTick, fact.Type, fact.Amount));
            }

            List<string>? matchedRuleIds = trace is null ? null : [];
            beforeEachFact?.Invoke(fact, emitter);
            if (beforeEachFact is not null)
            {
                resolvedEntities = default;
            }

            if (_invalidFact)
            {
                trace?.Add(new RuleTraceFact(factsProcessed - 1, fact, []));
                cutOff = true;
                cutoffReason = "invalid_fact";
                break;
            }

            if (_factBudgetExceeded)
            {
                trace?.Add(new RuleTraceFact(factsProcessed - 1, fact, []));
                cutOff = true;
                cutoffReason = "fact_budget";
                break;
            }

            if (!ReferenceEquals(cachedEventType, fact.Type))
            {
                cachedEventType = fact.Type;
                cachedEventRules = _rules.ForEvent(fact.Type);
            }

            foreach (var rule in cachedEventRules)
            {
                if (!Matches(world, fact, rule, ref resolvedEntities))
                {
                    continue;
                }

                if (rule.BundleId is { } bundleId)
                {
                    // A bundle is a named group that a tag enables. A timed grant that expires removes
                    // the tag, so the bundle disables itself without a second rule to turn it off.
                    var owner = resolvedEntities.Resolve(world, fact, RuleSubject.Source);
                    if (!_rules.Bundles.TryGetValue(bundleId, out var enabledByTag)
                        || owner is null || !world.HasTag(owner, enabledByTag))
                    {
                        continue;
                    }
                }

                if (!TryEnterCooldown(world, rule, fact))
                {
                    continue;
                }

                rulesMatched++;
                matchedRuleIds?.Add(rule.Id);
                foreach (var effect in rule.Effects)
                {
                    if (effectsApplied == _maxEffects)
                    {
                        cutOff = true;
                        cutoffReason = "effect_budget";
                        break;
                    }

                    effectsApplied++;
                    Apply(world, fact, rule.Id, effect, ref resolvedEntities, ref cutOff, ref cutoffReason);
                    if (_factBudgetExceeded)
                    {
                        cutOff = true;
                        cutoffReason = "fact_budget";
                    }
                    else if (_invalidFact)
                    {
                        cutOff = true;
                        cutoffReason = "invalid_fact";
                    }

                    if (cutOff)
                    {
                        break;
                    }
                }

                if (cutOff)
                {
                    break;
                }
            }

            trace?.Add(new RuleTraceFact(factsProcessed - 1, fact,
                    matchedRuleIds is null ? [] : [.. matchedRuleIds]));
        }

        var rolledBack = cutoffReason is "non_finite_property" or "invalid_fact";
        if (!rolledBack && _eventHistory.Count + _pendingEventHistory.Count > MaximumEventHistoryFacts)
        {
            cutOff = true;
            cutoffReason = "event_history_budget";
            rolledBack = true;
        }

        if (rolledBack)
        {
            // Delayed facts are engine state, not world state, so a rolled-back dispatch has to drop
            // the ones it queued itself. Otherwise a failed tick would leave work for a later tick.
            if (_scheduled.Count > scheduledCount)
            {
                _scheduled.RemoveRange(scheduledCount, _scheduled.Count - scheduledCount);
            }

            RestoreCooldowns();

            _activeTransaction = null;
            transaction.Rollback();
        }
        else
        {
            if (_pendingEventHistory.Count > 0)
            {
                var firstAppendedIndex = _eventHistory.Count;
                _eventHistory.AddRange(_pendingEventHistory);
                AppendPendingHistoryToCommittedIndex();
                RegisterAppendedHistoryRollback(transaction, firstAppendedIndex);
            }

            _activeTransaction = null;
            transaction.Commit();
        }

        _pendingEventHistory.Clear();
        ClearPendingEventHistoryIndex();
        _eventQueryCache.Clear();

        return new RuleDispatchResult(factsProcessed, rulesMatched, effectsApplied, cutOff, cutoffReason, rolledBack);
    }

    internal bool TryEnqueue(long generation, SimulationFact fact)
    {
        if (!_dispatchActive || generation != _dispatchGeneration)
        {
            return false;
        }

        return EnqueueFact(fact);
    }

    internal bool TryRegisterCommitCallback(long generation, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!_dispatchActive || generation != _dispatchGeneration || _activeTransaction is not { } transaction)
        {
            return false;
        }

        transaction.OnCommit(action);
        return true;
    }


    private bool EnqueueFact(SimulationFact fact)
    {
        if (string.IsNullOrWhiteSpace(fact.Type) || !fact.HasValidAddress
            || fact.Depth < 0 || !double.IsFinite(fact.Amount))
        {
            _invalidFact = true;
            return false;
        }

        if (_queue.Count >= _maxFacts)
        {
            _factBudgetExceeded = true;
            return false;
        }

        _queue.Add(fact);
        return true;
    }

    private bool Matches(World world, SimulationFact fact, CompiledRule rule, ref FactEntityCache resolvedEntities,
        int scannedSlot = -1)
    {
        foreach (var condition in rule.Conditions)
        {
            if (condition.Kind == RuleConditionKind.Query)
            {
                if (condition.Query is not { } query)
                {
                    throw new InvalidOperationException($"Rule '{rule.Id}' has a query condition without a compiled query.");
                }

                var result = EntityQueryEvaluator.Evaluate(world, query, fact);
                // A selection with nothing to aggregate has no value: a formula must not read it as
                // zero and pass, which would run effects for a query that matched nothing.
                if (!result.HasValue)
                {
                    return false;
                }

                if (query.Formula is { } formula)
                {
                    if (!formula.EvaluateBoolean(new QueryExpressionScope(world, fact, this, result)))
                    {
                        return false;
                    }
                }
                else if (!result.HasValue
                    || !EntityQueryEvaluator.Compare(result.Value, query.Threshold, query.Comparison))
                {
                    return false;
                }

                continue;
            }

            if (condition.Kind == RuleConditionKind.Expression)
            {
                var scope = new WorldExpressionScope(world, fact, this);
                if (!condition.Formula!.EvaluateBoolean(scope))
                {
                    return false;
                }

                continue;
            }

            if (condition.Kind == RuleConditionKind.Chance)
            {
                if (world.Draws.NextFloat() >= condition.Threshold)
                {
                    return false;
                }

                continue;
            }

            if (condition.Subject.AddressesRow)
            {
                int slot;
                if (scannedSlot >= 0)
                {
                    slot = scannedSlot;
                }
                else if (!TryResolveRowSlot(world, fact, condition.Subject, out slot))
                {
                    return false;
                }

                var binding = ResolveRowBinding(world, rule.Id, condition.Subject, condition.Name,
                    condition.Kind == RuleConditionKind.HasTag ? RowTargetKind.Tag : RowTargetKind.Column,
                    fact.Type);
                if (!RowMatches(binding, condition, slot))
                {
                    return false;
                }

                continue;
            }

            var entity = resolvedEntities.Resolve(world, fact, condition.Subject);
            if (entity is null)
            {
                return false;
            }

            if (condition.Kind == RuleConditionKind.HasTag)
            {
                if (!world.HasTag(entity, condition.Name))
                {
                    return false;
                }
            }
            else if (!entity.TryGetProperty(world, condition.Name, out var value)
                || value < condition.Threshold)
            {
                return false;
            }
        }

        return true;
    }

    internal double EventCount(string eventType, int windowTicks, int currentTick)
    {
        var key = new EventQueryKey(EventQueryKind.Count, eventType, windowTicks);
        if (_eventQueryCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var firstTick = currentTick - windowTicks + 1;
        var count = 0;
        if (_eventHistoryByType.TryGetValue(eventType, out var committedSamples))
        {
            foreach (var sample in committedSamples)
            {
                if (sample.Tick >= firstTick)
                {
                    count++;
                }
            }
        }

        if (_pendingEventHistoryByType.TryGetValue(eventType, out var pendingSamples))
        {
            foreach (var sample in pendingSamples)
            {
                if (sample.Tick >= firstTick)
                {
                    count++;
                }
            }
        }

        var result = (double)count;
        _eventQueryCache.Add(key, result);
        return result;
    }

    internal double EventRate(string eventType, int windowTicks, int currentTick)
    {
        var key = new EventQueryKey(EventQueryKind.Rate, eventType, windowTicks);
        if (_eventQueryCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var firstTick = currentTick - windowTicks + 1;
        var amount = 0.0;
        if (_eventHistoryByType.TryGetValue(eventType, out var committedSamples))
        {
            foreach (var sample in committedSamples)
            {
                if (sample.Tick >= firstTick)
                {
                    amount += sample.Amount;
                }
            }
        }

        if (_pendingEventHistoryByType.TryGetValue(eventType, out var pendingSamples))
        {
            foreach (var sample in pendingSamples)
            {
                if (sample.Tick >= firstTick)
                {
                    amount += sample.Amount;
                }
            }
        }

        var result = amount * _tickRateHz / windowTicks;
        _eventQueryCache.Add(key, result);
        return result;
    }

    private EventSample[] PruneEventHistory(int currentTick)
    {
        if (_rules.MaximumEventWindowTicks == 0 || _eventHistory.Count == 0)
        {
            return [];
        }

        var firstTick = currentTick - _rules.MaximumEventWindowTicks + 1;
        var write = 0;
        List<EventSample>? removed = null;
        for (var read = 0; read < _eventHistory.Count; read++)
        {
            var sample = _eventHistory[read];
            if (sample.Tick >= firstTick)
            {
                _eventHistory[write++] = sample;
            }
            else
            {
                (removed ??= []).Add(sample);
            }
        }

        if (removed is not null)
        {
            _eventHistory.RemoveRange(write, _eventHistory.Count - write);
            PruneEventHistoryIndex(firstTick);
            return [.. removed];
        }

        return [];
    }

    private void RegisterPrunedHistoryRollback(WorldTransaction transaction, EventSample[] prunedHistory) =>
        transaction.OnRollback(() =>
        {
            _eventHistory.InsertRange(0, prunedHistory);
            _eventHistoryIndexDirty = true;
        });

    private void RegisterAppendedHistoryRollback(WorldTransaction transaction, int firstAppendedIndex) =>
        transaction.OnRollback(() =>
        {
            _eventHistory.RemoveRange(firstAppendedIndex, _eventHistory.Count - firstAppendedIndex);
            _eventHistoryIndexDirty = true;
        });

    private void EnsureEventHistoryIndex()
    {
        if (!_eventHistoryIndexDirty)
        {
            return;
        }

        _eventHistoryByType.Clear();
        foreach (var sample in _eventHistory)
        {
            GetOrAddSamples(_eventHistoryByType, sample.EventType).Add(sample);
        }

        _eventHistoryIndexDirty = false;
    }

    private void AddPendingEventSample(EventSample sample)
    {
        _pendingEventHistory.Add(sample);
        GetOrAddSamples(_pendingEventHistoryByType, sample.EventType).Add(sample);
        _eventQueryCache.Clear();
    }

    private void AppendPendingHistoryToCommittedIndex()
    {
        foreach (var sample in _pendingEventHistory)
        {
            GetOrAddSamples(_eventHistoryByType, sample.EventType).Add(sample);
        }
    }

    private void ClearPendingEventHistoryIndex()
    {
        foreach (var samples in _pendingEventHistoryByType.Values)
        {
            samples.Clear();
        }
    }

    private void PruneEventHistoryIndex(int firstTick)
    {
        foreach (var samples in _eventHistoryByType.Values)
        {
            var write = 0;
            for (var read = 0; read < samples.Count; read++)
            {
                var sample = samples[read];
                if (sample.Tick >= firstTick)
                {
                    samples[write++] = sample;
                }
            }

            if (write < samples.Count)
            {
                samples.RemoveRange(write, samples.Count - write);
            }
        }
    }

    private static List<EventSample> GetOrAddSamples(
        Dictionary<string, List<EventSample>> samplesByType,
        string eventType)
    {
        if (!samplesByType.TryGetValue(eventType, out var samples))
        {
            samples = [];
            samplesByType.Add(eventType, samples);
        }

        return samples;
    }

    private enum EventQueryKind
    {
        Count,
        Rate,
    }

    private readonly record struct EventQueryKey(EventQueryKind Kind, string EventType, int WindowTicks);

    private void Apply(
        World world,
        SimulationFact fact,
        string ruleId,
        RuleEffect effect,
        ref FactEntityCache resolvedEntities,
        ref bool cutOff,
        ref string? cutoffReason,
        int scannedSlot = -1)
    {
        if (effect.Kind == RuleEffectKind.EmitEvent)
        {
            if (fact.Depth >= _maxDepth)
            {
                cutOff = true;
                cutoffReason = "reaction_depth";
                return;
            }

            // The row reference travels with the reaction, so a later fact still names the same row
            // and can detect that the row despawned in between.
            EnqueueFact(new SimulationFact(effect.Name, fact.SourceId, fact.TargetId, fact.Depth + 1,
                fact.Amount, fact.Position, effect.Payload ?? fact.Payload, fact.Row));
            return;
        }

        if (effect.Subject.AddressesRow)
        {
            int slot;
            if (scannedSlot >= 0)
            {
                slot = scannedSlot;
            }
            else if (!TryResolveRowSlot(world, fact, effect.Subject, out slot))
            {
                return;
            }

            var kind = effect.Kind switch
            {
                RuleEffectKind.AddTag => RowTargetKind.Tag,
                RuleEffectKind.SetModifier => RowTargetKind.Modifier,
                _ => RowTargetKind.Column,
            };
            var binding = ResolveRowBinding(world, ruleId, effect.Subject, effect.Name, kind, fact.Type);
            // A slot past the live row count is a despawned row: like a missing entity, the effect
            // is accepted by a matched rule and has nowhere to land.
            ApplyRowEffect(binding, effect, slot);
            return;
        }

        var entity = resolvedEntities.Resolve(world, fact, effect.Subject);
        if (entity is null)
        {
            return;
        }

        switch (effect.Kind)
        {
            case RuleEffectKind.AdjustProperty:
                ApplyQuantity(world, entity, effect, ref cutOff, ref cutoffReason);
                break;
            case RuleEffectKind.SetModifier:
                if (effect.SourceProperty is { } sourceProperty)
                {
                    world.SetModifierFromProperty(ruleId, entity, effect.Name, effect.Operation, effect.Value,
                        sourceProperty, effect.DurationTicks, 0, effect.Stage);
                }
                else
                {
                    world.SetModifier(ruleId, entity, effect.Name, effect.Operation, effect.Value,
                        effect.DurationTicks, stage: effect.Stage);
                }
                if (!double.IsFinite(world.EffectiveProperty(entity, effect.Name)))
                {
                    cutOff = true;
                    cutoffReason = "non_finite_property";
                }

                break;
            case RuleEffectKind.AddTag:
                entity.AddTag(effect.Name);
                break;
            case RuleEffectKind.RemoveTag:
                entity.RemoveTag(effect.Name);
                break;
            case RuleEffectKind.Despawn:
                if (world.ById(entity.Id) is not null)
                {
                    world.Remove(entity);
                }

                break;
            case RuleEffectKind.Transfer:
                ApplyTransfer(world, fact, entity, effect, ref resolvedEntities, ref cutOff, ref cutoffReason);
                break;
            case RuleEffectKind.Spawn:
                ApplySpawn(world, fact, entity, effect);
                break;
            case RuleEffectKind.Schedule:
                ApplySchedule(world, fact, effect);
                break;
            case RuleEffectKind.InvokeAction:
                var actor = resolvedEntities.Resolve(world, fact, effect.Subject);
                var invokedTarget = resolvedEntities.Resolve(world, fact, RuleSubject.Target);
                if (_rules.Actions.TryGetValue(effect.Name, out var handler))
                {
                    handler(new RuleActionContext(world, fact, actor, invokedTarget, effect));
                }

                break;
        }
    }

    private static void ApplyQuantity(World world, Entity entity, RuleEffect effect,
        ref bool cutOff, ref string? cutoffReason)
    {
        if (effect.Quantity == QuantityOperation.Add && entity is DataEntity dataEntity
            && dataEntity.TryAdjustProperty(world, effect.Name, effect.Value, out var dataUpdated))
        {
            if (!double.IsFinite(dataUpdated))
            {
                cutOff = true;
                cutoffReason = "non_finite_property";
            }

            return;
        }

        if (!entity.TryGetProperty(world, effect.Name, out var current))
        {
            return;
        }

        var updated = effect.Quantity switch
        {
            QuantityOperation.Add => current + effect.Value,
            QuantityOperation.Set => effect.Value,
            QuantityOperation.Multiply => current * effect.Value,
            QuantityOperation.Min => Math.Min(current, effect.Value),
            QuantityOperation.Max => Math.Max(current, effect.Value),
            _ => current,
        };

        if (!double.IsFinite(updated))
        {
            cutOff = true;
            cutoffReason = "non_finite_property";
            return;
        }

        entity.TrySetProperty(world, effect.Name, updated);
    }

    private static void ApplyTransfer(World world, SimulationFact fact, Entity source, RuleEffect effect,
        ref FactEntityCache resolvedEntities, ref bool cutOff, ref string? cutoffReason)
    {
        if (effect.Destination is not { } destinationSubject)
        {
            return;
        }

        var destination = resolvedEntities.Resolve(world, fact, destinationSubject);
        if (destination is null || ReferenceEquals(destination, source)
            || !source.TryGetProperty(world, effect.Name, out var available))
        {
            return;
        }

        var targetProperty = effect.TargetProperty ?? effect.Name;
        if (!destination.TryGetProperty(world, targetProperty, out var held))
        {
            return;
        }

        // A transfer moves what is there and no more, so content never has to guard the amount.
        var moved = Math.Min(effect.Value, Math.Max(available, 0));
        if (moved <= 0)
        {
            return;
        }

        var received = held + moved;
        var remaining = available - moved;
        if (!double.IsFinite(received) || !double.IsFinite(remaining))
        {
            cutOff = true;
            cutoffReason = "non_finite_property";
            return;
        }

        // Write the destination first. A resource entity exposes amount for reading but declines
        // writes, so writing the source first would destroy quantity the transfer could not deliver.
        if (!destination.TrySetProperty(world, targetProperty, received))
        {
            return;
        }

        if (!source.TrySetProperty(world, effect.Name, remaining))
        {
            destination.TrySetProperty(world, targetProperty, held);
        }
    }

    private static void ApplySpawn(World world, SimulationFact fact, Entity? origin, RuleEffect effect)
    {
        if (effect.Template is not { } template)
        {
            return;
        }

        var properties = template.Properties
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => (pair.Key, pair.Value))
            .ToArray();
        var spawned = world.Add(new DataEntity(template.Tags, template.Capabilities, properties));
        if (template.SpatialBounds is not { } bounds)
        {
            return;
        }

        var centerX = bounds.X;
        var centerY = bounds.Y;
        if (fact.Position is { } position)
        {
            centerX = position.X;
            centerY = position.Y;
        }
        else if (origin is not null && world.TryGetSpatialBounds(origin.Id, out var originBounds))
        {
            centerX = originBounds.X;
            centerY = originBounds.Y;
        }

        world.SetSpatialBounds(spawned, new Bounds2(centerX, centerY, bounds.HalfWidth, bounds.HalfHeight));
    }

    private void ApplySchedule(World world, SimulationFact fact, RuleEffect effect)
    {
        var scheduled = new SimulationFact(effect.Name, fact.SourceId, fact.TargetId, 0, fact.Amount,
            fact.Position, effect.Payload ?? fact.Payload, fact.Row);
        if (effect.DelayTicks <= 0)
        {
            EnqueueFact(scheduled with { Depth = fact.Depth + 1 });
            return;
        }

        // A delayed fact schedules from its own due tick, so a fast-forward that dispatches a chain in
        // one call lands every step at the same tick it would have reached online.
        if (_scheduled.Count >= MaximumScheduledFacts)
        {
            // A rule that schedules more work than the queue holds must not grow it without bound.
            return;
        }

        _scheduled.Add(new ScheduledFactEntry((long)world.LogicalTick + effect.DelayTicks,
            _scheduledSequence++, scheduled));
    }

    /// <summary>Dispatches the scheduled facts whose delay has elapsed at the world's logical tick.</summary>
    /// <param name="world">World whose logical tick decides which facts are due.</param>
    /// <returns>The number of facts dispatched.</returns>
    /// <remarks>
    /// Facts run oldest due tick first, then in the order they were scheduled, so a save that replays
    /// the same ticks produces the same order. A due fact is a new root: its depth restarts at zero.
    /// </remarks>
    public int DispatchDue(World world, SimulationFactObserver? beforeEachFact = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (_dispatchActive || _scheduled.Count == 0)
        {
            return 0;
        }

        var dispatched = 0;
        while (dispatched < MaximumDueFactsPerCall)
        {
            var index = -1;
            for (var candidate = 0; candidate < _scheduled.Count; candidate++)
            {
                var entry = _scheduled[candidate];
                if (entry.DueTick > world.LogicalTick)
                {
                    continue;
                }

                if (index < 0 || entry.DueTick < _scheduled[index].DueTick
                    || (entry.DueTick == _scheduled[index].DueTick
                        && entry.Sequence < _scheduled[index].Sequence))
                {
                    index = candidate;
                }
            }

            if (index < 0)
            {
                return dispatched;
            }

            var due = _scheduled[index];
            _scheduled.RemoveAt(index);
            dispatched++;
            // Run every fact at its own due tick: expressions, event windows and modifier expiry read
            // World.LogicalTick, so a fast-forward must rewind the clock to each fact's tick.
            var previousTick = world.LogicalTick;
            world.LogicalTick = (int)Math.Min(due.DueTick, int.MaxValue);
            try
            {
                Dispatch(world, due.Fact, beforeEachFact);
            }
            finally
            {
                world.LogicalTick = previousTick;
            }
        }

        return dispatched;
    }

    private readonly record struct ScheduledFactEntry(long DueTick, long Sequence, SimulationFact Fact);

    private readonly record struct CooldownChange((string Rule, int Source, int Target) Key, int Previous,
        bool HadPrevious);

    private bool TryEnterCooldown(World world, CompiledRule rule, SimulationFact fact)
    {
        if (rule.CooldownTicks <= 0)
        {
            return true;
        }

        var key = (rule.Id, fact.SourceId, fact.TargetId);
        var tick = world.LogicalTick;
        if (_lastRuleFire.TryGetValue(key, out var expiry))
        {
            _cooldownChanges.Add(new CooldownChange(key, expiry, true));
            if (expiry > tick)
            {
                return false;
            }
        }
        else
        {
            // At capacity, expire finished windows; if none can go, the rule still runs but this pair
            // is not remembered. Clearing the table would let an active cooldown fire again.
            if (_lastRuleFire.Count >= MaximumCooldownEntries)
            {
                PruneExpiredCooldowns(tick);
                if (_lastRuleFire.Count >= MaximumCooldownEntries)
                {
                    return true;
                }
            }

            _cooldownChanges.Add(new CooldownChange(key, 0, false));
        }

        _lastRuleFire[key] = (int)Math.Min((long)tick + rule.CooldownTicks, int.MaxValue);
        return true;
    }

    private void PruneExpiredCooldowns(int tick)
    {
        List<(string Rule, int Source, int Target)>? expired = null;
        foreach (var (key, expiry) in _lastRuleFire)
        {
            if (expiry <= tick)
            {
                (expired ??= []).Add(key);
            }
        }

        if (expired is null)
        {
            return;
        }

        foreach (var key in expired)
        {
            _lastRuleFire.Remove(key);
        }
    }

    private void RestoreCooldowns()
    {
        for (var index = _cooldownChanges.Count - 1; index >= 0; index--)
        {
            var change = _cooldownChanges[index];
            if (change.HadPrevious)
            {
                _lastRuleFire[change.Key] = change.Previous;
            }
            else
            {
                _lastRuleFire.Remove(change.Key);
            }
        }

        _cooldownChanges.Clear();
    }

    private readonly record struct EventSample(int Tick, string EventType, double Amount);

    private struct FactEntityCache
    {
        private Entity? _source;
        private Entity? _target;
        private int _sourceId;
        private int _targetId;
        private bool _sourceResolved;
        private bool _targetResolved;

        public Entity? Resolve(World world, SimulationFact fact, RuleSubject subject)
        {
            if (subject.Kind == RuleSubjectKind.Entity)
            {
                return world.ById(subject.Slot);
            }

            if (subject == RuleSubject.Source)
            {
                if (!_sourceResolved || _sourceId != fact.SourceId)
                {
                    _source = world.ById(fact.SourceId);
                    _sourceId = fact.SourceId;
                    _sourceResolved = true;
                }

                return _source;
            }

            if (!_targetResolved || _targetId != fact.TargetId)
            {
                _target = world.ById(fact.TargetId);
                _targetId = fact.TargetId;
                _targetResolved = true;
            }

            return _target;
        }
    }
}
