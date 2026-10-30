using System.Collections.Immutable;
using System.Diagnostics;
using Systemic.Kernel;
using Systemic.Presentation;
using Systemic.Rules;
using Systemic.Ui;
using Xunit.Abstractions;

namespace Systemic.Tests.Presentation;

public sealed class DeclarativeUiEvaluationBudgetTests(ITestOutputHelper output)
{
    private const int BindingGroups = 12;
    private const int WarmupCount = 4;
    private const int SampleCount = 100;
    private const int EvaluationsPerSample = 8;
    private readonly ITestOutputHelper _output = output;

    [Fact]
    [Trait("Category", "Performance")]
    public void RepeatedNestedUiEvaluationReportsReleaseCostWithoutTimingGate()
    {
        var world = new World(seed: 88, valueScale: 1) { LogicalTick = 23 };
        var shortEntity = world.Add(new DataEntity([], [], ("ore.vein.balance", 2)));
        var longEntity = world.Add(new DataEntity(["material"], [], ("balance", 21)));
        var entityIds = ImmutableDictionary<string, int>.Empty
            .Add("quarry", shortEntity.Id)
            .Add("quarry.ore.vein", longEntity.Id);
        var rows = ImmutableArray.CreateBuilder<UiNode>(BindingGroups);
        for (var group = 0; group < BindingGroups; group++)
        {
            rows.Add(new UiNode("row", null, null, null,
            [
                new UiNode("value", null, "world.quarry.ore.vein.balance", null, []),
                new UiNode("value", null, "world.quarry.ore.vein.tag.material", null, []),
                new UiNode("value", null, "world.tick", null, []),
                new UiNode("value", null, "selection.energy", null, []),
                new UiNode("value", null, "event.type", null, []),
                new UiNode("value", null, "module.energy", null, [])
            ]));
        }

        var layout = new UiLayout("nested", new UiNode("column", null, null, null, rows.MoveToImmutable()));
        var context = new UiBindingContext(world, entityIds,
            ImmutableDictionary<string, double>.Empty.Add("energy", 3),
            new SimulationFact("contact", longEntity.Id, shortEntity.Id, 2), [new FixedUiProvider()]);
        var initial = DeclarativeUiEvaluator.Evaluate(layout, context);
        Assert.Equal(21, initial.Children[0].Children[0].NumberValue);
        Assert.Equal(1, initial.Children[0].Children[1].NumberValue);
        Assert.Equal(23, initial.Children[0].Children[2].NumberValue);
        Assert.Equal(3, initial.Children[0].Children[3].NumberValue);
        Assert.Equal("contact", initial.Children[0].Children[4].TextValue);
        Assert.Equal(5, initial.Children[0].Children[5].NumberValue);

        UiNodeState frame = initial;
        void EvaluateBatch()
        {
            for (var index = 0; index < EvaluationsPerSample; index++)
            {
                frame = DeclarativeUiEvaluator.Evaluate(layout, context);
            }
        }

        for (var warmup = 0; warmup < WarmupCount; warmup++)
        {
            EvaluateBatch();
        }

        var timings = new double[SampleCount];
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var sample = 0; sample < timings.Length; sample++)
        {
            var started = Stopwatch.GetTimestamp();
            EvaluateBatch();
            timings[sample] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Array.Sort(timings);
        var medianIndex = (int)Math.Ceiling(timings.Length * 0.50) - 1;
        var p95Index = (int)Math.Ceiling(timings.Length * 0.95) - 1;
        Assert.Equal(21, frame.Children[0].Children[0].NumberValue);
        _output.WriteLine($"Nested layout groups={BindingGroups}, bindings={BindingGroups * 6}, "
            + $"warmup={WarmupCount}, samples={SampleCount}, evaluations/sample={EvaluationsPerSample}; "
            + $"p50/p95={timings[medianIndex]:F3}/{timings[p95Index]:F3} ms; allocations={allocatedBytes} B total "
            + $"({allocatedBytes / (SampleCount * EvaluationsPerSample)} B/evaluation).");
    }

    private sealed class FixedUiProvider : ISystemicUiBindingProvider
    {
        public bool TryResolve(string binding, SystemicUiBindingContext context, out SystemicUiBindingValue value)
        {
            value = binding == "module.energy" ? SystemicUiBindingValue.FromNumber(5) : default;
            return value.HasValue;
        }
    }
}
