# Systemic game plan (personal use)

Status: Phase 0 complete; Phase 1 code-first migration in progress.
Date: 2026-10-31.
Execution checklist: [phase-1-checklist.md](phase-1-checklist.md)
Scope: one developer, one repository, no external users.
Target: original systemic games whose general mechanics combine into strategies, goals, and
identities the developer did not enumerate. A second game with different mechanics proves
the framework did not bake the genre in. Gnorp and Idle Colony are selective references, not
templates.
Design bar: choices express identity, interact with other systems, and aim for emergent
play. The framework supplies primitives and structural enforcement; it cannot guarantee
synergy or emergence. Numeric upgrades and scripted choice trees fail. See
[expressive agency and emergence](design/expressive-agency.md).

## The correction

The framework may not contain a type, method, or module named Talent, Merge, Prestige,
Upgrade, Producer, or Colony. Those are game concepts.

Behavior is C# systems and commands. Data is JSON. A talent is a data record plus a small
behavior function. A merge is a command over two entities. Prestige is a command that
clears a scope. A producer is an interval system. An upgrade is a costed command plus a
rank property. None of them need a framework feature.

The 41 Gnorp talents are a reference corpus and an expressiveness test, not a design to
copy. The goal is original systems whose choices and rules combine into authorship,
including strategies the developer did not enumerate. The second game, a merge-focused game
with no talents, proves the framework is not secretly a Gnorp engine.

## The code-first correction (2026-10-31)

The JSON rule layer became an underpowered programming language: conditions, effects,
expressions, queries, schedules, and a rule engine to interpret them. It hid routing in
strings. The 41-talent pack passed tests while the host path did nothing, and several
mechanics needed flags or test-only inputs to appear to work.

A typed rule builder would be a third language over the same IR. The project does not need
non-programmer authoring or hot reload. It needs direct, deterministic simulation code.

The decision is:

- `ISystemicSystem` and typed commands are the only execution model.
- JSON is data: entities, tuning, UI, visuals, presentation, and saves.
- The JSON rule engine, expression language, rule schema, and rule history state are
  removed after the samples and tests are ported.
- The old rule path is frozen now. Do not add conditions, effects, subjects, schedules,
  expressions, or facts to it.

See [ADR 0004](adr/0004-code-first-simulation.md).

## The goal, stated as a test

I write one paragraph for an original game. My coding agent, working in this repository,
produces a playable build in one day. A reference-quality slice takes three weeks. The 41
Gnorp talents are a stress test of the composition layer, not a cloning target: they become
data plus behavior functions, with no framework type called Talent, and the game is free to
redesign or replace them. The game must reward combinations, not only selections: a player
should be able to find a coherent strategy the plan did not name, and the game should
support it without a special case. A second game with different mechanics, including
merging but no talents, uses the same primitives and requires no framework change.

Adding a talent is one data record and a small behavior function, and takes five minutes.
Adding a creature or icon is a recipe edit and takes ten minutes. Adding a mechanic the
framework has never seen is a game-side system or command plus data.

## The design bar: expressive agency through emergence

The player should feel that they are not merely becoming stronger, but becoming something
of their own choosing. Each option must open a distinct, desirable future. The choice is an
expression of identity, an invitation to experiment, and a source of excitement about what
comes next.

The developer does not author every viable strategy. The developer supplies general rules,
pressures, verbs, constraints, and legible feedback. The player combines them into plans
and identities the design did not enumerate. Authored choices are seeds and constraints;
they are not the whole strategy space.

The framework can enforce systemic composition. It cannot supply synergy or emergence;
those are outcomes of the game's design and are confirmed in play.

Every talent, upgrade, merge branch, prestige option, and unlock must pass the
[expressive-agency test](design/expressive-agency.md):

1. What kind of player does this let me become?
2. What new action, strategy, target, or constraint does it open?
3. What does it make harder, slower, riskier, or impossible?
4. In what situation is a competing option the better expression of a different playstyle?
5. Which two existing mechanics does it combine with, and what new behavior appears only
   when it does?
6. Can those interactions produce a strategy the developer did not enumerate, without a
   special-case branch?
7. Which playstyles does it support?
8. Can the player understand and test it through play?

A choice may be strong, but not strictly better. It may be simple, but not only a number.
"+20% damage" is a number. "Fight at low progress and turn risk into tempo" is a choice.
The same standard applies to the game: if every viable plan is prewritten, it is a menu, not
a system.

## Three layers

Keep the layers separate. Most bad framework decisions come from collapsing them.

**1. Framework primitives.** General, genre-neutral, and small:

