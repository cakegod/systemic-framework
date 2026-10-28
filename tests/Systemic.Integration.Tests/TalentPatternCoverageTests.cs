using System.Numerics;
using System.Text.Json;
using Systemic.Kernel;
using Systemic.Content;
using Systemic.Gameplay2D;
using Systemic.Host;
using Systemic.Rules;
using Systemic.Tests;

namespace Systemic.Gameplay2D.Tests;

public sealed class TalentPatternCoverageTests
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Lazy<ContentPack> s_sample = new(() => SystemicTestContent.Load(
        Path.Combine(AppContext.BaseDirectory, "samples", "incremental-quarry")));

    private static ContentPack Sample => s_sample.Value;

    public static IEnumerable<object[]> PatternCases => ReadCases()
        .Select(pattern => new object[] { pattern.Id, pattern.Mechanic, pattern.Ports });

    [Fact]
    public void Anonymous_coverage_corpus_tracks_all_forty_one_audited_patterns()
    {
        var cases = ReadCases();

        Assert.Equal(41, cases.Length);
        Assert.Equal(41, cases.Select(pattern => pattern.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(cases, pattern =>
        {
            Assert.Matches("^pattern-[0-9]{2}$", pattern.Id);
            Assert.False(string.IsNullOrWhiteSpace(pattern.Mechanic));
            Assert.NotEmpty(pattern.Ports);
        });
    }

    [Theory]
    [MemberData(nameof(PatternCases))]
    public void Every_anonymous_pattern_resolves_to_a_working_framework_port(
        string id,
        string mechanic,
        string[] ports)
    {
        Assert.StartsWith("pattern-", id, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(mechanic));
        foreach (var port in ports)
        {
            VerifyPort(port);
        }
    }

    private static PatternCoverage[] ReadCases()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "talent-pattern-coverage.json");
        return JsonSerializer.Deserialize<PatternCoverage[]>(File.ReadAllText(path), s_jsonOptions)
            ?? throw new InvalidDataException("Talent-pattern coverage fixture is empty.");
    }

    private static void VerifyPort(string port)
    {
        switch (port)
        {
            case "rules.expression":
                var expression = ExpressionCompiler.Compile("event.amount >= 1 && target.power > 0");
                Assert.True(expression.EvaluateBoolean(new PatternScope()));
                break;
            case "rules.event":
                var world = new World(seed: 1, valueScale: 1);
                var entity = world.Add(new DataEntity([], [], ("value", 0)));
                var rules = new CompiledRuleSet([
                    new CompiledRule("pattern.emit", "pattern.input", 0, [],
                        [RuleEffect.AdjustProperty(RuleSubject.Target, "value", 1), RuleEffect.Emit("pattern.output")])]);
                var result = new RuleEngine(rules).Dispatch(world,
                    new SimulationFact("pattern.input", entity.Id, entity.Id));
                Assert.Equal(2, result.FactsProcessed);
                Assert.Equal(1, world.EffectiveProperty(entity, "value"));
                break;
            case "rules.event-history":
                var eventCount = ExpressionCompiler.Compile("event_count('pattern.hit', 12) <= 3");
                Assert.True(eventCount.EvaluateBoolean(new PatternScope()));
                break;
            case "rules.chance":
                Assert.Equal(0.25, RuleCondition.Chance(0.25).Threshold);
                Assert.Throws<ArgumentOutOfRangeException>(() => RuleCondition.Chance(1.1));
                break;
            case "rules.schedule":
                var schedule = new RuleScheduleDefinition("pattern.periodic", "pattern.pulse", 5,
                    1, null, null, 2);
                Assert.Equal(schedule, Assert.Single(new CompiledRuleSet([], [schedule]).Schedules));
                break;
            case "kernel.modifier":
                var modifierWorld = new World(seed: 2, valueScale: 1);
                var target = modifierWorld.Add(new DataEntity([], [], ("power", 2)));
                modifierWorld.SetModifier("pattern.buff", target, "power", ModifierOperation.Add, 3,
                    remainingTicks: 2, stage: ModifierStage.Final);
                Assert.Equal(5, modifierWorld.EffectiveProperty(target, "power"));
                break;
            case "incremental.catalog":
                Assert.NotEmpty(SystemicTestContent.Incremental(Sample).Catalog.Producers);
                break;
            case "build-choices":
                Assert.NotEmpty(SystemicTestContent.Incremental(Sample).Choices.Choices);
                break;
            case "ui.binding":
                var uiMaterialized = Sample.CreateWorld(seed: 3, valueScale: 1);
                var uiGame = new SystemicGameBuilder(Sample, uiMaterialized).Build();
                Assert.NotEmpty(uiGame.EvaluateUi("quarry.hud").Children);
                break;
            case "gameplay2d.actor":
                var actorWorld = new World(seed: 4, valueScale: 1);
                var actor = Gameplay2D.SpawnActor(actorWorld, Blueprint(), new Point2(1, 2));
                Assert.True(actorWorld.TryGetSpatialBounds(actor.Id, out _));
                break;
            case "gameplay2d.projectile":
                var projectileWorld = new World(seed: 5, valueScale: 1);
                var projectile = Gameplay2D.SpawnProjectile(projectileWorld, Blueprint(), new Point2(0, 0),
                    new Vector2(2, 1), lifetimeTicks: 3);
                var projectileContext = Context(projectileWorld, 1);
                new Gameplay2DModule().Step(projectileContext);
                projectileContext.ApplyDeferredCommands();
                Assert.True(projectile.TryGetProperty(projectileWorld, Gameplay2D.PositionX, out var x));
                Assert.Equal(2, x);
                break;
            case "gameplay2d.target.nearest":
            case "gameplay2d.target.farthest":
                var targetWorld = new World(seed: 6, valueScale: 1);
                var near = Gameplay2D.SpawnActor(targetWorld, Blueprint(), new Point2(2, 0));
                var far = Gameplay2D.SpawnActor(targetWorld, Blueprint(), new Point2(7, 0));
                var candidates = new List<int>();
                var selection = port.EndsWith("nearest", StringComparison.Ordinal)
                    ? Gameplay2D.SelectNearestTarget(targetWorld, new Point2(0, 0), 10, "pattern.target", candidates)
                    : Gameplay2D.SelectFarthestTarget(targetWorld, new Point2(0, 0), 10, "pattern.target", candidates);
                Assert.Equal(port.EndsWith("nearest", StringComparison.Ordinal) ? near.Id : far.Id, selection!.Id);
                break;
            case "gameplay2d.status":
                var statusWorld = new World(seed: 7, valueScale: 1);
                var statusTarget = statusWorld.Add(new DataEntity([], [], ("power", 3)));
                Gameplay2D.ApplyStatus(statusWorld, statusTarget, new StatusEffectDefinition("pattern.status",
                    "pattern.active", "power", ModifierOperation.Multiply, 2, DurationTicks: 2));
                Assert.Equal(6, statusWorld.EffectiveProperty(statusTarget, "power"));
                break;
            default:
                throw new InvalidOperationException($"Pattern references unregistered framework port '{port}'.");
        }
    }

    private static ActorDefinition Blueprint() =>
        new("pattern.target", ["pattern.target"], [], new Dictionary<string, double>(), 1, 1);

    private static SystemicSystemContext Context(World world, int tick) =>
        new(world, new RuleEngine(new CompiledRuleSet([])), tick);

    private sealed record PatternCoverage(string Id, string Mechanic, string[] Ports);

    private readonly struct PatternScope : IExpressionScope
    {
        public double ReadNumber(string name) => name switch
        {
            "event.amount" => 1,
            "target.power" => 2,
            _ => throw new ExpressionEvaluationException($"Unknown test scope value '{name}'."),
        };

        public double Count(string selector) => 1;
        public double Rate(string selector, int windowTicks) => 1;
        public double EventCount(string eventType, int windowTicks) => 1;
    }
}
