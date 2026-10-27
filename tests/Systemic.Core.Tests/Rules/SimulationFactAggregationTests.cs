using Systemic.Rules;

namespace Systemic.Tests.Rules;

public sealed class SimulationFactAggregationTests
{
    [Fact]
    public void Aggregates_only_selected_types_by_floor_divided_region_in_stable_order()
    {
        var aggregator = new SimulationFactAggregator([new FactTypeId("hit"), new FactTypeId("spark")],
            regionWidth: 4, regionHeight: 4, maxFacts: 10, maxGroups: 5);
        Assert.Equal(FactAggregationResult.Added, aggregator.TryAdd(Fact("spark", 1, 1, 5)));
        Assert.Equal(FactAggregationResult.Added, aggregator.TryAdd(Fact("hit", 1, 1, 2)));
        Assert.Equal(FactAggregationResult.Added, aggregator.TryAdd(Fact("hit", 3, 1, 3)));
        Assert.Equal(FactAggregationResult.Added, aggregator.TryAdd(Fact("hit", -1, -1, 7)));
        Assert.Equal(FactAggregationResult.Added, aggregator.TryAdd(Fact("hit", -4, -4, 11)));
        Assert.Equal(FactAggregationResult.Added, aggregator.TryAdd(Fact("hit", -5, 0, 13)));
        Assert.Equal(FactAggregationResult.TypeNotConfigured, aggregator.TryAdd(Fact("move", 1, 1, 100)));
        Assert.Equal(FactAggregationResult.MissingPosition, aggregator.TryAdd(new SimulationFact("hit", 0, 0)));

        var batch = aggregator.Snapshot();
        Assert.Equal(new[]
        {
            new SimulationFactAggregate(new FactTypeId("hit"), new(-1, -1), 2, 18),
            new SimulationFactAggregate(new FactTypeId("hit"), new(-2, 0), 1, 13),
            new SimulationFactAggregate(new FactTypeId("hit"), new(0, 0), 2, 5),
            new SimulationFactAggregate(new FactTypeId("spark"), new(0, 0), 1, 5)
        }, batch);
        Assert.Equal(6, aggregator.Count);
        Assert.Equal(batch, aggregator.Drain());
        Assert.Empty(aggregator.Snapshot());
        Assert.Equal(0, aggregator.Count);
        Assert.Equal(4, batch.Length);
    }

    [Fact]
    public void Limits_and_invalid_inputs_are_reported_without_changing_the_batch()
    {
        var aggregator = new SimulationFactAggregator([new FactTypeId("hit")], 4, 4, maxFacts: 2, maxGroups: 1);
        Assert.Equal(FactAggregationResult.InvalidFact,
            aggregator.TryAdd(new SimulationFact("hit", 0, 0, Amount: double.NaN, Position: new(0, 0))));
        Assert.Equal(FactAggregationResult.Added, aggregator.TryAdd(Fact("hit", 0, 0, 1)));
        Assert.Equal(FactAggregationResult.GroupLimitReached, aggregator.TryAdd(Fact("hit", 4, 0, 100)));
        Assert.Equal(FactAggregationResult.Added, aggregator.TryAdd(Fact("hit", 1, 1, 2)));
        Assert.Equal(FactAggregationResult.FactLimitReached, aggregator.TryAdd(Fact("hit", 0, 0, 100)));
        Assert.Equal(new SimulationFactAggregate(new("hit"), new(0, 0), 2, 3), Assert.Single(aggregator.Snapshot()));
        aggregator.Clear();
        Assert.Equal(FactAggregationResult.Added, aggregator.TryAdd(Fact("hit", 4, 0, 4)));
    }

    [Fact]
    public void Amounts_are_summed_exactly_before_rounding_and_are_independent_of_input_order()
    {
        var first = new SimulationFactAggregator([new FactTypeId("hit")], 1, 1, 8, 1);
        var second = new SimulationFactAggregator([new FactTypeId("hit")], 1, 1, 8, 1);
        foreach (var amount in new[] { 1e16, 1d, -1e16 })
            Assert.Equal(FactAggregationResult.Added, first.TryAdd(Fact("hit", 0, 0, amount)));
        foreach (var amount in new[] { 1d, -1e16, 1e16 })
            Assert.Equal(FactAggregationResult.Added, second.TryAdd(Fact("hit", 0, 0, amount)));

        Assert.Equal(1, Assert.Single(first.Snapshot()).TotalAmount);
        Assert.Equal(first.Snapshot(), second.Snapshot());
    }

    [Fact]
    public void Amount_rounding_is_ties_to_even_and_overflow_does_not_destroy_the_batch()
    {
        var small = new SimulationFactAggregator([new FactTypeId("hit")], 1, 1, 4, 1);
        small.TryAdd(Fact("hit", 0, 0, 1));
        small.TryAdd(Fact("hit", 0, 0, Math.ScaleB(1, -53)));
        Assert.Equal(1d, Assert.Single(small.Snapshot()).TotalAmount);
        small.TryAdd(Fact("hit", 0, 0, Math.ScaleB(1, -52)));
        Assert.Equal(Math.BitIncrement(Math.BitIncrement(1d)), Assert.Single(small.Snapshot()).TotalAmount);

        var large = new SimulationFactAggregator([new FactTypeId("hit")], 1, 1, 4, 1);
        large.TryAdd(Fact("hit", 0, 0, double.MaxValue));
        large.TryAdd(Fact("hit", 0, 0, double.MaxValue));
        Assert.Throws<OverflowException>(() => large.Drain());
        Assert.Equal(2, large.Count);
        large.TryAdd(Fact("hit", 0, 0, -double.MaxValue));
        Assert.Equal(double.MaxValue, Assert.Single(large.Drain()).TotalAmount);
    }

    [Fact]
    public void Invalid_aggregation_configuration_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new SimulationFactAggregator([], 1, 1, 1, 1));
        Assert.Throws<ArgumentException>(() => new SimulationFactAggregator([default], 1, 1, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SimulationFactAggregator([new("hit")], 0, 1, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SimulationFactAggregator([new("hit")], 1, -1, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SimulationFactAggregator([new("hit")], 1, 1, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SimulationFactAggregator([new("hit")], 1, 1, 1, 0));
    }

    private static SimulationFact Fact(string type, long x, long y, double amount) =>
        new(type, 0, 0, Amount: amount, Position: new SimulationFactPosition(x, y));
}