| Primitive | What it does |
| --- | --- |
| State | Entities, populations, properties, tags, links, and scopes |
| Commands | Typed input, UI actions, and mutations with validation |
| Events | Typed notifications for presentation, telemetry, and decoupling |
| Queries | Deterministic entity and population selection |
| Effects | Set, add, multiply, min, max, spawn, despawn, transfer, tag, modify |
| Modifiers | Typed, stacked, timed, scoped, rank-aware |
| Time | Tick, timer, cooldown, and deterministic fast-forward |
| Transactions | Per-tick or per-range commit and rollback |
| Content | Named schemas, validation, diagnostics, and reload |
| Presentation | Data-bound UI, cues, and procedural recipes |
| Save and replay | Generic state capture, migration, and hash comparison |

**2. Game behavior.** C# systems and commands the game registers: strike, build, carry,
deposit, meditate, launch, freeze, select, buy, combine, prestige. These are game-side code.
They can be reused across mechanics, but a talent never gets its own framework class.

**3. Content data.** JSON for entities, tags, properties, tuning, UI, visuals, presentation,
and saves. Reusable patterns are game-side C# modules plus data, not framework APIs:

| Pattern | Composition |
| --- | --- |
| Talent | A data record plus a behavior function, gated by a selected tag |
| Merge | A command over two same-tier entities that despawns both and spawns the next |
| Prestige | A command that clears one scope, preserves another, and grants a currency |
| Producer | An interval system that adds a quantity from a rate and its modifiers |
| Upgrade | A costed command that increments a rank; modifiers read the rank |
| Worker | A population with tags and columns, advanced by a system |
| Offline | A host fast-forward over elapsed ticks; the same systems run |
| Status effect | A timed modifier attached to an entity and removed on expiry |
| Unlock | A system or command adds a tag or property; other code checks it |

The pattern library is allowed to be opinionated. The framework is not.

## The 41 talents as composition

The wiki lists 41 talents: 5 default and 6 groups of 6, one free per group. The framework
knows none of them. Each is a data record plus a behavior function.

The count is an expressiveness stress test, not a quota of formulas. Several wiki talents
are raw stat increases. Those fail the design bar unless they are redesigned into distinct
choices. The gate is 41 meaningful choices, not 41 copied percentage bonuses.

| Group | Unlock | Talents |
| --- | --- | --- |
| Default | start | Motivation, Hiking Buddy, Runner Backstop, Extra Housing, Bathe In Fire |
| 1 | 2 points | Strength In Numbers, Popcorn, Bullet Breath, Superhot, Timeshift Delivery, Explosive Exposition |
| 2 | 5 points | Inspiration, Gatling Balloon, The Flow, Bigger Pile, The Higher The Pile, Notes |
| 3 | 8 points | Prestige Expedience, Future To The Back, Project Constellation, Supershatter, Return To Sender, Descent Of The King |
| 4 | 11 points | Zybellium Mastery, Rocket Gatler, Frostfire, Infinite Reverberation, Deals, Reclamation Mitigation |
| 5 | 14 points | New Building, Combo Power, Arrows' Last Hurrah, DON'T EAT THAT!, DRONE.TARGET = FAR OR DRONE.TARGET = CLOSE, Vulnerability Empowerment |
| 6 | 17 points | Multiplier Growth, Operation: Pocket Rocket Timeshift Retainment, Agitated Zybe, Gnorpcalibur: Unleashed, Modern Warfare, Drone Capacity |

### Game metadata, not framework types

A choice is an entity with properties and a tag. Group, cost, free status, prerequisites,
and description are data. A generic list UI binds to entities with `ui.choice`. A command
checks the tag and the cost.

```json
{
  "id": "talent.motivation",
  "tags": ["ui.choice", "talent.default.motivation"],
  "properties": { "group": 0, "cost": 0, "free": 1, "selected": 0, "requires": 0 },
  "visualRecipeId": "icon.motivation"
}
```

No `Talent` type. No `TalentStone` panel. The UI lists choices because they have the tag,
and the game's behavior table decides what a selected choice does.

### The behavior behind the talents

A redesigned Motivation, a state choice that changes timing and risk. The wiki version is
a damage multiplier; the design audit replaces the number with a decision:

```csharp
static void Motivation(in TalentContext context)
{
    if (context.Runner.Progress >= 0.5)
    {
        return;
    }

    // Low progress becomes a different way to play: faster abilities, no healing.
    context.Runner.AddTag("cannot-heal");
    context.Runner.AddModifier("abilityCooldown", ModifierOperation.Multiply, 0.5, ticks: 1);
}
```

Extra Housing, an event with a cadence condition and a free build:

```csharp
static void ExtraHousing(in BuildContext context)
{
    if (context.Runner.BuildCount % 7 != 0)
    {
        return;
    }

    context.Build("housing", cost: 0);
    context.Runner.BuildCount++;
}
```

