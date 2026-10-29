using System.Numerics;

using Systemic.Kernel;

namespace MoleculeSwarm;

/// <summary>
/// The molecule schema. One row is one glowing molecule, and everything the simulation needs to know
/// about it is a column: position, velocity, health, marching speed and kind. Rows are world state, so
/// the swarm clones, saves and rolls back with the world.
/// </summary>
internal sealed class Molecule : IPopulationKind
{
    public static string Id => "molecule";

    public static int Version => 1;
}

internal enum MoleculeKind : byte
{
    Grunt,
    Brute,
    Runner,
}

internal static class MoleculeStats
{
    public static float Hp(MoleculeKind kind) => kind switch
    {
        MoleculeKind.Grunt => 10f,
        MoleculeKind.Brute => 72f,
        _ => 6f,
    };

    public static float Speed(MoleculeKind kind) => kind switch
    {
        MoleculeKind.Grunt => 24f,
        MoleculeKind.Brute => 16f,
        _ => 42f,
    };

    public static int Bounty(MoleculeKind kind) => kind switch
    {
        MoleculeKind.Grunt => 1,
        MoleculeKind.Brute => 4,
        _ => 1,
    };

    public static int CoreDamage(MoleculeKind kind) => kind switch
    {
        MoleculeKind.Grunt => 1,
        MoleculeKind.Brute => 4,
        _ => 1,
    };
}

/// <summary>
/// The swarm: molecules as Kernel population rows, the flow-field march, the crowd separation and the
/// targeting grid the emitters shoot into. Each tick touches the population in a handful of scoped
/// accesses: movement, then one resolve pass that applies staged damage, adds the wave's arrivals and
/// drops the faded rows.
/// </summary>
internal sealed class Swarm
{
    private const float Friction = 3.2f;

    /// <summary>Cohesion grid: per-cell head count and centroid drive the fluid separation force.</summary>
    private const int CohesionCell = 14;
    private const int CohesionColumns = (Sim.Width / CohesionCell) + 1;
    private const int CohesionRows = (Sim.Height / CohesionCell) + 1;

    /// <summary>Cells searched each way: enough to reach a brute that starts two cells over.</summary>
    private const int RelaxRange = 2;

    private const int TargetCell = 32;
    private const int TargetColumns = Sim.Width / TargetCell;
    private const int TargetRows = Sim.Height / TargetCell;

    private static readonly PopulationColumnKey<Vector2> PositionKey =
        new("molecule.position", 1, PopulationCodecs.Vector2);

    private static readonly PopulationColumnKey<Vector2> VelocityKey =
        new("molecule.velocity", 1, PopulationCodecs.Vector2);

    private static readonly PopulationColumnKey<float> HpKey =
        new("molecule.hp", 1, PopulationCodecs.Single);

    private static readonly PopulationColumnKey<float> SpeedKey =
        new("molecule.speed", 1, PopulationCodecs.Single);

    private static readonly PopulationColumnKey<float> SeedKey =
        new("molecule.seed", 1, PopulationCodecs.Single);

    private static readonly PopulationColumnKey<float> StuckKey =
        new("molecule.stuck", 1, PopulationCodecs.Single);

    private static readonly PopulationColumnKey<byte> KindKey =
        new("molecule.kind", 1, PopulationCodecs.Byte);

    private readonly Population<Molecule> _population;
    private readonly PopulationColumn<Vector2> _position;
    private readonly PopulationColumn<Vector2> _velocity;
    private readonly PopulationColumn<float> _hp;
    private readonly PopulationColumn<float> _speed;
    private readonly PopulationColumn<float> _seed;
    private readonly PopulationColumn<float> _stuck;
    private readonly PopulationColumn<byte> _kind;

    private readonly FlowField _flow;
    private readonly Terrain _terrain;
    private readonly Random _random;

    // Per-tick views and scratch. None of this is state: it is rebuilt from the columns every tick.
    private readonly Vector2[] _positionSnapshot = new Vector2[Sim.MaxMolecules];
    private readonly byte[] _kindSnapshot = new byte[Sim.MaxMolecules];
    private readonly float[] _pendingDamage = new float[Sim.MaxMolecules];
    private readonly bool[] _fading = new bool[Sim.MaxMolecules];
    private readonly List<Arrival> _arrivals = [];
    private readonly Vector2[] _drawPositions = new Vector2[Sim.MaxMolecules];
    private readonly float[] _drawKinds = new float[Sim.MaxMolecules];
    private readonly float[] _drawSeeds = new float[Sim.MaxMolecules];
    private int _drawCount;
    private readonly int[] _cellCount = new int[TargetColumns * TargetRows];
    private readonly int[] _cellStart = new int[(TargetColumns * TargetRows) + 1];
    private readonly int[] _cellItems = new int[Sim.MaxMolecules];
    private readonly int[] _liveList = new int[Sim.MaxMolecules];
    private readonly int[] _cohesionCount = new int[CohesionColumns * CohesionRows];
    private readonly int[] _cohesionStart = new int[(CohesionColumns * CohesionRows) + 1];
    private readonly int[] _cohesionItems = new int[Sim.MaxMolecules];

