using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Systemic.Host;

namespace GlowGarden;

/// <summary>Refreshes a short system timing report at most once per simulation second.</summary>
public sealed class GardenTickDiagnostics
{
    private const int MaximumDisplayedSystems = 8;
    private const int MaximumDisplayedWaves = 8;
    private const int MaximumDisplayedSystemsPerWave = 8;
    private int _lastSampleTick = -1;
    private string _cachedText = "No system ticks measured.";

    public string GetText(int tick, int tickRateHz, Func<IReadOnlyList<SystemTickStats>> sample)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tickRateHz);
        ArgumentNullException.ThrowIfNull(sample);
        if (_lastSampleTick < 0 || tick < _lastSampleTick || (long)tick - _lastSampleTick >= tickRateHz)
        {
            _cachedText = Format(sample());
            _lastSampleTick = tick;
        }

        return _cachedText;
    }

    public static string FormatWaves(ImmutableArray<ImmutableArray<string>> waves)
    {
        if (waves.IsDefaultOrEmpty) return "Waves: none";
        var text = new StringBuilder("Waves: ");
        var displayedWaves = Math.Min(MaximumDisplayedWaves, waves.Length);
        for (var index = 0; index < displayedWaves; index++)
        {
            if (index > 0) text.Append("  |  ");
            text.Append(index).Append(": ");
            var displayedSystems = Math.Min(MaximumDisplayedSystemsPerWave, waves[index].Length);
            for (var systemIndex = 0; systemIndex < displayedSystems; systemIndex++)
            {
                if (systemIndex > 0) text.Append(", ");
                text.Append(waves[index][systemIndex]);
            }
            if (waves[index].Length > displayedSystems)
            {
                text.Append(", +").Append(waves[index].Length - displayedSystems);
            }
        }

        if (waves.Length > displayedWaves)
        {
            text.Append("  |  +").Append(waves.Length - displayedWaves).Append(" waves");
        }

        return text.ToString();
    }

    private static string Format(IReadOnlyList<SystemTickStats> stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        if (stats.Count == 0) return "No system ticks measured.";
        var text = new StringBuilder("System ms: ");
        var count = Math.Min(MaximumDisplayedSystems, stats.Count);
        for (var index = 0; index < count; index++)
        {
            if (index > 0) text.Append("  |  ");
            var item = stats[index];
            text.Append(item.SystemId)
                .Append(" last ").Append(Milliseconds(item.Last))
                .Append(" mean ").Append(Milliseconds(item.Mean))
                .Append(" p95 ").Append(Milliseconds(item.P95))
                .Append(" max ").Append(Milliseconds(item.Max))
                .Append(" ").Append(item.AllocatedBytes.ToString("N0", CultureInfo.InvariantCulture)).Append(" B")
                .Append(" budget exceeded ").Append(item.BudgetExceededCount.ToString(CultureInfo.InvariantCulture));
        }

        if (stats.Count > count)
        {
            text.Append("  |  +").Append(stats.Count - count).Append(" systems");
        }

        return text.ToString();
    }

    private static string Milliseconds(TimeSpan elapsed) =>
        elapsed.TotalMilliseconds.ToString("0.00", CultureInfo.InvariantCulture);
}
