<div align="center">

# Systemic

[![Quality gates](https://github.com/cakegod/systemic-framework/actions/workflows/quality-gates.yml/badge.svg)](https://github.com/cakegod/systemic-framework/actions/workflows/quality-gates.yml)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)

</div>

Systemic is a .NET framework for deterministic, content-driven games. The current target is small-scope incremental and colony games, with agents, producers, upgrades, merges, and prestige expressed as content patterns over general primitives.

The framework does not ship a Talent, Merge, Prestige, Upgrade, or Producer subsystem. A talent is a tag plus rules. A merge is a rule over two entities. Prestige is a rule over a reset fact. The composition layer is the point. See the [agent-first plan](docs/agent-first-plan.md) for the full direction.

## Phase 0

Phase 0 is the agent loop:

- Create a playable pack from a template.
- Validate it, run it headless, fast-forward it, save it, load it, and verify it.
- Record and verify a deterministic replay.
- Render procedural visual recipes to a contact sheet and an atlas.
- Do all of it with `--json` and stable exit codes.

That loop is implemented. Phase 1 has started with declared facts: a fact can name a typed payload, and emitted and scheduled facts carry it through the same dispatch path. The [Incremental audit](docs/phase-1-incremental-audit.md) freezes the old module and maps its behaviour to general patterns. Queries, the broader effect set, and the 41-talent pack are next.

## Quick start

Requires the .NET 10 SDK.

```sh
git clone https://github.com/cakegod/systemic-framework.git
cd systemic-framework
./bootstrap.sh
```

The bootstrap builds the solution, creates a temporary game, validates it, runs it, verifies it, and simulates ten seconds.

To work on a game:

```sh
dotnet run --project src/tools/Systemic.Cli -- new my-game
dotnet run --project src/tools/Systemic.Cli -- run my-game --ticks 600
dotnet run --project src/tools/Systemic.Cli -- verify my-game --ticks 600
dotnet run --project src/tools/Systemic.Cli -- simulate my-game --seconds 3600 --csv artifacts/simulate.csv
```

`new` writes a valid pack with a resource, a generator, a scheduled rule, a declarative HUD, and three procedural recipes. It is a game loop, not an empty manifest.

## CLI

| Command | Purpose |
| --- | --- |
| `new <dir>` | Create the playable template |
| `validate <dir>` | Validate schemas, references, and content |
| `run <dir> --ticks N` | Step the headless host |
| `simulate <dir> --seconds N --csv path` | Fast-forward and write a balance CSV |
| `dump <dir>` | Print entities, properties, and the state hash |
| `save <dir> --out path` | Run and write a checksummed save |
| `load <dir> --save path` | Restore a save |
| `verify <dir> --ticks N` | Run twice, restore a save, and compare hashes |
| `replay record <dir> --out path` | Record a replay |
| `replay verify <dir> --save path` | Verify a replay tick by tick |
| `assets sheet <dir> --out path` | Render a labeled contact sheet |
| `assets validate <dir>` | Check every visual recipe |
| `assets atlas <dir> --out path --manifest path` | Pack recipes into an atlas |

Every command except `watch` and `preview` accepts `--json`. Exit codes are `0` success, `1` content error, `2` usage, `3` runtime, and `4` verification mismatch.

Errors use a stable envelope:

```json
{
  "ok": false,
  "code": "content.invalid",
  "message": "Content pack directory does not exist: /tmp/missing"
}
```

The envelope also carries diagnostic details with `file`, `path`, `line`, and `column` when content loading produced them.

## Content

A pack has three required files:

- `game.json`: manifest, tick rate, file paths, and optional modules.
- `entities.json`: entities, tags, numeric properties, links, and visual recipe IDs.
- `rules.json`: facts, schedules, rules, conditions, effects, and fragments.

Optional declarative presentation lives in `ui.json`, `visuals.json`, and `presentation.json`. The schemas are in `schemas/`, and every diagnostic names the file and JSON path.

A schedule and a rule are enough to express a producer:

```json
{
  "schemaVersion": 2,
  "schedules": [
    {
      "id": "demo.pulse",
      "event": "demo.tick",
      "everyTicks": 60,
      "sourceEntityId": "actor.generator",
      "targetEntityId": "resource.energy"
    }
  ],
  "rules": [
    {
      "id": "demo.produce",
      "on": "demo.tick",
      "priority": 0,
      "when": [{ "kind": "hasTag", "subject": "source", "tag": "generator" }],
      "effects": [
        { "kind": "adjustProperty", "subject": "target", "property": "balance", "amount": 1 }
      ]
    }
  ]
}
```

## Determinism

The host runs fixed ticks. The simulation uses seeded random streams and integer or fixed-point state. The same seed, content fingerprint, and inputs produce the same save state and replay hash.

`verify` checks three things: two fresh runs match, a save restores to the same hash, and a replay advances to the same hash on every tick. A mismatch exits with code 4.

## Samples

| Sample | Shows | Run |
| --- | --- | --- |
| [`systemic-sandbox`](samples/systemic-sandbox) | A Core-only pack with rules | `validate samples/systemic-sandbox` |
| [`incremental-quarry`](samples/incremental-quarry) | Incremental, Presentation and Gameplay2D composed in one pack | `preview samples/incremental-quarry` |
| [`boids-monogame`](samples/boids-monogame/README.md) | 100,000 boids on the scheduled path | `dotnet run -c Release --project samples/boids-monogame/Boids.MonoGame.csproj` |
| [`glow-garden`](samples/glow-garden/README.md) | A playable incremental colony | `dotnet run -c Release --project samples/glow-garden` |
| [`pixel-mine`](samples/pixel-mine/README.md) | A playable procedural mine | `dotnet run -c Release --project samples/pixel-mine/PixelMine.csproj` |
| [`molecule-swarm`](samples/molecule-swarm/README.md) | A tower defense against a growing swarm | `dotnet run -c Release --project samples/molecule-swarm -- --autoplay` |

The samples are references and diagnostics. They are not the target architecture. The million-row crowd and ten-million-pixel benchmarks remain in the repository as headroom checks, not release gates.

## Documentation

- [Agent-first plan](docs/agent-first-plan.md) is the current direction and gates.
- [Phase 1 checklist](docs/phase-1-checklist.md) tracks every remaining composition and asset item.
- [Incremental audit](docs/phase-1-incremental-audit.md) records the Phase 1.1 freeze and pattern mapping.
- [Agent recipes](docs/agents) cover the run, content, asset, and save loops.
- [Framework guides](docs/index.md) cover the system context, populations, and analyzers.
- [ADRs](docs/adr/0001-populations-as-rule-subjects.md) record the population and expression decisions.

## Verify

```sh
dotnet build Systemic.sln --configuration Release
dotnet test Systemic.sln --configuration Release
dotnet test tests/Systemic.Cli.Tests --configuration Release
```

The CLI tests create a template, run it, verify it, and check the JSON error envelope. The full suite also covers populations, rules, saves, replays, and the samples.

## Scope

This is a personal project. No license is granted for reuse. There is no package, support promise, or external API compatibility commitment.
