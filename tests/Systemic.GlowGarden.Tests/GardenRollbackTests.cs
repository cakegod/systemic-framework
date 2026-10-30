using GlowGarden;
using Systemic.Host;
using Systemic.Kernel;
using Systemic.Save;
using Xunit;

namespace Systemic.GlowGarden.Tests;

public sealed class GardenRollbackTests
{
    [Fact]
    public void SaveLoadRestoresProgressionClockPopulationsAndFutureSimulation()
    {
        using var original = GardenHost.Create(42);
        Assert.True(original.Simulation.ToggleTalent(Talent.Seedstorm));
        Assert.True(original.Simulation.Buy(UpgradeKind.GathererSpeed));
        original.Step(90);
        original.Game.Advance(TimeSpan.FromMilliseconds(5));
        var json = new SystemicSaveCodec().Save(original.Game);

        using var restored = GardenHost.Load(json);

        Assert.Equal(original.Game.Clock.CaptureState(), restored.Game.Clock.CaptureState());
        Assert.Equal(original.Game.World.Draws.CaptureState(), restored.Game.World.Draws.CaptureState());
        Assert.Equal(original.Simulation.CaptureState().GetRawText(), restored.Simulation.CaptureState().GetRawText());
        Assert.Equal(original.Simulation.Plants, restored.Simulation.Plants);
        Assert.Equal(original.Simulation.Creatures, restored.Simulation.Creatures);
        Assert.Equal(original.Simulation.Motes, restored.Simulation.Motes);

        original.Step(60);
        restored.Step(60);
        Assert.Equal(original.Simulation.CaptureState().GetRawText(), restored.Simulation.CaptureState().GetRawText());
        Assert.Equal(original.Simulation.Plants, restored.Simulation.Plants);
        Assert.Equal(original.Simulation.Creatures, restored.Simulation.Creatures);
        Assert.Equal(original.Simulation.Motes, restored.Simulation.Motes);
    }

    [Fact]
    public void FailedTickRestoresGardenOwnedStateAsWellAsTheWorld()
    {
        var builder = GardenHost.CreateBuilder(GardenHost.ContentDirectory, 42);
        var simulation = new GardenSimulation(builder.World);
        simulation._plants.Clear();
        simulation._plants.Add(new PlantState(0, GardenSimulation.Colony, 1));
        simulation._creatures.Clear();
        simulation._creatures.Add(new CreatureState(0, CreatureKind.Burstling, GardenSimulation.Colony, 0, 0, false));
        Assert.True(simulation.ToggleTalent(Talent.Seedstorm));
        using var game = builder.AddSystem(simulation)
            .AddSystem(new FailingSystem())
            .AddFactObserver(0, simulation.ObserveFact)
            .Build();
        var before = simulation.CaptureState().GetRawText();
        var worldTick = game.World.LogicalTick;
        var plants = simulation.Plants.ToArray();
        var creatures = simulation.Creatures.ToArray();

        Assert.ThrowsAny<Exception>(game.Step);

        Assert.Equal(worldTick, game.World.LogicalTick);
        Assert.Equal(plants, simulation.Plants);
        Assert.Equal(creatures, simulation.Creatures);
        Assert.Equal(before, simulation.CaptureState().GetRawText());
    }

    private sealed class FailingSystem : ISystemicSystem
    {
        public string Id => "z-failing-system";
        public int Order => 10;
        public IReadOnlyList<SystemAccess> Access => [SystemAccess.WriteWorld];
        public void Step(SystemicSystemContext gameContext) =>
            throw new InvalidOperationException("injected failure after garden step");
    }
}
