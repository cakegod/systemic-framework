using System.Numerics;
using System.Reflection;
using GlowGarden;
using Xunit;

namespace Systemic.GlowGarden.Tests;

public sealed class GardenPlacementSafetyTests
{
    [Fact]
    public void Seedstorm_falls_back_to_a_free_cell_after_random_candidates_fail()
    {
        using var host = GardenHost.Create(42);
        var simulation = host.Simulation;
        RestrictWalkableCells(simulation, includeDistantCell: true);
        ArrangeSingleBurstling(simulation);
        Assert.True(simulation.ToggleTalent(Talent.Seedstorm));

        host.Step(1);

        var seed = Assert.Single(simulation.PendingImpacts.Where(impact => impact.IsSeed));
        Assert.False(simulation.Obstacles.IsBlocked(seed.Position));
        Assert.True(Vector2.Distance(seed.Position, GardenSimulation.Colony) >= 50);
    }

    [Fact]
    public void Seedstorm_rejects_a_seed_when_no_eligible_free_cell_exists()
    {
        using var host = GardenHost.Create(42);
        var simulation = host.Simulation;
        RestrictWalkableCells(simulation, includeDistantCell: false);
        ArrangeSingleBurstling(simulation);
        Assert.True(simulation.ToggleTalent(Talent.Seedstorm));

        host.Step(1);

        Assert.DoesNotContain(simulation.PendingImpacts, impact => impact.IsSeed);
        Assert.Equal(1, simulation.RejectedSeedSpawns);
    }

    private static void ArrangeSingleBurstling(GardenSimulation simulation)
    {
        simulation._plants.Clear();
        simulation._plants.Add(new PlantState(0, GardenSimulation.Colony, 1));
        simulation._creatures.Clear();
        simulation._creatures.Add(new CreatureState(0, CreatureKind.Burstling, GardenSimulation.Colony, 0, 0, false));
    }

    private static void RestrictWalkableCells(GardenSimulation simulation, bool includeDistantCell)
    {
        var field = (float[])typeof(GardenObstacles)
            .GetField("_field", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(simulation.Obstacles)!;
        var columns = (int)(GardenSimulation.Width / 8);
        Array.Fill(field, 1f);
        var colony = GardenSimulation.Colony;
        field[((int)colony.Y / 8 * columns) + ((int)colony.X / 8)] = 0;
        if (includeDistantCell)
        {
            field[((int)colony.Y / 8 * columns) + ((int)colony.X / 8) + 12] = 0;
        }
    }
}
