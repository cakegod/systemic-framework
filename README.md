<div align="center">

# Systemic

[![Quality gates](https://github.com/cakegod/systemic-framework/actions/workflows/quality-gates.yml/badge.svg)](https://github.com/cakegod/systemic-framework/actions/workflows/quality-gates.yml)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)

</div>

Systemic is a .NET framework for games that need a deterministic simulation and content that designers can edit without recompiling. The simulation, rules, saves and replays live in the framework. Presentation, progression and engine adapters are optional.

A Core-only game can load a JSON content pack, tick a fixed-step host, save the world and replay the run. Games that need more opt into modules for incremental progression, presentation cues or 2D gameplay. The same seed and inputs produce the same world state, save document and replay hash.

## Highlights

- Runs are deterministic. Seeded random, fixed-point math and a fixed-step host reproduce a run on another machine, after a save, or during replay.
- Modules are optional. Kernel and Core have no engine dependency, and Incremental, Presentation, Gameplay2D and Rendering are separate assemblies registered only when a game uses them.
- Content stays in JSON. Entities, rules, tuning, visuals and UI layouts are validated at load, and every error reports its file and line.
- Populations scale to a million rows. Generated column schemas and row-addressed rules process them inside a 30 Hz frame with no per-tick allocation.
- State recovers from failure. Transactions roll back structural edits, modifiers, population rows and rule effects, and saves migrate forward between schema versions.

## Features

- [x] Fixed-step host with deterministic fact dispatch and rule traces
- [x] Seeded random, fixed-point math and deterministic trigonometry
- [x] Transactional world with rollback for entities, tags, grants, modifiers and population rows
- [x] Entities with tags, capabilities, links, properties and spatial queries
- [x] JSON content packs with schema validation and source diagnostics
- [x] Rules with conditions, priorities, fragments, schedules and traces
- [x] Row-addressed rules, aggregate sinks and generation-checked row references
- [x] Source-generated population schemas with typed column access
- [x] Population tags as bit masks, inline modifiers and fixed-point columns
- [x] Rebuilt and incremental spatial grids with radius and nearest queries
- [x] Scheduled systems with declared data access and parallel range work
- [x] Versioned, checksummed saves with explicit migrations
- [x] Deterministic replays with hash comparison
- [x] Typed tuning schemas and prepared content reloads
- [x] CLI for new, validate, watch, preview and trace
- [x] Compile-time analyzers for determinism and system access
- [x] Benchmark tiers with zero-allocation and frame-budget gates
- [x] Samples for MonoGame, SDL/OpenGL and content-only packs
- [x] .NET 10

## Installation

There is no NuGet package. Clone the repository and build it from source:

```sh
git clone https://github.com/cakegod/systemic-framework.git
cd systemic-framework
dotnet build Systemic.sln --configuration Release
```

A game references `src/Systemic.Core/Systemic.Core.csproj` and any optional module under `src/modules` that it uses. Projects under `samples/` inherit the Kernel generator reference from [`samples/Directory.Build.targets`](samples/Directory.Build.targets); projects elsewhere add it explicitly.

## Quick start

Create and validate a content pack:

```sh
dotnet run --project src/tools/Systemic.Cli -- new my-game
dotnet run --project src/tools/Systemic.Cli -- validate my-game
```

Inspect how a rule chain dispatches:

```sh
dotnet run --project src/tools/Systemic.Cli -- trace samples/incremental-quarry incremental.production quarry.ember-source quarry.ore
```

```text
[0] depth=0 incremental.production (quarry.ember-source -> quarry.ore) rules: quarry.ember-drill-produces-ore
[1] depth=1 quarry.ember-ore-produced (quarry.ember-source -> quarry.ore) rules: quarry.ore-production-connects-to-ember
Facts: 2; rules: 2; effects: 3; cutoff: none; rolled back: False
```

The CLI can also serve a read-only preview (`preview <directory>`), revalidate a pack when its files change (`watch <directory>`), and change the preview port with `--port`.

## Examples

### Rules in JSON

A rule names the fact it reacts to, the conditions on the fact or a population row, and the effects it applies. This is the crowd rule from [`samples/crowd-million`](samples/crowd-million/README.md):

```json
{
  "id": "crowd.prey.burn",
  "on": "crowd.prey.think",
  "priority": 0,
  "when": [
    { "kind": "hasTag", "factRow": "crowd.prey", "tag": "burning" },
    { "kind": "propertyAtLeast", "factRow": "crowd.prey", "property": "crowd.prey.energy", "value": 1 }
  ],
  "effects": [
    { "kind": "adjustProperty", "factRow": "crowd.prey", "property": "crowd.prey.energy", "amount": -1 }
  ]
}
```

### Populations in C#

Large uniform swarms use a generated schema and typed columns:

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

The [system context guide](docs/api/system-context.md) shows a host system built on this population, and the [compile-checked example](tests/Systemic.Core.Tests/Documentation/ApiExamples.cs) is part of the test suite.

## Samples

| Sample | Shows | Run |
| --- | --- | --- |
| [`systemic-sandbox`](samples/systemic-sandbox) | A Core-only pack with rules and no optional modules | `validate samples/systemic-sandbox` |
| [`incremental-quarry`](samples/incremental-quarry) | Incremental, Presentation and Gameplay2D composed in one pack | `preview samples/incremental-quarry` |
| [`boids-monogame`](samples/boids-monogame/README.md) | 100,000 boids on the scheduled path with MonoGame | `dotnet run -c Release --project samples/boids-monogame/Boids.MonoGame.csproj` |
| [`glow-garden`](samples/glow-garden/README.md) | An incremental colony simulation on MonoGame and Gum | `dotnet run -c Release --project samples/glow-garden` |
| [`pixel-mine`](samples/pixel-mine/README.md) | A playable mine of ten million pixels with MonoGame | `dotnet run -c Release --project samples/pixel-mine/PixelMine.csproj` |
| [`molecule-swarm`](samples/molecule-swarm/README.md) | A tower defense against a growing swarm with SDL/OpenGL | `dotnet run -c Release --project samples/molecule-swarm -- --autoplay` |
| [`crowd-million`](samples/crowd-million/README.md) | A content-only pack for the crowd benchmark tier | `dotnet run -c Release --project benchmarks/Systemic.Benchmarks -- --gate --scenario crowd` |

The first two rows run through the CLI from the Quick start, for example `dotnet run --project src/tools/Systemic.Cli -- validate samples/systemic-sandbox`.

## Benchmarks

The gate runner measures reusable workloads and fails when a tick crosses its budget:

```sh
dotnet run -c Release --project benchmarks/Systemic.Benchmarks -- --gate --scenario all --warmup-ticks 1000 --ticks 500 --repeat 3 --workers 1 --max-tick-ms 5 --tick-log artifacts/kernel.csv
```

| Tier | Workload | Budget | Result |
| --- | --- | --- | --- |
| Crowd | 1,000,000 rows, each evaluating an authored rule every tick | 10 ms, the simulation's share of a 30 Hz frame | p50 2.8 to 3.3 ms, p95 3.2 to 3.9 ms, zero bytes per tick |
| Content | 2,000 actors through the entity and rule path | 5 ms and 4 KiB per tick | passes on the gate runner |
| Kernel | 50,000 producers and a reactive actor workload | zero allocated bytes per tick | passes on the gate runner |

`--scenario crowd`, `--scenario content`, `--agents`, `--content-root` and `--workers` select a tier and its shape. `--spatial` measures the three spatial grids and cross-checks every query against a brute-force oracle. These numbers come from the available runner, an AMD EPYC 9V74 capped at two CPUs. The documented target is a 2015-era low-power laptop, and this runner cannot certify it.

## Documentation

- [Framework guides](docs/index.md) cover the system context, populations and scheduling, and the analyzer reference.
- [ADRs](docs/adr/0001-populations-as-rule-subjects.md) record the decisions behind populations as rule subjects and the expression language.
- [Analyzer reference](docs/api/analyzers.md) lists the `SYSDET` and `SYSACC` diagnostics and their limits.
- [API reference](docs/reference/Systemic.Kernel.yml) is generated from XML documentation.

## Verify

Run the test suite, the formatting check and the API documentation check from the repository root:

```sh
dotnet test Systemic.sln --configuration Release
dotnet format Systemic.sln --verify-no-changes
python3 docs/check_api_xml_docs.py --configuration Release
```

CI runs the two stricter format passes, docfx, the replay fixture on Linux and Windows, and the allocation gates.

## License

This repository does not include a license file yet, so the default copyright rules apply. Ask the maintainer before reusing the code outside this project.

## Contributing and contact

Systemic is maintained by Fábio Rodrigues Sousa ([@cakegod](https://github.com/cakegod)). Open an issue or a pull request in this repository. The [contributor graph](https://github.com/cakegod/systemic-framework/graphs/contributors) lists everyone who has contributed.
