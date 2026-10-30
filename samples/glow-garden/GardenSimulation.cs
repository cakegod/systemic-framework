using System.Collections.Immutable;
using System.Numerics;

using Systemic.Host;
using Systemic.Kernel;
using Systemic.Rules;

namespace GlowGarden;

public readonly record struct PlantState(int Id, Vector2 Position, float Growth);

public readonly record struct CreatureState(int Id, CreatureKind Kind, Vector2 Position, int CarriedGlow, int Cooldown, bool IsCopy, float LeapProgress = 0, float Heading = 0);

public readonly record struct MoteState(int Id, Vector2 Position, Vector2 Velocity, int Value);

/// <summary>
/// Live plants, creatures (including copy expiry), and motes are authoritative Kernel populations.
/// Progression, identifiers, diagnostics, scent and delayed effects are captured as system state.
/// </summary>
public sealed partial class GardenSimulation : ISystemicSystem, ISystemicTransactionalState
{
    public const int MaxWorkers = 1000, MaxPlants = 64, MaxMotes = 12000, MaxCopies = 128;
    public const int StartingPlants = 9, StartingWorkers = 9;

    public const float Width = 1920, Height = 1280;

    public static readonly Vector2 Colony = new(480, 320);

    private const float SearchStartRadius = 24;
    private const int StallLimit = 90;
    private static readonly float MaximumSearchRadius = MathF.Sqrt(Width * Width + Height * Height);

    private readonly World _world;
    private readonly Entity _garden;
    private readonly GardenTuning _tuning;
    private readonly DeterministicRandomFactory _scoutRandom;
    private readonly PopulationSpatialGrid2D _plantGrid = new(new Bounds2(Width / 2, Height / 2, Width / 2, Height / 2), 64);
    private readonly PopulationSpatialGrid2D _moteGrid = new(new Bounds2(Width / 2, Height / 2, Width / 2, Height / 2), 64);
    private readonly List<int> _nearbyPlants = new(256);
    private readonly List<int> _nearbyMotes = new(256);
    private readonly List<int> _reactionPlants = new(MaxReactionBursts);
    internal readonly GardenScent _scent = new(Width, Height, 24);
    internal readonly GardenObstacles Obstacles;
    internal readonly PlantStates _plants;
    internal readonly CreatureStates _creatures;
    internal readonly MoteStates _motes;
    private int _nextCreatureId, _nextMoteId, _nextPlantId;

    private sealed class PlantSearchState(
        GardenSimulation simulation,
        Vector2 position,
        float maxRadius,
        List<int> rows)
    {
        public GardenSimulation Simulation { get; } = simulation;
        public Vector2 Position { get; } = position;
        public float MaxRadius { get; } = maxRadius;
        public List<int> Rows { get; } = rows;
        public int Result { get; set; } = -1;
    }

    private sealed class MagnetismState(
        GardenSimulation simulation,
        PopulationLease<GardenCreature> creatures,
        float radius,
        float pull)
    {
        public GardenSimulation Simulation { get; } = simulation;
        public PopulationLease<GardenCreature> Creatures { get; } = creatures;
        public float Radius { get; } = radius;
        public float Pull { get; } = pull;
    }

    private sealed class DecisionState(
        GardenSimulation simulation,
        PopulationLease<GardenPlant> plants,
        PopulationLease<GardenMote> motes)
    {
        public GardenSimulation Simulation { get; } = simulation;
        public PopulationLease<GardenPlant> Plants { get; } = plants;
        public PopulationLease<GardenMote> Motes { get; } = motes;
    }

