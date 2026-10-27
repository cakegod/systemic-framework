using Systemic.Kernel;
using Systemic.Rules;

namespace Systemic.Tests.Rules;

public sealed class TypedSimulationFactTests
{
    [Fact]
    public void Typed_identifier_matches_authored_rules_and_emitted_facts_keep_position_and_payload()
    {
        var payload = new SimulationFactPayload([
            new("variant", SimulationFactValue.FromText("spark")),
            new("strength", SimulationFactValue.FromInteger(4))
        ]);
        var initial = new SimulationFact(new FactTypeId("hit"), 1, 2, Amount: 3,
            Position: new SimulationFactPosition(17, -4), Payload: payload);
        var engine = new RuleEngine(new CompiledRuleSet([
            new CompiledRule("first", "hit", 0, [], [RuleEffect.Emit("chain")]),
            new CompiledRule("second", "chain", 0, [], [RuleEffect.Emit("done")])
        ]));

        var trace = engine.DispatchWithTrace(new World(seed: 1, valueScale: 1), initial);

        Assert.Equal(3, trace.Result.FactsProcessed);
        Assert.Equal(2, trace.Result.RulesMatched);
        Assert.Equal(new[] { "hit", "chain", "done" }, trace.Facts.Select(f => f.Fact.Type));
        Assert.Equal(new FactTypeId("hit"), trace.Facts[0].Fact.TypeId);
        Assert.Equal(new FactTypeId("chain"), trace.Facts[1].Fact.TypeId);
        Assert.All(trace.Facts, item =>
        {
            Assert.Equal(new SimulationFactPosition(17, -4), item.Fact.Position);
            Assert.Same(payload, item.Fact.Payload);
            Assert.Equal(3, item.Fact.Amount);
            Assert.Equal(1, item.Fact.SourceId);
            Assert.Equal(2, item.Fact.TargetId);
        });
        Assert.Equal(new[] { 0, 1, 2 }, trace.Facts.Select(f => f.Fact.Depth));
    }

    [Fact]
    public void Identifiers_are_validated_and_compare_ordinally()
    {
        Assert.Throws<ArgumentException>(() => new FactTypeId(" "));
        Assert.Throws<ArgumentNullException>(() => new FactTypeId(null!));
        Assert.NotEqual(new FactTypeId("hit"), new FactTypeId("Hit"));
        Assert.Equal(new FactTypeId("hit"), new SimulationFact("hit", 0, 0).TypeId);
        Assert.False(default(FactTypeId).IsValid);
    }

    [Fact]
    public void Payload_is_canonical_and_copies_the_input()
    {
        var values = new[]
        {
            new SimulationFactPayloadEntry("z", SimulationFactValue.FromText("fire")),
            new SimulationFactPayloadEntry("a", SimulationFactValue.FromInteger(7)),
            new SimulationFactPayloadEntry("flag", SimulationFactValue.FromBoolean(true)),
            new SimulationFactPayloadEntry("number", SimulationFactValue.FromNumber(2.5))
        };
        var payload = new SimulationFactPayload(values);
        values[0] = new("z", SimulationFactValue.FromText("changed"));

        Assert.Equal(new[] { "a", "flag", "number", "z" }, payload.Entries.Select(entry => entry.Key));
        Assert.True(payload.TryGetValue("z", out var text));
        Assert.Equal(SimulationFactValue.FromText("fire"), text);
        Assert.True(payload.TryGetValue("a", out var integer));
        Assert.Equal(7, integer.Integer);
        Assert.False(payload.TryGetValue("missing", out _));
    }

    [Fact]
    public void Payload_rejects_unbounded_or_invalid_values()
    {
        Assert.Throws<ArgumentException>(() => new SimulationFactPayload([
            new("same", SimulationFactValue.FromInteger(1)), new("same", SimulationFactValue.FromInteger(2))]));
        Assert.Throws<ArgumentException>(() => new SimulationFactPayload([new(" ", SimulationFactValue.FromInteger(1))]));
        Assert.Throws<ArgumentException>(() => new SimulationFactPayload([new(new string('k', 65), SimulationFactValue.FromInteger(1))]));
        Assert.Throws<ArgumentException>(() => new SimulationFactPayload([new("bad", default)]));
        Assert.Throws<ArgumentException>(() => new SimulationFactPayload(
            Enumerable.Range(0, 17).Select(i => new SimulationFactPayloadEntry(i.ToString(), SimulationFactValue.FromInteger(i)))));
        Assert.Throws<ArgumentOutOfRangeException>(() => SimulationFactValue.FromText(new string('x', 257)));
        Assert.Throws<ArgumentOutOfRangeException>(() => SimulationFactValue.FromNumber(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => SimulationFactValue.FromNumber(double.PositiveInfinity));
    }
}