    /// <summary>Molecules that arrived after the last cohesion rebuild; the grid cannot see them yet.</summary>
    private readonly List<int> _fresh = [];

    private readonly record struct Arrival(MoleculeKind Kind, float Hp, float Speed, Vector2 Position, Vector2 Velocity);

    public Swarm(World world, FlowField flow, Terrain terrain, Random random)
    {
        _flow = flow;
        _terrain = terrain;
        _random = random;

        _population = world.DefinePopulation<Molecule>();
        _position = _population.Column(PositionKey);
        _velocity = _population.Column(VelocityKey);
        _hp = _population.Column(HpKey);
        _speed = _population.Column(SpeedKey);
        _seed = _population.Column(SeedKey);
        _stuck = _population.Column(StuckKey);
        _kind = _population.Column(KindKey);
        _population.Reserve(Sim.MaxMolecules);
    }

    /// <summary>Raised when an emitter finishes a molecule: the position and kind it faded as.</summary>
    public event Action<Vector2, MoleculeKind>? MoleculeDestroyed;

    /// <summary>Raised when a molecule reaches the core and is consumed by it.</summary>
    public event Action<Vector2, MoleculeKind>? MoleculeReachedCore;

    public int Count => _population.Count;

    public int Capacity => Sim.MaxMolecules;

    public Span<Vector2> DrawPositions => _drawPositions.AsSpan(0, _drawCount);

    public Span<float> DrawKinds => _drawKinds.AsSpan(0, _drawCount);

    public Span<float> DrawSeeds => _drawSeeds.AsSpan(0, _drawCount);

    /// <summary>True while the row is live and has not already been marked to fade this tick.</summary>
    public bool IsAlive(int index) =>
        index >= 0 && index < _population.Count && !_fading[index] && _pendingDamage[index] < float.MaxValue;

    public Vector2 PositionOf(int index) => _positionSnapshot[index];

    public MoleculeKind KindOf(int index) => (MoleculeKind)_kindSnapshot[index];

    /// <summary>
    /// Queues one molecule on an edge cell, on a spot with room for its whole body. Returns false when
    /// every spot it tried was taken, so the wave waits a tick instead of arriving inside a crowd.
    /// </summary>
    public bool TrySpawn(MoleculeKind kind, float hp, float speed)
    {
        if (_population.Count + _arrivals.Count >= Sim.MaxMolecules || _flow.SpawnCells.Count == 0)
        {
            return false;
        }

        // Four cells and the emptiest wins: a wave arrives in bursts, and a burst all dropped on one
        // edge cell lands inside itself and has to be pushed apart again.
        var cell = PickSpawnCell();
        var center = FlowField.CenterOf(cell);
        var position = center;
        var push = Vector2.Zero;
        var roomiest = float.MaxValue;
        var radius = Radius(kind);
        for (var draw = 0; draw < 8; draw++)
        {
            var candidate = new Vector2(
                center.X + ((_random.NextSingle() - 0.5f) * 22f),
                center.Y + ((_random.NextSingle() - 0.5f) * 22f));
            if (_terrain.Blocked(candidate.X, candidate.Y))
            {
                continue;
            }

            // A spot that fits the sprite's whole body beats one that merely fits its centre.
            if (_terrain.Distance(candidate.X, candidate.Y) < radius + 2f)
            {
                continue;
            }

            var candidatePush = Relax(candidate, -1, radius) + PushOutOfArrivals(candidate, radius);
            var room = candidatePush.Length();
            if (room < roomiest)
            {
                roomiest = room;
                position = candidate;
                push = candidatePush;
            }

            if (room < 0.6f)
            {
                // Room for the body and nothing in the way: take it and stop looking.
                break;
            }
        }

        if (roomiest == float.MaxValue)
        {
            return false;
        }

        // The wave queues behind the frame: the spot that was just picked is where the molecule will
        // step on screen, and it starts a short way outside that, fanned out along the border so a
        // burst walks in as a stream instead of appearing on one point.
        var inward = Vector2.Normalize(Terrain.CoreCenter - position);
        var outward = position - (inward * (150f + (_random.NextSingle() * 190f))) +
            (new Vector2(-inward.Y, inward.X) * ((_random.NextSingle() - 0.5f) * 70f));
        _arrivals.Add(new Arrival(kind, hp, speed, outward, (inward * (speed * 0.45f)) + push));
        return true;
    }