    public GardenSimulation(World world, GardenTuning? tuning = null, bool restoring = false)
    {
        _world = world;
        _tuning = tuning ?? GardenTuning.Default;
        _scoutRandom = new DeterministicRandomFactory(unchecked((ulong)(long)world.Seed), "glow-garden.scouting");
        _garden = world.Entities.Single(e => e.HasTag("garden"));
        _plants = new PlantStates(world);
        _creatures = new CreatureStates(world);
        _motes = new MoteStates(world);
        // Noise-derived thickets: deterministic per seed, and generated before any pod is placed.
        Obstacles = new GardenObstacles(Width, Height,
            unchecked((ulong)(long)world.Seed), Colony, 170);
        _creatureGrid.Reserve(MaxWorkers + MaxCopies);
        _plantGrid.Reserve(MaxPlants);
        _moteGrid.Reserve(MaxMotes);
        Plants = _plants;
        Creatures = _creatures;
        Motes = _motes;
        PendingImpacts = _pending.AsReadOnly();

        // Population state is restored by the host after all schemas have been registered.
        // In particular, do not consume the saved draw stream while constructing a restored game.
        if (restoring)
        {
            return;
        }

        // Pods start across the whole garden rather than ringed around the colony, so feeding grounds
        // are spread out and workers have to travel and forage between them.
        const float margin = 90;
        for (var i = 0; i < StartingPlants; i++)
        {
            var column = i % 3;
            var row = i / 3;
            var spot = default(Vector2);
            for (var attempt = 0; attempt < 12; attempt++)
            {
                spot = new Vector2(
                    margin + ((column + 0.2f + (NextFloat() * 0.6f)) * ((Width - (2 * margin)) / 3)),
                    margin + ((row + 0.2f + (NextFloat() * 0.6f)) * ((Height - (2 * margin)) / 3)));
                if (!Obstacles.IsBlocked(spot))
                {
                    break;
                }
            }

            if (Obstacles.IsBlocked(spot)
                && !Obstacles.TryFindFreePosition(spot, null, 0, out spot))
            {
                throw new InvalidOperationException("The garden has no free position for an initial plant.");
            }

            _plants.Add(new(_nextPlantId++, spot, NextFloat()));
        }

        // A larger garden needs a larger founding colony, or the first harvests take minutes.
        for (var i = 0; i < StartingWorkers; i++)
        {
            _creatures.Add(new(_nextCreatureId++, i < 3 ? CreatureKind.Burstling : CreatureKind.Gatherer, Colony, 0, 0, false));
        }
    }

    public string Id => "glow-garden";

    public int Order => 0;

    private static readonly ImmutableArray<SystemAccess> s_access =
    [
        SystemAccess.WriteWorld,
        GardenPlantPopulation.ReadStableId,
        GardenPlantPopulation.ReadPosition,
        GardenPlantPopulation.ReadGrowth,
        GardenPlantPopulation.WriteStableId,
        GardenPlantPopulation.WritePosition,
        GardenPlantPopulation.WriteGrowth,
        GardenCreaturePopulation.ReadStableId,
        GardenCreaturePopulation.ReadKind,
        GardenCreaturePopulation.ReadPosition,
        GardenCreaturePopulation.ReadCarriedGlow,
        GardenCreaturePopulation.ReadCooldown,
        GardenCreaturePopulation.ReadIsCopy,
        GardenCreaturePopulation.ReadLeapProgress,
        GardenCreaturePopulation.ReadHeading,
        GardenCreaturePopulation.ReadWanderTicks,
        GardenCreaturePopulation.ReadStallTicks,
        GardenCreaturePopulation.ReadSlideSide,
        GardenCreaturePopulation.ReadCopyExpiryTick,
        GardenCreaturePopulation.WriteStableId,
        GardenCreaturePopulation.WriteKind,
        GardenCreaturePopulation.WritePosition,
        GardenCreaturePopulation.WriteCarriedGlow,
        GardenCreaturePopulation.WriteCooldown,
        GardenCreaturePopulation.WriteIsCopy,
        GardenCreaturePopulation.WriteLeapProgress,
        GardenCreaturePopulation.WriteHeading,
        GardenCreaturePopulation.WriteWanderTicks,
        GardenCreaturePopulation.WriteStallTicks,
        GardenCreaturePopulation.WriteSlideSide,
        GardenCreaturePopulation.WriteCopyExpiryTick,
        GardenMotePopulation.ReadStableId,
        GardenMotePopulation.ReadPosition,
        GardenMotePopulation.ReadVelocity,
        GardenMotePopulation.ReadValue,
        GardenMotePopulation.WriteStableId,
        GardenMotePopulation.WritePosition,
        GardenMotePopulation.WriteVelocity,
        GardenMotePopulation.WriteValue,
    ];

