namespace GlowGarden;

public sealed record GardenOptions(bool Headless, bool VerifyRender, int Ticks, int Seed, string? Screenshot)
{
    public static GardenOptions Parse(string[] args)
    {
        bool headless = false, render = false, hasTicks = false;
        int ticks = 3600, seed = 42;
        string? screenshot = null;
        var seen = new HashSet<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var option = args[i];
            if (!seen.Add(option)) throw new ArgumentException($"Duplicate option {option}.");
            if (option == "--headless") headless = true;
            else if (option == "--verify-render") render = true;
            else if (option is "--ticks" or "--seed" or "--screenshot")
            {
                if (++i >= args.Length) throw new ArgumentException($"Missing value for {option}.");
                if (option == "--screenshot") screenshot = args[i];
                else
                {
                    if (!int.TryParse(args[i], out var value)) throw new ArgumentException($"Invalid integer for {option}.");
                    if (option == "--ticks") { ticks = value; hasTicks = true; }
                    else seed = value;
                }
            }
            else throw new ArgumentException($"Unknown option {option}.");
        }
        if (ticks is < 1 or > 100000) throw new ArgumentException("Ticks must be 1..100000.");
        if (headless && render) throw new ArgumentException("Choose headless or render verification.");
        if (hasTicks && !headless) throw new ArgumentException("--ticks requires --headless.");
        if (screenshot != null && !render) throw new ArgumentException("--screenshot requires --verify-render.");
        return new(headless, render, ticks, seed, screenshot);
    }
}

/// <summary>Restart replaces the entire actual host, including its clock, RNG and pending work.</summary>
public sealed class GardenSession(int seed) : IDisposable
{
    public GardenHost Host { get; private set; } = GardenHost.Create(seed);
    public void SaveToFile(string path) => Host.SaveToFile(path);
    public void LoadFromFile(string path)
    {
        var restored = GardenHost.LoadFromFile(path);
        var previous = Host;
        Host = restored;
        previous.Dispose();
    }
    public void Restart()
    {
        var fresh = GardenHost.Create(seed);
        Host.Dispose();
        Host = fresh;
    }
    public void Dispose() => Host.Dispose();
}

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            var options = GardenOptions.Parse(args);
            if (options.Headless) return RunHeadless(options);
            using var game = new GardenGame(options);
            game.Run();
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            Console.Error.WriteLine("Usage: GlowGarden [--seed N] [--headless --ticks 1..100000 | --verify-render [--screenshot path.png]]");
            return 1;
        }
    }

    private static int RunHeadless(GardenOptions options)
    {
        using var session = new GardenSession(options.Seed);
        var model = session.Host.Simulation;
        model.ToggleTalent(Talent.Seedstorm);
        model.ToggleTalent(Talent.Magnetism);
        model.ToggleTalent(Talent.Echo);
        for (var tick = 0; tick < options.Ticks; tick++)
        {
            // Purchases use only starting Glow and the actual returned harvest.
            if (tick % 120 == 0)
            {
                model.Hire(tick % 240 == 0 ? CreatureKind.Gatherer : CreatureKind.Burstling);
                model.Buy(UpgradeKind.PodGrowth);
            }
            session.Host.Step(1);
        }
        Console.WriteLine($"seed={options.Seed} ticks={model.Tick} bursts={model.BurstCount} banked={model.BankedGlow} balance={model.Glow} loose={model.Motes.Sum(m => m.Value)} carried={model.Creatures.Sum(c => c.CarriedGlow)} plants={model.Plants.Count} workers={model.Creatures.Count}");
        if (model.BurstCount == 0 || model.BankedGlow == 0)
            throw new InvalidOperationException("Smoke workflow did not produce and bank Glow; use enough ticks (e.g. 3600).");
        return 0;
    }
}
