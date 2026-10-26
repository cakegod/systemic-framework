namespace Boids.MonoGame;

public readonly record struct TickBudgetReport(
    bool Passed,
    double MaximumMilliseconds,
    double MeanMilliseconds,
    int OverBudgetTicks,
    long AllocatedBytes)
{
    public static TickBudgetReport Evaluate(
        ReadOnlySpan<double> tickMilliseconds,
        long allocatedBytes,
        double maximumTickMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(allocatedBytes);
        if (tickMilliseconds.IsEmpty)
        {
            throw new ArgumentException("At least one measured tick is required.", nameof(tickMilliseconds));
        }

        if (!double.IsFinite(maximumTickMilliseconds) || maximumTickMilliseconds <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumTickMilliseconds));
        }

        var elapsedMilliseconds = 0.0;
        var maximumMilliseconds = 0.0;
        var overBudgetTicks = 0;
        foreach (var milliseconds in tickMilliseconds)
        {
            if (!double.IsFinite(milliseconds) || milliseconds < 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(tickMilliseconds),
                    "Tick durations must be finite and non-negative.");
            }

            elapsedMilliseconds += milliseconds;
            maximumMilliseconds = Math.Max(maximumMilliseconds, milliseconds);
            if (milliseconds >= maximumTickMilliseconds)
            {
                overBudgetTicks++;
            }
        }

        var meanMilliseconds = elapsedMilliseconds / tickMilliseconds.Length;
        return new TickBudgetReport(overBudgetTicks == 0 && allocatedBytes == 0,
            maximumMilliseconds, meanMilliseconds, overBudgetTicks, allocatedBytes);
    }
}
