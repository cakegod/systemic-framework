using Systemic.Content;
using Systemic.Host;
using Systemic.Presentation;
using Systemic.Save;

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
        var simulation = new GardenSimulation(builder.World, GardenTuning.From(builder.Content.Tuning("garden")));
        return new GardenHost(simulation,
            builder.AddSystem(simulation).AddFactObserver(0, simulation.ObserveFact).Build());
    }

    public static GardenHost Load(string json) => Load(ContentDirectory, json);

    public static GardenHost Load(string contentDirectory, string json)
    {
        var content = LoadContent(contentDirectory);
        return Restore(content, new SystemicSaveCodec().Load(content, json));
    }

    public static GardenHost LoadFromFile(string path)
    {
        var content = LoadContent(ContentDirectory);
        return Restore(content, new SystemicSaveCodec().LoadFromFile(content, path));
    }

    public void SaveToFile(string path) => new SystemicSaveCodec().SaveToFile(Game, path);

    private static GardenHost Restore(ContentPack content, MaterializedContentWorld materialized)
    {
        var builder = new SystemicGameBuilder(content, materialized);
        var simulation = new GardenSimulation(builder.World, GardenTuning.From(content.Tuning("garden")), restoring: true);
        return new GardenHost(simulation,
            builder.AddSystem(simulation).AddFactObserver(0, simulation.ObserveFact).Build());
    }

    /// <summary>Loads Core plus the Presentation module; the sample hosts its own rendering and UI.</summary>
    internal static SystemicGameBuilder CreateBuilder(string contentDirectory, int seed)
    {
        var content = LoadContent(contentDirectory);
        return new SystemicGameBuilder(content, content.CreateWorld(seed, 1));
    }

    private static ContentPack LoadContent(string contentDirectory) => ContentPackLoader.Load(contentDirectory,
        new ContentModuleRegistry()
            .Add(new PresentationContentModule())
            .AddTuningSchema("garden", GardenTuning.Schema));

    internal static string ContentDirectory => Path.Combine(AppContext.BaseDirectory, "content");

    public void Step(int ticks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ticks);
        for (var i = 0; i < ticks; i++)
        {
            Game.Step();
        }
    }

    public void Dispose() => Game.Dispose();
}
