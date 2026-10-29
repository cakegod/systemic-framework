using Systemic.Host;
using Systemic.Kernel;
using Xunit;

namespace Systemic.Tests.Host;

public sealed class FixedStepClockStateTests
{
    [Fact]
    public void Snapshot_restores_fractional_phase_and_continues_identically()
    {
        var original = new FixedStepClock(tickRateHz: 60, maxCatchUpSteps: 4);
        var steps = 0;
        Assert.Equal(0, original.Advance(TimeSpan.FromTicks(83_333), () => steps++).Steps);
        var snapshot = original.CaptureState();

        var restored = new FixedStepClock(tickRateHz: 60, maxCatchUpSteps: 4);
        restored.RestoreState(snapshot);
        var restoredSteps = 0;
        var elapsed = TimeSpan.FromTicks(83_334);
        var first = original.Advance(elapsed, () => steps++);
        var second = restored.Advance(elapsed, () => restoredSteps++);

        Assert.Equal(1, steps);
        Assert.Equal(1, restoredSteps);
        Assert.Equal(first, second);
        Assert.Equal(original.CaptureState(), restored.CaptureState());
    }

    [Fact]
    public void Restore_rejects_invalid_or_incompatible_state_without_mutating_clock()
    {
        var clock = new FixedStepClock(tickRateHz: 60, maxCatchUpSteps: 4);
        clock.Advance(TimeSpan.FromTicks(100), () => { });
        var original = clock.CaptureState();

        Assert.Throws<ArgumentOutOfRangeException>(() => clock.RestoreState(original with { PhaseUnits = -1 }));
        Assert.Throws<ArgumentException>(() => clock.RestoreState(original with { TickRateHz = 30 }));
        Assert.Throws<ArgumentException>(() => clock.RestoreState(original with { MaxCatchUpSteps = 2 }));
        Assert.Equal(original, clock.CaptureState());
    }

    [Fact]
    public void Failed_step_preserves_pending_time_for_retry()
    {
        var clock = new FixedStepClock(tickRateHz: 60, maxCatchUpSteps: 4);
        Assert.Throws<InvalidOperationException>(() => clock.Advance(TimeSpan.FromTicks(166_667),
            () => throw new InvalidOperationException("expected")));
        var snapshot = clock.CaptureState();
        var restored = new FixedStepClock(60, 4);
        restored.RestoreState(snapshot);
        var steps = 0;

        Assert.Equal(1, restored.Advance(TimeSpan.Zero, () => steps++).Steps);
        Assert.Equal(1, steps);
    }

    [Fact]
    public void Post_commit_callback_failure_does_not_retry_the_committed_step()
    {
        var clock = new FixedStepClock(60, 4);
        Assert.Throws<WorldCommitCallbackException>(() => clock.Advance(TimeSpan.FromTicks(166_667),
            () => throw new WorldCommitCallbackException([new InvalidOperationException("observer failed")])));

        Assert.Equal(0, clock.Advance(TimeSpan.Zero, () => throw new Exception("committed tick was retried")).Steps);
    }

    [Fact]
    public void Commit_callback_observes_the_committed_tick_already_consumed()
    {
        var clock = new FixedStepClock(1, 4);
        FixedStepClockState observed = default;

        clock.Advance(TimeSpan.FromSeconds(1), () => observed = clock.CaptureState());

        Assert.Equal((Int128)0, observed.PhaseUnits);
    }

    [Fact]
    public void Catch_up_uses_exact_integer_phase_and_reports_dropped_duration()
    {
        var clock = new FixedStepClock(tickRateHz: 60, maxCatchUpSteps: 2);
        var result = clock.Advance(TimeSpan.FromMilliseconds(100), () => { });

        Assert.Equal(2, result.Steps);
        Assert.Equal(TimeSpan.FromTicks(666_667), result.DroppedTime);
        Assert.Equal(TimeSpan.Zero, result.RemainingTime);
        Assert.Equal((Int128)0, clock.CaptureState().PhaseUnits);
    }
}
