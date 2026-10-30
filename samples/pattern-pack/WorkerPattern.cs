using Systemic.Host;
using Systemic.Kernel;
using Systemic.Rules;

namespace PatternPack;

/// <summary>A worker is a population row, not an entity: content never sees the individual worker.</summary>
/// <remarks>
/// The schema declares the row tag content guards on and binds one column, <c>mined</c>, that the
/// content rule writes. Both the tag and the column live here because a row has no entity tags or
/// properties: its schema is its shape.
/// </remarks>
public readonly struct WorkerRow : IPopulationKind
{
    public static string Id => "sample.pattern-pack.worker";

    public static int Version => 1;

    public static string[] Tags => ["pattern.worker"];
}

/// <summary>
/// Publishes one fact per live worker row. One content rule credits every fact's row, so adding
/// workers never adds rules, and the credit stays a single deterministic dispatch per worker.
/// </summary>
public sealed class WorkerPatternSystem : ISystemicSystem
{
    private static readonly PopulationColumnKey<int> s_mined = new("mined", 1, PopulationCodecs.Int32);

    private readonly Population<WorkerRow> _workers;

    public WorkerPatternSystem(World world, int workers)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (workers is < 0 or > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(workers));
        }

        _workers = world.DefinePopulation<WorkerRow>(enableHandles: true);
        // Binding the column is what makes it addressable from content: the rule names the column
        // by its stable ID, so nothing about the C# type reaches the rules.
        _workers.Column(s_mined);
        _workers.Reserve(workers);
        _workers.AddRange(workers);
        for (var row = 0; row < workers; row++)
        {
            _workers.AddTag(row, "pattern.worker");
        }
    }

    public string Id => "sample.pattern-pack.workers";

    /// <summary>Gets the live worker count, which is what a step publishes for.</summary>
    public int Workers => _workers.Count;

    /// <summary>Removes one live worker, as a game would when a worker retires.</summary>
    /// <param name="row">The row to remove.</param>
    /// <returns>Whether the row was live and is now gone.</returns>
    public bool RemoveWorker(int row) => _workers.TryRemove(_workers.GetHandle(row));

    /// <summary>Stops one live worker from being paid without removing it.</summary>
    /// <param name="row">The row whose tag to clear.</param>
    /// <returns>Whether the tag was cleared.</returns>
    public bool StopWorker(int row) => _workers.RemoveTag(row, "pattern.worker");

    /// <summary>Reads how much the content rules have credited to one worker row.</summary>
    /// <param name="row">The row to read.</param>
    /// <returns>The row's <c>mined</c> column.</returns>
    /// <remarks>This is the read a game performs; the sample keeps it here so its tests need no lease.</remarks>
    public int Mined(int row)
    {
        var read = new MinedRead { Column = _workers.Column(s_mined) };
        _workers.WithAccess(new PopulationRange(row, 1), read,
            static (MinedRead state, scoped ref PopulationAccessScope<WorkerRow> access) =>
                state.Value = access.ReadRange(state.Column)[0]);
        return read.Value;
    }

    public void Step(SystemicSystemContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        for (var row = 0; row < _workers.Count; row++)
        {
            // A row fact names the row and no entity, so content can only touch that row. That is
            // the point: a worker's yield lands on the worker, not on a shared counter. The amount
            // is the unit of work the content rule requires.
            context.Publish(SimulationFact.ForRow("pattern.workers.worked", _workers.ReferenceOf(row),
                amount: 1));
        }
    }

    // A class, not a struct: the access callback receives caller state by value, so a struct copy
    // would drop the value it read.
    private sealed class MinedRead
    {
        public required PopulationColumn<int> Column { get; init; }

        public int Value { get; set; }
    }
}