Hiking Buddy, a spawn with a stat penalty and a visual variant:

```csharp
static void HikingBuddy(in DepartContext context)
{
    var friend = context.Spawn("mountaineer.friend", at: context.Runner);
    friend.AddModifier("dig", ModifierOperation.Multiply, 0.5);
    friend.SetVisualVariant("small-hat-frown");
}
```

Project Constellation, a timed batch with three overrides:

```csharp
static void ProjectConstellation(in TalentTick context)
{
    foreach (var rocket in context.RocketsAirborneInIdOrder())
    {
        context.Strike(rocket);
        rocket.Set("seeking", 0);
        rocket.Set("range", 3); // then 7 on the next pulse, or a timed override
        rocket.Set("triggersWeakspot", 1);
    }
}
```

Merging, with no merge feature:

```csharp
public void Combine(in CombineCommand command)
{
    if (!command.First.HasTag("fruit.tier2") || !command.Second.HasTag("fruit.tier2"))
    {
        return;
    }

    world.Remove(command.First);
    world.Remove(command.Second);
    world.Spawn("fruit.tier3", at: command.First.Position);
}
```

Prestige, with no prestige feature:

```csharp
public void Prestige(in PrestigeCommand command)
{
    world.TransferShare(command.Run, "shards", command.Meta, "shards", basisPoints: 1000);
    world.ClearScope("scope.run");
}
```

These use the same primitives as a status effect, a spawner, or a door. That is the point.

### The acceptance gate

All 41 talents are data records plus behavior functions. Each has a host scenario with a
positive and a negative case, a design note against the expressive-agency test, a
competing-choice comparison, and a synergy scenario where another mechanic changes what the
choice does. The pack loads with the group, point, cost, and free-talent logic expressed as
commands and systems, not framework fields. Tooltips come from choice properties.

If a talent cannot be expressed, the gate fails. "Add a Talent subsystem" is not a fix.
Add a game-side command, a deterministic query, or a general primitive, and prove it with a
second mechanic. If a talent is only a numeric buff, redesign it into a distinct choice or
replace it. "Add a bigger number" is not a fix either. A combination that needs its own
named branch is a special case, not a system: generalise the rule or cut it.

## The second game

An incremental slice built from references can accidentally become a clone. The check is a
second game with different mechanics, built after the first, using only the same primitives.

The second game is a merge-focused Idle Colony-like prototype: fruit tiers, combine
commands, producers, workers, prestige, no talents. It uses the same entities, tags, scopes,
modifiers, UI binding, and asset recipes.

The framework may change for a genuine bug. It may not change because the second game needs
a new concept. If it does, the abstraction failed, and the primitive is too specific.

## Procedural assets

The agent should not need me to draw sprites, icons, tiles, or effects. At least 90 percent
of the art in a game is authored as procedural recipes, rendered to a contact sheet,
reviewed once, and packed into an atlas. Fonts and audio stay imported.

What exists: `VisualRecipe` with rect, ellipse, and line layers, a palette, per-layer
jitter, state variants, nested parts, and an imported underlay, overlay, or replace.
`ProceduralRasterizer` is deterministic and engine-neutral, and `PixelBuffer` is the source
format. That is enough for simple glyphs. It is not enough for a gnorp with a silhouette,
shading, an outline, four work frames, a hat, and a damage state.

| Category | What the agent generates | Frames per game |
| --- | --- | --- |
| Actors | Four gnorp types, four states each | 16 |
| Structures | Six buildings, two damage states | 12 |
| UI | Twenty icons, panels, bars, buttons, cursors | 30 |
| World | Eight rock tiles, three background layers | 11 |
| Effects | Six particles, four projectiles, hit and purchase flashes | 14 |
| Portraits | Optional emotes and talent art | 12 |

### Target recipe shape

```json
{
  "id": "gnorp.slammer",
  "canvas": [24, 24],
  "palette": { "outline": "#1b1420", "body": "#e0a14c", "light": "#f6d79a", "dark": "#8c5a2b" },
  "frames": { "idle": 2, "work": 4, "hit": 1 },
  "parts": [
    { "shape": "body", "at": [12, 16], "radius": 6, "fill": "body", "outline": 1 },
    { "shape": "ellipse", "at": [12, 9], "size": [7, 6], "fill": "light" },
    { "shape": "attachment", "slot": "hat", "at": [12, 5] }
  ],
  "shading": { "light": [-1, -1], "highlight": 0.3, "shadow": 0.25 },
  "pivot": [12, 22],
  "atlas": "actors"
}
```

This is the target shape, not the current schema. The current schema has layers, not body
parts or frames.

### DSL additions

