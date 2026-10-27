using System.Collections.Immutable;
using Systemic.Host;
using Systemic.Kernel;

namespace Systemic.Core.Tests.Documentation;

#pragma warning disable CS0649 // The generator consumes these schema fields and emits their bindings.
[PopulationSchema("docs.agent", version: 1)]
internal partial struct DocumentationAgent
{
    [PopulationColumn("position", DoubleBuffered = true)]
    public int Position;

    [PopulationColumn("velocity")]
    public int Velocity;
}
#pragma warning restore CS0649

internal sealed class DocumentationPopulationSystem : ISystemicSystem
{
    private static readonly ImmutableArray<SystemAccess> s_access =
    [
        DocumentationAgentPopulation.ReadPosition,
        DocumentationAgentPopulation.ReadVelocity,
        DocumentationAgentPopulation.WritePosition,
        DocumentationAgentPopulation.WriteVelocity
    ];

    private readonly Population<DocumentationAgent> _agents;
    private readonly DocumentationAgentPopulation.Columns _columns;

    public DocumentationPopulationSystem(Population<DocumentationAgent> agents)
    {
        _agents = agents;
        _columns = DocumentationAgentPopulation.Bind(agents);
    }

    public string Id => "docs.agent-movement";

    public IReadOnlyList<SystemAccess> Access => s_access;

    public void Step(SystemicSystemContext context)
    {
        _ = context.Tick;
        using var lease = context.BorrowPopulation(_agents);
        context.ForEachRange((Lease: lease, Columns: _columns), _agents.Count,
            static (state, _, range) =>
            {
                var positions = state.Lease.ReadCurrent(state.Columns.Position);
                var velocities = state.Lease.Read(state.Columns.Velocity);
                var writer = state.Columns.WriteRange(range);

                for (var offset = 0; offset < range.Length; offset++)
                {
                    var row = range.Start + offset;
                    writer.Write(offset, new DocumentationAgentPopulation.Row
                    {
                        Position = positions[row] + velocities[row],
                        Velocity = velocities[row]
                    });
                }
            });
    }

    public static SystemicGameBuilder Register(
        SystemicGameBuilder builder,
        Population<DocumentationAgent> agents) =>
        builder.AddSystem(new DocumentationPopulationSystem(agents));
}
