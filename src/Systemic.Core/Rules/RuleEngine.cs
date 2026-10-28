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
    private bool _factBudgetExceeded;
    private bool _invalidFact;
    private bool _eventHistoryIndexDirty;
    // Rollback netcode clones the world per tick, so bindings are cached per world: rebinding
    // one world's columns would write into the wrong clone. The cap keeps a hot but bounded set.
    private readonly Dictionary<World, Dictionary<(string Population, string Name, RowTargetKind Kind), RowBinding>>
        _rowBindings = [];
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

                        break;
                    case RuleEffectKind.AddTag:
                        BindTag(world, rowBindings, rule, effect.Subject, effect.Name);
                        break;
                    case RuleEffectKind.SetModifier:
                        BindModifier(world, rowBindings, rule, effect);
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Rule '{rule.Id}' applies {effect.Kind} '{effect.Name}' to {effect.Subject}; "
                            + "row subjects support property adjustments, schema tags and additive modifiers only.");
                }
            }
        }

        if (_rowBindings.Count >= 8)
        {
            _rowBindings.Clear();
        }

        _rowBindings[world] = rowBindings;
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

        rowBindings[(subject.PopulationId, tag, RowTargetKind.Tag)] = RowBinding.ForTag(tags, bit);
    }

    // Populations are usually defined during world initialization, after the game is built, so a row
    // rule set binds against a world on its first dispatch instead of requiring a build-order
    // assumption. A missing column still fails before any effect applies.
    private void EnsureBound(World world)
    {
        if (!_rowBindings.ContainsKey(world))
        {
            Bind(world);
        }
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
            int tagBit, PopulationModifierAccess modifiers, ushort modifierKey)
        {
            Kind = kind;
            Column = column;
            Tags = tags;
            TagBit = tagBit;
            Modifiers = modifiers;
            ModifierKey = modifierKey;
        }

        public RowTargetKind Kind { get; }

        public PopulationColumnBase? Column { get; }

        public PopulationTagAccess Tags { get; }

        public int TagBit { get; }

        public PopulationModifierAccess Modifiers { get; }

        public ushort ModifierKey { get; }

        public bool HasModifierKey => ModifierKey != RowModifierSlot.EmptyKey;

        public static RowBinding ForColumn(PopulationColumnBase column, PopulationModifierAccess modifiers,
            ushort modifierKey) =>
            new(RowTargetKind.Column, column, default, 0, modifiers, modifierKey);

        public static RowBinding ForTag(PopulationTagAccess tags, int tagBit) =>
            new(RowTargetKind.Tag, null, tags, tagBit, default, RowModifierSlot.EmptyKey);

        public static RowBinding ForModifier(PopulationModifierAccess modifiers, ushort modifierKey,
            PopulationColumnBase column) =>
            new(RowTargetKind.Modifier, column, default, 0, modifiers, modifierKey);
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
        using var transaction = world.BeginTransaction();
        _batchTransaction = transaction;
        var factsProcessed = 0;
        var rulesMatched = 0;
        var effectsApplied = 0;
        var cutOff = false;
        string? cutoffReason = null;
        var rolledBack = false;
        try
        {
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
                transaction.Rollback();
            }
            else
            {
                transaction.Commit();
            }
        }
        finally
        {
            _batchTransaction = null;
            _activeTransaction = null;
            _dispatchActive = false;
        }

        return new RuleDispatchResult(factsProcessed, rulesMatched, effectsApplied, cutOff, cutoffReason, rolledBack);
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

    private bool Matches(World world, SimulationFact fact, CompiledRule rule, ref FactEntityCache resolvedEntities)
    {
        foreach (var condition in rule.Conditions)
        {
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
                if (!TryResolveRowSlot(world, fact, condition.Subject, out var slot))
                {
                    return false;
                }

                var binding = ResolveRowBinding(world, rule.Id, condition.Subject, condition.Name,
                    condition.Kind == RuleConditionKind.HasTag ? RowTargetKind.Tag : RowTargetKind.Column,
                    fact.Type);
                if (binding.Kind == RowTargetKind.Tag)
                {
                    if (!binding.Tags.TryHasTagBit(slot, binding.TagBit))
                    {
                        return false;
                    }
                }
                else
                {
                    double rowValue;
                    var read = binding.HasModifierKey
                        ? binding.Modifiers.TryGetEffectiveValue(slot, binding.Column!, binding.ModifierKey, out rowValue)
                        : binding.Column!.TryGetRowValue(slot, out rowValue);
                    if (!read || rowValue < condition.Threshold)
                    {
                        return false;
                    }
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
        ref string? cutoffReason)
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
                fact.Amount, fact.Position, fact.Payload, fact.Row));
            return;
        }

        if (effect.Subject.AddressesRow)
        {
            if (!TryResolveRowSlot(world, fact, effect.Subject, out var slot))
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
            switch (binding.Kind)
            {
                case RowTargetKind.Tag:
                    binding.Tags.TrySetTagBit(slot, binding.TagBit, value: true);
                    break;
                case RowTargetKind.Modifier:
                    binding.Modifiers.TrySet(slot, binding.ModifierKey, effect.Value, effect.DurationTicks);
                    break;
                default:
                    binding.Column!.TryAddRowValue(slot, effect.Value);
                    break;
            }

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
                if (entity is DataEntity dataEntity
                    && dataEntity.TryAdjustProperty(world, effect.Name, effect.Value, out var dataUpdated))
                {
                    if (!double.IsFinite(dataUpdated))
                    {
                        cutOff = true;
                        cutoffReason = "non_finite_property";
                    }
                }
                else if (entity.TryGetProperty(world, effect.Name, out var value))
                {
                    var updated = value + effect.Value;
                    if (!double.IsFinite(updated))
                    {
                        cutOff = true;
                        cutoffReason = "non_finite_property";
                    }
                    else
                    {
                        entity.TrySetProperty(world, effect.Name, updated);
                    }
                }

                break;
            case RuleEffectKind.SetModifier:
                world.SetModifier(ruleId, entity, effect.Name, effect.Operation, effect.Value,
                    effect.DurationTicks, stage: effect.Stage);
                if (!double.IsFinite(world.EffectiveProperty(entity, effect.Name)))
                {
                    cutOff = true;
                    cutoffReason = "non_finite_property";
                }

                break;
            case RuleEffectKind.AddTag:
                entity.AddTag(effect.Name);
                break;
        }
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