| Area | Additions |
| --- | --- |
| Primitives | Rounded rect, polygon, circle, capsule, arc, thick line |
| Styling | Outline, inner shadow, highlight, two-stop gradient, dither, noise, palette swap |
| Composition | Anchors, mirrored parts, grids, attachments, body-part slots |
| Animation | 2 to 8 frames per state, per-frame offsets, loop metadata, pivot |
| Variants | Seed variants, palette variants, damage stages |
| Metadata | Category, pivot, bounds, z-order, atlas group, tags |

No arbitrary paths, no bitmap tracing, no imported PNGs except the font. A primitive is
added only when three assets need it.

### Quality bar

High quality here means crisp, readable pixel art, not illustration.

- Readable at 1x and 2x on the target 1280x800 screen.
- Distinct silhouettes within a category: at least 15 percent opaque-pixel difference.
- Consistent palette and one light direction across a category.
- Outline or 3:1 contrast against both light and dark backgrounds.
- 2 to 4 frames for a 24px animation; no empty frames; no clipping; pivot at the feet.
- Original shapes only, built from primitives.
- No semi-transparent fringes on pixel art.

Automated checks cover palette size, alpha, bounds, pivot, frame count, empty frames,
silhouette difference, and background contrast. The contact sheet covers taste: labeled
recipes at 4x and 8x, on light and dark backgrounds, plus an in-game screenshot at 1x. I
review one sheet per batch.

### Workflow

1. The agent writes a style guide and recipes: palette, pixel scale, outline, light
   direction, and per-category silhouettes.
2. `systemic assets sheet --content <pack> --out artifacts/assets.png --scale 8
   --background dark,light` renders a labeled contact sheet and a manifest.
3. `systemic assets validate` runs the mechanical checks and fails on clipping, empty
   frames, palette drift, or low contrast.
4. The agent inspects the sheet, revises the recipes, and repeats. Three passes maximum per
   batch.
5. `systemic assets atlas --content <pack> --out content/atlas.png --manifest
   content/atlas.json` packs the frames.
6. The game references recipe IDs, never PNG paths. Runtime loads the atlas; no
   rasterization per frame.

Exit gate G1b: 90 percent of the art is procedural, the contact sheet is accepted, and no
hand-drawn PNG remains except the font. A new asset takes under 10 minutes.

## Current position, with evidence

| Area | State | Why it blocks the goal |
| --- | --- | --- |
| Rule model | Legacy JSON rules are frozen: 5 conditions and 11 effects, no loops, iteration, formula effects, or per-entity schedules | The port validates but cannot express the acceptance test cleanly. The engine is scheduled for deletion |
| Rule tests | Manual fact dispatch passes while the host path does nothing | Not acceptance evidence. Port the cases to host-level system tests |
| Incremental module | Resources, producers, upgrades, roles, and choice groups | A genre schema. Freeze it and delete it after the samples move |
| `incremental-quarry` | One resource, one producer, one upgrade, two choice groups | A content preview, not a game |
| `glow-garden` | Playable colony with 1,000 workers, 64 plants, 12,000 motes, talents, save/load | Progression, talents, UI, and save are hand-rolled and some behavior lives in rules |
| `pixel-mine` | Playable mining game with upgrades, HUD, menu, particles | Hand-rolls economy, upgrades, and HUD |
| Presentation | Declarative UI, action bindings, timelines, procedural recipes, cue stream | Preview is read-only. A real game still builds its HUD by hand |
| Procedural art | `VisualRecipe` supports rect, ellipse, line, palette, jitter, states, parts, and imported layers | No outlines, shading, gradients, symmetry, animation frames, atlas, contact sheet, or quality checks |
| CLI | `new`, `validate`, `trace`, `watch` | No `run`, `simulate`, `verify`, `dump`, or replay. Errors are prose |
| Scale | 1M row and 10M movement benchmarks | Real for a different genre, irrelevant here |

The strongest assets are the content loader, the deterministic kernel, the Presentation UI
and cues, and the two playable samples. The weakest point is behavior authoring: JSON rules
are too small to compose a game, so every real sample writes C# anyway.

## Hard decisions

**D1. Personal use, no distribution.** No license, NuGet, feed, or external compatibility
work. `git clone` plus `./bootstrap.sh` is the install story.

**D2. No genre nouns in the framework.** No type, method, or module named Talent, Merge,
Prestige, Upgrade, Producer, or Colony. A code review rejects them.

**D3. General primitives only.** A new framework primitive must be genre-neutral and serve
at least two different mechanics. If it serves only Gnorp, it is a game command or a
game-side pattern.

**D4. Patterns are game-side code and data.** Producers, upgrades, talents, merges,
prestige, workers, and offline summaries ship as game-side C# modules plus JSON data. They
can be copied and changed. They are not framework APIs.

