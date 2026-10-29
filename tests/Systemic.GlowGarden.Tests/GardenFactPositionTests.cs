using System.Numerics;
using GlowGarden;
using Systemic.Rules;
using Xunit;

namespace Systemic.GlowGarden.Tests;

public sealed class GardenFactPositionTests
{
    [Fact]
    public void Impact_facts_use_explicit_rounded_position_while_reactions_keep_exact_position()
    {
        var builder = GardenHost.CreateBuilder(GardenHost.ContentDirectory, 17);
        var model = new GardenSimulation(builder.World);
        var facts = new List<SimulationFact>();
        using var game = builder.AddSystem(model).AddFactObserver(0, model.ObserveFact)
            .AddFactObserver(100, (fact, _) => facts.Add(fact)).Build();
        var point = new Vector2(561.5f, 280.5f);
        model._plants.Clear();
        model._plants.Add(new(1, point, 1));
        model._creatures.Clear();
        model._creatures.Add(new(0, CreatureKind.Burstling, point, 0, 0, false));
        model.ToggleTalent(Talent.Echo);

        game.Step();

        var garden = game.World.Entities.Single(entity => entity.HasTag("garden"));
        var burst = Assert.Single(facts.Where(fact => fact.Type == "garden.pod.burst"));
        var echo = Assert.Single(facts.Where(fact => fact.Type == "garden.talent.echo"));
        Assert.Equal(garden.Id, burst.TargetId);
        Assert.Equal(new SimulationFactPosition(562, 280), burst.Position);
        Assert.Equal(burst.Position, echo.Position);
        Assert.Equal(point, Assert.Single(model.PendingImpacts).Position);
    }
}
