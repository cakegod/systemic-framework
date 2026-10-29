using Systemic.Kernel;

namespace Systemic.Tests.Kernel;

/// <summary>
/// Row tags are a schema-declared mask, so the bit that persists in a save is decided by the schema
/// rather than by whichever rule set happened to bind first.
/// </summary>
public sealed class PopulationTagTests
{
    private static readonly string[] s_tooManyTags =
        [.. Enumerable.Range(0, Population<TaggedKind>.MaximumTags + 1).Select(index => $"tag{index}")];

    [Fact]
    public void Schema_tags_pack_into_one_mask_column_in_declaration_order()
    {
        var world = new World(seed: 41, valueScale: 1);
        var population = world.DefinePopulation<TaggedKind>();
        population.AddRange(2);

        Assert.Equal(["poisoned", "blessed", "asleep"], population.Tags.ToArray());
        population.AddTag(1, "blessed");

        Assert.False(population.HasTag(0, "blessed"));
        Assert.True(population.HasTag(1, "blessed"));
        Assert.False(population.HasTag(1, "poisoned"));
        Assert.False(population.HasTag(1, "asleep"));
        // One ulong per row is the whole tag storage, no matter how many tags the schema declares.
        Assert.Equal(2, population.MaskColumn!.Count);
    }

    [Fact]
    public void Tag_state_rolls_back_with_the_enclosing_transaction()
    {
        var world = new World(seed: 42, valueScale: 1);
        var population = world.DefinePopulation<TaggedKind>();
        population.AddRange(1);

        using (var transaction = world.BeginTransaction())
        {
            Assert.True(population.AddTag(0, "poisoned"));
            Assert.True(population.HasTag(0, "poisoned"));
            transaction.Rollback();
        }

        Assert.False(population.HasTag(0, "poisoned"));

        using (var transaction = world.BeginTransaction())
        {
            population.AddTag(0, "poisoned");
            transaction.Commit();
        }

        Assert.True(population.HasTag(0, "poisoned"));
    }

    [Fact]
    public void Unknown_tag_names_the_schema_and_its_declared_tags()
    {
        var world = new World(seed: 43, valueScale: 1);
        var population = world.DefinePopulation<TaggedKind>();
        population.AddRange(1);

        var error = Assert.Throws<InvalidOperationException>(() => population.HasTag(0, "onfire"));

        Assert.Contains(TaggedKind.Id, error.Message, StringComparison.Ordinal);
        Assert.Contains("onfire", error.Message, StringComparison.Ordinal);
        Assert.Contains("poisoned", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Tag_writes_to_a_row_outside_the_live_population_are_declined()
    {
        var world = new World(seed: 44, valueScale: 1);
        var population = world.DefinePopulation<TaggedKind>();
        population.AddRange(1);

        Assert.False(population.AddTag(3, "poisoned"));
        Assert.False(population.HasTag(3, "poisoned"));
        Assert.False(population.RemoveTag(-1, "poisoned"));
    }

    [Fact]
    public void A_schema_may_not_declare_more_tags_than_the_mask_holds()
    {
        var world = new World(seed: 45, valueScale: 1);

        var error = Assert.Throws<InvalidOperationException>(() => world.DefinePopulation<TooManyTagsKind>());

        Assert.Contains("65", error.Message, StringComparison.Ordinal);
        Assert.Contains(Population<TooManyTagsKind>.MaximumTags.ToString(), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_schema_may_not_declare_a_tag_twice()
    {
        var world = new World(seed: 46, valueScale: 1);

        var error = Assert.Throws<InvalidOperationException>(() => world.DefinePopulation<DuplicateTagsKind>());

        Assert.Contains("duplicate", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Tag_masks_round_trip_through_the_population_save_state()
    {
        var source = new World(seed: 47, valueScale: 1);
        var sourcePopulation = source.DefinePopulation<TaggedKind>();
        sourcePopulation.AddRange(3);
        sourcePopulation.AddTag(0, "poisoned");
        sourcePopulation.AddTag(1, "asleep");
        sourcePopulation.AddTag(2, "blessed");
        var state = source.CapturePopulationState();

        var target = new World(seed: 47, valueScale: 1);
        var targetPopulation = target.DefinePopulation<TaggedKind>();
        target.RestorePopulationState(state);

        Assert.Equal(3, targetPopulation.Count);
        Assert.True(targetPopulation.HasTag(0, "poisoned"));
        Assert.True(targetPopulation.HasTag(1, "asleep"));
        Assert.True(targetPopulation.HasTag(2, "blessed"));
        Assert.False(targetPopulation.HasTag(0, "blessed"));
    }

    [Fact]
    public void A_clone_shares_the_mask_until_one_side_writes()
    {
        var source = new World(seed: 48, valueScale: 1);
        var sourcePopulation = source.DefinePopulation<TaggedKind>();
        sourcePopulation.AddRange(2);
        sourcePopulation.AddTag(0, "poisoned");

        var clone = source.Clone();
        var clonePopulation = clone.GetPopulation<TaggedKind>();
        Assert.True(clonePopulation.HasTag(0, "poisoned"));

        clonePopulation.AddTag(1, "blessed");

        Assert.True(clonePopulation.HasTag(1, "blessed"));
        Assert.False(sourcePopulation.HasTag(1, "blessed"));
        Assert.True(sourcePopulation.HasTag(0, "poisoned"));
    }

    [Fact]
    public void A_schema_without_tags_creates_no_mask_column()
    {
        var world = new World(seed: 49, valueScale: 1);
        var population = world.DefinePopulation<UntaggedKind>();
        population.AddRange(1);

        Assert.True(population.Tags.IsEmpty);
        Assert.Null(population.MaskColumn);
    }

    private readonly struct TaggedKind : IPopulationKind
    {
        public static string Id => "test.tagged";

        public static int Version => 1;

        public static string[] Tags => ["poisoned", "blessed", "asleep"];
    }

    private readonly struct TooManyTagsKind : IPopulationKind
    {
        public static string Id => "test.too-many-tags";

        public static int Version => 1;

        public static string[] Tags => s_tooManyTags;
    }

    private readonly struct DuplicateTagsKind : IPopulationKind
    {
        public static string Id => "test.duplicate-tags";

        public static int Version => 1;

        public static string[] Tags => ["duplicate", "duplicate"];
    }

    private readonly struct UntaggedKind : IPopulationKind
    {
        public static string Id => "test.untagged";

        public static int Version => 1;
    }
}
