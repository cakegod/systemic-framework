using Systemic.Kernel;

namespace Systemic.Host;

public readonly record struct FixedStepResult(int Steps, TimeSpan DroppedTime, TimeSpan RemainingTime);

/// <summary>A portable fixed-step clock configuration and exact pending phase.</summary>
/// <param name="TickRateHz">Fixed simulation steps per second.</param>
/// <param name="MaxCatchUpSteps">Maximum number of steps performed for one elapsed-time update.</param>
/// <param name="PhaseUnits">Pending TimeSpan ticks multiplied by the tick rate.</param>
public readonly record struct FixedStepClockState(int TickRateHz, int MaxCatchUpSteps, Int128 PhaseUnits);

/// <summary>Engine-neutral fixed-step accumulator with an explicit catch-up bound.</summary>
public sealed class FixedStepClock
{
    private readonly int _maxCatchUpSteps;
    private Int128 _phaseUnits;

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
    }

    public int TickRateHz { get; }

    public int MaxCatchUpSteps => _maxCatchUpSteps;

    /// <summary>Captures the exact phase, including pending work after a failed step.</summary>
    public FixedStepClockState CaptureState() => new(TickRateHz, _maxCatchUpSteps, _phaseUnits);

    /// <summary>Restores a state created for the same fixed-step configuration.</summary>
    public void RestoreState(FixedStepClockState state)
    {
        if (state.TickRateHz != TickRateHz || state.MaxCatchUpSteps != _maxCatchUpSteps)
        {
            throw new ArgumentException("Clock state does not match the fixed-step configuration.", nameof(state));
        }

        if (state.PhaseUnits < 0 || state.PhaseUnits > MaximumPhaseUnits)
        {
            throw new ArgumentOutOfRangeException(nameof(state), "Clock phase is outside the supported time range.");
        }

        _phaseUnits = state.PhaseUnits;
    }

    private Int128 MaximumPhaseUnits => (Int128)TimeSpan.MaxValue.Ticks * TickRateHz;

    public FixedStepResult Advance(TimeSpan elapsed, Action step)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);

        // Multiply elapsed ticks by the rate so rates that do not divide one second
        // retain their exact fractional phase without floating-point drift.
        var nextPhase = _phaseUnits + ((Int128)elapsed.Ticks * TickRateHz);
        if (nextPhase > MaximumPhaseUnits)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed), "Pending time exceeds the supported clock range.");
        }

        _phaseUnits = nextPhase;
        var steps = 0;
        while (_phaseUnits >= TimeSpan.TicksPerSecond && steps < _maxCatchUpSteps)
        {
            try { step(); }
            catch (WorldCommitCallbackException)
            {
                // The world is already committed; retrying this tick would run it twice.
                _phaseUnits -= TimeSpan.TicksPerSecond;
                throw;
            }
            _phaseUnits -= TimeSpan.TicksPerSecond;
            steps++;
        }

        var droppedUnits = (_phaseUnits / TimeSpan.TicksPerSecond) * TimeSpan.TicksPerSecond;
        _phaseUnits -= droppedUnits;

        return new FixedStepResult(steps, TimeSpan.FromTicks(ToTimeSpanTicks(droppedUnits)),
            TimeSpan.FromTicks(ToTimeSpanTicks(_phaseUnits)));
    }

    private long ToTimeSpanTicks(Int128 units)
    {
        var whole = units / TickRateHz;
        var remainder = units % TickRateHz;
        // Match TimeSpan's nearest-tick representation while retaining the exact phase internally.
        if ((remainder * 2) > TickRateHz || ((remainder * 2) == TickRateHz && (whole & 1) != 0))
        {
            whole++;
        }

        return checked((long)whole);
    }
}
