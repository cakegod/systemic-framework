using BenchmarkDotNet.Attributes;
using Systemic.Kernel;

namespace Systemic.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob]
public class ScheduledRangeBenchmarks
{
    private int[] _values = [];
    private World _world = null!;
    private DeterministicSystemSchedule _schedule = null!;
    private RangeSystem _system = null!;

    [Params(100_000, 1_000_000)]
    public int Rows { get; set; }

    [Params(1, 2, 4, 8)]
    public int Workers { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _values = new int[Rows];
        _world = new World(seed: 1, valueScale: 1);
        _system = new RangeSystem(_values);
        _schedule = new DeterministicSystemScheduleBuilder(Workers).Add(_system).Build();
        _schedule.Execute(_world, 0);
    }

    [Benchmark(Baseline = true)]
    public void DirectRange() => RangeSystem.Execute(_values, new PopulationRange(0, _values.Length));

    [Benchmark]
    public void ScheduledRanges() => _schedule.Execute(_world, 1);

    [GlobalCleanup]
    public void Cleanup() => _schedule.Dispose();

    private sealed class RangeSystem(int[] values) : IScheduledKernelSystem
    {
        private static readonly Action<int[], int, PopulationRange> s_execute =
            static (state, _, range) => Execute(state, range);

        public string Id => "benchmark.range";
        public IReadOnlyList<SystemAccess> Access => s_access;
        private static readonly SystemAccess[] s_access = [SystemAccess.WriteResource("benchmark.rows")];

        public void Execute(ScheduledSystemContext context) => context.ForEachRange(values, values.Length, s_execute);

        public static void Execute(int[] destination, PopulationRange range)
        {
            for (var index = range.Start; index < range.End; index++)
            {
                destination[index] = destination[index] + 1;
            }
        }
    }
}