**D5. Behavior is C#.** A game registers systems and commands. A talent never becomes a
framework class.

**D6. The 41 talents and the second game are the acceptance tests.** One proves
expressiveness, the other proves the absence of genre lock.

**D7. One execution model.** `ISystemicSystem` runs behavior. There is no rule engine, no
scripting layer, and no second runtime.

**D8. Offline is fast-forward.** The host runs elapsed ticks as fast as possible,
deterministically. What production means is systems. No offline API.

**D9. Balance is a command.** The game's headless mode runs a pack, applies a policy, and
writes a CSV. No balance UI.

**D10. Procedural-first art.** At least 90 percent of art is a recipe. Fonts and audio are
the only imports.

**D11. Determinism and save stay.** Integer and fixed-point simulation. Presentation may use
floats. Save, fast-forward, and replay are tested. Simulation systems are stateless; state
lives in the World.

**D12. Freeze `Systemic.Incremental`.** Do not add features to it. Delete it when the
samples no longer need it.

**D13. Code-first is the primary authoring surface.** Behavior is C# systems and commands.
JSON is data.

**D14. The World is the only simulation state.** A system field is a bug unless it is a
derived cache rebuilt on load.

**D15. Save and replay carry a composition version.** A behavior change invalidates an old
save or replay unless it migrates. Targeted in 1.20; until then only the content fingerprint
is checked.

**D16. Delete the rule engine after migration.** The JSON rule path is legacy. Do not extend
it, and do not build a typed version of it.

**D17. Design for expressive agency through emergence.** Every choice must express identity,
open a distinct future, carry a real trade-off, and interact with at least two mechanics.
The interactions must combine through general rules into strategies the developer did not
enumerate. A strictly better stat upgrade or a per-combination script is a failed design,
even if it is balanced.

## Phase 0: the loop and a playable template (1 week)

Goal: my agent can scaffold, run, simulate, verify, and render assets from a clean clone.

| ID | Work | Acceptance evidence |
| --- | --- | --- |
| 0.1 | Bootstrap script: restore, build, test, run the template | `git clone && ./bootstrap.sh` works with only the SDK installed |
| 0.2 | `systemic new --template systemic` creates a playable game project: a data pack, a C# game module, one interval system, one tag toggle, one generic list, one asset, save, and a headless test | Window opens; the game host's `--headless --ticks 600` changes state |
| 0.3 | Game-host `run`, `simulate`, `verify`, `dump`, `save`, `load`, and `replay` commands | Each works headless on the template |
| 0.4 | Game-host `simulate --seconds 3600 --policy greedy --csv` runs a balance pass | One game hour in under 10 seconds |
| 0.5 | Add `--json` and one error envelope: `code`, `message`, `file`, `path`, `line`, `column`, `docs`, `hint` | Snapshot tests for success and each diagnostic class |
| 0.6 | Document exit codes: 0 success, 1 content error, 2 usage, 3 runtime, 4 verification mismatch | README and `--help` agree |
| 0.7 | Rewrite `AGENTS.md`: build, test, run, simulate, content map, determinism rules, common failures | Every command is copy-paste runnable |
| 0.8 | Add `docs/agents/` with one task per file: state, command, event, selector, effect, tag toggle, save, asset | Each under 80 lines and linked to a runnable example |
| 0.9 | README claims match the new target | No claim without a gate artifact |
| 0.10 | Asset loop: template ships a procedural icon, actor, and panel; `assets sheet`, `validate`, and `atlas` work | Contact sheet and atlas from content; no PNG except the font |

Exit gate G0: from a clean clone, my agent runs `./bootstrap.sh`, scaffolds the template,
plays it, simulates one hour, and verifies a save, using `AGENTS.md` alone. Under 15 minutes.

Stop rule: if G0 needs a human step, do not start Phase 1.

## Phase 1: code-first simulation (6 weeks)

The legacy rule work (old 1.1-1.12) is frozen. The asset work (1.13-1.16) continues in
parallel. The migration is:

