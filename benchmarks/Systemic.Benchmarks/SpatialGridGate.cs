using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Systemic.Kernel;

namespace Systemic.Benchmarks;

/// <summary>
/// Measures the three kernel grids — the unbounded entity index, the rebuilt population grid and the
/// incremental population grid — on one comparable scene, and cross-checks each against a brute-force
/// oracle so a timing number is never reported for results that are wrong.
/// </summary>
/// <remarks>
/// Run with <c>dotnet run -c Release --project benchmarks/Systemic.Benchmarks -- --spatial</c>.
/// Numbers are reported, not gated on time; the only hard failures are a parity mismatch or an
/// allocation, because the kernel tier promises both.
/// </remarks>
internal static class SpatialGridGate
{
    private const int EntityCount = 2_000;
    private const int PopulationCount = 100_000;
    private const float WorldHalf = 1_000f;
    private const float EntryHalfExtent = 8f;
    private const float QueryRadius = 100f;

    private static readonly Bounds2 World = new(0, 0, WorldHalf, WorldHalf);

    private static readonly int[] EntityMoves = Enumerable.Range(0, EntityCount / 10).ToArray();
    private static readonly int[] PopulationMoves = Enumerable.Range(0, PopulationCount / 50).ToArray();

    public static int Run(string[] arguments)
    {
        var iterations = ReadOption(arguments, "--iterations", 50);
        var repetitions = ReadOption(arguments, "--repeat", 3);
        if (iterations <= 0 || repetitions <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(arguments), "Iterations and repetitions must be positive.");
        }

