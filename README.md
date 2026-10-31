<div align="center">

# Systemic

[![Quality gates](https://github.com/cakegod/systemic-framework/actions/workflows/quality-gates.yml/badge.svg)](https://github.com/cakegod/systemic-framework/actions/workflows/quality-gates.yml)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)

</div>

Systemic is a .NET framework for deterministic, systemic games built from general
interacting rules. Behavior is C# systems and commands. JSON is data: entities, tuning, UI,
visuals, presentation, and saves.

The goal is not a larger authored choice tree. The developer supplies rules, pressures,
verbs, and constraints; players combine them into strategies and goals the developer did
not enumerate. Systemic means composable. Synergetic means mechanics change each other.
Emergent means coherent play appears from those interactions without a named branch for
every combination.

The framework does not ship a Talent, Merge, Prestige, Upgrade, or Producer subsystem. A
talent is a data record plus a behavior function. A merge is a command over two entities.
Prestige is a command that clears a scope. A producer is an interval system. The composition
layer is the point. See the [agent-first plan](docs/agent-first-plan.md) and
[ADR 0004](docs/adr/0004-code-first-simulation.md).

The design bar is expressive agency through systemic emergence. The player should feel they
are becoming something of their own choosing. Every choice must open a distinct way to
play, carry a real trade-off, and interact with other mechanics. The framework supplies the
general primitives and guardrails; the game is built systemically so those interactions can
compound into strategies the developer did not enumerate. Synergy and emergence are
outcomes to design for and validate, not guarantees. A strictly better stat upgrade is a
failed choice, and a rigid choice tree is a failed game. Gnorp is a reference corpus, not a
template; the goal is an original game. See
[expressive agency and emergence](docs/design/expressive-agency.md).

## Current direction

The JSON rule engine is legacy. It became an underpowered programming language and could
not express the 41-talent acceptance test cleanly. It is frozen now and deleted after the
samples and tests move to code-first modules.

The target is:

- one execution model: `ISystemicSystem`;
- typed commands for input, UI, and mutations;
- all simulation state in the World or a saved extension;
- JSON for data, tuning, UI, art, and saves;
- game-side pattern code for producers, upgrades, talents, merges, prestige, and workers;
- save and replay keyed by content fingerprint and game module version.

## Quick start

Requires the .NET 10 SDK.

```sh
git clone https://github.com/cakegod/systemic-framework.git
cd systemic-framework
./bootstrap.sh
```

The bootstrap builds the solution, creates a temporary game, validates it, runs it, verifies
it, and simulates ten seconds.

To work on a game:

```sh
dotnet run --project src/tools/Systemic.Cli -- new my-game
systemic validate my-game
dotnet run --project my-game -- --headless --ticks 600
dotnet run --project my-game -- verify --ticks 600
dotnet run --project my-game -- simulate --seconds 3600 --csv artifacts/simulate.csv
```

`new` writes a playable game template with a data pack, a C# game module, a scheduled system,
a declarative HUD, and three procedural recipes. The game executable owns the simulation
host. The CLI owns validation and assets.

## CLI

The `systemic` command is for content and assets:

| Command | Purpose |
| --- | --- |
| `new <dir>` | Create the playable game template |
| `validate <dir>` | Validate schemas, references, and content data |
| `dump <dir>` | Print entities, properties, and the content hash |
| `assets sheet <dir> --out path` | Render a labeled contact sheet |
| `assets validate <dir>` | Check every visual recipe |
| `assets atlas <dir> --out path --manifest path` | Pack recipes into an atlas |

Every command accepts `--json`. Exit codes are `0` success, `1` content error, `2` usage,
`3` runtime, and `4` verification mismatch.

Errors use a stable envelope:

```json
{
  "ok": false,
  "code": "content.invalid",
  "message": "Content pack directory does not exist: /tmp/missing"
}
```

The envelope also carries diagnostic details with `file`, `path`, `line`, and `column` when
content loading produced them.

## Content and behavior

A pack has two required files:

- `game.json`: manifest, tick rate, content paths, and optional module declarations.
- `entities.json`: entities, tags, numeric properties, links, and visual recipe IDs.

Optional declarative presentation lives in `ui.json`, `visuals.json`, and
`presentation.json`. The schemas are in `schemas/`, and every diagnostic names the file and
JSON path.

`rules.json` is a legacy reference format. New packs do not have one. Do not add conditions,
effects, schedules, expressions, or facts to it. A pack without `rules.json` loads with an
empty rule set and still validates its entities and tuning data.

Behavior lives in a game module:

```csharp
public sealed class GameModule : ISystemicGameModule
{
    public string Id => "game";
    public int Version => 1;

    public void Compose(SystemicGameBuilder builder)
    {
        builder.AddSystem(new EconomySystem());
        builder.AddSystem(new ChoiceSystem());
        builder.AddCommand<SelectChoice, SelectChoiceHandler>();
        builder.BindUiAction<SelectChoice>("select", action =>
            new SelectChoice(action.OwnerEntityId));
        builder.AddEvent<ChoiceSelected, ChoicePresentation>();
    }
}
```

Typed commands run in their own World transaction. UI actions resolve to a bound command;
the click does not become a rule fact. Typed events dispatch in registration order with a
read-only World view. A UI binding whose command has no handler fails when the game builds.

A talent is data plus a small function:

```json
{
  "id": "talent.default.motivation",
  "group": "default",
  "cost": 1,
  "tags": ["ui.choice", "talent.default.motivation"],
  "description": "Low progress: faster abilities, but no healing."
}
```

```csharp
static void Motivation(in TalentContext context)
{
    if (context.Runner.Progress >= 0.5)
    {
        return;
    }

    // A redesign target: low progress changes risk and timing, not just damage.
    context.Runner.AddTag("cannot-heal");
    context.Runner.AddModifier("abilityCooldown", ModifierOperation.Multiply, 0.5, ticks: 1);
}
```

No `Talent` framework type. No rule fragment. No hidden routing string.

## Determinism

The host runs fixed ticks. Authoritative simulation state uses integers or fixed point.
Systems are stateless; timers, cooldowns, ranks, and counters live in the World. The strict
determinism analyzer applies to game systems.

`verify` checks that two fresh runs finish with the same hash and that a save restores to
that hash. `replay record` and `replay verify` compare hashes on every tick. Saves carry the
game module and content module versions, so a behavior change rejects stale state and names
the module and both versions.

## Samples

| Sample | Shows | Run |
| --- | --- | --- |
| [`systemic-sandbox`](samples/systemic-sandbox) | A legacy Core-only rule pack | `systemic validate samples/systemic-sandbox` |
| [`pattern-pack`](samples/pattern-pack) | Legacy rule fragments; superseded by game-side patterns | `systemic validate samples/pattern-pack` |
| [`incremental-quarry`](samples/incremental-quarry) | Incremental, Presentation and Gameplay2D composed in one pack | `systemic validate samples/incremental-quarry` |
| [`module-pack`](samples/module-pack) | A code-first game module over a pack with no rules file | `dotnet test tests/Systemic.ModulePack.Tests` |
| [`boids-monogame`](samples/boids-monogame/README.md) | 100,000 boids on the scheduled path | `dotnet run -c Release --project samples/boids-monogame/Boids.MonoGame.csproj` |
| [`glow-garden`](samples/glow-garden/README.md) | A playable incremental colony | `dotnet run -c Release --project samples/glow-garden` |
| [`pixel-mine`](samples/pixel-mine/README.md) | A playable procedural mine | `dotnet run -c Release --project samples/pixel-mine/PixelMine.csproj` |
| [`molecule-swarm`](samples/molecule-swarm/README.md) | A tower defense against a growing swarm | `dotnet run -c Release --project samples/molecule-swarm -- --autoplay` |

The samples are references and diagnostics. They are not the target architecture. The
samples that still use `rules.json` are legacy and are ported or retired before the rule
engine is deleted. The million-row crowd and ten-million-pixel benchmarks remain as
headroom checks, not release gates.

## Documentation

- [Agent-first plan](docs/agent-first-plan.md) is the current direction and gates.
- [Phase 1 checklist](docs/phase-1-checklist.md) tracks the code-first and asset slices.
- [Agent recipes](docs/agents) cover the content, run, save, and asset loops.
- [Framework guides](docs/index.md) cover the system context, populations, and analyzers.
- [ADR 0004](docs/adr/0004-code-first-simulation.md) records the code-first decision.
- [Expressive agency and emergence](docs/design/expressive-agency.md) is the design bar for
  every choice, mechanic, and interaction.

## Verify

```sh
dotnet build Systemic.sln --configuration Release
scripts/loop.sh --full
scripts/loop.sh --samples
```

The gate covers the kernel, host, saves, replays, and content validation. The sample suites
that drive the playable games run separately through `--samples`.

## Scope

This is a personal project. No license is granted for reuse. There is no package, support
promise, or external API compatibility commitment.
