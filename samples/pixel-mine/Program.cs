using System.Diagnostics;

namespace PixelMine;

public sealed record MineOptions(
    bool Headless,
    bool VerifyRender,
    int Ticks,
    int Seed,
    string? Screenshot,
    int StressBalls,
    int StressSpeed)
{
    public static MineOptions Parse(string[] args)
    {
        bool headless = false, render = false, hasTicks = false, hasStressBalls = false, hasStressSpeed = false;
        int ticks = 12_000, seed = 42;
        var stressBalls = 0;
        var stressSpeed = 16;
        string? screenshot = null;
        var seen = new HashSet<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var option = args[i];
            if (!seen.Add(option))
            {
                throw new ArgumentException($"Duplicate option {option}.");
            }

            if (option == "--headless")
            {
                headless = true;
            }
            else if (option == "--verify-render")
            {
                render = true;
            }
            else if (option is "--ticks" or "--seed" or "--screenshot" or "--stress-balls" or "--stress-speed")
            {
                if (++i >= args.Length)
                {
                    throw new ArgumentException($"Missing value for {option}.");
                }

                if (option == "--screenshot")
                {
                    screenshot = args[i];
                }
                else
                {
                    if (!int.TryParse(args[i], out var value))
                    {
                        throw new ArgumentException($"Invalid integer for {option}.");
                    }

                    if (option == "--ticks")
                    {
                        ticks = value;
                        hasTicks = true;
                    }
                    else if (option == "--stress-balls")
                    {
                        stressBalls = value;
                        hasStressBalls = true;
                    }
                    else if (option == "--stress-speed")
                    {
                        stressSpeed = value;
                        hasStressSpeed = true;
                    }
                    else
                    {
                        seed = value;
                    }
                }
            }
            else
            {
                throw new ArgumentException($"Unknown option {option}.");
            }
        }

        if (hasStressBalls && (stressBalls is < 10_000 or > 50_000))
        {
            throw new ArgumentException("Stress ball count must be 10000..50000.");
        }

        if (hasStressSpeed && !hasStressBalls)
        {
            throw new ArgumentException("--stress-speed requires --stress-balls.");
        }

        if (hasStressBalls && (stressSpeed is < 8 or > 32))
        {
            throw new ArgumentException("Stress speed must be 8..32.");
        }

        if (hasStressBalls && !headless)
        {
            throw new ArgumentException("Stress runs require --headless.");
        }

        if (hasStressBalls && !hasTicks)
        {
            ticks = 10;
        }

        if (ticks is < 1 or > 100000)
        {
            throw new ArgumentException("Ticks must be 1..100000.");
        }

        if (headless && render)
        {
            throw new ArgumentException("Choose headless or render verification.");
        }

        if (hasTicks && !headless)
        {
            throw new ArgumentException("--ticks requires --headless.");
        }

        if (screenshot != null && !render)
        {
            throw new ArgumentException("--screenshot requires --verify-render.");
        }

        return new(headless, render, ticks, seed, screenshot, stressBalls, stressSpeed);
    }
}

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            var options = MineOptions.Parse(args);
            if (options.Headless)
            {
                return RunHeadless(options);
            }

            using var game = new MineGame(options);
            game.Run();
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            Console.Error.WriteLine("Usage: PixelMine [--seed N] [--headless --ticks 1..100000 [--stress-balls 10000..50000 --stress-speed 8..32] | --verify-render [--screenshot path.png]]");
            return 1;
        }
    }

    internal static void AutoplayTick(PixelMineSimulation simulation, int tick)
    {
        if (tick % 10 == 0)
        {
            var kind = simulation.IsUnlocked(BallKind.Directional)
                ? BallKind.Directional
                : simulation.IsUnlocked(BallKind.Smash) && tick % 60 == 0 ? BallKind.Smash : BallKind.Basic;
            var (aimX, aimY) = Aim(simulation, tick);
            simulation.RequestShot(aimX, aimY, kind);
        }

        if (tick % 60 == 0)
        {
            BuyNext(simulation);
        }
    }

    private static (int X, int Y) Aim(PixelMineSimulation simulation, int tick)
    {
        if (!simulation.HasRod || !simulation.IsUnlocked(BallKind.Directional) || simulation.Won)
        {
            var (directionX, directionY) = MineMath.Directions[(tick / 10) % 16];
            return (simulation.World.SpawnX + (directionX * 4), simulation.World.SpawnY + (directionY * 4));
        }

        var wobble = (((tick / 10) % 5) - 2) * 6;
        return (simulation.World.GoldX + wobble, simulation.World.GoldY);
    }

    private static void BuyNext(PixelMineSimulation simulation)
    {
        ReadOnlySpan<UpgradeKind> priority =
        [
            UpgradeKind.Rod,
            UpgradeKind.Directional,
            UpgradeKind.Smash,
            UpgradeKind.Power,
            UpgradeKind.Speed,
            UpgradeKind.Capacity,
            UpgradeKind.AutoFire
        ];

        foreach (var kind in priority)
        {
            var upgrade = simulation.Upgrades[(int)kind];
            if (upgrade.Level >= upgrade.MaxLevel)
            {
                continue;
            }

            var affordable = upgrade.UsesPrisms
                ? simulation.Prisms >= upgrade.Cost
                : simulation.Dust >= upgrade.Cost;
            if (affordable)
            {
                simulation.RequestUpgrade(kind);
                return;
            }
        }
    }

    private static int RunHeadless(MineOptions options)
    {
        if (options.StressBalls > 0)
        {
            return RunStressTest(options);
        }

        using var host = PixelMineHost.Create(options.Seed);
        var simulation = host.Simulation;
        for (var tick = 0; tick < options.Ticks; tick++)
        {
            AutoplayTick(simulation, tick);
            host.Step(1);
        }

        Console.WriteLine(
            $"seed={options.Seed} ticks={simulation.Tick} mined={simulation.MinedPixels} dust={simulation.Dust} "
            + $"prisms={simulation.Prisms} balls={simulation.BallCount} shots={simulation.BallsFired} won={simulation.Won}");
        if (simulation.MinedPixels == 0)
        {
            throw new InvalidOperationException("Autoplay mined nothing; use more ticks (e.g. 12,000).");
        }

        return 0;
    }

    private static int RunStressTest(MineOptions options)
    {
        using var host = PixelMineHost.CreateStressTest(options.Seed, options.StressBalls, options.StressSpeed);
        var simulation = host.Simulation;
        host.Step(2);

        var milliseconds = new double[options.Ticks];
        for (var tick = 0; tick < milliseconds.Length; tick++)
        {
            var start = Stopwatch.GetTimestamp();
            host.Step(1);
            milliseconds[tick] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        Array.Sort(milliseconds);
        var total = 0d;
        foreach (var elapsed in milliseconds)
        {
            total += elapsed;
        }

        var p95 = milliseconds[(int)Math.Ceiling(milliseconds.Length * 0.95) - 1];
        Console.WriteLine(
            $"stress seed={options.Seed} ticks={options.Ticks} balls={simulation.BallCount} "
            + $"speed={options.StressSpeed}x mean_ms={total / milliseconds.Length:F2} "
            + $"p95_ms={p95:F2} max_ms={milliseconds[^1]:F2}");
        if (simulation.BallCount == 0)
        {
            throw new InvalidOperationException("Stress run lost every ball.");
        }

        return 0;
    }
}
