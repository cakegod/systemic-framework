using System.Numerics;

using Systemic.Kernel;

namespace MoleculeSwarm;

/// <summary>
/// The run: the horde, the arena, the turrets, and the economy. It owns the tick order and hands
/// the presentation layer read-only views of what happened, so drawing never changes the game.
/// </summary>
internal sealed class Sim
{
    public const int Width = 1280;
    public const int Height = 720;
    public const int MaxMolecules = 14_000;
    public const int CoreHpMax = 100;

    private readonly Random _random;
    private readonly World _world;
    private readonly FlowField _flow;
    private readonly Swarm _swarm;
    private readonly Effects _effects;
    private readonly Emplacements _turrets;

    private float _spawnClock;
    private float _stall;
    private int _lastAlive;

    public Sim(int seed)
    {
        _random = new Random(seed);
        _world = new World(seed, 1.0);
        Terrain = new Terrain(seed);
        _flow = new FlowField(Terrain);

        // A field with no reachable border cell would never see a wave, so walk the lanes the flow
        // field built and draw another arena until a molecule has somewhere to walk in from.
        for (var attempt = 1; _flow.SpawnCells.Count == 0 && attempt <= 8; attempt++)
        {
            Terrain = new Terrain(seed + (attempt * 7919));
            _flow = new FlowField(Terrain);
        }
        _effects = new Effects(_random);
        _swarm = new Swarm(_world, _flow, Terrain, _random);
        _turrets = new Emplacements(_swarm, _effects, Terrain);

        _swarm.MoleculeDestroyed += (position, kind) =>
        {
            Gold += MoleculeStats.Bounty(kind);
            Kills++;
            _effects.AddBlood(position, kind == MoleculeKind.Brute ? 12f : 9f);
        };
        _swarm.MoleculeReachedCore += (position, kind) =>
        {
            CoreHp -= MoleculeStats.CoreDamage(kind);
            Leaks++;
            _effects.AddBlood(position, kind == MoleculeKind.Brute ? 12f : 9f);
        };

        Gold = 520;
        CoreHp = CoreHpMax;
        Intermission = 4f;
    }

    public Terrain Terrain { get; }

    public FlowField Flow => _flow;

    public Swarm Swarm => _swarm;

    public Effects Effects => _effects;

    public Emplacements Turrets => _turrets;

    public int Gold { get; private set; }

    public int CoreHp { get; private set; }

    public bool GameOver => CoreHp <= 0;

    public int Wave { get; private set; }

    public int Kills { get; private set; }

    public int Leaks { get; private set; }

    public int AliveCount => _swarm.Count;

    public bool WaveActive { get; private set; }

    public int PendingSpawns { get; private set; }

    /// <summary>Seconds left before the next wave, or zero while a wave is running.</summary>
    public float Intermission { get; private set; }

    public float Elapsed { get; private set; }

    /// <summary>The mount under a screen point, or -1 when the point is outside the field.</summary>
    public static int MountAt(Vector2 point) => MountAt(point.X, point.Y);

    public static int MountAt(float x, float y)
    {
        if (x < 0f || y < 0f || x >= Width || y >= Height)
        {
            return -1;
        }

        var column = (int)(x / TurretStats.MountCell);
        var row = (int)(y / TurretStats.MountCell);
        if (column >= TurretStats.MountColumns || row >= TurretStats.MountRows)
        {
            return -1;
        }

        return (row * TurretStats.MountColumns) + column;
    }

    public bool IsBuildable(int mount) => !GameOver && _turrets.CanBuild(mount);

    public bool IsOccupied(int mount) => _turrets.IsOccupied(mount);

    public int TurretIndexAtMount(int mount) => _turrets.IndexAtMount(mount);

    public bool TryPlace(int mount, TurretKind kind)
    {
        if (GameOver || !_turrets.CanBuild(mount) || _turrets.IsOccupied(mount) ||
            _turrets.Count >= Emplacements.MaxTurrets)
        {
            return false;
        }

        var cost = TurretStats.Cost(kind);
        if (Gold < cost)
        {
            return false;
        }

        Gold -= cost;
        _turrets.Place(mount, kind);
        return true;
    }

