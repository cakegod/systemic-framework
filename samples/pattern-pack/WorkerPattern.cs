using Systemic.Host;
using Systemic.Kernel;
using Systemic.Rules;

namespace PatternPack;

/// <summary>A worker is a population row, not an entity: content never sees the individual worker.</summary>
public readonly struct WorkerRow : IPopulationKind
{
    public static string Id => "sample.pattern-pack.worker";

    public static int Version => 1;

    public static string[] Tags => ["pattern.worker"];
}

/// <summary>
/// Publishes one fact per worker row. One content rule pays every fact out, so adding workers never
/// adds rules, and the payout stays a single deterministic dispatch.
/// </summary>
public sealed class WorkerPatternSystem : ISystemicSystem
{
    private readonly int _leadEntityId;
    private readonly int _oreEntityId;
    private readonly int _workers;

    public WorkerPatternSystem(World world, int leadEntityId, int oreEntityId, int workers)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (workers is < 0 or > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(workers));
        }

        var population = world.DefinePopulation<WorkerRow>(enableHandles: true);
        population.Reserve(workers);
        population.AddRange(workers);
        for (var row = 0; row < workers; row++)
        {
            population.AddTag(row, "pattern.worker");
        }

        _leadEntityId = leadEntityId;
        _oreEntityId = oreEntityId;
        _workers = workers;
    }

    public string Id => "sample.pattern-pack.workers";

    public int Workers => _workers;

    public void Step(SystemicSystemContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        for (var row = 0; row < _workers; row++)
        {
            context.Publish(new SimulationFact("pattern.workers.worked", _leadEntityId, _oreEntityId, Amount: row));
        }
    }
}
