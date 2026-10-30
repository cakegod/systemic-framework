using Systemic.Rules;

namespace Systemic.Tests.Rules;

public sealed class ExpressionCompilerTests
{
    [Fact]
    public void CompilesCountArithmeticAndFloorFormulas()
    {
        var scope = new TestExpressionScope(counts: new Dictionary<string, double>
        {
            ["units.employed"] = 19,
        });

        var expression = ExpressionCompiler.Compile("floor(count(\"units.employed\") / 4) * 0.1");

        Assert.Equal(0.4, expression.EvaluateNumber(scope), precision: 12);
    }

    [Fact]
    public void CompilesRollingRatesAndBooleanComparisons()
    {
        var scope = new TestExpressionScope(rates: new Dictionary<(string, int), double>
        {
            [("collection", 60)] = 8,
            [("damage", 60)] = 20,
        });

        var expression = ExpressionCompiler.Compile(
            "rate(\"collection\", 60) < rate(\"damage\", 60) * 0.5 && event_count(\"impact\", 10) >= 2");
        scope.EventCounts[("impact", 10)] = 3;

        Assert.True(expression.EvaluateBoolean(scope));
    }

    [Theory]
    [InlineData("1 +")]
    [InlineData("System.IO.File.Delete(\"save.json\")")]
    [InlineData("count(\"workers\", 1, 2)")]
    public void RejectsUnsupportedOrMalformedExpressionSyntax(string source)
    {
        var error = Assert.Throws<ExpressionCompileException>(() => ExpressionCompiler.Compile(source));

        Assert.InRange(error.Position, 0, source.Length);
    }

    [Fact]
    public void RejectsNonFiniteEvaluationResults()
    {
        var expression = ExpressionCompiler.Compile("amount / 0");
        var scope = new TestExpressionScope(numbers: new Dictionary<string, double>
        {
            ["amount"] = 12,
        });

        Assert.Throws<ExpressionEvaluationException>(() => expression.EvaluateNumber(scope));
    }

    [Theory]
    [InlineData("false && 1 / 0 > 0", false)]
    [InlineData("true || 1 / 0 > 0", true)]
    public void BooleanOperatorsShortCircuit(string source, bool expected)
    {
        var expression = ExpressionCompiler.Compile(source);

        Assert.Equal(expected, expression.EvaluateBoolean(new TestExpressionScope()));
    }

    [Fact]
    public void RejectsExpressionsThatExceedTheSourceLengthLimit()
    {
        var source = "1" + new string(' ', 4096);

        Assert.Throws<ExpressionCompileException>(() => ExpressionCompiler.Compile(source));
    }

    private sealed class TestExpressionScope(
        IReadOnlyDictionary<string, double>? numbers = null,
        IReadOnlyDictionary<string, double>? counts = null,
        IReadOnlyDictionary<(string, int), double>? rates = null) : IExpressionScope
    {
        public Dictionary<(string, int), double> EventCounts { get; } = [];

        public double ReadNumber(string name) => numbers is not null && numbers.TryGetValue(name, out var value)
            ? value
            : throw new ExpressionEvaluationException($"Unknown number '{name}'.");

        public double Count(string selector) => counts is not null && counts.TryGetValue(selector, out var value)
            ? value
            : throw new ExpressionEvaluationException($"Unknown count selector '{selector}'.");

        public double Rate(string selector, int windowTicks) => rates is not null
            && rates.TryGetValue((selector, windowTicks), out var value)
                ? value
                : throw new ExpressionEvaluationException($"Unknown rate selector '{selector}'.");

        public double EventCount(string eventType, int windowTicks) => EventCounts.TryGetValue((eventType, windowTicks), out var value)
            ? value
            : 0;
    }
}