    private int PickSpawnCell()
    {
        var cells = _flow.SpawnCells;
        var cell = cells[_random.Next(cells.Count)];
        var roomiest = LocalDensity(FlowField.CenterOf(cell));
        for (var draw = 0; draw < 3; draw++)
        {
            var candidate = cells[_random.Next(cells.Count)];
            var room = LocalDensity(FlowField.CenterOf(candidate));
            if (room < roomiest)
            {
                roomiest = room;
                cell = candidate;
            }
        }

        return cell;
    }

    /// <summary>Moves the living molecules for one tick, inside a single population access scope.</summary>
    public void Move(float dt)
    {
        if (_population.Count == 0)
        {
            return;
        }

        BuildCohesion();
        _fresh.Clear();
        _population.WithAccess(new PopulationRange(0, _population.Count), (Swarm: this, Dt: dt),
            static ((Swarm Swarm, float Dt) state, scoped ref PopulationAccessScope<Molecule> scope) =>
                state.Swarm.MoveWithin(ref scope, state.Dt));
        BuildTargetGrid();
    }

    private void MoveWithin(scoped ref PopulationAccessScope<Molecule> scope, float dt)
    {
        var position = scope.EditRange(_position);
        var velocity = scope.EditRange(_velocity);
        var stuck = scope.EditRange(_stuck);
        var kind = scope.ReadAll(_kind);

        var decay = MathF.Max(0f, 1f - (Friction * dt));
        for (var row = 0; row < position.Length; row++)
        {
            if (_fading[row])
            {
                continue;
            }

            var here = position[row];
            var move = velocity[row];
            var kindOfRow = (MoleculeKind)kind[row];
            var body = Radius(kindOfRow);
            var offField = IsOffField(here);

            // Goal force: the flow field pulls toward the core with just enough strength to reach the
            // molecule's marching speed once friction balances it.
            var speed = _speedSnapshot[row];
            // A packed crowd walks slower than an open one, which is what lets a gate drain: the
            // queue behind thins out instead of compressing the front row into itself.
            var density = 1f / (1f + (0.45f * LocalDensity(here)));
            var force = offField
                // A molecule still outside the frame drifts toward the nearest opening in the
                // border, at half its marching pace: a wave is meant to arrive on screen as a
                // stream through a lane, not to snap onto the wall the moment it is queued.
                ? Vector2.Normalize(_terrain.EntryPoint(here) - here) * (speed * Friction * 1.1f)
                : (_flow.Sample(here) * (speed * Friction * 2.6f * density)) + WallForce(here, body);

            // Separation is positional: half of every overlap is taken out of the step, so two
            // molecules at the same spot are half a sprite apart after one tick and settled after a
            // few, without the crowd ever gaining speed from being squeezed.
            var relax = Relax(here, row, body);
            move = (move + (force * dt)) * decay;
            var maxSpeed = speed * 2.6f;
            if (move.LengthSquared() > maxSpeed * maxSpeed)
            {
                move = Vector2.Normalize(move) * maxSpeed;
            }

            var wanted = here + (move * dt);
            var next = wanted;

            if (offField)
            {
                // Off the field there is no rock and no border yet: the step just carries the
                // molecule in, and it is a normal member of the crowd from the moment it is inside.
                velocity[row] = move;
                position[row] = next + (relax * 0.5f);
                _positionSnapshot[row] = position[row];
                stuck[row] = 0f;
                continue;
            }

            if (_terrain.Blocked(next.X, next.Y))
            {
                // Ride the mass instead of stopping dead against it.
                next = here + SlideAlongWall(here, next - here);
            }

            // The sprite is wider than its centre point, so keep the whole body off the mass: the
            // crowd can shove a molecule into it, and it has to slide back out on its own.
            var clearance = _terrain.Distance(next.X, next.Y);
            if (clearance < body)
            {
                next = PushOutOfWall(next, body, clearance);
            }

            if (_terrain.Blocked(next.X, next.Y))
            {
                next = here;
                move = Vector2.Zero;
            }
            else if ((next - wanted).LengthSquared() > 0.01f)
            {
                // The mass turned the step: carry the corrected motion into the velocity instead of
                // letting the next tick drive into the same matter again.
                move = (next - here) / MathF.Max(dt, 0.0001f);
            }

            // The map edge is a wall: clamp the body inside it and drop the velocity component that
            // was heading out, or a pinned molecule keeps grinding into the same line every tick.
            var margin = body + 1f;
            if (next.X < margin)
            {
                next.X = margin;
                move.X = MathF.Max(0f, move.X);
            }
            else if (next.X > Sim.Width - margin)
            {
                next.X = Sim.Width - margin;
                move.X = MathF.Min(0f, move.X);
            }

            if (next.Y < margin)
            {
                next.Y = margin;
                move.Y = MathF.Max(0f, move.Y);
            }
            else if (next.Y > Sim.Height - margin)
            {
                next.Y = Sim.Height - margin;
                move.Y = MathF.Min(0f, move.Y);
            }

            if (relax != Vector2.Zero)
            {
                var nudged = next + relax;
                nudged.X = Math.Clamp(nudged.X, margin, Sim.Width - margin);
                nudged.Y = Math.Clamp(nudged.Y, margin, Sim.Height - margin);
                var nudgedClearance = _terrain.Distance(nudged.X, nudged.Y);
                if (nudgedClearance < body)
                {
                    // The shove may aim into the rock; slide the body back out rather than dropping
                    // the whole correction, or a crowd pressed on a wall never relaxes.
                    nudged = PushOutOfWall(nudged, body, nudgedClearance);
                }

                if (!_terrain.Blocked(nudged.X, nudged.Y))
                {
                    next = nudged;
                }
            }

            velocity[row] = move;

            var moved = (next - here).LengthSquared() > 0.0004f;
            if (!moved)
            {
                stuck[row] += dt;
                if (stuck[row] > 3f)
                {
                    position[row] = FindFreeSpot(here);
                    stuck[row] = 0f;
                }
            }
            else if (Vector2.DistanceSquared(next, Terrain.CoreCenter) < 400f)
            {
                _fading[row] = true;
                _reached.Add((next, (MoleculeKind)kind[row]));
                continue;
            }
            else
            {
                stuck[row] = 0f;
            }

            position[row] = next;
            // The separation reads the snapshot, so keep it live as the row moves: a molecule that
            // has already stepped aside this tick is where the next one has to make room for.
            _positionSnapshot[row] = next;
        }
    }

