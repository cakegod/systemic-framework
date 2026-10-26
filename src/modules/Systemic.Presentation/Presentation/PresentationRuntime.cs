using System.Text.Json;
using Systemic.Kernel;
using Systemic.Host;
using Systemic.Rules;

namespace Systemic.Presentation;

public sealed class PresentationRuntime : ISystemicStatefulExtension
{
    private readonly World _world;
    private readonly Dictionary<string, PresentationTimeline> _timelines;
    private readonly Dictionary<string, PresentationCueBinding[]> _bindings;

    public PresentationRuntime(World world, PresentationCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(catalog);
        _world = world;
        _timelines = catalog.Timelines.ToDictionary(timeline => timeline.Id, StringComparer.Ordinal);
        _bindings = catalog.Bindings.GroupBy(binding => binding.EventType, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(binding => binding.TimelineId, StringComparer.Ordinal)
                .ThenBy(binding => binding.Anchor).ToArray(), StringComparer.Ordinal);
        Identity = new PresentationIdentityState(unchecked((long)world.Draws.CaptureState().State), 0);
    }

    public string Id => "systemic.presentation.identity";

    public PresentationIdentityState Identity { get; private set; }

    public PresentationCueStream Cues { get; } = new();

    public bool TryGetTimeline(string timelineId, out PresentationTimeline timeline) =>
        _timelines.TryGetValue(timelineId, out timeline!);

    public long AllocateActorVisualId()
    {
        if (Identity.NextActorVisualId == long.MaxValue)
        {
            throw new InvalidOperationException("Actor presentation identity sequence is exhausted.");
        }

        var id = Identity.NextActorVisualId;
        Identity = Identity with { NextActorVisualId = id + 1 };
        return id;
    }

    public void ObserveFact(SimulationFact fact, SimulationFactEmitter emitter)
    {
        if (!_bindings.TryGetValue(fact.Type, out var bindings))
        {
            return;
        }

        var tick = _world.LogicalTick;
        var source = CapturePosition(fact.SourceId);
        var target = CapturePosition(fact.TargetId);
        foreach (var binding in bindings)
        {
            _ = emitter.OnCommit(() => PublishCue(fact, tick, binding, source, target));
        }
    }

    public JsonElement CaptureState() => JsonSerializer.SerializeToElement(Identity);

    public void RestoreState(JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Saved presentation identity state is empty.");
        }

        var restored = state.Deserialize<PresentationIdentityState>();
        if (restored.NextActorVisualId < 0 || restored.NextCueSeedOrdinal < 0)
        {
            throw new InvalidOperationException("Saved presentation identity state has an invalid next identity sequence.");
        }

        Identity = restored;
    }

    private void PublishCue(
        SimulationFact fact,
        int tick,
        PresentationCueBinding binding,
        Point2? source,
        Point2? target)
    {
        var timeline = _timelines[binding.TimelineId];
        var ordinal = Identity.NextCueSeedOrdinal;
        if (ordinal < long.MaxValue)
        {
            Identity = Identity with { NextCueSeedOrdinal = ordinal + 1 };
        }

        var seed = Mix(unchecked((ulong)Identity.VisualRootSeed)
            ^ unchecked((ulong)ordinal)
            ^ StableHash(fact.Type)
            ^ unchecked((ulong)binding.SeedSalt)
            ^ unchecked((ulong)fact.SourceId << 32)
            ^ unchecked((ulong)fact.TargetId)
            ^ unchecked((ulong)tick)
            ^ unchecked((ulong)fact.Depth));
        var cue = new PresentationCue(0, tick, fact.Type, binding.TimelineId,
            fact.SourceId, fact.TargetId, source, target,
            ResolveAnchor(binding.Anchor, source, target), seed, timeline.ParticleCount, fact.Depth, fact.Amount);
        _ = Cues.TryEnqueue(cue, out _);
    }

    private Point2? CapturePosition(int entityId) =>
        _world.TryGetSpatialBounds(entityId, out var bounds) ? new Point2(bounds.X, bounds.Y) : null;

    private static Point2? ResolveAnchor(PresentationAnchor anchor, Point2? source, Point2? target) => anchor switch
    {
        PresentationAnchor.Source => source ?? target,
        PresentationAnchor.Target => target ?? source,
        PresentationAnchor.Midpoint when source is { } sourcePoint && target is { } targetPoint =>
            new Point2((sourcePoint.X + targetPoint.X) / 2, (sourcePoint.Y + targetPoint.Y) / 2),
        PresentationAnchor.Midpoint => source ?? target,
        _ => null,
    };

    private static ulong StableHash(string value)
    {
        var hash = 14695981039346656037UL;
        foreach (var character in value)
        {
            hash = (hash ^ character) * 1099511628211UL;
        }

        return hash;
    }

    private static ulong Mix(ulong value)
    {
        value += 0x9E3779B97F4A7C15UL;
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }
}
