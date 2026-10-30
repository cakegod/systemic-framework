using System.Text.Json;
using System.Text.Json.Nodes;
using GlowGarden;
using Systemic.Content;
using Xunit;

namespace Systemic.GlowGarden.Tests;

public sealed class GardenTuningValidationTests
{
    [Fact]
    public void InvalidTuningReportsTheSourcePropertyDuringHostConstruction()
    {
        WithContent(directory =>
        {
            ChangeGarden(directory, garden => garden["properties"]!["garden.echo.delay"] = 601);
            var error = Assert.Throws<ContentPackException>(() => GardenHost.Create(directory, 1));
            Assert.Contains(error.Diagnostics, d => d.Code == "tuning.range"
                && d.File == "entities.json" && d.Path.Contains("garden.echo.delay", StringComparison.Ordinal)
                && d.Line > 1 && d.Column > 0);
        });
    }

    [Fact]
    public void UnknownPropertiesFractionalIntegerAndMissingRequiredTagAreRejected()
    {
        WithContent(directory =>
        {
            ChangeGarden(directory, garden =>
            {
                garden["properties"]!["garden.echo.delay"] = 2.5;
                garden["properties"]!["garden.echo.dleay"] = 3;
                garden["tags"] = new JsonArray();
            });

            var error = Assert.Throws<ContentPackException>(() => GardenHost.Create(directory, 1));
            Assert.Contains(error.Diagnostics, d => d.Code == "tuning.type" && d.Path.Contains("garden.echo.delay", StringComparison.Ordinal));
            Assert.Contains(error.Diagnostics, d => d.Code == "tuning.unknown_property" && d.Path.Contains("garden.echo.dleay", StringComparison.Ordinal));
            Assert.Contains(error.Diagnostics, d => d.Code == "tuning.required_tag" && d.Path.EndsWith(".tags", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void MissingTuningUsesValidatedDefaultAndRuntimeUsesCachedTypedValues()
    {
        WithContent(directory =>
        {
            ChangeGarden(directory, garden => garden["properties"]!.AsObject().Remove("garden.echo.delay"));
            using var host = GardenHost.Create(directory, 7);
            ArrangeBurst(host.Simulation);
            Assert.True(host.Simulation.ToggleTalent(Talent.Echo));
            host.Step(1);
            Assert.Equal(13, Assert.Single(host.Simulation.PendingImpacts).DueTick);
        });

        using var mutable = GardenHost.Create(7);
        Assert.Equal(12, mutable.Game.Content.Tuning("garden").GetInteger("garden.echo.delay"));
        var garden = mutable.Game.World.Entities.Single(entity => entity.HasTag("garden"));
        Assert.True(garden.TrySetProperty(mutable.Game.World, "garden.echo.delay", 4));
        ArrangeBurst(mutable.Simulation);
        Assert.True(mutable.Simulation.ToggleTalent(Talent.Echo));
        mutable.Step(1);
        Assert.Equal(13, Assert.Single(mutable.Simulation.PendingImpacts).DueTick);
    }

    private static void ArrangeBurst(GardenSimulation simulation)
    {
        simulation._plants.Clear();
        simulation._plants.Add(new(1, GardenSimulation.Colony, 1));
        simulation._creatures.Clear();
        simulation._creatures.Add(new(0, CreatureKind.Burstling, GardenSimulation.Colony, 0, 0, false));
    }

    private static void WithContent(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "garden-validation-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var file in Directory.GetFiles(GardenHost.ContentDirectory))
            {
                File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
            }

            action(directory);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void ChangeGarden(string directory, Action<JsonNode> change)
    {
        var path = Path.Combine(directory, "entities.json");
        var root = JsonNode.Parse(File.ReadAllText(path))!;
        var garden = root["entities"]!.AsArray().First(entity => entity!["id"]!.GetValue<string>() == "garden")!;
        change(garden);
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
