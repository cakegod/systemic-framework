using Systemic.Kernel;
using Systemic.Rules;

namespace Systemic.Tests.Rules;

/// <summary>
/// A sink is the difference between "one fact per row" and "one fact per tick": the count it reports
/// must be exact, and the number of facts must not follow the row count.
/// </summary>
public sealed class PopulationSinkTests
{
    private static readonly PopulationColumnKey<int> s_healthKey =
        new("test.sink.health", 1, PopulationCodecs.Int32);

    [Fact]
    public void AHundredThousandCrossingsBecomeOneFact()
    {
        var world = new World(seed: 101, valueScale: 1);
        var population = Create(world, 100_000, crossedHalf: 50_000);
        var sink = new PopulationSink(world, Crowd.Id, "test.sink.health", threshold: 0,
            "crowd.downed", population.ReferenceOf(0));
        var facts = new List<SimulationFact>();

        Assert.Equal(1, sink.Collect(facts));
        Assert.Single(facts);
        Assert.Equal(50_000, facts[0].Amount);
        Assert.Equal("crowd.downed", facts[0].Type);
        Assert.Equal(50_000, sink.Count);
    }

    [Fact]
    public void FactVolumeIsOneAtAThousandRowsAndAtAMillion()
    {
        var small = new World(seed: 102, valueScale: 1);
        var smallPopulation = Create(small, 1_000, crossedHalf: 500);
        var large = new World(seed: 103, valueScale: 1);
        var largePopulation = Create(large, 1_000_000, crossedHalf: 500_000);
        var smallFacts = new List<SimulationFact>();
        var largeFacts = new List<SimulationFact>();

        Assert.Equal(1, new PopulationSink(small, Crowd.Id, s_healthKey.Id, 0, "crowd.downed",
            smallPopulation.ReferenceOf(0)).Collect(smallFacts));
        Assert.Equal(1, new PopulationSink(large, Crowd.Id, s_healthKey.Id, 0, "crowd.downed",
            largePopulation.ReferenceOf(0)).Collect(largeFacts));
        Assert.Equal(500, smallFacts[0].Amount);
        Assert.Equal(500_000, largeFacts[0].Amount);
    }

    [Fact]
    public void AnUnchangedCountCostsNoFactAndAChangedOneReportsTheNewTotal()
    {
        var world = new World(seed: 104, valueScale: 1);
        var population = Create(world, 4, crossedHalf: 2);
        var sink = new PopulationSink(world, Crowd.Id, s_healthKey.Id, 0, "crowd.downed",
            population.ReferenceOf(0));
        var facts = new List<SimulationFact>();

        Assert.Equal(1, sink.Collect(facts));
        Assert.Equal(0, sink.Collect(facts));
        Assert.Single(facts);

        SetHealth(population, 0, -5);
        Assert.Equal(1, sink.Collect(facts));
        Assert.Equal(2, facts.Count);
        Assert.Equal(1, facts[1].Amount);
    }

    [Fact]
    public void ATagFilterCountsOnlyTaggedRows()
    {
        var world = new World(seed: 105, valueScale: 1);
        var population = Create(world, 6, crossedHalf: 6);
        population.AddTag(1, "poisoned");
        population.AddTag(4, "poisoned");
        var sink = new PopulationSink(world, Crowd.Id, s_healthKey.Id, 0, "crowd.downed",
            population.ReferenceOf(0), tag: "poisoned");
        var facts = new List<SimulationFact>();

        sink.Collect(facts);

        Assert.Equal(2, facts[0].Amount);
    }

    [Fact]
    public void ARolledBackTickRestoresTheBaselineSoTheRetryReportsTheChange()
    {
        var world = new World(seed: 111, valueScale: 1);
        var population = Create(world, 4, crossedHalf: 2);
        var sink = new PopulationSink(world, Crowd.Id, s_healthKey.Id, 0, "crowd.downed",
            population.ReferenceOf(0));
        var facts = new List<SimulationFact>();
        sink.Collect(facts);

        using (var transaction = world.BeginTransaction())
        {
            SetHealth(population, 2, 5);
            Assert.Equal(1, sink.Collect(facts));
            Assert.Equal(3, sink.Count);
            transaction.Rollback();
        }

        Assert.Equal(-1, Read(population.Column(s_healthKey), population, 2));
        Assert.Equal(2, sink.Count);
        // The caller's list keeps the fact from the failed pass, so only the retry's fact is new.
        var appended = facts.Count;
        SetHealth(population, 2, 5);
        Assert.Equal(1, sink.Collect(facts));
        Assert.Equal(appended + 1, facts.Count);
        Assert.Equal(3, facts[^1].Amount);
    }

    [Fact]
    public void ADespawnedAnchorStopsEmitting()
    {
        var world = new World(seed: 106, valueScale: 1);
        var population = Create(world, 4, crossedHalf: 2);
        var sink = new PopulationSink(world, Crowd.Id, s_healthKey.Id, 0, "crowd.downed",
            population.ReferenceOf(3));
        var facts = new List<SimulationFact>();

        population.RemoveSwapBack(3);
        population.AddRange(1);

        Assert.Equal(0, sink.Collect(facts));
        Assert.Empty(facts);
    }

