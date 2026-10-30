using Systemic.Kernel;

namespace Systemic.Tests.Kernel;

public sealed class WorldTransactionSeedTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-17)]
    [InlineData(int.MaxValue)]
    public void SeedIsPreservedByCloningAndAdoption(int seed)
    {
        var world = new World(seed, 1);
        var branch = world.Clone();
        Assert.Equal(seed, world.Seed);
        Assert.Equal(seed, branch.Seed);
        Assert.Equal(seed, branch.Clone().Seed);

        world.AdoptActionWorld(branch);

        Assert.Equal(seed, world.Seed);
        Assert.Equal(seed, branch.Seed);
    }

    [Fact]
    public void AdoptionRejectsAWorldWithADifferentSeedWithoutConsumingEitherWorld()
    {
        var world = new World(1, 1);
        var other = new World(2, 1);

        Assert.Throws<InvalidOperationException>(() => world.AdoptActionWorld(other));
        Assert.Equal(1, world.Seed);
        Assert.Equal(2, other.Seed);
        Assert.Equal(1, world.Clone().Seed);
        Assert.Equal(2, other.Clone().Seed);
    }
}
