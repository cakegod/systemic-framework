using Systemic.Host;

namespace GlowGarden;


public sealed class GardenHost : IDisposable
{
    private GardenHost(GardenSimulation simulation, SystemicGame game)
    {
        Simulation = simulation;
        Game = game;
    }

    public GardenSimulation Simulation
    { get; }

    public SystemicGame Game
    { get; }

    public static GardenHost Create(int seed)
    {
        var simulation = new GardenSimulation(seed);

        var builder = SystemicGame.FromDirectory(Path.Combine(AppContext.BaseDirectory, "content"), seed);

        return new GardenHost(simulation, builder.AddSystem(simulation).Build());
    }

    public void Step(int ticks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ticks);
        for (var i = 0; i < ticks; i++) Game.Step();
    }

    public void Dispose() => Game.Dispose();
}

