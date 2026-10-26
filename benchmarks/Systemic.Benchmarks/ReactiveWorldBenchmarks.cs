using BenchmarkDotNet.Attributes;
using Systemic.Kernel;
using Systemic.Rules;

namespace Systemic.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob]
public class ReactiveWorldBenchmarks
{
    private const int ProductCount = 50_000;
    private const int ActorCount = 1_000;
    private World _world = null!;
    private RuleEngine _engine = null!;
    private Entity[] _actors = [];
    private int[] _targetIds = [];
    private List<int>[] _spatialQueries = [];

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(seed: 71, valueScale: 1, spatialCellSize: 24);
        _actors = new Entity[ActorCount];
        _targetIds = new int[ActorCount];
        _spatialQueries = new List<int>[ActorCount];

        for (var product = 0; product < ProductCount; product++)
        {
            var x = (product % 250) * 24.0f;
            var y = (product / 250) * 24.0f;
            var entity = _world.Add(new DataEntity(["material", "brittle"], [],
                ("heat", 0), ("stress", 0), ("durability", 100)));
            _world.SetSpatialBounds(entity, new Bounds2(x, y, 4, 4));
        }

        for (var actorIndex = 0; actorIndex < ActorCount; actorIndex++)
        {
            var x = (actorIndex % 50) * 120.0f + 10;
            var y = (actorIndex / 50) * 120.0f + 10;
            _actors[actorIndex] = _world.Add(new DataEntity(
                ["talent.magma", "talent.chain", "talent.echo", "talent.tempering"], [],
                ("charge", 1)));
            _world.SetSpatialBounds(_actors[actorIndex], new Bounds2(x, y, 3, 3));
            _spatialQueries[actorIndex] = new List<int>(24);
        }

        _engine = new RuleEngine(CreateReactiveRules());
        FindTargets();
    }

    [Benchmark]
    public int QueryAndDispatchReactionChains()
    {
        FindTargets();
        return DispatchReactionChains();
    }

    public void ExecuteQueryTick() => FindTargets();

    public void ExecuteDispatchTick() => _ = DispatchReactionChains();

    public void ExecuteTick() => _ = QueryAndDispatchReactionChains();

    private int DispatchReactionChains()
    {
        var effects = 0;
        for (var actorIndex = 0; actorIndex < ActorCount; actorIndex++)
        {
            var result = _engine.Dispatch(_world,
                new SimulationFact("contact", _actors[actorIndex].Id, _targetIds[actorIndex]));
            effects += result.EffectsApplied;
        }

        return effects;
    }

    private void FindTargets()
    {
        for (var actorIndex = 0; actorIndex < ActorCount; actorIndex++)
        {
            var actor = _actors[actorIndex];
            var bounds = _world.TryGetSpatialBounds(actor.Id, out var current) ? current : default;
            var nearby = _spatialQueries[actorIndex];
            _world.QuerySpatialNearby(new Point2(bounds.X, bounds.Y), 20, nearby);
            var target = 0;
            for (var index = 0; index < nearby.Count; index++)
            {
                var candidateId = nearby[index];
                if (candidateId != actor.Id && _world.ById(candidateId)?.HasTag("material") == true)
                {
                    target = candidateId;
                    break;
                }
            }

            if (target == 0)
            {
                throw new InvalidOperationException($"Actor {actor.Id} has no nearby product.");
            }

            _targetIds[actorIndex] = target;
        }
    }

    private static CompiledRuleSet CreateReactiveRules()
    {
        var rules = new List<CompiledRule>
        {
            new("talent.magma-contact", "contact", 10,
                [RuleCondition.HasTag(RuleSubject.Source, "talent.magma"),
                 RuleCondition.HasTag(RuleSubject.Source, "talent.chain"),
                 RuleCondition.HasTag(RuleSubject.Target, "material")],
                [RuleEffect.AdjustProperty(RuleSubject.Target, "heat", 1), RuleEffect.Emit("reaction.chain.0")]),
            new("talent.echo-contact", "contact", 5,
                [RuleCondition.HasTag(RuleSubject.Source, "talent.echo"),
                 RuleCondition.HasTag(RuleSubject.Target, "brittle")],
                [RuleEffect.AdjustProperty(RuleSubject.Target, "stress", 1), RuleEffect.Emit("reaction.chain.0")]),
            new("talent.tempering-contact", "contact", 0,
                [RuleCondition.HasTag(RuleSubject.Source, "talent.tempering"),
                 RuleCondition.HasTag(RuleSubject.Target, "brittle")],
                [RuleEffect.AdjustProperty(RuleSubject.Target, "durability", -0.25)])
        };

        for (var reaction = 0; reaction < 8; reaction++)
        {
            rules.Add(new CompiledRule(
                $"reaction.chain.{reaction}",
                $"reaction.chain.{reaction}",
                0,
                [RuleCondition.PropertyAtLeast(RuleSubject.Target, "heat", 0)],
                [RuleEffect.AdjustProperty(RuleSubject.Target, "stress", 0.25),
                 RuleEffect.Emit($"reaction.chain.{reaction + 1}")]));
        }

        return new CompiledRuleSet([.. rules]);
    }
}
