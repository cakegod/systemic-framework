using Systemic.Content;
using Systemic.Kernel;
using Systemic.Rules;

namespace Systemic.Benchmarks;

// Crowd tier: one million agents, each evaluating one authored rule per tick from column-resident
// state, inside one tick transaction, with a fact volume that does not follow the row count.
//
// The rule comes from samples/crowd-million/content/rules.json, so this measures the content
// vocabulary a game actually authors rather than a hand-written loop. Movement is fixed point, so
// the validation below can assert exact bits instead of a tolerance.
//
// Movement is staggered one quarter of the crowd per tick, which is how a crowd demo keeps moving
// while the tick stays inside the simulation share of the frame; every agent still evaluates the
// rule on every tick, which is what the gate is actually about.
internal sealed class CrowdTierWorkload : IDisposable
{
    public const int AgentCount = 1_000_000;
    public const long MaximumBytesPerTick = 0;
    public const int BurningEvery = 100;
    public const int BurningEnergy = 1_000_000;
    public const int MovementBuckets = 8;

    private static readonly PopulationColumnKey<FixedVector2> s_positionKey =
        new("crowd.prey.position", 1, FixedVector2Codec.Instance);

    private static readonly PopulationColumnKey<FixedVector2> s_velocityKey =
        new("crowd.prey.velocity", 1, FixedVector2Codec.Instance);

    private static readonly PopulationColumnKey<int> s_energyKey =
        new("crowd.prey.energy", 1, PopulationCodecs.Int32);

    private static readonly PopulationColumnKey<int> s_reportKey =
        new("crowd.prey.report", 1, PopulationCodecs.Int32);

    private readonly World _world = new(seed: 23, valueScale: 1);
    private readonly Population<CrowdAgent> _population;
    private readonly PopulationColumn<FixedVector2> _position;
    private readonly PopulationColumn<FixedVector2> _velocity;
    private readonly PopulationColumn<int> _energy;
    private readonly PopulationColumn<int> _report;
    private readonly RuleEngine _rules;
    private readonly PopulationSink _sink;
    private readonly List<SimulationFact> _facts = new(2);
    private readonly int _count;

    public CrowdTierWorkload(int count, string contentRoot)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        _count = count;
        _population = _world.DefinePopulation<CrowdAgent>(enableHandles: true);
        _position = _population.Column(s_positionKey);
        _velocity = _population.Column(s_velocityKey);
        _energy = _population.Column(s_energyKey);
        _report = _population.Column(s_reportKey);
        _population.Reserve(count);
        _population.AddRange(count);
        _population.WithAccess(new PopulationRange(0, count), this,
            static (CrowdTierWorkload workload, scoped ref PopulationAccessScope<CrowdAgent> scope) =>
                workload.Initialize(ref scope));

