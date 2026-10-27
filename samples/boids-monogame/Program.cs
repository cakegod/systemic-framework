using System.Globalization;
using System.Security.Cryptography;
using System.Diagnostics;
using Systemic.Kernel;
using Systemic.Content;
using Systemic.Host;

var seed = ReadOption(args, "--seed", RandomNumberGenerator.GetInt32(int.MaxValue));
var benchmarkSeconds = ReadOption(args, "--benchmark-seconds", 0);
var verifyRender = args.Contains("--verify-render", StringComparer.Ordinal);
var headlessBenchmark = args.Contains("--headless-benchmark", StringComparer.Ordinal);
if (headlessBenchmark)
{
    var warmupTicks = ReadOption(args, "--warmup-ticks", 1_000);
    var measuredTicks = ReadOption(args, "--ticks", 500);
    var directStep = args.Contains("--direct", StringComparer.Ordinal);
    var strictBudget = args.Contains("--strict-budget", StringComparer.Ordinal);
    var maximumTickMilliseconds = ReadDoubleOption(args, "--max-tick-ms", 5.0);
    var workerCount = ReadOption(args, "--workers", 0);
    var repetitions = ReadOption(args, "--repeat", 1);
    var tickLogPath = ReadStringOption(args, "--tick-log");
    if (repetitions <= 0)
    {
        throw new ArgumentOutOfRangeException(nameof(args), "Repeat count must be positive.");
    }

    for (var repetition = 1; repetition <= repetitions; repetition++)
    {
        var repetitionLogPath = tickLogPath is null || repetitions == 1
            ? tickLogPath
            : AddRunSuffix(tickLogPath, repetition);
        RunHeadlessBenchmark(seed, warmupTicks, measuredTicks, directStep, workerCount,
            maximumTickMilliseconds, strictBudget, repetition, repetitionLogPath);
    }

    return;
}

if (benchmarkSeconds < 0)
{
    throw new ArgumentOutOfRangeException(nameof(args), "Benchmark duration cannot be negative.");
}

static void RunHeadlessBenchmark(
    int seed,
    int warmupTicks,
    int measuredTicks,
    bool directStep,
    int workerCount,
    double maximumTickMilliseconds,
    bool strictBudget,
    int repetition,
    string? tickLogPath)
{
    if (warmupTicks < 0 || measuredTicks <= 0 || workerCount < 0
        || !double.IsFinite(maximumTickMilliseconds) || maximumTickMilliseconds <= 0.0)
    {
        throw new ArgumentOutOfRangeException(nameof(warmupTicks),
            "Warm-up and worker count must be non-negative; measured ticks and tick budget must be positive.");
    }

    if (strictBudget && directStep)
    {
        throw new ArgumentException("The strict budget gate measures the production scheduled path.", nameof(directStep));
    }

    var content = ContentPackLoader.Load(Path.Combine(AppContext.BaseDirectory, "content"));
    var materialized = content.CreateWorld(seed, valueScale: 1.0);
    using var flock = new Boids.MonoGame.BoidFlockSystem(materialized.World,
        materialized.EntityIds["flock.main"], seed, content.Manifest.TickRateHz);
    var hostBuilder = new Systemic.Host.SystemicGameBuilder(content, materialized)
        .AddService(flock)
        .AddSystem(flock);
    if (workerCount > 0)
    {
        hostBuilder.WithScheduledWorkerCount(workerCount);
    }

    using var host = hostBuilder.Build();
    var tickMilliseconds = new double[measuredTicks];
    SystemicSystemContext[] directContexts = [];
    if (directStep)
    {
        var rules = new Systemic.Rules.RuleEngine(content.Rules, tickRateHz: content.Manifest.TickRateHz);
        var extensions = new EventEngine([]);
        directContexts = [.. Enumerable.Range(1, checked(warmupTicks + measuredTicks)).Select(tick => new SystemicSystemContext(materialized.World, rules, extensions, tick))];
    }

    for (var tick = 0; tick < warmupTicks; tick++)
    {
        if (directStep)
        {
            materialized.World.LogicalTick = directContexts[tick].Tick;
            flock.Step(directContexts[tick]);
        }
        else
        {
            host.Step();
        }
    }

    // WHY: precise boundaries keep delayed warm-up allocation accounting out of the measured interval.
    var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
    var started = Stopwatch.GetTimestamp();
    for (var tick = 0; tick < measuredTicks; tick++)
    {
        var tickStarted = Stopwatch.GetTimestamp();
        if (directStep)
        {
            var context = directContexts[warmupTicks + tick];
            materialized.World.LogicalTick = context.Tick;
            flock.Step(context);
        }
        else
        {
            host.Step();
        }

        tickMilliseconds[tick] = Stopwatch.GetElapsedTime(tickStarted).TotalMilliseconds;
    }

    var elapsed = Stopwatch.GetElapsedTime(started);
    var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
    var report = Boids.MonoGame.TickBudgetReport.Evaluate(
        tickMilliseconds, allocatedBytes, maximumTickMilliseconds);
    if (tickLogPath is not null)
    {
        WriteTickLog(tickLogPath, tickMilliseconds);
    }

    var effectiveWorkerCount = directStep
        ? 1
        : workerCount == 0 ? Math.Clamp(Environment.ProcessorCount, 1, 4) : workerCount;
    Console.WriteLine($"headless mode={(directStep ? "direct" : "scheduled")} workers={effectiveWorkerCount} run={repetition} seed={seed} population={flock.Population} warmupTicks={warmupTicks} "
        + $"measuredTicks={measuredTicks} elapsedMs={elapsed.TotalMilliseconds:F2} "
        + $"meanMs={report.MeanMilliseconds:F3} maxMs={report.MaximumMilliseconds:F3} "
        + $"overBudgetTicks={report.OverBudgetTicks} allocatedBytes={report.AllocatedBytes} "
        + $"bytesPerTick={(double)report.AllocatedBytes / measuredTicks:F2} "
        + $"budgetMs={maximumTickMilliseconds:F2} gate={(report.Passed ? "pass" : "fail")}");
    if (strictBudget && !report.Passed)
    {
        Environment.ExitCode = 1;
    }

    GC.KeepAlive(host);
}

