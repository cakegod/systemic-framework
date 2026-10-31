using Systemic.Content;
using Systemic.Kernel;
using Systemic.Rules;
using Xunit;

namespace Systemic.Tests.Talents;

public static class TalentTestSupport
{
    private static readonly Lazy<ContentPack> s_content = new(() => ContentPackLoader.Load(SampleRoot));

    public static string SampleRoot => Path.Combine(AppContext.BaseDirectory, "samples", "talent-pack");

    public static ContentPack Pack => s_content.Value;

    public static (World World, Entity Runner, Entity Talent, RuleEngine Engine) Arrange(
        string talentId,
        params (string Property, double Value)[] setup) =>
        Arrange(talentId, selected: true, setup);

    public static (World World, Entity Runner, Entity Talent, RuleEngine Engine) ArrangeUnselected(
        string talentId,
        params (string Property, double Value)[] setup) =>
        Arrange(talentId, selected: false, setup);

    private static (World World, Entity Runner, Entity Talent, RuleEngine Engine) Arrange(
        string talentId,
        bool selected,
        (string Property, double Value)[] setup)
    {
        var content = s_content.Value;
        var materialized = content.CreateWorld(seed: 41, valueScale: 1);
        var world = materialized.World;
        var runner = world.ById(materialized.EntityIds["actor.runner"])!;
        var talent = world.ById(materialized.EntityIds[talentId])!;
        Assert.True(talent.TrySetProperty(world, "selected", selected ? 1 : 0));
        foreach (var (property, value) in setup)
        {
            Assert.True(runner.TrySetProperty(world, property, value));
        }

        return (world, runner, talent, new RuleEngine(content.Rules));
    }

    public static void Dispatch(
        World world,
        RuleEngine engine,
        string trigger,
        Entity runner,
        Entity talent) =>
        engine.Dispatch(world, new SimulationFact(trigger, runner.Id, talent.Id));

    public static double Read(World world, Entity runner, string property) =>
        world.EffectiveProperty(runner, property);

    public static int Count(World world, string tag) => world.LiveCount(tag);

    public static void Deactivate(World world, Entity talent, string talentId)
    {
        talent.RemoveTag(talentId);
        Assert.True(talent.TrySetProperty(world, "selected", 0));
    }

    public enum Outcome
    {
        Delta,
        Modifier,
        Spawn,
        Chance,
    }

    public sealed record TalentCase(
        string Name,
        string Id,
        string Trigger,
        Outcome Outcome,
        string Property = "",
        double Delta = 0,
        string SpawnTag = "",
        int SpawnCount = 0,
        string SetupProperty = "",
        double SetupValue = 0,
        string SetupProperty2 = "",
        double SetupValue2 = 0)
    {
        public override string ToString() => Name;
    }
}
