using System.Numerics;

namespace MoleculeSwarm;

internal enum TurretKind : byte
{
    Gun,
    Mortar,
    Laser,
}

internal struct Turret
{
    public TurretKind Kind;
    public int Level;
    public int Mount;
    public Vector2 Position;
    public float Cooldown;
    public float Aim;
    public float Flash;
}

internal static class TurretStats
{
    public const int MaxLevel = 6;

    /// <summary>Turrets sit on a 32px mount grid laid over the whole field.</summary>
    public const int MountCell = 32;
    public const int MountColumns = Sim.Width / MountCell;
    public const int MountRows = Sim.Height / MountCell;
    public const int MountCount = MountColumns * MountRows;

    public static Vector2 MountCenter(int mount) =>
        new(((mount % MountColumns) + 0.5f) * MountCell, ((mount / MountColumns) + 0.5f) * MountCell);

    public static int Cost(TurretKind kind) => kind switch
    {
        TurretKind.Gun => 30,
        TurretKind.Mortar => 80,
        _ => 120,
    };

    public static int UpgradeCost(TurretKind kind, int level) =>
        (int)MathF.Round(Cost(kind) * (0.45f + (0.45f * level)));

    public static float Damage(TurretKind kind) => kind switch
    {
        TurretKind.Gun => 15f,
        TurretKind.Mortar => 60f,
        _ => 42f,
    };

    /// <summary>Shots per second; the laser's is its damage tick rate.</summary>
    public static float Rate(TurretKind kind) => kind switch
    {
        TurretKind.Gun => 6.2f,
        TurretKind.Mortar => 0.65f,
        _ => 9f,
    };

    public static float Range(TurretKind kind) => kind switch
    {
        TurretKind.Gun => 150f,
        TurretKind.Mortar => 260f,
        _ => 190f,
    };

    public static float Splash(TurretKind kind) => kind == TurretKind.Mortar ? 46f : 0f;

    public static float DamageAtLevel(TurretKind kind, int level) => Damage(kind) * (1f + (0.42f * (level - 1)));

    public static float RangeAtLevel(TurretKind kind, int level) => Range(kind) * (1f + (0.06f * (level - 1)));

    public static string Name(TurretKind kind) => kind switch
    {
        TurretKind.Gun => "GUN",
        TurretKind.Mortar => "MORTAR",
        _ => "LASER",
    };
}

internal readonly record struct Shell(Vector2 Origin, Vector2 Position, Vector2 Target, float Damage, float Splash);

/// <summary>
/// The turrets on the cliff shelves and the shots they have in the air. Placement is a grid lookup;
/// firing asks the horde for the nearest body in range and the shot resolves into the horde's damage.
/// </summary>
internal sealed class Emplacements
{
    public const int MaxTurrets = 512;
    public const int MaxShells = 160;

    private readonly Swarm _swarm;
    private readonly Effects _effects;
    private readonly Terrain _terrain;

    private readonly Turret[] _turrets = new Turret[MaxTurrets];
    private readonly int[] _turretAtMount = new int[TurretStats.MountCount];
    private int _turretCount;

    private readonly Shell[] _shells = new Shell[MaxShells];
    private int _shellCount;

    public Emplacements(Swarm horde, Effects effects, Terrain terrain)
    {
        _swarm = horde;
        _effects = effects;
        _terrain = terrain;
        Array.Fill(_turretAtMount, -1);
    }

    public int Count => _turretCount;

    public Turret TurretAt(int index) => _turrets[index];

    public int ShellCount => _shellCount;

    public Shell ShellAt(int index) => _shells[index];

    public bool CanBuild(int mount)
    {
        if (mount < 0 || mount >= TurretStats.MountCount)
        {
            return false;
        }

        var center = TurretStats.MountCenter(mount);
        return _terrain.IsHighGround(center.X, center.Y) && !Terrain.InCorePad(center);
    }

