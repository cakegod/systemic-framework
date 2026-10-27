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
    SimulationFactPayload? Payload = null)
{
    public SimulationFact(FactTypeId Type, int SourceId, int TargetId, int Depth = 0, double Amount = 0,
        SimulationFactPosition? Position = null, SimulationFactPayload? Payload = null)
        : this(Type.Value, SourceId, TargetId, Depth, Amount, Position, Payload) { }

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
    private WorldTransaction? _activeTransaction;

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

        if (initialFact.SourceId < 0 || initialFact.TargetId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialFact),
                "Fact source and target IDs must be non-negative.");
        }

        if (!double.IsFinite(initialFact.Amount))
        {
            throw new ArgumentOutOfRangeException(nameof(initialFact), initialFact.Amount,
                "Fact amount must be finite.");
        }

        using var transaction = world.BeginTransaction();
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
            var resolvedEntities = new FactEntityCache();
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

            foreach (var rule in _rules.ForEvent(fact.Type))
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
        if (string.IsNullOrWhiteSpace(fact.Type) || fact.SourceId < 0 || fact.TargetId < 0
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

            EnqueueFact(new SimulationFact(effect.Name, fact.SourceId, fact.TargetId, fact.Depth + 1,
                fact.Amount, fact.Position, fact.Payload));
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
                if (entity.TryGetProperty(world, effect.Name, out var value))
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
        private bool _sourceResolved;
        private bool _targetResolved;

        public Entity? Resolve(World world, SimulationFact fact, RuleSubject subject)
        {
            if (subject == RuleSubject.Source)
            {
                if (!_sourceResolved)
                {
                    _source = world.ById(fact.SourceId);
                    _sourceResolved = true;
                }

                return _source;
            }

            if (!_targetResolved)
            {
                _target = world.ById(fact.TargetId);
                _targetResolved = true;
            }

            return _target;
        }
    }
}