        var passed = true;
        passed &= RunEntityIndex(iterations, repetitions);
        passed &= RunPopulationGrid(iterations, repetitions);
        passed &= RunIncrementalPopulationGrid(iterations, repetitions);
        passed &= CheckParity();
        Console.WriteLine($"spatial result={(passed ? "pass" : "fail")}");
        return passed ? 0 : 1;
    }

    private static bool RunEntityIndex(int iterations, int repetitions)
    {
        var positions = CreatePositions(EntityCount, seed: 17);
        var index = new SpatialIndex(32);
        for (var entityId = 1; entityId <= EntityCount; entityId++)
        {
            index.Upsert(entityId, EntryBounds(positions[entityId - 1]));
        }

        var results = new List<int>();
        var moved = 0;
        var passed = true;
        // Upsert creates a cell bucket the first time an entry occupies a previously empty cell, so
        // this op reports its allocations rather than promising none; the queries below promise none.
        passed &= Measure("entity-index", "upsert-churn", EntityMoves.Length, EntityCount, iterations, repetitions,
            cleanAllocations: false, () =>
            {
                foreach (var id in EntityMoves)
                {
                    var target = (moved++ * 7919) % EntityCount;
                    index.Upsert(id + 1, EntryBounds(positions[target]));
                }
            });

        passed &= Measure("entity-index", "radius-query", 200, EntityCount, iterations, repetitions,
            cleanAllocations: true, () =>
            {
                var checksum = 0;
                for (var query = 0; query < 200; query++)
                {
                    index.QueryNearby(new Point2(positions[query].X, positions[query].Y), QueryRadius, results);
                    checksum += results.Count;
                }

                return checksum;
            });

        passed &= Measure("entity-index", "overlap-query", 200, EntityCount, iterations, repetitions,
            cleanAllocations: true, () =>
            {
                var checksum = 0;
                for (var query = 0; query < 200; query++)
                {
                    var center = positions[query];
                    index.QueryOverlaps(new Bounds2(center.X, center.Y, QueryRadius, QueryRadius), results);
                    checksum += results.Count;
                }

                return checksum;
            });

        return passed;
    }

    private static bool RunPopulationGrid(int iterations, int repetitions)
    {
        var positions = CreatePositions(PopulationCount, seed: 23);
        var grid = new PopulationSpatialGrid2D(World, 24);
        grid.Reserve(PopulationCount);
        grid.Rebuild(positions);

        var results = new List<int>();
        var passed = true;
        passed &= Measure("population-grid", "rebuild", 1, PopulationCount, iterations, repetitions,
            cleanAllocations: true, () =>
            {
                grid.Rebuild(positions);
                return grid.Count;
            });

        passed &= Measure("population-grid", "radius-query", 2_000, PopulationCount, iterations, repetitions,
            cleanAllocations: true, () =>
            {
                var checksum = 0;
                for (var query = 0; query < 2_000; query++)
                {
                    grid.QueryRadius(positions[query * 37 % PopulationCount], QueryRadius, results);
                    checksum += results.Count;
                }

                return checksum;
            });

        passed &= Measure("population-grid", "nearest", 2_000, PopulationCount, iterations, repetitions,
            cleanAllocations: true, () =>
            {
                var checksum = 0;
                for (var query = 0; query < 2_000; query++)
                {
                    if (grid.TryQueryNearest(positions[query * 41 % PopulationCount], out var row))
                    {
                        checksum += row;
                    }
                }

                return checksum;
            });

        return passed;
    }

    private static bool RunIncrementalPopulationGrid(int iterations, int repetitions)
    {
        var positions = CreatePositions(PopulationCount, seed: 29);
        var grid = new IncrementalPopulationSpatialGrid2D(World, 24);
        for (var row = 0; row < positions.Length; row++)
        {
            grid.Add(row, positions[row]);
        }

        var results = new List<int>();
        var passed = true;
        passed &= Measure("population-incremental", "update-churn", PopulationMoves.Length, PopulationCount,
            iterations, repetitions, cleanAllocations: true, () =>
            {
                foreach (var row in PopulationMoves)
                {
                    grid.Update(row, positions[(row * 13 + 5) % PopulationCount]);
                }
            });

        passed &= Measure("population-incremental", "radius-query", 2_000, PopulationCount, iterations, repetitions,
            cleanAllocations: true, () =>
            {
                var checksum = 0;
                for (var query = 0; query < 2_000; query++)
                {
                    grid.QueryRadius(positions[query * 37 % PopulationCount], QueryRadius, results);
                    checksum += results.Count;
                }

                return checksum;
            });

        return passed;
    }

    private static bool CheckParity()
    {
        var passed = true;
        var positions = CreatePositions(4_000, seed: 31);
        var expected = new List<int>();
        var actual = new List<int>();

        var index = new SpatialIndex(32);
        for (var entityId = 1; entityId <= positions.Length; entityId++)
        {
            index.Upsert(entityId, EntryBounds(positions[entityId - 1]));
        }

        var indexMismatches = 0;
        foreach (var (wrappedX, wrappedY) in new[] { (false, false), (true, true) })
        {
            var grid = new PopulationSpatialGrid2D(World, 32, wrappedX, wrappedY);
            grid.Reserve(positions.Length);
            grid.Rebuild(positions);
            var incremental = new IncrementalPopulationSpatialGrid2D(World, 32, wrappedX, wrappedY);
            for (var row = 0; row < positions.Length; row++)
            {
                incremental.Add(row, positions[row]);
            }

            for (var query = 0; query < positions.Length; query += 7)
            {
                var center = positions[query];
                OracleRadius(positions, center, QueryRadius, wrappedX, wrappedY, expected);
                grid.QueryRadius(center, QueryRadius, actual);
                indexMismatches += Compare(actual, expected, "population-grid", wrappedX, wrappedY);
                incremental.QueryRadius(center, QueryRadius, actual);
                indexMismatches += Compare(actual, expected, "population-incremental", wrappedX, wrappedY);
            }
        }

        // The entity index has no wrap support, so it is compared on the unwrapped scene only, and
        // its entries are boxes: an entry counts when its edge, not its center, is within the radius.
        var entityMismatches = 0;
        for (var query = 0; query < positions.Length; query += 7)
        {
            var center = positions[query];
            OracleBoxRadius(positions, center, QueryRadius, expected);
            index.QueryNearby(new Point2(center.X, center.Y), QueryRadius, actual);
            entityMismatches += CopyAndCompare(actual, expected, "entity-index");
        }

        passed &= indexMismatches == 0 && entityMismatches == 0;
        if (entityMismatches == 0)
        {
            Console.WriteLine("spatial parity=entity-index-radius against-brute-force mismatches=0 result=pass");
        }

        return passed;
    }

    private static int Compare(List<int> actual, List<int> expected, string source, bool wrapX, bool wrapY)
    {
        if (actual.Count == expected.Count)
        {
            var equal = true;
            for (var position = 0; position < actual.Count; position++)
            {
                if (actual[position] != expected[position])
                {
                    equal = false;
                    break;
                }
            }

            if (equal)
            {
                return 0;
            }
        }

        Console.WriteLine($"spatial parity={source} wrapX={wrapX} wrapY={wrapY} expected={expected.Count} "
            + $"actual={actual.Count} result=fail");
        return 1;
    }

    private static int CopyAndCompare(List<int> actual, List<int> expected, string source)
    {
        actual.Sort();
        return Compare(actual, expected, source, wrapX: false, wrapY: false);
    }

    private static void OracleRadius(
        IReadOnlyList<Vector2> positions,
        Vector2 center,
        float radius,
        bool wrapX,
        bool wrapY,
        List<int> results)
    {
        results.Clear();
        var limit = (double)radius * radius;
        for (var row = 0; row < positions.Count; row++)
        {
            var position = positions[row];
            var dx = Math.Abs((double)center.X - position.X);
            var dy = Math.Abs((double)center.Y - position.Y);
            if (wrapX)
            {
                dx %= WorldHalf * 2d;
                dx = Math.Min(dx, (WorldHalf * 2d) - dx);
            }

            if (wrapY)
            {
                dy %= WorldHalf * 2d;
                dy = Math.Min(dy, (WorldHalf * 2d) - dy);
            }

            if ((dx * dx) + (dy * dy) <= limit)
            {
                results.Add(row);
            }
        }
    }

    private static void OracleBoxRadius(
        IReadOnlyList<Vector2> positions,
        Vector2 center,
        float radius,
        List<int> results)
    {
        results.Clear();
        var limit = (double)radius * radius;
        for (var row = 0; row < positions.Count; row++)
        {
            var position = positions[row];
            var dx = Math.Max(Math.Abs((double)center.X - position.X) - EntryHalfExtent, 0d);
            var dy = Math.Max(Math.Abs((double)center.Y - position.Y) - EntryHalfExtent, 0d);
            if ((dx * dx) + (dy * dy) <= limit)
            {
                results.Add(row + 1);
            }
        }
    }

    private static Vector2[] CreatePositions(int count, int seed)
    {
        var random = new Random(seed);
        var positions = new Vector2[count];
        for (var row = 0; row < count; row++)
        {
            positions[row] = new Vector2(
                (random.NextSingle() * 2f * WorldHalf) - WorldHalf,
                (random.NextSingle() * 2f * WorldHalf) - WorldHalf);
        }

        return positions;
    }

    private static Bounds2 EntryBounds(Vector2 position) =>
        new(position.X, position.Y, EntryHalfExtent, EntryHalfExtent);

    private static bool Measure(
        string scenario,
        string operation,
        int operationsPerIteration,
        int count,
        int iterations,
        int repetitions,
        bool cleanAllocations,
        Action action) => Measure(scenario, operation, operationsPerIteration, count, iterations, repetitions,
            cleanAllocations, () =>
            {
                action();
                return 0;
            });

    private static bool Measure(
        string scenario,
        string operation,
        int operationsPerIteration,
        int count,
        int iterations,
        int repetitions,
        bool cleanAllocations,
        Func<int> action)
    {
        for (var warmup = 0; warmup < Math.Max(3, iterations / 10); warmup++)
        {
            action();
        }

        var passed = true;
        for (var repetition = 1; repetition <= repetitions; repetition++)
        {
            var timings = new double[iterations];
            var allocated = 0L;
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                var started = Stopwatch.GetTimestamp();
                action();
                timings[iteration] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                allocated += GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            }

            Array.Sort(timings);
            var mean = 0d;
            foreach (var elapsed in timings)
            {
                mean += elapsed;
            }

            mean /= timings.Length;
            var p95 = timings[(int)Math.Ceiling(timings.Length * 0.95) - 1];
            var allocatedPerOperation = (double)allocated / (iterations * operationsPerIteration);
            var runPassed = !cleanAllocations || allocated == 0;
            passed &= runPassed;
            Console.WriteLine($"spatial scenario={scenario} op={operation} count={count} "
                + $"operationsPerIteration={operationsPerIteration} run={repetition} iterations={iterations} "
                + $"meanUsPerIteration={(mean * 1_000).ToString("F2", CultureInfo.InvariantCulture)} "
                + $"p95UsPerIteration={(p95 * 1_000).ToString("F2", CultureInfo.InvariantCulture)} "
                + $"meanNsPerOperation={(mean * 1_000_000 / operationsPerIteration).ToString("F1", CultureInfo.InvariantCulture)} "
                + $"p95NsPerOperation={(p95 * 1_000_000 / operationsPerIteration).ToString("F1", CultureInfo.InvariantCulture)} "
                + $"allocatedBytes={allocated} allocatedBytesPerOperation={allocatedPerOperation.ToString("F3", CultureInfo.InvariantCulture)} "
                + $"result={(runPassed ? "pass" : "fail")}");
        }

        return passed;
    }

    private static int ReadOption(string[] arguments, string name, int fallback)
    {
        var index = Array.IndexOf(arguments, name);
        if (index < 0)
        {
            return fallback;
        }

        return index + 1 < arguments.Length
            && int.TryParse(arguments[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new ArgumentException($"Use '{name} <value>'.", nameof(arguments));
    }
}
