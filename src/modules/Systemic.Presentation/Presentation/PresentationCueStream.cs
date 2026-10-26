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

/// <summary>Thread-safe bounded FIFO. Overflow drops the oldest cue and increments a cumulative counter.</summary>
public sealed class PresentationCueStream
{
    private readonly Lock _gate = new();
    private readonly PresentationCue[] _buffer;
    private int _head;
    private int _count;
    private long _nextSequence;
    private long _droppedCount;

    public PresentationCueStream(int capacity = 1024)
    {
        if (capacity is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Cue capacity must be between 1 and 65,536.");
        }

        _buffer = new PresentationCue[capacity];
    }

    public int Capacity => _buffer.Length;

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

    public long Enqueue(PresentationCue cue)
    {
        if (!TryEnqueue(cue, out var sequence))
        {
            throw new InvalidOperationException("Presentation cue sequence is exhausted.");
        }

        return sequence;
    }

    public bool TryEnqueue(PresentationCue cue, out long sequence)
    {
        Validate(cue);
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
                _buffer[_head] = default;
                _head = (_head + 1) % _buffer.Length;
                _count--;
                if (_droppedCount < long.MaxValue)
                {
                    _droppedCount++;
                }
            }

            var tail = (_head + _count) % _buffer.Length;
            _buffer[tail] = cue;
            _count++;
            return true;
        }
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