        var content = ContentPackLoader.Load(contentRoot);
        _rules = new RuleEngine(content.Rules);
        _rules.Bind(_world);
        _sink = new PopulationSink(_world, CrowdAgent.Id, s_energyKey.Id, threshold: 1,
            "crowd.prey.standing", _population.ReferenceOf(0));
    }

    public int Count => _count;

    public int FactsDispatched { get; private set; }

    public int Ticks { get; private set; }

    public void Tick()
    {
        using var transaction = _world.BeginTransaction();
        _population.WithAccess(BucketRange(Ticks % MovementBuckets), this,
            static (CrowdTierWorkload workload, scoped ref PopulationAccessScope<CrowdAgent> scope) =>
                workload.Move(ref scope));

        var scan = _rules.EvaluateRowEvent(_world, "crowd.prey.think", CrowdAgent.Id,
            new PopulationRange(0, _count));
        if (scan.RolledBack)
        {
            throw new InvalidOperationException(
                $"Crowd scan rolled back on tick {Ticks}: {scan.CutoffReason}.");
        }

        _facts.Clear();
        if (_sink.Collect(_facts) > 0)
        {
            var dispatched = _rules.Dispatch(_world, _facts[0]);
            if (dispatched.RolledBack)
            {
                throw new InvalidOperationException(
                    $"Sink fact rolled back on tick {Ticks}: {dispatched.CutoffReason}.");
            }

            FactsDispatched++;
        }

        transaction.Commit();
        Ticks++;
    }

    // Every agent's position is checked against the closed form of the moves its bucket received,
    // and the rule's two outcomes (burning rows drained by one per tick, other rows untouched) are
    // checked at a deterministic sample. A workload that silently stops doing the work cannot pass.
    public void Validate(int ticks)
    {
        var report = new int[1];
        var firstFailure = new string?[1];
        _population.WithAccess(new PopulationRange(0, _count),
            (Workload: this, Ticks: ticks, Failures: report, First: firstFailure),
            static ((CrowdTierWorkload Workload, int Ticks, int[] Failures, string?[] First) state,
                scoped ref PopulationAccessScope<CrowdAgent> scope) =>
            {
                var workload = state.Workload;
                var position = scope.ReadAll(workload._position);
                var velocity = scope.ReadAll(workload._velocity);
                var energy = scope.ReadAll(workload._energy);
                var report = scope.ReadAll(workload._report);
                var completedMoves = state.Ticks / MovementBuckets;
                var currentBucket = state.Ticks % MovementBuckets;
                for (var bucket = 0; bucket < MovementBuckets; bucket++)
                {
                    var start = BucketStart(bucket, workload._count);
                    var end = BucketStart(bucket + 1, workload._count);
                    var moved = completedMoves + (bucket < currentBucket ? 1 : 0);
                    for (var row = start; row < end; row++)
                    {
                        var expectedPosition = velocity[row] * (Fixed64)moved;
                        if (position[row] != expectedPosition)
                        {
                            state.Failures[0]++;
                            state.First[0] ??= $"row {row}: position {position[row]}, expected {expectedPosition}";
                        }

                        if (row % 997 != 0)
                        {
                            continue;
                        }

                        var burning = IsBurning(row);
                        var expectedEnergy = burning ? BurningEnergy - state.Ticks : 5;
                        if (energy[row] != expectedEnergy)
                        {
                            state.Failures[0]++;
                            state.First[0] ??= $"row {row}: energy {energy[row]}, expected {expectedEnergy}";
                        }

                        // Only the sink anchor (row 0) receives the standing-count fact.
                        var expectedReport = row == 0 ? workload.FactsDispatched : 0;
                        if (report[row] != expectedReport)
                        {
                            state.Failures[0]++;
                            state.First[0] ??= $"row {row}: report {report[row]}, expected {expectedReport}";
                        }
                    }
                }
            });

        if (report[0] > 0)
        {
            throw new InvalidOperationException(
                $"Crowd tier validation failed on {report[0]} checks after {ticks} ticks; first: {firstFailure[0]}.");
        }

        if (FactsDispatched > Ticks)
        {
            throw new InvalidOperationException(
                $"Crowd tier emitted {FactsDispatched} facts over {Ticks} ticks; the sink must not exceed one per tick.");
        }
    }

    public void Dispose()
    {
    }

    public static string ResolveContentRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "samples", "crowd-million", "content");
            if (File.Exists(Path.Combine(candidate, "game.json")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Crowd tier content was not found; run the benchmark from the repository so "
            + "samples/crowd-million/content is reachable, or pass --content-root.");
    }

    private static bool IsBurning(int row) => row % BurningEvery == 0;

    private static int BucketStart(int bucket, int count) => (int)((long)bucket * count / MovementBuckets);

    private static PopulationRange BucketRange(int bucket, int count) => new(
        BucketStart(bucket, count), BucketStart(bucket + 1, count) - BucketStart(bucket, count));

    private PopulationRange BucketRange(int bucket) => BucketRange(bucket, _count);

    private void Initialize(ref PopulationAccessScope<CrowdAgent> scope)
    {
        var position = scope.EditRange(_position);
        var velocity = scope.EditRange(_velocity);
        var energy = scope.EditRange(_energy);
        var report = scope.EditRange(_report);
        for (var row = 0; row < _count; row++)
        {
            position[row] = FixedVector2.Zero;
            velocity[row] = new FixedVector2((Fixed64)((row % 7) - 3) * (Fixed64)0.25m,
                (Fixed64)((row % 5) - 2) * (Fixed64)0.25m);
            energy[row] = 5;
            report[row] = 0;
            if (IsBurning(row))
            {
                _population.AddTag(row, "burning");
                energy[row] = BurningEnergy;
            }
        }
    }

    private void Move(ref PopulationAccessScope<CrowdAgent> scope)
    {
        var position = scope.EditRange(_position);
        var velocity = scope.ReadRange(_velocity);
        for (var row = 0; row < position.Length; row++)
        {
            position[row] += velocity[row];
        }
    }

    internal readonly struct CrowdAgent : IPopulationKind
    {
        public static string Id => "crowd.prey";

        public static int Version => 1;

        public static string[] Tags => ["burning"];
    }
}
