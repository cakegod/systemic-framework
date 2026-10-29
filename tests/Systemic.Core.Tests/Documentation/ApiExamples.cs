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

internal sealed class DocumentationPopulationSystem(Population<DocumentationAgent> agents) : ISystemicSystem
{
    private static readonly ImmutableArray<SystemAccess> s_access =
    [
        DocumentationAgentPopulation.ReadPosition,
        DocumentationAgentPopulation.ReadVelocity,
        DocumentationAgentPopulation.WritePosition,
        DocumentationAgentPopulation.WriteVelocity
    ];

    private readonly Population<DocumentationAgent> _agents = agents;
    private readonly DocumentationAgentPopulation.Columns _columns = DocumentationAgentPopulation.Bind(agents);

    public string Id => "docs.agent-movement";

    public IReadOnlyList<SystemAccess> Access => s_access;

    public void Step(SystemicSystemContext context)
    {
        _ = context.Tick;
        using var lease = context.BorrowPopulation(_agents);
        context.ForEachPopulationRange(lease, (Lease: lease, Columns: _columns),
            static ((PopulationLease<DocumentationAgent> Lease, DocumentationAgentPopulation.Columns Columns) state,
                int _, scoped ref PopulationAccessScope<DocumentationAgent> access) =>
            {
                var range = access.Range;
                var positions = access.ReadCurrentRange(state.Columns.Position);
                var velocities = access.ReadRange(state.Columns.Velocity);
                var writer = state.Columns.WriteRange(ref access);

                for (var offset = 0; offset < range.Length; offset++)
                {
                    writer.Write(offset, new DocumentationAgentPopulation.Row
                    {
                        Position = positions[offset] + velocities[offset],
                        Velocity = velocities[offset]
                    });
                }
            });
    }

    public static SystemicGameBuilder Register(
        SystemicGameBuilder builder,
        Population<DocumentationAgent> agents) =>
        builder.AddSystem(new DocumentationPopulationSystem(agents));
}
