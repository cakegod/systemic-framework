using System.Collections.Immutable;
using Systemic.Content;

namespace Systemic.Presentation;

/// <summary>Deterministic, engine-neutral pixel rasterization for declarative visual recipes.</summary>
public static class ProceduralRasterizer
{
    public const int GeneratorVersion = 2;
    public const long MaximumPixelWork = 16_777_216;
    private const ulong LayerSeedStep = 0x9E3779B97F4A7C15UL;

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

        var pixels = imported is not null && recipe.ImportedMode == ImportedAssetMode.Underlay
            ? imported.Pixels.ToArray()
            : new byte[checked(width * height * 4)];

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
            var color = ParseColor(recipe.Palette[layer.Color]);

            switch (layer.Shape)
            {
                case "rect":
                    FillRectangle(pixels, width, height, x, y, layerWidth, layerHeight, color);
                    break;
                case "ellipse":
                    FillEllipse(pixels, width, height, x, y, layerWidth, layerHeight, color);
                    break;
                case "line":
                    DrawLine(pixels, width, height, x, y, x + layerWidth, y + layerHeight, color);
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
            CompositePart(pixels, width, height, part, partPixels);
        }

        if (imported is not null && recipe.ImportedMode == ImportedAssetMode.Overlay)
        {
            CompositeOver(pixels, width, imported);
        }

        return new PixelBuffer(width, height, pixels, takeOwnership: true);
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

    private static void CompositeOver(byte[] destination, int destinationWidth, PixelBuffer source)
    {
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                BlendPixel(destination, destinationWidth, x, y, source.GetPixel(x, y));
            }
        }
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

    private static void FillRectangle(byte[] pixels, int width, int height, int x, int y, int shapeWidth, int shapeHeight, Rgba32 color)
    {
        var left = Math.Max(x, 0);
        var top = Math.Max(y, 0);
        var right = Math.Min(x + shapeWidth, width);
        var bottom = Math.Min(y + shapeHeight, height);
        for (var py = top; py < bottom; py++)
        {
            for (var px = left; px < right; px++)
            {
                BlendPixel(pixels, width, px, py, color);
            }
        }
    }

    private static void FillEllipse(byte[] pixels, int width, int height, int x, int y, int shapeWidth, int shapeHeight, Rgba32 color)
    {
        if (shapeWidth <= 0 || shapeHeight <= 0)
        {
            return;
        }

        var left = Math.Max(x, 0);
        var top = Math.Max(y, 0);
        var right = Math.Min(x + shapeWidth, width);
        var bottom = Math.Min(y + shapeHeight, height);
        var centerX = x + shapeWidth / 2.0;
        var centerY = y + shapeHeight / 2.0;
        var radiusX = Math.Max(shapeWidth / 2.0, 0.5);
        var radiusY = Math.Max(shapeHeight / 2.0, 0.5);
        for (var py = top; py < bottom; py++)
        {
            var dy = ((py + 0.5) - centerY) / radiusY;
            for (var px = left; px < right; px++)
            {
                var dx = ((px + 0.5) - centerX) / radiusX;
                if ((dx * dx) + (dy * dy) <= 1.0)
                {
                    BlendPixel(pixels, width, px, py, color);
                }
            }
        }
    }

    private static void DrawLine(byte[] pixels, int width, int height, int x0, int y0, int x1, int y1, Rgba32 color)
    {
        var dx = Math.Abs(x1 - x0);
        var sx = x0 < x1 ? 1 : -1;
        var dy = -Math.Abs(y1 - y0);
        var sy = y0 < y1 ? 1 : -1;
        var error = dx + dy;
        while (true)
        {
            if ((uint)x0 < (uint)width && (uint)y0 < (uint)height)
            {
                BlendPixel(pixels, width, x0, y0, color);
            }

            if (x0 == x1 && y0 == y1)
            {
                break;
            }

            var doubledError = 2 * error;
            if (doubledError >= dy)
            {
                error += dy;
                x0 += sx;
            }

            if (doubledError <= dx)
            {
                error += dx;
                y0 += sy;
            }
        }
    }

    private static void CompositePart(byte[] destination, int destinationWidth, int destinationHeight,
        VisualPart part, PixelBuffer source)
    {
        for (var sourceY = 0; sourceY < source.Height; sourceY++)
        {
            var targetY = part.Y + (sourceY * part.Scale);
            if (targetY >= destinationHeight || targetY + part.Scale <= 0)
            {
                continue;
            }

            for (var sourceX = 0; sourceX < source.Width; sourceX++)
            {
                var targetX = part.X + (sourceX * part.Scale);
                if (targetX >= destinationWidth || targetX + part.Scale <= 0)
                {
                    continue;
                }

                var sourcePixel = source.GetPixel(sourceX, sourceY);
                for (var offsetY = 0; offsetY < part.Scale; offsetY++)
                {
                    var y = targetY + offsetY;
                    if ((uint)y >= (uint)destinationHeight)
                    {
                        continue;
                    }

                    for (var offsetX = 0; offsetX < part.Scale; offsetX++)
                    {
                        var x = targetX + offsetX;
                        if ((uint)x < (uint)destinationWidth)
                        {
                            BlendPixel(destination, destinationWidth, x, y, sourcePixel);
                        }
                    }
                }
            }
        }
    }

    private static void BlendPixel(byte[] pixels, int width, int x, int y, Rgba32 source)
    {
        var index = ((y * width) + x) * 4;
        if (source.A == byte.MaxValue)
        {
            pixels[index] = source.R;
            pixels[index + 1] = source.G;
            pixels[index + 2] = source.B;
            pixels[index + 3] = source.A;
            return;
        }

        if (source.A == 0)
        {
            return;
        }

        var destinationAlpha = pixels[index + 3];
        var inverseAlpha = 255 - source.A;
        var outputAlpha = source.A + ((destinationAlpha * inverseAlpha + 127) / 255);
        pixels[index] = BlendChannel(source.R, pixels[index], source.A, destinationAlpha, inverseAlpha, outputAlpha);
        pixels[index + 1] = BlendChannel(source.G, pixels[index + 1], source.A, destinationAlpha, inverseAlpha, outputAlpha);
        pixels[index + 2] = BlendChannel(source.B, pixels[index + 2], source.A, destinationAlpha, inverseAlpha, outputAlpha);
        pixels[index + 3] = (byte)outputAlpha;
    }

    private static byte BlendChannel(byte source, byte destination, int sourceAlpha, int destinationAlpha, int inverseAlpha, int outputAlpha)
    {
        var premultiplied = (source * sourceAlpha) + ((destination * destinationAlpha * inverseAlpha + 127) / 255);
        return (byte)((premultiplied + outputAlpha / 2) / outputAlpha);
    }

    private static int ToPixel(float value) => (int)MathF.Round(value, MidpointRounding.AwayFromZero);

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
