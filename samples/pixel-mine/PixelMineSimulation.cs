using System.Collections.Immutable;
using Systemic.Host;
using Systemic.Kernel;

namespace PixelMine;

public enum BallKind
{
    Basic = 0,
    Smash = 1,
    Directional = 2
}

public enum UpgradeKind
{
    Power = 0,
    Speed = 1,
    Capacity = 2,
    AutoFire = 3,
    Rod = 4,
    Smash = 5,
    Directional = 6
}

public readonly record struct UpgradeInfo(
    string Name,
    bool UsesPrisms,
    int Level,
    int MaxLevel,
    long Cost);

public readonly record struct MineHitEvent(int X, int Y);

/// <summary>Integer-only mine rules: ball physics, erosion, economy and the golden pixel win.</summary>
public sealed class PixelMineSimulation : ISystemicSystem, IDisposable
{
    public const int MaximumBalls = 1080;
    public const int StartingDust = 40;
    public const int StartingBalls = 6;
    private const int MaximumPendingShots = 32;
    private const int MaximumHitEventsPerTick = 24;
    private const int BaseShotCost = 3;
    private const int SmashShotCost = 8;
    private const int DirectionalShotCost = 5;
    private const int SmashPrismCost = 8;
    private const int DirectionalPrismCost = 12;

    private static readonly ImmutableArray<SystemAccess> s_access =
    [
        SystemAccess.DeferWorldWrites,
        MineBallPopulation.WriteX,
        MineBallPopulation.WriteY,
        MineBallPopulation.WriteDirectionX,
        MineBallPopulation.WriteDirectionY,
        MineBallPopulation.WriteKind
    ];

    private readonly int _mineId;
    private readonly Entity _dustEntity;
    private readonly Entity _prismEntity;
    private readonly PixelMineWorld _world;
    private readonly DeterministicRandomFactory _random;
    private readonly Population<MineBall> _balls;
    private readonly int _populationCapacity;
    private readonly int _speedMultiplier;
    private readonly DoubleBufferedPopulationColumn<int> _x;
    private readonly DoubleBufferedPopulationColumn<int> _y;
    private readonly DoubleBufferedPopulationColumn<int> _directionX;
    private readonly DoubleBufferedPopulationColumn<int> _directionY;
    private readonly DoubleBufferedPopulationColumn<int> _kinds;
    private readonly UpgradeInfo[] _upgrades = new UpgradeInfo[7];
    private readonly Queue<MineCommand> _commands = new();
    private readonly ShotRequest[] _pendingShots = new ShotRequest[MaximumPendingShots];
    private readonly MineHitEvent[] _hitEvents = new MineHitEvent[MaximumHitEventsPerTick];
    private readonly int[] _freeSlots;
    private readonly MineBall[] _ballSnapshot;
    private int _pendingShotCount;
    private int _hitEventCount;
    private int _freeSlotCount;
    private int _autoCooldown;
    private int _autoAngle;
    private int _messageTicks;
    private string _message = string.Empty;
    private int _tick;
    private bool _goldFound;
    private bool _disposed;

    public PixelMineSimulation(World world, int mineId, int dustId, int prismId, int seed)
        : this(world, mineId, dustId, prismId, seed, 0, 1)
    {
    }

