using System.Text.Json;
using Systemic.Content;
using Systemic.Kernel;
using Systemic.Rules;
using Xunit;

namespace Systemic.Tests.Talents;

/// <summary>
/// Every talent is a choice entity plus a rule gated on its activation tag. Each case runs the
/// talent once with the tag and once without it, so a missing tag cannot silently pass.
/// </summary>
public sealed class TalentPackTests
{
    private static readonly Lazy<ContentPack> s_content = new(() => ContentPackLoader.Load(SampleRoot));

    private static string SampleRoot => Path.Combine(AppContext.BaseDirectory, "samples", "talent-pack");

    public static IEnumerable<object[]> Cases => TalentCases
        .Select(talent => new object[]
        {
            talent.Name, talent.Id, talent.Trigger, talent.Property, talent.Delta,
            talent.SetupProperty, talent.SetupValue, talent.SpawnedTag,
        });

    [Theory]
    [MemberData(nameof(Cases))]
    public void EachTalentAppliesOnlyWhileItsTagIsSet(
        string name,
        string talentId,
        string trigger,
        string property,
        double delta,
        string setupProperty,
        double setupValue,
        string spawnedTag)
    {
        Assert.False(string.IsNullOrWhiteSpace(name));

        var enabled = Arrange(talentId, setupProperty, setupValue);
        var before = Read(enabled.World, enabled.Runner, property, spawnedTag);
        enabled.Talent.AddTag(talentId);
        enabled.Engine.Dispatch(enabled.World,
            new SimulationFact(trigger, enabled.Talent.Id, enabled.Runner.Id));
        var after = Read(enabled.World, enabled.Runner, property, spawnedTag);

        Assert.Equal(before + (spawnedTag.Length > 0 ? 1 : delta), after, 6);

        var disabled = Arrange(talentId, setupProperty, setupValue);
        var unchanged = Read(disabled.World, disabled.Runner, property, spawnedTag);
        disabled.Engine.Dispatch(disabled.World,
            new SimulationFact(trigger, disabled.Talent.Id, disabled.Runner.Id));

        Assert.Equal(unchanged, Read(disabled.World, disabled.Runner, property, spawnedTag), 6);
    }

    [Fact]
    public void ThePackAuthorsAllFortyOneTalentsInSevenGroups()
    {
        var entities = JsonDocument.Parse(File.ReadAllText(Path.Combine(SampleRoot, "entities.json")));
        var talents = entities.RootElement.GetProperty("entities"u8).EnumerateArray()
            .Where(entity => entity.GetProperty("tags"u8).EnumerateArray()
                .Any(tag => tag.GetString() == "talent"))
            .ToArray();

        Assert.Equal(41, talents.Length);
        Assert.Equal(41, talents.Select(talent => talent.GetProperty("id"u8).GetString()).Distinct().Count());
        Assert.Equal(5, talents.Count(talent => talent.GetProperty("properties"u8).GetProperty("group"u8).GetInt32() == 0));
        for (var group = 1; group <= 6; group++)
        {
            Assert.Equal(6, talents.Count(talent =>
                talent.GetProperty("properties"u8).GetProperty("group"u8).GetInt32() == group));
        }

        var rules = File.ReadAllText(Path.Combine(SampleRoot, "rules.json"));
        foreach (var talent in talents)
        {
            Assert.Contains($"\"{talent.GetProperty("id"u8).GetString()}\"", rules, StringComparison.Ordinal);
        }

        using var rulesDocument = JsonDocument.Parse(rules);
        Assert.Equal(7, rulesDocument.RootElement.GetProperty("fragments"u8).GetArrayLength());
    }

    private static (World World, Entity Runner, Entity Talent, RuleEngine Engine) Arrange(
        string talentId,
        string setupProperty,
        double setupValue)
    {
        var content = s_content.Value;
        var materialized = content.CreateWorld(seed: 41, valueScale: 1);
        var world = materialized.World;
        var runner = world.ById(materialized.EntityIds["actor.runner"])!;
        var talent = world.ById(materialized.EntityIds[talentId])!;
        if (setupProperty.Length > 0)
        {
            Assert.True(runner.TrySetProperty(world, setupProperty, setupValue));
        }

        return (world, runner, talent, new RuleEngine(content.Rules));
    }

    private static double Read(World world, Entity runner, string property, string spawnedTag) =>
        spawnedTag.Length > 0
            ? world.Entities.Count(entity => entity.HasTag(spawnedTag))
            : world.EffectiveProperty(runner, property);

    private sealed record TalentCase(
        string Name,
        string Id,
        string Trigger,
        string Property,
        double Delta,
        string SetupProperty = "",
        double SetupValue = 0,
        string SpawnedTag = "");

