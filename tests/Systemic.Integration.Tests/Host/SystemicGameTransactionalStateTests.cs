using System.Collections.Immutable;
using System.Text.Json;
using Systemic.Content;
using Systemic.Host;
using Systemic.Kernel;
using Systemic.Rules;

namespace Systemic.Tests.Host;

public sealed class SystemicGameTransactionalStateTests
{
    [Fact]
    public void Failed_tick_restores_registered_system_state_automatically()
    {
        var content = new ContentPack("", new GameManifest("test", "Test", 30, 4),
            new CompiledRuleSet([]), new CompiledEntities(ImmutableArray<EntityDefinition>.Empty));
        var system = new StatefulSystem();
        using var game = new SystemicGameBuilder(content, new World(7, 1))
            .AddSystem(system).AddSystem(new FailingSystem()).Build();

        Assert.Throws<InvalidOperationException>(game.Step);
        Assert.Equal(0, game.World.LogicalTick);
        Assert.Equal(0, system.Value);
    }

    [Fact]
    public void Host_reports_failing_system_and_keeps_bounded_timing_and_budget_statistics()
    {
        var content = new ContentPack("", new GameManifest("test", "Test", 30, 4),
            new CompiledRuleSet([]), new CompiledEntities(ImmutableArray<EntityDefinition>.Empty));
        var system = new StatefulSystem();
        using var game = new SystemicGameBuilder(content, new World(7, 1))
            .AddSystem(system).AddSystem(new FailingSystem()).Build();
        game.TickStats.SetBudget("stateful", TimeSpan.FromTicks(1));

        var result = game.TryStep();

        Assert.False(result.Succeeded);
        Assert.Equal("failure", result.Failure!.SystemId);
        Assert.Equal(1, result.Failure.Tick);
        Assert.False(result.Failure.Committed);
        Assert.Equal(0, system.Value);
        var measured = Assert.Single(game.TickStats.Snapshot(), sample => sample.SystemId == "stateful");
        Assert.Equal(1, measured.Count);
        Assert.True(measured.Max >= measured.Last);
        Assert.True(measured.AllocatedBytes >= 0);
    }

    [Fact]
    public void Rollback_failure_preserves_the_original_system_failure()
    {
        var content = new ContentPack("", new GameManifest("test", "Test", 30, 4),
            new CompiledRuleSet([]), new CompiledEntities(ImmutableArray<EntityDefinition>.Empty));
        using var game = new SystemicGameBuilder(content, new World(7, 1))
            .RegisterStatefulExtension(new BadRestore()).AddSystem(new FailingSystem()).Build();

        var failure = Assert.Throws<AggregateException>(game.Step).Flatten();

        Assert.Contains(failure.InnerExceptions, error => error.Message == "failure after mutation");
        Assert.Contains(failure.InnerExceptions, error => error.Message == "restore failed");
    }

    private sealed class StatefulSystem : ISystemicSystem, ISystemicStatefulExtension
    {
        public string Id => "stateful";
        public int Order => 1;
        public int Value { get; private set; }
        public void Step(SystemicSystemContext context) => Value++;
        public JsonElement CaptureState() => JsonSerializer.SerializeToElement(Value);
        public void RestoreState(JsonElement state) => Value = state.GetInt32();
    }

    private sealed class FailingSystem : ISystemicSystem
    {
        public string Id => "failure";
        public int Order => 2;
        public void Step(SystemicSystemContext context) => throw new InvalidOperationException("failure after mutation");
    }

    private sealed class BadRestore : ISystemicStatefulExtension
    {
        public string Id => "bad-restore";
        public JsonElement CaptureState() => JsonSerializer.SerializeToElement(1);
        public void RestoreState(JsonElement state) => throw new InvalidOperationException("restore failed");
    }
}