| ID | Work | Acceptance evidence |
| --- | --- | --- |
| 1.17 | Game module API: `ISystemicGameModule`, `AddModule`, typed commands, and module version tracking | A template game composes systems and commands; duplicate module IDs fail |
| 1.18 | Typed commands and events: records, handlers, deterministic dispatch, UI action binding | A UI button invokes a typed command; a missing handler is a startup error |
| 1.19 | Data-only content path: `rules.json` optional, no rule compilation for new packs, data validation for entities and tuning | A pack without `rules.json` loads and validates |
| 1.20 | Save and replay composition version: module ID, version, content fingerprint, and mismatch diagnostics | A changed module version rejects an old save with a clear message; replay hashes still match |
| 1.21 | Five-talent host spike: Motivation, Strength In Numbers, Project Constellation, Hiking Buddy, Deals, plus one pattern | Host scenarios with real systems, positive and negative cases, save/load, replay, and p95 measurement |
| 1.22 | Port and design-audit the 41-reference corpus as data plus behavior functions | 41/41 host scenarios and expressive-agency notes; each has a competing-choice comparison, two named synergies, and one non-obvious interaction exercised through general rules; numeric-only or clone-only talents redesigned or replaced; no per-talent class; no flag without a consumer; no test-only property |
| 1.23 | Neutral pattern library: interval emitter, costed mutation, tier combiner, scope reset, tag gate, row worker | Game A and Game B reuse the same patterns without framework changes |
| 1.24 | Game A playable with the code-first module | Playable; 41 meaningful choices; at least three recorded combination strategies that no content branch names, exercised through general rules; save, fast-forward, prestige |
| 1.25 | Game B playable with merging and no talents | Playable; no talent tag or behavior; framework diff empty |
| 1.26 | Delete the rule engine, expression language, rule schema, rule tests, and rule docs | Build and tests pass without the legacy runtime; no new JSON rule primitives |

The asset work remains:

| ID | Work | Acceptance evidence |
| --- | --- | --- |
| 1.13 | Asset DSL: primitives, outline, shading, gradient, dither, symmetry, parts, frames, atlas metadata | The target `gnorp.slammer` recipe compiles; every new primitive has a raster test |
| 1.14 | Asset commands: `sheet`, `validate`, `atlas`, labeled manifest | Sheet, atlas, and manifest are produced from content; a bad recipe names its file and path |
| 1.15 | Quality checks: palette, alpha, bounds, pivot, frames, silhouette, contrast | A bad recipe fails once for each check with a useful diagnostic; a good batch passes |
| 1.16 | Port the slice's art to recipes; delete hand-made atlases and the Aseprite dependency | Only the font remains imported; the contact sheet is accepted; the game loads the generated atlas |

Exit gate G1: 41/41 talents are data plus behavior functions with host scenarios, each passes
the expressive-agency test and has a competing-choice comparison and recorded synergy. At
least three play-tested combination strategies that no content branch names are recorded
from general rules. The neutral pattern library is used by both games, the framework has no
genre type, save and replay carry the module version, and the asset sheet and atlas are
accepted. The legacy rule engine is deleted.

Stop rules:
- If a pattern needs a framework feature, express it as a general primitive and prove it
  with a second mechanic.
- If a talent needs a new game command, add one command used by at least two mechanics.
  Do not add a talent class.
- If a combination needs its own branch, generalise the rules or cut the combination. Do not
  add a special case.
- If every viable strategy is already enumerated in content, the game is a menu. Add
  interaction surface, not more options.
- Do not add a JSON rule primitive to make a mechanic fit.
- If a game system needs state outside the World, move the state into the World or a saved
  extension.

## Phase 2: two games, one framework (3 weeks)

Goal: an original incremental game that uses the Gnorp corpus as a reference, plus an
original merge-focused game with no talents, both on the same primitives.

| ID | Work | Acceptance evidence |
| --- | --- | --- |
| 2.1 | Game A: an original incremental game with its own fiction and choices, using the Gnorp corpus only as a mechanical reference; four unit types, six structures, twelve upgrades as game-side patterns, 41 meaningful choices, prestige, fast-forward | Playable; choices pass the design bar; at least three play-tested combination strategies no content branch names; not a clone |
| 2.2 | Game B: an original merge-focused game with its own fiction, combine commands, producers, workers, prestige, no talents | Playable; no talent tag or behavior |
| 2.3 | Build B without changing the framework | Diff shows game code and data only |
| 2.4 | Generic HUD: resources, rates, choice list, upgrade list, cooldowns, tooltips, fast-forward summary | Both games use the same widgets |
| 2.5 | Feedback: floating numbers, flashes, particles, screen shake | Every purchase and hit has feedback |
| 2.6 | Balance pass with the game's headless mode for both games | CSV and a tuning note per game |
| 2.7 | Save, reload, fast-forward, and replay at 1x and 4x | Same hash after reload; fast-forward within one percent |
| 2.8 | All art procedural except the font; contact sheet accepted | Atlas loads; no PNG |

Exit gate G2: both games are playable for two hours. Game A has 41 meaningful talents, not
just 41 selectable numbers, and at least three play-tested combination strategies that no
content branch names. Game B has none. The framework diff between them is empty. 10,000
actors and 50,000 particles hold 60 Hz. Save, fast-forward, and prestige work in both.

Stop rules:
- If Game B needs a framework change, the primitive is too specific. Fix it before
  continuing.
