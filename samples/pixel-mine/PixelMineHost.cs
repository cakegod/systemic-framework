using Systemic.Content;
using Systemic.Host;

namespace PixelMine;

public sealed class PixelMineHost : IDisposable
{
    private PixelMineHost(PixelMineSimulation simulation, SystemicGame game)
    {
        Simulation = simulation;
        Game = game;
    }

    public PixelMineSimulation Simulation { get; }

    public SystemicGame Game { get; }

    public static PixelMineHost Create(int seed) => Create(ContentDirectory, seed);

    public static PixelMineHost Create(string contentDirectory, int seed) => Create(contentDirectory, seed, 0, 1);

    public static PixelMineHost CreateStressTest(int seed, int ballCount, int speedMultiplier) =>
        Create(ContentDirectory, seed, ballCount, speedMultiplier);

    private static PixelMineHost Create(string contentDirectory, int seed, int stressBallCount, int speedMultiplier)
    {
        var content = LoadContent(contentDirectory);
        var materialized = content.CreateWorld(seed, 1);
        var simulation = new PixelMineSimulation(
            materialized.World,
            materialized.EntityIds["pixel.mine"],
            materialized.EntityIds["pixel.dust"],
            materialized.EntityIds["pixel.prism"],
            seed,
            stressBallCount,
            speedMultiplier);
        var game = new SystemicGameBuilder(content, materialized)
            .AddService(simulation)
            .AddSystem(simulation)
            .Build();
        return new PixelMineHost(simulation, game);
    }

    internal static string ContentDirectory => Path.Combine(AppContext.BaseDirectory, "content");

    internal static ContentPack LoadContent(string contentDirectory) => ContentPackLoader.Load(contentDirectory);

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