    public bool TryUpgrade(int mount)
    {
        var index = TurretIndexAtMount(mount);
        if (index < 0 || GameOver)
        {
            return false;
        }

        var turret = _turrets.TurretAt(index);
        if (turret.Level >= TurretStats.MaxLevel)
        {
            return false;
        }

        var cost = TurretStats.UpgradeCost(turret.Kind, turret.Level);
        if (Gold < cost)
        {
            return false;
        }

        Gold -= cost;
        _turrets.Upgrade(index);
        return true;
    }

    public void StartWaveNow()
    {
        if (!GameOver && !WaveActive)
        {
            Intermission = 0f;
        }
    }

    public void Tick(float dt)
    {
        if (GameOver || dt <= 0f)
        {
            return;
        }

        Elapsed += dt;
        UpdateWaves(dt);
        SpawnQueuedMolecules(dt);
        _swarm.Move(dt);
        _turrets.Fire(dt);
        _turrets.UpdateShells(dt);
        _swarm.Resolve(dt);
        _effects.Update(dt);
    }

    private void UpdateWaves(float dt)
    {
        if (WaveActive)
        {
            TrackStragglers(dt);
            if (PendingSpawns == 0 && _swarm.Count == 0)
            {
                WaveActive = false;
                Gold += 30 + (8 * Wave);
                Intermission = 4.5f;
            }

            return;
        }

        Intermission -= dt;
        if (Intermission > 0f)
        {
            return;
        }

        Wave++;
        WaveActive = true;
        PendingSpawns = WaveCount(Wave);
        Intermission = 0f;
        _spawnClock = 0f;
    }

    /// <summary>
    /// A handful of orcs can end up wedged where no amount of steering frees them; if the last of a
    /// wave has not thinned for a while, they give up and leave, so the next wave can start.
    /// </summary>
    private void TrackStragglers(float dt)
    {
        if (PendingSpawns > 0 || _swarm.Count != _lastAlive)
        {
            _lastAlive = _swarm.Count;
            _stall = 0f;
            return;
        }

        _stall += dt;
        if (_stall < 8f)
        {
            return;
        }

        _stall = 0f;
        _swarm.RemoveAll();
    }

    private static int WaveCount(int wave) =>
        (int)MathF.Min(6000f, 380f + (200f * MathF.Pow(wave, 1.3f)));

    private static float SpawnRate(int wave) => MathF.Min(1400f, 420f + (80f * wave));

    private void SpawnQueuedMolecules(float dt)
    {
        if (!WaveActive || PendingSpawns == 0)
        {
            return;
        }

        _spawnClock -= dt;
        var rate = SpawnRate(Wave);
        var budget = (int)MathF.Min(120f, dt * rate) + 1;
        while (_spawnClock <= 0f && PendingSpawns > 0 && budget-- > 0)
        {
            if (_swarm.Count >= MaxMolecules)
            {
                break;
            }

            if (!SpawnMolecule())
            {
                // The edge is packed; leave the rest of the wave for the next tick.
                break;
            }

            PendingSpawns--;
            _spawnClock += 1f / rate;
        }

        _spawnClock = MathF.Max(_spawnClock, 0f);
    }

    private bool SpawnMolecule()
    {
        var kind = PickKind();
        return _swarm.TrySpawn(
            kind,
            MoleculeStats.Hp(kind) * MathF.Pow(1.28f, Wave - 1),
            MoleculeStats.Speed(kind) * (1f + (0.012f * (Wave - 1))));
    }

    private MoleculeKind PickKind()
    {
        var roll = _random.NextSingle();
        var bruteChance = MathF.Min(0.34f, 0.05f + (0.03f * Wave));
        var runnerChance = Wave < 2 ? 0f : MathF.Min(0.42f, 0.02f + (0.035f * Wave));
        if (roll < bruteChance)
        {
            return MoleculeKind.Brute;
        }

        return roll < bruteChance + runnerChance ? MoleculeKind.Runner : MoleculeKind.Grunt;
    }
}
