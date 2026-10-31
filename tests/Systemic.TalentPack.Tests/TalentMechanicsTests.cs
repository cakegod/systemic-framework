using Xunit;
using static Systemic.Tests.Talents.TalentTestSupport;

namespace Systemic.Tests.Talents;

/// <summary>
/// One case per wiki talent. Each case drives the event or tick the talent reacts to, asserts the
/// mechanic it changes, then deactivates the talent and proves it stops.
/// </summary>
public sealed class TalentMechanicsTests
{
    public static IEnumerable<object[]> Cases => s_cases.Select(test => new object[] { test });

    [Theory]
    [MemberData(nameof(Cases))]
    public void EachTalentImplementsItsWikiMechanicAndStopsWhenDeactivated(TalentCase test)
    {
        var setup = Setup(test.SetupProperty, test.SetupValue)
            .Concat(Setup(test.SetupProperty2, test.SetupValue2))
            .ToArray();
        var (world, runner, talent, engine) = Arrange(test.Id, setup);

        var before = test.Outcome is Outcome.Spawn
            ? Count(world, test.SpawnTag)
            : Read(world, runner, test.Property);

        switch (test.Outcome)
        {
            case Outcome.Delta:
            case Outcome.Modifier:
                Dispatch(world, engine, test.Trigger, runner, talent);
                Assert.Equal(before + test.Delta, Read(world, runner, test.Property), 6);
                Deactivate(world, talent, test.Id);
                Dispatch(world, engine, test.Trigger, runner, talent);
                if (test.Outcome == Outcome.Modifier)
                {
                    world.LogicalTick = 1;
                    world.AdvanceModifiers();
                    world.LogicalTick = 2;
                    world.AdvanceModifiers();
                    world.LogicalTick = 3;
                    world.AdvanceModifiers();
                    Assert.Equal(before, Read(world, runner, test.Property), 6);
                }
                else
                {
                    Assert.Equal(before + test.Delta, Read(world, runner, test.Property), 6);
                }

                break;
            case Outcome.Spawn:
                Dispatch(world, engine, test.Trigger, runner, talent);
                Assert.Equal(before + test.SpawnCount, Count(world, test.SpawnTag));
                Deactivate(world, talent, test.Id);
                Dispatch(world, engine, test.Trigger, runner, talent);
                Assert.Equal(before + test.SpawnCount, Count(world, test.SpawnTag));
                break;
            case Outcome.Chance:
                for (var attempt = 0; attempt < 60; attempt++)
                {
                    Dispatch(world, engine, test.Trigger, runner, talent);
                }

                var after = Read(world, runner, test.Property);
                Assert.True(after > before, $"{test.Name} never procured in 60 attempts.");
                Deactivate(world, talent, test.Id);
                for (var attempt = 0; attempt < 60; attempt++)
                {
                    Dispatch(world, engine, test.Trigger, runner, talent);
                }

                Assert.Equal(after, Read(world, runner, test.Property), 6);
                break;
            default:
                throw new InvalidOperationException($"Unknown test outcome '{test.Outcome}'.");
        }
    }

    private static (string Property, double Value)[] Setup(string first, double firstValue) =>
        string.IsNullOrEmpty(first) ? [] : [(first, firstValue)];

    private static (string Property, double Value)[] Setup(
        string first,
        double firstValue,
        string second,
        double secondValue) =>
        (first, second) switch
        {
            ("", "") => [],
            ("", _) => [(second, secondValue)],
            (_, "") => [(first, firstValue)],
            _ => [(first, firstValue), (second, secondValue)],
        };

