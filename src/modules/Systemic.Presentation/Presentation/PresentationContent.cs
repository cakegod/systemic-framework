using System.Collections.Immutable;
using Systemic.Content;
using Systemic.Ui;

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
    ImmutableDictionary<string, string> ChoiceVisualRecipes) : ISystemicModuleContent, ISystemicUiActionCatalog
{
    /// <summary>Checks whether any compiled layout declares the action.</summary>
    /// <param name="actionId">The action ID a game bound to a command.</param>
    public bool DeclaresAction(string actionId) =>
        !Ui.Layouts.IsDefaultOrEmpty
        && Ui.Layouts.Any(layout => DeclaresAction(layout.Root, actionId));

    private static bool DeclaresAction(UiNode node, string actionId)
    {
        if (string.Equals(node.Action, actionId, StringComparison.Ordinal))
        {
            return true;
        }

        if (node.Children.IsDefaultOrEmpty)
        {
            return false;
        }

        foreach (var child in node.Children)
        {
            if (DeclaresAction(child, actionId))
            {
                return true;
            }
        }

        return false;
    }
}
