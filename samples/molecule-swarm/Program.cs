using System.Diagnostics;
using System.Globalization;
using System.Numerics;

namespace MoleculeSwarm;

internal sealed record Options(
    bool Headless,
    bool AutoPlay,
    string? Screenshot,
    int Ticks,
    int Seed,
    int Speed,
    bool SelfTest)
{
    public static Options Parse(string[] args)
    {
        var headless = false;
        var autoPlay = false;
        string? screenshot = null;
        var ticks = 60 * 90;
        var seed = 1337;
        var speed = 1;
        var selfTest = false;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--headless":
                    headless = true;
                    break;
                case "--self-test":
                    selfTest = true;
                    break;
                case "--autoplay":
                    autoPlay = true;
                    break;
                case "--screenshot" when index + 1 < args.Length:
                    screenshot = args[++index];
                    break;
                case "--ticks" when index + 1 < args.Length:
                    ticks = int.Parse(args[++index], CultureInfo.InvariantCulture);
                    break;
                case "--seed" when index + 1 < args.Length:
                    seed = int.Parse(args[++index], CultureInfo.InvariantCulture);
                    break;
                case "--speed" when index + 1 < args.Length:
                    speed = Math.Clamp(int.Parse(args[++index], CultureInfo.InvariantCulture), 1, 16);
                    break;
            }
        }

        return new Options(headless, autoPlay, screenshot, ticks, seed, speed, selfTest);
    }
}

internal static class Program
{
    private const float TickSeconds = 1f / 60f;
    private const string Title = "Molecule Swarm";

    private static int Main(string[] args)
    {
        var options = Options.Parse(args);
        if (options.Headless)
        {
            return RunHeadless(options);
        }

        if (options.Screenshot is not null)
        {
            return RunScreenshot(options);
        }

        if (options.SelfTest)
        {
            return RunSelfTest(options);
        }

        return RunWindow(options);
    }

    private static int RunHeadless(Options options)
    {
        var sim = new Sim(options.Seed);
        var brain = new Autopilot(options.Seed);
        var sw = Stopwatch.StartNew();
        var lastWave = 0;
        for (var tick = 0; tick < options.Ticks && !sim.GameOver; tick++)
        {
            brain.Think(sim);
            sim.Tick(TickSeconds);
            if (sim.Wave != lastWave)
            {
                lastWave = sim.Wave;
                Console.WriteLine(
                    $"t={sim.Elapsed,6:F1}s wave {sim.Wave,2} gold {sim.Gold,5} core {sim.CoreHp,3} " +
                    $"orcs {sim.AliveCount,5} kills {sim.Kills,6} leaked {sim.Leaks,4}");
            }

            if (sim.Gold < 0)
            {
                Console.Error.WriteLine($"gold went negative at tick {tick}");
                return 2;
            }
        }

        sw.Stop();
        var ticks = Math.Max(1, (int)(sim.Elapsed * 60));
        Console.WriteLine(
            $"done: wave {sim.Wave} kills {sim.Kills} leaked {sim.Leaks} core {sim.CoreHp} " +
            $"orcs {sim.AliveCount} turrets {sim.Turrets.Count} gold {sim.Gold} " +
            $"in {sim.Elapsed:F1}s sim ({sw.Elapsed.TotalMilliseconds / ticks:F2} ms/tick)");
        if (!sim.GameOver && options.Ticks < 60 * 600)
        {
            Console.WriteLine("run ended at the tick limit, not on a death");
        }

        return 0;
    }

