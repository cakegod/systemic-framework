using System.Text.Json;
using Systemic.Host;
using Xunit;
using static Systemic.Tests.Talents.TalentTestSupport;

namespace Systemic.Tests.Talents;

/// <summary>
/// Pack-level checks: the seven groups, the point and free-talent rules, tooltip inputs, and the
/// mechanics that are easier to prove with more than one dispatch.
/// </summary>
public sealed class TalentPackTests
{
    private static readonly string[] s_tooltipInputs = ["group", "unlock", "cost", "free", "selected"];

    [Fact]
    public void ThePackAuthorsAllFortyOneTalentsInSevenGroups()
    {
        using var entities = JsonDocument.Parse(File.ReadAllText(Path.Combine(SampleRoot, "entities.json")));
        var talents = entities.RootElement.GetProperty("entities"u8).EnumerateArray()
            .Where(entity => entity.GetProperty("tags"u8).EnumerateArray()
                .Any(tag => tag.GetString() == "talent"))
            .ToArray();

        Assert.Equal(41, talents.Length);
        Assert.Equal(41, talents.Select(talent => talent.GetProperty("id"u8).GetString()).Distinct().Count());
        Assert.Equal(5, CountGroup(talents, 0));
        for (var group = 1; group <= 6; group++)
        {
            Assert.Equal(6, CountGroup(talents, group));
            Assert.Equal(1, talents.Count(talent =>
                talent.GetProperty("properties"u8).GetProperty("group"u8).GetInt32() == group
                && talent.GetProperty("properties"u8).GetProperty("free"u8).GetInt32() == 1));
        }

        Assert.All(talents, talent =>
        {
            var properties = talent.GetProperty("properties"u8);
            foreach (var required in s_tooltipInputs)
            {
                Assert.True(properties.TryGetProperty(required, out _),
                    $"{talent.GetProperty("id"u8).GetString()} is missing tooltip input '{required}'.");
            }
        });
    }

    [Fact]
    public void EveryTalentHasARuleGatedByItsChoiceTag()
    {
        using var rules = JsonDocument.Parse(File.ReadAllText(Path.Combine(SampleRoot, "rules.json")));
        var fragments = rules.RootElement.GetProperty("fragments"u8).EnumerateArray().ToArray();
        var gatedTags = fragments
            .SelectMany(fragment => fragment.GetProperty("rules"u8).EnumerateArray())
            .SelectMany(rule => rule.TryGetProperty("when"u8, out var when) ? when.EnumerateArray().ToArray() : [])
            .Where(condition => condition.GetProperty("kind"u8).GetString() == "hasTag")
            .Select(condition => condition.GetProperty("tag"u8).GetString())
            .ToHashSet(StringComparer.Ordinal);

        using var entities = JsonDocument.Parse(File.ReadAllText(Path.Combine(SampleRoot, "entities.json")));
        var talentIds = entities.RootElement.GetProperty("entities"u8).EnumerateArray()
            .Where(entity => entity.GetProperty("tags"u8).EnumerateArray()
                .Any(tag => tag.GetString() == "talent"))
            .Select(entity => entity.GetProperty("id"u8).GetString()!)
            .ToArray();

        Assert.Equal(41, talentIds.Length);
        Assert.All(talentIds, id => Assert.Contains(id, gatedTags));
    }

    [Fact]
    public void SelectingATalentSpendsAPointAndMarksTheChoice()
    {
        var (world, runner, talent, engine) = ArrangeUnselected("talent.g1.popcorn",
            ("talentPoints", 3), ("talentEarned", 3));

        Dispatch(world, engine, "talent.select", runner, talent);

        Assert.Equal(1, Read(world, talent, "selected"));
        Assert.Equal(2, Read(world, runner, "talentPoints"));
        Assert.Equal(1, Read(world, runner, "talentSpent"));
    }

    [Fact]
    public void AChoiceFromALockedGroupCannotBeSelected()
    {
        var (world, runner, talent, engine) = ArrangeUnselected("talent.g3.supershatter",
            ("talentPoints", 3), ("talentEarned", 1));

        Dispatch(world, engine, "talent.select", runner, talent);

        Assert.Equal(0, Read(world, talent, "selected"));
        Assert.Equal(3, Read(world, runner, "talentPoints"));
    }

    [Fact]
    public void AFreeTalentActivatesWithoutSpendingATalentPoint()
    {
        var (world, runner, talent, engine) = ArrangeUnselected("talent.g1.strength-in-numbers",
            ("talentPoints", 2), ("talentEarned", 2));

        Dispatch(world, engine, "talent.refresh", runner, talent);

        Assert.Equal(1, Read(world, talent, "selected"));
        Assert.Equal(2, Read(world, runner, "talentPoints"));
        Assert.Equal(0, Read(world, runner, "talentSpent"));
    }

