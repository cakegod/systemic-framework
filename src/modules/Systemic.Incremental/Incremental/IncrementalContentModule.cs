using System.Text.Json;
using Systemic.Content;
using Systemic.Host;
using Systemic.Ui;

namespace Systemic.Incremental;

public sealed record IncrementalModuleContent(
    IncrementalCatalog Catalog,
    BuildChoiceCatalog Choices) : ISystemicModuleContent;

/// <summary>Loads incremental economy and advancement content and adds its runtimes to a Core host.</summary>
public sealed class IncrementalContentModule : ISystemicContentModule
{
    public const string ModuleId = "systemic.incremental";

    public string Id => ModuleId;

    public ISystemicModuleContent Compile(ContentModuleContext context, JsonElement declaration) =>
        IncrementalContentCompiler.Compile(context, declaration);

    public void Configure(SystemicGameBuilder builder, ISystemicModuleContent content)
    {
        var module = (IncrementalModuleContent)content;
        var initialize = !builder.IsRestoringWorld;
        var incremental = new IncrementalRuntime(module.Catalog, builder.World, builder.EntityIds,
            initialize: initialize);
        builder.AddSystem(incremental).AddService(incremental);

        var choices = new BuildChoiceRuntime(module.Choices, module.Catalog, builder.World, builder.EntityIds,
            initialize: initialize);
        builder.AddSystem(choices).AddService(choices);
        builder.AddFactObserver(order: 100, choices.ObserveFact);
        builder.AddUiBindingProvider(new BuildChoiceUiBindingProvider(choices));
    }
}

internal sealed class BuildChoiceUiBindingProvider(BuildChoiceRuntime choices) : ISystemicUiBindingProvider
{
    public bool TryResolve(string binding, SystemicUiBindingContext context, out SystemicUiBindingValue value)
    {
        if (!TryGetState(binding, context, out var state, out var field))
        {
            value = default;
            return false;
        }

        value = field switch
        {
            "selected" => SystemicUiBindingValue.FromNumber(state.IsSelected ? 1 : 0),
            "active" => SystemicUiBindingValue.FromNumber(state.IsActive ? 1 : 0),
            "locked" => SystemicUiBindingValue.FromNumber(state.IsLocked ? 1 : 0),
            "available" => SystemicUiBindingValue.FromNumber(state.IsAvailable ? 1 : 0),
            "cooldown" => SystemicUiBindingValue.FromNumber(state.CooldownProgress),
            "activeRemainingTicks" => SystemicUiBindingValue.FromNumber(state.ActiveTicksRemaining),
            "cooldownRemainingTicks" => SystemicUiBindingValue.FromNumber(state.CooldownTicksRemaining),
            _ => default,
        };
        return value.HasValue;
    }

    public string? ResolveVisualState(string binding, SystemicUiBindingContext context)
    {
        if (!TryGetState(binding, context, out var state, out _))
        {
            return null;
        }

        return state.IsActive ? "active"
            : state.CooldownTicksRemaining > 0 ? "cooldown"
            : state.IsSelected ? "selected"
            : state.IsAvailable ? "available"
            : state.IsLocked ? "locked"
            : "inactive";
    }

    private static bool TryParse(string binding, out string choiceId, out string field)
    {
        choiceId = string.Empty;
        field = string.Empty;
        if (!binding.StartsWith("choice.", StringComparison.Ordinal))
        {
            return false;
        }

        var path = binding["choice.".Length..];
        var separator = path.LastIndexOf('.');
        if (separator <= 0)
        {
            return false;
        }

        choiceId = path[..separator];
        field = path[(separator + 1)..];
        return field is "selected" or "active" or "locked" or "available" or "cooldown"
            or "activeRemainingTicks" or "cooldownRemainingTicks";
    }

    private bool TryGetState(
        string binding,
        SystemicUiBindingContext context,
        out BuildChoiceState state,
        out string field)
    {
        if (TryParse(binding, out var choiceId, out field))
        {
            return context.OwnerEntityId is { } ownerId
                ? choices.TryGetState(choiceId, ownerId, out state)
                : choices.TryGetState(choiceId, out state);
        }

        state = default;
        field = string.Empty;
        return false;
    }
}

/// <summary>Typed gameplay APIs for games that opt into the Incremental module.</summary>
public static class IncrementalGameExtensions
{
    public static IncrementalRuntime Incremental(this SystemicGame game) => game.GetService<IncrementalRuntime>();

    public static BuildChoiceRuntime BuildChoices(this SystemicGame game) => game.GetService<BuildChoiceRuntime>();

    public static IncrementalCommandResult AssignRole(this SystemicGame game, int entityId, string roleId) =>
        TryGetService<IncrementalRuntime>(game, out var incremental)
            ? incremental.AssignRole(entityId, roleId)
            : new(false, "This content pack has no incremental roles.");

    public static IncrementalCommandResult PurchaseUpgrade(this SystemicGame game, string upgradeId, int purchaserId) =>
        TryGetService<IncrementalRuntime>(game, out var incremental)
            ? incremental.PurchaseUpgrade(upgradeId, purchaserId, game.Publish)
            : new(false, "This content pack has no incremental upgrades.");

    public static IncrementalCommandResult RegisterChoiceOwner(
        this SystemicGame game,
        string groupId,
        int ownerEntityId,
        int? progressEntityId = null) =>
        TryGetService<BuildChoiceRuntime>(game, out var choices)
            ? choices.RegisterOwner(groupId, ownerEntityId, progressEntityId)
            : new(false, "This content pack has no build choices.");

    public static IncrementalCommandResult SelectChoice(this SystemicGame game, string choiceId, int purchaserId) =>
        TryGetService<BuildChoiceRuntime>(game, out var choices)
            ? choices.SelectChoice(choiceId, purchaserId, game.Publish)
            : new(false, "This content pack has no build choices.");

    public static IncrementalCommandResult ActivateChoice(this SystemicGame game, string choiceId, int actorId) =>
        TryGetService<BuildChoiceRuntime>(game, out var choices)
            ? choices.ActivateChoice(choiceId, actorId, game.Publish)
            : new(false, "This content pack has no build choices.");

    private static bool TryGetService<TService>(SystemicGame game, out TService service)
        where TService : notnull
    {
        try
        {
            service = game.GetService<TService>();
            return true;
        }
        catch (KeyNotFoundException)
        {
            service = default!;
            return false;
        }
    }
}
