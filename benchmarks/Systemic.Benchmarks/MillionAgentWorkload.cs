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
    private readonly Population<MovementAgent> _population;
    private readonly PopulationColumn<int> _x;
    private readonly PopulationColumn<int> _y;
    private readonly PopulationColumn<int> _vx;
    private readonly PopulationColumn<int> _vy;
    private readonly int _count;
    private readonly bool _safe;
    private int _positionRow;
    private int _validationTicks;
    private ulong _validationHash;
    private (int X, int Y) _position;

    public MillionAgentWorkload(int count, bool safe)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        _count = count;
        _safe = safe;
        _population = _world.DefinePopulation<MovementAgent>();
        _x = _population.Column(s_xKey);
        _y = _population.Column(s_yKey);
        _vx = _population.Column(s_vxKey);
        _vy = _population.Column(s_vyKey);
        _population.Reserve(count);
        _population.AddRange(count);
        _population.WithAccess(new PopulationRange(0, count), this, static (MillionAgentWorkload workload, scoped ref PopulationAccessScope<MovementAgent> scope) =>
        {
            var x = scope.EditRange(workload._x);
            var y = scope.EditRange(workload._y);
            var vx = scope.EditRange(workload._vx);
            var vy = scope.EditRange(workload._vy);
            for (var row = 0; row < workload._count; row++)
            {
                x[row] = row % 4096 - 2048;
                y[row] = (int)(((long)row * 17) % 4096) - 2048;
                vx[row] = row % 7 - 3;
                vy[row] = row / 7 % 7 - 3;
            }
        });
    }

    public void Tick()
    {
        if (_safe)
        {
            using var transaction = _world.BeginTransaction();
            _population.WithAccess(new PopulationRange(0, _count), this, static (MillionAgentWorkload workload, scoped ref PopulationAccessScope<MovementAgent> scope) =>
                workload.Move(ref scope));
            transaction.Commit();
        }
        else
        {
            _population.WithAccess(new PopulationRange(0, _count), this, static (MillionAgentWorkload workload, scoped ref PopulationAccessScope<MovementAgent> scope) =>
                workload.Move(ref scope));
        }
    }

    public (int X, int Y) Position(int row)
    {
        _positionRow = row;
        _position = default;
        _population.WithAccess(new PopulationRange(0, _count), this, static (MillionAgentWorkload workload, scoped ref PopulationAccessScope<MovementAgent> scope) =>
        {
            workload._position = (scope.ReadAll(workload._x)[workload._positionRow], scope.ReadAll(workload._y)[workload._positionRow]);
        });
        return _position;
    }

    // Validate every row against an independent closed-form result and return a
    // deterministic final-state fingerprint for comparing modes and repeat runs.
    public ulong Validate(int ticks)
    {
        var hash = 14695981039346656037UL;
        _validationTicks = ticks;
        _validationHash = hash;
        _population.WithAccess(new PopulationRange(0, _count), this, static (MillionAgentWorkload workload, scoped ref PopulationAccessScope<MovementAgent> scope) =>
        {
            var x = scope.ReadAll(workload._x);
            var y = scope.ReadAll(workload._y);
            var vx = scope.ReadAll(workload._vx);
            var vy = scope.ReadAll(workload._vy);
            var hash = workload._validationHash;
            for (var row = 0; row < workload._count; row++)
            {
                var expectedVx = row % 7 - 3;
                var expectedVy = row / 7 % 7 - 3;
                var expectedX = unchecked(row % 4096 - 2048 + workload._validationTicks * expectedVx);
                var expectedY = unchecked((int)(((long)row * 17) % 4096) - 2048 + workload._validationTicks * expectedVy);
                if (vx[row] != expectedVx || vy[row] != expectedVy || x[row] != expectedX || y[row] != expectedY)
                {
                    throw new InvalidOperationException($"Movement validation failed at row {row} after {workload._validationTicks} ticks.");
                }
                hash = unchecked((hash ^ (uint)x[row]) * 1099511628211UL);
                hash = unchecked((hash ^ (uint)y[row]) * 1099511628211UL);
            }
            workload._validationHash = hash;
        });
        return _validationHash;
    }

    private void Move(ref PopulationAccessScope<MovementAgent> scope)
    {
        var x = scope.EditRange(_x);
        var y = scope.EditRange(_y);
        var vx = scope.ReadAll(_vx);
        var vy = scope.ReadAll(_vy);
        for (var row = 0; row < _count; row++)
        {
            x[row] = unchecked(x[row] + vx[row]);
            y[row] = unchecked(y[row] + vy[row]);
        }
    }

    internal readonly struct MovementAgent : IPopulationKind
    {
        public static string Id => "benchmark.movement-agent";
        public static int Version => 1;
    }
}
