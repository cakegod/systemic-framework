using Systemic.Kernel;

namespace Systemic.Tests.Kernel;

public sealed class DeterministicRandomFactoryTests
{
    [Fact]
    public void CachedSystemStreamMatchesGoldenGeneratorWithoutPerRowAllocations()
    {
        var factory = new DeterministicRandomFactory(1, "movement");
        Assert.Equal(new DeterministicRandom(1, "movement", 2, 3).At(4), factory.ForEntity(2, 3).At(4));
        ulong sum = 0;
        for (ulong row = 0; row < 100; row++)
        {
            sum ^= factory.ForEntity(row, 7).At(0);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (ulong row = 0; row < 10_000; row++)
        {
            sum ^= factory.ForEntity(row, 7).At(0);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.NotEqual(0UL, sum);
        Assert.Equal(0, allocated);
    }
}
