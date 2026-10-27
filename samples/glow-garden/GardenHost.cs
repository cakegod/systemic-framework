using Systemic.Content;
using Systemic.Host;
using Systemic.Presentation;

namespace GlowGarden;

public sealed class GardenHost : IDisposable
{
    private GardenHost(GardenSimulation simulation, SystemicGame game)
    {
        Simulation = simulation;
        Game = game;
    }

    public GardenSimulation Simulation { get; }

    public SystemicGame Game { get; }

    public static GardenHost Create(int seed) => Create(ContentDirectory, seed);

    public static GardenHost Create(string contentDirectory, int seed)
    {
        var builder = CreateBuilder(contentDirectory, seed);
        var simulation = new GardenSimulation(builder.World);

        return new GardenHost(simulation,
            builder.AddScheduledSystem(simulation).AddFactObserver(0, simulation.ObserveFact).Build());
    }

    /// <summary>Loads Core plus the Presentation module; the sample hosts its own rendering and UI.</summary>
    internal static SystemicGameBuilder CreateBuilder(string contentDirectory, int seed)
    {
        var content = ContentPackLoader.Load(contentDirectory,
            new ContentModuleRegistry().Add(new PresentationContentModule()));
        return new SystemicGameBuilder(content, content.CreateWorld(seed, 1));
    }

    internal static string ContentDirectory => Path.Combine(AppContext.BaseDirectory, "content");

    public void Step(int ticks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ticks);
        for (var i = 0; i < ticks; i++) Game.Step();
    }

    public void Dispose() => Game.Dispose();
}
