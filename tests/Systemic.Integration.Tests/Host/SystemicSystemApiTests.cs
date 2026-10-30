using Systemic.Host;
using Systemic.Kernel;
using Systemic.Rules;

namespace Systemic.Tests.Host;

public sealed class SystemicSystemApiTests
{
    [Fact]
    public void OneSystemApiDefaultsToExclusiveAccessAndPreservesOrderRegistrationOrder()
    {
        var content = SystemicTestContent.Load(Path.Combine(AppContext.BaseDirectory, "samples", "incremental-quarry"))
            with
        { Modules = [] };
        var world = new World(seed: 1, valueScale: 1);
        var target = world.Add(new DataEntity([], [], ("value", 0)));
        var service = new TestService();
        var order = new List<string>();
        var visits = new List<int>();
        var first = new TestSystem("first", context =>
        {
            order.Add("first");
            Assert.Same(service, context.GetService<TestService>());
            Assert.True(context.World.TryGetProperty(target.Id, "value", out _));
            context.Commands.SetProperty(target.Id, "value", 7);
            context.Publish(new SimulationFact("test.tick", target.Id, target.Id));
            context.ForEachRange(visits, 4, static (rows, _, range) =>
            {
                for (var row = range.Start; row < range.End; row++)
                {
                    lock (rows)
                    {
                        rows.Add(row);
                    }
                }
            });
        });
        var second = new TestSystem("second", _ => order.Add("second"));

        using var game = new SystemicGameBuilder(content, world)
            .AddService(service)
            .AddSystem(first)
            .AddSystem(second)
            .WithScheduledWorkerCount(2)
            .Build();

        game.Step();

        Assert.Equal(["first", "second"], game.SchedulerWaves.SelectMany(wave => wave));
        Assert.Equal(["first", "second"], order);
        Assert.Equal([0, 1, 2, 3], visits.Order());
        Assert.Equal(7, world.EffectiveProperty(target, "value"));
    }

    [Fact]
    public void UnifiedContextRollbackRegistrationIsHonoredWhenALaterSystemFails()
    {
        var content = SystemicTestContent.Load(Path.Combine(AppContext.BaseDirectory, "samples", "incremental-quarry"))
            with
        { Modules = [] };
        var world = new World(seed: 1, valueScale: 1);
        var target = world.Add(new DataEntity([], [], ("value", 0)));
        var externalValue = 0;
        using var game = new SystemicGameBuilder(content, world)
            .AddSystem(new TestSystem("first", context =>
            {
                context.OnRollback(() => externalValue = 0);
                externalValue = 3;
                context.Commands.SetProperty(target.Id, "value", 9);
            }))
            .AddSystem(new TestSystem("failing", _ => throw new InvalidOperationException("expected")))
            .Build();

        Assert.Throws<InvalidOperationException>(game.Step);

        Assert.Equal(0, externalValue);
        Assert.Equal(0, world.EffectiveProperty(target, "value"));
        Assert.Equal(0, world.LogicalTick);
    }

    private sealed class TestService { }

    private sealed class TestSystem(string id, Action<SystemicSystemContext> execute) : ISystemicSystem
    {
        public string Id { get; } = id;

        public void Step(SystemicSystemContext context) => execute(context);
    }
}
