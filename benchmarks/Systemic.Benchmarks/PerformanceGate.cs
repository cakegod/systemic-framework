using System.Diagnostics;
using System.Globalization;

namespace Systemic.Benchmarks;

internal static class PerformanceGate
{
    public static int Run(string[] arguments)
    {
        var scenarioName = ReadOption(arguments, "--scenario", "all");
        var warmupTicks = ReadOption(arguments, "--warmup-ticks", 1_000);
        var measuredTicks = ReadOption(arguments, "--ticks", 500);
        var repetitions = ReadOption(arguments, "--repeat", 3);
        var workerCount = ReadOption(arguments, "--workers", 0);
        // The crowd tier is the only scenario measured against the simulation's share of a 30 Hz
        // frame (10 ms), because one rule per agent at a million rows is what that budget is for.
        var defaultBudgetMilliseconds = scenarioName == "crowd" ? 10.0 : 5.0;
        var maximumTickMilliseconds = ReadDoubleOption(arguments, "--max-tick-ms", defaultBudgetMilliseconds);
        var logPath = ReadOption<string?>(arguments, "--tick-log", null);
        if (warmupTicks < 0 || measuredTicks <= 0 || repetitions <= 0 || workerCount < 0
            || !double.IsFinite(maximumTickMilliseconds) || maximumTickMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(arguments),
                "Warm-up must be non-negative; measured ticks, repetitions, and timing budget must be positive.");
        }

        var scenarios = scenarioName switch
        {
            "all" => new[] { "producer", "reactive" },
            "content" or "crowd" => [scenarioName],
            "producer" or "reactive" or "reactive-query" or "reactive-dispatch" => [scenarioName],
            _ => throw new ArgumentException(
                "Scenario must be 'all', 'content', 'crowd', 'producer', 'reactive', 'reactive-query', or 'reactive-dispatch'.",
                nameof(arguments)),
        };

        var passed = true;
        foreach (var scenario in scenarios)
        {
            for (var repetition = 1; repetition <= repetitions; repetition++)
            {
                using var workload = CreateScenario(scenario, workerCount, arguments);
                for (var warmup = 0; warmup < warmupTicks; warmup++)
                {
                    workload.Tick();
                }

                var timings = new double[measuredTicks];
                var threadAllocations = new long[measuredTicks];
                var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
                for (var index = 0; index < timings.Length; index++)
                {
                    var threadAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                    var started = Stopwatch.GetTimestamp();
                    workload.Tick();
                    timings[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    threadAllocations[index] = GC.GetAllocatedBytesForCurrentThread() - threadAllocatedBefore;
                }

                var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
                workload.Validate(warmupTicks + measuredTicks);
                var maximum = 0d;
                var mean = 0d;
                var overBudget = 0;
                var threadAllocatedBytes = 0L;
                var maxThreadAllocatedBytes = 0L;
                foreach (var elapsed in timings)
                {
                    maximum = Math.Max(maximum, elapsed);
                    mean += elapsed;
                    if (elapsed >= maximumTickMilliseconds)
                    {
                        overBudget++;
                    }
                }

                foreach (var allocated in threadAllocations)
                {
                    threadAllocatedBytes += allocated;
                    maxThreadAllocatedBytes = Math.Max(maxThreadAllocatedBytes, allocated);
                }

                mean /= timings.Length;
                var sortedTimings = (double[])timings.Clone();
                Array.Sort(sortedTimings);
                var p50 = Percentile(sortedTimings, 0.50);
                var p95 = Percentile(sortedTimings, 0.95);
                var p99 = Percentile(sortedTimings, 0.99);
                var runLogPath = logPath is null ? null : GetRunLogPath(logPath, scenario, repetition, repetitions);
                if (runLogPath is not null)
                {
                    WriteTickLog(runLogPath, timings);
                }

                var maximumAllocatedBytes = workload.MaximumBytesPerTick * measuredTicks;
                var runPassed = overBudget == 0 && allocatedBytes <= maximumAllocatedBytes;
                passed &= runPassed;
                Console.WriteLine($"gate scenario={scenario} run={repetition} warmupTicks={warmupTicks} "
                    + $"measuredTicks={measuredTicks} meanMs={mean:F3} p50Ms={p50:F3} "
                    + $"p95Ms={p95:F3} p99Ms={p99:F3} maxMs={maximum:F3} "
                    + $"overBudgetTicks={overBudget} allocatedBytes={allocatedBytes} "
                    + $"threadAllocatedBytes={threadAllocatedBytes} maxTickAllocatedBytes={maxThreadAllocatedBytes} "
                    + $"bytesPerTick={(double)allocatedBytes / measuredTicks:F2} "
                    + $"byteBudgetPerTick={workload.MaximumBytesPerTick} "
                    + $"budgetMs={maximumTickMilliseconds:F2} result={(runPassed ? "pass" : "fail")}");
            }
        }

        return passed ? 0 : 1;
    }

