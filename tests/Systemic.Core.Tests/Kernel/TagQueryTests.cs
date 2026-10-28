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
}