    public bool IsOccupied(int mount) => mount >= 0 && mount < TurretStats.MountCount && _turretAtMount[mount] >= 0;

    public int IndexAtMount(int mount) =>
        mount >= 0 && mount < TurretStats.MountCount ? _turretAtMount[mount] : -1;

    /// <summary>Puts a level-one turret on a mount. The caller has already checked and charged.</summary>
    public void Place(int mount, TurretKind kind)
    {
        if (_turretCount >= MaxTurrets)
        {
            return;
        }

        _turrets[_turretCount] = new Turret
        {
            Kind = kind,
            Level = 1,
            Mount = mount,
            Position = TurretStats.MountCenter(mount),
            Cooldown = 0.35f,
            Aim = -MathF.PI / 2f,
        };
        _turretAtMount[mount] = _turretCount;
        _turretCount++;
    }

    /// <summary>Adds a level to a turret. The caller has already checked and charged.</summary>
    public void Upgrade(int index) => _turrets[index].Level++;

    /// <summary>Aims and fires every turret for one tick.</summary>
    public void Fire(float dt)
    {
        for (var index = 0; index < _turretCount; index++)
        {
            ref var turret = ref _turrets[index];
            var range = TurretStats.RangeAtLevel(turret.Kind, turret.Level);
            var target = _swarm.FindNearest(turret.Position, range);
            turret.Flash = MathF.Max(0f, turret.Flash - (dt * 6f));
            if (target >= 0)
            {
                var position = _swarm.PositionOf(target);
                turret.Aim = MathF.Atan2(position.Y - turret.Position.Y, position.X - turret.Position.X);
            }

            turret.Cooldown -= dt;
            if (target < 0 || turret.Cooldown > 0f)
            {
                continue;
            }

            turret.Cooldown = 1f / TurretStats.Rate(turret.Kind);
            var damage = TurretStats.DamageAtLevel(turret.Kind, turret.Level);
            var origin = turret.Position;
            var aimPoint = _swarm.PositionOf(target);
            turret.Flash = 1f;
            switch (turret.Kind)
            {
                case TurretKind.Gun:
                    _swarm.Damage(target, damage);
                    _effects.AddTracer(origin, aimPoint, 0.08f, 2.4f);
                    break;

                case TurretKind.Mortar:
                    if (_shellCount < MaxShells)
                    {
                        _shells[_shellCount++] = new Shell(
                            origin,
                            origin,
                            aimPoint + (_swarm.LeadDirection(target) * 0.4f),
                            damage,
                            TurretStats.Splash(turret.Kind));
                    }

                    break;

                default:
                    _swarm.Damage(target, damage);
                    _swarm.PierceBeam(origin, aimPoint, damage * 0.7f, target);
                    _effects.AddTracer(origin, aimPoint, 0.14f, 3.8f);
                    break;
            }
        }
    }

    public void UpdateShells(float dt)
    {
        for (var index = _shellCount - 1; index >= 0; index--)
        {
            var shell = _shells[index];
            var delta = shell.Target - shell.Position;
            var distance = delta.Length();
            var step = 420f * dt;
            if (distance <= step || distance < 1f)
            {
                Detonate(shell);
                _shells[index] = _shells[--_shellCount];
                continue;
            }

            _shells[index] = shell with { Position = shell.Position + (delta / distance * step) };
        }
    }

    private void Detonate(Shell shell)
    {
        var radiusSquared = shell.Splash * shell.Splash;
        for (var index = 0; index < Sim.MaxMolecules; index++)
        {
            if (!_swarm.IsAlive(index))
            {
                continue;
            }

            var delta = _swarm.PositionOf(index) - shell.Target;
            if ((delta.X * delta.X) + (delta.Y * delta.Y) <= radiusSquared)
            {
                _swarm.Damage(index, shell.Damage);
            }
        }

        _effects.AddBoom(shell.Target, shell.Splash);
        _effects.AddScorch(shell.Target, shell.Splash * 0.85f, 10);
    }
}
