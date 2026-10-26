using System.Collections.Immutable;

namespace Systemic.Incremental;

public enum ChoiceActivationMode
{
    Passive,
    Triggered,
    Timed,
}

public sealed record BuildChoiceGroupDefinition(
    string Id,
    string OwnerEntityId,
    string ProgressEntityId,
    string ProgressProperty,
    double UnlockAt,
    int SelectionLimit,
    string? FreeChoiceId);

public sealed record BuildChoiceDefinition(
    string Id,
    string GroupId,
    string EntityId,
    ChoiceActivationMode ActivationMode,
    string ChoiceTag,
    string SelectedProperty,
    string ActiveUntilProperty,
    string CooldownUntilProperty,
    string? CostResourceId,
    double CostAmount,
    int ActiveTicks,
    int CooldownTicks,
    string? TriggerEvent,
    string? ActivationEvent,
    bool IsFree,
    ImmutableArray<string> Requires = default,
    ImmutableArray<string> RequiresAny = default,
    ImmutableArray<string> Excludes = default);

public sealed record BuildChoiceCatalog(
    ImmutableArray<BuildChoiceGroupDefinition> Groups,
    ImmutableArray<BuildChoiceDefinition> Choices);

public readonly record struct BuildChoiceState(
    bool IsSelected,
    bool IsActive,
    int ActiveTicksRemaining,
    int CooldownTicksRemaining,
    double CooldownProgress,
    bool IsLocked = false,
    bool IsAvailable = false);

public sealed record BuildChoiceEntityInstanceState(string ChoiceId, int EntityId);

public sealed record BuildChoiceOwnerInstanceState(
    string GroupId,
    int OwnerEntityId,
    int ProgressEntityId,
    ImmutableArray<BuildChoiceEntityInstanceState> Choices);

public sealed record BuildChoiceRuntimeState(ImmutableArray<BuildChoiceOwnerInstanceState> Owners);
