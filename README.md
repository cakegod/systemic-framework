# Systemic game framework

The framework separates deterministic simulation from optional game modules and engine adapters. A Core-only game can load entities, run rules, tick, and save without referencing incremental gameplay, presentation, or 2D gameplay assemblies.

## Project layout

- `src/Systemic.Kernel` contains world state, deterministic transactions, queries, modifiers, and random state.
- `src/Systemic.Core` contains generic content, rules, hosting, save contracts, and module registration. It references Kernel only.
- `src/modules/Systemic.Incremental` contains resources, producers, upgrades, advancement choices, and their runtime.
- `src/modules/Systemic.Presentation` contains visual recipes, UI layouts, timelines, cue state, and local preview tools.
- `src/modules/Systemic.Gameplay2D` contains engine-neutral actor, projectile, spatial targeting, and timed status primitives.
- `src/tools/Systemic.Cli` composes all built-in modules for authoring and preview workflows.
- `tests/Systemic.Core.Tests` and `tests/modules/*` test each boundary in isolation. `tests/Systemic.Cli.Tests` exercises the actual CLI process, and `tests/Systemic.Integration.Tests` tests compositions across modules.

Each module references Core and Kernel only. Modules do not reference sibling modules. A game registers only the module implementations it uses; the CLI registers all built-in modules.

## Try the samples

The Core-only sandbox demonstrates rules without optional module files. Quarry composes all built-in modules.

```sh
dotnet run --project src/tools/Systemic.Cli -- validate samples/systemic-sandbox
dotnet run --project src/tools/Systemic.Cli -- validate samples/incremental-quarry
dotnet run --project src/tools/Systemic.Cli -- trace samples/incremental-quarry incremental.production quarry.ember-source quarry.ore
dotnet run --project src/tools/Systemic.Cli -- preview samples/incremental-quarry
```

The CLI also supports `new <directory>` and `watch <directory>`. Preview serves a read-only local page; stop it with Ctrl+C. JSON schemas are grouped under `schemas/core`, `schemas/incremental`, `schemas/presentation`, and `schemas/gameplay2d`.

## Author content

Every project has a `game.json` manifest at schema version 3. Core requires only `entities` and `rules` paths. Optional content is declared under the module ID that owns it. For example, Quarry declares `systemic.incremental`, `systemic.presentation`, and `systemic.gameplay2d`; a Core-only project omits `modules` entirely. A declared module without a registered loader fails with a diagnostic at its manifest entry.

Core entity visual references are opaque IDs. The Presentation module validates and resolves them. Cross-module identifiers use Core's generic reference catalog, so modules can validate links without project references to each other. Choice icon assignments are presentation content keyed by choice ID.

The Incremental module compiles resource balances, producers, upgrades, actor roles, and advancement choices. Its runtime handles production and choice commands. Choice groups can be registered for multiple owners and persist through module extension state.

The Presentation module compiles bounded pixel recipes, imported-asset composition, UI layouts, and fact timelines. Its rasterizer produces RGBA buffers for actors and effects. Timeline cues publish only after the enclosing fact transaction commits; rollback suppresses them. The preview exposes layouts, icons, bars, and timeline bindings without placing renderer dependencies in simulation.

The Gameplay2D module compiles actor blueprints and status profiles, and provides deterministic spawning, movement, expiry, spatial target selection, resource transfer, and timed status primitives. Hosts can add domain rules through Core's generic system and extension contracts.

## Host and save state

`SystemicGame` and `HeadlessSystemicHost` provide fixed-step execution, deterministic facts, rule traces, and save operations. Presentation-specific UI and cue APIs are module facades; Core does not expose those types. Engine adapters own input, rendering, audio, physics observations, and platform services. The adapter boundary is documented in `adapters/README.md`.

`SystemicSaveCodec` writes versioned world state and opaque stateful-extension data. Existing save versions 1 and 2 migrate into the generic extension representation; new saves use the current version. Core does not serialize module-specific DTOs directly.

## Population simulation

The Kernel also supports dense, row-oriented populations for large visual swarms. Annotate an unmanaged partial struct with `[PopulationSchema("stable.id", version)]` and its fields with `[PopulationColumn("column.id")]`; the generator produces typed column bindings, stable column keys, access declarations, and range initialization. Generated schemas work with `PopulationCommandBuffer<TKind>` for transactional batched spawns and removals.

Reference Kernel normally and configure `Systemic.Kernel.Generator` as an analyzer with `OutputItemType="Analyzer"` and `ReferenceOutputAssembly="false"`, as in the [Boids project](samples/boids-monogame/Boids.MonoGame.csproj).

