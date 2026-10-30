# AGENTS

Use conventional commit messages.

## Current goal

Finish Phase 1 from `docs/agent-first-plan.md`. Track every item in
`docs/phase-1-checklist.md`. Do not start Phase 2 until G1 passes. Each unchecked item
is a TDD slice:
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

## CLI

Run the CLI from the repository root:

```sh
dotnet run --project src/tools/Systemic.Cli --configuration Release -- <command>
```

Phase 0 commands:

```sh
systemic new <directory> [--template systemic]
systemic validate <directory>
systemic run <directory> --ticks 600 [--seed 1] [--json]
systemic simulate <directory> --seconds 3600 [--csv artifacts/simulate.csv] [--json]
systemic verify <directory> --ticks 600 [--json]
systemic dump <directory> [--json]
systemic save <directory> --out save.json --ticks 600
systemic load <directory> --save save.json
systemic replay record <directory> --out replay.json --ticks 600
systemic replay verify <directory> --save replay.json
systemic assets sheet <directory> --out artifacts/assets.png
systemic assets validate <directory>
systemic assets atlas <directory> --out content/atlas.png --manifest content/atlas.json
```

Every new command accepts `--json`. Errors use a stable envelope with `code`, `message`, and, where available, `file`, `path`, `line`, `column`, `docs`, and `hint`.

Exit codes: `0` success, `1` content error, `2` usage, `3` runtime, `4` verification mismatch.

## Content map

- `game.json`: manifest, tick rate, file paths, optional module declarations.
- `entities.json`: entities, archetypes, tags, properties, links, visual recipe IDs.
- `rules.json`: schedules, rules, fragments. Conditions and effects are bounded and validated at load.
- `incremental.json`, `advancements.json`: frozen legacy schema. Do not add genre features here.
- `ui.json`, `visuals.json`, `presentation.json`: declarative UI, procedural recipes, timelines and cues.

Content files are JSON with strict schemas in `schemas/`. Every diagnostic carries a file and path. Load the pack after every content edit.

## Rules for changes

- Adding a new feature must always use TDD: write a failing test first (red), make the smallest change that passes (green), then refactor. Repeat until the behavior is complete.
- The framework must not add a type, method, or module named Talent, Merge, Prestige, Upgrade, Producer, or Colony. Express those as content patterns over general primitives.
- Authoritative simulation state uses integer or fixed-point math. Do not use floats, ambient time, or unordered iteration in systems.
- Declare every column and World access a system uses. A wrong declaration is a race, not a performance hint.
- Do not edit generated population code. Change the schema and rebuild.
- Keep save and replay deterministic. If a change affects state, add or update a hash comparison.
- Do not weaken or skip tests to make a change pass.

## File map

- `src/Systemic.Kernel`: World, entities, populations, spatial indexes, deterministic primitives.
- `src/Systemic.Core`: content loading, host, rules, save, replay, CLI-facing APIs.
- `src/modules`: optional Incremental, Presentation, Gameplay2D, Rendering modules.
- `src/tools/Systemic.Cli`: the `systemic` command.
- `tests`: unit, integration, CLI, and sample tests.
- `samples`: playable reference games and content packs.

## Common failures

- `Content error:` means a JSON file failed schema or semantic validation. Read the file and path in the message.
- `Unknown content entity` means a rule or schedule names an entity that is not in `entities.json`.
- A save fingerprint mismatch means the content changed after the save. Recreate the save or add a migration.
- A replay mismatch means the same seed and inputs produced different state. Do not update the expected hash without finding the cause.
- `SYSACC` diagnostics mean the system's declared access does not match its actual column or World use.
