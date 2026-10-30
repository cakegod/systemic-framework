using System.Collections.Immutable;
using System.Diagnostics;
using Systemic.Kernel;
using Systemic.Rules;
using Xunit.Abstractions;

namespace Systemic.Tests.Performance;

public sealed class BuildChoiceRuntimeBudgetTests(ITestOutputHelper output)
{
    private const int GroupCount = 16;
    private const int ChoicesPerGroup = 64;
    private const int OwnersInMeasuredGroup = 64;
    private const int WarmupCount = 4;
    private const int SampleCount = 15;
    private const int RepeatsPerOwner = 3;
    private readonly ITestOutputHelper _output = output;

    [Fact]
    [Trait("Category", "Performance")]
    public void LargeCatalogRepeatedStateAndSelectionChecksReportReleaseCost()
    {
        var world = new World(seed: 86, valueScale: 1);
        var entityIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var groups = ImmutableArray.CreateBuilder<BuildChoiceGroupDefinition>(GroupCount);
        var choices = ImmutableArray.CreateBuilder<BuildChoiceDefinition>(GroupCount * ChoicesPerGroup);
        for (var groupIndex = 0; groupIndex < GroupCount; groupIndex++)
        {
            var groupId = $"group.{groupIndex:D2}";
            var ownerEntityId = $"owner.{groupIndex:D2}";
            var owner = world.Add(new DataEntity([], [], ("progress", 0)));
            entityIds.Add(ownerEntityId, owner.Id);
            groups.Add(new BuildChoiceGroupDefinition(groupId, ownerEntityId, ownerEntityId,
                "progress", 0, 2, null));

            for (var choiceIndex = 0; choiceIndex < ChoicesPerGroup; choiceIndex++)
            {
                var choiceId = $"choice.{groupIndex:D2}.{choiceIndex:D3}";
                var entityId = $"entity.{choiceId}";
                var entity = world.Add(new DataEntity([], [], ("selected", 0)));
                entityIds.Add(entityId, entity.Id);
                choices.Add(new BuildChoiceDefinition(choiceId, groupId, entityId,
                    ChoiceActivationMode.Passive, $"tag.{choiceId}", "selected", "activeUntil", "cooldownUntil",
                    null, 0, 0, 0, null, null, false));
            }
        }

        var runtime = new BuildChoiceRuntime(new BuildChoiceCatalog(groups.MoveToImmutable(), choices.MoveToImmutable()),
            incremental: null, world, entityIds);
        var measuredOwners = new List<int>(OwnersInMeasuredGroup) { entityIds["owner.00"] };
        for (var ownerIndex = 1; ownerIndex < OwnersInMeasuredGroup; ownerIndex++)
        {
            var owner = world.Add(new DataEntity([], [], ("progress", 0)));
            Assert.True(runtime.RegisterOwner("group.00", owner.Id, owner.Id).Accepted);
            measuredOwners.Add(owner.Id);
        }

        var publish = new Func<SimulationFact, RuleDispatchResult>(
            static _ => new RuleDispatchResult(1, 0, 0, false));
        foreach (var ownerId in measuredOwners)
        {
            Assert.True(runtime.SelectChoice("choice.00.000", ownerId, publish).Accepted);
            Assert.True(runtime.SelectChoice("choice.00.001", ownerId, publish).Accepted);
        }

        const string targetChoiceId = "choice.00.002";
        var invalidResults = 0;
        void CheckStateAndSelection()
        {
            foreach (var ownerId in measuredOwners)
            {
                for (var repeat = 0; repeat < RepeatsPerOwner; repeat++)
                {
                    if (!runtime.TryGetState(targetChoiceId, ownerId, out var state) || state.IsAvailable)
                    {
                        invalidResults++;
                    }

                    if (runtime.SelectChoice(targetChoiceId, ownerId, publish).Accepted)
                    {
                        invalidResults++;
                    }
                }
            }
        }

        for (var warmup = 0; warmup < WarmupCount; warmup++)
        {
            CheckStateAndSelection();
        }

        var timings = new double[SampleCount];
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var sample = 0; sample < timings.Length; sample++)
        {
            var started = Stopwatch.GetTimestamp();
            CheckStateAndSelection();
            timings[sample] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        var allocations = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Array.Sort(timings);
        Assert.Equal(0, invalidResults);
        _output.WriteLine($"Catalog choices={GroupCount * ChoicesPerGroup}, groups={GroupCount}, "
            + $"owners in measured group={OwnersInMeasuredGroup}, TryGetState + SelectChoice checks per sample="
            + $"{OwnersInMeasuredGroup * RepeatsPerOwner * 2}, warmup={WarmupCount}, samples={SampleCount}; "
            + $"p50/p95={timings[7]:F3}/{timings[14]:F3} ms; allocations={allocations} B total "
            + $"({allocations / SampleCount} B/sample).");
    }
}
