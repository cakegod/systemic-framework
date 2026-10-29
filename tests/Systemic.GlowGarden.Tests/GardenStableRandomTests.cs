using GlowGarden;
using Xunit;

namespace Systemic.GlowGarden.Tests;

public sealed class GardenStableRandomTests
{
    [Fact]
    public void Scouting_draws_are_independent_of_population_row_order()
    {
        using var first = GardenHost.Create(42);
        using var second = GardenHost.Create(42);
        Arrange(first.Simulation, targetRow: 0);
        Arrange(second.Simulation, targetRow: 1);

        first.Step(30);
        second.Step(30);

        Assert.Equal(first.Simulation.Creatures.Single(c => c.Id == 7),
            second.Simulation.Creatures.Single(c => c.Id == 7));
    }

    [Fact]
    public void Scouting_draws_include_the_world_seed()
    {
        using var first = GardenHost.Create(42);
        using var second = GardenHost.Create(43);
        Arrange(first.Simulation, targetRow: 0);
        Arrange(second.Simulation, targetRow: 0);

        first.Step(20);
        second.Step(20);

        Assert.NotEqual(first.Simulation.Creatures.Single(c => c.Id == 7).Heading,
            second.Simulation.Creatures.Single(c => c.Id == 7).Heading);
    }

    private static void Arrange(GardenSimulation simulation, int targetRow)
    {
        simulation._plants.Clear();
        simulation._motes.Clear();
        simulation._creatures.Clear();
        var target = new CreatureState(7, CreatureKind.Burstling, GardenSimulation.Colony, 0, 0, false);
        var idle = new CreatureState(99, CreatureKind.Burstling, GardenSimulation.Colony, 0, 1000, false);
        if (targetRow == 0)
        {
            simulation._creatures.Add(target);
            simulation._creatures.Add(idle);
        }
        else
        {
            simulation._creatures.Add(idle);
            simulation._creatures.Add(target);
        }
    }
}
