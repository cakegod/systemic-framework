using GlowGarden;
using Xunit;

public class CommandLineTests
{
    [Theory]
    [InlineData("--ticks", "0")]
    [InlineData("--ticks", "-1")]
    [InlineData("--ticks", "no")]
    [InlineData("--seed", "2147483648")]
    [InlineData("--unknown", "1")]
    public void InvalidValuesAreRejected(string flag, string value) =>
        Assert.Throws<ArgumentException>(() => GardenOptions.Parse(["--headless", flag, value]));

    [Fact]
    public void MissingAndConflictingOptionsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => GardenOptions.Parse(["--seed"]));
        Assert.Throws<ArgumentException>(() => GardenOptions.Parse(["--headless", "--verify-render"]));
        Assert.Throws<ArgumentException>(() => GardenOptions.Parse(["--ticks", "100"]));
    }

    [Fact]
    public void RestartDiscardsProgressAndRecreatesSeededWorld()
    {
        using var session = new GardenSession(42);
        var original = session.Host;
        var plants = original.Simulation.Plants.ToArray();
        original.Simulation.Hire(CreatureKind.Burstling);
        original.Simulation.ToggleTalent(Talent.Echo);
        original.Step(100);
        session.Restart();
        Assert.NotSame(original, session.Host);
        Assert.Equal(0, session.Host.Simulation.Tick);
        Assert.Equal(30, session.Host.Simulation.Glow);
        Assert.Equal(GardenSimulation.StartingWorkers, session.Host.Simulation.Creatures.Count);
        Assert.Equal(0, session.Host.Simulation.ActiveTalentCount);
        Assert.Equal(plants, session.Host.Simulation.Plants);
    }
}
