using System.Collections.Immutable;
using Systemic.Content;

namespace Systemic.Presentation;

public sealed record VisualRecipe(
    string Id,
    string Kind,
    long Seed,
    int CanvasWidth,
    int CanvasHeight,
    ImmutableArray<string> Palette,
    ImmutableArray<VisualLayer> Layers,
    ImmutableDictionary<string, int>? PaletteRoles = null,
    ImmutableArray<VisualPart> Parts = default,
    string? ImportedAssetId = null,
    ImportedAssetMode ImportedMode = ImportedAssetMode.Underlay);

public sealed record VisualPart(VisualRecipe Recipe, int X, int Y, int Scale);

public sealed record VisualLayer(
    string Shape,
    int Color,
    float X,
    float Y,
    float Width,
    float Height,
    int JitterPixels,
    string? State = null);

public sealed record UiLayout(string Id, UiNode Root);

public sealed record UiNode(
    string Type,
    string? Text,
    string? Bind,
    string? Action,
    ImmutableArray<UiNode> Children,
    double? Minimum = null,
    double? Maximum = null,
    string? VisualRecipeId = null,
    string? Tooltip = null,
    int? Amount = null);

public sealed record CompiledVisuals(ImmutableArray<VisualRecipe> Recipes);

public sealed record CompiledUi(ImmutableArray<UiLayout> Layouts);

public sealed record PresentationModuleContent(
    CompiledVisuals Visuals,
    CompiledUi Ui,
    PresentationCatalog Catalog,
    ImmutableDictionary<string, string> ChoiceVisualRecipes) : ISystemicModuleContent;
