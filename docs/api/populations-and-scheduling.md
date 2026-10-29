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

## Scoped population access

`Population<TKind>.Borrow` and `SystemicSystemContext.BorrowPopulation` return a `PopulationLease<TKind>`. The lease exposes no spans or memories. Use `Population.WithAccess` for serial work, or `SystemicSystemContext.ForEachPopulationRange(population, ...)` for scheduler-assigned parallel partitions. Both APIs avoid a separate lease object. Keep an explicit lease when row identity must span multiple phases or the callback reads another population. Every access callback receives a `scoped ref PopulationAccessScope<TKind>`; spans from it expire when the callback returns.

When migrating code that previously read or edited columns through a lease, move those operations into `WithAccess` and use `ReadAll`/`ReadRange` or `ReadCurrent`/`ReadCurrentRange` for reads and `EditRange`/`EditNext` for writes. The lease may still protect row identity across phases, but column spans and memories are available only inside the synchronous callback.

```csharp
context.ForEachPopulationRange(agents, columns,
    static (AgentPopulation.Columns state, int _, scoped ref PopulationAccessScope<Agent> access) =>
    {
        var range = access.Range;
        var positions = access.ReadCurrentRange(state.Position);
        var velocities = access.ReadRange(state.Velocity);
        var writer = state.WriteRange(ref access);

        for (var offset = 0; offset < range.Length; offset++)
        {
            writer.Write(offset, new AgentPopulation.Row
            {
                Position = positions[offset] + velocities[offset],
                Velocity = velocities[offset]
            });
        }
    });
```

`Columns.WriteRange(ref access)` is bound to the exact assigned rows, writes double-buffered values to `Next`, and checks the callback token on each write. For single-buffered columns, `ReadAll` requires a read declaration. A parallel callback cannot read all rows of a single-buffered column that the same system writes, even when both read and write are declared; use `ReadRange` to stay within the assigned rows. The current buffer of a double-buffered column can be read across all rows while the callback writes its assigned rows to `Next`.

Serial initialization uses the same scope. Generated `InitializeRange` binds columns before access, reads the caller's input span directly without copying it, writes only the requested range, and initializes both buffers. Structural changes, buffer swaps, transactions, and cloning remain blocked until the callback returns and its access scope is released.

Rendering, UI and tests read rows outside an access callback with the generated snapshot, which copies a range into a caller-owned buffer using one scoped callback:

```csharp
var snapshot = new Agent[1000];
AgentPopulation.Snapshot(agents, new PopulationRange(0, snapshot.Length), snapshot);
```

`Snapshot` reads the current buffer of double-buffered columns and writes `range.Length` entries starting at index zero, so callers must not keep population spans alive instead. It allocates nothing beyond the buffer the caller supplies.

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

The complete [compile-checked population system](https://github.com/cakegod/systemic-framework/blob/main/tests/Systemic.Core.Tests/Documentation/ApiExamples.cs) exercises the scoped lease, range writer, generated schema, and host context.

The range writer writes each field in the generated row, so it needs write access to both columns even though the example only changes `Position`.

When storing access declarations in a field, use an immutable collection. The analyzer resolves direct collection initializers and private static readonly `ImmutableArray<SystemAccess>` fields; it intentionally skips mutable `SystemAccess[]` fields and computed access expressions.

## System access rules

For a lower-level Kernel system, use `SystemAccess.ReadColumn<TKind, TValue>` and `WriteColumn<TKind, TValue>` with the generated column key. The scheduler can run two systems together when their declarations do not conflict. Read/read access is compatible, and so is a reader beside a buffered writer, because buffered commands land at the wave barrier after every reader; a writer conflicts with access to the same resource. `SystemAccess.WriteWorld` is exclusive. Systems using immediate World operations or rule dispatch need that declaration. Systems that only queue World changes can use `SystemAccess.DeferWorldWrites` and may share a wave with other deferred writers.

The scheduler relies on declarations; it does not infer every memory access at runtime. Declare service state and any other shared resources too. The analyzer checks several direct generated-column and context-access patterns, with limits described in the [analyzer reference](analyzers.md#analysis-boundaries).

## Row-addressed rules and crowd scans

A population schema is an `IPopulationKind`: a stable `Id`, a `Version`, and optionally the tags and modifier properties the schema declares. Generated schemas implement it, and a hand-written schema is how the crowd tier declares its columns:

```csharp
internal readonly struct CrowdAgent : IPopulationKind
{
    public static string Id => "crowd.prey";
    public static int Version => 1;
    public static string[] Tags => ["burning"];
}

var prey = world.DefinePopulation<CrowdAgent>(enableHandles: true);
var energy = prey.Column(new PopulationColumnKey<int>("crowd.prey.energy", 1, PopulationCodecs.Int32));
```

Declared tags are one `ulong` mask column whose bit order is the schema's, so a tag test is a bit test and the mask is part of saved state. Declared modifier properties give each row inline `ModifierSlot8` slots; `SetModifier` and `EffectiveValue` address them by property name. `enableHandles` gives every row a generation-checked `PopulationRowReference`, which is what lets a fact name a row and a despawn be detected instead of retargeted.

A rule addresses the row its fact is about with the `factRow` subject in content, and one scope evaluates it against a whole population:

```csharp
var rules = new RuleEngine(content.Rules);
rules.Bind(world);
var result = rules.EvaluateRowEvent(world, "crowd.prey.think", CrowdAgent.Id, new PopulationRange(0, prey.Count));
```

This is the crowd path: no fact per row, one scope for the range, and only `factRow` subjects are accepted, so an entity subject or an `emitEvent` effect is rejected before the first row instead of acting once per row. The population must be defined with handles, because each row is addressed by a reference. A rule whose first condition is a tag reads a packed tag bit per row (one bit per row, not the row's eight-byte mask word) and evaluates the rest of the rule only for rows that carry it; every other condition keeps its authored order. The bitset is derived from the mask column, so a rollback or a structural change rebuilds it before the next scan. `RuleRowScanResult` reports rows evaluated, rules matched, effects applied, and whether the scan rolled back with its transaction.

To turn many rows crossing a threshold into one fact, use `PopulationSink`. It counts one column against one threshold in a single pass, appends at most one row-addressed fact carrying that count, and appends nothing while the count is unchanged:

```csharp
var sink = new PopulationSink(world, CrowdAgent.Id, "crowd.prey.energy", threshold: 1,
    "crowd.prey.standing", prey.ReferenceOf(0));
var facts = new List<SimulationFact>(1);
if (sink.Collect(facts) > 0)
{
    rules.Dispatch(world, facts[0]);
}
```

The anchor row receives the fact, so a rule with a `factRow` subject writes a count on that row. If the anchor despawns, the sink stops emitting rather than reporting against a replaced row. Stagger movement across ticks (the crowd tier moves an eighth of the rows per tick) so the per-tick cost stays inside the simulation budget.

`benchmarks/Systemic.Benchmarks -- --gate --scenario crowd` is the acceptance run for this path: one million rows, one authored rule per row per tick, every tick inside the 10 ms simulation share of a 30 Hz frame, zero allocation. See `samples/crowd-million` for the content and `RowScanTests` for the behaviours the scan pins.

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
