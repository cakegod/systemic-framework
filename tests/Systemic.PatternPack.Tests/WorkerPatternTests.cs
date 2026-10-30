using PatternPack;
using Systemic.Content;
using Systemic.Host;
using Systemic.Kernel;
using Systemic.Rules;
using Xunit;

namespace Systemic.Tests.Patterns;

public sealed class WorkerPatternTests
{
    [Fact]
    public void EveryWorkerPaysOutThroughOneRuleAndNoWorkersPayNothing()
    {
        var content = ContentPackLoader.Load(Path.Combine(AppContext.BaseDirectory, "samples", "pattern-pack"));
        var materialized = content.CreateWorld(seed: 9, valueScale: 1);
        var world = materialized.World;
        var engine = new RuleEngine(content.Rules);
        var lead = materialized.EntityIds["pattern.worker.lead"];
        var ore = materialized.EntityIds["pattern.ore"];

        // A population is defined once per world, so the empty case gets its own world.
        var idleWorld = content.CreateWorld(seed: 10, valueScale: 1).World;
        var idle = new WorkerPatternSystem(idleWorld, lead, ore, workers: 0);
        idle.Step(new SystemicSystemContext(idleWorld, engine, 1));
        Assert.Equal(0, idleWorld.EffectiveProperty(idleWorld.ById(ore)!, "balance"));

        var workers = new WorkerPatternSystem(world, lead, ore, workers: 3);
        workers.Step(new SystemicSystemContext(world, engine, 2));

        Assert.Equal(3, workers.Workers);
        Assert.Equal(3, world.EffectiveProperty(world.ById(ore)!, "balance"));
    }
}