using var game = new Boids.MonoGame.BoidGame(seed, benchmarkSeconds, verifyRender);
game.Run();

static int ReadOption(string[] arguments, string option, int defaultValue)
{
    var index = Array.IndexOf(arguments, option);
    if (index < 0)
    {
        return defaultValue;
    }

    if (index + 1 < arguments.Length
        && int.TryParse(arguments[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
    {
        return value;
    }

    throw new ArgumentException($"Use '{option} <integer>'.", nameof(arguments));
}

static string? ReadStringOption(string[] arguments, string option)
{
    var index = Array.IndexOf(arguments, option);
    if (index < 0)
    {
        return null;
    }

    if (index + 1 < arguments.Length && !string.IsNullOrWhiteSpace(arguments[index + 1]))
    {
        return arguments[index + 1];
    }

    throw new ArgumentException($"Use '{option} <path>'.", nameof(arguments));
}

static string AddRunSuffix(string path, int repetition)
{
    var directory = Path.GetDirectoryName(path);
    var name = Path.GetFileNameWithoutExtension(path);
    var extension = Path.GetExtension(path);
    return Path.Combine(directory ?? string.Empty, $"{name}-run{repetition}{extension}");
}

static void WriteTickLog(string path, ReadOnlySpan<double> tickMilliseconds)
{
    var fullPath = Path.GetFullPath(path);
    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
    using var writer = new StreamWriter(fullPath, append: false);
    writer.WriteLine("tick,elapsed_ms");
    for (var tick = 0; tick < tickMilliseconds.Length; tick++)
    {
        writer.Write(tick.ToString(CultureInfo.InvariantCulture));
        writer.Write(',');
        writer.WriteLine(tickMilliseconds[tick].ToString("F6", CultureInfo.InvariantCulture));
    }
}

static double ReadDoubleOption(string[] arguments, string option, double defaultValue)
{
    var index = Array.IndexOf(arguments, option);
    if (index < 0)
    {
        return defaultValue;
    }

    if (index + 1 < arguments.Length
        && double.TryParse(arguments[index + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
    {
        return value;
    }

    throw new ArgumentException($"Use '{option} <milliseconds>'.", nameof(arguments));
}
