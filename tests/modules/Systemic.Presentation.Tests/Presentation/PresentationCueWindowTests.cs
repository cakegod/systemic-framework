using Systemic.Presentation;

namespace Systemic.Tests.Presentation;

public sealed class PresentationCueWindowTests
{
    [Fact]
    public void Pump_expires_old_cues_and_keeps_future_cues_in_order()
    {
        var stream = new PresentationCueStream();
        var window = new PresentationCueWindow(capacity: 4, maxAgeTicks: 3);
        stream.Enqueue(Cue(1));
        stream.Enqueue(Cue(2, tick: 1));
        window.Pump(stream, currentTick: 0);
        stream.Enqueue(Cue(3, tick: 5));

        window.Pump(stream, currentTick: 3);

        Assert.Equal(new long[] { 2, 3 }, window.Cues.Select(cue => cue.Sequence));
        Assert.Equal(1, window.ExpiredCount);
        Assert.Equal(0, window.DroppedCount);
        Assert.Equal(0, stream.Count);
    }

    [Fact]
    public void Pump_bounds_active_cues_by_evicting_the_oldest_and_clear_can_reset_counters()
    {
        var stream = new PresentationCueStream();
        var window = new PresentationCueWindow(capacity: 2, maxAgeTicks: 10);
        stream.Enqueue(Cue(1));
        stream.Enqueue(Cue(2));
        window.Pump(stream, currentTick: 0);
        stream.Enqueue(Cue(3));

        window.Pump(stream, currentTick: 0);

        Assert.Equal(new long[] { 2, 3 }, window.Cues.Select(cue => cue.Sequence));
        Assert.Equal(1, window.DroppedCount);
        window.Clear();
        Assert.Empty(window.Cues);
        Assert.Equal(0, window.DroppedCount);
        Assert.Equal(0, window.ExpiredCount);
    }

    private static PresentationCue Cue(long sequence, int tick = 0) =>
        new(sequence, tick, "test.fact", "test.timeline", 0, 0, null, null, null, 1, 0, 0, 0);
}