    internal PixelMineSimulation(World world, int mineId, int dustId, int prismId, int seed, int stressBallCount, int speedMultiplier)
    {
        ArgumentNullException.ThrowIfNull(world);

        if (stressBallCount != 0 && stressBallCount is < 10_000 or > 50_000)
        {
            throw new ArgumentOutOfRangeException(nameof(stressBallCount), "Stress runs require 10,000..50,000 balls.");
        }

        if ((stressBallCount == 0 && speedMultiplier != 1)
            || (stressBallCount > 0 && speedMultiplier is < 8 or > 32))
        {
            throw new ArgumentOutOfRangeException(nameof(speedMultiplier), "Stress speed must be 8..32.");
        }

        _mineId = mineId;
        _dustEntity = world.ById(dustId) ?? throw new ArgumentException("The dust resource entity is missing.", nameof(dustId));
        _prismEntity = world.ById(prismId) ?? throw new ArgumentException("The prism resource entity is missing.", nameof(prismId));
        _world = new PixelMineWorld(seed);
        _random = new DeterministicRandomFactory((ulong)(uint)seed, Id);
        _populationCapacity = Math.Max(MaximumBalls, stressBallCount);
        _speedMultiplier = stressBallCount == 0 ? 1 : speedMultiplier;
        _freeSlots = new int[_populationCapacity];
        _ballSnapshot = new MineBall[_populationCapacity];

        Dust = StartingDust;
        DefineUpgrades();

        _balls = MineBallPopulation.Define(world);
        var columns = MineBallPopulation.Bind(_balls);
        _x = columns.X;
        _y = columns.Y;
        _directionX = columns.DirectionX;
        _directionY = columns.DirectionY;
        _kinds = columns.Kind;
        _balls.Reserve(_populationCapacity);
        if (_balls.AddRange(_populationCapacity).Start != 0)
        {
            throw new InvalidOperationException("A new ball population must begin at row zero.");
        }

        var values = new MineBall[_populationCapacity];
        var spawn = _random.ForEntity((ulong)(uint)mineId, 0);
        for (var row = 0; row < _populationCapacity; row++)
        {
            values[row] = new MineBall { Kind = -1 };
        }

        var initialBalls = stressBallCount == 0 ? StartingBalls : stressBallCount;
        for (var row = 0; row < initialBalls; row++)
        {
            var (directionX, directionY) = MineMath.Directions[(int)(spawn.At((ulong)row) % 16)];
            var offsetX = stressBallCount == 0 ? 0 : (int)(spawn.At((ulong)(50_000 + row)) % 131) - 65;
            var offsetY = stressBallCount == 0 ? 0 : (int)(spawn.At((ulong)(100_000 + row)) % 81) - 40;
            values[row] = new MineBall
            {
                X = (_world.SpawnX + offsetX) * MineMath.PositionOne,
                Y = (_world.SpawnY + offsetY) * MineMath.PositionOne,
                DirectionX = directionX,
                DirectionY = directionY,
                Kind = (int)BallKind.Basic
            };
        }

        MineBall.InitializeRange(_balls, new PopulationRange(0, _populationCapacity), values);
        MineBallPopulation.Snapshot(_balls, new PopulationRange(0, _populationCapacity), _ballSnapshot);
        BallCount = initialBalls;
    }

    public string Id => "sample.pixel-mine.balls";

    public int Order => 100;

    public IReadOnlyList<SystemAccess> Access => s_access;

    public int Tick => _tick;

    public long Dust { get; private set; }

    public int Prisms { get; private set; }

    public long MinedPixels { get; private set; }

    public int BallsFired { get; private set; }

    public int BallCount { get; private set; }

    public int BallLimit => 120 + (60 * UpgradeLevel(UpgradeKind.Capacity));

    public bool Won { get; private set; }

    public int WinTick { get; private set; } = -1;

    public string Message => _messageTicks > 0 ? _message : string.Empty;

    public PixelMineWorld World => _world;

    public IReadOnlyList<UpgradeInfo> Upgrades => _upgrades;

    internal ReadOnlySpan<MineBall> Balls => _ballSnapshot;

    public ReadOnlySpan<MineHitEvent> HitEvents => _hitEvents.AsSpan(0, _hitEventCount);

    public bool HasRod => UpgradeLevel(UpgradeKind.Rod) > 0;

    /// <summary>Normalized direction in pixels from the spawn cave to the golden pixel.</summary>
    public (int X, int Y) GoldDirection => MineMath.DirectionTo(_world.SpawnX, _world.SpawnY, _world.GoldX, _world.GoldY);

    public int GoldDistance => MineMath.IntegerSquareRoot(
        ((long)_world.GoldX - _world.SpawnX) * ((long)_world.GoldX - _world.SpawnX)
        + ((long)_world.GoldY - _world.SpawnY) * ((long)_world.GoldY - _world.SpawnY));

    public int UpgradeLevel(UpgradeKind kind) => _upgrades[(int)kind].Level;

    public bool IsUnlocked(BallKind kind) => kind switch
    {
        BallKind.Basic => true,
        BallKind.Smash => UpgradeLevel(UpgradeKind.Smash) > 0,
        _ => UpgradeLevel(UpgradeKind.Directional) > 0
    };

    public static int ShotCost(BallKind kind) => kind switch
    {
        BallKind.Basic => BaseShotCost,
        BallKind.Smash => SmashShotCost,
        _ => DirectionalShotCost
    };

