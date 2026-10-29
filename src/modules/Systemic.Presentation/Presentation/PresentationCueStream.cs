using Systemic.Kernel;

namespace Systemic.Presentation;

public readonly record struct PresentationCue(
    long Sequence,
    int Tick,
    string FactType,
    string TimelineId,
    int SourceEntityId,
    int TargetEntityId,
    Point2? SourcePosition,
    Point2? TargetPosition,
    Point2? AnchorPosition,
    ulong VisualSeed,
    int ParticleCount,
    int ReactionDepth,
    double Amount);

public enum PresentationCueOverflowPolicy
{
    /// <summary>Evict the first queued cue and append the incoming cue.</summary>
    DropOldest,
    /// <summary>Reject the incoming cue.</summary>
    DropNewest,
    /// <summary>Merge with the first matching queued cue; reject the incoming cue if none matches.</summary>
    Coalesce,
    /// <summary>On a full stream, admit only every configured sequence and evict the oldest cue.</summary>
    Sample,
}

public enum PresentationCueStreamEventKind
{
    Dropped,
    Expired,
    Coalesced,
}

/// <summary>
/// An affected cue. For coalescing, Cue is the incoming cue and RelatedCue is the updated queued cue.
/// For a drop, Cue is either the rejected incoming cue or the evicted queued cue.
/// </summary>
public readonly record struct PresentationCueStreamEvent(
    PresentationCueStreamEventKind Kind,
    PresentationCue Cue,
    PresentationCue? RelatedCue = null);

public sealed class PresentationCueStreamOptions
{
    /// <summary>The maximum number of queued cues, from 1 through 65,536.</summary>
    public int Capacity { get; init; } = 1024;

    /// <summary>
    /// Maximum age in ticks. Null disables expiry. Call <see cref="PresentationCueStream.Expire"/>
    /// as the simulation clock advances; zero expires cues at their own tick.
    /// </summary>
    public int? MaxAgeTicks { get; init; }

    /// <summary>The default policy applied when the stream is full.</summary>
    public PresentationCueOverflowPolicy OverflowPolicy { get; init; } = PresentationCueOverflowPolicy.DropOldest;

    /// <summary>Maximum combined particle count after a merge, from 0 through 1,024.</summary>
    public int CoalescedParticleLimit { get; init; } = 1024;

    /// <summary>
    /// Required when any policy is Sample and at least two. While full, a cue is admitted when its
    /// stream-wide attempt sequence is divisible by this value; otherwise it is rejected.
    /// </summary>
    public int? SampleEvery { get; init; }

    /// <summary>Overrides selected by the incoming cue's case-sensitive fact type, copied at construction.</summary>
    public IReadOnlyDictionary<string, PresentationCueOverflowPolicy>? OverflowPoliciesByFactType { get; init; }

    /// <summary>
    /// Synchronously observes the affected cue and its fact type after mutation, outside the stream lock.
    /// Observer exceptions propagate after the operation has taken effect.
    /// </summary>
    public Action<PresentationCueStreamEvent>? Observer { get; init; }
}

/// <summary>
/// Thread-safe bounded FIFO. Each validated attempt is assigned a sequence in lock acquisition order;
/// no ordering is guaranteed between concurrent callers beyond that serialized order. The default
/// overflow policy evicts the oldest cue. Observer callbacks execute outside the lock and callbacks from
/// different threads may overlap or arrive in a different order from their associated mutations.
/// </summary>
/// <remarks>
/// Coalescing occurs only on overflow. Its exact key is fact type, timeline, tick, source ID, target ID,
/// and nullable anchor position. The first matching queued cue keeps its sequence, positions, seed and
/// reaction depth. Particle counts sum up to the configured cap; amounts sum and saturate at the finite
/// double limits. No matching cue means the incoming cue is dropped.
/// </remarks>
public sealed class PresentationCueStream
{
    private readonly Lock _gate = new();
    private readonly PresentationCue[] _buffer;
    private int _head;
    private int _count;
    private long _nextSequence;
    private long _droppedCount;
    private long _acceptedCount;
    private long _rejectedCount;
    private long _expiredCount;
    private long _coalescedCount;
    private readonly int? _maxAgeTicks;
    private readonly PresentationCueOverflowPolicy _overflowPolicy;
    private readonly int _coalescedParticleLimit;
    private readonly int? _sampleEvery;
    private readonly Action<PresentationCueStreamEvent>? _observer;
    private readonly Dictionary<string, PresentationCueOverflowPolicy> _typePolicies = new(StringComparer.Ordinal);