    [Fact]
    public void SamplingDoesNotAllocateOnceWarmed()
    {
        var world = new World(seed: 107, valueScale: 1);
        var population = Create(world, 100_000, crossedHalf: 50_000);
        var sink = new PopulationSink(world, Crowd.Id, s_healthKey.Id, 0, "crowd.downed",
            population.ReferenceOf(0));
        for (var warmup = 0; warmup < 4; warmup++)
        {
            sink.Sample();
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        var sampled = 0;
        for (var repetition = 0; repetition < 4; repetition++)
        {
            sampled += sink.Sample();
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(200_000, sampled);
        Assert.True(allocated == 0, $"Sink sampling allocated {allocated} bytes over 100k rows.");
    }

    [Fact]
    public void ASinkFactDispatchesToARuleOnItsAnchorRow()
    {
        var world = new World(seed: 108, valueScale: 1);
        var population = Create(world, 4, crossedHalf: 2);
        var scoreKey = new PopulationColumnKey<int>("test.sink.score", 1, PopulationCodecs.Int32);
        var score = population.Column(scoreKey);
        var sink = new PopulationSink(world, Crowd.Id, s_healthKey.Id, 0, "crowd.downed",
            population.ReferenceOf(0));
        var engine = new RuleEngine(new CompiledRuleSet([
            new CompiledRule("crowd.downed", "crowd.downed", 0, [],
                [RuleEffect.AdjustProperty(RuleSubject.FactRow(Crowd.Id), "test.sink.score", 1)])]));
        engine.Bind(world);
        var facts = new List<SimulationFact>();
        sink.Collect(facts);

        var result = engine.DispatchBatch(world, facts.ToArray());

        Assert.False(result.RolledBack);
        Assert.Equal(1, result.RulesMatched);
        Assert.Equal(1, Read(score, population, 0));
        Assert.Equal(0, Read(score, population, 1));
    }

    [Fact]
    public void ASinkRejectsADoubleBufferedColumnWhenItIsCreated()
    {
        var world = new World(seed: 112, valueScale: 1);
        var population = world.DefinePopulation<Crowd>(enableHandles: true);
        population.AddRange(2);
        population.DoubleBufferedColumn(s_healthKey);

        var error = Assert.Throws<InvalidOperationException>(() =>
            new PopulationSink(world, Crowd.Id, s_healthKey.Id, 0, "crowd.downed",
                population.ReferenceOf(0)));

        Assert.Contains("double-buffered", error.Message, StringComparison.Ordinal);
        Assert.Contains(s_healthKey.Id, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASinkRejectsAnUnknownColumnAndANonNumericOne()
    {
        var world = new World(seed: 109, valueScale: 1);
        var population = Create(world, 1, crossedHalf: 0);
        var flagKey = new PopulationColumnKey<bool>("test.sink.flag", 1, PopulationCodecs.Boolean);
        population.Column(flagKey);

        var missing = Assert.Throws<InvalidOperationException>(() =>
            new PopulationSink(world, Crowd.Id, "test.sink.missing", 0, "crowd.downed",
                population.ReferenceOf(0)));
        var nonNumeric = Assert.Throws<InvalidOperationException>(() =>
            new PopulationSink(world, Crowd.Id, "test.sink.flag", 0, "crowd.downed",
                population.ReferenceOf(0)));

        Assert.Contains("test.sink.missing", missing.Message, StringComparison.Ordinal);
        Assert.Contains("test.sink.flag", nonNumeric.Message, StringComparison.Ordinal);
        Assert.Contains("numeric", nonNumeric.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASinkRejectsAnAnchorFromAnotherPopulationAndADeadOne()
    {
        var world = new World(seed: 110, valueScale: 1);
        var population = Create(world, 2, crossedHalf: 0);
        var other = world.DefinePopulation<OtherCrowd>(enableHandles: true);
        other.AddRange(1);

        Assert.Throws<InvalidOperationException>(() =>
            new PopulationSink(world, Crowd.Id, s_healthKey.Id, 0, "crowd.downed", other.ReferenceOf(0)));

        var dead = population.ReferenceOf(1);
        population.RemoveSwapBack(1);
        population.AddRange(1);
        Assert.Throws<InvalidOperationException>(() =>
            new PopulationSink(world, Crowd.Id, s_healthKey.Id, 0, "crowd.downed", dead));
    }

    private static Population<Crowd> Create(World world, int count, int crossedHalf)
    {
        var population = world.DefinePopulation<Crowd>(enableHandles: true);
        var health = population.Column(s_healthKey);
        population.Reserve(count);
        population.AddRange(count);
        population.WithAccess(new PopulationRange(0, count), (Column: health, Crossed: crossedHalf),
            static ((PopulationColumn<int> Column, int Crossed) state,
                scoped ref PopulationAccessScope<Crowd> scope) =>
            {
                var values = scope.EditRange(state.Column);
                for (var row = 0; row < values.Length; row++)
                {
                    // Positive values are "crossed the zero threshold", negative are not.
                    values[row] = row < state.Crossed ? 1 : -1;
                }
            });
        return population;
    }

    private static void SetHealth(Population<Crowd> population, int row, int value) =>
        population.WithAccess(new PopulationRange(row, 1), (Column: population.Column(s_healthKey), Value: value),
            static ((PopulationColumn<int> Column, int Value) state, scoped ref PopulationAccessScope<Crowd> scope) =>
                scope.EditRange(state.Column)[0] = state.Value);

    private static int Read(PopulationColumn<int> column, Population<Crowd> population, int row)
    {
        var values = new int[1];
        population.WithAccess(new PopulationRange(row, 1), (Column: column, Row: row, Values: values),
            static ((PopulationColumn<int> Column, int Row, int[] Values) state,
                scoped ref PopulationAccessScope<Crowd> scope) =>
                state.Values[0] = scope.ReadAll(state.Column)[state.Row]);
        return values[0];
    }

    private readonly struct Crowd : IPopulationKind
    {
        public static string Id => "test.sink-crowd";

        public static int Version => 1;

        public static string[] Tags => ["poisoned"];
    }

    private readonly struct OtherCrowd : IPopulationKind
    {
        public static string Id => "test.sink-other-crowd";

        public static int Version => 1;
    }
}
