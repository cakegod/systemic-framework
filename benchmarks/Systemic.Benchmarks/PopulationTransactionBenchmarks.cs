using BenchmarkDotNet.Attributes;
using Systemic.Kernel;

namespace Systemic.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob]
public class PopulationTransactionBenchmarks
{
    private static readonly PopulationColumnKey<int> s_valueKey =
        new("benchmark.transaction.values", 1, PopulationCodecs.Int32);
    private World _world = null!;
    private Population<TransactionBenchKind> _population = null!;
    private DoubleBufferedPopulationColumn<int> _values = null!;
    private int _count;

    [Params(50_000, 100_000)]
    public int Population { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(seed: 1, valueScale: 1);
        _population = _world.DefinePopulation<TransactionBenchKind>();
        _values = _population.DoubleBufferedColumn(s_valueKey);
        _population.Reserve(Population);
        _population.AddRange(Population);
        _count = _population.Count;
        for (var iteration = 0; iteration < 10; iteration++)
        {
            CommitFullRange();
            CommitSparseRange();
        }
    }

    [Benchmark]
    public void FullRangeTransactionalWrite() => CommitFullRange();

    [Benchmark]
    public void SparseTransactionalWrite() => CommitSparseRange();

    private void CommitFullRange()
    {
        using var transaction = _world.BeginTransaction();
        _population.WithAccess(new PopulationRange(0, _count), _values,
            static (DoubleBufferedPopulationColumn<int> values, scoped ref PopulationAccessScope<TransactionBenchKind> scope) =>
                scope.EditNext(values).Fill(1));
        _values.Swap();
        transaction.Commit();
    }

    private void CommitSparseRange()
    {
        using var transaction = _world.BeginTransaction();
        _population.WithAccess(new PopulationRange(0, 1), _values,
            static (DoubleBufferedPopulationColumn<int> values, scoped ref PopulationAccessScope<TransactionBenchKind> scope) =>
                scope.EditNext(values)[0]++);
        _values.Swap();
        transaction.Commit();
    }

    private readonly struct TransactionBenchKind : IPopulationKind
    {
        public static string Id => "benchmark.transaction-particles";
        public static int Version => 1;
    }
}
