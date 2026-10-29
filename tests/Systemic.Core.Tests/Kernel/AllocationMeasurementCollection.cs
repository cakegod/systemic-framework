using Xunit;

namespace Systemic.Tests.Kernel;

/// <summary>
/// Allocation assertions compare <see cref="GC.GetAllocatedBytesForCurrentThread"/> deltas against
/// exactly zero, so unrelated tests must not be collecting and re-filling allocation contexts on
/// other threads while the measured window is open — a GC started by a neighbouring test lands in the
/// window as a one-off few-hundred-byte delta that is not an allocation. Tests in this collection do
/// not run alongside other collections.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AllocationMeasurementCollection
{
    public const string Name = "Allocation measurements";
}
