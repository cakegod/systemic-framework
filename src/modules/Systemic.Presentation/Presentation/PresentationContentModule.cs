using System.Text.Json;
using Systemic.Content;
using Systemic.Host;
using Systemic.Rules;

namespace Systemic.Presentation;

/// <summary>Loads visuals, UI layouts, timelines, and presentation-owned choice icon assignments.</summary>
public sealed class PresentationContentModule : ISystemicContentModule
{
    public const string ModuleId = "systemic.presentation";

    public string Id => ModuleId;

    public ISystemicModuleContent Compile(ContentModuleContext context, JsonElement declaration) =>
        PresentationContentCompiler.Compile(context, declaration);

    public void Configure(SystemicGameBuilder builder, ISystemicModuleContent content)
    {
        var module = (PresentationModuleContent)content;
        builder.AddService(module);
        var runtime = new PresentationRuntime(builder.World, module.Catalog);
        builder.AddService(runtime);
        builder.AddFactObserver(order: 200, runtime.ObserveFact);
    }
}

/// <summary>Typed presentation APIs for games that opt into the Presentation module.</summary>
public static class PresentationGameExtensions
{
    public static PresentationModuleContent PresentationContent(this SystemicGame game) =>
        game.Content.Modules.Select(module => module.Content).OfType<PresentationModuleContent>().SingleOrDefault()
        ?? throw new InvalidOperationException("This content pack has no Presentation module.");

    public static PresentationRuntime Presentation(this SystemicGame game) => game.GetService<PresentationRuntime>();

    public static PresentationCueStream PresentationCues(this SystemicGame game) => game.Presentation().Cues;

    public static PresentationIdentityState PresentationIdentity(this SystemicGame game) => game.Presentation().Identity;

    public static UiNodeState EvaluateUi(
        this SystemicGame game,
        string layoutId,
        IReadOnlyDictionary<string, double>? selection = null,
        SimulationFact? eventFact = null,
        int? choiceOwnerEntityId = null)
    {
        var layout = game.PresentationContent().Ui.Layouts.FirstOrDefault(candidate => candidate.Id == layoutId)
            ?? throw new KeyNotFoundException($"UI layout '{layoutId}' is not part of this content pack.");
        var context = new UiBindingContext(game.World, game.EntityIds, selection, eventFact,
            game.UiBindingProviders, choiceOwnerEntityId);
        return DeclarativeUiEvaluator.Evaluate(layout, context);
    }

    public static long AllocateActorVisualId(this SystemicGame game) => game.Presentation().AllocateActorVisualId();

    public static bool TryGetPresentationTimeline(
        this SystemicGame game,
        string timelineId,
        out PresentationTimeline timeline) => game.Presentation().TryGetTimeline(timelineId, out timeline!);

    public static bool TrySamplePresentation(
        this SystemicGame game,
        PresentationCue cue,
        int ageTicks,
        out PresentationFrame frame)
    {
        if (game.Presentation().TryGetTimeline(cue.TimelineId, out var timeline))
        {
            return PresentationTimelineEvaluator.TrySample(timeline, cue, ageTicks, out frame);
        }

        frame = default;
        return false;
    }
}