    [Fact]
    public void ThePackClockActivatesAnUnlockedFreeTalent()
    {
        var content = Pack;
        var materialized = content.CreateWorld(seed: 41, valueScale: 1);
        var runner = materialized.World.ById(materialized.EntityIds["actor.runner"])!;
        Assert.True(runner.TrySetProperty(materialized.World, "talentEarned", 2));
        using var host = new HeadlessSystemicHost(
            new SystemicGameBuilder(content, materialized).Build());

        host.RunTicks(61);

        var talent = host.Game.World.ById(materialized.EntityIds["talent.g1.strength-in-numbers"])!;
        Assert.Equal(1, host.Game.World.EffectiveProperty(talent, "selected"));
    }

    [Fact]
    public void HikingBuddySpawnsAtMostOneFriendPerCooldown()
    {
        var (world, runner, talent, engine) = Arrange("talent.default.hiking-buddy");
        var before = Count(world, "hiking-friend");

        Dispatch(world, engine, "talent.depart", runner, talent);
        Dispatch(world, engine, "talent.depart", runner, talent);

        Assert.Equal(before + 1, Count(world, "hiking-friend"));
    }

    [Fact]
    public void ExtraHousingOnlyBuildsTheSeventhHouseForFree()
    {
        var sixth = Arrange("talent.default.extra-housing", ("buildCount", 6));
        Dispatch(sixth.World, sixth.Engine, "talent.build", sixth.Runner, sixth.Talent);
        Assert.Equal(0, Read(sixth.World, sixth.Runner, "housing"));

        var seventh = Arrange("talent.default.extra-housing", ("buildCount", 7));
        Dispatch(seventh.World, seventh.Engine, "talent.build", seventh.Runner, seventh.Talent);
        Assert.Equal(1, Read(seventh.World, seventh.Runner, "housing"));
    }

    [Fact]
    public void DealsGrantsThreeGnorpsOnAnArrowTypePurchase()
    {
        var (world, runner, talent, engine) = Arrange("talent.g4.deals");

        Dispatch(world, engine, "talent.arrow.purchase", runner, talent);

        Assert.Equal(3, Read(world, runner, "gnorps"));
        Assert.Equal(3, Read(world, runner, "employed"));
    }

    [Fact]
    public void ProjectConstellationFiresItsScheduledVolley()
    {
        var (world, runner, talent, engine) = Arrange("talent.g3.project-constellation");

        Dispatch(world, engine, "talent.rocket.strike", runner, talent);

        Assert.Equal(1, Read(world, runner, "rocketVolley"));
    }

    [Fact]
    public void ArrowsLastHurrahPaysOutWhenAStuckArrowExpires()
    {
        var (world, runner, talent, engine) = Arrange("talent.g5.arrows-last-hurrah",
            ("stuckArrows", 1));

        Dispatch(world, engine, "talent.prepare", runner, talent);
        Dispatch(world, engine, "talent.tick", runner, talent);
        Dispatch(world, engine, "talent.arrow.expire", runner, talent);

        Assert.Equal(1, Read(world, runner, "arrowDeathHits"));
        Assert.Equal(0, Read(world, runner, "stuckArrows"));
    }

    [Fact]
    public void VulnerabilityEmpowermentReturnsTheShardsTakenRecently()
    {
        var (world, runner, talent, engine) = Arrange("talent.g5.vulnerability-empowerment",
            ("shardsTakenRecent", 4));

        Dispatch(world, engine, "talent.vulnerability", runner, talent);

        Assert.Equal(4, Read(world, runner, "shards"));
        Assert.Equal(1, Read(world, runner, "returnedShards"));
    }

    [Fact]
    public void AgitatedZybeCapsCompressionRewardsAtThree()
    {
        var (world, runner, talent, engine) = Arrange("talent.g6.agitated-zybe");

        for (var compression = 0; compression < 5; compression++)
        {
            Dispatch(world, engine, "talent.compression", runner, talent);
        }

        Assert.Equal(3, Read(world, runner, "zybellium"));
        Assert.Equal(3, Read(world, runner, "agitatorUses"));
    }

    private static int CountGroup(JsonElement[] talents, int group) =>
        talents.Count(talent =>
            talent.GetProperty("properties"u8).GetProperty("group"u8).GetInt32() == group);
}