    public PresentationCueStream(int capacity = 1024) : this(new PresentationCueStreamOptions { Capacity = capacity })
    {
    }

    public PresentationCueStream(PresentationCueStreamOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var capacity = options.Capacity;
        if (capacity is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Cue capacity must be between 1 and 65,536.");
        }

        if (options.MaxAgeTicks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Maximum cue age must be non-negative.");
        }

        if (options.CoalescedParticleLimit is < 0 or > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The coalesced particle limit must be between 0 and 1,024.");
        }

        if (!Enum.IsDefined(options.OverflowPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The overflow policy is not recognized.");
        }

        if (options.OverflowPoliciesByFactType is not null)
        {
            foreach (var (factType, policy) in options.OverflowPoliciesByFactType)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(factType);
                if (!Enum.IsDefined(policy))
                {
                    throw new ArgumentOutOfRangeException(nameof(options), "A fact type's overflow policy is not recognized.");
                }

                _typePolicies.Add(factType, policy);
            }
        }

        if (options.SampleEvery is < 2 || options.SampleEvery is null
            && (options.OverflowPolicy == PresentationCueOverflowPolicy.Sample
                || _typePolicies.ContainsValue(PresentationCueOverflowPolicy.Sample)))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Sampling requires an explicit interval of at least two attempts.");
        }

