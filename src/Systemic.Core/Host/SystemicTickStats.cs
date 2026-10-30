using System.Diagnostics;

namespace Systemic.Host;

/// <summary>Timing and current-thread allocation measurements for one registered system.</summary>
public readonly record struct SystemTickStats(string SystemId, long Count, TimeSpan Last, TimeSpan Mean,
    TimeSpan P95, TimeSpan Max, long AllocatedBytes, long BudgetExceededCount);

public sealed record SystemicTickFailure(string? SystemId, int Tick, bool Committed, Exception Exception);

/// <summary>Distinguishes a failure raised by this tick's commit from a nested world's commit.</summary>
internal static class SystemicTickCommitState
{
    private const string NestedCommitKey = "Systemic.NestedCommit";

    public static void MarkNestedCommit(Exception exception) => exception.Data[NestedCommitKey] = true;

    public static bool IsNestedCommit(Exception exception) => exception.Data[NestedCommitKey] is true;
}

public readonly record struct SystemicTickResult(bool Succeeded, SystemicTickFailure? Failure);

/// <summary>Bounded per-system timing history; worker-thread allocations are not included.</summary>
public sealed class SystemicTickStats
{
    private readonly Dictionary<string, Measurement> _measurements;

    internal SystemicTickStats(IEnumerable<string> systemIds) => _measurements =
        systemIds.ToDictionary(id => id, id => new Measurement(id), StringComparer.Ordinal);

    public void SetBudget(string systemId, TimeSpan? budget)
    {
        if (budget is { } value && value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(budget));
        }

        Get(systemId).SetBudget(budget);
    }

    public IReadOnlyList<SystemTickStats> Snapshot() => _measurements.Values
        .OrderBy(measurement => measurement.Id, StringComparer.Ordinal).Select(measurement => measurement.Snapshot()).ToArray();

    internal Measurement Get(string systemId) => _measurements.TryGetValue(systemId, out var measurement)
        ? measurement : throw new KeyNotFoundException($"Unknown system '{systemId}'.");

    internal sealed class Measurement(string id)
    {
        private readonly object _gate = new();
        private readonly long[] _recent = new long[256];
        private long _count, _last, _max, _allocated, _budgetExceeded;
        private Int128 _total;
        private long? _budget;
        public string Id { get; } = id;

        public void SetBudget(TimeSpan? budget)
        {
            lock (_gate)
            {
                if (budget is not { } value) { _budget = null; return; }
                var ticks = ((Int128)value.Ticks * Stopwatch.Frequency) / TimeSpan.TicksPerSecond;
                _budget = ticks > long.MaxValue ? long.MaxValue : (long)ticks;
            }
        }

        public void Record(long elapsed, long allocated)
        {
            lock (_gate)
            {
                _recent[(int)(_count % _recent.Length)] = elapsed;
                _count++;
                _last = elapsed;
                _total += elapsed;
                _max = Math.Max(_max, elapsed);
                _allocated += Math.Max(0, allocated);
                if (_budget is { } budget && elapsed > budget)
                {
                    _budgetExceeded++;
                }
            }
        }

        public SystemTickStats Snapshot()
        {
            lock (_gate)
            {
                var count = (int)Math.Min(_count, _recent.Length);
                var recent = _recent.AsSpan(0, count).ToArray();
                Array.Sort(recent);
                var p95 = count == 0 ? 0 : recent[(int)Math.Ceiling(count * 0.95) - 1];
                return new(Id, _count, Convert(_last), Convert(_count == 0 ? 0 : (long)(_total / _count)),
                    Convert(p95), Convert(_max), _allocated, _budgetExceeded);
            }
        }

        private static TimeSpan Convert(long stopwatchTicks) => TimeSpan.FromTicks(
            (long)(((Int128)stopwatchTicks * TimeSpan.TicksPerSecond) / Stopwatch.Frequency));
    }
}