    private readonly List<(Vector2 Position, MoleculeKind Kind)> _reached = [];

    /// <summary>
    /// True while the molecule is still walking in from outside the frame. It is a real row from the
    /// moment the wave queues it, so the field it has not entered yet is derived from its position
    /// rather than kept as a flag that could drift from the truth.
    /// </summary>
    public bool IsOnField(int index) => !IsOffField(_positionSnapshot[index]);

    private static bool IsOffField(Vector2 position) =>
        position.X < 0f || position.Y < 0f || position.X > Sim.Width || position.Y > Sim.Height;

    /// <summary>The nearest point inside the frame, which is where an entering molecule is headed.</summary>
    private static Vector2 InwardPoint(Vector2 position) => new(
        Math.Clamp(position.X, 12f, Sim.Width - 12f),
        Math.Clamp(position.Y, 12f, Sim.Height - 12f));

    /// <summary>
    /// Applies the tick's damage, adds the wave's arrivals, drops the faded rows and rebuilds the
    /// targeting grid and the views the emitters and the renderer read.
    /// </summary>
    public void Resolve(float dt)
    {
        _ = dt;
        ApplyDamage();

        if (_arrivals.Count > 0)
        {
            var range = _population.AddRange(_arrivals.Count);
            for (var index = 0; index < _arrivals.Count; index++)
            {
                var row = range.Start + index;
                _pendingHp[row] = _arrivals[index].Hp;
                _pendingSpeed[row] = _arrivals[index].Speed;
                _pendingSeed[row] = _random.NextSingle();
                _pendingKind[row] = (byte)_arrivals[index].Kind;
                _pendingPosition[row] = _arrivals[index].Position;
                _pendingVelocity[row] = _arrivals[index].Velocity;
                _fading[row] = false;
                _pendingDamage[row] = 0f;
                _fresh.Add(row);
            }

            _arrivals.Clear();
        }

        WritePendingValues();

        for (var row = _population.Count - 1; row >= 0; row--)
        {
            if (_fading[row])
            {
                _population.RemoveSwapBack(row);
                _fading[row] = false;
                _pendingDamage[row] = 0f;
            }
        }

        foreach (var reached in _reached)
        {
            MoleculeReachedCore?.Invoke(reached.Position, reached.Kind);
        }

        _reached.Clear();
        BuildTargetGrid();
    }

    /// <summary>Folds the tick's staged damage into the mirror of the health column.</summary>
    private void ApplyDamage()
    {
        for (var row = 0; row < _population.Count; row++)
        {
            var damage = _pendingDamage[row];
            _pendingDamage[row] = 0f;
            if (damage <= 0f || _fading[row])
            {
                continue;
            }

            var hp = _pendingHp[row] - damage;
            if (hp > 0f)
            {
                _pendingHp[row] = hp;
                continue;
            }

            _pendingHp[row] = 0f;
            _fading[row] = true;
            MoleculeDestroyed?.Invoke(_positionSnapshot[row], (MoleculeKind)_kindSnapshot[row]);
        }
    }

    /// <summary>Stages damage on a row; it lands in <see cref="Resolve"/> at the end of the tick.</summary>
    public void Damage(int index, float damage)
    {
        if (IsAlive(index))
        {
            _pendingDamage[index] += damage;
        }
    }

