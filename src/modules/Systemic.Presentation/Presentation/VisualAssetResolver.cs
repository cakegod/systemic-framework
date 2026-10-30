using System.Collections.Immutable;

namespace Systemic.Presentation;

public interface IVisualAssetProvider
{
    bool TryGetPixels(string assetId, out PixelBuffer? pixels);
}

public sealed record VisualAssetResolution(
    PixelBuffer Pixels,
    bool UsedImportedAsset,
    ImmutableArray<string> MissingAssetIds);

public static class VisualAssetResolver
{
    public static VisualAssetResolution Resolve(
        VisualRecipe recipe,
        IVisualAssetProvider? provider,
        ulong variantSeed = 0,
        string? visualState = null)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        var missingAssets = new List<string>();
        var usedAssets = new List<string>();
        var pixels = ProceduralRasterizer.Rasterize(recipe, variantSeed, visualState,
            provider, missingAssets, usedAssets);
        return new VisualAssetResolution(pixels,
            usedAssets.Count > 0,
            [.. missingAssets]);
    }
}
