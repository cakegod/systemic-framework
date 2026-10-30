using PatternPack;
using Systemic.Content;
using Systemic.Host;
using Systemic.Kernel;
using Systemic.Rules;
using Xunit;

namespace Systemic.Tests.Patterns;

public sealed class WorkerPatternTests
{
    private static (ContentPack Content, MaterializedContentWorld Materialized, World World, RuleEngine Engine) Arrange(int seed)
    {
        var content = ContentPackLoader.Load(Path.Combine(AppContext.BaseDirectory, "samples", "pattern-pack"));
        var materialized = content.CreateWorld(seed: seed, valueScale: 1);
        return (content, materialized, materialized.World, new RuleEngine(content.Rules));
    }

    private static WorkerPatternSystem Workers(
        (ContentPack Content, MaterializedContentWorld Materialized, World World, RuleEngine Engine) arranged,
        int workers) =>
        new(arranged.World, workers);

    private static void Step(WorkerPatternSystem system,
        (ContentPack Content, MaterializedContentWorld Materialized, World World, RuleEngine Engine) arranged,
        int tick) =>
        system.Step(new SystemicSystemContext(arranged.World, arranged.Engine, tick));

    [Fact]
    public void EveryLiveWorkerCreditsItsOwnRowAndNoWorkersCreditNothing()
    {
        var idle = Arrange(seed: 10);
        var idleSystem = Workers(idle, workers: 0);
        Step(idleSystem, idle, 1);

        Assert.Equal(0, idleSystem.Workers);

        var working = Arrange(seed: 9);
        var system = Workers(working, workers: 3);

        Step(system, working, 1);
        Step(system, working, 2);

        // Each worker has its own column value, which only a row-addressed rule can produce.
        Assert.Equal(3, system.Workers);
        Assert.Equal(2, system.Mined(0));
        Assert.Equal(2, system.Mined(1));
        Assert.Equal(2, system.Mined(2));
    }

    [Fact]
    public void RemovingAWorkerStopsItsCreditImmediately()
    {
        var arranged = Arrange(seed: 11);
        var system = Workers(arranged, workers: 3);

        Step(system, arranged, 1);
        Assert.Equal(1, system.Mined(2));

        Assert.True(system.RemoveWorker(0));
        Step(system, arranged, 2);

        // A captured row count would still publish a fact for a row that no longer exists.
        Assert.Equal(2, system.Workers);
        Assert.Equal(2, system.Mined(0));
        Assert.Equal(2, system.Mined(1));
    }

    [Fact]
    public void AWorkerWithoutItsTagIsNotCreditedBecauseContentReadsTheRowTag()
    {
        var arranged = Arrange(seed: 12);
        var system = Workers(arranged, workers: 2);

        Assert.True(system.StopWorker(1));
        Step(system, arranged, 1);

        Assert.Equal(2, system.Workers);
        Assert.Equal(1, system.Mined(0));
        Assert.Equal(0, system.Mined(1));
    }

    [Fact]
    public void AWorkFactWithoutAnAmountIsNotCredited()
    {
        var arranged = Arrange(seed: 13);
        var system = Workers(arranged, workers: 1);
        var population = arranged.World.GetPopulation<WorkerRow>();

        arranged.Engine.Dispatch(arranged.World,
            SimulationFact.ForRow("pattern.workers.worked", population.ReferenceOf(0), amount: 0));

        // The rule reads event.amount, so the fact's amount is part of the contract, not a constant
        // the system adds to satisfy an analyzer.
        Assert.Equal(0, system.Mined(0));
    }
}