    /// <summary>Applies a splash of damage to everything inside a radius of a point.</summary>
    public void Splash(Vector2 center, float radius, float damage)
    {
        var radiusSquared = radius * radius;
        for (var row = 0; row < _population.Count; row++)
        {
            if (!IsAlive(row))
            {
                continue;
            }

            var delta = _positionSnapshot[row] - center;
            if ((delta.X * delta.X) + (delta.Y * delta.Y) <= radiusSquared)
            {
                _pendingDamage[row] += damage;
            }
        }
    }

    /// <summary>Damages everything within a beam width of the line, skipping the target already hit.</summary>
    public void PierceBeam(Vector2 origin, Vector2 target, float damage, int skip)
    {
        var direction = Vector2.Normalize(target - origin);
        var length = Vector2.Distance(origin, target) + 26f;
        for (var row = 0; row < _population.Count; row++)
        {
            if (row == skip || !IsAlive(row))
            {
                continue;
            }

            var delta = _positionSnapshot[row] - origin;
            var along = Vector2.Dot(delta, direction);
            if (along < 0f || along > length)
            {
                continue;
            }

            var perpendicular = MathF.Abs((delta.X * direction.Y) - (delta.Y * direction.X));
            if (perpendicular < 11f)
            {
                _pendingDamage[row] += damage;
            }
        }
    }

    /// <summary>Removes every molecule still on the field; used when a wave is written off as stuck.</summary>
    public void RemoveAll()
    {
        while (_population.Count > 0)
        {
            _population.RemoveSwapBack(_population.Count - 1);
        }

        _arrivals.Clear();
        Array.Clear(_fading);
        Array.Clear(_pendingDamage);
        _drawCount = 0;
    }

    /// <summary>Where a molecule tends to go next, used to lead a mortar shell.</summary>
    public Vector2 LeadDirection(int index) => _flow.Sample(_positionSnapshot[index]) * _speedSnapshot[index];

    /// <summary>The closest live molecule to a point within range, or -1 when the range is empty.</summary>
    public int FindNearest(Vector2 point, float range)
    {
        var best = -1;
        var bestDistance = range * range;
        var minColumn = Math.Clamp((int)((point.X - range) / TargetCell), 0, TargetColumns - 1);
        var maxColumn = Math.Clamp((int)((point.X + range) / TargetCell), 0, TargetColumns - 1);
        var minRow = Math.Clamp((int)((point.Y - range) / TargetCell), 0, TargetRows - 1);
        var maxRow = Math.Clamp((int)((point.Y + range) / TargetCell), 0, TargetRows - 1);
        for (var row = minRow; row <= maxRow; row++)
        {
            for (var column = minColumn; column <= maxColumn; column++)
            {
                var cell = (row * TargetColumns) + column;
                var start = _cellStart[cell];
                var end = _cellStart[cell + 1];
                for (var slot = start; slot < end; slot++)
                {
                    var index = _cellItems[slot];
                    var delta = _positionSnapshot[index] - point;
                    var squared = (delta.X * delta.X) + (delta.Y * delta.Y);
                    if (squared < bestDistance)
                    {
                        bestDistance = squared;
                        best = index;
                    }
                }
            }
        }

        return best;
    }

    // The tick's views, mirrored from the columns so reads never need an access scope.
    private float[] _hpSnapshot = new float[Sim.MaxMolecules];
    private float[] _speedSnapshot = new float[Sim.MaxMolecules];
    private float[] _pendingHp = new float[Sim.MaxMolecules];
    private float[] _pendingSpeed = new float[Sim.MaxMolecules];
    private float[] _pendingSeed = new float[Sim.MaxMolecules];
    private byte[] _pendingKind = new byte[Sim.MaxMolecules];
    private Vector2[] _pendingPosition = new Vector2[Sim.MaxMolecules];
    private Vector2[] _pendingVelocity = new Vector2[Sim.MaxMolecules];

    private void WritePendingValues()
    {
        if (_population.Count == 0)
        {
            return;
        }

        _population.WithAccess(new PopulationRange(0, _population.Count), this,
            static (Swarm swarm, scoped ref PopulationAccessScope<Molecule> scope) =>
            {
                var hp = scope.EditRange(swarm._hp);
                var speed = scope.EditRange(swarm._speed);
                var seed = scope.EditRange(swarm._seed);
                var kind = scope.EditRange(swarm._kind);
                var position = scope.EditRange(swarm._position);
                var velocity = scope.EditRange(swarm._velocity);
                for (var row = 0; row < hp.Length; row++)
                {
                    hp[row] = swarm._pendingHp[row];
                    speed[row] = swarm._pendingSpeed[row];
                    seed[row] = swarm._pendingSeed[row];
                    kind[row] = swarm._pendingKind[row];
                    position[row] = swarm._pendingPosition[row];
                    velocity[row] = swarm._pendingVelocity[row];
                }
            });
    }

