using System.Diagnostics;
using System.Globalization;

namespace Systemic.Benchmarks;

// Measures the per-row rule budget: one masked threshold condition and one column write per
// agent, inside one World transaction per tick. The 60 Hz target is a rule-path claim, not a
// storage claim; see *Crowd target: one million thinking agents* in the remediation plan.
internal static class ThinkingAgentBenchmark
{
    private const int DefaultAgentCount = 1_000_000;
    // The simulation's share of a 30 Hz frame. At 60 Hz the frame is 16.7 ms and presentation needs
    // most of it, so 16.7 is a frame budget, not a simulation one.
    private const double DefaultTargetMs = 10.0;

    public static int Run(string[] arguments)
    {
        try
        {
            if (arguments is ["--help"])
            {
                PrintUsage();
                return 0;
            }

            var mode = "safe";
            var warmup = 20;
            var ticks = 100;
            var agentCount = DefaultAgentCount;
            var targetMs = DefaultTargetMs;
            var selfTest = false;
            for (var index = 0; index < arguments.Length; index++)
            {
                if (arguments[index] == "--self-test")
                {
                    selfTest = true;
                    continue;
                }

                if (index + 1 >= arguments.Length)
                {
                    throw new ArgumentException($"Missing value for {arguments[index]}.");
                }

                var value = arguments[++index];
                switch (arguments[index - 1])
                {
                    case "--mode": mode = value; break;
                    case "--agents": agentCount = ParseCount(value, "agent"); break;
                    case "--warmup-ticks": warmup = ParseCount(value, "warm-up"); break;
                    case "--ticks": ticks = ParseCount(value, "measured"); break;
                    case "--target-ms": targetMs = ParseTarget(value); break;
                    default: throw new ArgumentException($"Unknown option {arguments[index - 1]}.");
                }
            }

            if (mode is not ("safe" or "optimized" or "both"))
            {
                throw new ArgumentException("Mode must be safe, optimized, or both.");
            }

            if (warmup < 0 || ticks <= 0 || warmup > 100_000 || ticks > 100_000)
            {
                throw new ArgumentException("Warm-up must be 0–100,000 and measured ticks 1–100,000.");
            }

            if (agentCount <= 0 || agentCount > 100_000_000)
            {
                throw new ArgumentException("Agent count must be 1–100,000,000.");
            }

            if (!double.IsFinite(targetMs) || targetMs <= 0)
            {
                throw new ArgumentException("Target milliseconds must be a positive finite number.");
            }

            ThinkingAgentSelfTest.Run();
            if (selfTest)
            {
                Console.WriteLine("thinking-agent workload self-test passed");
                return 0;
            }

            BenchmarkEnvironmentReport.Write();
            Console.WriteLine($"workload thinking-agents rows={agentCount} "
                + "columns=int32-x-vx-health-ulong-mask update=advance-then-masked-threshold-write "
                + "schedule=single-threaded exclusions=rule-dispatch-spatial-query-rendering");
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
            Console.Error.WriteLine($"thinking-agent benchmark: {exception.Message}");
            return 1;
        }
    }

    private static ulong Measure(bool safe, int warmup, int ticks, int agentCount, double targetMs)
    {
        var workload = new ThinkingAgentWorkload(agentCount, safe);
        for (var index = 0; index < warmup; index++)
        {
            workload.Tick();
        }

        var samples = new double[ticks];
        var gen0Before = GC.CollectionCount(0);
        var gen2Before = GC.CollectionCount(2);
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        for (var index = 0; index < ticks; index++)
        {
            var started = Stopwatch.GetTimestamp();
            workload.Tick();
            samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var gen0 = GC.CollectionCount(0) - gen0Before;
        var gen2 = GC.CollectionCount(2) - gen2Before;
        var hash = workload.Validate(checked(warmup + ticks));
        Array.Sort(samples);
        var p50 = NearestRank(samples, 0.50);
        var p95 = NearestRank(samples, 0.95);
        var max = samples[^1];
        var nanosecondsPerRow = p50 * 1_000_000 / agentCount;
        Console.WriteLine(FormattableString.Invariant(
            $"result mode={(safe ? "safe-transactional" : "optimized-no-rollback")} agents={agentCount} warmupTicks={warmup} measuredTicks={ticks} p50Ms={p50:F3} p95Ms={p95:F3} maxMs={max:F3} targetP95Ms={targetMs:F1} targetMet={p95 < targetMs} p50NsPerRow={nanosecondsPerRow:F2} totalAllocatedBytes={allocated} bytesPerTick={(double)allocated / ticks:F2} gc0={gen0} gc2={gen2} stateHash={hash:x16}"));
        return hash;
    }

    private static double NearestRank(double[] sorted, double quantile) =>
        sorted[(int)Math.Ceiling(sorted.Length * quantile) - 1];

    private static int ParseCount(string value, string name) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            ? count : throw new ArgumentException($"Invalid {name} count '{value}'.");

    private static double ParseTarget(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var target)
            ? target : throw new ArgumentException($"Invalid target milliseconds '{value}'.");

    private static void PrintUsage() => Console.WriteLine(
        "--thinking-agents [--mode safe|optimized|both] [--agents N] [--warmup-ticks N] [--ticks N] "
        + "[--target-ms N]\n"
        + "--thinking-agents --self-test\n"
        + "Each agent advances x and applies one masked threshold write to health per tick; safe "
        + "(default) wraps the tick in a World transaction. --target-ms defaults to 10.0, the "
        + "simulation share of a 30 Hz frame; a 60 Hz frame is 16.7 ms and must also carry presentation.");
}
