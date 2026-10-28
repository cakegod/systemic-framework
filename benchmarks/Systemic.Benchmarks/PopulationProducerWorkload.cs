using System.Collections.Immutable;
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
        _population.WithAccess(rows, _produced, static (PopulationColumn<double> produced, scoped ref PopulationAccessScope<ProducerKind> scope) =>
            scope.EditRange(produced).Clear());
        _schedule = new DeterministicSystemScheduleBuilder(workerCount)
            .Add(new IncrementProducerSystem(_population, _produced))
            .Build();
    }

    public void Tick() => _schedule.Execute(_world, ++_tick);

    public void Validate(int expectedTicks)
    {
        _population.WithAccess(new PopulationRange(0, _population.Count), (_produced, expectedTicks), static ((PopulationColumn<double> Produced, int ExpectedTicks) state, scoped ref PopulationAccessScope<ProducerKind> scope) =>
        {
            var values = scope.ReadAll(state.Produced);
            if (values.Length != ProducerCount)
            {
                throw new InvalidOperationException($"Expected {ProducerCount} producers, found {values.Length}.");
            }

            for (var index = 0; index < values.Length; index++)
            {
                if (values[index] != state.ExpectedTicks)
                {
                    throw new InvalidOperationException(
                        $"Producer {index} advanced to {values[index]} instead of {state.ExpectedTicks}.");
                }
            }
        });
    }

    public void Dispose() => _schedule.Dispose();

    private readonly struct ProducerKind : IPopulationKind
    {
        public static string Id => "benchmark.producers";

        public static int Version => 1;
    }

    private sealed class IncrementProducerSystem(
        Population<ProducerKind> population,
        PopulationColumn<double> produced) : IScheduledKernelSystem
    {
        private static readonly ImmutableArray<SystemAccess> s_access =
            [SystemAccess.WriteColumn<ProducerKind, double>(s_producedKey)];
        private static readonly PopulationRangeAction<ProducerKind, PopulationColumn<double>> s_incrementRange = IncrementRange;

        public string Id => "benchmark.increment_producers";

        public IReadOnlyList<SystemAccess> Access => s_access;

        public void Execute(ScheduledSystemContext context)
        {
            var lease = context.BorrowPopulation(population);
            context.ForEachPopulationRange(lease, produced, s_incrementRange);
        }

        private static void IncrementRange(PopulationColumn<double> values, int _, scoped ref PopulationAccessScope<ProducerKind> scope)
        {
            var rows = scope.EditRange(values);
            for (var index = 0; index < rows.Length; index++)
            {
                rows[index] += 1;
            }
        }
    }
}