    /// <summary>Rebuilds the targeting grid and the draw lists from the population in one scope.</summary>
    private void BuildTargetGrid()
    {
        if (_population.Count > 0)
        {
            _population.WithAccess(new PopulationRange(0, _population.Count), this,
                static (Swarm swarm, scoped ref PopulationAccessScope<Molecule> scope) =>
                    swarm.CopyViews(ref scope));
        }

        Array.Clear(_cellCount);
        _drawCount = 0;
        var live = 0;
        for (var row = 0; row < _population.Count; row++)
        {
            if (_fading[row])
            {
                continue;
            }

            var position = _positionSnapshot[row];
            if (IsOffField(position))
            {
                // Still outside the frame: not drawn and not targetable until it walks in.
                continue;
            }

            var cell = CellOf(position);
            _cellCount[cell] += 1;
            _liveList[live++] = row;
            _drawPositions[_drawCount] = position;
            _drawKinds[_drawCount] = _kindSnapshot[row];
            _drawSeeds[_drawCount] = _seedSnapshot[row];
            _drawCount++;
        }

        _cellStart[0] = 0;
        for (var cell = 0; cell < _cellCount.Length; cell++)
        {
            _cellStart[cell + 1] = _cellStart[cell] + _cellCount[cell];
        }

        Array.Clear(_cellCount);
        for (var slot = 0; slot < live; slot++)
        {
            var row = _liveList[slot];
            var cell = CellOf(_positionSnapshot[row]);
            _cellItems[_cellStart[cell] + _cellCount[cell]] = row;
            _cellCount[cell] += 1;
        }
    }

    private void CopyViews(scoped ref PopulationAccessScope<Molecule> scope)
    {
        var count = _population.Count;
        var position = scope.ReadAll(_position);
        var velocity = scope.ReadAll(_velocity);
        var hp = scope.ReadAll(_hp);
        var speed = scope.ReadAll(_speed);
        var seed = scope.ReadAll(_seed);
        var kind = scope.ReadAll(_kind);
        for (var row = 0; row < count; row++)
        {
            _positionSnapshot[row] = position[row];
            _velocitySnapshot[row] = velocity[row];
            _hpSnapshot[row] = hp[row];
            _speedSnapshot[row] = speed[row];
            _seedSnapshot[row] = seed[row];
            _kindSnapshot[row] = kind[row];
            _pendingPosition[row] = position[row];
            _pendingVelocity[row] = velocity[row];
            _pendingHp[row] = hp[row];
            _pendingSpeed[row] = speed[row];
            _pendingSeed[row] = seed[row];
            _pendingKind[row] = kind[row];
        }
    }

    private readonly float[] _seedSnapshot = new float[Sim.MaxMolecules];
    private readonly Vector2[] _velocitySnapshot = new Vector2[Sim.MaxMolecules];

    private void BuildCohesion()
    {
        Array.Clear(_cohesionCount);
        var live = 0;
        for (var row = 0; row < _population.Count; row++)
        {
            if (_fading[row])
            {
                continue;
            }

            var position = _positionSnapshot[row];
            _liveList[live++] = row;
            _cohesionCount[CohesionCellOf(position)] += 1;
        }

        _cohesionStart[0] = 0;
        for (var cell = 0; cell < _cohesionCount.Length; cell++)
        {
            _cohesionStart[cell + 1] = _cohesionStart[cell] + _cohesionCount[cell];
        }

        Array.Clear(_cohesionCount);
        for (var slot = 0; slot < live; slot++)
        {
            var row = _liveList[slot];
            var cell = CohesionCellOf(_positionSnapshot[row]);
            _cohesionItems[_cohesionStart[cell] + _cohesionCount[cell]] = row;
            _cohesionCount[cell] += 1;
        }
    }

    private static int CohesionCellOf(Vector2 point)
    {
        var column = Math.Clamp((int)(point.X / CohesionCell), 0, CohesionColumns - 1);
        var row = Math.Clamp((int)(point.Y / CohesionCell), 0, CohesionRows - 1);
        return (row * CohesionColumns) + column;
    }

