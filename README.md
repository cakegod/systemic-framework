# Systemic game framework

[![Quality gates](https://github.com/cakegod/systemic-framework/actions/workflows/quality-gates.yml/badge.svg)](https://github.com/cakegod/systemic-framework/actions/workflows/quality-gates.yml)

Systemic is a .NET framework for games that need a deterministic simulation. It separates the simulation from optional game modules and engine adapters, so a Core-only game can load entities, run rules, tick and save without referencing incremental gameplay, presentation or 2D gameplay assemblies. The same seed and inputs produce the same world state, save document and replay hash.

## Requirements

- .NET SDK 10, since the projects target `net10.0`
- Python 3 for the API documentation check
- A display with SDL/OpenGL support for the MonoGame and SDL samples

## Installation

The framework is not published as a NuGet package. Clone the repository and build it from source:

```sh
git clone https://github.com/cakegod/systemic-framework.git
cd systemic-framework
dotnet build Systemic.sln --configuration Release
```

A game references `src/Systemic.Core/Systemic.Core.csproj` and any optional module under `src/modules` that it uses. Projects under `samples/` inherit the Kernel generator reference from [`samples/Directory.Build.targets`](samples/Directory.Build.targets); projects elsewhere add it explicitly.

## Usage

The CLI validates content packs, prints rule traces and serves a local preview:

```sh
dotnet run --project src/tools/Systemic.Cli -- validate samples/systemic-sandbox
dotnet run --project src/tools/Systemic.Cli -- trace samples/incremental-quarry incremental.production quarry.ember-source quarry.ore
dotnet run --project src/tools/Systemic.Cli -- preview samples/incremental-quarry
```

`preview` serves a read-only page until you press Ctrl+C. `watch` revalidates a pack when its files change, and `new <directory>` creates a minimal project.

## Samples

- `samples/systemic-sandbox` is a Core-only pack for rule authoring, with no optional module files.
- `samples/incremental-quarry` composes the Incremental, Presentation and Gameplay2D modules. Validate or preview it with the CLI commands above.
- [MonoGame boids](samples/boids-monogame/README.md) runs 100,000 boids on the scheduled path: `dotnet run --configuration Release --project samples/boids-monogame/Boids.MonoGame.csproj`
- [Glow Garden](samples/glow-garden/README.md) is an incremental colony simulation on MonoGame and Gum: `dotnet run --project samples/glow-garden -c Release`
- [Molecule swarm](samples/molecule-swarm/README.md) is a tower defense against a growing swarm: `dotnet run --configuration Release --project samples/molecule-swarm -- --autoplay`
- [Pixel mine](samples/pixel-mine/README.md) is a playable mine of ten million pixels: `dotnet run --configuration Release --project samples/pixel-mine/PixelMine.csproj`
- [Crowd million](samples/crowd-million/README.md) is a content-only pack used by the crowd benchmark.

## Project layout

- `src/Systemic.Kernel` contains world state, deterministic transactions, queries, modifiers, populations, spatial grids and random state.
- `src/Systemic.Core` contains content loading, rules, hosting, save contracts, replay and module registration. It references Kernel only.
- `src/modules/Systemic.Incremental` contains resources, producers, upgrades, advancement choices and their runtime.
- `src/modules/Systemic.Presentation` contains visual recipes, UI layouts, timelines, cue state and local preview tools.
- `src/modules/Systemic.Gameplay2D` contains engine-neutral actor, projectile, navigation, spatial targeting and timed status primitives.
- `src/tools/Systemic.Cli` composes all built-in modules for authoring and preview workflows.
- `tests` holds unit, integration and process tests for each boundary. `benchmarks/Systemic.Benchmarks` contains the performance gates, and `docs` contains the API guides, ADRs and analyzer reference.

Each module references Core and Kernel only. Modules never reference sibling modules. A game registers only the module implementations it uses; the CLI registers all built-in modules.

## Author content

Every project has a `game.json` manifest at schema version 3. Core requires the `entities` and `rules` paths. Optional content is declared under the module ID that owns it: Quarry, for example, declares `systemic.incremental`, `systemic.presentation` and `systemic.gameplay2d`, while a Core-only project omits `modules` entirely. A declared module without a registered loader fails with a diagnostic at its manifest entry.

Core entity visual references are opaque IDs, and the Presentation module validates and resolves them. Cross-module identifiers use Core's generic reference catalog, so modules can validate links without project references to each other. Choice icon assignments are presentation content keyed by choice ID.

The Incremental module compiles resource balances, producers, upgrades, actor roles and advancement choices. Its runtime handles production and choice commands. Choice groups can be registered for multiple owners and persist through module extension state.

The Presentation module compiles bounded pixel recipes, imported asset composition, UI layouts and fact timelines, and its rasterizer produces RGBA buffers for actors and effects. Timeline cues publish only after the enclosing fact transaction commits, and rollback suppresses them. The preview exposes layouts, icons, bars and timeline bindings without placing renderer dependencies in the simulation.

The Gameplay2D module compiles actor blueprints and status profiles, and provides deterministic spawning, movement, expiry, spatial target selection, resource transfer and timed status primitives. Hosts add domain rules through Core's generic system and extension contracts.

## Host and save state

`SystemicGame` and `HeadlessSystemicHost` provide fixed-step execution, deterministic facts, rule traces and save operations. Each `ISystemicSystem.Step` call receives one `SystemicSystemContext` for host and Kernel operations: read-only World access, deferred World commands, rule dispatch, registered services, population leases, range work and rollback hooks. Register a system with `SystemicGameBuilder.AddSystem` and declare its resource and generated-column access through `ISystemicSystem.Access`. Systems with undeclared access use the conservative exclusive `SystemAccess.WriteWorld` default. A system that only queues World commands declares `SystemAccess.DeferWorldWrites` and shares its wave with readers and other buffered writers. The [system context guide](docs/api/system-context.md) and [population and scheduling guide](docs/api/populations-and-scheduling.md) describe the contracts in full.

`SystemicSaveCodec` writes versioned, checksummed world state plus opaque stateful-extension data. The current schema is v6, and a version 5 document migrates by dropping the retired typed event engine's counters. Version 4 and earlier are rejected; version 4 has no migration because it stored modifier durations where version 5 stores absolute expiry ticks. Explicit sequential migrations can upgrade supported v5-and-later documents when the schema advances. Core does not serialize module-specific DTOs directly.

## Population simulation

The Kernel supports dense, row-oriented populations for large visual swarms. Annotate an unmanaged partial struct with `[PopulationSchema("stable.id", version)]` and its fields with `[PopulationColumn("column.id")]`; the generator produces typed column bindings, stable column keys, access declarations and range initialization. Generated schemas work with `PopulationCommandBuffer<TKind>` for transactional batched spawns and removals.

Reference Kernel normally and configure `Systemic.Kernel.Generator` as an analyzer with `OutputItemType="Analyzer"` and `ReferenceOutputAssembly="false"`. The analyzer also provides the determinism and system-access diagnostics described in the [analyzer reference](docs/api/analyzers.md).

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

Row-addressed rules are the crowd tier. A content rule names the population row its fact is about with `factRow`, and `RuleEngine.EvaluateRowEvent` evaluates one event's rules against every row of a population in a single scope: no fact per row, and a leading tag condition reads one packed bit per row instead of the row's mask word. `PopulationSink` collapses rows crossing a threshold into one row-addressed fact. [`samples/crowd-million`](samples/crowd-million/README.md) is a content-only pack for that path. The crowd gate holds one million rows, each evaluating the authored rule every tick with staggered movement, to the simulation's 10 ms share of a 30 Hz frame: 500 ticks after 200 warm-up measure p50 2.8 to 3.3 ms and p95 3.2 to 3.9 ms with zero bytes allocated. A hand-written schema declares its tags and modifier properties through `IPopulationKind`, and `enableHandles: true` lets a fact carry a generation-checked row reference.

`PopulationSpatialGrid2D` builds a dense fixed-bounds 2D grid from position spans. Its short-lived snapshots expose stable exact-radius and bounds queries using caller-owned buffers, and a capped candidate query supports systems that intentionally limit per-row work. Reserve grid rows and query buffers before stepping to avoid warmed allocations.

`SystemicGameBuilder.AddSystem` places each host system in the same deterministic access-declared schedule. Systems are sorted by `Order`, and equal orders retain registration order before scheduling. Conflicting systems preserve this priority, while compatible systems may share a concurrent wave; `SystemAccess.WriteWorld` runs alone. `ISystemicSystem.Access` names World and generated population-column reads and writes. `SystemicSystemContext.ForEachRange` assigns non-overlapping row ranges to workers, and `ReduceRanges` merges partitions in their specified order. A schedule reuses its worker threads across ticks, chooses at most four by default, and is disposed with `SystemicGame`. Kernel-only users can build a lower-level schedule from `IScheduledKernelSystem` with `DeterministicSystemScheduleBuilder`.

Systems must honor every access declaration and write only to their assigned rows. Use `SystemAccess.DeferWorldWrites` for deferred population commands or `SystemAccess.WriteWorld` when a system needs immediate World access, rule dispatch or event dispatch. `SystemicSystemContext.OnRollback` restores external state when a scheduled tick fails. The analyzer catches several direct access and callback violations, but it does not prove whole-program safety; see its [scope and limitations](docs/api/analyzers.md#analysis-boundaries).

Borrow contexts only during the system call. Submit World commands and rollback hooks from the system body; range callbacks write disjoint population slices. Command writers record their execution and reject later use. Cache rollback delegates when you need allocation-free ticks.

## Verify

Run the test suite, the formatting checks and the API documentation check from the repository root:

```sh
dotnet test Systemic.sln --configuration Release
dotnet format Systemic.sln --verify-no-changes
python3 docs/check_api_xml_docs.py --configuration Release
```

The two `dotnet format` severity passes, the docfx build and the allocation gates also run in CI; see [`.github/workflows`](.github/workflows). Save-format snapshots are checked in as Verify `*.verified.json` files next to their tests. A failure writes a `*.received.json` diff, which git ignores. The full v6 save document is one snapshot, so a wire-format change is reviewed as a diff instead of through scattered assertions.

## Benchmarks

The strict gate runner in `benchmarks/Systemic.Benchmarks` measures reusable workloads:

```sh
dotnet run -c Release --project benchmarks/Systemic.Benchmarks -- --gate --scenario all --warmup-ticks 1000 --ticks 500 --repeat 3 --workers 1 --max-tick-ms 5 --tick-log artifacts/kernel.csv
```

It records every measured tick, reports mean, p50, p95, p99 and maximum, and fails if any tick reaches the budget. Kernel scenarios must allocate nothing. `--scenario content` gates 2,000 actors through the entity and rule path at 5 ms with a 4 KiB per-tick allocation budget, which is the only place a frame-time claim is asserted; a wall-clock budget inside the parallel test suite measures the machine rather than the code. `--scenario crowd` gates one million rows against the simulation's 10 ms share of a 30 Hz frame, with a 10 ms budget and zero allocation. `--agents` sets the row count and `--content-root` selects the pack. `--workers` accepts a fixed count; omit it to use the capped machine default. `reactive-query` and `reactive-dispatch` isolate the two stages of the combined actor workload.

`--spatial` measures the three spatial grids on one scene: the unbounded entity index, the rebuilt population grid and the incremental population grid. It cross-checks every radius and bounds query against a brute-force oracle and fails on a parity mismatch or an allocation inside a query:

```sh
dotnet run -c Release --project benchmarks/Systemic.Benchmarks -- --spatial --iterations 60 --repeat 3
```

All three grids share `SpatialGrid`'s cell arithmetic, axis windows, wrap rules and ascending result order; they differ only in storage.

The performance target is an Intel Core i5-6200U-class low-power CPU (2 cores, 4 threads, circa 2015 and 2016), with a strict per-tick limit below 5 ms and zero warmed allocations. The available runner is an AMD EPYC 9V74, with benchmark containers capped at two CPUs for a rough core-count comparison. That does not model the target's clock, cache or hyper-threading behavior, so it cannot certify the laptop. The Boids sample's `--strict-budget` headless command measures the full 100,000-boid scheduled path under the same gate. BenchmarkDotNet cases cover grid, scheduler, transaction and reactive query costs. The anonymized behavior matrix is in `tests/Systemic.Integration.Tests/Fixtures/talent-pattern-coverage.json`.

## License

This repository does not include a license file yet, so the default copyright rules apply. Ask the maintainer before reusing the code outside this project.

## Contributors and contact

Systemic is maintained by Fábio Rodrigues Sousa ([@cakegod](https://github.com/cakegod)). Open an issue or a pull request in this repository. The [contributor graph](https://github.com/cakegod/systemic-framework/graphs/contributors) lists everyone who has contributed.

The projects target .NET 10. Core uses `Corvus.Text.Json.Validator` for JSON Schema validation, and the CLI uses `System.CommandLine`; neither introduces an engine dependency. Games consume the framework as a library and supply their own engine adapter, so no engine type is a dependency of the framework.