    private static readonly TalentCase[] s_cases =
    [
        new("Motivation", "talent.default.motivation", "talent.tick", Outcome.Modifier,
            "damage", 10, SetupProperty: "runnerProgress", SetupValue: 0.5),
        new("Hiking Buddy", "talent.default.hiking-buddy", "talent.depart", Outcome.Spawn,
            SpawnTag: "hiking-friend", SpawnCount: 1),
        new("Runner Backstop", "talent.default.runner-backstop", "talent.tick", Outcome.Modifier,
            "capacity", 20, SetupProperty: "collectionPerMinute", SetupValue: 0),
        new("Extra Housing", "talent.default.extra-housing", "talent.build", Outcome.Delta,
            "housing", 1, SetupProperty: "buildCount", SetupValue: 7),
        new("Bathe In Fire", "talent.default.bathe-in-fire", "talent.bomb", Outcome.Delta,
            "speed", 20, SetupProperty: "heat", SetupValue: 1),

        new("Strength In Numbers", "talent.g1.strength-in-numbers", "talent.tick", Outcome.Modifier,
            "multiplier", 0.2, SetupProperty: "employed", SetupValue: 8,
            SetupProperty2: "employedGroup", SetupValue2: 2),
        new("Popcorn", "talent.g1.popcorn", "talent.tick", Outcome.Modifier,
            "fireRangeMax", 6),
        new("Bullet Breath", "talent.g1.bullet-breath", "talent.combo", Outcome.Delta,
            "bullets", 1, SetupProperty: "fireBreath", SetupValue: 1,
            SetupProperty2: "comboStacks", SetupValue2: 7),
        new("Superhot", "talent.g1.superhot", "talent.tick", Outcome.Modifier,
            "fireDamage", 1.5),
        new("Timeshift Delivery", "talent.g1.timeshift-delivery", "talent.timeshift", Outcome.Delta,
            "timeshiftTicks", 30, SetupProperty: "timeShiftImpact", SetupValue: 1),
        new("Explosive Exposition", "talent.g1.explosive-exposition", "talent.grenade", Outcome.Delta,
            "weakspotTicks", -180, SetupProperty: "weakspotTicks", SetupValue: 180),

        new("Inspiration", "talent.g2.inspiration", "talent.tick", Outcome.Modifier,
            "inspiration", 1),
        new("Gatling Balloon", "talent.g2.gatling-balloon", "talent.balloon", Outcome.Delta,
            "gatlingBullets", 1, SetupProperty: "balloonGun", SetupValue: 1),
        new("The Flow", "talent.g2.the-flow", "talent.tick", Outcome.Modifier,
            "damage", 6, SetupProperty: "meditation", SetupValue: 1,
            SetupProperty2: "comboStacks", SetupValue2: 2),
        new("Bigger Pile", "talent.g2.bigger-pile", "talent.tick", Outcome.Modifier,
            "gnorps", 5),
        new("The Higher The Pile", "talent.g2.the-higher-the-pile", "talent.throw", Outcome.Delta,
            "throwVelocity", 1, SetupProperty: "pileHeight", SetupValue: 0.8,
            SetupProperty2: "pileThreshold", SetupValue2: 1),
        new("Notes", "talent.g2.notes", "talent.tick", Outcome.Modifier,
            "multiplier", 2),

        new("Prestige Expedience", "talent.g3.prestige-expedience", "talent.tick", Outcome.Modifier,
            "gnorps", 3, SetupProperty: "talentSpent", SetupValue: 3),
        new("Future To The Back", "talent.g3.future-to-the-back", "talent.tick", Outcome.Modifier,
            "arrowRegular", 1),
        new("Project Constellation", "talent.g3.project-constellation", "talent.tick", Outcome.Modifier,
            "rocketRangeMax", 2),
        new("Supershatter", "talent.g3.supershatter", "talent.tick", Outcome.Modifier,
            "freezeTicks", 1500),
        new("Return To Sender", "talent.g3.return-to-sender", "talent.director.leave", Outcome.Chance,
            "arrows"),
        new("Descent Of The King", "talent.g3.descent-of-the-king", "talent.king.descend", Outcome.Delta,
            "multiplier", 8),

        new("Zybellium Mastery", "talent.g4.zybellium-mastery", "talent.tick", Outcome.Modifier,
            "zybellium", 1),
        new("Rocket Gatler", "talent.g4.rocket-gatler", "talent.hit", Outcome.Chance,
            "rockets"),
        new("Frostfire", "talent.g4.frostfire", "talent.tick", Outcome.Modifier,
            "statusFire", 1),
        new("Infinite Reverberation", "talent.g4.infinite-reverberation", "talent.tick", Outcome.Modifier,
            "shockwaveFalloff", -1),
        new("Deals", "talent.g4.deals", "talent.shoot", Outcome.Delta,
            "arrows", 1),
        new("Reclamation Mitigation", "talent.g4.reclamation-mitigation", "talent.tick", Outcome.Modifier,
            "reclamationBias", 1),

        new("New Building", "talent.g5.new-building", "talent.tick", Outcome.Modifier,
            "shrine", 1),
        new("Combo Power", "talent.g5.combo-power", "talent.prepare", Outcome.Delta,
            "comboChance", 0.01),
        new("Arrows' Last Hurrah", "talent.g5.arrows-last-hurrah", "talent.tick", Outcome.Modifier,
            "arrowStickDenominator", 17),
        new("DON'T EAT THAT!", "talent.g5.dont-eat-that", "talent.drone.consume", Outcome.Delta,
            "zygnorpEnergy", 1),
        new("DRONE.TARGET = FAR OR DRONE.TARGET = CLOSE", "talent.g5.drone-target", "talent.tick", Outcome.Modifier,
            "droneTargetFar", 1),
        new("Vulnerability Empowerment", "talent.g5.vulnerability-empowerment", "talent.tick", Outcome.Modifier,
            "vulnerabilityMultiplier", 1),

        new("Multiplier Growth", "talent.g6.multiplier-growth", "talent.growth", Outcome.Delta,
            "multiplier", 0.1),
        new("Operation: Pocket Rocket Timeshift Retainment", "talent.g6.pocket-rocket-timeshift-retainment",
            "talent.timeshift", Outcome.Delta, "pocketRockets", 1,
            SetupProperty: "timeShiftImpact", SetupValue: 1),
        new("Agitated Zybe", "talent.g6.agitated-zybe", "talent.compression", Outcome.Delta,
            "zybellium", 1),
        new("Gnorpcalibur: Unleashed", "talent.g6.gnorpcalibur-unleashed", "talent.tick", Outcome.Modifier,
            "accordConcurrent", 1),
        new("Modern Warfare", "talent.g6.modern-warfare", "talent.rifle", Outcome.Delta,
            "rifleGrenades", 1, SetupProperty: "grenadeLauncher", SetupValue: 1),
        new("Drone Capacity", "talent.g6.drone-capacity", "talent.drone.death", Outcome.Spawn,
            SpawnTag: "drone.baby", SpawnCount: 2),
    ];
}
