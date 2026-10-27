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
- `GetWritableWorld`, `SetProperty`, `Publish`, and `Execute` perform immediate World work. The system must declare `SystemAccess.WriteWorld`; this places it in a wave by itself.
- `ForEachPopulationRange` can take a `Population<TKind>` directly, lease its storage for the scheduled callback, and release it when the range call returns. `BorrowPopulation` is available when a system needs a lease outside one range call, such as to read another population. Structural changes must wait until all relevant access callbacks and leases end.
- `ForEachRange` partitions contiguous row ranges. Each row belongs to at most one callback, and each callback receives its partition index and range. Worker callbacks should write only that range and should not make immediate World changes or mutate a shared command buffer.
- `ReduceRanges` maps logical partitions and merges partial results in partition order. Keep the partition count fixed when results must use the same merge order across worker counts; the arithmetic itself must also be deterministic.
- `OnRollback` registers recovery for state outside the World journal. Stateful extensions already participate in the host's transaction snapshots; use this hook for other external state changed by the current step.

`ISystemicSystem.Access` defaults to `SystemAccess.WriteWorld`, preserving an exclusive wave for systems that do not declare their data dependencies. To let systems share a wave, declare reads and writes for the resources and columns they actually use. The scheduler orders conflicting declarations in registration order; an incorrect declaration can still permit races, so declarations and range ownership remain the system author's responsibility.

For host systems, `Order` sorts registration before the schedule is built. It sets priority for conflicting systems and the stable order used at barriers; systems with compatible access may still share a concurrent wave, so `Order` does not sequence independent systems.

## Direct tests and standalone harnesses

The public `SystemicSystemContext` constructor creates a single-use, standalone context for direct tests or small harnesses. It has one worker, and its `ForEachRange` callback runs once over the whole range. It does not emulate the host's parallel scheduler, registered services, or rollback callbacks. Use host-level integration tests when behavior depends on access declarations, wave barriers, or worker partitioning.
