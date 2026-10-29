using Systemic.Kernel;

namespace Systemic.Tests.Kernel;

public sealed class TagQueryTests
{
    [Fact]
    public void Returns_only_entities_that_carry_the_tag()
    {
        var world = new World(seed: 1, valueScale: 1);
        var first = world.Add(new DataEntity(["bolt"], []));
        _ = world.Add(new DataEntity(["rock"], []));
        var second = world.Add(new DataEntity(["bolt", "hot"], []));

        var ids = world.EntitiesWithTag("bolt").ToArray();

        Assert.Equal([first.Id, second.Id], ids);
        Assert.Empty(world.EntitiesWithTag("missing").ToArray());
    }

    [Fact]
    public void Rejects_a_missing_tag_name()
    {
        var world = new World(seed: 1, valueScale: 1);

        Assert.Throws<ArgumentException>(() => world.EntitiesWithTag(" "));
    }

    [Fact]
    public void Refreshes_after_a_tag_changes()
    {
        var world = new World(seed: 1, valueScale: 1);
        var entity = world.Add(new DataEntity([], []));
        Assert.Empty(world.EntitiesWithTag("bolt").ToArray());

        entity.AddTag("bolt");

        Assert.Single(world.EntitiesWithTag("bolt").ToArray());
    }

    [Fact]
    public void Live_count_follows_a_tag_added_after_the_count_was_queried()
    {
        var world = new World(seed: 3, valueScale: 1);
        var entity = world.Add(new DataEntity([], []));
        Assert.Equal(0, world.LiveCount("bolt"));

        entity.AddTag("bolt");

        Assert.Equal(1, world.LiveCount("bolt"));
        Assert.Equal(1, world.LiveCount("bolt"));
    }

    [Fact]
    public void Live_count_follows_a_tag_grant_after_the_count_was_queried()
    {
        var world = new World(seed: 4, valueScale: 1);
        var entity = world.Add(new DataEntity([], []));
        Assert.Equal(0, world.LiveCount("powered"));

        world.Grant("test", entity, GrantKind.Tag, "powered");

        Assert.Equal(1, world.LiveCount("powered"));
    }

    [Fact]
    public void A_rollback_drops_every_cached_tag_bitset_not_only_the_one_read_next()
    {
        var world = new World(seed: 2, valueScale: 1);
        var population = world.DefinePopulation<TwoTagKind>(enableHandles: true);
        population.AddRange(4);
        population.AddTag(0, "alpha");
        population.AddTag(1, "beta");
        population.AddTag(2, "alpha");
        population.AddTag(3, "beta");
        Assert.True(population.TryGetTagRowBits(0, out _));
        Assert.True(population.TryGetTagRowBits(1, out _));

        using (var transaction = world.BeginTransaction())
        {
            Assert.True(population.RemoveTag(1, "beta"));
            transaction.Rollback();
        }

        // Reading alpha first rebuilds it and, before the fix, advanced the shared epoch so beta kept
        // the bitset the rolled-back write had already patched.
        Assert.True(population.TryGetTagRowBits(0, out var alpha));
        Assert.True(population.TryGetTagRowBits(1, out var beta));
        Assert.Equal(0b0101UL, alpha[0]);
        Assert.Equal(0b1010UL, beta[0]);
    }

    [Fact]
    public void Cached_tag_queries_do_not_accumulate_across_world_changes()
    {
        var world = new World(seed: 1, valueScale: 1);
        for (var index = 0; index < 32; index++)
        {
            var tag = $"group-{index}";
            var entity = world.Add(new DataEntity([tag], []));
            Assert.Equal(entity.Id, world.FirstTagged(tag, string.Empty));
        }

        Assert.Equal(1, world.FirstTaggedCacheCount);

        world.Add(new DataEntity([], []));

        Assert.Equal(0, world.FirstTaggedCacheCount);
    }

    [Fact]
    public void Repeated_queries_do_not_allocate()
    {
        var world = new World(seed: 1, valueScale: 1);
        for (var index = 0; index < 64; index++)
        {
            world.Add(new DataEntity(["bolt"], []));
        }

        _ = world.EntitiesWithTag("bolt");
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var query = 0; query < 100; query++)
        {
            _ = world.EntitiesWithTag("bolt").Length;
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
    }

    [Fact]
    public void Concurrent_tag_queries_share_one_rebuild_of_the_index()
    {
        for (var repetition = 0; repetition < 20; repetition++)
        {
            var world = new World(seed: repetition, valueScale: 1);
            for (var index = 0; index < 2_000; index++)
            {
                world.Add(new DataEntity(index % 2 == 0 ? ["bolt"] : ["rock"], []));
            }

            var mismatches = 0;
            Parallel.For(0, 8, _ =>
            {
                if (world.EntitiesWithTag("bolt").Length != 1_000)
                {
                    Interlocked.Increment(ref mismatches);
                }

                if (world.FirstTagged("bolt", string.Empty) < 0)
                {
                    Interlocked.Increment(ref mismatches);
                }

                if (world.EntitiesWithTag("rock").Length != 1_000)
                {
                    Interlocked.Increment(ref mismatches);
                }
            });

            Assert.Equal(0, mismatches);
        }
    }

    private readonly struct TwoTagKind : IPopulationKind
    {
        public static string Id => "test.tag-query-two-tags";

        public static int Version => 1;

        public static string[] Tags => ["alpha", "beta"];
    }
}
