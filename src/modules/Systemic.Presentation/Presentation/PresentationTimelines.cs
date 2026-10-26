using System.Collections.Immutable;
using Systemic.Kernel;

namespace Systemic.Presentation;

public enum PresentationAnchor
{
    Source,
    Target,
    Midpoint,
}

public enum ImportedAssetMode
{
    Underlay,
    Overlay,
    Replace,
}

public sealed record PresentationKeyframe(
    int Tick,
    float OffsetX = 0,
    float OffsetY = 0,
    float Scale = 1,
    float Opacity = 1,
    string? VisualState = null);

public sealed record PresentationTimeline(
    string Id,
    string VisualRecipeId,
    int DurationTicks,
    bool Loop,
    int ParticleCount,
    ImmutableArray<PresentationKeyframe> Keyframes);

public sealed record PresentationCueBinding(
    string EventType,
    string TimelineId,
    PresentationAnchor Anchor,
    long SeedSalt = 0);

public sealed record PresentationCatalog(
    ImmutableArray<PresentationTimeline> Timelines,
    ImmutableArray<PresentationCueBinding> Bindings)
{
    public ImmutableArray<PresentationCueBinding> ForEvent(string eventType) =>
        [.. Bindings.Where(binding => string.Equals(binding.EventType, eventType, StringComparison.Ordinal))];
}

public readonly record struct PresentationFrame(Point2 Position, float Scale, float Opacity, string? VisualState);

public static class PresentationTimelineEvaluator
{
    public static bool TrySample(
        PresentationTimeline timeline,
        PresentationCue cue,
        int ageTicks,
        out PresentationFrame frame)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (ageTicks < 0 || timeline.DurationTicks < 1 || timeline.Keyframes.IsDefaultOrEmpty
            || !timeline.Loop && ageTicks >= timeline.DurationTicks)
        {
            frame = default;
            return false;
        }

        var localTick = timeline.Loop ? ageTicks % timeline.DurationTicks : ageTicks;
        var keyframes = timeline.Keyframes;
        var leftIndex = 0;
        while (leftIndex + 1 < keyframes.Length && keyframes[leftIndex + 1].Tick <= localTick)
        {
            leftIndex++;
        }

        var left = keyframes[leftIndex];
        if (leftIndex + 1 == keyframes.Length)
        {
            frame = Compose(left, cue.AnchorPosition);
            return true;
        }

        var right = keyframes[leftIndex + 1];
        var span = right.Tick - left.Tick;
        var amount = span == 0 ? 0 : (localTick - left.Tick) / (float)span;
        frame = new PresentationFrame(
            new Point2(
                AnchorX(cue.AnchorPosition) + Lerp(left.OffsetX, right.OffsetX, amount),
                AnchorY(cue.AnchorPosition) + Lerp(left.OffsetY, right.OffsetY, amount)),
            Lerp(left.Scale, right.Scale, amount),
            Lerp(left.Opacity, right.Opacity, amount),
            left.VisualState);
        return true;
    }

    private static PresentationFrame Compose(PresentationKeyframe keyframe, Point2? anchor) => new(
        new Point2(AnchorX(anchor) + keyframe.OffsetX, AnchorY(anchor) + keyframe.OffsetY),
        keyframe.Scale,
        keyframe.Opacity,
        keyframe.VisualState);

    private static float AnchorX(Point2? anchor) => anchor?.X ?? 0;
    private static float AnchorY(Point2? anchor) => anchor?.Y ?? 0;
    private static float Lerp(float start, float end, float amount) => start + ((end - start) * amount);
}
