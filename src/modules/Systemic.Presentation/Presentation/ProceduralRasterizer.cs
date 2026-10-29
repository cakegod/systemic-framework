using System.Collections.Immutable;
using SkiaSharp;
using Systemic.Content;

namespace Systemic.Presentation;

/// <summary>Deterministic, engine-neutral pixel rasterization for declarative visual recipes.</summary>
public static class ProceduralRasterizer
{
    public const int GeneratorVersion = 3;
    public const long MaximumPixelWork = 16_777_216;
    private const ulong LayerSeedStep = 0x9E3779B97F4A7C15UL;
    private static readonly SKSamplingOptions NearestSampling =
        new(SKFilterMode.Nearest, SKMipmapMode.None);

    public static PixelBuffer Rasterize(VisualRecipe recipe, ulong variantSeed = 0, string? visualState = null) =>
        RasterizeCore(recipe, variantSeed, visualState, assetProvider: null, missingAssets: null, usedAssets: null);

    internal static PixelBuffer Rasterize(
        VisualRecipe recipe,
        ulong variantSeed,
        string? visualState,
        IVisualAssetProvider? assetProvider,
        List<string>? missingAssets,
        List<string>? usedAssets) =>
        RasterizeCore(recipe, variantSeed, visualState, assetProvider, missingAssets, usedAssets);

    private static PixelBuffer RasterizeCore(
        VisualRecipe recipe,
        ulong variantSeed,
        string? visualState,
        IVisualAssetProvider? assetProvider,
        List<string>? missingAssets,
        List<string>? usedAssets)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        Validate(recipe);
        var width = recipe.CanvasWidth;
        var height = recipe.CanvasHeight;
        var imported = ResolveImportedAsset(recipe, assetProvider, missingAssets, usedAssets);
        if (imported is not null && recipe.ImportedMode == ImportedAssetMode.Replace)
        {
            return imported;
        }

        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        if (imported is not null && recipe.ImportedMode == ImportedAssetMode.Underlay)
        {
            DrawImage(canvas, imported, 0, 0);
        }

