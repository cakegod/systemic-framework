using Systemic.Host;
using Systemic.Kernel;
using Systemic.Rules;

namespace Systemic.Tests.Kernel;

public sealed class RangeReductionScheduleTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void Reduction_uses_fixed_logical_partitions_and_merges_them_in_index_order(int workers)
    {
        string? result = null;
        using var schedule = new DeterministicSystemScheduleBuilder(workers)
            .Add(new TestSystem(context => result = context.ReduceRanges(11, 11, 4, "",
                static (_, partition, range) =>
                {
                    Thread.Sleep((3 - partition) * 2);
                    return $"{partition}:{range.Start}-{range.End};";
                }, static (left, right) => left + right)))
            .Build();

        schedule.Execute(new World(1, 1), 1);

        Assert.Equal("0:0-2;1:2-5;2:5-8;3:8-11;", result);
    }

    [Fact]
    public void Standalone_reduction_uses_the_scheduled_partition_boundaries()
    {
        var world = new World(1, 1);
        var context = new SystemicSystemContext(world, new RuleEngine(new CompiledRuleSet([])), tick: 1);

        var result = context.ReduceRanges(0, 11, 4, "", static (_, partition, range) =>
            $"{partition}:{range.Start}-{range.End};", static (left, right) => left + right);

        Assert.Equal("0:0-2;1:2-5;2:5-8;3:8-11;", result);
    }

    [Fact]
    public void Empty_reduction_returns_the_identity_without_calling_the_mapper()
    {
        var calls = 0;
        var result = 0;
        using var schedule = new DeterministicSystemScheduleBuilder(4)
            .Add(new TestSystem(context => result = context.ReduceRanges(0, 0, 4, 42,
                (_, _, _) => { calls++; return 5; }, static (left, right) => left + right)))
            .Build();

        schedule.Execute(new World(1, 1), 1);

        Assert.Equal(42, result);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Failed_partition_skips_merge_and_rolls_back_all_partition_writes()
    {
        var world = new World(1, 1);
        var population = world.DefinePopulation<Kind>();
        var key = new PopulationColumnKey<int>("reduction.values", 1, PopulationCodecs.Int32);
        var column = population.Column(key);
        population.AddRange(8);
        var merges = 0;
        using var schedule = new DeterministicSystemScheduleBuilder(4)
            .Add(new TestSystem(context => context.ReduceRanges(column, 8, 4, 0,
                static (values, partition, range) =>
                {
                    values.EditRange(range.Start, range.Length).Fill(partition + 1);
                    if (partition is 1 or 2) throw new InvalidOperationException($"partition {partition}");
                    return range.Length;
                }, (left, right) => { merges++; return left + right; })))
            .Build();

        var error = Assert.Throws<InvalidOperationException>(() => schedule.Execute(world, 1));

        Assert.Equal("partition 1", error.Message);
        Assert.Equal(0, merges);
        Assert.Equal(new int[8], column.ReadOnlySpan.ToArray());
    }

    [Fact]
    public void Concurrent_systems_reduce_every_logical_partition_without_nested_worker_dispatch()
    {
        var results = new int[2];
        var builder = new DeterministicSystemScheduleBuilder(2);
        for (var index = 0; index < results.Length; index++)
        {
            var systemIndex = index;
            builder.Add(new TestSystem(context => results[systemIndex] = context.ReduceRanges(0, 16, 16, 0,
                static (_, _, range) => range.Length, static (left, right) => left + right),
                $"reduce-{index}", [SystemAccess.ReadResource($"independent-{index}")]));
        }

        using var schedule = builder.Build();
        schedule.Execute(new World(1, 1), 1);

        Assert.Equal(new[] { 16, 16 }, results);
    }

    private readonly struct Kind : IPopulationKind
    {
        public static string Id => "test.range-reduction";
        public static int Version => 1;
    }

    private sealed class TestSystem(Action<ScheduledSystemContext> execute, string id = "reduce",
        IReadOnlyList<SystemAccess>? access = null) : IScheduledKernelSystem
    {
        public string Id => id;
        public IReadOnlyList<SystemAccess> Access { get; } = access ?? [SystemAccess.WriteWorld];
        public void Execute(ScheduledSystemContext context) => execute(context);
    }
}
