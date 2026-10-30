using System.Diagnostics;
using System.Globalization;

namespace Systemic.Benchmarks;

internal static class MillionAgentBenchmark
{
    private const int DefaultAgentCount = 1_000_000;
    private const double DefaultTargetMs = 8.0;

    public static int Run(string[] arguments)
    {
        try
        {
            if (arguments is ["--help"])
            {
                PrintUsage();
                return 0;
            }
            if (arguments is ["--self-test"])
            {
                MillionAgentBenchmarkSelfTest.Run();
                Console.WriteLine("million-agent workload self-test passed");
                return 0;
            }

            var mode = "safe";
            var warmup = 20;
            var ticks = 100;
            var agentCount = DefaultAgentCount;
            var targetMs = DefaultTargetMs;
            for (var index = 0; index < arguments.Length; index++)
            {
                if (index + 1 >= arguments.Length)
                {
                    throw new ArgumentException($"Missing value for {arguments[index]}.");
                }

                var value = arguments[++index];
                switch (arguments[index - 1])
                {
                    case "--mode": mode = value; break;
                    case "--warmup-ticks": warmup = ParseCount(value, "warm-up"); break;
                    case "--ticks": ticks = ParseCount(value, "measured"); break;
                    case "--agents": agentCount = ParseCount(value, "agent"); break;
                    case "--target-ms": targetMs = ParseTarget(value); break;
                    default: throw new ArgumentException($"Unknown option {arguments[index - 1]}.");
                }
            }
            if (mode is not ("safe" or "optimized" or "both"))
            {
                throw new ArgumentException("Mode must be safe, optimized, or both.");
            }

            if (warmup < 0 || ticks <= 0 || warmup > 1_000_000 || ticks > 1_000_000)
            {
                throw new ArgumentException("Warm-up must be 0–1,000,000 and measured ticks 1–1,000,000.");
            }

            if (agentCount <= 0 || agentCount > 100_000_000)
            {
                throw new ArgumentException("Agent count must be 1–100,000,000.");
            }

            if (!double.IsFinite(targetMs) || targetMs <= 0)
            {
                throw new ArgumentException("Target milliseconds must be a positive finite number.");
            }

            MillionAgentBenchmarkSelfTest.Run();
            BenchmarkEnvironmentReport.Write();
            Console.WriteLine($"workload agents={agentCount} columns=int32-x-y-vx-vy update=unchecked-x-plus-vx-y-plus-vy "
                + "schedule=single-threaded exclusions=host-scheduler-rules-collisions-rendering");
            ulong? safeHash = null;
            if (mode is "safe" or "both")
            {
                safeHash = Measure(safe: true, warmup, ticks, agentCount, targetMs);
            }

            if (mode is "optimized" or "both")
            {
                var optimizedHash = Measure(safe: false, warmup, ticks, agentCount, targetMs);
                if (safeHash is { } expected && expected != optimizedHash)
                {
                    throw new InvalidOperationException("Safe and optimized modes produced different results.");
                }
            }
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            Console.Error.WriteLine($"million-agent benchmark: {exception.Message}");
            return 1;
        }
    }

    private static ulong Measure(bool safe, int warmup, int ticks, int agentCount, double targetMs)
    {
        var workload = new MillionAgentWorkload(agentCount, safe);
        for (var index = 0; index < warmup; index++)
        {
            workload.Tick();
        }

        var samples = new double[ticks];
        var gen0Before = GC.CollectionCount(0);
        var gen1Before = GC.CollectionCount(1);
        var gen2Before = GC.CollectionCount(2);
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var threadBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < ticks; index++)
        {
            var started = Stopwatch.GetTimestamp();
            workload.Tick();
            samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        var threadAllocated = GC.GetAllocatedBytesForCurrentThread() - threadBefore;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var gen0 = GC.CollectionCount(0) - gen0Before;
        var gen1 = GC.CollectionCount(1) - gen1Before;
        var gen2 = GC.CollectionCount(2) - gen2Before;
        var hash = workload.Validate(checked(warmup + ticks));
        Array.Sort(samples);
        var p50 = NearestRank(samples, 0.50);
        var p95 = NearestRank(samples, 0.95);
        var max = samples[^1];
        Console.WriteLine(FormattableString.Invariant(
            $"result mode={(safe ? "safe-transactional" : "optimized-no-rollback")} warmupTicks={warmup} measuredTicks={ticks} p50Ms={p50:F3} p95Ms={p95:F3} maxMs={max:F3} targetP95Ms={targetMs:F1} targetMet={p95 < targetMs} totalAllocatedBytes={allocated} threadAllocatedBytes={threadAllocated} bytesPerTick={(double)allocated / ticks:F2} gc0={gen0} gc1={gen1} gc2={gen2} stateHash={hash:x16} validatedRows={agentCount}"));
        return hash;
    }

    private static double NearestRank(double[] sorted, double quantile) =>
        sorted[(int)Math.Ceiling(sorted.Length * quantile) - 1];

    private static int ParseCount(string value, string name) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            ? count : throw new ArgumentException($"Invalid {name} tick count '{value}'.");

    private static double ParseTarget(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var target)
            ? target : throw new ArgumentException($"Invalid target milliseconds '{value}'.");

    private static void PrintUsage() => Console.WriteLine(
        "--million-agents [--mode safe|optimized|both] [--warmup-ticks N] [--ticks N] [--agents N] [--target-ms N]\n"
        + "--million-agents --self-test\n"
        + "Safe (default) wraps each tick in a World transaction; optimized explicitly skips rollback.");
}
