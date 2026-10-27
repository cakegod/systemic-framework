namespace Systemic.Benchmarks;

internal static class MillionAgentBenchmarkSelfTest
{
    public static void Run()
    {
        var mount = Path.Combine(Path.GetTempPath(), "systemic-cgroup-test");
        if (BenchmarkEnvironmentReport.ResolveV2Path("/..", mount, "/") != Path.GetFullPath(mount)
            || BenchmarkEnvironmentReport.ResolveV2Path("/tenant", mount, "/tenant/team") != Path.Combine(Path.GetFullPath(mount), "team"))
        {
            throw new InvalidOperationException("Cgroup namespace path resolution failed.");
        }
        foreach (var safe in new[] { true, false })
        {
            var workload = new MillionAgentWorkload(17, safe);
            workload.Tick();
            workload.Tick();
            if (workload.Position(0) != (-2054, -2054)
                || workload.Position(1) != (-2051, -2037))
            {
                throw new InvalidOperationException("Integer movement produced unexpected sample positions.");
            }
            workload.Validate(2);
        }
    }
}