    public IReadOnlyList<SystemAccess> Access => s_access;

    public IReadOnlyList<PlantState> Plants { get; }

    public IReadOnlyList<CreatureState> Creatures { get; }

    public IReadOnlyList<MoteState> Motes { get; }

    public long BurstCount { get; private set; }

    public int Tick { get; private set; }

    private float NextFloat() => _world.Draws.NextFloat();

    private void BindPopulationContext(SystemicSystemContext? context)
    {
        _plants.BindContext(context);
        _creatures.BindContext(context);
        _motes.BindContext(context);
    }

    private static Vector2 Move(Vector2 from, Vector2 to, float distance)
    {
        var delta = to - from;
        var length = delta.Length();
        return length <= distance ? to : from + delta * (distance / length);
    }

    /// <summary>Consumed motes are tombstoned so row indexes stay valid during the worker loop.</summary>
    private void RemoveConsumedMotes()
    {
        _motes.WithAccess(new PopulationRange(0, _motes.Count), this,
            static (GardenSimulation simulation, scoped ref PopulationAccessScope<GardenMote> access) =>
            {
                var values = access.ReadRange(simulation._motes.Columns.Value);
                simulation._consumedMotes.Clear();
                for (var row = values.Length - 1; row >= 0; row--)
                {
                    if (values[row] <= 0)
                    {
                        simulation._consumedMotes.Add(row);
                    }
                }
            });

        foreach (var row in _consumedMotes)
        {
            _motes.RemoveAt(row);
        }
    }

    private readonly List<int> _consumedMotes = new(MaxMotes);

    /// <summary>
    /// Grid radius queries grow from a small radius; a hit inside the queried radius is the true
    /// nearest because nothing farther away can be closer.
    /// </summary>
    private int NearestRipe(Vector2 position, float maxRadius, List<int>? rows = null)
    {
        var state = new PlantSearchState(this, position, maxRadius, rows ?? _nearbyPlants);
        _plants.WithAccess(new PopulationRange(0, _plants.Count), state,
            static (PlantSearchState search, scoped ref PopulationAccessScope<GardenPlant> access) =>
            {
                search.Result = search.Simulation.FindNearestRipe(
                    search.Position,
                    search.MaxRadius,
                    search.Rows,
                    access.ReadRange(search.Simulation._plants.Columns.Growth),
                    access.ReadRange(search.Simulation._plants.Columns.Position));
            });
        return state.Result;
    }

    private int FindNearestRipe(
        Vector2 position,
        float maxRadius,
        List<int> scratch,
        ReadOnlySpan<float> growth,
        ReadOnlySpan<Vector2> positions)
    {
        var radius = MathF.Min(SearchStartRadius, maxRadius);
        while (true)
        {
            var found = -1;
            var best = float.MaxValue;
            _plantGrid.QueryRadius(position, radius, scratch);
            foreach (var row in scratch)
            {
                if (growth[row] < 1)
                {
                    continue;
                }

                var distance = Vector2.DistanceSquared(position, positions[row]);
                if (distance < best)
                {
                    best = distance;
                    found = row;
                }
            }

            if (found >= 0)
            {
                return found;
            }

            if (radius >= maxRadius)
            {
                return -1;
            }

            radius = MathF.Min(radius * 2, maxRadius);
        }
    }