- If Game A needs a per-talent class, the composition layer failed.
- Three weeks is the cap.

## Phase 3: agent evaluation (1 week, overlaps Phase 2)

Goal: measure the agent on mechanics, not on a tutorial.

| ID | Work | Acceptance evidence |
| --- | --- | --- |
| 3.1 | Task corpus of 14 tasks across both games | Each has a prompt, a starting state, a verification command, and a token budget |
| 3.2 | Harness: fresh checkout, my daily model, three attempts, pass@3, transcripts | One command runs the suite and writes a report |
| 3.3 | Error taxonomy: data, system, command, UI binding, save, fast-forward, determinism, build | Every failure classified; recurring classes become diagnostics |
| 3.4 | Baseline the current tree | Near-zero pass rate kept as the number to beat |
| 3.5 | Run before each gate; block on regression | Report checked in under `artifacts/agent-eval/` |

The 14 tasks:

1. Add a quantity and an interval system that increases it.
2. Add a cost and a rank, with a modifier that reads the rank.
3. Express Motivation as data plus a behavior function.
4. Express Project Constellation as a timed system.
5. Add a tag toggle list with group and point commands.
6. Build a three-tier merge chain from a combine command.
7. Express prestige as a scope reset command.
8. Fast-forward eight hours and show a summary.
9. Format a value at 1e15 and bind it to the HUD.
10. Add a worker behavior with carry and return.
11. Add a save migration after a data schema change.
12. Rebalance production in Game B.
13. Generate a new creature recipe with idle and work frames.
14. Generate eight UI icons in one style and pass the sheet.

Exit gate G3: pass@3 at or above 80 percent on 14 tasks, median time to a verified change
under 10 minutes, median token use under 60k per task, no task below 50 percent.

Stop rule: if G3 is under 50 percent, do not add features. Redesign the content surfaces
from the transcripts.

## Phase 4: dogfood and polish (1 week)

Goal: both games are worth opening again.

| ID | Work | Acceptance evidence |
| --- | --- | --- |
| 4.1 | Three play sessions of at least 30 minutes across both games | A punch list |
| 4.2 | Fix the top five friction points | Each fix has a test or a content diff |
| 4.3 | Tune the first hour of each game with the headless balance mode | Time-to-tier within the target band |
| 4.4 | Add audio only if the silence hurts | One cue per purchase, hit, unlock, and reset |
| 4.5 | Save migration and replay across a reset | Load and hash tests |
| 4.6 | Tag `v0.1` locally | Bootstrap from that tag works |

Exit gate G4: both games are playable from a clean clone at `v0.1`, and every earlier gate
still passes.

## Creation contract

1. Clean clone to playable template: under 15 minutes.
2. New game idea to playable loop: under one day.
3. A game with different mechanics, no framework change: under two days.
4. New talent: one data record plus a behavior function that passes the expressive-agency
   test, at most 12 behavior lines, under five minutes.
5. New merge chain: at most 10 lines per tier in a combine command.
6. New producer, upgrade, or status: a system or command plus data, under 10 minutes.
7. New mechanic: a game-side system or command; a new framework primitive only when two
   mechanics need it.
8. New asset: under 10 minutes, at most three sheet passes.
9. Edit to visible change: under 2 seconds.
10. Simulate one game hour: under 10 seconds.
11. Verify everything: one game-host command, `verify`.
12. Every failure has a code, a location, and a next action.

## Performance contract

| Tier | Workload | Budget | Gate |
| --- | --- | --- | --- |
| Game | 10,000 actors, 50,000 particles, 20 active mechanics, 60 Hz | p95 ≤ 8 ms, zero simulation allocation | both games |
| Colony | 1,000 actors, 12,000 motes, 60 Hz | p95 ≤ 4 ms, zero simulation allocation | Game B |
| Content | 2,000 entities through systems | p95 ≤ 5 ms, ≤ 4 KiB per tick | existing gate |
| Balance | 1 hour of game time, 1,000 actors, headless | ≤ 10 seconds | headless mode |
| Assets | 100 recipes, 4 states, 24px | sheet ≤ 30 s, atlas ≤ 2 s | `assets` |
| Headroom | 100,000 actors, movement and simple behavior | p95 ≤ 16 ms | optional, not a release gate |

The 1M and 10M tiers stay as diagnostics. They are not release gates and not on the front
page.

## What this plan deletes or freezes

- Delete the 1M and 10M agent gates from the critical path and the README headline.
- Delete the 60 Hz million-agent claim.
- Delete the behavior layer built for 1M agents.
- Freeze `Systemic.Incremental`. No new resources, producers, upgrades, roles, or
  advancements. Delete it when the samples no longer need it.
