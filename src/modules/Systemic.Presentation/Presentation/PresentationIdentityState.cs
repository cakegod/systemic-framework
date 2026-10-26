namespace Systemic.Presentation;

public readonly record struct PresentationIdentityState(
    long VisualRootSeed,
    long NextActorVisualId,
    long NextCueSeedOrdinal = 0);