    private int FindNearestMote(
        Vector2 position,
        float maxRadius,
        List<int> scratch,
        ReadOnlySpan<int> values,
        ReadOnlySpan<Vector2> positions)
    {
        var radius = MathF.Min(SearchStartRadius, maxRadius);
        while (true)
        {
            var found = -1;
            var best = float.MaxValue;
            _moteGrid.QueryRadius(position, radius, scratch);
            foreach (var row in scratch)
            {
                if (values[row] <= 0)
                {
                    continue;
                }

                var distance = Vector2.DistanceSquared(position, positions[row]);
                if (distance < best)
                {
                    best = distance;
                    found = row;
                }
            }

            if (found >= 0)
            {
                return found;
            }

            if (radius >= maxRadius)
            {
                return -1;
            }

            radius = MathF.Min(radius * 2, maxRadius);
        }
    }

    private const byte IntentNone = 0, IntentBurst = 1, IntentCollect = 2, IntentDeposit = 3;

    [ThreadStatic] private static List<int>? t_plantRows;
    [ThreadStatic] private static List<int>? t_moteRows;

    private readonly byte[] _intents = new byte[MaxWorkers + MaxCopies];
    private readonly int[] _intentTargets = new int[MaxWorkers + MaxCopies];
    private readonly Vector2[] _decidedPositions = new Vector2[MaxWorkers + MaxCopies];
    private readonly int[] _decidedCooldowns = new int[MaxWorkers + MaxCopies];
    private readonly float[] _decidedLeaps = new float[MaxWorkers + MaxCopies];
    private readonly float[] _decidedHeadings = new float[MaxWorkers + MaxCopies];
    private readonly int[] _decidedWaits = new int[MaxWorkers + MaxCopies];
    private readonly int[] _decidedStalls = new int[MaxWorkers + MaxCopies];
    private readonly int[] _decidedSides = new int[MaxWorkers + MaxCopies];
    private readonly int[] _resolvedIds = new int[MaxWorkers + MaxCopies];
    private readonly bool[] _resolvedCopies = new bool[MaxWorkers + MaxCopies];
    private readonly int[] _resolvedCarried = new int[MaxWorkers + MaxCopies];
    private Action<SimulationFact> _publish = _ => { };
    private int _tickCooldown, _tickCapacity, _tickPatience;
    private float _tickSpeed, _tickScoutRadius, _tickTrailThreshold, _tickTrailStrength;

    public void Step(SystemicSystemContext context)
    {
        _publish = fact => context.Publish(fact);
        BindPopulationContext(context);
        try
        {
            ExecuteCore(context);
        }
        finally
        {
            BindPopulationContext(null);
        }
    }

    private void ExecuteCore(SystemicSystemContext context)
    {
        Tick = context.Tick;
        _tickScoutRadius = _tuning.ScoutRadius;
        _tickPatience = _tuning.Patience;
        _tickTrailThreshold = _tuning.TrailThreshold;
        _tickTrailStrength = _tuning.TrailStrength;
        _scent.Decay(_tuning.TrailDecay);
        // WHY: a loaded save starts with an empty plant grid, and a due Echo impact queries it
        // before this tick's rebuild. A matching count means the grid already mirrors the rows.
        if (_plantGrid.Count != _plants.Count)
        {
            RebuildPlantGrid();
        }

        StepTalents();
        RebuildPlantGrid();
        GrowPlants();
        DriftMotes();

        _tickCooldown = Math.Max(3, 30 - Rank(UpgradeKind.BurstlingWorkRate));
        _tickCapacity = 3 + Rank(UpgradeKind.GathererCapacity);
        _tickSpeed = 3 + Rank(UpgradeKind.GathererSpeed) * 0.3f;
        var count = _creatures.Count;
        {
            using var creatures = context.BorrowPopulation(_creatures.Population);
            using var plants = context.BorrowPopulation(_plants.Population);
            using var motes = context.BorrowPopulation(_motes.Population);
            context.ForEachPopulationRange(
                creatures,
                new DecisionState(this, plants, motes),
                DecidePopulationRange);
        }
        ApplyDecisions(count);
        ResolveWorkerIntents(count);
        RemoveConsumedMotes();

        // Outside any rule dispatch, so talent work can publish without reentering dispatch.
        DrainTalentIntents();
    }

