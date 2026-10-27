namespace Systemic.Kernel;

public enum CommandDisposition
{
    Applied,
    NoChange,
    Rejected,
}

public readonly record struct CommandExecutionResult(CommandDisposition Disposition, string? Reason = null)
{
    public bool Accepted => Disposition != CommandDisposition.Rejected;
}

/// <summary>Dispatches typed events and commits their effects atomically.</summary>
public sealed class EventEngine
{
    public const int MaxDepth = EngineLimits.MaxDepth;
    public const int MaxEffectsPerCommand = EngineLimits.MaxEffectsPerCommand;
    public const int MaxDiagnosticRecordsPerCommand = EngineLimits.MaxDiagnosticRecordsPerCommand;
    public const int MaxRetainedDiagnostics = EngineLimits.MaxRetainedDiagnostics;
    public const int MaxRetainedCutoffs = EngineLimits.MaxRetainedCutoffs;

    private readonly List<IKernelTrigger> _triggers;
    private readonly Dictionary<Type, IKernelTrigger[]> _triggersByType;
    private readonly int _maxEffectsPerCommand;
    private readonly Queue<KernelEvent> _pending = new();
    private readonly List<DiagnosticRecord> _diagnostics = [];
    private readonly List<CutoffRecord> _cutoffs = [];
    private readonly List<CommittedAction> _committedActions = [];
    private readonly List<ReactionOutcome> _reactionOutcomes = [];
    private readonly List<CommittedReaction> _committedReactions = [];
    private readonly List<ProgressFact> _progressFacts = [];
    private readonly Dictionary<Type, Stack<TriggerContext>> _freeContexts = [];
    private readonly Dictionary<Type, Stack<ActionContext>> _freeActions = [];
    private readonly List<KernelEvent[]> _pendingSnapshots = [];
    private readonly Stack<int[]> _freeTargetBuffers = new();
    private int _branchDepth;
    private KernelEvent? _current;
    private string? _activeRule;
    private int _chainId;
    private long _serial;
    private int _triggerApplicationsThisCommand;
    private int _batchWorkItemsThisCommand;
    private int _diagnosticRecordsThisCommand;
    private bool _accepted;
    private bool _pendingOverflow;
    private bool _pendingOverflowReported;
    private KernelEvent? _pendingOverflowEvent;

    public EventEngine(IEnumerable<IKernelTrigger> triggers,
        int maxEffectsPerCommand = MaxEffectsPerCommand)
    {
        _triggers = [.. triggers
            .OrderByDescending(trigger => trigger.Priority)
            .ThenBy(trigger => trigger.Id, StringComparer.Ordinal)];
        _triggersByType = _triggers
            .GroupBy(trigger => trigger.EventType)
            .ToDictionary(group => group.Key, group => group.ToArray());
        _maxEffectsPerCommand = maxEffectsPerCommand;
        Validate();
    }

    public IReadOnlyList<IKernelTrigger> Triggers => _triggers;
    public IReadOnlyList<DiagnosticRecord> Diagnostics => _diagnostics;
    public IReadOnlyList<CutoffRecord> Cutoffs => _cutoffs;
    public IReadOnlyList<CommittedAction> CommittedActions => _committedActions;
    public IReadOnlyList<ReactionOutcome> ReactionOutcomes => _reactionOutcomes;
    public IReadOnlyList<CommittedReaction> CommittedReactions => _committedReactions;
    public IReadOnlyList<ProgressFact> ProgressFacts => _progressFacts;
    public string? RejectionReason { get; private set; }
    public CommandDisposition LastDisposition { get; private set; } = CommandDisposition.NoChange;

    private void Validate()
    {
        var identities = new HashSet<(Type EventType, string Id)>();
        foreach (var trigger in _triggers)
        {
            if (string.IsNullOrWhiteSpace(trigger.Id))
            {
                throw new ArgumentException("A rule needs a non-empty ID.");
            }

            if (!trigger.HasExactlyOneBody)
            {
                throw new ArgumentException($"Rule '{trigger.Id}' must define exactly one effect or action.");
            }

            if (!identities.Add((trigger.EventType, trigger.Id)))
            {
                throw new ArgumentException(
                    $"Rule '{trigger.Id}' is registered twice for {trigger.EventType.Name}.");
            }
        }
    }