    private static int RunSelfTest(Options options)
    {
        var failures = 0;
        void Check(string name, bool condition)
        {
            Console.WriteLine($"{(condition ? "ok  " : "FAIL")} {name}");
            if (!condition)
            {
                failures++;
            }
        }

        Check("mount at the first cell", Sim.MountAt(new Vector2(16, 16)) == 0);
        Check("mount at the last cell", Sim.MountAt(new Vector2(1279, 700)) ==
            (TurretStats.MountCount - 1));
        Check("point outside the field has no mount", Sim.MountAt(new Vector2(-4, 40)) == -1);

        var sim = new Sim(options.Seed);
        Check("starting gold", sim.Gold == 520);
        Check("pool centre is not buildable", !sim.IsBuildable(Sim.MountAt(new Vector2(118f, 92f))));
        Check("core footing is not buildable", !sim.IsBuildable(Sim.MountAt(Terrain.CoreCenter)));
        Check("open dirt away from a cliff is not buildable",
            !sim.IsBuildable(Sim.MountAt(new Vector2(700f, 646f))));

        var mount = NearestBuildable(sim);
        Check("a cliff shelf is buildable", mount >= 0);
        Check("places a gun", sim.TryPlace(mount, TurretKind.Gun));
        Check("gun paid for", sim.Gold == 520 - TurretStats.Cost(TurretKind.Gun));
        Check("mount is occupied", sim.IsOccupied(mount));
        Check("cannot build twice on a mount", !sim.TryPlace(mount, TurretKind.Gun));
        Check("upgrades within budget", sim.TryUpgrade(mount));
        Check("upgrade charged", sim.Gold < 490);
        var second = NextBuildable(sim, mount);
        Check("another shelf mount exists", second >= 0);
        Check("laser fits the remaining gold", second < 0 || sim.TryPlace(second, TurretKind.Laser));

        // A gun on the corridor wall must actually kill orcs flowing through the gate.
        var shooter = new Sim(options.Seed);
        shooter.TryPlace(mount, TurretKind.Gun);
        var goldBefore = shooter.Gold;
        var kills = 0;
        for (var tick = 0; tick < 60 * 60 && kills == 0; tick++)
        {
            shooter.Tick(TickSeconds);
            kills = shooter.Kills;
        }

        Check("turret kills orcs", kills > 0);
        Check("kills paid bounty", shooter.Gold - goldBefore >= kills);

        // With nothing shooting at them the horde has to walk the flow field into the core.
        var marcher = new Sim(options.Seed);
        var reached = 0;
        for (var tick = 0; tick < 60 * 90 && reached == 0; tick++)
        {
            marcher.Tick(TickSeconds);
            reached = marcher.Leaks;
        }

        Check("horde reaches the core", reached > 0);
        Console.WriteLine($"debug marcher deepest {DeepestOrcInRock(marcher):F1} count {marcher.Swarm.Count}");

        // Nothing may end up standing inside the rock, however crowded the gates get. The bot is
        // buying turrets here so the run is the crowded one the player actually sees.
        var wedged = new Sim(options.Seed);
        var wedgedBrain = new Autopilot(options.Seed);
        var deepest = 0f;
        for (var tick = 0; tick < 60 * 200; tick++)
        {
            wedgedBrain.Think(wedged);
            wedged.Tick(TickSeconds);
            if (tick % 6 == 0)
            {
                deepest = MathF.Min(deepest, DeepestOrcInRock(wedged));
            }
        }

        Check($"no orc sinks into the rock (deepest {deepest:F2}px)", deepest > -1.5f);

        // Nor may the crowd pile sprites on top of each other: the separation has to hold while the
        // wave is at its thickest.
        var crowded = new Sim(options.Seed);
        var crowdedBrain = new Autopilot(options.Seed);
        var piles = 0;
        for (var tick = 0; tick < 60 * 200; tick++)
        {
            crowdedBrain.Think(crowded);
            crowded.Tick(TickSeconds);
            if (tick % 60 == 0)
            {
                piles = Math.Max(piles, PiledPairs(crowded));
            }
        }

        Check($"orcs core their spacing (at most {piles} piled pairs)", piles <= 6);
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// How many pairs of living orcs overlap by more than half a sprite, found through a coarse grid
    /// so the check stays linear in the number of orcs.
    /// </summary>
    private static int PiledPairs(Sim sim)
    {
        const int Cell = 32;
        var columns = (Sim.Width / Cell) + 1;
        var rows = (Sim.Height / Cell) + 1;
        var buckets = new List<int>[columns * rows];
        for (var index = 0; index < Sim.MaxMolecules; index++)
        {
            if (!sim.Swarm.IsAlive(index) || !sim.Swarm.IsOnField(index))
            {
                continue;
            }

            var position = sim.Swarm.PositionOf(index);
            var cell = (((int)(position.Y / Cell)) * columns) + (int)(position.X / Cell);
            (buckets[cell] ??= []).Add(index);
        }

        var piled = 0;
        for (var cell = 0; cell < buckets.Length; cell++)
        {
            var bucket = buckets[cell];
            if (bucket is null)
            {
                continue;
            }

            var column = cell % columns;
            var row = cell / columns;
            for (var dy = 0; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    var nextColumn = column + dx;
                    var nextRow = row + dy;
                    if (nextColumn < 0 || nextRow >= rows || nextColumn >= columns)
                    {
                        continue;
                    }

                    var other = buckets[(nextRow * columns) + nextColumn];
                    if (other is null)
                    {
                        continue;
                    }

                    foreach (var left in bucket)
                    {
                        var leftPosition = sim.Swarm.PositionOf(left);
                        var leftRadius = Swarm.Radius(sim.Swarm.KindOf(left));
                        foreach (var right in other)
                        {
                            if (right <= left)
                            {
                                continue;
                            }

                            var wanted = leftRadius + Swarm.Radius(sim.Swarm.KindOf(right));
                            var distance = Vector2.Distance(leftPosition, sim.Swarm.PositionOf(right));
                            if (wanted - distance > wanted * 0.5f)
                            {
                                piled++;
                            }
                        }
                    }
                }
            }
        }

        return piled;
    }