    /// <summary>
    /// Position-based separation, the way a fluid solver handles incompressibility: any neighbour
    /// inside the spacing radius pushes the molecule out by half the overlap, read from the snapshot
    /// of where the crowd stood at the start of the tick so the answer does not depend on which
    /// molecule happened to move first.
    /// </summary>
    private Vector2 Relax(Vector2 position, int self, float selfRadius)
    {
        var column = Math.Clamp((int)(position.X / CohesionCell), 0, CohesionColumns - 1);
        var row = Math.Clamp((int)(position.Y / CohesionCell), 0, CohesionRows - 1);
        var correction = Vector2.Zero;
        for (var dy = -RelaxRange; dy <= RelaxRange; dy++)
        {
            for (var dx = -RelaxRange; dx <= RelaxRange; dx++)
            {
                var nextColumn = column + dx;
                var nextRow = row + dy;
                if (nextColumn < 0 || nextRow < 0 || nextColumn >= CohesionColumns || nextRow >= CohesionRows)
                {
                    continue;
                }

                var cell = (nextRow * CohesionColumns) + nextColumn;
                var end = _cohesionStart[cell] + _cohesionCount[cell];
                for (var slot = _cohesionStart[cell]; slot < end; slot++)
                {
                    var other = _cohesionItems[slot];
                    if (other == self)
                    {
                        continue;
                    }

                    correction += PushOutOf(position, selfRadius, other);
                }
            }
        }

        // A burst arrives between two rebuilds, so the grid does not contain the molecules that
        // arrived earlier in this same tick; keep each arrival clear of the ones ahead of it.
        foreach (var other in _fresh)
        {
            if (other != self)
            {
                correction += PushOutOf(position, selfRadius, other);
            }
        }

        var length = correction.Length();
        if (length > 5f)
        {
            correction = correction / length * 5f;
        }

        return correction;
    }

    private Vector2 PushOutOf(Vector2 position, float selfRadius, int other)
    {
        var wanted = selfRadius + Radius(KindOf(other)) + SpriteMargin;
        var delta = position - _positionSnapshot[other];
        var distance = delta.Length();
        if (distance >= wanted)
        {
            return Vector2.Zero;
        }

        if (distance < 0.25f)
        {
            var angle = (position.X * 0.71f) + (other * 0.37f);
            delta = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            distance = 0.25f;
        }

        return delta / distance * ((wanted - distance) * 0.5f);
    }

    /// <summary>
    /// The arrivals staged earlier in this tick are not in the cohesion grid yet, so a spawn has to
    /// keep clear of them by hand; otherwise a burst lands inside itself on the edge.
    /// </summary>
    private Vector2 PushOutOfArrivals(Vector2 position, float radius)
    {
        var correction = Vector2.Zero;
        foreach (var arriving in _arrivals)
        {
            var wanted = radius + Radius(arriving.Kind) + SpriteMargin;
            var delta = position - arriving.Position;
            var distance = delta.Length();
            if (distance >= wanted)
            {
                continue;
            }

            if (distance < 0.25f)
            {
                delta = new Vector2(1f, 0f);
                distance = 0.25f;
            }

            correction += delta / distance * ((wanted - distance) * 0.5f);
        }

        return correction;
    }

