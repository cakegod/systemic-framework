using System.Diagnostics;
using Systemic.Kernel;
using Xunit.Abstractions;

namespace Systemic.Tests.Performance;

public sealed class TaggedResourcePaymentBudgetTests(ITestOutputHelper output)
{
    private const int NonmatchingEntityCount = 50_000;
    private const int CandidateCount = 1_000;
    private const int WarmupCount = 20;
    private const int SampleCount = 60;
    private readonly ITestOutputHelper _output = output;

    [Fact]
    [Trait("Category", "Performance")]
    public void Tagged_payment_reports_index_candidate_workload_cost()
    {
        var world = new World(seed: 812, valueScale: 1);
        for (var index = 0; index < NonmatchingEntityCount; index++)
        {
            world.Add(new DataEntity([], []));
        }

        var resources = new SimpleResourceEntity[CandidateCount];
        for (var index = 0; index < resources.Length; index++)
        {
            var resource = world.Add(new SimpleResourceEntity("ore", 1, "payment.ore"));
            resource.TryAdd(1);
            world.Grant("bench.use", resource, GrantKind.Capability, "payment.use");
            world.Grant("bench.spend", resource, GrantKind.Capability, EntityCapabilities.BeSpent);
            resources[index] = resource;
        }

        var payment = new TaggedResourcePayment("payment.ore", "payment.use", CandidateCount / 2);
        for (var index = 0; index < WarmupCount; index++)
        {
            ResetBalances(resources);
            _ = payment.TryPay(world);
        }

        var timings = new double[SampleCount];
        long allocatedBytes = 0;
        IReadOnlyList<ResourcePayment>? lastPayments = null;
        for (var index = 0; index < timings.Length; index++)
        {
            ResetBalances(resources);
            var allocationStart = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            lastPayments = payment.TryPay(world);
            timings[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            allocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        }

        Array.Sort(timings);
        Assert.NotNull(lastPayments);
        Assert.Equal(CandidateCount / 2, lastPayments.Count);
        Assert.Equal(CandidateCount / 2, lastPayments.Sum(item => item.Amount));
        Assert.Equal(resources[0].Id, lastPayments[0].PayerId);
        Assert.Equal(resources[(CandidateCount / 2) - 1].Id, lastPayments[^1].PayerId);

        _output.WriteLine($"Nonmatching={NonmatchingEntityCount}, candidates={CandidateCount}, payment={CandidateCount / 2}, warmup={WarmupCount}, samples={SampleCount}");
        _output.WriteLine($"Tagged payment p50/p95={Percentile(timings, 0.50):F3}/{Percentile(timings, 0.95):F3} ms; allocations={allocatedBytes} B total, {allocatedBytes / (double)SampleCount:F1} B/payment");
    }

    private static void ResetBalances(IEnumerable<SimpleResourceEntity> resources)
    {
        foreach (var resource in resources)
        {
            var missing = 1 - resource.Amount;
            if (missing > 0)
            {
                Assert.Equal(missing, resource.TryAdd(missing));
            }
        }
    }

    private static double Percentile(double[] sorted, double percentile) =>
        sorted[(int)Math.Ceiling(sorted.Length * percentile) - 1];
}
