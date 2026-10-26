using GlowGarden;
using System.Numerics;
using System.Text.Json.Nodes;
using Systemic.Host;
using Systemic.Rules;
using Xunit;

namespace Systemic.GlowGarden.Tests;

public sealed class FrameworkIntegrationTests
{
    [Fact]
    public void HostWorldOwnsAllLivePopulations()
    {
        using var host = GardenHost.Create(42);
        Assert.True(host.Game.World.HasPopulations);
        var populations = host.Game.World.CapturePopulationState().Populations;
        Assert.Equal(3, populations.Length);
    }

    [Fact]
    public void WorldDrawsDriveInitializationAndProductionDeterministically()
    {
        using var a = GardenHost.Create(42);
        using var b = GardenHost.Create(42);
        Assert.Equal(6, a.Game.World.Draws.Draws);
        a.Step(300);
        b.Step(300);
        Assert.True(a.Game.World.Draws.Draws > 6);
        Assert.Equal(a.Game.World.Draws.CaptureState(), b.Game.World.Draws.CaptureState());
        Assert.Equal(a.Simulation.Motes.ToArray(), b.Simulation.Motes.ToArray());
    }

    [Fact]
    public void PopulationStructuralChangesRollBackWithKernelTransaction()
    {
        using var host = GardenHost.Create(42);
        var before = host.Simulation.Creatures.ToArray();
        using (var transaction = host.Game.World.BeginTransaction())
        {
            Assert.True(host.Simulation.Hire(CreatureKind.Burstling));
            Assert.Equal(before.Length + 1, host.Simulation.Creatures.Count);
            transaction.Rollback();
        }
        // Progression/scratch rollback is deliberately outside this population-only contract.
        Assert.Equal(before, host.Simulation.Creatures.ToArray());
    }

    [Fact]
    public void ActiveTalentTagsSelectAnAuthoredReaction()
    {
        var builder = SystemicGame.FromDirectory(Path.Combine(AppContext.BaseDirectory, "content"), 42);
        var model = new GardenSimulation(builder.World);
        var facts = new List<SimulationFact>();
        using var game = builder.AddSystem(model)
            .AddFactObserver(0, model.ObserveFact)
            .AddFactObserver(100, (fact, _) => facts.Add(fact)).Build();
        model.ToggleTalent(Talent.Seedstorm);
        model._plants[0] = model._plants[0] with { Position = GardenSimulation.Colony, Growth = 1 };
        game.Step();
        var actualBurst = facts.First(fact => fact.Type == "garden.pod.burst");
        var trace = game.Trace(actualBurst);
        Assert.Contains(trace.Facts, fact => fact.MatchedRules.Contains("garden.talent.seedstorm"));
        Assert.Contains(trace.Facts, fact => fact.Fact.Type == "garden.talent.seedstorm");
        Assert.Contains("garden.talent.seedstorm", builder.World.Entities.Single().Tags);
        Assert.Contains(model.PendingImpacts, impact => impact.IsSeed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RemovingAuthoredSeedstormRuleSuppressesActualNewPlants(bool removeRule)
    {
        var directory = Path.Combine(Path.GetTempPath(), "garden-rules-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "content")))
                File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
            var path = Path.Combine(directory, "rules.json");
            var document = JsonNode.Parse(File.ReadAllText(path))!;
            var rules = document["rules"]!.AsArray();
            var removed = rules.FirstOrDefault(rule => rule!["id"]!.GetValue<string>() == "garden.talent.seedstorm");
            Assert.NotNull(removed);
            if (removeRule) rules.Remove(removed);
            File.WriteAllText(path, document.ToJsonString());

            using var normal = GardenHost.Create(31);
            var builder = SystemicGame.FromDirectory(directory, 31);
            var changed = new GardenSimulation(builder.World);
            using var game = builder.AddSystem(changed).AddFactObserver(0, changed.ObserveFact).Build();
            foreach (var model in new[] { normal.Simulation, changed })
            {
                model._plants.Clear();
                model._plants.Add(new(100, GardenSimulation.Colony, 1));
                model._creatures.Clear();
                model._creatures.Add(new(10, CreatureKind.Burstling, GardenSimulation.Colony, 0, 0, false));
                model.ToggleTalent(Talent.Seedstorm);
            }
            normal.Step(20);
            for (var i = 0; i < 20; i++) game.Step();
            Assert.True(normal.Simulation.Plants.Count > 1);
            if (removeRule)
            {
                Assert.Single(changed.Plants);
                Assert.Equal(0, changed.EffectCount(Talent.Seedstorm));
            }
            else
            {
                Assert.Equal(normal.Simulation.Plants.ToArray(), changed.Plants.ToArray());
                Assert.Equal(normal.Simulation.EffectCount(Talent.Seedstorm), changed.EffectCount(Talent.Seedstorm));
            }
            Assert.True(changed.BurstCount > 0);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void TypedColumnEditsAreLiveAndRollBackIncludingSwapBack()
    {
        using var host = GardenHost.Create(12);
        var model = host.Simulation;
        var before = model.Plants.ToArray();
        using (var transaction = host.Game.World.BeginTransaction())
        {
            var columns = GardenPlantPopulation.Bind(host.Game.World.GetPopulation<GardenPlant>());
            columns.Growth.EditRange(0, 1)[0] = 0.75f;
            Assert.Equal(0.75f, model.Plants[0].Growth);
            model._plants.RemoveAt(1);
            Assert.Equal(before[^1].Id, model.Plants[1].Id);
            transaction.Rollback();
        }
        Assert.Equal(before, model.Plants.ToArray());
    }

    [Fact]
    public void ProductionPublishesRealBurstCollectionAndDepositAmounts()
    {
        var builder = SystemicGame.FromDirectory(Path.Combine(AppContext.BaseDirectory, "content"), 41);
        var model = new GardenSimulation(builder.World);
        var facts = new List<SimulationFact>();
        using var game = builder.AddSystem(model).AddFactObserver(100, (fact, _) => facts.Add(fact)).Build();
        for (var i = 0; i < 800; i++) game.Step();
        Assert.Contains(facts, fact => fact.Type == "garden.pod.burst");
        Assert.Contains(facts, fact => fact.Type == "garden.glow.collected" && fact.Amount > 0);
        Assert.Equal(model.BankedGlow, facts.Where(fact => fact.Type == "garden.glow.deposited").Sum(fact => fact.Amount));
        Assert.True(model.BankedGlow > 0);
    }

    [Fact]
    public void CopyMembershipIsAuthoritativeInWorldPopulation()
    {
        using var host = GardenHost.Create(61);
        var model = host.Simulation;
        model._plants.Clear();
        model._plants.Add(new(100, GardenSimulation.Colony, 1));
        model._plants.Add(new(101, GardenSimulation.Colony + new Vector2(20, 0), 1));
        model._creatures.Clear();
        model._creatures.Add(new(0, CreatureKind.Burstling, GardenSimulation.Colony, 0, 0, false));
        model.ToggleTalent(Talent.LivingProjectile);
        model.ToggleTalent(Talent.Clone);
        host.Step(1);
        Assert.Equal(1, model.CopyCount);
        var population = host.Game.World.GetPopulation<GardenCreature>();
        var copyRow = Array.FindIndex(model.Creatures.ToArray(), creature => creature.IsCopy);
        population.RemoveSwapBack(copyRow);
        Assert.Equal(0, model.CopyCount);
    }
}
