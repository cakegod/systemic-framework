namespace Systemic.Host;

public readonly record struct FixedStepResult(int Steps, TimeSpan DroppedTime, TimeSpan RemainingTime);

/// <summary>Engine-neutral fixed-step accumulator with an explicit catch-up bound.</summary>
public sealed class FixedStepClock
{
    private readonly double _stepSeconds;
    private readonly int _maxCatchUpSteps;
    private double _accumulatorSeconds;

    public FixedStepClock(int tickRateHz, int maxCatchUpSteps)
    {
        if (tickRateHz is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(tickRateHz));
        }

        if (maxCatchUpSteps is < 1 or > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCatchUpSteps));
        }

        TickRateHz = tickRateHz;
        _maxCatchUpSteps = maxCatchUpSteps;
        _stepSeconds = 1.0 / tickRateHz;
    }

    public int TickRateHz { get; }

    public FixedStepResult Advance(TimeSpan elapsed, Action step)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);

        _accumulatorSeconds += elapsed.TotalSeconds;
        var steps = 0;
        while (_accumulatorSeconds >= _stepSeconds && steps < _maxCatchUpSteps)
        {
            step();
            _accumulatorSeconds -= _stepSeconds;
            steps++;
        }

        var droppedSeconds = 0.0;
        if (_accumulatorSeconds >= _stepSeconds)
        {
            var skippedSteps = Math.Floor(_accumulatorSeconds / _stepSeconds);
            droppedSeconds = skippedSteps * _stepSeconds;
            _accumulatorSeconds -= droppedSeconds;
        }

        return new FixedStepResult(steps, TimeSpan.FromSeconds(droppedSeconds),
            TimeSpan.FromSeconds(_accumulatorSeconds));
    }
}
