# Populations and scheduling

Use generated population schemas for dense, row-oriented state. Declare stable schema and column IDs, then bind the generated columns once when a system is initialized.

```csharp
[PopulationSchema("game.agent", version: 1)]
public partial struct Agent
{
    [PopulationColumn("position", DoubleBuffered = true)]
    public int Position;

    [PopulationColumn("velocity")]
    public int Velocity;
}

var agents = AgentPopulation.Define(world);
agents.Reserve(10_000);
var columns = AgentPopulation.Bind(agents);
```

Each generated column has a stable key and `Read*` and `Write*` `SystemAccess` declarations. Use those declarations in `ISystemicSystem.Access` or `IScheduledKernelSystem.Access` so the scheduler can order systems that share data.

## Borrow and edit rows

`Population<TKind>.Borrow` and `SystemicSystemContext.BorrowPopulation` return a `PopulationLease<TKind>`. Read a normal column with `Read`, and write it with `Edit`; double-buffered columns use `ReadCurrent` and `EditNext`. `EditBoth` is for structural initialization such as spawning, while ordinary updates should write to the next buffer.

```csharp
using var lease = context.BorrowPopulation(agents);
context.ForEachRange((Lease: lease, Columns: columns), agents.Count,
    static (state, _, range) =>
    {
        var positions = state.Lease.ReadCurrent(state.Columns.Position);
        var velocities = state.Lease.Read(state.Columns.Velocity);
        var writer = state.Columns.WriteRange(range);

        for (var offset = 0; offset < range.Length; offset++)
        {
            var row = range.Start + offset;
            writer.Write(offset, new AgentPopulation.Row
            {
                Position = positions[row] + velocities[row],
                Velocity = velocities[row]
            });
        }
    });
```

`Columns.WriteRange(range)` bounds edits to that range and writes double-buffered values to `Next`. A `PopulationLease<TKind>` keeps row identity and buffers stable while borrowed views are in use. Do not keep spans or memories beyond the lease lifetime or across structural changes, buffer swaps, cloning, or World adoption. Concurrent writes must target disjoint row ranges. `ForEachRange` helps assign disjoint ranges; it does not make arbitrary shared state safe.

The system must declare every column touched by this example:

```csharp
using System.Collections.Immutable;

private static readonly ImmutableArray<SystemAccess> s_access =
[
    AgentPopulation.ReadPosition,
    AgentPopulation.ReadVelocity,
    AgentPopulation.WritePosition,
    AgentPopulation.WriteVelocity
];

public IReadOnlyList<SystemAccess> Access => s_access;
```

The complete [compile-checked population system](https://github.com/cakegod/systemic-framework/blob/main/tests/Systemic.Core.Tests/Documentation/ApiExamples.cs) exercises the same lease, range writer, generated schema, and host context.

The range writer writes each field in the generated row, so it needs write access to both columns even though the example only changes `Position`.

When storing access declarations in a field, use an immutable collection. The analyzer resolves direct collection initializers and private static readonly `ImmutableArray<SystemAccess>` fields; it intentionally skips mutable `SystemAccess[]` fields and computed access expressions.

## System access rules

For a lower-level Kernel system, use `SystemAccess.ReadColumn<TKind, TValue>` and `WriteColumn<TKind, TValue>` with the generated column key. The scheduler can run two systems together when their declarations do not conflict. Read/read access is compatible; a writer conflicts with access to the same resource. `SystemAccess.WriteWorld` is exclusive. Systems using immediate World operations, rule dispatch, or event dispatch need that declaration. Systems that only queue World changes can use `SystemAccess.DeferWorldWrites` and may share a wave with other deferred writers.

The scheduler relies on declarations; it does not infer every memory access at runtime. Declare service state and any other shared resources too. The analyzer checks several direct generated-column and context-access patterns, with limits described in the [analyzer reference](analyzers.md#analysis-boundaries).

## Queue population changes

Population rows cannot be added or removed while a lease is active. For direct setup code, use `PopulationCommandBuffer<TKind>` and call `Flush` after the lease ends. Flush resolves removals before additions and initializes each spawned row through the generated schema.

In a scheduled system, create partition-local buffers before dispatching range work:

```csharp
var partitionCount = Math.Max(1, Math.Min(rowCount, context.WorkerCount));
var commands = context.DeferPopulationCommands(agents, partitionCount);

context.ForEachRange(commands, rowCount, static (byPartition, worker, range) =>
{
    var localCommands = byPartition[worker];
    QueueChangesFor(range, localCommands);
});
```

Each callback writes only its own buffer. Scheduled buffers apply together at the wave barrier; do not flush them yourself or retain them after `Step`. Removals happen before additions. Spawns merge in system registration and partition order. `SystemAccess.DeferWorldWrites` or `SystemAccess.WriteWorld` is required for these buffers.

## Build the lower-level Kernel schedule

`DeterministicSystemScheduleBuilder` is available when an application drives Kernel systems without `SystemicGame`:

```csharp
using var schedule = new DeterministicSystemScheduleBuilder(workerCount: 4)
    .Add(new KernelSystem())
    .Build();

schedule.Execute(world, tick);
```

Each `IScheduledKernelSystem` provides an ID, access declarations, and `Execute(ScheduledSystemContext)`. The builder keeps conflicting systems in registration order and groups compatible systems into waves. The schedule owns reusable worker threads and must be disposed. A zero worker count selects a capped default of one to four workers; `SystemicGameBuilder.WithScheduledWorkerCount` sets the host schedule's worker count explicitly.
