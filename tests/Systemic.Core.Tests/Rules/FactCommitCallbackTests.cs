using Systemic.Kernel;
using Systemic.Rules;

namespace Systemic.Tests.Rules;

public sealed class FactCommitCallbackTests
{
    [Fact]
    public void FactEmitterRunsCommitCallbacksOnlyAfterSuccessfulOuterDispatch()
    {
        var rules = new RuleEngine(new CompiledRuleSet([]));
        var world = new World(seed: 1, valueScale: 1);
        var committedCallbacks = 0;

        var committed = rules.Dispatch(world, new SimulationFact("ok", 0, 0),
            (_, emitter) => Assert.True(emitter.OnCommit(() => committedCallbacks++)));

        Assert.False(committed.RolledBack);
        Assert.Equal(1, committedCallbacks);

        var rolledBack = rules.Dispatch(world, new SimulationFact("root", 0, 0), (_, emitter) =>
        {
            Assert.True(emitter.OnCommit(() => committedCallbacks++));
            Assert.False(emitter.Enqueue(new SimulationFact("invalid", -1, 0)));
        });

        Assert.True(rolledBack.RolledBack);
        Assert.Equal(1, committedCallbacks);
    }
}
