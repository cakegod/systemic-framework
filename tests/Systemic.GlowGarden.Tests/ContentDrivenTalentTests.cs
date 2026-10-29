using System.Numerics;
using System.Text.Json.Nodes;
using GlowGarden;
using Systemic.Presentation;
using Xunit;

namespace Systemic.GlowGarden.Tests;

public sealed class ContentDrivenTalentTests
{
    private static string CopyContent()
    {
        var directory = Path.Combine(Path.GetTempPath(), "garden-tuning-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        foreach (var file in Directory.GetFiles(GardenHost.ContentDirectory))
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        return directory;
    }

    private static void SetProperty(string directory, string property, double value)
    {
        var path = Path.Combine(directory, "entities.json");
        var document = JsonNode.Parse(File.ReadAllText(path))!;
        var garden = document["entities"]!.AsArray()
            .First(entity => entity!["id"]!.GetValue<string>() == "garden")!;
        garden["properties"]![property] = value;
        File.WriteAllText(path, document.ToJsonString());
    }

    private static void ArrangeBurst(GardenSimulation simulation)
    {
        simulation._plants.Clear();
        simulation._plants.Add(new(1, GardenSimulation.Colony, 1));
        simulation._creatures.Clear();
        simulation._creatures.Add(new(0, CreatureKind.Burstling, GardenSimulation.Colony, 0, 0, false));
    }

    private static double GardenProperty(GardenHost host, string name)
    {
        var world = host.Game.World;
        var garden = world.Entities.Single(entity => entity.HasTag("garden"));
        return garden.TryGetProperty(world, name, out var value) ? value : double.NaN;
    }

    [Fact]
    public void AuthoredTuningControlsTalentTiming()
    {
        var directory = CopyContent();
        try
        {
            SetProperty(directory, "garden.echo.delay", 3);
            using var tuned = GardenHost.Create(directory, 7);
            using var normal = GardenHost.Create(7);
            foreach (var host in new[] { tuned, normal })
            {
                ArrangeBurst(host.Simulation);
                host.Simulation.ToggleTalent(Talent.Echo);
            }

            tuned.Step(1);
            normal.Step(1);

            Assert.Equal(1 + 3, tuned.Simulation.PendingImpacts.Single().DueTick);
            Assert.Equal(1 + 12, normal.Simulation.PendingImpacts.Single().DueTick);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void AuthoredSlotsLimitTalentSelection()
    {
        var directory = CopyContent();
        try
        {
            SetProperty(directory, "garden.talents.slots", 2);
            using var host = GardenHost.Create(directory, 3);
            var simulation = host.Simulation;

            Assert.True(simulation.ToggleTalent(Talent.Echo));
            Assert.True(simulation.ToggleTalent(Talent.Clone));
            Assert.False(simulation.ToggleTalent(Talent.Seedstorm));
            Assert.Equal(2, simulation.ActiveTalentCount);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void AuthoredRulesAccountForEachSelectedReaction()
    {
        using var host = GardenHost.Create(9);
        var simulation = host.Simulation;
        ArrangeBurst(simulation);
        simulation.ToggleTalent(Talent.Echo);

        host.Step(1);

        Assert.Equal(1L, simulation.EffectCount(Talent.Echo));
        Assert.Equal(1d, GardenProperty(host, "garden.talent.echo.uses"));
    }

    [Fact]
    public void ScheduledPulseDrivesMagnetismWithoutDomainPublishing()
    {
        using var host = GardenHost.Create(13);
        var simulation = host.Simulation;
        simulation._plants.Clear();
        simulation._creatures.Clear();
        simulation._creatures.Add(new(0, CreatureKind.Gatherer, GardenSimulation.Colony, 0, 0, false));
        simulation._motes.Add(new(0, GardenSimulation.Colony + new Vector2(40, 0), default, 1));
        simulation.ToggleTalent(Talent.Magnetism);

        host.Step(1);

        Assert.True(simulation.EffectCount(Talent.Magnetism) > 0);
        Assert.True(simulation.Motes[0].Position.X < GardenSimulation.Colony.X + 40);
    }

    [Fact]
    public void AuthoredCueRecipesRasterizeAndResolve()
    {
        using var host = GardenHost.Create(5);
        var presentation = host.Game.PresentationContent();
        var recipeIds = presentation.Visuals.Recipes.Select(recipe => recipe.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(presentation.Catalog.Timelines, timeline => Assert.Contains(timeline.VisualRecipeId, recipeIds));

        foreach (var recipe in presentation.Visuals.Recipes)
        {
            var buffer = ProceduralRasterizer.Rasterize(recipe, 123);
            var visible = 0;
            for (var y = 0; y < buffer.Height; y++)
            {
                for (var x = 0; x < buffer.Width; x++)
                {
                    if (buffer.GetPixel(x, y).A > 0) visible++;
                }
            }
            Assert.True(visible > 0, $"Recipe '{recipe.Id}' rendered no visible pixels.");
        }
    }

    [Fact]
    public void TalentReactionsPublishAuthoredCuesAtTheImpact()
    {
        using var host = GardenHost.Create(17);
        var simulation = host.Simulation;
        var target = GardenSimulation.Colony + new Vector2(80, -40);
        simulation._plants.Clear();
        simulation._plants.Add(new(1, target, 1));
        simulation._creatures.Clear();
        simulation._creatures.Add(new(0, CreatureKind.Burstling, target, 0, 0, false));
        simulation.ToggleTalent(Talent.Echo);

        host.Step(1);

        var cues = new List<PresentationCue>();
        while (host.Game.PresentationCues().TryDequeue(out var cue)) cues.Add(cue);

        var burst = cues.Single(cue => cue.FactType == "garden.pod.burst");
        Assert.Equal("garden.burst-ring", burst.TimelineId);
        var echo = cues.Single(cue => cue.FactType == "garden.talent.echo");
        Assert.Equal("garden.echo-spark", echo.TimelineId);
        Assert.NotNull(echo.AnchorPosition);
        Assert.Equal(target.X, echo.AnchorPosition!.Value.X, 3);
        Assert.Equal(target.Y, echo.AnchorPosition!.Value.Y, 3);

        Assert.True(host.Game.TrySamplePresentation(echo, 0, out var opening));
        Assert.Equal(0.8f, opening.Opacity, 3);
        Assert.True(host.Game.TrySamplePresentation(echo, 12, out var midpoint));
        Assert.Equal(target.X + 2, midpoint.Position.X, 3);
        Assert.Equal(target.Y - 3, midpoint.Position.Y, 3);
    }
}