    private static GateWorkload CreateScenario(string scenario, int workerCount, string[] arguments) => scenario switch
    {
        // The content tier owns the whole game step, so it ignores the worker count.
        "content" => CreateContentTier(),
        "crowd" => CreateCrowdTier(arguments),
        "producer" => CreatePopulationProducer(workerCount),
        "reactive" => CreateReactiveScenario(static workload => workload.ExecuteTick()),
        "reactive-query" => CreateReactiveScenario(static workload => workload.ExecuteQueryTick()),
        "reactive-dispatch" => CreateReactiveScenario(static workload => workload.ExecuteDispatchTick()),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
    };

    // The content tier owns the whole game step, so it ignores the worker count. Its contract allows
    // 4 KiB per tick for the entity and rule path; the kernel scenarios must allocate nothing.
    private static GateWorkload CreateContentTier()
    {
        var workload = new ContentTierWorkload();
        return new GateWorkload(workload.Tick, workload.Validate, ContentTierWorkload.MaximumBytesPerTick, workload);
    }

    private static GateWorkload CreateCrowdTier(string[] arguments)
    {
        var agents = ReadOption(arguments, "--agents", CrowdTierWorkload.AgentCount);
        var contentRoot = ReadOption<string?>(arguments, "--content-root", null)
            ?? CrowdTierWorkload.ResolveContentRoot();
        var workload = new CrowdTierWorkload(agents, contentRoot);
        return new GateWorkload(workload.Tick, workload.Validate, CrowdTierWorkload.MaximumBytesPerTick, workload);
    }

    private static GateWorkload CreatePopulationProducer(int workerCount)
    {
        var workload = new PopulationProducerWorkload(workerCount);
        return new GateWorkload(workload.Tick, workload.Validate, MaximumBytesPerTick: 0, workload);
    }

    private static GateWorkload CreateReactiveScenario(Action<ReactiveWorldBenchmarks> tick)
    {
        var workload = new ReactiveWorldBenchmarks();
        workload.Setup();
        return new GateWorkload(() => tick(workload), static _ => { }, MaximumBytesPerTick: 0);
    }

    private sealed record GateWorkload(
        Action Tick,
        Action<int> Validate,
        long MaximumBytesPerTick,
        IDisposable? Owner = null) : IDisposable
    {
        public void Dispose() => Owner?.Dispose();
    }

    private static string ReadOption(string[] arguments, string name, string fallback) =>
        ReadOption<string>(arguments, name, fallback)!;

    private static double Percentile(double[] sortedValues, double percentile) =>
        sortedValues[(int)Math.Ceiling(sortedValues.Length * percentile) - 1];

    private static T ReadOption<T>(string[] arguments, string name, T fallback)
    {
        var index = Array.IndexOf(arguments, name);
        if (index < 0)
        {
            return fallback;
        }

        if (index + 1 >= arguments.Length)
        {
            throw new ArgumentException($"Use '{name} <value>'.", nameof(arguments));
        }

        var value = arguments[index + 1];
        if (typeof(T) == typeof(string))
        {
            return (T)(object)value;
        }

        if (typeof(T) == typeof(int)
            && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            return (T)(object)integer;
        }

        throw new ArgumentException($"'{value}' is not a valid value for '{name}'.", nameof(arguments));
    }

    private static double ReadDoubleOption(string[] arguments, string name, double fallback)
    {
        var index = Array.IndexOf(arguments, name);
        if (index < 0)
        {
            return fallback;
        }

        return index + 1 < arguments.Length
            && double.TryParse(arguments[index + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new ArgumentException($"Use '{name} <milliseconds>'.", nameof(arguments));
    }

    private static string GetRunLogPath(string path, string scenario, int repetition, int repetitions)
    {
        var directory = Path.GetDirectoryName(path);
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var suffix = repetitions == 1 ? scenario : $"{scenario}-run{repetition}";
        return Path.Combine(directory ?? string.Empty, $"{name}-{suffix}{extension}");
    }

    private static void WriteTickLog(string path, ReadOnlySpan<double> timings)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        using var writer = new StreamWriter(fullPath, append: false);
        writer.WriteLine("tick,elapsed_ms");
        for (var index = 0; index < timings.Length; index++)
        {
            writer.Write(index.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.WriteLine(timings[index].ToString("F6", CultureInfo.InvariantCulture));
        }
    }
}