    public void BeginCommand()
    {
        RejectionReason = null;
        LastDisposition = CommandDisposition.NoChange;
        _pending.Clear();
        _committedActions.Clear();
        _reactionOutcomes.Clear();
        _committedReactions.Clear();
        _progressFacts.Clear();
        _triggerApplicationsThisCommand = 0;
        _batchWorkItemsThisCommand = 0;
        _diagnosticRecordsThisCommand = 0;
        _pendingOverflow = false;
        _pendingOverflowReported = false;
        _pendingOverflowEvent = null;
    }

    /// <summary>Dispatches a typed event snapshot without mutating the caller's event.</summary>
    public bool Execute(World world, KernelEvent domainEvent) => ExecuteDetailed(world, domainEvent).Accepted;

    public CommandExecutionResult ExecuteDetailed(World world, KernelEvent domainEvent)
        => ExecuteDetailedCore(world, domainEvent, reuseRootEvent: false);

    /// <summary>
    /// Dispatches a caller-owned root event without cloning it to assign dispatch metadata.
    /// The event is mutated with its assigned event ID and causality. Do not share it across
    /// concurrent dispatches or rely on it as an immutable input snapshot after dispatch.
    /// </summary>
    public bool ExecuteReusable(World world, KernelEvent domainEvent) =>
        ExecuteReusableDetailed(world, domainEvent).Accepted;

    /// <summary>Dispatches a reusable event and returns the command result.</summary>
    public CommandExecutionResult ExecuteReusableDetailed(World world, KernelEvent domainEvent) =>
        ExecuteDetailedCore(world, domainEvent, reuseRootEvent: true);

    /// <summary>Gets the number of event IDs assigned so far.</summary>
    internal long Serial => _serial;

    /// <summary>Gets the number of command chains dispatched so far.</summary>
    internal int ChainId => _chainId;

    /// <summary>Restores the counters captured by <see cref="Serial" /> and <see cref="ChainId" />.</summary>
    internal void RestoreCounters(long serial, int chainId)
    {
        _serial = serial;
        _chainId = chainId;
    }

