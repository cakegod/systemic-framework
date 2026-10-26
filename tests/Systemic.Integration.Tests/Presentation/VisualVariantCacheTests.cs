using Systemic.Content;
using Systemic.Presentation;

namespace Systemic.Tests.Presentation;

public sealed class VisualVariantCacheTests
{
    [Fact]
    public void Cache_reuses_warm_variants_and_evicts_by_a_hard_byte_limit()
    {
        var recipe = SystemicTestContent.Presentation(SystemicTestContent.Load(
            Path.Combine(AppContext.BaseDirectory, "samples", "incremental-quarry")))
            .Visuals.Recipes.Single(item => item.Id == "quarry.miner");
        var variantBytes = recipe.CanvasWidth * recipe.CanvasHeight * 4;
        var cache = new VisualVariantCache(variantBytes * 2L);

        var first = cache.GetOrCreate(recipe, 1);
        Assert.Same(first, cache.GetOrCreate(recipe, 1));
        var equivalentRecipe = recipe with
        {
            Palette = [.. recipe.Palette],
            Layers = [.. recipe.Layers]
        };
        Assert.Same(first, cache.GetOrCreate(equivalentRecipe, 1));
        _ = cache.GetOrCreate(recipe, 2);
        _ = cache.GetOrCreate(recipe, 1);
        _ = cache.GetOrCreate(recipe, 3);
        var recreatedSecond = cache.GetOrCreate(recipe, 2);

        Assert.NotSame(first, recreatedSecond);
        Assert.Equal(2, cache.CachedCount);
        Assert.InRange(cache.CachedBytes, 0, cache.MaximumBytes);
        cache.Clear();
        Assert.Equal(0, cache.CachedBytes);
        Assert.Equal(0, cache.CachedCount);
    }

    [Fact]
    public void Cache_keeps_visual_states_separate_for_the_same_recipe_seed()
    {
        var recipe = SystemicTestContent.Presentation(SystemicTestContent.Load(
            Path.Combine(AppContext.BaseDirectory, "samples", "incremental-quarry")))
            .Visuals.Recipes.Single(item => item.Id == "quarry.overdrive-icon");
        var cache = new VisualVariantCache(1024 * 1024);

        var selected = cache.GetOrCreate(recipe, variantSeed: 4);
        var active = cache.GetOrCreate(recipe, variantSeed: 4, visualState: "active");

        Assert.NotSame(selected, active);
        Assert.Equal(2, cache.CachedCount);
        Assert.NotEqual(Convert.ToBase64String(selected.Pixels.Span), Convert.ToBase64String(active.Pixels.Span));
    }

    [Fact]
    public void Cache_keys_composite_parts_by_their_resolved_recipe_content()
    {
        var redPart = new VisualRecipe("part", "prop", 1, 1, 1, ["#ff0000"],
            [new VisualLayer("rect", 0, 0, 0, 1, 1, 0)]);
        var bluePart = redPart with { Palette = ["#0000ff"] };
        var redComposite = new VisualRecipe("actor", "actor", 2, 1, 1, ["#00000000"], [],
            Parts: [new VisualPart(redPart, 0, 0, 1)]);
        var blueComposite = redComposite with { Parts = [new VisualPart(bluePart, 0, 0, 1)] };
        var cache = new VisualVariantCache(1024);

        var red = cache.GetOrCreate(redComposite);
        var blue = cache.GetOrCreate(blueComposite);

        Assert.NotSame(red, blue);
        Assert.Equal(new Rgba32(255, 0, 0), red.GetPixel(0, 0));
        Assert.Equal(new Rgba32(0, 0, 255), blue.GetPixel(0, 0));
        Assert.Equal(2, cache.CachedCount);
    }
}
