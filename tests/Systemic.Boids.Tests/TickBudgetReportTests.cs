using Boids.MonoGame;

namespace Systemic.Tests.Boids;

public sealed class TickBudgetReportTests
{
    [Fact]
    public void TickAtTheLimitFailsTheStrictBudget()
    {
        var report = TickBudgetReport.Evaluate([1.0, 4.999, 5.0], allocatedBytes: 0, maximumTickMilliseconds: 5.0);

        Assert.False(report.Passed);
        Assert.Equal(5.0, report.MaximumMilliseconds);
        Assert.Equal(1, report.OverBudgetTicks);
    }

    [Fact]
    public void AnyWarmedAllocationFailsTheZeroAllocationGate()
    {
        var report = TickBudgetReport.Evaluate([1.0, 2.0], allocatedBytes: 1, maximumTickMilliseconds: 5.0);

        Assert.False(report.Passed);
        Assert.Equal(0, report.OverBudgetTicks);
        Assert.Equal(1, report.AllocatedBytes);
    }

    [Fact]
    public void BudgetPassesOnlyWhenEveryTickIsUnderLimitAndNoBytesAreAllocated()
    {
        var report = TickBudgetReport.Evaluate([1.0, 4.999], allocatedBytes: 0, maximumTickMilliseconds: 5.0);

        Assert.True(report.Passed);
        Assert.Equal(2.9995, report.MeanMilliseconds, precision: 4);
        Assert.Equal(0, report.OverBudgetTicks);
    }
}