    private CommandExecutionResult ExecuteDetailedCore(World world, KernelEvent domainEvent, bool reuseRootEvent)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(domainEvent);
        BeginCommand();
        var publication = CapturePublication();
        // The engine's own scope restores publication state when its command fails, but an enclosing
        // rollback must also rewind the counters so a retried rule sees the same event ID.
        world.JournalEventCounters(this);
        var transaction = world.BeginScope();
        try
        {
            if (!RunCommand(world, domainEvent, reuseRootEvent))
            {
                var reason = RejectionReason ?? "rejected";
                transaction.Rollback();
                RestorePublication(publication);
                _accepted = false;
                RejectionReason = reason;
                LastDisposition = CommandDisposition.Rejected;

                return new(LastDisposition, reason);
            }

            try
            {
                if (world.HasPendingRemovals)
                {
                    world.PruneRemoved();
                }
            }
            catch (InvalidOperationException error)
            {
                transaction.Rollback();
                RestorePublication(publication);
                _accepted = false;
                RejectionReason = error.Message;
                LastDisposition = CommandDisposition.Rejected;

                return new(LastDisposition, error.Message);
            }

            var changed = transaction.HasChanges;
            transaction.Commit();
            TrimDiagnostics();
            LastDisposition = changed ? CommandDisposition.Applied : CommandDisposition.NoChange;

            return new(LastDisposition);
        }
        catch
        {
            transaction.Rollback();
            RestorePublication(publication);

            throw;
        }
    }

    public bool TryExecute(World world, KernelEvent domainEvent, out World next)
    {
        BeginCommand();
        var publication = CapturePublication();
        world.JournalEventCounters(this);
        var working = world.Clone();
        var transaction = working.BeginScope();
        try
        {
            if (!RunCommand(working, domainEvent))
            {
                var reason = RejectionReason ?? "rejected";
                transaction.Rollback();
                RestorePublication(publication);
                _accepted = false;
                RejectionReason = reason;
                LastDisposition = CommandDisposition.Rejected;
                next = world;

                return false;
            }

            try
            {
                working.PruneRemoved();
            }
            catch (InvalidOperationException error)
            {
                transaction.Rollback();
                RestorePublication(publication);
                _accepted = false;
                RejectionReason = error.Message;
                LastDisposition = CommandDisposition.Rejected;
                next = world;

                return false;
            }

            var changed = transaction.HasChanges;
            transaction.Commit();
            TrimDiagnostics();
            LastDisposition = changed ? CommandDisposition.Applied : CommandDisposition.NoChange;
            next = working;

            return true;
        }
        catch
        {
            transaction.Rollback();
            RestorePublication(publication);

            throw;
        }
    }

    internal void EmitTopLevel(World world, KernelEvent domainEvent) => Execute(world, domainEvent);

    public void Emit(World world, KernelEvent domainEvent) => Enqueue(Child(domainEvent));

    public void EmitNow(World world, KernelEvent domainEvent)
    {
        Enqueue(Child(domainEvent));
        Drain(world);
    }

    private bool RunCommand(World world, KernelEvent domainEvent, bool reuseRootEvent = false)
    {
        _accepted = true;
        _chainId++;
        var diagnosticMark = _diagnostics.Count;
        var cutoffMark = _cutoffs.Count;
        var causality = domainEvent.Causality with { ChainId = _chainId, Depth = 0 };
        if (reuseRootEvent)
        {
            domainEvent.SetDispatchMetadata(++_serial, causality);
            Enqueue(domainEvent);
        }
        else
        {
            Enqueue(domainEvent with { EventId = ++_serial, Causality = causality });
        }
        Drain(world);

        if (!_accepted)
        {
            RejectionReason ??= "rejected";
            Truncate(diagnosticMark, cutoffMark);
            _committedActions.Clear();
            _reactionOutcomes.Clear();
            _committedReactions.Clear();
            _progressFacts.Clear();
        }

        return _accepted;
    }

    private PublicationState CapturePublication() => new(
        _committedActions.Count,
        _reactionOutcomes.Count,
        _committedReactions.Count,
        _progressFacts.Count,
        _diagnostics.Count,
        _cutoffs.Count,
        _serial,
        _current,
        _activeRule,
        _accepted,
        _triggerApplicationsThisCommand,
        _diagnosticRecordsThisCommand,
        _chainId,
        RejectionReason,
        _pendingOverflow,
        _pendingOverflowReported,
        _pendingOverflowEvent);

    private void RestorePublication(PublicationState state)
    {
        TrimTo(_committedActions, state.CommittedActions);
        TrimTo(_reactionOutcomes, state.ReactionOutcomes);
        TrimTo(_committedReactions, state.CommittedReactions);
        TrimTo(_progressFacts, state.ProgressFacts);
        TrimTo(_diagnostics, state.Diagnostics);
        TrimTo(_cutoffs, state.Cutoffs);
        _serial = state.Serial;
        _current = state.Current;
        _activeRule = state.ActiveRule;
        _accepted = state.Accepted;
        _triggerApplicationsThisCommand = state.TriggerApplications;
        _diagnosticRecordsThisCommand = state.DiagnosticRecords;
        _chainId = state.ChainId;
        RejectionReason = state.RejectionReason;
        _pendingOverflow = state.PendingOverflow;
        _pendingOverflowReported = state.PendingOverflowReported;
        _pendingOverflowEvent = state.PendingOverflowEvent;
    }

    private static void TrimTo<T>(List<T> list, int count)
    {
        if (list.Count > count)
        {
            list.RemoveRange(count, list.Count - count);
        }
    }

    private readonly record struct PublicationState(
        int CommittedActions,
        int ReactionOutcomes,
        int CommittedReactions,
        int ProgressFacts,
        int Diagnostics,
        int Cutoffs,
        long Serial,
        KernelEvent? Current,
        string? ActiveRule,
        bool Accepted,
        int TriggerApplications,
        int DiagnosticRecords,
        int ChainId,
        string? RejectionReason,
        bool PendingOverflow,
        bool PendingOverflowReported,
        KernelEvent? PendingOverflowEvent);

    public void Record(World world, string ruleId, int sourceId, int targetId, double cost, string result)
    {
        if (_diagnosticRecordsThisCommand >= MaxDiagnosticRecordsPerCommand)
        {
            return;
        }

        _diagnostics.Add(new DiagnosticRecord(
            ruleId,
            sourceId,
            targetId,
            cost,
            result,
            world.LogicalTick,
            _current?.EventId ?? 0,
            _current?.Causality.ChainId ?? _chainId));
        _diagnosticRecordsThisCommand++;
    }

    public void Reject(string? reason = null)
    {
        _accepted = false;
        RejectionReason ??= reason ?? "rejected";
    }

    internal void RecordRejectedReaction(World world, string rule, KernelEvent domainEvent, string? reason = null)
    {
        reason ??= $"{rule} rejected";
        _reactionOutcomes.Add(new ReactionOutcome(rule, ActionCause(domainEvent), false, reason));
        Record(world, rule, domainEvent.SourceId, domainEvent.TargetId, 0, "reaction rejected");
    }

    public void CommitProgress(int sourceId, int delta, int newValue) =>
        _progressFacts.Add(new ProgressFact(++_serial, _current?.EventId ?? 0, sourceId, delta, newValue));

    private static long ActionCause(KernelEvent domainEvent) =>
        domainEvent.Causality.RootActionId != 0
            ? domainEvent.Causality.RootActionId
            : domainEvent.Causality.ParentEventId;

    private TEvent Child<TEvent>(TEvent domainEvent)
        where TEvent : KernelEvent
    {
        var parent = _current;
        var cause = domainEvent.Causality;
        return domainEvent with
        {
            EventId = ++_serial,
            Causality = cause with
            {
                RootActionId = cause.RootActionId != 0 ? cause.RootActionId
                    : parent is null ? 0 : ActionCause(parent),
                ParentEventId = parent?.EventId ?? cause.ParentEventId,
                ChainId = parent?.Causality.ChainId ?? _chainId,
                Depth = parent is null ? cause.Depth : parent.Causality.Depth + 1,
            },
        };
    }

    private void Enqueue(KernelEvent domainEvent)
    {
        if (IsReaction(domainEvent) && _pending.Count >= _maxEffectsPerCommand)
        {
            _pendingOverflow = true;
            _pendingOverflowEvent ??= domainEvent;
            return;
        }

        _pending.Enqueue(domainEvent);
    }

    private void Drain(World world)
    {
        while (_pending.Count > 0)
        {
            var domainEvent = _pending.Dequeue();
            if (_pendingOverflow && !_pendingOverflowReported)
            {
                Cutoff(world, _pendingOverflowEvent ?? domainEvent, "pending_budget");
                _pendingOverflowReported = true;
                _pendingOverflowEvent = null;
            }

            if (domainEvent.Causality.Depth > MaxDepth)
            {
                Cutoff(world, domainEvent, "depth");
                continue;
            }

            if (IsReaction(domainEvent) && BudgetSpent())
            {
                Cutoff(world, domainEvent, "budget");
                continue;
            }

            ApplyEvent(world, domainEvent);
        }

        if (_pendingOverflow && !_pendingOverflowReported && _pendingOverflowEvent is { } overflow)
        {
            Cutoff(world, overflow, "pending_budget");
            _pendingOverflowReported = true;
            _pendingOverflowEvent = null;
        }
    }

    private void ApplyEvent(World world, KernelEvent domainEvent)
    {
        if (!_triggersByType.TryGetValue(domainEvent.GetType(), out var triggers))
        {
            return;
        }

        foreach (var trigger in triggers)
        {
            if (trigger is IBatchKernelTrigger batchTrigger)
            {
                int[]? batchTargetBuffer = null;
                try
                {
                    var targets = ResolveBatchTargets(world, batchTrigger, out batchTargetBuffer);
                    if (!_accepted)
                    {
                        _pending.Clear();

                        return;
                    }

                    if (targets.IsEmpty)
                    {
                        continue;
                    }

                    var parent = _current;
                    var parentRule = _activeRule;
                    _current = domainEvent;
                    _activeRule = trigger.Id;
                    try
                    {
                        batchTrigger.InvokeBatch(this, world, domainEvent, targets);
                    }
                    finally
                    {
                        _current = parent;
                        _activeRule = parentRule;
                    }

                    if (!_accepted && domainEvent.Causality.Depth == 0)
                    {
                        _pending.Clear();

                        return;
                    }
                }
                finally
                {
                    if (batchTargetBuffer is not null)
                    {
                        _freeTargetBuffers.Push(batchTargetBuffer);
                    }
                }

                Drain(world);

                continue;
            }

            int[]? targetBuffer = null;
            try
            {
                var targets = ResolveTargets(world, trigger, domainEvent, out targetBuffer);
                for (var index = 0; index < targets.Count; index++)
                {
                    var candidate = targets.Ids.IsEmpty
                        ? targets.Single
                        : world.ById(targets.Ids.Span[index]);
                    var target = candidate is null ? null : world.ById(candidate.Id);
                    if (target is not null && !trigger.Matches(world, target))
                    {
                        continue;
                    }

                    var nested = IsReaction(domainEvent);
                    if (nested && BudgetSpent())
                    {
                        Cutoff(world, domainEvent, "budget", trigger.Id);
                        return;
                    }

                    if (nested)
                    {
                        _triggerApplicationsThisCommand++;
                    }

                    var parent = _current;
                    var parentRule = _activeRule;
                    _current = domainEvent;
                    _activeRule = trigger.Id;
                    try
                    {
                        trigger.Invoke(this, world, domainEvent, target);
                        if (_current is { } current
                            && current.GetType() == domainEvent.GetType()
                            && (current.EventId != domainEvent.EventId || current.Causality != domainEvent.Causality))
                        {
                            domainEvent = domainEvent with
                            {
                                EventId = current.EventId,
                                Causality = current.Causality,
                            };
                        }
                    }
                    finally
                    {
                        _current = parent;
                        _activeRule = parentRule;
                    }

                    if (!_accepted && domainEvent.Causality.Depth == 0)
                    {
                        _pending.Clear();
                        return;
                    }
                }
            }
            finally
            {
                if (targetBuffer is not null)
                {
                    _freeTargetBuffers.Push(targetBuffer);
                }
            }

            Drain(world);
        }
    }

    private EntityQuerySnapshot ResolveBatchTargets(
        World world,
        IBatchKernelTrigger trigger,
        out int[]? targetBuffer)
    {
        targetBuffer = null;
        var query = trigger.Query;
        var candidates = world.AllTaggedCandidates(query.Tags, query.Capabilities);
        if (!TryChargeBatchWork(candidates.Length))
        {
            return default;
        }

        if (query.Cacheable && world.TryGetQuerySnapshot(query, out var cached))
        {
            return cached;
        }

        if (candidates.IsEmpty)
        {
            return default;
        }

        var ids = query.Cacheable ? new int[candidates.Length] : RentTargetBuffer(candidates.Length);
        var entities = query.Cacheable ? new Entity[candidates.Length] : [];
        if (!query.Cacheable)
        {
            targetBuffer = ids;
        }

        var count = 0;
        foreach (var candidateId in candidates.Span)
        {
            if (world.ById(candidateId) is { } candidate && query.Matches(world, candidate))
            {
                ids[count] = candidateId;
                if (query.Cacheable)
                {
                    entities[count] = candidate;
                }
                count++;
            }
        }

        var snapshot = new EntityQuerySnapshot(ids.AsMemory(0, count),
            query.Cacheable ? entities.AsMemory(0, count) : default,
            query.Cacheable ? world.StructuralVersion : -1);
        if (query.Cacheable)
        {
            world.CacheQuerySnapshot(query, snapshot);
        }
        return snapshot;
    }

    internal void Invoke<TEvent>(Trigger<TEvent> trigger, World world, TEvent domainEvent, Entity? target)
        where TEvent : KernelEvent
    {
        if (trigger.Action is { } action)
        {
            ApplyAction(world, domainEvent, action);
        }
        else
        {
            ApplyDelegate(world, domainEvent, target, trigger);
        }
    }

    internal void InvokeBatch<TEvent, TEntity>(
        BatchTrigger<TEvent, TEntity> trigger,
        World world,
        TEvent domainEvent,
        EntityQuerySnapshot targetIds)
        where TEvent : KernelEvent
        where TEntity : Entity =>
        ApplyBatchDelegate(world, domainEvent, targetIds, trigger);

    internal void ForEachBatchTarget<TEvent, TEntity>(
        BatchContext<TEvent, TEntity> context,
        EntityQuery<TEntity> query,
        EntityQuerySnapshot targetIds,
        Action<TEntity> effect)
        where TEvent : KernelEvent
        where TEntity : Entity
    {
        var nested = IsReaction(context.Event);
        if (context.RecordTargetTraversal()
            && !TryChargeBatchWork(targetIds.Length))
        {
            return;
        }

        for (var index = 0; index < targetIds.Length; index++)
        {
            if (context.Rejected || (!_accepted && context.Event.Causality.Depth == 0))
            {
                return;
            }

            TEntity target;
            if (((IEntityQuery)query).Cacheable && targetIds.StructuralVersion == context.World.StructuralVersion)
            {
                target = (TEntity)targetIds.Entities.Span[index];
            }
            else
            {
                if (context.World.ById(targetIds.Ids.Span[index]) is not TEntity current
                    || !query.Matches(context.World, current))
                {
                    continue;
                }
                target = current;
            }

            if (nested && BudgetSpent())
            {
                Cutoff(context.World, context.Event, "budget", context.Trigger.Id);

                return;
            }

            if (nested)
            {
                _triggerApplicationsThisCommand++;
            }

            effect(target);
        }
    }

    private bool TryChargeBatchWork(int workItems)
    {
        if (workItems > EngineLimits.MaxBatchWorkItems - _batchWorkItemsThisCommand)
        {
            Reject($"Batch work cap exceeded ({EngineLimits.MaxBatchWorkItems} items).");

            return false;
        }

        _batchWorkItemsThisCommand += workItems;

        return true;
    }

    private void ApplyBatchDelegate<TEvent, TEntity>(
        World world,
        TEvent domainEvent,
        EntityQuerySnapshot targetIds,
        BatchTrigger<TEvent, TEntity> trigger)
        where TEvent : KernelEvent
        where TEntity : Entity
    {
        var context = AcquireBatchContext(world, domainEvent, targetIds, trigger);
        try
        {
            if (!trigger.Transactional
                || (domainEvent.Causality.Depth == 0 && _committedActions.Count == 0))
            {
                trigger.Effect(context);

                return;
            }

            using var branch = world.BeginScope();
            var publication = CapturePublication();
            var depth = _branchDepth++;
            var pendingCount = _pending.Count;
            var pending = PendingSnapshot(depth, pendingCount);
            try
            {
                try
                {
                    trigger.Effect(context);
                }
                catch
                {
                    branch.Rollback();
                    RestoreBranch(publication, pending, pendingCount);
                    throw;
                }

                if (!context.Rejected)
                {
                    branch.Commit();

                    return;
                }

                branch.Rollback();
                RestoreBranch(publication, pending, pendingCount);
                RecordRejectedReaction(world, trigger.Id, domainEvent);
            }
            finally
            {
                _branchDepth--;
            }
        }
        finally
        {
            ReleaseBatchContext(context);
        }
    }

    private BatchContext<TEvent, TEntity> AcquireBatchContext<TEvent, TEntity>(
        World world,
        TEvent domainEvent,
        EntityQuerySnapshot targetIds,
        BatchTrigger<TEvent, TEntity> trigger)
        where TEvent : KernelEvent
        where TEntity : Entity
    {
        var type = typeof(BatchContext<TEvent, TEntity>);
        var pool = ContextPool(_freeContexts, type);
        var context = pool.Count > 0
            ? (BatchContext<TEvent, TEntity>)pool.Pop()
            : new BatchContext<TEvent, TEntity>(this);
        context.Reset(world, domainEvent, trigger, trigger.TypedQuery, targetIds);

        return context;
    }

    private void ReleaseBatchContext<TEvent, TEntity>(BatchContext<TEvent, TEntity> context)
        where TEvent : KernelEvent
        where TEntity : Entity =>
        ContextPool(_freeContexts, context.GetType()).Push(context);

    private void ApplyDelegate<TEvent>(World world, TEvent domainEvent, Entity? target, Trigger<TEvent> trigger)
        where TEvent : KernelEvent
    {
        var context = AcquireContext(world, domainEvent, target, trigger);
        try
        {
            if (!trigger.Transactional
                || (domainEvent.Causality.Depth == 0 && _committedActions.Count == 0))
            {
                trigger.Effect!(context);
                return;
            }

            using var branch = world.BeginScope();
            var publication = CapturePublication();
            var depth = _branchDepth++;
            var pendingCount = _pending.Count;
            var pending = PendingSnapshot(depth, pendingCount);
            try
            {
                try
                {
                    trigger.Effect!(context);
                }
                catch
                {
                    branch.Rollback();
                    RestoreBranch(publication, pending, pendingCount);
                    throw;
                }

                if (!context.Rejected)
                {
                    branch.Commit();
                    return;
                }

                branch.Rollback();
                RestoreBranch(publication, pending, pendingCount);
                RecordRejectedReaction(world, trigger.Id, domainEvent);
            }
            finally
            {
                _branchDepth--;
            }
        }
        finally
        {
            ReleaseContext(context);
        }
    }

    private TriggerContext<TEvent> AcquireContext<TEvent>(
        World world, TEvent domainEvent, Entity? target, Trigger<TEvent> trigger)
        where TEvent : KernelEvent
    {
        var pool = ContextPool(_freeContexts, typeof(TriggerContext<TEvent>));
        var context = pool.Count > 0
            ? (TriggerContext<TEvent>)pool.Pop()
            : TriggerContext<TEvent>.Create(this);
        context.Reset(world, domainEvent, target is null ? null : world.ById(target.Id), trigger);
        return context;
    }

    private void ReleaseContext<TEvent>(TriggerContext<TEvent> context) where TEvent : KernelEvent =>
        ContextPool(_freeContexts, typeof(TriggerContext<TEvent>)).Push(context);

    private static Stack<TContext> ContextPool<TContext>(Dictionary<Type, Stack<TContext>> pools, Type key)
    {
        if (!pools.TryGetValue(key, out var pool))
        {
            pool = new Stack<TContext>();
            pools.Add(key, pool);
        }

        return pool;
    }

    private KernelEvent[] PendingSnapshot(int depth, int count)
    {
        if (_pendingSnapshots.Count <= depth)
        {
            _pendingSnapshots.Add([]);
        }

        var buffer = _pendingSnapshots[depth];
        if (buffer.Length < count)
        {
            buffer = new KernelEvent[Math.Max(count, 4)];
            _pendingSnapshots[depth] = buffer;
        }

        _pending.CopyTo(buffer, 0);
        return buffer;
    }

    private void RestoreBranch(PublicationState publication, KernelEvent[] pending, int count)
    {
        RestorePublication(publication);
        _pending.Clear();
        for (var index = 0; index < count; index++)
        {
            _pending.Enqueue(pending[index]);
        }
    }

    public bool PublishBatch<TEvent>(World world, TEvent domainEvent, ActionDefinition definition,
        ActionOutcome outcome, int repetitions, object? detail)
        where TEvent : KernelEvent =>
        ApplyAction(world, Child(domainEvent), definition, outcome, repetitions, detail);

    internal ActionContext BeginBatch(World world, KernelEvent domainEvent, string rule) =>
        BeginBatch<KernelEvent>(world, domainEvent, rule);

    internal ActionContext<TEvent> BeginBatch<TEvent>(World world, TEvent domainEvent, string rule)
        where TEvent : KernelEvent =>
        BeginAction(world, Child(domainEvent), rule, optional: false, resolved: true);

    internal ActionContext BeginAction(World world, KernelEvent domainEvent, string rule, bool optional) =>
        BeginAction<KernelEvent>(world, domainEvent, rule, optional);

    internal ActionContext<TEvent> BeginAction<TEvent>(World world, TEvent domainEvent, string rule, bool optional,
        bool resolved = false)
        where TEvent : KernelEvent
    {
        var pool = ContextPool(_freeActions, typeof(ActionContext<TEvent>));
        var action = pool.Count > 0
            ? (ActionContext<TEvent>)pool.Pop()
            : new ActionContext<TEvent>(this);
        action.Reset(world, domainEvent, rule, ++_serial,
            _maxEffectsPerCommand - _triggerApplicationsThisCommand, optional, resolved);
        return action;
    }

    internal void ReleaseAction(ActionContext action) =>
        ContextPool(_freeActions, action.GetType()).Push(action);

    internal void RejectAction(World world, KernelEvent domainEvent, string rule, bool optional = false)
    {
        var reason = $"{rule} rejected";
        if (!optional && (domainEvent.Ordinary
            || (domainEvent.Causality.Depth == 0 && _committedActions.Count == 0)))
        {
            Reject(reason);
        }
        else
        {
            var rejectedEvent = optional
                ? domainEvent with
                {
                    Causality = domainEvent.Causality with
                    {
                        RootActionId = domainEvent.Causality.RootActionId != 0
                            ? domainEvent.Causality.RootActionId
                            : domainEvent.EventId,
                    },
                }
                : domainEvent;
            RecordRejectedReaction(world, rule, rejectedEvent, reason);
        }

        Record(world, rule, domainEvent.SourceId, domainEvent.TargetId, 0, reason);
    }

    internal void PublishAction(World world, KernelEvent domainEvent, CommittedAction fact, int charged)
    {
        _triggerApplicationsThisCommand += charged;
        _committedActions.Add(fact);
        _current = domainEvent with
        {
            Causality = domainEvent.Causality with { RootActionId = fact.Serial },
        };
        var committed = new CommittedEvent(domainEvent.SourceId, domainEvent.TargetId, fact)
        {
            Causality = _current.Causality,
        };
        Enqueue(Child(committed));
        Record(world, fact.Action, domainEvent.SourceId, domainEvent.TargetId, 0, fact.Outcome.Kind);
    }

    private bool ApplyAction(World world, KernelEvent domainEvent, ActionDefinition definition,
        ActionOutcome? resolved = null, int repetitions = 1, object? detail = null)
    {
        var resolution = ActionPipeline.ResolveInPlace(world,
            new ActionIntent(definition, domainEvent.SourceId, domainEvent.TargetId,
                domainEvent.EventId, domainEvent.Count),
            ++_serial, _maxEffectsPerCommand - _triggerApplicationsThisCommand,
            ordinary: !IsReaction(domainEvent),
            resolved: resolved,
            repetitions: repetitions,
            detail: detail);
        if (!resolution.Accepted)
        {
            RejectAction(world, domainEvent, definition.Id);
            return false;
        }

        var fact = resolution.Fact!;
        PublishAction(world, domainEvent, fact, resolution.ChargeableEffects);
        _reactionOutcomes.AddRange(resolution.Reactions);
        _committedReactions.AddRange(resolution.CommittedReactions);
        foreach (var reaction in resolution.Reactions)
        {
            var cost = resolution.CommittedReactions
                .Where(committed => committed.Reaction == reaction.Reaction)
                .Sum(committed => committed.PaidResources.Sum(payment => payment.Amount));
            Record(world, reaction.Reaction, domainEvent.SourceId, domainEvent.TargetId, cost, reaction.Reason);
        }

        return true;
    }

    private (Entity? Single, ReadOnlyMemory<int> Ids, int Count) ResolveTargets(
        World world, IKernelTrigger trigger, KernelEvent domainEvent, out int[]? targetBuffer)
    {
        targetBuffer = null;
        switch (trigger.Scope)
        {
            case TriggerScope.World:
                return (null, null, 1);
            case TriggerScope.Source:
            case TriggerScope.Target:
                var entity = world.ById(trigger.Scope == TriggerScope.Source
                    ? domainEvent.SourceId : domainEvent.TargetId);
                return entity is not null && trigger.Matches(world, entity)
                    ? (entity, null, 1) : default;
            case TriggerScope.AllTagged:
                var candidates = world.AllTaggedCandidates(trigger.Tags, trigger.Capabilities);
                if (candidates.IsEmpty)
                {
                    return default;
                }

                targetBuffer = RentTargetBuffer(candidates.Length);
                var count = 0;
                foreach (var candidateId in candidates.Span)
                {
                    if (world.ById(candidateId) is { } candidate && trigger.Matches(world, candidate))
                    {
                        targetBuffer[count++] = candidateId;
                    }
                }

                return (null, targetBuffer.AsMemory(0, count), count);
            default:
                return default;
        }
    }

    private int[] RentTargetBuffer(int capacity)
    {
        if (_freeTargetBuffers.Count > 0)
        {
            var buffer = _freeTargetBuffers.Pop();
            if (buffer.Length >= capacity)
            {
                return buffer;
            }
        }

        return new int[capacity];
    }

    private static bool IsReaction(KernelEvent domainEvent) =>
        domainEvent.Causality.Depth > 0 && !domainEvent.Ordinary;

    private bool BudgetSpent() => _triggerApplicationsThisCommand >= _maxEffectsPerCommand;

    private void Cutoff(World world, KernelEvent domainEvent, string reason, string? rule = null) =>
        _cutoffs.Add(new CutoffRecord(
            world.LogicalTick,
            domainEvent.Causality.ChainId,
            reason,
            domainEvent.GetType().Name,
            rule ?? _activeRule ?? string.Empty,
            domainEvent.Causality.ParentEventId,
            domainEvent.SourceId,
            domainEvent.TargetId,
            domainEvent.Causality.Depth,
            _triggerApplicationsThisCommand,
            _maxEffectsPerCommand));

    private void Truncate(int diagnosticMark, int cutoffMark)
    {
        if (_diagnostics.Count > diagnosticMark)
        {
            _diagnostics.RemoveRange(diagnosticMark, _diagnostics.Count - diagnosticMark);
        }

        if (_cutoffs.Count > cutoffMark)
        {
            _cutoffs.RemoveRange(cutoffMark, _cutoffs.Count - cutoffMark);
        }
    }

    private void TrimDiagnostics()
    {
        if (_diagnostics.Count > MaxRetainedDiagnostics)
        {
            _diagnostics.RemoveRange(0, _diagnostics.Count - MaxRetainedDiagnostics);
        }

        if (_cutoffs.Count > MaxRetainedCutoffs)
        {
            _cutoffs.RemoveRange(0, _cutoffs.Count - MaxRetainedCutoffs);
        }
    }
}