    /// <summary>Local head count around a point, smoothed, used to make the crowd thick and slow.</summary>
    private float LocalDensity(Vector2 position)
    {
        var column = Math.Clamp((int)(position.X / CohesionCell), 0, CohesionColumns - 1);
        var row = Math.Clamp((int)(position.Y / CohesionCell), 0, CohesionRows - 1);
        var total = 0f;
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                var nextColumn = column + dx;
                var nextRow = row + dy;
                if (nextColumn < 0 || nextRow < 0 || nextColumn >= CohesionColumns || nextRow >= CohesionRows)
                {
                    continue;
                }

                total += _cohesionCount[(nextRow * CohesionColumns) + nextColumn];
            }
        }

        return total / 9f;
    }

    /// <summary>
    /// Keeps the whole sprite off the mass, not just its centre: the matter is a repulsive surface at
    /// one sprite radius, so a molecule pressed against it stops when its body touches, never sinks in.
    /// </summary>
    private Vector2 WallForce(Vector2 position, float radius)
    {
        var force = Vector2.Zero;
        var skin = radius + 2f;
        var distance = _terrain.Distance(position.X, position.Y);
        if (distance < skin && distance >= 0f)
        {
            const float Step = 6f;
            var dx = _terrain.Distance(position.X + Step, position.Y) - _terrain.Distance(position.X - Step, position.Y);
            var dy = _terrain.Distance(position.X, position.Y + Step) - _terrain.Distance(position.X, position.Y - Step);
            var normal = new Vector2(dx, dy);
            if (normal.LengthSquared() > 0.0001f)
            {
                force += Vector2.Normalize(normal) * MathF.Min((skin - distance) * 420f, 6000f);
            }
        }

        // The map border is a wall too. Without this a burst that arrives on the edge is held on the
        // clamp line, and every molecule that follows it lands on top of the ones already there.
        var edge = radius + 10f;
        if (position.X < edge)
        {
            force.X += (edge - position.X) * 260f;
        }
        else if (position.X > Sim.Width - edge)
        {
            force.X -= (position.X - (Sim.Width - edge)) * 260f;
        }

        if (position.Y < edge)
        {
            force.Y += (edge - position.Y) * 260f;
        }
        else if (position.Y > Sim.Height - edge)
        {
            force.Y -= (position.Y - (Sim.Height - edge)) * 260f;
        }

        return force;
    }

    private Vector2 SlideAlongWall(Vector2 position, Vector2 step)
    {
        const float Probe = 7f;
        var dx = _terrain.Distance(position.X + Probe, position.Y) -
            _terrain.Distance(position.X - Probe, position.Y);
        var dy = _terrain.Distance(position.X, position.Y + Probe) -
            _terrain.Distance(position.X, position.Y - Probe);
        var normal = new Vector2(dx, dy);
        if (normal.LengthSquared() < 0.0001f)
        {
            return Vector2.Zero;
        }

        normal = Vector2.Normalize(normal);
        var tangent = new Vector2(-normal.Y, normal.X);
        // The tangent carries the step along the mass; the sliver of outward push keeps a molecule
        // that runs straight into it drifting off instead of grinding on one spot.
        return (tangent * Vector2.Dot(step, tangent)) + (normal * 0.8f);
    }

    /// <summary>
    /// Slides a point out of the mass along the gradient of the distance field until the sprite's
    /// body clears it. Capped per tick so a shove never pops a molecule across the lane.
    /// </summary>
    private Vector2 PushOutOfWall(Vector2 point, float body, float distance)
    {
        const float MaxStep = 4f;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var needed = body - distance;
            if (needed <= 0f)
            {
                break;
            }

            const float Probe = 6f;
            var dx = _terrain.Distance(point.X + Probe, point.Y) - _terrain.Distance(point.X - Probe, point.Y);
            var dy = _terrain.Distance(point.X, point.Y + Probe) - _terrain.Distance(point.X, point.Y - Probe);
            var normal = new Vector2(dx, dy);
            if (normal.LengthSquared() < 0.0001f)
            {
                break;
            }

            point += Vector2.Normalize(normal) * MathF.Min(needed, MaxStep);
            distance = _terrain.Distance(point.X, point.Y);
        }

        return point;
    }

    /// <summary>
    /// Last resort for a molecule that has been pinned for a while: step it to the neighbouring flow
    /// cell that is closer to the core, scattered so several rescues never land on one point.
    /// </summary>
    private Vector2 FindFreeSpot(Vector2 position)
    {
        var home = FlowField.CellOf(position);
        var homeColumn = home % Terrain.Columns;
        var homeRow = home / Terrain.Columns;
        for (var radius = 1; radius <= 4; radius++)
        {
            for (var dy = -radius; dy <= radius; dy++)
            {
                for (var dx = -radius; dx <= radius; dx++)
                {
                    if (Math.Abs(dx) != radius && Math.Abs(dy) != radius)
                    {
                        continue;
                    }

                    var column = homeColumn + dx;
                    var row = homeRow + dy;
                    if (column < 0 || row < 0 || column >= Terrain.Columns || row >= Terrain.Rows)
                    {
                        continue;
                    }

                    var cell = (row * Terrain.Columns) + column;
                    if (_flow.DistanceAt(cell) >= _flow.DistanceAt(home) || _flow.IsBlocked(column, row))
                    {
                        continue;
                    }

                    var center = FlowField.CenterOf(cell);
                    if (_terrain.Blocked(center.X, center.Y))
                    {
                        continue;
                    }

                    var spot = center + new Vector2(
                        (_random.NextSingle() - 0.5f) * 10f,
                        (_random.NextSingle() - 0.5f) * 10f);
                    return _terrain.Blocked(spot.X, spot.Y) ? center : spot;
                }
            }
        }

        // Nowhere to go at all: put the molecule back at the edge so a jam cannot hold a wave open.
        var spawnCells = _flow.SpawnCells;
        if (spawnCells.Count == 0)
        {
            return position;
        }

        for (var draw = 0; draw < 8; draw++)
        {
            var center = FlowField.CenterOf(spawnCells[_random.Next(spawnCells.Count)]);
            var spot = center + new Vector2(
                (_random.NextSingle() - 0.5f) * 10f,
                (_random.NextSingle() - 0.5f) * 10f);
            if (!_terrain.Blocked(spot.X, spot.Y) && _terrain.Distance(spot.X, spot.Y) > 2f)
            {
                return spot;
            }
        }

        // The edge is packed too: staying put is better than being dropped inside the mass.
        return position;
    }

    private static int CellOf(Vector2 point)
    {
        var column = Math.Clamp((int)(point.X / TargetCell), 0, TargetColumns - 1);
        var row = Math.Clamp((int)(point.Y / TargetCell), 0, TargetRows - 1);
        return (row * TargetColumns) + column;
    }

    /// <summary>
    /// Half the body width per kind. The sprite is drawn wider than this (its halo is the glow), so
    /// the collision circle is what keeps the cores apart.
    /// </summary>
    public static float Radius(MoleculeKind kind) => kind switch
    {
        MoleculeKind.Brute => 11.5f,
        MoleculeKind.Runner => 6.8f,
        _ => 8f,
    };

    /// <summary>Extra room per pair, covering the sprite wobble and the idle bob.</summary>
    private const float SpriteMargin = 2f;
}
