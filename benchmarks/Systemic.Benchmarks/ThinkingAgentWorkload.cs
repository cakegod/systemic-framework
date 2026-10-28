using Systemic.Kernel;

namespace Systemic.Benchmarks;

// Column-resident per-row decision: advance x, then apply one masked threshold effect to
// health. This is the shape the row-addressed rule path must sustain at one million agents,
// not a general rule engine: four column reads, one compare, one conditional write.
internal sealed class ThinkingAgentWorkload
{
    private static readonly PopulationColumnKey<int> s_xKey = new("benchmark.thinking.x", 1, PopulationCodecs.Int32);
    private static readonly PopulationColumnKey<int> s_vxKey = new("benchmark.thinking.vx", 1, PopulationCodecs.Int32);
    private static readonly PopulationColumnKey<int> s_healthKey = new("benchmark.thinking.health", 1, PopulationCodecs.Int32);
    private static readonly PopulationColumnKey<ulong> s_maskKey = new("benchmark.thinking.mask", 1, PopulationCodecs.UInt64);

    private readonly World _world = new(seed: 1, valueScale: 1);
    private readonly Population<ThinkingAgent> _population;
    private readonly PopulationColumn<int> _x;
    private readonly PopulationColumn<int> _vx;
    private readonly PopulationColumn<int> _health;
    private readonly PopulationColumn<ulong> _mask;
    private readonly int _count;
    private readonly bool _safe;
    private ulong _validationHash;
    private int _validationTicks;
    private int _sampleRow;
    private (int X, int Health, ulong Mask) _sample;

    public ThinkingAgentWorkload(int count, bool safe)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        _count = count;
        _safe = safe;
        _population = _world.DefinePopulation<ThinkingAgent>();
        _x = _population.Column(s_xKey);
        _vx = _population.Column(s_vxKey);
        _health = _population.Column(s_healthKey);
        _mask = _population.Column(s_maskKey);
        _population.Reserve(count);
        _population.AddRange(count);
        _population.WithAccess(new PopulationRange(0, count), this,
            static (ThinkingAgentWorkload workload, scoped ref PopulationAccessScope<ThinkingAgent> scope) =>
            {
                var x = scope.EditRange(workload._x);
                var vx = scope.EditRange(workload._vx);
                var health = scope.EditRange(workload._health);
                var mask = scope.EditRange(workload._mask);
                for (var row = 0; row < workload._count; row++)
                {
                    x[row] = row % 4096 - 2048;
                    vx[row] = row % 7 - 3;
                    health[row] = 100;
                    mask[row] = (row & 1) == 0 ? 1UL : 0UL;
                }
            });
    }

    public int Count => _count;

    public void Tick()
    {
        if (_safe)
        {
            using var transaction = _world.BeginTransaction();
            _population.WithAccess(new PopulationRange(0, _count), this,
                static (ThinkingAgentWorkload workload, scoped ref PopulationAccessScope<ThinkingAgent> scope) =>
                    workload.Think(ref scope));
            transaction.Commit();
        }
        else
        {
            _population.WithAccess(new PopulationRange(0, _count), this,
                static (ThinkingAgentWorkload workload, scoped ref PopulationAccessScope<ThinkingAgent> scope) =>
                    workload.Think(ref scope));
        }
    }

    public (int X, int Health, ulong Mask) Sample(int row)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, _count);
        _sampleRow = row;
        _sample = default;
        _population.WithAccess(new PopulationRange(0, _count), this,
            static (ThinkingAgentWorkload workload, scoped ref PopulationAccessScope<ThinkingAgent> scope) =>
                workload._sample = (scope.ReadAll(workload._x)[workload._sampleRow],
                    scope.ReadAll(workload._health)[workload._sampleRow],
                    scope.ReadAll(workload._mask)[workload._sampleRow]));
        return _sample;
    }

    // Every row's position is checked against its closed form; the masked health outcome is
    // recomputed step by step for a deterministic sample of rows.
    public ulong Validate(int ticks)
    {
        var hash = 14695981039346656037UL;
        _validationTicks = ticks;
        _validationHash = hash;
        _population.WithAccess(new PopulationRange(0, _count), this,
            static (ThinkingAgentWorkload workload, scoped ref PopulationAccessScope<ThinkingAgent> scope) =>
            {
                var x = scope.ReadAll(workload._x);
                var vx = scope.ReadAll(workload._vx);
                var health = scope.ReadAll(workload._health);
                var mask = scope.ReadAll(workload._mask);
                var hash = workload._validationHash;
                for (var row = 0; row < workload._count; row++)
                {
                    var expectedX = unchecked(row % 4096 - 2048 + workload._validationTicks * (row % 7 - 3));
                    if (x[row] != expectedX)
                    {
                        throw new InvalidOperationException(
                            $"Thinking validation failed at row {row}: x={x[row]}, expected {expectedX}.");
                    }

                    if (row % 512 == 0)
                    {
                        var expectedHealth = ExpectedHealth(row, workload._validationTicks);
                        if (health[row] != expectedHealth)
                        {
                            throw new InvalidOperationException(
                                $"Thinking validation failed at row {row}: health={health[row]}, expected {expectedHealth}.");
                        }
                    }

                    hash = unchecked((hash ^ (uint)x[row]) * 1099511628211UL);
                    hash = unchecked((hash ^ (uint)health[row]) * 1099511628211UL);
                    hash = unchecked((hash ^ (uint)mask[row]) * 1099511628211UL);
                    hash = unchecked((hash ^ (uint)vx[row]) * 1099511628211UL);
                }

                workload._validationHash = hash;
            });
        return _validationHash;
    }

    private static int ExpectedHealth(int row, int ticks)
    {
        var value = 100;
        if ((row & 1) != 0)
        {
            return value;
        }

        var x = row % 4096 - 2048;
        var vx = row % 7 - 3;
        for (var step = 0; step < ticks; step++)
        {
            x = unchecked(x + vx);
            if (x > 0)
            {
                value = unchecked(value - 1);
            }
        }

        return value;
    }

    private void Think(ref PopulationAccessScope<ThinkingAgent> scope)
    {
        var x = scope.EditRange(_x);
        var vx = scope.ReadAll(_vx);
        var health = scope.EditRange(_health);
        var mask = scope.ReadAll(_mask);
        for (var row = 0; row < _count; row++)
        {
            var next = unchecked(x[row] + vx[row]);
            x[row] = next;
            if ((mask[row] & 1UL) != 0 && next > 0)
            {
                health[row] = unchecked(health[row] - 1);
            }
        }
    }

    internal readonly struct ThinkingAgent : IPopulationKind
    {
        public static string Id => "benchmark.thinking-agent";

        public static int Version => 1;
    }
}
