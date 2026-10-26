using Systemic.Content;
using Systemic.Presentation;
using Systemic.Tests;

namespace Systemic.Tests.Presentation;

public sealed class ProceduralRasterizerTests
{
    [Fact]
    public void Pixel_recipes_are_seeded_repeatable_and_support_variants()
    {
        var samplePath = Path.Combine(AppContext.BaseDirectory, "samples", "incremental-quarry");
        var recipe = SystemicTestContent.Presentation(SystemicTestContent.Load(samplePath)).Visuals.Recipes
            .Single(item => item.Id == "quarry.miner");

        var first = ProceduralRasterizer.Rasterize(recipe, variantSeed: 11);
        var repeated = ProceduralRasterizer.Rasterize(recipe, variantSeed: 11);
        var variant = ProceduralRasterizer.Rasterize(recipe, variantSeed: 12);
        var activeRecipe = SystemicTestContent.Presentation(SystemicTestContent.Load(samplePath)).Visuals.Recipes
            .Single(item => item.Id == "quarry.overdrive-icon");
        var baseState = ProceduralRasterizer.Rasterize(activeRecipe, variantSeed: 11);
        var active = ProceduralRasterizer.Rasterize(activeRecipe, variantSeed: 11, visualState: "active");

        Assert.Equal(recipe.CanvasWidth, first.Width);
        Assert.Equal(recipe.CanvasHeight, first.Height);
        Assert.Equal(Convert.ToBase64String(first.Pixels.Span), Convert.ToBase64String(repeated.Pixels.Span));
        Assert.NotEqual(Convert.ToBase64String(first.Pixels.Span), Convert.ToBase64String(variant.Pixels.Span));
        Assert.NotEqual(Convert.ToBase64String(baseState.Pixels.Span), Convert.ToBase64String(active.Pixels.Span));
        Assert.Equal(Convert.ToBase64String(active.Pixels.Span), Convert.ToBase64String(
            ProceduralRasterizer.Rasterize(activeRecipe, variantSeed: 11, visualState: "active").Pixels.Span));
    }

    [Fact]
    public void Composite_recipes_place_and_scale_reusable_pixel_parts()
    {
        using var pack = TestPack.Create();
        File.WriteAllText(Path.Combine(pack.Root, "visuals.json"), """
            { "schemaVersion": 2, "recipes": [
              { "id": "part", "kind": "prop", "seed": 1, "canvasWidth": 2, "canvasHeight": 2,
                "palette": ["#ff0000"], "layers": [
                  { "shape": "rect", "color": 0, "x": 0, "y": 0, "width": 2, "height": 2 }
                ] },
              { "id": "composite", "kind": "actor", "seed": 2, "canvasWidth": 6, "canvasHeight": 6,
                "palette": ["#00000000"], "parts": [
                  { "recipeId": "part", "x": 1, "y": 2, "scale": 2 }
                ] }
            ] }
            """);
        TestPack.SetTimelineVisual(pack, "composite");
        var recipe = SystemicTestContent.Presentation(SystemicTestContent.Load(pack.Root)).Visuals.Recipes
            .Single(item => item.Id == "composite");

        var pixels = ProceduralRasterizer.Rasterize(recipe);

        Assert.Equal([255, 0, 0, 255], pixels.Pixels.Span.Slice(((2 * pixels.Width) + 1) * 4, 4).ToArray());
        Assert.Equal([255, 0, 0, 255], pixels.Pixels.Span.Slice(((5 * pixels.Width) + 4) * 4, 4).ToArray());
        Assert.Equal([0, 0, 0, 0], pixels.Pixels.Span[..4].ToArray());
    }

    [Fact]
    public void Raster_work_budget_counts_long_lines_even_when_their_canvas_is_small()
    {
        var line = new VisualLayer("line", 0, 0, 0, 1024, 1024, 0);
        var partRecipe = new VisualRecipe("part", "prop", 1, 1, 1, ["#ffffff"],
            [.. Enumerable.Repeat(line, 512)]);
        var recipe = new VisualRecipe("composite", "actor", 2, 1, 1, ["#00000000"], [],
            Parts: [.. Enumerable.Repeat(new VisualPart(partRecipe, 0, 0, 1), 64)]);

        var error = Assert.Throws<ArgumentException>(() => ProceduralRasterizer.Rasterize(recipe));

        Assert.Contains("raster work budget", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
