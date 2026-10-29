# Use `SystemicSystemContext`

`SystemicSystemContext` is the context passed to each `ISystemicSystem.Step`. It brings host operations and Kernel scheduling operations together so a system has one entry point for its current tick, World view, deferred commands, service lookup, population access, range work, and rollback hooks.

Register host systems with `SystemicGameBuilder.AddSystem`:

```csharp
public sealed class RetireEntitySystem(int entityId) : ISystemicSystem
{
    public string Id => "game.retire-entity";

    public int Order => 100;

    public IReadOnlyList<SystemAccess> Access => [SystemAccess.DeferWorldWrites];

    public void Step(SystemicSystemContext context)
    {
        _ = context.Tick;
        context.Commands.Remove(entityId);
    }
}

using var game = new SystemicGameBuilder(content, world)
    .AddSystem(new RetireEntitySystem(entityId))
    .WithScheduledWorkerCount(4)
    .Build();
```

The [compile-checked example](https://github.com/cakegod/systemic-framework/blob/main/tests/Systemic.Core.Tests/Documentation/ApiExamples.cs) registers a host system that uses this context with a generated population.

## What the context exposes

- `Tick`, `WorkerCount`, `World`, and `IsScheduledExecution` describe the current execution. Context methods are valid during a scheduled `Step`; do not retain the context or command writers after that call returns. Population data is available only through synchronous access callbacks, whose spans expire when each callback returns.
- `GetService<T>()` retrieves a service registered with `SystemicGameBuilder.AddService`. Systems share the registered service instance, so declare the resources that protect any shared mutable service state.
- `Commands` records deferred World changes. A system's private commands flush at the end of its scheduling wave in stable registration order.
- `GetWritableWorld`, `SetProperty`, and `Publish` perform immediate World work. The system must declare `SystemAccess.WriteWorld`; this places it in a wave by itself.
- `DeferFact` queues a fact for serial dispatch after the whole schedule, so a system that only emits facts can declare `SystemAccess.DeferWorldWrites` and share a wave. Effects of a deferred fact are not visible to other systems until the next tick. Call it from the system body, not from a range callback.
- `ForEachPopulationRange` can take a `Population<TKind>` directly, lease its storage for the scheduled callback, and release it when the range call returns. `BorrowPopulation` is available when a system needs a lease outside one range call, such as to read another population. Structural changes must wait until all relevant access callbacks and leases end.
- `ForEachRange` partitions contiguous row ranges. Each row belongs to at most one callback, and each callback receives its partition index and range. Worker callbacks should write only that range and should not make immediate World changes or mutate a shared command buffer.
- `ReduceRanges` maps logical partitions and merges partial results in partition order. Keep the partition count fixed when results must use the same merge order across worker counts; the arithmetic itself must also be deterministic.
- `OnRollback` registers recovery for state outside the World journal. Stateful extensions already participate in the host's transaction snapshots; use this hook for other external state changed by the current step.

`ISystemicSystem.Access` defaults to `SystemAccess.WriteWorld`, preserving an exclusive wave for systems that do not declare their data dependencies. To let systems share a wave, declare reads and writes for the resources and columns they actually use. The scheduler orders conflicting declarations in registration order; an incorrect declaration can still permit races, so declarations and range ownership remain the system author's responsibility.

For host systems, `Order` sorts registration before the schedule is built. It sets priority for conflicting systems and the stable order used at barriers; systems with compatible access may still share a concurrent wave, so `Order` does not sequence independent systems.

## Pick the smallest API that fits

| You need to | Use |
| --- | --- |
| Run a tick system in a Core host | `ISystemicSystem` + `SystemicGameBuilder.AddSystem`; declare `Access` so compatible systems share a wave |
| Drive Kernel without content, rules or the host | `IScheduledKernelSystem` + `DeterministicSystemScheduleBuilder` |
| Read population rows outside an access callback (rendering, UI, tests) | the generated `<Schema>Population.Snapshot(population, range, buffer)` |
| Read a few rows inside a system | `WithAccess` + `ReadCurrent`/`ReadRange` |
| Write disjoint rows from parallel partitions | `ForEachPopulationRange` + `Columns.WriteRange(ref access)` |
| Change World state this tick | `Commands` (deferred, shared wave) then `ApplyDeferredCommands()` in a standalone harness |
| Remove an entity | `Commands.Remove(entityId)` in a system, or `World.Remove(entity)` outside the schedule |
| Emit a fact other systems may observe this tick | `Publish` (immediate, needs `WriteWorld`) |
| Emit a fact only rules and observers need | `DeferFact` (needs `DeferWorldWrites`, shared wave) |

Immediate World operations are the only way to make a change visible to another system in the same tick. They require `WriteWorld`, which excludes every other system from the wave. When the change can wait until the tick ends, queue it through `Commands` and keep a shared wave.

Entity removal is a World command, not an entity mutation. A system queues `Commands.Remove(entityId)`; code outside the schedule calls `World.Remove(entity)`. Both mark the entity and let the world own the pending removal, so it journals with the enclosing transaction and rolls back: an entity marked for removal in a rolled-back tick is live again. `Entity.ToRemove` is readable everywhere and writable only by the world. Removal takes effect at the next `PruneRemoved`, which the host runs at the end of the tick; until then queries that filter live entities already skip the row.

### Rolling back domain state

| State you need restored on a failed tick | Implement |
| --- | --- |
| Plain fields with an in-memory snapshot | `ISystemicTransactionalState` |
| Allocation-sensitive state with a pooled snapshot | `ISystemicReusableTransactionalState` |
| Save/load only, no per-tick rollback | `ISystemicStatefulExtension` |

Systems and services that implement one of these are registered automatically. `RegisterStatefulExtension` is for standalone state objects only. The host snapshots registered state before the systems run and restores it when the tick rolls back.

## Direct tests and standalone harnesses

The public `SystemicSystemContext` constructor creates a single-use, standalone context for direct tests or small harnesses. It has one worker, and its `ForEachRange` callback runs once over the whole range. It does not emulate the host's parallel scheduler, registered services, or rollback callbacks. Use host-level integration tests when behavior depends on access declarations, wave barriers, or worker partitioning.

A standalone context also has no wave barrier, so deferred World commands stay queued after `Step` returns. Call `ApplyDeferredCommands()` before inspecting the World from a direct test. A scheduled system must not call it; the schedule applies its commands at the barrier.
