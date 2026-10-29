using Systemic.Content;
using Systemic.Host;
using Systemic.Kernel;
using Systemic.Rules;

namespace Systemic.Benchmarks;

// Content tier of the approved performance contract: 2,000 actors driving entity and rule work
// through one game step per tick, at most 4 KiB per tick and inside a 5 ms tick.
// The frame-time claim is gated here, in a dedicated process, because a wall-clock budget asserted
// inside the parallel test suite measures whatever else the machine is running.
internal sealed class ContentTierWorkload : IDisposable
{
    public const int ActorCount = 2_000;
    public const long MaximumBytesPerTick = 4 * 1024;

    private readonly World _world;
    private readonly Entity _target;
    private readonly SystemicGame _game;

    public ContentTierWorkload()
    {
        _world = new World(seed: 17, valueScale: 1);
        _target = _world.Add(new DataEntity([], [], ("value", 0.0)));
        _game = new SystemicGameBuilder(Content(), _world)
            .AddSystem(new EmitSystem(_target.Id))
            .Build();
    }

    public void Tick() => _game.Step();

    // The gate is worthless if the workload stops doing the work it claims to measure, so every
    // emitted fact's effect is checked against the closed form at the end of the run.
    public void Validate(int ticks)
    {
        var expected = (double)ActorCount * ticks;
        if (!_target.TryGetProperty(_world, "value", out var value) || value != expected)
        {
            throw new InvalidOperationException(
                $"Content tier validation failed: value={value}, expected {expected} after {ticks} ticks.");
        }
    }

    public void Dispose() => _game.Dispose();

    private static ContentPack Content() => new("", new GameManifest("test", "Test", 30, 4),
        new CompiledRuleSet([
            new CompiledRule("test.adjust", "adjust", 0, [],
                [RuleEffect.AdjustProperty(RuleSubject.Target, "value", 1)])]),
        new CompiledEntities([]));

    private sealed class EmitSystem(int targetId) : ISystemicSystem
    {
        public string Id => "test.emit";

        public IReadOnlyList<SystemAccess> Access => [SystemAccess.DeferWorldWrites];

        public void Step(SystemicSystemContext context)
        {
            for (var actor = 0; actor < ActorCount; actor++)
            {
                context.DeferFact(new SimulationFact("adjust", 0, targetId));
            }
        }
    }
}
