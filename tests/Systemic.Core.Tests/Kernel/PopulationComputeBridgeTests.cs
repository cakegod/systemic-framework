using Systemic.Kernel;

namespace Systemic.Tests.Kernel;

public sealed class PopulationComputeBridgeTests
{
    [Fact]
    public async Task Reference_backend_moves_agents_and_reduces_positions_in_stable_integer_units()
    {
        IPopulationComputeBackend backend = new CpuPopulationComputeBackend();
        PopulationComputeAgent[] input =
        [
            new(new((Fixed64)1, (Fixed64)2), new((Fixed64)3, (Fixed64)(-2))),
            new(new((Fixed64)(-1), (Fixed64)4), new((Fixed64)1, (Fixed64)2))
        ];
        var output = new PopulationComputeAgent[2];

        var aggregate = await backend.IntegrateAsync(input, output, (Fixed64)0.5m);

        Assert.Equal(new FixedVector2((Fixed64)2.5m, (Fixed64)1), output[0].Position);
        Assert.Equal(new FixedVector2((Fixed64)(-0.5m), (Fixed64)5), output[1].Position);
        Assert.Equal(2, aggregate.Count);
        Assert.Equal((Int128)((Fixed64)2).RawValue, aggregate.SumXRaw);
        Assert.Equal((Int128)((Fixed64)6).RawValue, aggregate.SumYRaw);
        Assert.Equal(new FixedVector2((Fixed64)1, (Fixed64)2), input[0].Position);
    }

    [Fact]
    public async Task Reference_backend_preserves_destination_on_failure_and_supports_overlapping_buffers()
    {
        var backend = new CpuPopulationComputeBackend();
        var overflow = new[] { new PopulationComputeAgent(new(Fixed64.FromRaw(long.MaxValue), Fixed64.Zero),
            new(Fixed64.One, Fixed64.Zero)) };
        var destination = new[] { new PopulationComputeAgent(new(Fixed64.One, Fixed64.One), default) };
        var before = destination[0];
        await Assert.ThrowsAsync<OverflowException>(async () =>
            await backend.IntegrateAsync(overflow, destination, Fixed64.One));
        Assert.Equal(before, destination[0]);

        var values = new[]
        {
            new PopulationComputeAgent(new((Fixed64)1, Fixed64.Zero), new(Fixed64.One, Fixed64.Zero)),
            new PopulationComputeAgent(new((Fixed64)2, Fixed64.Zero), new(Fixed64.One, Fixed64.Zero)),
            default
        };
        await backend.IntegrateAsync(values.AsMemory(0, 2), values.AsMemory(1, 2), Fixed64.One);
        Assert.Equal((Fixed64)2, values[1].Position.X);
        Assert.Equal((Fixed64)3, values[2].Position.X);
    }

    [Fact]
    public void Stable_lod_assigns_each_agent_exactly_one_tick_per_period()
    {
        for (ulong id = 0; id < 200; id++)
        {
            Assert.Single(Enumerable.Range(0, 7).Where(tick => FixedUpdateLod.ShouldUpdate(id, tick, 7)));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => FixedUpdateLod.ShouldUpdate(0, 0, 0));
    }
}