    private void RebuildPlantGrid() =>
        _plants.WithAccess(new PopulationRange(0, _plants.Count), this,
            static (GardenSimulation simulation, scoped ref PopulationAccessScope<GardenPlant> access) =>
                simulation._plantGrid.Rebuild(access.ReadRange(simulation._plants.Columns.Position)));

    private void GrowPlants()
    {
        var growth = (1 + Rank(UpgradeKind.PodGrowth) * 0.12f) / 240f;
        _plants.WithAccess(new PopulationRange(0, _plants.Count), (_plants.Columns, Growth: growth),
            static ((GardenPlantPopulation.Columns Columns, float Growth) state, scoped ref PopulationAccessScope<GardenPlant> access) =>
            {
                var plants = access.EditRange(state.Columns.Growth);
                for (var row = 0; row < plants.Length; row++)
                {
                    plants[row] = MathF.Min(1, plants[row] + state.Growth);
                }
            });
    }

    private void DriftMotes()
    {
        _motes.WithAccess(new PopulationRange(0, _motes.Count), this,
            static (GardenSimulation simulation, scoped ref PopulationAccessScope<GardenMote> access) =>
            {
                var positions = access.EditRange(simulation._motes.Columns.Position);
                var velocities = access.EditRange(simulation._motes.Columns.Velocity);
                for (var row = 0; row < positions.Length; row++)
                {
                    var previous = positions[row];
                    var next = Vector2.Clamp(previous + velocities[row], Vector2.Zero, new(Width, Height));
                    // Glow drifts up to a thicket and stops against it, so it always stays collectable.
                    positions[row] = simulation.Obstacles.Constrain(previous, next);
                    velocities[row] *= 0.9f;
                }
                simulation._moteGrid.Rebuild(positions);
            });
    }

    /// <summary>Copies parallel decisions into population columns in stable row order.</summary>
    private void ApplyDecisions(int count)
    {
        _creatures.WithAccess(new PopulationRange(0, count), this,
            static (GardenSimulation simulation, scoped ref PopulationAccessScope<GardenCreature> access) =>
            {
                var positions = access.EditRange(simulation._creatures.Columns.Position);
                var cooldowns = access.EditRange(simulation._creatures.Columns.Cooldown);
                var leaps = access.EditRange(simulation._creatures.Columns.LeapProgress);
                var headings = access.EditRange(simulation._creatures.Columns.Heading);
                var waits = access.EditRange(simulation._creatures.Columns.WanderTicks);
                var stalls = access.EditRange(simulation._creatures.Columns.StallTicks);
                var sides = access.EditRange(simulation._creatures.Columns.SlideSide);
                for (var row = 0; row < positions.Length; row++)
                {
                    // Wall-following can push a worker past the garden edge; populations reject out-of-bounds rows.
                    positions[row] = Vector2.Clamp(simulation._decidedPositions[row], Vector2.Zero, new(Width, Height));
                    cooldowns[row] = simulation._decidedCooldowns[row];
                    leaps[row] = simulation._decidedLeaps[row];
                    headings[row] = simulation._decidedHeadings[row];
                    waits[row] = simulation._decidedWaits[row];
                    stalls[row] = simulation._decidedStalls[row];
                    sides[row] = simulation._decidedSides[row];
                }
            });
    }

    /// <summary>Deterministic per-row noise: scouting must not read the shared draw stream in parallel.</summary>
    private float Noise(int tick, int id, int salt)
    {
        var draw = _scoutRandom.ForEntity(unchecked((uint)id), unchecked((uint)tick)).At(unchecked((uint)salt));
        return (draw >> 40) / (float)0x1000000;
    }

