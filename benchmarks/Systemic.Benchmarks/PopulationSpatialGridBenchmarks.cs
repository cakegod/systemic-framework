using System.Numerics;
using BenchmarkDotNet.Attributes;
using Systemic.Kernel;

namespace Systemic.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob]
public class PopulationSpatialGridBenchmarks
{
    private Vector2[] _positions = [];
    private PopulationSpatialGrid2D _grid = null!;

    [Params(100_000, 1_000_000)]
    public int Population { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _positions = new Vector2[Population];
        var random = new Random(41);
        for (var row = 0; row < _positions.Length; row++)
        {
            _positions[row] = new Vector2(random.NextSingle() * 2_000, random.NextSingle() * 2_000);
        }

        _grid = new PopulationSpatialGrid2D(
            new Bounds2(1_000, 1_000, 1_000, 1_000), 24, wrapX: true, wrapY: true);
        _grid.Reserve(Population);
        _grid.Rebuild(_positions);
    }

    [Benchmark]
    public int Rebuild()
    {
        _grid.Rebuild(_positions);
        return _grid.Count;
    }

    [Benchmark]
    public int CappedNeighborScan()
    {
        Span<int> candidates = stackalloc int[12];
        var checksum = 0;
        for (var row = 0; row < Population; row++)
        {
            var count = _grid.QueryCellNeighborhoodCandidates(row, candidates, row % 9);
            var neighbors = 0;
            for (var candidate = 0; candidate < count && neighbors < 3; candidate++)
            {
                var other = candidates[candidate];
                if (other != row)
                {
                    checksum += other;
                    neighbors++;
                }
            }
        }

        return checksum;
    }

    [Benchmark]
    public int CappedNeighborScanLazy()
    {
        var checksum = 0;
        for (var row = 0; row < Population; row++)
        {
            var neighbors = 0;
            foreach (var other in _grid.QueryCellNeighborhoodCandidates(row, row % 9))
            {
                if (other == row)
                {
                    continue;
                }

                checksum += other;
                if (++neighbors == 3)
                {
                    break;
                }
            }
        }

        return checksum;
    }
}
