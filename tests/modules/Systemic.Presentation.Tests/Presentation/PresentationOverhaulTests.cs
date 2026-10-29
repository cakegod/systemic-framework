using System.Collections.Concurrent;
using Systemic.Kernel;
using Systemic.Presentation;

namespace Systemic.Presentation.Tests;

public sealed class PresentationOverhaulTests
{
    [Fact]
    public void Runtime_can_configure_its_owned_cue_stream()
    {
        var runtime = new PresentationRuntime(new World(1, 1), new PresentationCatalog([], []),
            new PresentationCueStreamOptions
            {
                Capacity = 1,
                MaxAgeTicks = 1,
                OverflowPolicy = PresentationCueOverflowPolicy.DropNewest,
            });
        runtime.Cues.Enqueue(Cue("first"));
        Assert.False(runtime.Cues.TryEnqueue(Cue("second"), out _));
        Assert.Equal(1, runtime.Cues.Expire(1));
        Assert.Equal(1, runtime.Cues.RejectedCount);
        Assert.Equal(0, runtime.Cues.Count);
    }

    [Fact]
    public void Expiry_removes_old_cues_in_queue_order_even_when_ticks_are_out_of_order()
    {
        var events = new List<PresentationCueStreamEvent>();
        var stream = new PresentationCueStream(new PresentationCueStreamOptions
        {
            Capacity = 4,
            MaxAgeTicks = 2,
            Observer = events.Add,
        });
        stream.Enqueue(Cue("removed-a", 0));
        stream.Enqueue(Cue("removed-b", 1));
        stream.Enqueue(Cue("future", 4));
        stream.Enqueue(Cue("expired-d", 1));
        Assert.True(stream.TryDequeue(out _));
        Assert.True(stream.TryDequeue(out _));
        stream.Enqueue(Cue("current", 3));
        stream.Enqueue(Cue("expired-f", 0));

        Assert.Equal(2, stream.Expire(3));
        Assert.Equal(["expired-d", "expired-f"], events.Select(entry => entry.Cue.FactType));
        Assert.All(events, entry => Assert.Equal(PresentationCueStreamEventKind.Expired, entry.Kind));
        Assert.Equal(2, stream.ExpiredCount);
        Assert.True(stream.TryDequeue(out var first));
        Assert.True(stream.TryDequeue(out var second));
        Assert.Equal(["future", "current"], new[] { first.FactType, second.FactType });
        Assert.Equal([3, 5], new[] { first.Sequence, second.Sequence });
        Assert.False(stream.TryDequeue(out _));
    }