    public void RequestShot(int targetX, int targetY, BallKind kind)
    {
        if (_commands.Count >= 128)
        {
            return;
        }

        var (directionX, directionY) = MineMath.DirectionTo(_world.SpawnX, _world.SpawnY, targetX, targetY);
        _commands.Enqueue(new MineCommand(MineCommandKind.Shot, kind, UpgradeKind.Power, directionX, directionY));
    }

    public void RequestUpgrade(UpgradeKind kind)
    {
        if (_commands.Count >= 128)
        {
            return;
        }

        _commands.Enqueue(new MineCommand(MineCommandKind.Upgrade, BallKind.Basic, kind, 0, 0));
    }

    public void Step(SystemicSystemContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _hitEventCount = 0;
        if (!context.World.ContainsEntity(_mineId))
        {
            return;
        }

        _tick = context.Tick;
        if (_messageTicks > 0)
        {
            _messageTicks--;
        }

        DrainCommands();
        MaybeAutoFire();
        AdvanceBalls();

        context.Commands.SetProperty(_dustEntity.Id, "balance", Dust);
        context.Commands.SetProperty(_prismEntity.Id, "balance", Prisms);
        if (_goldFound)
        {
            _goldFound = false;
            context.DeferFact(MineFacts.GoldenPixelFound(_mineId));
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }

    private void DrainCommands()
    {
        while (_commands.Count > 0)
        {
            var command = _commands.Dequeue();
            if (command.Kind == MineCommandKind.Upgrade)
            {
                ApplyUpgrade(command.Upgrade);
            }
            else if (_pendingShotCount < MaximumPendingShots)
            {
                _pendingShots[_pendingShotCount++] = new ShotRequest(command.DirectionX, command.DirectionY, command.BallKind);
            }
        }
    }

    private void MaybeAutoFire()
    {
        var level = UpgradeLevel(UpgradeKind.AutoFire);
        if (level == 0)
        {
            return;
        }

        if (_autoCooldown > 0)
        {
            _autoCooldown--;
            return;
        }

        _autoCooldown = Math.Max(2,
            45 - (6 * Math.Min(level, 3)) - (3 * Math.Max(0, level - 3)));
        int directionX, directionY;
        if (HasRod && !Won)
        {
            (directionX, directionY) = GoldDirection;
        }
        else
        {
            _autoAngle = (_autoAngle + 7) % 16;
            (directionX, directionY) = MineMath.Directions[_autoAngle];
        }

        if (_pendingShotCount < MaximumPendingShots)
        {
            _pendingShots[_pendingShotCount++] = new ShotRequest(directionX, directionY, BallKind.Basic);
        }
    }

    private void AdvanceBalls()
    {
        _balls.WithAccess(
            new PopulationRange(0, _populationCapacity),
            this,
            static (simulation, scoped ref access) =>
                simulation.AdvanceBalls(ref access));

        _x.Swap();
        _y.Swap();
        _directionX.Swap();
        _directionY.Swap();
        _kinds.Swap();
        MineBallPopulation.Snapshot(_balls, new PopulationRange(0, _populationCapacity), _ballSnapshot);
    }

    private void AdvanceBalls(scoped ref PopulationAccessScope<MineBall> access)
    {
        var positionsX = access.ReadCurrent(_x);
        var positionsY = access.ReadCurrent(_y);
        var directionsX = access.ReadCurrent(_directionX);
        var directionsY = access.ReadCurrent(_directionY);
        var kinds = access.ReadCurrent(_kinds);
        var nextX = access.EditNext(_x);
        var nextY = access.EditNext(_y);
        var nextDirectionX = access.EditNext(_directionX);
        var nextDirectionY = access.EditNext(_directionY);
        var nextKinds = access.EditNext(_kinds);

        _freeSlotCount = 0;
        var alive = 0;
        var speedScale = 100 + (UpgradeLevel(UpgradeKind.Speed) * 12);
        var random = _random.ForEntity((ulong)(uint)_mineId, (ulong)_tick);

        for (var row = 0; row < _populationCapacity; row++)
        {
            if (kinds[row] < 0)
            {
                nextKinds[row] = -1;
                _freeSlots[_freeSlotCount++] = row;
                continue;
            }

            var kind = (BallKind)kinds[row];
            var positionX = positionsX[row];
            var positionY = positionsY[row];
            var directionX = directionsX[row];
            var directionY = directionsY[row];
            var speed = (Speed(kind) * speedScale * _speedMultiplier) / 100;
            if (_speedMultiplier == 1)
            {
                speed /= 9;
            }

            var stepCount = Math.Max(1, (speed + 127) / 128);
            var stepX = ((long)directionX * speed) / stepCount;
            var stepY = ((long)directionY * speed) / stepCount;
            var dead = false;

            for (var step = 0; step < stepCount && !dead; step++)
            {
                positionX += (int)stepX;
                positionY += (int)stepY;
                if (positionX < 0 || positionY < 0
                    || positionX >= PixelMineWorld.Width * MineMath.PositionOne
                    || positionY >= PixelMineWorld.Height * MineMath.PositionOne)
                {
                    dead = true;
                    break;
                }

                var cellX = positionX >> 16;
                var cellY = positionY >> 16;
                if (!_world.IsSolid(cellX, cellY))
                {
                    continue;
                }

                Erode(cellX, cellY, kind, directionX, directionY);

                if (kind == BallKind.Directional)
                {
                    continue;
                }

                if (Math.Abs(directionX) >= Math.Abs(directionY))
                {
                    directionX = -directionX;
                }
                else
                {
                    directionY = -directionY;
                }

                directionX += (int)(random.At((ulong)(1000 + row)) % 41) - 20;
                directionY += (int)((random.At((ulong)(2000 + row)) >> 8) % 41) - 20;
                MineMath.Normalize(ref directionX, ref directionY);
                positionX -= (int)(stepX / 2);
                positionY -= (int)(stepY / 2);
                break;
            }

            if (dead)
            {
                nextKinds[row] = -1;
                _freeSlots[_freeSlotCount++] = row;
                continue;
            }

            nextX[row] = positionX;
            nextY[row] = positionY;
            nextDirectionX[row] = directionX;
            nextDirectionY[row] = directionY;
            nextKinds[row] = (int)kind;
            alive++;
        }

        alive = SpawnShots(alive, random, nextX, nextY, nextDirectionX, nextDirectionY, nextKinds);

        BallCount = alive;
        _pendingShotCount = 0;
    }

    private int SpawnShots(
        int alive,
        DeterministicRandom random,
        Span<int> nextX,
        Span<int> nextY,
        Span<int> nextDirectionX,
        Span<int> nextDirectionY,
        Span<int> nextKinds)
    {
        for (var shot = 0; shot < _pendingShotCount; shot++)
        {
            var request = _pendingShots[shot];
            var kind = request.BallKind;
            if (!IsUnlocked(kind))
            {
                Notify("BALL LOCKED");
                continue;
            }

            if (alive >= BallLimit || _freeSlotCount == 0)
            {
                Notify("BALL LIMIT");
                break;
            }

            var cost = ShotCost(kind);
            if (Dust < cost)
            {
                Notify("MORE DUST NEEDED");
                break;
            }

            var directionX = request.DirectionX;
            var directionY = request.DirectionY;
            if (directionX == 0 && directionY == 0)
            {
                (directionX, directionY) = MineMath.Directions[(int)(random.At((ulong)(3000 + shot)) % 16)];
            }

            Dust -= cost;
            BallsFired++;
            var row = _freeSlots[--_freeSlotCount];
            nextX[row] = _world.SpawnX * MineMath.PositionOne;
            nextY[row] = _world.SpawnY * MineMath.PositionOne;
            nextDirectionX[row] = directionX;
            nextDirectionY[row] = directionY;
            nextKinds[row] = (int)kind;
            alive++;
        }

        return alive;
    }

    private int Erode(int cellX, int cellY, BallKind kind, int directionX, int directionY)
    {
        var power = UpgradeLevel(UpgradeKind.Power);
        var destroyed = 0;
        switch (kind)
        {
            case BallKind.Basic:
                destroyed += Damage(cellX, cellY, 1);
                for (var reach = 1; reach <= power; reach++)
                {
                    destroyed += Damage(
                        cellX + ((directionX * reach) / MineMath.DirectionOne),
                        cellY + ((directionY * reach) / MineMath.DirectionOne),
                        1);
                }
                break;

            case BallKind.Smash:
                destroyed += DamageDisc(cellX, cellY, 2 + (power / 2), 2);
                break;

            default:
                destroyed += DamageDisc(cellX, cellY, 1 + (power / 3), 2);
                break;
        }

        return destroyed;
    }

    private int DamageDisc(int centerX, int centerY, int radius, int amount)
    {
        var destroyed = 0;
        var radiusSquared = radius * radius;
        for (var dy = -radius; dy <= radius; dy++)
        {
            for (var dx = -radius; dx <= radius; dx++)
            {
                if ((dx * dx) + (dy * dy) <= radiusSquared)
                {
                    destroyed += Damage(centerX + dx, centerY + dy, amount);
                }
            }
        }

        return destroyed;
    }

    private int Damage(int x, int y, int amount)
    {
        var destroyed = _world.TryDamage(x, y, amount, out var material);
        if (material == Material.Air)
        {
            return 0;
        }

        RecordHitEvent(new MineHitEvent(x, y));
        if (!destroyed)
        {
            return 0;
        }

        MinedPixels++;
        if (material == Material.Gold)
        {
            Dust += 5000;
            if (!Won)
            {
                Won = true;
                WinTick = _tick;
                _goldFound = true;
                Notify("GOLDEN PIXEL FOUND");
            }
            return 1;
        }

        if (material == Material.Prism)
        {
            Prisms++;
            Dust += 3;
            return 1;
        }

        Dust += PixelMineWorld.Yield(material);
        return 1;
    }

    private void RecordHitEvent(MineHitEvent hitEvent)
    {
        if (_hitEventCount < _hitEvents.Length)
        {
            _hitEvents[_hitEventCount++] = hitEvent;
        }
    }

    private void ApplyUpgrade(UpgradeKind kind)
    {
        var info = _upgrades[(int)kind];
        if (info.Level >= info.MaxLevel)
        {
            Notify("ALREADY MAXED");
            return;
        }

        if (info.UsesPrisms)
        {
            if (Prisms < info.Cost)
            {
                Notify("NEED PRISMS");
                return;
            }

            Prisms -= (int)info.Cost;
        }
        else
        {
            if (Dust < info.Cost)
            {
                Notify("NEED MORE DUST");
                return;
            }

            Dust -= info.Cost;
        }

        var level = info.Level + 1;
        _upgrades[(int)kind] = info with { Level = level, Cost = NextCost(kind, level) };
        Notify($"{info.Name} UP");
    }

    private void Notify(string message)
    {
        _message = message;
        _messageTicks = 90;
    }

    private void DefineUpgrades()
    {
        _upgrades[(int)UpgradeKind.Power] = new("POWER", false, 0, 20, NextCost(UpgradeKind.Power, 0));
        _upgrades[(int)UpgradeKind.Speed] = new("SPEED", false, 0, 20, NextCost(UpgradeKind.Speed, 0));
        _upgrades[(int)UpgradeKind.Capacity] = new("BALLS", false, 0, 16, NextCost(UpgradeKind.Capacity, 0));
        _upgrades[(int)UpgradeKind.AutoFire] = new("AUTO", false, 0, 12, NextCost(UpgradeKind.AutoFire, 0));
        _upgrades[(int)UpgradeKind.Rod] = new("ROD", false, 0, 1, NextCost(UpgradeKind.Rod, 0));
        _upgrades[(int)UpgradeKind.Smash] = new("SMASH", true, 0, 1, SmashPrismCost);
        _upgrades[(int)UpgradeKind.Directional] = new("DRILL", true, 0, 1, DirectionalPrismCost);
    }

    private static long NextCost(UpgradeKind kind, int level) => kind switch
    {
        UpgradeKind.Power => 60L * Pow(3, level),
        UpgradeKind.Speed => 90L * Pow(3, level),
        UpgradeKind.Capacity => 120L * Pow(4, level),
        UpgradeKind.AutoFire => 250L * Pow(4, level),
        UpgradeKind.Rod => 300,
        _ => 0
    };

    private static long Pow(int value, int exponent)
    {
        var result = 1L;
        for (var i = 0; i < exponent; i++)
        {
            result *= value;
        }

        return result;
    }

    private static int Speed(BallKind kind) => kind switch
    {
        BallKind.Smash => 512,
        BallKind.Directional => 717,
        _ => 576
    };

    private enum MineCommandKind
    {
        Shot,
        Upgrade
    }

    private readonly record struct MineCommand(
        MineCommandKind Kind,
        BallKind BallKind,
        UpgradeKind Upgrade,
        int DirectionX,
        int DirectionY);

    private readonly record struct ShotRequest(int DirectionX, int DirectionY, BallKind BallKind);
}