    private static readonly TalentCase[] TalentCases =
    [
        new("Motivation", "talent.default.motivation", "talent.strike", "damage", 2),
        new("Hiking Buddy", "talent.default.hiking-buddy", "talent.strike", "", 0, SpawnedTag: "hiking-friend"),
        new("Runner Backstop", "talent.default.runner-backstop", "talent.tick", "speed", 1),
        new("Extra Housing", "talent.default.extra-housing", "talent.build", "housing", 1, "buildCount", 3),
        new("Bathe In Fire", "talent.default.bathe-in-fire", "talent.hit", "damage", 3, "heat", 1),

        new("Strength In Numbers", "talent.g1.strength-in-numbers", "talent.strike", "damage", 3, "housing", 2),
        new("Popcorn", "talent.g1.popcorn", "talent.strike", "comboChance", 0.1),
        new("Bullet Breath", "talent.g1.bullet-breath", "talent.strike", "damage", 5),
        new("Superhot", "talent.g1.superhot", "talent.hit", "heat", 1),
        new("Timeshift Delivery", "talent.g1.timeshift-delivery", "talent.tick", "rate", 1),
        new("Explosive Exposition", "talent.g1.explosive-exposition", "talent.hit", "comboPower", 1),

        new("Inspiration", "talent.g2.inspiration", "talent.tick", "inspiration", 1),
        new("Gatling Balloon", "talent.g2.gatling-balloon", "talent.strike", "rate", 1),
        new("The Flow", "talent.g2.the-flow", "talent.tick", "comboChance", 0.05),
        new("Bigger Pile", "talent.g2.bigger-pile", "talent.pickup", "capacity", 2),
        new("The Higher The Pile", "talent.g2.the-higher-the-pile", "talent.pickup", "yield", 1, "capacity", 2),
        new("Notes", "talent.g2.notes", "talent.strike", "notes", 1),

        new("Prestige Expedience", "talent.g3.prestige-expedience", "talent.reset", "prestigeGain", 1),
        new("Future To The Back", "talent.g3.future-to-the-back", "talent.offline", "offlineGain", 1),
        new("Project Constellation", "talent.g3.project-constellation", "talent.launch", "rocketPower", 2),
        new("Supershatter", "talent.g3.supershatter", "talent.hit", "crit", 0.05),
        new("Return To Sender", "talent.g3.return-to-sender", "talent.hit", "damage", 1, "shield", 1),
        new("Descent Of The King", "talent.g3.descent-of-the-king", "talent.reset", "multiplier", 1, "shards", 10),

        new("Zybellium Mastery", "talent.g4.zybellium-mastery", "talent.tick", "multiplier", 1),
        new("Rocket Gatler", "talent.g4.rocket-gatler", "talent.launch", "rockets", 1),
        new("Frostfire", "talent.g4.frostfire", "talent.hit", "heat", -1, "heat", 1),
        new("Infinite Reverberation", "talent.g4.infinite-reverberation", "talent.combo", "comboPower", 1),
        new("Deals", "talent.g4.deals", "talent.spend", "buildCost", -1),
        new("Reclamation Mitigation", "talent.g4.reclamation-mitigation", "talent.reset", "shards", 5),

        new("New Building", "talent.g5.new-building", "talent.build", "housing", 1),
        new("Combo Power", "talent.g5.combo-power", "talent.combo", "comboPower", 2),
        new("Arrows' Last Hurrah", "talent.g5.arrows-last-hurrah", "talent.strike", "damage", 5),
        new("DON'T EAT THAT!", "talent.g5.dont-eat-that", "talent.pickup", "vulnerability", -1),
        new("DRONE.TARGET = FAR OR DRONE.TARGET = CLOSE", "talent.g5.drone-target", "talent.drone", "dronePower", 1),
        new("Vulnerability Empowerment", "talent.g5.vulnerability-empowerment", "talent.hit", "damage", 2),

        new("Multiplier Growth", "talent.g6.multiplier-growth", "talent.tick", "multiplier", 1),
        new("Operation: Pocket Rocket Timeshift Retainment",
            "talent.g6.pocket-rocket-timeshift-retainment", "talent.offline", "rocketPower", 1),
        new("Agitated Zybe", "talent.g6.agitated-zybe", "talent.hit", "damage", 1),
        new("Gnorpcalibur: Unleashed", "talent.g6.gnorpcalibur-unleashed", "talent.strike", "crit", 0.1, "notes", 1),
        new("Modern Warfare", "talent.g6.modern-warfare", "talent.drone", "dronePower", 2),
        new("Drone Capacity", "talent.g6.drone-capacity", "talent.drone", "drones", 1),
    ];
}