    /// <summary>How far the most embedded sprite reaches into the rock, in pixels (negative is overlap).</summary>
    private static float DeepestOrcInRock(Sim sim)
    {
        var deepest = 0f;
        for (var index = 0; index < Sim.MaxMolecules; index++)
        {
            if (!sim.Swarm.IsAlive(index) || !sim.Swarm.IsOnField(index))
            {
                continue;
            }

            var position = sim.Swarm.PositionOf(index);
            var clearance = sim.Terrain.Distance(position.X, position.Y) - Swarm.Radius(sim.Swarm.KindOf(index));
            deepest = MathF.Min(deepest, clearance);
        }

        return deepest;
    }

    /// <summary>The buildable shelf mount closest to the core, where the gates are.</summary>
    private static int NearestBuildable(Sim sim)
    {
        var best = -1;
        var bestDistance = float.MaxValue;
        for (var mount = 0; mount < TurretStats.MountCount; mount++)
        {
            if (!sim.IsBuildable(mount) || sim.IsOccupied(mount))
            {
                continue;
            }

            var distance = Vector2.Distance(TurretStats.MountCenter(mount), Terrain.CoreCenter);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = mount;
            }
        }

        return best;
    }

    private static int NextBuildable(Sim sim, int skip)
    {
        for (var mount = 0; mount < TurretStats.MountCount; mount++)
        {
            if (mount != skip && sim.IsBuildable(mount) && !sim.IsOccupied(mount))
            {
                return mount;
            }
        }

        return -1;
    }

    private static int RunScreenshot(Options options)
    {
        var sim = new Sim(options.Seed);
        var brain = new Autopilot(options.Seed);
        using var renderer = Renderer.Open(Title, hidden: true);
        Console.WriteLine($"gl {renderer.Backend}");
        for (var tick = 0; tick < options.Ticks && !sim.GameOver; tick++)
        {
            brain.Think(sim);
            sim.Tick(TickSeconds);
        }

        renderer.RenderOnly(sim);
        var path = options.Screenshot!;
        renderer.WritePpm(path);
        Console.WriteLine(
            $"wrote {path} at wave {sim.Wave}, {sim.AliveCount} orcs, core {sim.CoreHp}, " +
            $"{sim.Turrets.Count} turrets, {sim.Effects.BloodCount} blood, {sim.Effects.ScorchCount} scorch");
        return 0;
    }

    private static int RunWindow(Options options)
    {
        var sim = new Sim(options.Seed);
        var brain = new Autopilot(options.Seed + 7);
        using var renderer = Renderer.Open(Title, hidden: false);
        var input = new InputState();
        Console.WriteLine($"gl {renderer.Backend}");

        var selected = 0;
        var paused = false;
        var camera = Camera.Default;
        var clock = Stopwatch.StartNew();
        var previous = 0d;
        var accumulator = 0d;
        var frames = 0;
        var frameClock = Stopwatch.StartNew();
        var lastTitle = 0d;

        while (!input.Quit)
        {
            input.BeginFrame();
            renderer.Pump(input);

            var now = clock.Elapsed.TotalSeconds;
            var delta = Math.Min(0.1, now - previous);
            previous = now;

            // The wheel and the plus/minus keys zoom; the arrows or WASD pan the view over the field.
            if (input.Wheel != 0)
            {
                camera = camera.ZoomedBy(MathF.Pow(1.18f, input.Wheel));
            }

            if (input.Pressed(InputState.ScancodeEquals) || input.Pressed(InputState.ScancodeKpPlus))
            {
                camera = camera.ZoomedBy(1.18f);
            }

            if (input.Pressed(InputState.ScancodeMinus) || input.Pressed(InputState.ScancodeKpMinus))
            {
                camera = camera.ZoomedBy(1f / 1.18f);
            }

            var pan = Vector2.Zero;
            if (input.IsDown(InputState.ScancodeLeft) || input.IsDown(InputState.ScancodeA))
            {
                pan.X -= 1f;
            }

            if (input.IsDown(InputState.ScancodeRight) || input.IsDown(InputState.ScancodeD))
            {
                pan.X += 1f;
            }

            if (input.IsDown(InputState.ScancodeUp) || input.IsDown(InputState.ScancodeW))
            {
                pan.Y -= 1f;
            }

            if (input.IsDown(InputState.ScancodeDown) || input.IsDown(InputState.ScancodeS))
            {
                pan.Y += 1f;
            }

            if (pan != Vector2.Zero)
            {
                camera = camera.PannedBy(pan * (760f * (float)delta / camera.Zoom));
            }

            var worldMouse = camera.ToWorld(input.Mouse);

            if (!sim.GameOver)
            {
                if (input.Pressed(InputState.ScancodePause))
                {
                    paused = !paused;
                }

                if (input.Pressed(InputState.ScancodeSpace))
                {
                    sim.StartWaveNow();
                }

                for (var kind = 0; kind < 3; kind++)
                {
                    if (input.Pressed(InputState.ScancodeDigit1 + kind))
                    {
                        selected = kind;
                    }
                }

                foreach (var click in input.Clicks)
                {
                    if (Hud.SlotAt(click) is var slot and >= 0)
                    {
                        selected = slot;
                        continue;
                    }

                    var mount = Sim.MountAt(camera.ToWorld(click));
                    if (mount < 0)
                    {
                        continue;
                    }

                    if (sim.IsOccupied(mount))
                    {
                        sim.TryUpgrade(mount);
                    }
                    else
                    {
                        sim.TryPlace(mount, (TurretKind)selected);
                    }
                }

                if (!paused && options.AutoPlay)
                {
                    brain.Think(sim);
                }

                if (!paused)
                {
                    accumulator += delta * options.Speed;
                    var steps = 0;
                    while (accumulator >= TickSeconds && steps < 16)
                    {
                        sim.Tick(TickSeconds);
                        accumulator -= TickSeconds;
                        steps++;
                    }
                }
            }
            else if (input.Pressed(InputState.ScancodeRestart))
            {
                sim = new Sim(options.Seed + 1);
                accumulator = 0d;
            }

            renderer.Frame(sim, selected, Sim.MountAt(worldMouse), paused, sim.GameOver, camera, worldMouse);
            renderer.Present();
            frames++;

            if (frameClock.Elapsed.TotalSeconds - lastTitle >= 0.5)
            {
                lastTitle = frameClock.Elapsed.TotalSeconds;
                var fps = frames / lastTitle;
                renderer.SetTitle(
                    $"Molecule Swarm · wave {sim.Wave} · {sim.AliveCount:N0} molecules · " +
                    $"core {sim.CoreHp} · energy {sim.Gold} · {camera.Zoom:F2}x · {fps:F0} fps");
                Console.WriteLine(
                    $"fps {fps:F0} · frame {1000 / Math.Max(1, fps):F1} ms · wave {sim.Wave} · " +
                    $"orcs {sim.AliveCount:N0} · turrets {sim.Turrets.Count} · core {sim.CoreHp} · gold {sim.Gold}");
            }
        }

        return 0;
    }
}