    [Fact]
    public void Zero_age_expires_current_cues_but_preserves_future_cues()
    {
        var stream = new PresentationCueStream(new PresentationCueStreamOptions { MaxAgeTicks = 0 });
        stream.Enqueue(Cue("now", 0));
        stream.Enqueue(Cue("future", 1));

        Assert.Equal(1, stream.Expire(0));
        Assert.True(stream.TryDequeue(out var remaining));
        Assert.Equal("future", remaining.FactType);
        Assert.Equal(0, new PresentationCueStream().Expire(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Expire(-1));
    }

    [Fact]
    public void Drop_newest_reports_rejection_and_keeps_the_existing_cue()
    {
        var events = new List<PresentationCueStreamEvent>();
        var stream = new PresentationCueStream(new PresentationCueStreamOptions
        {
            Capacity = 1,
            OverflowPolicy = PresentationCueOverflowPolicy.DropNewest,
            Observer = events.Add,
        });
        Assert.Equal(1, stream.Enqueue(Cue("first")));

        Assert.False(stream.TryEnqueue(Cue("second"), out var attemptedSequence));
        Assert.Equal(2, attemptedSequence);
        Assert.Equal(2, stream.AttemptedCount);
        Assert.Equal(1, stream.AcceptedCount);
        Assert.Equal(1, stream.RejectedCount);
        Assert.Equal(1, stream.DroppedCount);
        Assert.Equal("second", Assert.Single(events).Cue.FactType);
        Assert.True(stream.TryDequeue(out var retained));
        Assert.Equal("first", retained.FactType);
        Assert.Equal(3, stream.Enqueue(Cue("third")));
    }

    [Fact]
    public void Drop_oldest_reports_the_evicted_fact_type()
    {
        var events = new List<PresentationCueStreamEvent>();
        var stream = new PresentationCueStream(new PresentationCueStreamOptions { Capacity = 1, Observer = events.Add });
        stream.Enqueue(Cue("evicted"));
        stream.Enqueue(Cue("incoming"));

        var entry = Assert.Single(events);
        Assert.Equal(PresentationCueStreamEventKind.Dropped, entry.Kind);
        Assert.Equal("evicted", entry.Cue.FactType);
        Assert.Equal(2, stream.AcceptedCount);
        Assert.Equal(0, stream.RejectedCount);
        Assert.True(stream.TryDequeue(out var retained));
        Assert.Equal("incoming", retained.FactType);
    }

    [Fact]
    public void Incoming_fact_type_selects_its_own_copied_overflow_policy()
    {
        var policies = new Dictionary<string, PresentationCueOverflowPolicy>
        {
            ["protected"] = PresentationCueOverflowPolicy.DropNewest,
        };
        var stream = new PresentationCueStream(new PresentationCueStreamOptions
        {
            Capacity = 1,
            OverflowPoliciesByFactType = policies,
        });
        policies["protected"] = PresentationCueOverflowPolicy.DropOldest;
        stream.Enqueue(Cue("first"));

        Assert.False(stream.TryEnqueue(Cue("protected"), out _));
        Assert.True(stream.TryEnqueue(Cue("ordinary"), out _));
        Assert.True(stream.TryDequeue(out var retained));
        Assert.Equal("ordinary", retained.FactType);
    }

    [Fact]
    public void Coalescing_caps_particles_sums_amount_and_preserves_first_cue_identity()
    {
        var events = new List<PresentationCueStreamEvent>();
        var stream = new PresentationCueStream(new PresentationCueStreamOptions
        {
            Capacity = 2,
            OverflowPolicy = PresentationCueOverflowPolicy.Coalesce,
            CoalescedParticleLimit = 5,
            Observer = events.Add,
        });
        stream.Enqueue(Cue("same") with { ParticleCount = 3, Amount = 2, VisualSeed = 9, ReactionDepth = 1 });
        stream.Enqueue(Cue("other"));

        Assert.True(stream.TryEnqueue(Cue("same") with { ParticleCount = 4, Amount = 4, VisualSeed = 11 }, out var sequence));
        Assert.Equal(3, sequence);
        Assert.False(stream.TryEnqueue(Cue("unrelated"), out _));
        Assert.Equal(4, stream.AttemptedCount);
        Assert.Equal(3, stream.AcceptedCount);
        Assert.Equal(1, stream.RejectedCount);
        Assert.Equal(1, stream.CoalescedCount);
        Assert.Equal(PresentationCueStreamEventKind.Coalesced, events[0].Kind);
        Assert.Equal("same", events[0].Cue.FactType);
        Assert.Equal(3, events[0].Cue.Sequence);
        Assert.Equal(5, events[0].RelatedCue!.Value.ParticleCount);
        Assert.Equal(PresentationCueStreamEventKind.Dropped, events[1].Kind);
        Assert.Equal("unrelated", events[1].Cue.FactType);
        Assert.True(stream.TryDequeue(out var combined));
        Assert.Equal(1, combined.Sequence);
        Assert.Equal(5, combined.ParticleCount);
        Assert.Equal(6, combined.Amount);
        Assert.Equal(9UL, combined.VisualSeed);
        Assert.Equal(1, combined.ReactionDepth);
        Assert.True(stream.TryDequeue(out var other));
        Assert.Equal("other", other.FactType);
    }

    [Fact]
    public void Coalescing_requires_every_key_field_to_match()
    {
        var first = Cue("same");
        var differentKeys = new[]
        {
            first with { FactType = "different" },
            first with { TimelineId = "different" },
            first with { Tick = 1 },
            first with { SourceEntityId = 3 },
            first with { TargetEntityId = 3 },
            first with { AnchorPosition = new Point2(4, 4) },
            first with { AnchorPosition = null },
        };
        foreach (var different in differentKeys)
        {
            var stream = new PresentationCueStream(new PresentationCueStreamOptions
            {
                Capacity = 1,
                OverflowPolicy = PresentationCueOverflowPolicy.Coalesce,
            });
            stream.Enqueue(first);
            Assert.False(stream.TryEnqueue(different, out _));
            Assert.Equal(0, stream.CoalescedCount);
            Assert.True(stream.TryDequeue(out var retained));
            Assert.Equal(first with { Sequence = 1 }, retained);
        }
    }

    [Fact]
    public void Coalesced_amount_saturates_to_a_finite_value()
    {
        var stream = new PresentationCueStream(new PresentationCueStreamOptions
        {
            Capacity = 1,
            OverflowPolicy = PresentationCueOverflowPolicy.Coalesce,
        });
        stream.Enqueue(Cue("same") with { Amount = double.MaxValue });
        stream.Enqueue(Cue("same") with { Amount = double.MaxValue });
        Assert.True(stream.TryDequeue(out var combined));
        Assert.Equal(double.MaxValue, combined.Amount);
    }

    [Fact]
    public void Sampling_admits_only_configured_attempts_when_full()
    {
        var stream = new PresentationCueStream(new PresentationCueStreamOptions
        {
            Capacity = 1,
            OverflowPolicy = PresentationCueOverflowPolicy.Sample,
            SampleEvery = 3,
        });
        Assert.True(stream.TryEnqueue(Cue("one"), out _));
        Assert.False(stream.TryEnqueue(Cue("two"), out _));
        Assert.True(stream.TryEnqueue(Cue("three"), out _));
        Assert.False(stream.TryEnqueue(Cue("four"), out _));
        Assert.False(stream.TryEnqueue(Cue("five"), out _));
        Assert.True(stream.TryEnqueue(Cue("six"), out _));
        Assert.Equal(6, stream.AttemptedCount);
        Assert.Equal(3, stream.AcceptedCount);
        Assert.Equal(3, stream.RejectedCount);
        Assert.Equal(5, stream.DroppedCount);
        Assert.True(stream.TryDequeue(out var retained));
        Assert.Equal("six", retained.FactType);
        Assert.Equal(6, retained.Sequence);
    }

    [Fact]
    public void Observer_runs_outside_the_queue_lock_for_coalescing_drops_and_expiry()
    {
        var events = new List<PresentationCueStreamEvent>();
        PresentationCueStream? stream = null;
        stream = new PresentationCueStream(new PresentationCueStreamOptions
        {
            Capacity = 2,
            MaxAgeTicks = 3,
            OverflowPolicy = PresentationCueOverflowPolicy.Coalesce,
            Observer = entry =>
            {
                var accessed = new ManualResetEventSlim();
                var thread = new Thread(() => { _ = stream!.Count; accessed.Set(); }) { IsBackground = true };
                thread.Start();
                Assert.True(accessed.Wait(TimeSpan.FromSeconds(5)));
                events.Add(entry);
            },
        });
        stream.Enqueue(Cue("merge"));
        stream.Enqueue(Cue("new", 2));
        stream.Enqueue(Cue("merge"));
        stream.Enqueue(Cue("drop"));
        Assert.Equal(1, stream.Expire(3));

        Assert.Equal([PresentationCueStreamEventKind.Coalesced, PresentationCueStreamEventKind.Dropped,
            PresentationCueStreamEventKind.Expired], events.Select(entry => entry.Kind));
        Assert.Equal(["merge", "drop", "merge"], events.Select(entry => entry.Cue.FactType));
    }

    [Fact]
    public void Concurrent_producers_assign_unique_sequences_and_preserve_fifo_order()
    {
        const int attempts = 4_000;
        var stream = new PresentationCueStream(attempts);
        var sequences = new ConcurrentBag<long>();
        Parallel.For(0, attempts, _ => sequences.Add(stream.Enqueue(Cue("parallel"))));

        Assert.Equal(attempts, stream.AttemptedCount);
        Assert.Equal(attempts, stream.AcceptedCount);
        Assert.Equal(0, stream.DroppedCount);
        Assert.Equal(Enumerable.Range(1, attempts).Select(i => (long)i), sequences.Order());
        for (var expected = 1; expected <= attempts; expected++)
        {
            Assert.True(stream.TryDequeue(out var cue));
            Assert.Equal(expected, cue.Sequence);
        }

        Assert.False(stream.TryDequeue(out _));
    }

    [Fact]
    public void Concurrent_rejections_do_not_change_the_first_accepted_cues()
    {
        var stream = new PresentationCueStream(new PresentationCueStreamOptions
        {
            Capacity = 100,
            OverflowPolicy = PresentationCueOverflowPolicy.DropNewest,
        });
        Parallel.For(0, 2_000, index => stream.TryEnqueue(Cue("parallel"), out _));

        Assert.Equal(2_000, stream.AttemptedCount);
        Assert.Equal(100, stream.AcceptedCount);
        Assert.Equal(1_900, stream.RejectedCount);
        Assert.Equal(1_900, stream.DroppedCount);
        for (var sequence = 1; sequence <= 100; sequence++)
        {
            Assert.True(stream.TryDequeue(out var cue));
            Assert.Equal(sequence, cue.Sequence);
        }
    }

    [Fact]
    public void Stream_options_reject_invalid_configuration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PresentationCueStream(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PresentationCueStream(new PresentationCueStreamOptions { MaxAgeTicks = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PresentationCueStream(new PresentationCueStreamOptions { CoalescedParticleLimit = 1025 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PresentationCueStream(new PresentationCueStreamOptions { OverflowPolicy = PresentationCueOverflowPolicy.Sample }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PresentationCueStream(new PresentationCueStreamOptions
        {
            OverflowPoliciesByFactType = new Dictionary<string, PresentationCueOverflowPolicy> { ["sample"] = PresentationCueOverflowPolicy.Sample },
            SampleEvery = 1,
        }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PresentationCueStream(new PresentationCueStreamOptions { OverflowPolicy = (PresentationCueOverflowPolicy)99 }));
    }

    [Fact]
    public void Viewport_clamps_camera_and_transforms_and_culls_world_bounds()
    {
        var viewport = new PresentationViewport(new Bounds2(50, 50, 50, 50), new Bounds2(100, 50, 100, 50),
            new Point2(200, 200), 2);
        Assert.Equal(new Point2(50, 75), viewport.CameraCenter);
        Assert.Equal(new Point2(0, 0), viewport.WorldToScreen(new Point2(0, 50)));
        Assert.Equal(new Point2(200, 100), viewport.WorldToScreen(new Point2(100, 100)));
        Assert.Equal(new Point2(50, 75), viewport.ScreenToWorld(new Point2(100, 50)));
        Assert.Equal(new Bounds2(100, 50, 20, 10), viewport.WorldToScreen(new Bounds2(50, 75, 10, 5)));
        Assert.Equal(new Bounds2(50, 75, 10, 5), viewport.ScreenToWorld(new Bounds2(100, 50, 20, 10)));
        Assert.True(viewport.IsVisible(new Bounds2(50, 75, 2, 2)));
        Assert.True(viewport.IsVisible(new Bounds2(50, 49, 1, 1)));
        Assert.False(viewport.IsVisible(new Bounds2(50, 20, 2, 2)));
    }

    [Fact]
    public void Viewport_centers_letterboxing_and_clamps_after_zoom_changes()
    {
        var viewport = new PresentationViewport(new Bounds2(50, 50, 50, 50), new Bounds2(200, 150, 200, 150));
        Assert.Equal(3, viewport.Scale);
        Assert.Equal(new Point2(50, 0), viewport.WorldToScreen(new Point2(0, 0)));
        Assert.Equal(new Point2(350, 300), viewport.WorldToScreen(new Point2(100, 100)));
        var zoomedOut = viewport.WithCamera(new Point2(1000, -1000)).WithZoom(0.5f);
        Assert.Equal(new Point2(50, 50), zoomedOut.CameraCenter);
        Assert.Equal(new Point2(125, 75), zoomedOut.WorldToScreen(new Point2(0, 0)));
        var zoomedIn = viewport.WithZoom(2).WithCamera(new Point2(-10, 200));
        Assert.InRange(zoomedIn.CameraCenter.X, 33.3333f, 33.3334f);
        Assert.Equal(75, zoomedIn.CameraCenter.Y);
        Assert.False(zoomedOut.IsVisible(new Bounds2(101, 50, 0, 0)));
    }

    [Fact]
    public void Viewport_rejects_invalid_bounds_zoom_and_coordinates()
    {
        var world = new Bounds2(50, 50, 50, 50);
        var screen = new Bounds2(100, 50, 100, 50);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PresentationViewport(world with { HalfWidth = 0 }, screen));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PresentationViewport(world, screen, zoom: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PresentationViewport(world, screen, zoom: float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PresentationViewport(world, screen, new Point2(float.NaN, 0)));
        var viewport = new PresentationViewport(world, screen);
        Assert.Throws<ArgumentOutOfRangeException>(() => viewport.WorldToScreen(new Point2(float.PositiveInfinity, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => viewport.IsVisible(new Bounds2(0, 0, -1, 1)));
    }

    private static PresentationCue Cue(string factType, int tick = 0) =>
        new(0, tick, factType, "timeline", 1, 2, new Point2(1, 2), new Point2(3, 4),
            new Point2(3, 4), 123, 3, 0, 1);
}
