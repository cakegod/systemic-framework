using Systemic.Kernel;

namespace Systemic.Benchmarks;

// Integer-only, structure-of-arrays movement. In safe mode each complete tick is a
// world transaction; optimized mode deliberately skips that rollback boundary.
internal sealed class MillionAgentWorkload
{
    private static readonly PopulationColumnKey<int> s_xKey = new("benchmark.movement.x", 1, PopulationCodecs.Int32);
    private static readonly PopulationColumnKey<int> s_yKey = new("benchmark.movement.y", 1, PopulationCodecs.Int32);
    private static readonly PopulationColumnKey<int> s_vxKey = new("benchmark.movement.vx", 1, PopulationCodecs.Int32);
    private static readonly PopulationColumnKey<int> s_vyKey = new("benchmark.movement.vy", 1, PopulationCodecs.Int32);

    private readonly World _world = new(seed: 1, valueScale: 1);
    private readonly PopulationColumn<int> _x;
    private readonly PopulationColumn<int> _y;
    private readonly PopulationColumn<int> _vx;
    private readonly PopulationColumn<int> _vy;
    private readonly int _count;
    private readonly bool _safe;

    public MillionAgentWorkload(int count, bool safe)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        _count = count;
        _safe = safe;
        var population = _world.DefinePopulation<MovementAgent>();
        _x = population.Column(s_xKey);
        _y = population.Column(s_yKey);
        _vx = population.Column(s_vxKey);
        _vy = population.Column(s_vyKey);
        population.Reserve(count);
        population.AddRange(count);
        var x = _x.EditRange(0, count);
        var y = _y.EditRange(0, count);
        var vx = _vx.EditRange(0, count);
        var vy = _vy.EditRange(0, count);
        for (var row = 0; row < count; row++)
        {
            x[row] = row % 4096 - 2048;
            y[row] = (int)(((long)row * 17) % 4096) - 2048;
            vx[row] = row % 7 - 3;
            vy[row] = row / 7 % 7 - 3;
        }
    }

    public void Tick()
    {
        if (_safe)
        {
            using var transaction = _world.BeginTransaction();
            Move();
            transaction.Commit();
        }
        else
        {
            Move();
        }
    }

    public (int X, int Y) Position(int row) => (_x.ReadOnlySpan[row], _y.ReadOnlySpan[row]);

    // Validate every row against an independent closed-form result and return a
    // deterministic final-state fingerprint for comparing modes and repeat runs.
    public ulong Validate(int ticks)
    {
        var x = _x.ReadOnlySpan;
        var y = _y.ReadOnlySpan;
        var vx = _vx.ReadOnlySpan;
        var vy = _vy.ReadOnlySpan;
        var hash = 14695981039346656037UL;
        for (var row = 0; row < _count; row++)
        {
            var expectedVx = row % 7 - 3;
            var expectedVy = row / 7 % 7 - 3;
            var expectedX = unchecked(row % 4096 - 2048 + ticks * expectedVx);
            var expectedY = unchecked((int)(((long)row * 17) % 4096) - 2048 + ticks * expectedVy);
            if (vx[row] != expectedVx || vy[row] != expectedVy || x[row] != expectedX || y[row] != expectedY)
            {
                throw new InvalidOperationException($"Movement validation failed at row {row} after {ticks} ticks.");
            }
            hash = unchecked((hash ^ (uint)x[row]) * 1099511628211UL);
            hash = unchecked((hash ^ (uint)y[row]) * 1099511628211UL);
        }
        return hash;
    }

    private void Move()
    {
        var x = _x.EditRange(0, _count);
        var y = _y.EditRange(0, _count);
        var vx = _vx.ReadOnlySpan;
        var vy = _vy.ReadOnlySpan;
        for (var row = 0; row < _count; row++)
        {
            x[row] = unchecked(x[row] + vx[row]);
            y[row] = unchecked(y[row] + vy[row]);
        }
    }

    private readonly struct MovementAgent : IPopulationKind
    {
        public static string Id => "benchmark.movement-agent";
        public static int Version => 1;
    }
}
