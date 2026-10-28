using Systemic.Kernel;

namespace Systemic.Tests.Kernel;

/// <summary>
/// A rule that adjusts one row per fact must not pay for the 4 KiB page around that row. These
/// tests pin the journal size, not the wall clock: the row path captures the touched row and the
/// range path still captures the pages it covers.
/// </summary>
public sealed class PopulationRowJournalTests
{
    private const int Rows = 1_000_000;

    [Fact]
    public void A_sparse_row_write_journals_one_row_instead_of_a_page()
    {
        var world = new World(seed: 81, valueScale: 1);
        var population = world.DefinePopulation<TaggedKind>();
        population.AddRange(Rows);
        var before = world.PopulationJournalBytes;

        using (var transaction = world.BeginTransaction())
        {
            population.AddTag(Rows / 2, "poisoned");
            var captured = world.PopulationJournalBytes - before;

            Assert.Equal(sizeof(ulong), captured);
            Assert.True(population.HasTag(Rows / 2, "poisoned"));
            transaction.Commit();
        }

        Assert.True(population.HasTag(Rows / 2, "poisoned"));
    }

    [Fact]
    public void A_sparse_row_write_still_rolls_back()
    {
        var world = new World(seed: 82, valueScale: 1);
        var population = world.DefinePopulation<TaggedKind>();
        population.AddRange(4);
        population.AddTag(1, "poisoned");

        using (var transaction = world.BeginTransaction())
        {
            population.AddTag(1, "blessed");
            population.RemoveTag(1, "poisoned");
            // The same row touched twice must still restore its pre-transaction mask.
            population.AddTag(1, "asleep");
            transaction.Rollback();
        }

        Assert.True(population.HasTag(1, "poisoned"));
        Assert.False(population.HasTag(1, "blessed"));
        Assert.False(population.HasTag(1, "asleep"));
    }

    [Fact]
    public void A_page_capture_after_a_row_capture_still_restores_the_pre_transaction_state()
    {
        var world = new World(seed: 83, valueScale: 1);
        var population = world.DefinePopulation<TaggedKind>();
        var mask = population.MaskColumn!;
        population.AddRange(8);
        population.AddTag(0, "poisoned");

        using (var transaction = world.BeginTransaction())
        {
            // Row capture first, then a range edit that captures the page holding the same row.
            population.AddTag(1, "blessed");
            WriteMask(population, 0, 8);
            transaction.Rollback();
        }

        Assert.True(population.HasTag(0, "poisoned"));
        Assert.False(population.HasTag(1, "blessed"));
        Assert.Equal(sizeof(ulong), mask.ElementSize);
    }

    [Fact]
    public void A_full_row_range_edit_still_captures_pages()
    {
        var world = new World(seed: 84, valueScale: 1);
        var population = world.DefinePopulation<TaggedKind>();
        population.AddRange(Rows);
        var before = world.PopulationJournalBytes;

        using (var transaction = world.BeginTransaction())
        {
            WriteMask(population, 0, Rows);
            var captured = world.PopulationJournalBytes - before;

            // One buffer capture for the whole column: the range path keeps its old cost, and only
            // the sparse path narrowed.
            Assert.Equal(Rows * sizeof(ulong), captured);
            transaction.Rollback();
        }
    }

    private static void WriteMask(Population<TaggedKind> population, int start, int length)
    {
        var mask = population.MaskColumn!;
        population.WithAccess(new PopulationRange(start, length), mask,
            static (PopulationColumn<ulong> column, scoped ref PopulationAccessScope<TaggedKind> scope) =>
            {
                scope.EditRange(column).Fill(ulong.MaxValue);
            });
    }

    private readonly struct TaggedKind : IPopulationKind
    {
        public static string Id => "test.journal-tagged";

        public static int Version => 1;

        public static string[] Tags => ["poisoned", "blessed", "asleep"];
    }
}
