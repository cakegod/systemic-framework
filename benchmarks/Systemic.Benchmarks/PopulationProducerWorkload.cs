using Systemic.Kernel;

namespace Systemic.Benchmarks;

internal sealed class PopulationProducerWorkload : IDisposable
{
    private const int ProducerCount = 50_000;
    private static readonly PopulationColumnKey<double> s_producedKey =
        new("benchmark.producer.produced", 1, PopulationCodecs.Double);

    private readonly World _world = new(seed: 1, valueScale: 1);
    private readonly Population<ProducerKind> _population;
    private readonly PopulationColumn<double> _produced;
    private readonly DeterministicSystemSchedule _schedule;
    private int _tick;

    public PopulationProducerWorkload(int workerCount)
    {
        _population = _world.DefinePopulation<ProducerKind>();
        _produced = _population.Column(s_producedKey);
        var rows = _population.AddRange(ProducerCount);
        _produced.EditRange(rows.Start, rows.Length).Clear();
        _schedule = new DeterministicSystemScheduleBuilder(workerCount)
            .Add(new IncrementProducerSystem(_produced, ProducerCount))
            .Build();
    }

    public void Tick() => _schedule.Execute(_world, ++_tick);

    public void Validate(int expectedTicks)
    {
        var values = _produced.ReadOnlySpan;
        if (values.Length != ProducerCount)
        {
            throw new InvalidOperationException($"Expected {ProducerCount} producers, found {values.Length}.");
        }

        for (var index = 0; index < values.Length; index++)
        {
            if (values[index] != expectedTicks)
            {
                throw new InvalidOperationException(
                    $"Producer {index} advanced to {values[index]} instead of {expectedTicks}.");
            }
        }
    }

    public void Dispose() => _schedule.Dispose();

    private readonly struct ProducerKind : IPopulationKind
    {
        public static string Id => "benchmark.producers";

        public static int Version => 1;
    }

    private sealed class IncrementProducerSystem(
        PopulationColumn<double> produced,
        int producerCount) : IScheduledKernelSystem
    {
        private static readonly SystemAccess[] s_access =
            [SystemAccess.WriteColumn<ProducerKind, double>(s_producedKey)];
        private static readonly Action<Memory<double>, int, PopulationRange> s_incrementRange = IncrementRange;

        public string Id => "benchmark.increment_producers";

        public IReadOnlyList<SystemAccess> Access => s_access;

        public void Execute(ScheduledSystemContext context)
        {
            var values = produced.EditMemory(0, producerCount);
            context.ForEachRange(values, producerCount, s_incrementRange);
        }

        private static void IncrementRange(Memory<double> values, int _, PopulationRange range)
        {
            var rows = values.Span.Slice(range.Start, range.Length);
            for (var index = 0; index < rows.Length; index++)
            {
                rows[index] += 1;
            }
        }
    }
}
