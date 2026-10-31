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

    public int Version => 1;

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
/// The outcome of invoking an authored UI action. <see cref="Handled"/> is true when a bound typed
/// command ran or at least one rule matched the published fact; <see cref="RolledBack"/> mirrors
/// the fallback dispatch result.
/// </summary>
public sealed record UiActionResult(string LayoutId, string ActionId, bool Handled, bool RolledBack)
{
    /// <summary>Gets whether the action was refused: a bound handler rejected it, or nothing consumed it.</summary>
    public bool Rejected { get; init; }

    /// <summary>Gets the handler's rejection reason, or null when the action was accepted or had no consumer.</summary>
    public string? Reason { get; init; }
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

    /// <summary>
    /// Resolves a declared UI action to its clicked amount, then runs its bound typed command or
    /// publishes the legacy fact when the action is rule-backed. The action must be declared by the
    /// layout it is invoked on, and the owning entity is required so a rule authored on the action
    /// id has a subject.
    /// </summary>
    /// <remarks>
    /// <see cref="UiActionResult.Handled"/> is true when a bound command handler accepted the action
    /// or when at least one rule matched, so a layout that declares an action with no consumer reports
    /// <c>false</c> instead of a silent success. A handler that rejects the command rolls back its
    /// mutations and reports <see cref="UiActionResult.Rejected"/> with <see cref="UiActionResult.Reason"/>.
    /// </remarks>
    /// <param name="game">The game whose presentation content declares the layout.</param>
    /// <param name="layoutId">The layout that declares <paramref name="actionId"/>.</param>
    /// <param name="actionId">The declared action ID, published as the fact type.</param>
    /// <param name="choiceOwnerEntityId">The entity that owns the click; it becomes both fact subjects.</param>
    /// <param name="amount">
    /// The amount of the node the player clicked, read from <see cref="UiNodeState.Amount"/> (0 when the
    /// button carries none). It is required when the layout offers the action at more than one amount,
    /// and it must be one of the amounts the layout offers.
    /// </param>
    public static UiActionResult InvokeUiAction(
        this SystemicGame game,
        string layoutId,
        string actionId,
        int choiceOwnerEntityId,
        int? amount = null)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(actionId);
        if (amount is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount));
        }

        var layout = game.PresentationContent().Ui.Layouts.FirstOrDefault(candidate => candidate.Id == layoutId)
            ?? throw new KeyNotFoundException($"UI layout '{layoutId}' is not part of this content pack.");
        var declared = new List<UiNode>();
        FindActions(layout.Root, actionId, declared);
        if (declared.Count == 0)
        {
            throw new KeyNotFoundException(
                $"UI layout '{layoutId}' does not declare the action '{actionId}'.");
        }

        // A layout may offer the same action at several amounts, such as "Buy x1" and "Buy x10", or at
        // an amount and none. The renderer knows which node the player clicked, so it passes that
        // node's amount instead of letting one matching node in the tree decide for every button.
        var offered = declared.Select(node => node.Amount ?? 0).Distinct().ToArray();
        var resolved = amount ?? (offered.Length == 1
            ? offered[0]
            : throw new ArgumentException(
                $"UI layout '{layoutId}' declares '{actionId}' at more than one amount; pass the clicked node's amount.",
                nameof(amount)));
        if (!offered.Contains(resolved))
        {
            throw new ArgumentException(
                $"UI layout '{layoutId}' does not declare '{actionId}' at amount {resolved}.", nameof(amount));
        }

        // The action id is the fact type, the owning choice entity is both source and target, and the
        // buy amount travels on the fact, so a rule can scale by the quantity the player chose. The
        // owner is required: a fact aimed at entity 0 would match no entity-addressed rule.
        var action = new SystemicUiAction(layoutId, actionId, choiceOwnerEntityId, resolved);
        if (game.TryExecuteUiAction(action, out var outcome))
        {
            return new UiActionResult(layoutId, actionId, Handled: true, RolledBack: false);
        }

        if (outcome.Handled)
        {
            // The bound handler rejected the action, so fall through to a rule would double-dispatch.
            return new UiActionResult(layoutId, actionId, Handled: false, RolledBack: true)
            {
                Rejected = true,
                Reason = outcome.Reason,
            };
        }

        // WHY: a typed binding is optional, but the fact is not. On a rules-free host the engine has
        // no rules to match, yet the fact is the notification channel presentation and telemetry
        // observe, so publishing it is what makes a data-only button report anything at all.
        var dispatch = game.Publish(new SimulationFact(actionId, choiceOwnerEntityId, choiceOwnerEntityId,
            Amount: resolved));
        return new UiActionResult(layoutId, actionId, Handled: dispatch.RulesMatched > 0, dispatch.RolledBack);
    }

    private static void FindActions(UiNode node, string actionId, List<UiNode> found)
    {
        if (string.Equals(node.Action, actionId, StringComparison.Ordinal))
        {
            found.Add(node);
        }

        foreach (var child in node.Children)
        {
            FindActions(child, actionId, found);
        }
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