        using var fill = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill };
        using var stroke = new SKPaint
        {
            IsAntialias = false,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
        };

        for (var layerIndex = 0; layerIndex < recipe.Layers.Length; layerIndex++)
        {
            var layer = recipe.Layers[layerIndex];
            if (layer.State is not null && !string.Equals(layer.State, visualState, StringComparison.Ordinal))
            {
                continue;
            }

            var random = Mix(unchecked((ulong)recipe.Seed) ^ Mix(variantSeed + ((ulong)layerIndex * LayerSeedStep)));
            var jitterX = PickOffset(ref random, layer.JitterPixels);
            var jitterY = PickOffset(ref random, layer.JitterPixels);
            var x = ToPixel(layer.X) + jitterX;
            var y = ToPixel(layer.Y) + jitterY;
            var layerWidth = ToPixel(layer.Width);
            var layerHeight = ToPixel(layer.Height);
            var color = ToSkColor(ParseColor(recipe.Palette[layer.Color]));
            var bounds = SKRect.Create(x, y, layerWidth, layerHeight);

            switch (layer.Shape)
            {
                case "rect":
                    fill.Color = color;
                    canvas.DrawRect(bounds, fill);
                    break;
                case "ellipse":
                    fill.Color = color;
                    canvas.DrawOval(bounds, fill);
                    break;
                case "line":
                    stroke.Color = color;
                    canvas.DrawLine(x, y, x + layerWidth, y + layerHeight, stroke);
                    break;
                default:
                    throw new ArgumentException($"Unsupported visual shape '{layer.Shape}'.", nameof(recipe));
            }
        }

        for (var partIndex = 0; !recipe.Parts.IsDefaultOrEmpty && partIndex < recipe.Parts.Length; partIndex++)
        {
            var part = recipe.Parts[partIndex];
            var partPixels = RasterizeCore(part.Recipe,
                Mix(variantSeed + ((ulong)partIndex * LayerSeedStep)), visualState,
                assetProvider, missingAssets, usedAssets);
            DrawImage(canvas, partPixels, part.X, part.Y, scale: part.Scale);
        }

        if (imported is not null && recipe.ImportedMode == ImportedAssetMode.Overlay)
        {
            DrawImage(canvas, imported, 0, 0);
        }

        return new PixelBuffer(width, height, bitmap.Bytes, takeOwnership: true);
    }

    private static void DrawImage(SKCanvas canvas, PixelBuffer image, int x, int y, int scale = 1)
    {
        using var source = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888,
            SKAlphaType.Unpremul));
        image.Pixels.Span.CopyTo(source.GetPixelSpan());
        var destination = SKRect.Create(x, y, image.Width * scale, image.Height * scale);
        canvas.DrawBitmap(source, destination, NearestSampling);
    }

    private static PixelBuffer? ResolveImportedAsset(
        VisualRecipe recipe,
        IVisualAssetProvider? assetProvider,
        List<string>? missingAssets,
        List<string>? usedAssets)
    {
        if (recipe.ImportedAssetId is not { } assetId)
        {
            return null;
        }

        if (assetProvider is not null && assetProvider.TryGetPixels(assetId, out var imported)
            && imported is not null && imported.Width == recipe.CanvasWidth && imported.Height == recipe.CanvasHeight)
        {
            if (usedAssets is not null && !usedAssets.Contains(assetId, StringComparer.Ordinal))
            {
                usedAssets.Add(assetId);
            }

            return imported;
        }

        if (missingAssets is not null && !missingAssets.Contains(assetId, StringComparer.Ordinal))
        {
            missingAssets.Add(assetId);
        }

        return null;
    }

    private static void Validate(VisualRecipe recipe)
    {
        if (recipe.CanvasWidth is < 1 or > 256 || recipe.CanvasHeight is < 1 or > 256
            || recipe.Palette.IsDefaultOrEmpty
            || recipe.ImportedAssetId is { Length: 0 }
            || recipe.ImportedAssetId is null && recipe.ImportedMode != ImportedAssetMode.Underlay
            || recipe.ImportedMode is not (ImportedAssetMode.Underlay or ImportedAssetMode.Overlay or ImportedAssetMode.Replace))
        {
            throw new ArgumentException("Recipe dimensions and palette are invalid.", nameof(recipe));
        }

        foreach (var layer in recipe.Layers)
        {
            if (layer.Color < 0 || layer.Color >= recipe.Palette.Length
                || layer.State is { Length: 0 }
                || layer.JitterPixels is < 0 or > 4
                || !float.IsFinite(layer.X) || !float.IsFinite(layer.Y)
                || !float.IsFinite(layer.Width) || !float.IsFinite(layer.Height)
                || Math.Abs(layer.X) > 4096 || Math.Abs(layer.Y) > 4096
                || layer.Width is < 0 or > 1024 || layer.Height is < 0 or > 1024)
            {
                throw new ArgumentException("Recipe layer geometry is invalid.", nameof(recipe));
            }

            _ = ParseColor(recipe.Palette[layer.Color]);
        }

        if (!recipe.Parts.IsDefaultOrEmpty && recipe.Parts.Length > 64)
        {
            throw new ArgumentException("A visual recipe cannot contain more than 64 parts.", nameof(recipe));
        }

        foreach (var part in recipe.Parts.IsDefaultOrEmpty ? [] : recipe.Parts)
        {
            if (part.Recipe is null || !part.Recipe.Parts.IsDefaultOrEmpty
                || part.Scale is < 1 or > 8 || part.X is < -4096 or > 4096 || part.Y is < -4096 or > 4096)
            {
                throw new ArgumentException("Visual part references must be non-nested and within supported bounds.", nameof(recipe));
            }
        }

        if (EstimatePixelWork(recipe) > MaximumPixelWork)
        {
            throw new ArgumentException($"Visual recipe exceeds the raster work budget of {MaximumPixelWork} pixel operations.", nameof(recipe));
        }
    }

    internal static long EstimatePixelWork(VisualRecipe recipe)
        => EstimatePixelWorkCore(recipe, visualState: null, includeEveryState: true);

    internal static long EstimatePixelWorkForState(VisualRecipe recipe, string? visualState) =>
        EstimatePixelWorkCore(recipe, visualState, includeEveryState: false);

    private static long EstimatePixelWorkCore(VisualRecipe recipe, string? visualState, bool includeEveryState)
    {
        var work = (long)recipe.CanvasWidth * recipe.CanvasHeight;
        foreach (var layer in recipe.Layers)
        {
            if (!includeEveryState && layer.State is not null
                && !string.Equals(layer.State, visualState, StringComparison.Ordinal))
            {
                continue;
            }

            work = checked(work + (layer.Shape == "line"
                ? Math.Max(PixelDimension(layer.Width), PixelDimension(layer.Height)) + 1
                : PixelDimension(layer.Width, recipe.CanvasWidth) * PixelDimension(layer.Height, recipe.CanvasHeight)));
        }

        foreach (var part in recipe.Parts.AsSpan())
        {
            var child = part.Recipe;
            var childPixels = (long)child.CanvasWidth * child.CanvasHeight;
            var childWork = includeEveryState
                ? EstimatePixelWork(child)
                : EstimatePixelWorkCore(child, visualState, includeEveryState: false);
            work = checked(work + childWork + (childPixels * (1L + ((long)part.Scale * part.Scale))));
        }

        return work;
    }

    private static long PixelDimension(float size, int canvasSize) =>
        Math.Min(canvasSize, (long)Math.Ceiling(size + 0.5));

    private static long PixelDimension(float size) => (long)Math.Ceiling(size + 0.5);

    private static int PickOffset(ref ulong state, int jitterPixels)
    {
        if (jitterPixels == 0)
        {
            return 0;
        }

        state = Mix(state);
        return (int)(state % (uint)(jitterPixels * 2 + 1)) - jitterPixels;
    }

    private static int ToPixel(float value) => (int)MathF.Round(value, MidpointRounding.AwayFromZero);

    private static SKColor ToSkColor(Rgba32 color) => new(color.R, color.G, color.B, color.A);

    private static Rgba32 ParseColor(string color)
    {
        if (color.Length is not (7 or 9) || color[0] != '#'
            || !byte.TryParse(color.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, null, out var red)
            || !byte.TryParse(color.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out var green)
            || !byte.TryParse(color.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber, null, out var blue)
            || (color.Length == 9 && !byte.TryParse(color.AsSpan(7, 2), System.Globalization.NumberStyles.HexNumber, null, out _)))
        {
            throw new ArgumentException($"Invalid RGBA color '{color}'.", nameof(color));
        }

        var alpha = color.Length == 9
            ? byte.Parse(color.AsSpan(7, 2), System.Globalization.NumberStyles.HexNumber, null)
            : byte.MaxValue;
        return new Rgba32(red, green, blue, alpha);
    }

    private static ulong Mix(ulong value)
    {
        value += 0x9E3779B97F4A7C15UL;
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }
}