        _maxAgeTicks = options.MaxAgeTicks;
        _overflowPolicy = options.OverflowPolicy;
        _coalescedParticleLimit = options.CoalescedParticleLimit;
        _sampleEvery = options.SampleEvery;
        _observer = options.Observer;
        _buffer = new PresentationCue[capacity];
    }

    public int Capacity => _buffer.Length;

    /// <summary>The configured expiry age, or null when expiry is disabled.</summary>
    public int? MaxAgeTicks => _maxAgeTicks;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>Cumulative rejected incoming cues plus queued cues evicted by overflow.</summary>
    public long DroppedCount
    {
        get
        {
            lock (_gate)
            {
                return _droppedCount;
            }
        }
    }

    /// <summary>Validated attempts assigned a sequence; saturates when the sequence is exhausted.</summary>
    public long AttemptedCount
    {
        get { lock (_gate) { return _nextSequence; } }
    }

    /// <summary>Attempts appended to the queue or merged into a queued cue.</summary>
    public long AcceptedCount
    {
        get { lock (_gate) { return _acceptedCount; } }
    }

    /// <summary>Attempts rejected by an overflow policy, excluding sequence exhaustion.</summary>
    public long RejectedCount
    {
        get { lock (_gate) { return _rejectedCount; } }
    }

    /// <summary>Cumulative queued cues removed by expiry.</summary>
    public long ExpiredCount
    {
        get { lock (_gate) { return _expiredCount; } }
    }

    /// <summary>Cumulative incoming cues merged into queued cues.</summary>
    public long CoalescedCount
    {
        get { lock (_gate) { return _coalescedCount; } }
    }

    /// <summary>
    /// Assigns and returns an attempt sequence, including for policy-rejected cues. Use
    /// <see cref="TryEnqueue"/> when the caller needs the acceptance result.
    /// </summary>
    public long Enqueue(PresentationCue cue)
    {
        if (!TryEnqueue(cue, out var sequence) && sequence == 0)
        {
            throw new InvalidOperationException("Presentation cue sequence is exhausted.");
        }

        return sequence;
    }

    /// <summary>
    /// Returns true when appended or coalesced, false when rejected. A policy rejection still receives
    /// a nonzero attempt sequence; sequence zero signals exhaustion. Invalid metadata is rejected before
    /// a sequence is assigned. Observer exceptions propagate after the mutation has completed.
    /// </summary>
    public bool TryEnqueue(PresentationCue cue, out long sequence)
    {
        Validate(cue);
        PresentationCueStreamEvent? notification = null;
        var accepted = true;
        var append = true;
        lock (_gate)
        {
            if (_nextSequence == long.MaxValue)
            {
                sequence = 0;
                return false;
            }

            sequence = ++_nextSequence;
            cue = cue with { Sequence = sequence };
            if (_count == _buffer.Length)
            {
                var policy = _typePolicies.GetValueOrDefault(cue.FactType, _overflowPolicy);
                if (policy == PresentationCueOverflowPolicy.Coalesce && TryCoalesce(cue, out var combined))
                {
                    _acceptedCount++;
                    _coalescedCount++;
                    notification = new(PresentationCueStreamEventKind.Coalesced, cue, combined);
                    append = false;
                }
                else if (policy is PresentationCueOverflowPolicy.DropNewest or PresentationCueOverflowPolicy.Coalesce
                    || policy == PresentationCueOverflowPolicy.Sample && sequence % _sampleEvery!.Value != 0)
                {
                    _droppedCount++;
                    _rejectedCount++;
                    notification = new(PresentationCueStreamEventKind.Dropped, cue);
                    accepted = false;
                    append = false;
                }
                else
                {
                    notification = new(PresentationCueStreamEventKind.Dropped, _buffer[_head]);
                    _buffer[_head] = default;
                    _head = (_head + 1) % _buffer.Length;
                    _count--;
                    _droppedCount++;
                }
            }

            if (append)
            {
                var tail = (_head + _count) % _buffer.Length;
                _buffer[tail] = cue;
                _count++;
                _acceptedCount++;
            }
        }

        if (notification is { } value)
        {
            _observer?.Invoke(value);
        }

        return accepted;
    }

    private bool TryCoalesce(PresentationCue incoming, out PresentationCue combined)
    {
        for (var index = 0; index < _count; index++)
        {
            var slot = (_head + index) % _buffer.Length;
            var queued = _buffer[slot];
            if (queued.FactType != incoming.FactType || queued.TimelineId != incoming.TimelineId
                || queued.Tick != incoming.Tick || queued.SourceEntityId != incoming.SourceEntityId
                || queued.TargetEntityId != incoming.TargetEntityId || queued.AnchorPosition != incoming.AnchorPosition)
            {
                continue;
            }

            var amount = queued.Amount + incoming.Amount;
            if (!double.IsFinite(amount))
            {
                amount = Math.CopySign(double.MaxValue, amount);
            }

            combined = queued with
            {
                ParticleCount = Math.Min(_coalescedParticleLimit, queued.ParticleCount + incoming.ParticleCount),
                Amount = amount,
            };
            _buffer[slot] = combined;
            return true;
        }

        combined = default;
        return false;
    }

    public bool TryDequeue(out PresentationCue cue)
    {
        lock (_gate)
        {
            if (_count == 0)
            {
                cue = default;
                return false;
            }

            cue = _buffer[_head];
            _buffer[_head] = default;
            _head = (_head + 1) % _buffer.Length;
            _count--;
            return true;
        }
    }

    /// <summary>
    /// Removes cues whose age at <paramref name="currentTick"/> meets the configured maximum, even
    /// when tick order differs from enqueue order. Future cues and the relative order of retained cues
    /// are preserved. Notifications from this call follow queue order, outside the stream lock.
    /// </summary>
    public int Expire(int currentTick)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(currentTick);
        if (_maxAgeTicks is not { } maxAgeTicks)
        {
            return 0;
        }

        List<PresentationCue>? expiredCues = _observer is null ? null : [];
        int expired;
        lock (_gate)
        {
            var oldCount = _count;
            var retained = 0;
            for (var index = 0; index < oldCount; index++)
            {
                var cue = _buffer[(_head + index) % _buffer.Length];
                if ((long)currentTick - cue.Tick < maxAgeTicks)
                {
                    _buffer[(_head + retained) % _buffer.Length] = cue;
                    retained++;
                }
                else
                {
                    expiredCues?.Add(cue);
                }
            }

            for (var index = retained; index < oldCount; index++)
            {
                _buffer[(_head + index) % _buffer.Length] = default;
            }

            _count = retained;
            expired = oldCount - retained;
            _expiredCount += expired;
        }

        if (expiredCues is not null)
        {
            foreach (var cue in expiredCues)
            {
                _observer!(new PresentationCueStreamEvent(PresentationCueStreamEventKind.Expired, cue));
            }
        }

        return expired;
    }

    private static void Validate(PresentationCue cue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cue.FactType);
        ArgumentException.ThrowIfNullOrWhiteSpace(cue.TimelineId);
        if (cue.Tick < 0 || cue.SourceEntityId < 0 || cue.TargetEntityId < 0
            || cue.ParticleCount is < 0 or > 1024 || cue.ReactionDepth is < 0 or > 64
            || !double.IsFinite(cue.Amount)
            || !IsFinite(cue.SourcePosition) || !IsFinite(cue.TargetPosition) || !IsFinite(cue.AnchorPosition))
        {
            throw new ArgumentException("Presentation cue metadata must be finite and within supported bounds.", nameof(cue));
        }
    }

    private static bool IsFinite(Point2? point) => point is not { } value
        || float.IsFinite(value.X) && float.IsFinite(value.Y);
}
