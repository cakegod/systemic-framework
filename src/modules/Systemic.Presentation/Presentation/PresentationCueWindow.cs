using System.Collections.ObjectModel;

namespace Systemic.Presentation;

/// <summary>
/// Bounded consumer-side lifetime window for cues that have left the producer stream and remain
/// active for timeline sampling or rendering.
/// </summary>
public sealed class PresentationCueWindow
{
    private readonly List<PresentationCue> _cues;
    private readonly ReadOnlyCollection<PresentationCue> _view;

    public PresentationCueWindow(int capacity = 512, int maxAgeTicks = 300)
    {
        if (capacity is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAgeTicks);
        Capacity = capacity;
        MaxAgeTicks = maxAgeTicks;
        _cues = new List<PresentationCue>(capacity);
        _view = _cues.AsReadOnly();
    }

    public int Capacity { get; }
    public int MaxAgeTicks { get; }
    public int Count => _cues.Count;
    public IReadOnlyList<PresentationCue> Cues => _view;
    public long DroppedCount { get; private set; }
    public long ExpiredCount { get; private set; }

    /// <summary>Expires old active cues, then transfers every queued cue in FIFO order.</summary>
    public int Pump(PresentationCueStream stream, int currentTick)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(currentTick);
        Expire(currentTick);
        var drained = 0;
        while (stream.TryDequeue(out var cue))
        {
            drained++;
            if ((long)currentTick - cue.Tick >= MaxAgeTicks)
            {
                ExpiredCount++;
                continue;
            }

            if (_cues.Count == Capacity)
            {
                _cues.RemoveAt(0);
                DroppedCount++;
            }

            _cues.Add(cue);
        }

        return drained;
    }

    /// <summary>Removes expired entries while preserving the order of cues that remain active.</summary>
    public int Expire(int currentTick)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(currentTick);
        var expired = 0;
        var write = 0;
        for (var read = 0; read < _cues.Count; read++)
        {
            var cue = _cues[read];
            if ((long)currentTick - cue.Tick >= MaxAgeTicks)
            {
                expired++;
                continue;
            }

            if (write != read)
            {
                _cues[write] = cue;
            }

            write++;
        }

        if (write != _cues.Count)
        {
            _cues.RemoveRange(write, _cues.Count - write);
        }

        ExpiredCount += expired;
        return expired;
    }

    /// <summary>Clears the active list and, by default, its lifetime diagnostics.</summary>
    public void Clear(bool resetCounters = true)
    {
        _cues.Clear();
        if (!resetCounters)
        {
            return;
        }

        DroppedCount = 0;
        ExpiredCount = 0;
    }
}