```csharp
[PopulationSchema("game.projectiles", 1)]
partial struct Projectile
{
    [PopulationColumn("position", DoubleBuffered = true)] public Vector2 Position;
    [PopulationColumn("velocity", DoubleBuffered = true)] public Vector2 Velocity;
    [PopulationColumn("lifetime")] public float Lifetime;
}

var projectiles = ProjectilePopulation.Define(world);
var columns = ProjectilePopulation.Bind(projectiles);
var commands = new PopulationCommandBuffer<Projectile>(projectiles);
commands.Reserve(spawnCapacity: 1_000, removalCapacity: 1_000);
commands.Spawn(newProjectiles);
commands.Flush(); // removals resolve first; additions are initialized as one batch
```

`PopulationSpatialGrid2D` builds a dense fixed-bounds 2D grid from position spans. Its short-lived snapshots expose stable exact-radius and bounds queries using caller-owned buffers; a capped candidate query supports systems that intentionally limit per-row work. Reserve grid rows and query buffers before stepping to avoid warmed allocations.

`DeterministicSystemScheduleBuilder` opts systems into access-declared parallel waves while legacy `ISystemicSystem.Order` systems remain serial. `SystemicGameBuilder.AddScheduledSystem` runs the scheduled phase after legacy systems and before modifier expiry. Generated `Read*`/`Write*` declarations identify population dependencies; deferred World/entity mutations are queued per system and flushed in stable registration order at barriers. A failed game step rolls back World changes and its logical tick. Systems with persistent state outside World can register `SystemicScheduledContext.OnRollback` to restore it. Immediate rule or event dispatch requires exclusive `SystemAccess.WriteWorld`; ordinary deferred commands can share a wave. `ScheduledSystemContext.ForEachRange` partitions contiguous rows consistently across worker counts. Systems must honor their declared access and write disjoint population rows; conflicting accesses are ordered automatically. Schedules reuse owned worker threads across ticks, default to at most four workers, and are disposed by `SystemicGame.Dispose()`.

Borrow contexts only during the system call. Submit World commands and rollback hooks from the system body; range callbacks write disjoint population slices. Command writers record their execution and reject later use. Cache rollback delegates when you need allocation-free ticks.

## Verify

Run the full Release solution suite and C# formatting checks from the repository root:

```sh
dotnet test Systemic.sln --nologo --configuration Release -m:1
dotnet format Systemic.sln --verify-no-changes
dotnet format Systemic.sln style --verify-no-changes --severity info
dotnet format Systemic.sln analyzers --verify-no-changes --severity info
```

The strict gate runner in `benchmarks/Systemic.Benchmarks` measures three reusable workloads: 50,000 generic population producers, a 50,000-entity indexed batch query, and 50,000 products with 1,000 reactive actors. Run it with `dotnet run -c Release --project benchmarks/Systemic.Benchmarks -- --gate --scenario all --warmup-ticks 1000 --ticks 500 --repeat 3 --workers 1 --max-tick-ms 5 --tick-log artifacts/kernel.csv`. It records every measured tick, reports mean/p50/p95/p99/maximum, fails if any tick reaches the budget, and requires zero process-wide allocations. `--workers` accepts a fixed count; omit it to use the capped machine default. The `producer-entity` diagnostic measures the dynamic `DataEntity` authoring path, which is easier to author but is not the high-volume storage path. `reactive-query` and `reactive-dispatch` isolate the two stages of the combined actor workload. Use dense population columns with `ForEachRange` for large uniform swarms and producers; use `Entity` and indexed batch rules for smaller, heterogeneous actors and rule-driven work.

The performance target is an Intel Core i5-6200U-class low-power CPU (2 cores/4 threads, circa 2015–2016), with a strict per-tick limit below 5 ms and zero warmed allocations. The available runner is an AMD EPYC 9V74; benchmark containers are capped at two CPUs for a rough core-count comparison. This does not model the target's clock, cache, or hyper-threading behavior, so it cannot certify that laptop. The Boids sample's `--strict-budget` headless command measures the full 100,000-boid scheduled path under the same gate. BenchmarkDotNet cases cover grid, scheduler, transaction, and reactive query costs. The anonymized behavior matrix is in `tests/Systemic.Integration.Tests/Fixtures/talent-pattern-coverage.json`.

The projects target .NET 10. Core uses `Corvus.Text.Json.Validator` for JSON Schema validation, and the CLI uses `System.CommandLine`; neither introduces an engine dependency. Games consume the framework as a library and supply their own engine adapter; no engine type is a dependency of the framework.
