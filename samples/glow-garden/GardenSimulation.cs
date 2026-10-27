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
        if (restoring) return;

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
                if (!Obstacles.IsBlocked(spot)) break;
            }

            if (Obstacles.IsBlocked(spot)
                && !Obstacles.TryFindFreePosition(spot, null, 0, out spot))
                throw new InvalidOperationException("The garden has no free position for an initial plant.");

            _plants.Add(new(_nextPlantId++, spot, NextFloat()));
        }

        // A larger garden needs a larger founding colony, or the first harvests take minutes.
        for (var i = 0; i < StartingWorkers; i++) _creatures.Add(new(_nextCreatureId++, i < 3 ? CreatureKind.Burstling : CreatureKind.Gatherer, Colony, 0, 0, false));
    }

    public string Id => "glow-garden";

    public int Order => 0;

    private static readonly ImmutableArray<SystemAccess> s_access =
    [
        SystemAccess.WriteWorld,
        GardenPlantPopulation.WriteGrowth,
        GardenMotePopulation.WritePosition,
        GardenMotePopulation.WriteVelocity,
        GardenMotePopulation.WriteValue,
        GardenCreaturePopulation.WritePosition,
        GardenCreaturePopulation.WriteCarriedGlow,
        GardenCreaturePopulation.WriteCooldown,
        GardenCreaturePopulation.WriteLeapProgress,
    ];

    public IReadOnlyList<SystemAccess> Access => s_access;

    public IReadOnlyList<PlantState> Plants { get; }

    public IReadOnlyList<CreatureState> Creatures { get; }

    public IReadOnlyList<MoteState> Motes { get; }

    public long BurstCount { get; private set; }

    public int Tick { get; private set; }

    private float NextFloat() => _world.Draws.NextFloat();

    private static Vector2 Move(Vector2 from, Vector2 to, float distance)
    {
        var delta = to - from;
        var length = delta.Length();
        return length <= distance ? to : from + delta * (distance / length);
    }

    /// <summary>Consumed motes are tombstoned so row indexes stay valid during the worker loop.</summary>
    private void RemoveConsumedMotes()
    {
        for (var i = _motes.Count - 1; i >= 0; i--)
        {
            if (_motes.Columns.Value.ReadOnlySpan[i] <= 0) _motes.RemoveAt(i);
        }
    }

    /// <summary>
    /// Grid radius queries grow from a small radius; a hit inside the queried radius is the true
    /// nearest because nothing farther away can be closer.
    /// </summary>
    private int NearestRipe(Vector2 position, float maxRadius, List<int>? rows = null)
    {
        var scratch = rows ?? _nearbyPlants;
        var radius = MathF.Min(SearchStartRadius, maxRadius);
        while (true)
        {
            var found = -1;
            var best = float.MaxValue;
            _plantGrid.QueryRadius(position, radius, scratch);
            var growth = _plants.Columns.Growth.ReadOnlySpan;
            var positions = _plants.Columns.Position.ReadOnlySpan;
            foreach (var row in scratch)
            {
                if (growth[row] < 1) continue;
                var distance = Vector2.DistanceSquared(position, positions[row]);
                if (distance < best)
                {
                    best = distance;
                    found = row;
                }
            }

            if (found >= 0) return found;
            if (radius >= maxRadius) return -1;
            radius = MathF.Min(radius * 2, maxRadius);
        }
    }

    private int NearestMote(Vector2 position, float maxRadius, List<int>? rows = null)
    {
        var scratch = rows ?? _nearbyMotes;
        var radius = MathF.Min(SearchStartRadius, maxRadius);
        while (true)
        {
            var found = -1;
            var best = float.MaxValue;
            _moteGrid.QueryRadius(position, radius, scratch);
            var values = _motes.Columns.Value.ReadOnlySpan;
            var positions = _motes.Columns.Position.ReadOnlySpan;
            foreach (var row in scratch)
            {
                if (values[row] <= 0) continue;
                var distance = Vector2.DistanceSquared(position, positions[row]);
                if (distance < best)
                {
                    best = distance;
                    found = row;
                }
            }

            if (found >= 0) return found;
            if (radius >= maxRadius) return -1;
            radius = MathF.Min(radius * 2, maxRadius);
        }
    }

    private const byte IntentNone = 0, IntentBurst = 1, IntentCollect = 2, IntentDeposit = 3;

    private static readonly Action<GardenSimulation, int, PopulationRange> s_decideRange =
        static (simulation, _, range) => simulation.DecideRange(range.Start, range.End);

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
    private Action<SimulationFact> _publish = _ => { };
    private int _tickCooldown, _tickCapacity, _tickPatience;
    private float _tickSpeed, _tickScoutRadius, _tickTrailThreshold, _tickTrailStrength;

    public void Step(SystemicSystemContext context)
    {
        _publish = fact => context.Publish(fact);
        ExecuteCore(context);
    }

    private void ExecuteCore(SystemicSystemContext context)
    {
        Tick = context.Tick;
        _tickScoutRadius = _tuning.ScoutRadius;
        _tickPatience = _tuning.Patience;
        _tickTrailThreshold = _tuning.TrailThreshold;
        _tickTrailStrength = _tuning.TrailStrength;
        _scent.Decay(_tuning.TrailDecay);
        StepTalents();
        _plantGrid.Rebuild(_plants.Columns.Position.ReadOnlySpan);

        for (var i = 0; i < _plants.Count; i++)
        {
            var p = _plants[i];
            _plants[i] = p with
            {
                Growth = MathF.Min(1, p.Growth + (1 + Rank(UpgradeKind.PodGrowth) * 0.12f) / 240f)
            };
        }

        var motePositions = _motes.Columns.Position.EditRange(0, _motes.Count);
        var moteVelocities = _motes.Columns.Velocity.EditRange(0, _motes.Count);
        for (var i = 0; i < motePositions.Length; i++)
        {
            var previous = motePositions[i];
            var next = Vector2.Clamp(previous + moteVelocities[i], Vector2.Zero, new(Width, Height));
            // Glow drifts up to a thicket and stops against it, so it always stays collectable.
            motePositions[i] = Obstacles.Constrain(previous, next);
            moteVelocities[i] *= 0.9f;
        }
        _moteGrid.Rebuild(motePositions);

        _tickCooldown = Math.Max(3, 30 - Rank(UpgradeKind.BurstlingWorkRate));
        _tickCapacity = 3 + Rank(UpgradeKind.GathererCapacity);
        _tickSpeed = 3 + Rank(UpgradeKind.GathererSpeed) * 0.3f;
        var count = _creatures.Count;
        context.ForEachRange(this, count, s_decideRange);
        ApplyDecisions(count);
        ResolveWorkerIntents(count);
        RemoveConsumedMotes();

        // Outside any rule dispatch, so talent work can publish without reentering dispatch.
        DrainTalentIntents();
    }

    /// <summary>Copies parallel decisions into population columns in stable row order.</summary>
    private void ApplyDecisions(int count)
    {
        var positions = _creatures.Columns.Position.EditRange(0, count);
        var cooldowns = _creatures.Columns.Cooldown.EditRange(0, count);
        var leaps = _creatures.Columns.LeapProgress.EditRange(0, count);
        var headings = _creatures.Columns.Heading.EditRange(0, count);
        var waits = _creatures.Columns.WanderTicks.EditRange(0, count);
        var stalls = _creatures.Columns.StallTicks.EditRange(0, count);
        var sides = _creatures.Columns.SlideSide.EditRange(0, count);
        for (var row = 0; row < count; row++)
        {
            // Wall-following can push a worker past the garden edge; populations reject out-of-bounds rows.
            positions[row] = Vector2.Clamp(_decidedPositions[row], Vector2.Zero, new(Width, Height));
            cooldowns[row] = _decidedCooldowns[row];
            leaps[row] = _decidedLeaps[row];
            headings[row] = _decidedHeadings[row];
            waits[row] = _decidedWaits[row];
            stalls[row] = _decidedStalls[row];
            sides[row] = _decidedSides[row];
        }
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
            if (heading > MathF.PI) heading -= MathF.Tau;
            else if (heading < -MathF.PI) heading += MathF.Tau;
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
    private void DecideRange(int start, int end)
    {
        var positions = _creatures.Columns.Position.ReadOnlySpan;
        var cooldowns = _creatures.Columns.Cooldown.ReadOnlySpan;
        var leaps = _creatures.Columns.LeapProgress.ReadOnlySpan;
        var kinds = _creatures.Columns.Kind.ReadOnlySpan;
        var carried = _creatures.Columns.CarriedGlow.ReadOnlySpan;
        var headings = _creatures.Columns.Heading.ReadOnlySpan;
        var waits = _creatures.Columns.WanderTicks.ReadOnlySpan;
        var stalls = _creatures.Columns.StallTicks.ReadOnlySpan;
        var sides = _creatures.Columns.SlideSide.ReadOnlySpan;
        var ids = _creatures.Columns.StableId.ReadOnlySpan;
        var plants = _plants.Columns.Position.ReadOnlySpan;
        var motes = _motes.Columns.Position.ReadOnlySpan;
        var tick = Tick;
        var scoutSpeed = _tickSpeed * 0.7f;

        for (var row = start; row < end; row++)
        {
            var position = positions[row];
            var heading = headings[row];
            var waiting = waits[row];
            var stall = stalls[row];
            var side = sides[row];
            var next = position;
            var mode = IntentNone;
            var target = -1;
            var cooldown = cooldowns[row];
            var leap = leaps[row];

            if ((CreatureKind)kinds[row] == CreatureKind.Burstling)
            {
                if (cooldown > 0)
                {
                    cooldown--;
                    leap = 0;
                    waiting = 0;
                }
                else
                {
                    target = NearestRipe(position, _tickScoutRadius, t_plantRows ??= new(256));
                    var nearby = target >= 0;
                    if (!nearby && waiting >= _tickPatience)
                        target = NearestRipe(position, MaximumSearchRadius, t_plantRows ??= new(256));

                    if (target < 0)
                    {
                        if (_scent.TrySteer(position, _tickTrailThreshold, out var trail))
                        {
                            // Scout along a known food route before falling back to blind wandering.
                            next = Move(position, position + (trail * 16f), scoutSpeed);
                            heading = MathF.Atan2(trail.Y, trail.X);
                        }
                        else next = Wander(tick, ids[row], position, scoutSpeed, ref heading);
                        waiting++;
                    }
                    else
                    {
                        var plantPosition = plants[target];
                        var distance = Vector2.DistanceSquared(position, plantPosition);
                        next = Move(position, plantPosition, _tickSpeed);
                        leap = Math.Clamp(1 - (MathF.Sqrt(distance) / 48f), 0, 1);
                        heading = Direction(position, plantPosition, heading);
                        waiting = nearby ? 0 : _tickPatience;
                        mode = IntentBurst;
                    }
                }
            }
            else
            {
                // A fresh grid query here would see motes collected later this tick; serial
                // resolution skips values already taken, preserving first-come collection.
                target = NearestMote(position, _tickScoutRadius, t_moteRows ??= new(256));
                var nearby = target >= 0;
                if (!nearby && waiting >= _tickPatience && carried[row] == 0)
                    target = NearestMote(position, MaximumSearchRadius, t_moteRows ??= new(256));

                if (carried[row] >= _tickCapacity || (carried[row] > 0 && target < 0))
                {
                    next = Move(position, Colony, _tickSpeed);
                    heading = Direction(position, Colony, heading);
                    waiting = 0;
                    target = -1;
                    mode = IntentDeposit;
                }
                else if (target >= 0)
                {
                    var motePosition = motes[target];
                    next = Move(position, motePosition, _tickSpeed);
                    heading = Direction(position, motePosition, heading);
                    waiting = nearby ? 0 : _tickPatience;
                    mode = IntentCollect;
                }
                else if (_scent.TrySteer(position, _tickTrailThreshold, out var trail))
                {
                    // Recruited: climb the trail that a burst left between the pod and the colony.
                    next = Move(position, position + (trail * 16f), scoutSpeed);
                    heading = MathF.Atan2(trail.Y, trail.X);
                    waiting++;
                }
                else
                {
                    next = Wander(tick, ids[row], position, scoutSpeed, ref heading);
                    waiting++;
                }
            }

            // Trace the thicket edge instead of grinding into it; change tack when fully wedged.
            var legal = Obstacles.Steer(position, next, ref side);
            var troubled = side != 0 || (next != position && legal == position);
            stall = troubled ? stall + 1 : 0;
            if (stall > StallLimit)
            {
                // Stop hugging the thicket and try a fresh direction: a worker should never keep
                // pressing into the same edge, and re-aiming also breaks endless boundary loops.
                heading += 2.1f;
                if (heading > MathF.PI) heading -= MathF.Tau;
                side = 0;
                legal = Obstacles.Steer(position, Wander(tick, ids[row], position, scoutSpeed, ref heading), ref side);
                stall = 0;
                waiting = _tickPatience;
            }

            _intents[row] = IntentNone;
            _decidedPositions[row] = legal;
            _decidedCooldowns[row] = cooldown;
            _decidedLeaps[row] = leap;
            _decidedHeadings[row] = heading;
            _decidedWaits[row] = waiting;
            _decidedStalls[row] = stall;
            _decidedSides[row] = side;

            if (mode == IntentNone) continue;
            if (mode == IntentBurst && target >= 0 && Vector2.DistanceSquared(legal, plants[target]) < 16)
            {
                _decidedCooldowns[row] = _tickCooldown;
                _intents[row] = IntentBurst;
                _intentTargets[row] = target;
            }
            else if (mode == IntentCollect && target >= 0 && Vector2.DistanceSquared(legal, motes[target]) < 25)
            {
                _intents[row] = IntentCollect;
                _intentTargets[row] = target;
            }
            else if (mode == IntentDeposit && Vector2.DistanceSquared(legal, Colony) < 16)
            {
                _intents[row] = IntentDeposit;
            }
        }
    }

    private void ResolveWorkerIntents(int count)
    {
        var ids = _creatures.Columns.StableId.ReadOnlySpan;
        var copies = _creatures.Columns.IsCopy.ReadOnlySpan;
        var carried = _creatures.Columns.CarriedGlow.ReadOnlySpan;
        for (var row = 0; row < count; row++)
        {
            switch (_intents[row])
            {
                case IntentBurst:
                    var plant = _intentTargets[row];
                    if (_plants.Columns.Growth.ReadOnlySpan[plant] < 1)
                    {
                        // Another row already burst this plant; refund the cooldown decision.
                        _creatures.Columns.Cooldown.EditRange(row, 1)[0] = 0;
                        break;
                    }
                    BurstPlant(plant, new Impact(_plants.Columns.Position.ReadOnlySpan[plant],
                        new Reaction(ids[row], !copies[row])));
                    break;
                case IntentCollect:
                    var mote = _intentTargets[row];
                    var value = _motes.Columns.Value.ReadOnlySpan[mote];
                    if (value <= 0) break;
                    _creatures.Columns.CarriedGlow.EditRange(row, 1)[0] = carried[row] + value;
                    _motes.Columns.Value.EditRange(mote, 1)[0] = 0;
                    // Pickups keep refreshing the route while there is still food at the far end,
                    // so the trail only fades once the pod has actually been stripped.
                    _scent.StampTrail(_motes.Columns.Position.ReadOnlySpan[mote], Colony, _tickTrailStrength);
                    Publish("garden.glow.collected", value);
                    break;
                case IntentDeposit:
                    var amount = carried[row];
                    Glow += amount;
                    BankedGlow += amount;
                    _creatures.Columns.CarriedGlow.EditRange(row, 1)[0] = 0;
                    Publish("garden.glow.deposited", amount);
                    break;
            }
        }
    }

    /// <summary>Primary bursts can trigger talent rules; secondary bursts only carry a presentation anchor.</summary>
    private void BurstPlant(int index, Impact? impact = null, Vector2? anchor = null)
    {
        var p = _plants[index];
        if (p.Growth < 1) return;

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