- No framework type, method, or module named Talent, Merge, Prestige, Upgrade, Producer, or
  Colony.
- Delete per-talent classes. A talent that needs a class is a failed gate.
- Delete the JSON rule engine, expression language, rule schema, rule tests, and rule docs
  after migration.
- Delete the typed rule builder. Do not add a second execution model.
- Delete the assumption that Game A must clone Gnorp. Use the corpus as a reference only.
- Reject or redesign talents that are only numeric upgrades. A choice must open a distinct
  way to play.
- Delete hand-made PNG atlases and the Aseprite dependency. Fonts and audio stay imported.
- No editor, new renderer, networking, or multiplayer.

## Resourcing and sequence

One developer, 11 to 13 weeks, with a three-week buffer.

| Week | Phase | Milestone |
| --- | --- | --- |
| 1 | 0 | Bootstrap, playable template, run and simulate commands, `AGENTS.md` |
| 2-7 | 1 | Code-first module, commands, data-only loader, 41 talents, pattern library, assets, UI, save |
| 7-9 | 2 | Game A, Game B, no framework diff, HUD, feedback, balance |
| 9-10 | 3 | Task corpus, harness, baseline, pass@3 gate |
| 10-11 | 4 | Play sessions, fixes, audio if needed, `v0.1` |
| 11-13 | buffer | Fix what the gates expose; do not add scope |

Critical path: code-first module, five-talent host, 41 talents, neutral patterns, Game B
without a framework change, and the asset pipeline. Everything else can slip.

## Risks and kill criteria

| Risk | Likelihood | Response | Kill criterion |
| --- | --- | --- | --- |
| Genre nouns leak into the framework | High | No Talent, Merge, Prestige, Upgrade, Producer, or Colony types; review every primitive against two mechanics | A genre type appears: reject the change |
| General primitives are too low-level for the agent | High | Pattern library, recipes, examples, and eval tasks | Agent pass@3 under 50 percent: simplify the primitives, not add genre APIs |
| The 41 talents force Gnorp-specific hooks | High | Express them as data, systems, and commands; prove with Game B | A hook serves only one talent: make it a game command or cut it |
| Game B needs a framework change | Medium | Fix the primitive so both games use it | A game-code-only diff is impossible: stop and redesign |
| Game systems drift into nondeterminism | High | Strict analyzer, World-only state, fixed-point helpers, host tests | A save or replay mismatch without a content or version change: stop and fix |
| Talents are numerically better, not expressive | High | Expressive-agency note and competing-choice comparison for every choice; playtest | A choice is a strict stat upgrade with no trade-off or distinct future: redesign or cut |
| The game copies Gnorp instead of designing its own choices | High | Use Gnorp only as a reference corpus; write an original fiction and design note per choice | A mechanic exists only because Gnorp has it: redesign it for this game or cut it |
| Game behavior becomes verbose | Medium | Behavior table, neutral pattern library, five-talent spike | A talent exceeds 12 behavior lines or the port doubles the JSON pack size: simplify |
| Procedural art looks samey or unreadable | High | Style guide, palette, silhouette and contrast checks, contact sheet | Two sheets rejected: import a curated pack for that category |
| Asset DSL becomes a paint program | High | Named primitives; add one only when three assets need it | More than eight new primitives in a week: stop |
| Solo time runs out | Medium | Phase gates, two games, no external obligations | Week 13 without G3: finish Game A, stop framework work |

## Definition of done

1. Clean clone, bootstrap, play the template.
2. My agent takes a one-paragraph idea to a playable game in under one day.
3. The 41 Gnorp talents are a reference corpus, not a clone. Each is ported as data plus a
   behavior function, at most 12 behavior lines, with a host-level headless test, an
   expressive-agency note, and a competing-choice comparison. Every choice opens a distinct
   way to play; none is only a numeric upgrade.
4. Merging, prestige, producers, upgrades, and workers are game-side patterns, not framework
   features.
5. A second game with merging and no talents runs on the same primitives with no framework
   diff.
6. 90 percent of art is procedural, reviewed as a contact sheet and packed into an atlas;
   only the font and audio are imported.
7. 10,000 actors and 50,000 particles hold 60 Hz within 8 ms p95 with 20 active mechanics.
8. One game I play for more than two hours, built with the kit, tagged `v0.1`.
9. 80 percent pass@3 on 14 tasks across both games.
10. No framework type named Talent, Merge, Prestige, Upgrade, Producer, or Colony.
11. The rule engine, expression language, rule schema, and rule tests are deleted.
12. README claims match the gates, with artifacts.

If item 5 is false, the framework is a Gnorp engine. If item 10 is false, the framework is a
genre kit. If item 11 is false, the framework still carries a second language. Any one fails
the goal.
