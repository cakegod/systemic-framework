# AGENTS

Use conventional commit messages.

## Current goal

Finish Phase 1 from `docs/agent-first-plan.md`. Track every item in
`docs/phase-1-checklist.md`. Do not start Phase 2 until G1 passes.

The target is code-first: C# systems and commands own behavior; JSON is data. The JSON rule
engine is legacy, frozen, and scheduled for deletion. Do not add JSON rule conditions,
effects, subjects, schedules, expressions, or facts. Do not build a typed rule builder.
Every choice must pass the [expressive-agency test](docs/design/expressive-agency.md): it
must open a distinct way to play, carry a real trade-off, and interact with other mechanics.
The framework supplies the primitives and enforces systemic composition; the game must be
built so those mechanics can interact. Synergy and emergent play are outcomes to design for
and validate, not guarantees. They are acceptance criteria, not flavor. A strictly better
stat upgrade or a rigid choice tree is a failed design. Each unchecked item is a TDD slice:
Red — think about what you want to develop
Green — think about how to make your tests pass
Refactor — think about how to improve your existing implementation

## Build and test

```sh
scripts/loop.sh --full          # build and test every framework suite
scripts/loop.sh --format        # formatting, before a commit
```

The Boids, GlowGarden, and PixelMine suites drive the playable games and cost about two minutes
together, so the gate skips them. Run them on demand when a sample changed:

```sh
scripts/loop.sh --samples
```

`dotnet format` spends most of its time loading projects, so scope it to the file you touched
rather than the solution:

```sh
dotnet format whitespace src/Systemic.Kernel/Systemic.Kernel.csproj --include src/Systemic.Kernel/World.cs
```

A focused test filter is faster than the full suite:

```sh
dotnet test tests/Systemic.Core.Tests --configuration Release --filter FullyQualifiedName~Population
```

## CLI and game host

The `systemic` CLI validates content and renders assets:

```sh
systemic new <directory>
systemic validate <directory>
systemic dump <directory> [--json]
systemic assets sheet <directory> --out artifacts/assets.png
systemic assets validate <directory>
systemic assets atlas <directory> --out content/atlas.png --manifest content/atlas.json
```

The game executable owns the simulation host:

```sh
dotnet run --project my-game -- --headless --ticks 600 [--seed 1]
dotnet run --project my-game -- simulate --seconds 3600 [--csv artifacts/simulate.csv]
dotnet run --project my-game -- verify --ticks 600
dotnet run --project my-game -- save --out save.json --ticks 600
dotnet run --project my-game -- load --save save.json
dotnet run --project my-game -- replay record --out replay.json --ticks 600
dotnet run --project my-game -- replay verify --save replay.json
```

Every command accepts `--json` where the host implements it. Errors use a stable envelope
with `code`, `message`, and, where available, `file`, `path`, `line`, `column`, `docs`, and
`hint`.

Exit codes: `0` success, `1` content error, `2` usage, `3` runtime, `4` verification mismatch.

## Content map

- `game.json`: manifest, tick rate, content paths, optional module declarations.
- `entities.json`: entities, archetypes, tags, properties, links, visual recipe IDs.
- `ui.json`, `visuals.json`, `presentation.json`: declarative UI, procedural recipes,
  timelines and cues.
- Tuning documents declared by a module.
- `rules.json`: legacy reference only. Do not add one to a new pack.
- `incremental.json`, `advancements.json`: frozen legacy schema. Do not add genre features.

Content files are JSON with strict schemas in `schemas/`. Every diagnostic carries a file and
path. Load the pack after every content edit.

## Rules for changes

- Adding a new feature must always use TDD: write a failing test first (red), make the
  smallest change that passes (green), then refactor. Repeat until the behavior is complete.
- The framework must not add a type, method, or module named Talent, Merge, Prestige,
  Upgrade, Producer, or Colony. Express those as game-side patterns over general primitives.
- A choice must be expressive, not merely stronger. Write its identity, new decisions,
  trade-off, two synergies, supported playstyles, and competing-choice case. Reject or
  redesign a strict stat upgrade.
- Implement interactions through general rules. A branch named after one combination is a
  special case: generalise the primitive or cut it. Unexpected but coherent combinations
  are the goal, not a bug.
- Do not clone Gnorp. Use it as a reference corpus for the composition stress test; design
  original fiction, systems, and choices for the game being built.
- Behavior is C# systems and commands. Do not add a second execution model.
- Systems are stateless. Timers, cooldowns, ranks, selection, and counters live in the World
  or a saved extension.
- Authoritative simulation state uses integer or fixed-point math. Do not use floats,
  ambient time, or unordered iteration in systems.
- Declare every column and World access a system uses. A wrong declaration is a race, not a
  performance hint.
- Do not edit generated population code. Change the schema and rebuild.
- Keep save and replay deterministic. If a change affects state or behavior, update the
  module version and the hash comparison.
- Do not weaken or skip tests to make a change pass.

## File map

- `src/Systemic.Kernel`: World, entities, populations, spatial indexes, deterministic
  primitives.
- `src/Systemic.Core`: content loading, host, commands, save, replay, CLI-facing APIs.
  The legacy rule engine remains here until 1.26.
- `src/modules`: optional Incremental, Presentation, Gameplay2D, Rendering modules.
- `src/tools/Systemic.Cli`: the `systemic` content and asset command.
- `tests`: unit, integration, CLI, and sample tests.
- `samples`: playable reference games and content packs.

## Common failures

- `Content error:` means a JSON file failed schema or semantic validation. Read the file and
  path in the message.
- `Unknown content entity` means a game command or system names an entity that is not in
  `entities.json`.
- A save fingerprint mismatch means the content or game module version changed after the
  save. Create a new save or add a migration.
- A replay mismatch means the same seed, inputs, and module version produced different
  state. Do not update the expected hash without finding the cause.
- `SYSACC` diagnostics mean the system's declared access does not match its actual column or
  World use.
- `SYSDET` diagnostics mean simulation code uses a float, ambient time, or unordered
  iteration. Fix the code or move the value out of authoritative state.