    /// <summary>Scouting: hold a heading, turn on a per-worker cadence, and bounce off the garden edge.</summary>
    private Vector2 Wander(int tick, int id, Vector2 position, float speed, ref float heading)
    {
        if ((tick + id) % (20 + (id & 31)) == 0)
        {
            heading += (Noise(tick, id, 1) - 0.5f) * 3.2f;
            if (heading > MathF.PI)
            {
                heading -= MathF.Tau;
            }
            else if (heading < -MathF.PI)
            {
                heading += MathF.Tau;
            }
        }

        var next = position + (new Vector2(MathF.Cos(heading), MathF.Sin(heading)) * speed);
        if (next.X < 0 || next.X > Width || next.Y < 0 || next.Y > Height)
        {
            heading += MathF.PI;
            next = Vector2.Clamp(next, Vector2.Zero, new(Width, Height));
        }

        return next;
    }

    private static float Direction(Vector2 from, Vector2 to, float fallback)
    {
        var delta = to - from;
        return delta.LengthSquared() < 0.0001f ? fallback : MathF.Atan2(delta.Y, delta.X);
    }

    /// <summary>
    /// Parallel-safe pass: each worker row reads shared grids and writes only its own position,
    /// cooldown, leap, heading, patience and stall columns; bursts, pickups and deposits resolve
    /// serially. Workers perceive a local neighbourhood first, then scout or climb a trail; only a
    /// worker that finds nothing for a while commits to the full-map search, and stays committed
    /// until it arrives. Thickets are followed rather than pressed into.
    /// </summary>
    private static void DecidePopulationRange(
        DecisionState state,
        int partition,
        scoped ref PopulationAccessScope<GardenCreature> access)
    {
        _ = partition;
        var simulation = state.Simulation;
        var range = access.Range;
        var positions = access.ReadRange(simulation._creatures.Columns.Position);
        var cooldowns = access.ReadRange(simulation._creatures.Columns.Cooldown);
        var leaps = access.ReadRange(simulation._creatures.Columns.LeapProgress);
        var kinds = access.ReadRange(simulation._creatures.Columns.Kind);
        var carried = access.ReadRange(simulation._creatures.Columns.CarriedGlow);
        var headings = access.ReadRange(simulation._creatures.Columns.Heading);
        var waits = access.ReadRange(simulation._creatures.Columns.WanderTicks);
        var stalls = access.ReadRange(simulation._creatures.Columns.StallTicks);
        var sides = access.ReadRange(simulation._creatures.Columns.SlideSide);
        var ids = access.ReadRange(simulation._creatures.Columns.StableId);
        var plantGrowth = access.ReadAll(state.Plants, simulation._plants.Columns.Growth);
        var plants = access.ReadAll(state.Plants, simulation._plants.Columns.Position);
        var moteValues = access.ReadAll(state.Motes, simulation._motes.Columns.Value);
        var motes = access.ReadAll(state.Motes, simulation._motes.Columns.Position);
        var tick = simulation.Tick;
        var scoutSpeed = simulation._tickSpeed * 0.7f;

        for (var offset = 0; offset < range.Length; offset++)
        {
            var row = range.Start + offset;
            var position = positions[offset];
            var heading = headings[offset];
            var waiting = waits[offset];
            var stall = stalls[offset];
            var side = sides[offset];
            var next = position;
            var mode = IntentNone;
            var target = -1;
            var cooldown = cooldowns[offset];
            var leap = leaps[offset];

            if ((CreatureKind)kinds[offset] == CreatureKind.Burstling)
            {
                if (cooldown > 0)
                {
                    cooldown--;
                    leap = 0;
                    waiting = 0;
                }
                else
                {
                    target = simulation.FindNearestRipe(
                        position, simulation._tickScoutRadius, t_plantRows ??= new(256), plantGrowth, plants);
                    var nearby = target >= 0;
                    if (!nearby && waiting >= simulation._tickPatience)
                    {
                        target = simulation.FindNearestRipe(
                            position, MaximumSearchRadius, t_plantRows ??= new(256), plantGrowth, plants);
                    }

                    if (target < 0)
                    {
                        if (simulation._scent.TrySteer(position, simulation._tickTrailThreshold, out var trail))
                        {
                            // Scout along a known food route before falling back to blind wandering.
                            next = Move(position, position + (trail * 16f), scoutSpeed);
                            heading = MathF.Atan2(trail.Y, trail.X);
                        }
                        else
                        {
                            next = simulation.Wander(tick, ids[offset], position, scoutSpeed, ref heading);
                        }

                        waiting++;
                    }
                    else
                    {
                        var plantPosition = plants[target];
                        var distance = Vector2.DistanceSquared(position, plantPosition);
                        next = Move(position, plantPosition, simulation._tickSpeed);
                        leap = Math.Clamp(1 - (MathF.Sqrt(distance) / 48f), 0, 1);
                        heading = Direction(position, plantPosition, heading);
                        waiting = nearby ? 0 : simulation._tickPatience;
                        mode = IntentBurst;
                    }
                }
            }
            else
            {
                // A fresh grid query here would see motes collected later this tick; serial
                // resolution skips values already taken, preserving first-come collection.
                target = simulation.FindNearestMote(
                    position, simulation._tickScoutRadius, t_moteRows ??= new(256), moteValues, motes);
                var nearby = target >= 0;
                if (!nearby && waiting >= simulation._tickPatience && carried[offset] == 0)
                {
                    target = simulation.FindNearestMote(
                        position, MaximumSearchRadius, t_moteRows ??= new(256), moteValues, motes);
                }

                if (carried[offset] >= simulation._tickCapacity || (carried[offset] > 0 && target < 0))
                {
                    next = Move(position, Colony, simulation._tickSpeed);
                    heading = Direction(position, Colony, heading);
                    waiting = 0;
                    target = -1;
                    mode = IntentDeposit;
                }
                else if (target >= 0)
                {
                    var motePosition = motes[target];
                    next = Move(position, motePosition, simulation._tickSpeed);
                    heading = Direction(position, motePosition, heading);
                    waiting = nearby ? 0 : simulation._tickPatience;
                    mode = IntentCollect;
                }
                else if (simulation._scent.TrySteer(position, simulation._tickTrailThreshold, out var trail))
                {
                    // Recruited: climb the trail that a burst left between the pod and the colony.
                    next = Move(position, position + (trail * 16f), scoutSpeed);
                    heading = MathF.Atan2(trail.Y, trail.X);
                    waiting++;
                }
                else
                {
                    next = simulation.Wander(tick, ids[offset], position, scoutSpeed, ref heading);
                    waiting++;
                }
            }

            // Trace the thicket edge instead of grinding into it; change tack when fully wedged.
            var legal = simulation.Obstacles.Steer(position, next, ref side);
            var troubled = side != 0 || (next != position && legal == position);
            stall = troubled ? stall + 1 : 0;
            if (stall > StallLimit)
            {
                // Stop hugging the thicket and try a fresh direction: a worker should never keep
                // pressing into the same edge, and re-aiming also breaks endless boundary loops.
                heading += 2.1f;
                if (heading > MathF.PI)
                {
                    heading -= MathF.Tau;
                }

                side = 0;
                legal = simulation.Obstacles.Steer(position, simulation.Wander(tick, ids[offset], position, scoutSpeed, ref heading), ref side);
                stall = 0;
                waiting = simulation._tickPatience;
            }

            simulation._intents[row] = IntentNone;
            simulation._decidedPositions[row] = legal;
            simulation._decidedCooldowns[row] = cooldown;
            simulation._decidedLeaps[row] = leap;
            simulation._decidedHeadings[row] = heading;
            simulation._decidedWaits[row] = waiting;
            simulation._decidedStalls[row] = stall;
            simulation._decidedSides[row] = side;

            if (mode == IntentNone)
            {
                continue;
            }

            if (mode == IntentBurst && target >= 0 && Vector2.DistanceSquared(legal, plants[target]) < 16)
            {
                simulation._decidedCooldowns[row] = simulation._tickCooldown;
                simulation._intents[row] = IntentBurst;
                simulation._intentTargets[row] = target;
            }
            else if (mode == IntentCollect && target >= 0 && Vector2.DistanceSquared(legal, motes[target]) < 25)
            {
                simulation._intents[row] = IntentCollect;
                simulation._intentTargets[row] = target;
            }
            else if (mode == IntentDeposit && Vector2.DistanceSquared(legal, Colony) < 16)
            {
                simulation._intents[row] = IntentDeposit;
            }
        }
    }

