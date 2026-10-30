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

/// <summary>
/// The outcome of invoking an authored UI action. <see cref="Handled"/> is true when the action
/// was declared by the layout and published; <see cref="RolledBack"/> mirrors the dispatch result.
/// </summary>
public sealed record UiActionResult(string LayoutId, string ActionId, bool Handled, bool RolledBack);

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

    /// <summary>
    /// Publishes a UI action as one fact, so a click travels the same deterministic dispatch path
    /// as any other event. The action must be declared by the layout it is invoked on, and the
    /// owning entity is required so a rule authored on the action id has a subject.
    /// </summary>
    /// <remarks>
    /// <see cref="UiActionResult.Handled"/> is true only when at least one rule matched, so a layout
    /// that declares an action no rule listens for reports <c>false</c> instead of a silent success.
    /// </remarks>
    public static UiActionResult InvokeUiAction(
        this SystemicGame game,
        string layoutId,
        string actionId,
        int choiceOwnerEntityId)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(actionId);

        var layout = game.PresentationContent().Ui.Layouts.FirstOrDefault(candidate => candidate.Id == layoutId)
            ?? throw new KeyNotFoundException($"UI layout '{layoutId}' is not part of this content pack.");
        if (FindAction(layout.Root, actionId) is not { } node)
        {
            throw new KeyNotFoundException(
                $"UI layout '{layoutId}' does not declare the action '{actionId}'.");
        }

        // The action id is the fact type, the owning choice entity is both source and target, and the
        // button's buy amount travels on the fact, so a rule can scale by the quantity the player
        // chose. The owner is required: a fact aimed at entity 0 would match no entity-addressed rule.
        var dispatch = game.Publish(new SimulationFact(actionId, choiceOwnerEntityId, choiceOwnerEntityId,
            Amount: node.Amount ?? 0));
        return new UiActionResult(layoutId, actionId, Handled: dispatch.RulesMatched > 0, dispatch.RolledBack);
    }

    private static UiNode? FindAction(UiNode node, string actionId)
    {
        if (string.Equals(node.Action, actionId, StringComparison.Ordinal))
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            if (FindAction(child, actionId) is { } found)
            {
                return found;
            }
        }

        return null;
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
