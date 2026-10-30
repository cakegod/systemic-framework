using Systemic.Kernel;

namespace Systemic.Tests.Kernel;

public sealed class WorldWriteScheduleTests
{
    private static readonly int[] s_expectedRows = [1, 1, 1, 1];

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void WriteWorldRunsAloneEvenWhenTheOtherSystemDoesNotAccessWorld(
        bool worldFirst, bool otherHasAccess)
    {
        using var secondStarted = new ManualResetEventSlim();
        var active = 0;
        var overlapping = 0;
        void Start()
        {
            if (Interlocked.Increment(ref active) != 1)
            {
                Interlocked.Exchange(ref overlapping, 1);
            }
        }

        var otherAccess = otherHasAccess ? new[] { SystemAccess.WriteResource("independent") } : [];
        var firstAccess = worldFirst ? new[] { SystemAccess.WriteWorld } : otherAccess;
        var secondAccess = worldFirst ? otherAccess : new[] { SystemAccess.WriteWorld };
        var builder = new DeterministicSystemScheduleBuilder(2);
        builder.Add(new TestSystem("first", firstAccess, _ =>
        {
            Start();
            secondStarted.Wait(TimeSpan.FromMilliseconds(250));
            Interlocked.Decrement(ref active);
        }));
        builder.Add(new TestSystem("second", secondAccess, _ =>
        {
            Start();
            secondStarted.Set();
            Interlocked.Decrement(ref active);
        }));

        using var schedule = builder.Build();
        schedule.Execute(new World(1, 1), 1);

        Assert.Equal(0, overlapping);
    }

    [Fact]
    public void WriteWorldSystemCanStillUseAllWorkersForRanges()
    {
        using var barrier = new Barrier(4);
        var rows = new int[4];
        using var schedule = new DeterministicSystemScheduleBuilder(4)
            .Add(new TestSystem("world", [SystemAccess.WriteWorld], context =>
                context.ForEachRange((rows, barrier), rows.Length, static (state, _, range) =>
                {
                    Assert.True(state.barrier.SignalAndWait(TimeSpan.FromSeconds(5)));
                    state.rows[range.Start] = 1;
                })))
            .Add(new TestSystem("other", [], _ => { }))
            .Build();

        schedule.Execute(new World(1, 1), 1);

        Assert.Equal(s_expectedRows, rows);
    }

    private sealed class TestSystem(string id, IReadOnlyList<SystemAccess> access,
        Action<ScheduledSystemContext> execute) : IScheduledKernelSystem
    {
        public string Id => id;
        public IReadOnlyList<SystemAccess> Access => access;
        public void Execute(ScheduledSystemContext context) => execute(context);
    }
}