    private void ResolveWorkerIntents(int count)
    {
        _creatures.WithAccess(new PopulationRange(0, count), this,
            static (GardenSimulation simulation, scoped ref PopulationAccessScope<GardenCreature> access) =>
            {
                var ids = access.ReadRange(simulation._creatures.Columns.StableId);
                var copies = access.ReadRange(simulation._creatures.Columns.IsCopy);
                var carried = access.ReadRange(simulation._creatures.Columns.CarriedGlow);
                ids.CopyTo(simulation._resolvedIds);
                copies.CopyTo(simulation._resolvedCopies);
                carried.CopyTo(simulation._resolvedCarried);
            });

        for (var row = 0; row < count; row++)
        {
            switch (_intents[row])
            {
                case IntentBurst:
                    var plant = _intentTargets[row];
                    var plantState = _plants[plant];
                    if (plantState.Growth < 1)
                    {
                        // Another row already burst this plant; refund the cooldown decision.
                        _creatures[row] = _creatures[row] with { Cooldown = 0 };
                        break;
                    }
                    BurstPlant(plant, new Impact(plantState.Position,
                        new Reaction(_resolvedIds[row], !_resolvedCopies[row])));
                    break;
                case IntentCollect:
                    var mote = _intentTargets[row];
                    var moteState = _motes[mote];
                    var value = moteState.Value;
                    if (value <= 0)
                    {
                        break;
                    }

                    _creatures[row] = _creatures[row] with { CarriedGlow = _resolvedCarried[row] + value };
                    _motes[mote] = moteState with { Value = 0 };
                    // Pickups keep refreshing the route while there is still food at the far end,
                    // so the trail only fades once the pod has actually been stripped.
                    _scent.StampTrail(moteState.Position, Colony, _tickTrailStrength);
                    Publish("garden.glow.collected", value);
                    break;
                case IntentDeposit:
                    var amount = _resolvedCarried[row];
                    Glow += amount;
                    BankedGlow += amount;
                    _creatures[row] = _creatures[row] with { CarriedGlow = 0 };
                    Publish("garden.glow.deposited", amount);
                    break;
            }
        }
    }

    /// <summary>Primary bursts can trigger talent rules; secondary bursts only carry a presentation anchor.</summary>
    private void BurstPlant(int index, Impact? impact = null, Vector2? anchor = null)
    {
        var p = _plants[index];
        if (p.Growth < 1)
        {
            return;
        }

        _plants[index] = p with
        {
            Growth = 0
        };
        BurstCount++;
        // Fresh food: mark the route so nearby gatherers can be recruited onto it.
        _scent.StampTrail(p.Position, Colony, _tickTrailStrength);

        for (var j = 0; j < 8 + Rank(UpgradeKind.PodYield) * 2 && _motes.Count < MaxMotes; j++)
        {
            var angle = NextFloat() * MathF.Tau;
            var speed = 0.5f + NextFloat() * 2;
            _motes.Add(new(_nextMoteId++, p.Position, new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * speed, 1));
        }
        var published = impact ?? (anchor.HasValue ? new Impact(anchor.Value) : null);
        Publish(impact.HasValue ? "garden.pod.burst" : "garden.pod.secondary", p.Id, published);
    }
}

