# Systemic game plan (personal use)

Status: Phase 0 complete; Phase 1 in progress  
Date: 2026-10-30  
Execution checklist: [phase-1-checklist.md](phase-1-checklist.md)
Scope: one developer, one repository, no external users.  
Target: Gnorp-like and Idle Colony-like games built from general mechanics, plus a second game with different mechanics to prove the framework did not bake the genre in.

## The correction

The previous version put talents, merging, prestige, producers, and upgrades into the kit. That was wrong. It would have made a framework that can build exactly one kind of game, which defeats the point of a systemic framework.

The rule is now simple: **the framework may not contain a type, method, or module named Talent, Merge, Prestige, Upgrade, Producer, or Colony.** Those are game concepts. They are expressed as content built from general primitives. A talent is a tag plus rules. A merge is a rule over two entities. Prestige is a rule over a reset fact. A producer is a scheduled rule. An upgrade is a counter plus rules. None of them need a framework feature.

The 41 Gnorp talents stay as an acceptance test, but they test the composition layer, not a Talent subsystem. The second game, a merge-focused game with no talents, tests that the layer is not secretly a Gnorp engine.

## The goal, stated as a test

I write one paragraph for a game. My coding agent, working in this repository, produces a playable build in one day. A reference-quality slice takes three weeks. It expresses all 41 Gnorp talents as general rules and tags, with no framework type called Talent. A second game with different mechanics, including merging but no talents, uses the same primitives and requires no framework change.

Adding a talent is a 12-line rule edit and takes five minutes. Adding a creature or icon is a recipe edit and takes ten minutes. Adding a mechanic the framework has never seen is content work, not a new module.

## Three layers

Keep the layers separate. Most bad framework decisions come from collapsing them.

**1. Framework primitives.** General, genre-neutral, and small:

| Primitive | What it does |
| --- | --- |
| State | Entities, populations, properties, tags, links, and scopes |
| Facts | Named events with sources, targets, payloads, and deterministic dispatch |
| Queries | Selectors by tag, property, range, nearest, aggregate, count, and formula |
| Effects | Set, add, multiply, min, max, spawn, despawn, transfer, emit, schedule, tag, modify |
| Modifiers | Typed, stacked, timed, scoped, rank-aware |
| Rules | On a fact or while a condition holds, with guards, priority, and effects |
| Rule bundles | Named groups of rules that can be enabled, disabled, or parameterized |
| Time | Tick, schedule, delay, cooldown, and deterministic fast-forward |
| Transactions | Per-tick or per-range commit and rollback |
| Content | Named schemas, validation, diagnostics, and reload |
| Presentation | Data-bound UI, cues, and procedural recipes |
| Save and replay | Generic state capture, migration, and hash comparison |

**2. Game actions.** Domain verbs the game registers: strike, build, carry, deposit, meditate, launch, freeze. These are C# because they touch the game's simulation. They are reusable across many rules. A rule can invoke an action, but a talent never gets its own class.

**3. Content patterns.** Reusable compositions of the two layers, shipped as content, not APIs:

| Pattern | Composition |
| --- | --- |
| Talent | An entity with a tag, rules gated on that tag, and a generic toggle in a list |
| Merge | A rule on a `combine` fact that despawns two same-tier entities and spawns the next |
| Prestige | A rule on a `reset` fact that clears one scope, preserves another, and grants a currency |
| Producer | A scheduled rule that adds a quantity from a rate and its multipliers |
| Upgrade | A purchase rule that consumes a cost, increments a rank, and modifiers read that rank |
| Worker | A population with tags and columns, moved by a system or rules |
| Offline | A host fast-forward over elapsed ticks; the same rules run; the summary is UI |
| Status effect | A timed modifier attached to an entity and removed on expiry |
| Unlock | A rule that adds a tag or property; other rules check it |

The pattern pack is allowed to be opinionated. The framework is not.

## The 41 talents as composition

The wiki lists 41 talents: 5 default and 6 groups of 6, one free per group. The framework knows none of them. Each is an entity with a tag, plus rules that apply while the tag is present.

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

A choice is an entity with properties and a tag. Group, cost, free status, and prerequisites are properties. A generic list UI binds to entities with `ui.choice`. A rule checks the tag.

```json
{
  "id": "talent.motivation",
  "tags": ["ui.choice", "talent.motivation"],
  "properties": { "group": 0, "cost": 0, "free": 1, "spent": 0, "requires": 0 },
  "visualRecipeId": "icon.motivation"
}
```

No `Talent` type. No `TalentStone` panel. The UI lists choices because they have the tag, and the rules check the tag.

### The rules behind the talents

Motivation, a state condition with three effects:

```json
{
  "id": "gnorp.motivation",
  "while": [
    { "hasTag": "talent.motivation", "subject": "self" },
    { "runnerPast": "halfway" }
  ],
  "effects": [
    { "target": "tag.slammers", "stat": "damage", "op": "mul", "value": 2 },
    { "target": "tag.slammers", "stat": "comboChance", "op": "add", "value": 0.05 },
    { "target": "tag.slammers", "do": "meditate" }
  ]
}
```

Extra Housing, an event with a cadence condition and a free build:

```json
{
  "id": "gnorp.extra-housing",
  "on": "entity.built",
  "when": [
    { "hasTag": "housing", "subject": "target" },
    { "modulo": ["count.tag.housing", 7], "equals": 0 }
  ],
  "effects": [{ "do": "build", "archetype": "housing", "cost": 0 }]
}
```

Hiking Buddy, a spawn with a stat penalty and a visual variant:

```json
{
  "id": "gnorp.hiking-buddy",
  "on": "mountaineer.depart",
  "effects": [
    { "do": "spawn", "archetype": "mountaineer.friend", "count": 1, "at": "source" },
    { "target": "tag.mountaineer.friend", "stat": "dig", "op": "mul", "value": 0.5 },
    { "target": "tag.mountaineer.friend", "do": "visual", "variant": "small-hat-frown" }
  ]
}
```

Project Constellation, a timed batch with three overrides:

```json
{
  "id": "gnorp.project-constellation",
  "on": "tick.every.20s",
  "effects": [
    { "target": "tag.rocket.airborne", "do": "strike" },
    { "target": "tag.rocket", "stat": "seeking", "op": "set", "value": false },
    { "target": "tag.rocket", "stat": "range", "op": "set", "value": [3, 7] },
    { "target": "tag.rocket", "stat": "triggersWeakspot", "op": "set", "value": true }
  ]
}
```

Gnorpcalibur: Unleashed, a flag set while a tag is present:

```json
{
  "id": "gnorp.gnorpcalibur",
  "while": [{ "hasTag": "talent.gnorpcalibur", "subject": "self" }],
  "effects": [{ "do": "set", "what": "accords.concurrent", "value": true }]
}
```

Merging, with no merge feature:

```json
{
  "id": "game.merge.tier2",
  "on": "combine",
  "when": [
    { "hasTag": "fruit.tier2", "subject": "source" },
    { "hasTag": "fruit.tier2", "subject": "target" }
  ],
  "effects": [
    { "do": "despawn", "subject": "source" },
    { "do": "despawn", "subject": "target" },
    { "do": "spawn", "archetype": "fruit.tier3", "count": 1 }
  ]
}
```

Prestige, with no prestige feature:

```json
{
  "id": "game.prestige",
  "on": "reset",
  "effects": [
    { "do": "clearScope", "scope": "run" },
    { "do": "grant", "target": "meta.shards", "amount": { "formula": "run.shards * 0.1" } }
  ]
}
```

These rules use the same primitives as a status effect, a spawner, or a door. That is the point.

### The acceptance gate

All 41 talents are authored as general rules and tags, one file per group plus default. Each has a headless test with a positive and a negative case. The pack loads with the group, point, and free-talent logic expressed as rules, not framework fields. Tooltips are generated from choice properties.

If a talent cannot be expressed, the gate fails. "Add a Talent subsystem" is not a fix. Add a general primitive, a game action, or a selector, and prove it with a second mechanic.

## The second game

A Gnorp-like slice can accidentally become a Gnorp engine. The check is a second game with different mechanics, built after the first, using only the same primitives.

The second game is a merge-focused Idle Colony-like prototype: fruit tiers, combine actions, producers, workers, prestige, no talents. It uses the same rules, tags, scopes, modifiers, UI binding, and asset recipes.

The framework may change for a genuine bug. It may not change because the second game needs a new concept. If it does, the abstraction failed, and the primitive is too specific.

## Procedural assets

The agent should not need me to draw sprites, icons, tiles, or effects. At least 90 percent of the art in a game is authored as procedural recipes, rendered to a contact sheet, reviewed once, and packed into an atlas. Fonts and audio stay imported.

What exists: `VisualRecipe` with rect, ellipse, and line layers, a palette, per-layer jitter, state variants, nested parts, and an imported underlay, overlay, or replace. `ProceduralRasterizer` is deterministic and engine-neutral, and `PixelBuffer` is the source format. That is enough for simple glyphs. It is not enough for a gnorp with a silhouette, shading, an outline, four work frames, a hat, and a damage state.

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

This is the target shape, not the current schema. The current schema has layers, not body parts or frames.

### DSL additions

| Area | Additions |
| --- | --- |
| Primitives | Rounded rect, polygon, circle, capsule, arc, thick line |
| Styling | Outline, inner shadow, highlight, two-stop gradient, dither, noise, palette swap |
| Composition | Anchors, mirrored parts, grids, attachments, body-part slots |
| Animation | 2 to 8 frames per state, per-frame offsets, loop metadata, pivot |
| Variants | Seed variants, palette variants, damage stages |
| Metadata | Category, pivot, bounds, z-order, atlas group, tags |

No arbitrary paths, no bitmap tracing, no imported PNGs except the font. A primitive is added only when three assets need it.

### Quality bar

High quality here means crisp, readable pixel art, not illustration.

- Readable at 1x and 2x on the target 1280x800 screen.
- Distinct silhouettes within a category: at least 15 percent opaque-pixel difference.
- Consistent palette and one light direction across a category.
- Outline or 3:1 contrast against both light and dark backgrounds.
- 2 to 4 frames for a 24px animation; no empty frames; no clipping; pivot at the feet.
- Original shapes only, built from primitives.
- No semi-transparent fringes on pixel art.

Automated checks cover palette size, alpha, bounds, pivot, frame count, empty frames, silhouette difference, and background contrast. The contact sheet covers taste: labeled recipes at 4x and 8x, on light and dark backgrounds, plus an in-game screenshot at 1x. I review one sheet per batch.

### Workflow

1. The agent writes a style guide and recipes: palette, pixel scale, outline, light direction, and per-category silhouettes.
2. `systemic assets sheet --content <pack> --out artifacts/assets.png --scale 8 --background dark,light` renders a labeled contact sheet and a manifest.
3. `systemic assets validate` runs the mechanical checks and fails on clipping, empty frames, palette drift, or low contrast.
4. The agent inspects the sheet, revises the recipes, and repeats. Three passes maximum per batch.
5. `systemic assets atlas --content <pack> --out content/atlas.png --manifest content/atlas.json` packs the frames.
6. The game references recipe IDs, never PNG paths. Runtime loads the atlas; no rasterization per frame.

Exit gate G1b: 90 percent of the art is procedural, the contact sheet is accepted, and no hand-drawn PNG remains except the font. A new asset takes under 10 minutes.

## Current position, with evidence

| Area | State | Why it blocks the goal |
| --- | --- | --- |
| Rule model | 4 conditions (`HasTag`, `PropertyAtLeast`, `Expression`, `Chance`) and 4 effects (`AdjustProperty`, `SetModifier`, `AddTag`, `EmitEvent`) | No selectors, formulas, spawn or despawn, transfer, schedules, or dynamic actions. Most of the 41 talents and every merge are out of reach |
| Incremental module | Resources, producers, upgrades, roles, and choice groups | A genre schema. Extending it with prestige and talents would bake the genre into the framework. Freeze it and move its behavior into patterns |
| `incremental-quarry` | One resource, one producer, one upgrade, two choice groups | A content preview, not a game |
| `glow-garden` | Playable colony with 1,000 workers, 64 plants, 12,000 motes, talents, save/load | Progression, talents, UI, and save are hand-rolled |
| `pixel-mine` | Playable mining game with upgrades, HUD, menu, particles | Hand-rolls economy, upgrades, and HUD |
| Presentation | Declarative UI, action bindings, timelines, procedural recipes, cue stream | Preview is read-only. A real game still builds its HUD by hand |
| Procedural art | `VisualRecipe` supports rect, ellipse, line, palette, jitter, states, parts, and imported layers | No outlines, shading, gradients, symmetry, animation frames, atlas, contact sheet, or quality checks |
| CLI | `new`, `validate`, `trace`, `watch` | No `run`, `simulate`, `verify`, `dump`, or replay. Errors are prose |
| Scale | 1M row and 10M movement benchmarks | Real for a different genre, irrelevant here |

The strongest assets are the content loader, the deterministic kernel, the Presentation UI and cues, and the two playable samples. The weakest point is the rule vocabulary: it is too small to compose a game, so every sample writes C#.

## Hard decisions

**D1. Personal use, no distribution.** No license, NuGet, feed, or external compatibility work. `git clone` plus `./bootstrap.sh` is the install story.

**D2. No genre nouns in the framework.** No type, method, or module named Talent, Merge, Prestige, Upgrade, Producer, or Colony. A code review rejects them.

**D3. General primitives only.** A new framework primitive must be genre-neutral and serve at least two different mechanics. If it serves only Gnorp, it is a game action or a pattern.

**D4. Patterns are content.** Producers, upgrades, talents, merges, prestige, workers, and offline summaries ship as content in a pattern pack. They can be copied and changed. They are not APIs.

**D5. Game actions are C#, not per-talent C#.** The game registers verbs such as strike, build, carry, and meditate. Rules compose them across many mechanics. A talent never gets its own class.

**D6. The 41 talents and the second game are the acceptance tests.** One proves expressiveness, the other proves the absence of genre lock.

**D7. One composition layer, one UI, one host, one save.** No second economy layer.

**D8. Offline is fast-forward.** The host runs elapsed ticks as fast as possible, deterministically. What production means is rules. No offline API.

**D9. Balance is a command.** `systemic simulate` runs a pack headless, applies a policy, and writes a CSV. No balance UI.

**D10. Procedural-first art.** At least 90 percent of art is a recipe. Fonts and audio are the only imports.

**D11. Determinism and save stay.** Integer and fixed-point simulation. Presentation may use floats. Save, fast-forward, and replay are tested.

**D12. Freeze `Systemic.Incremental`.** Do not add features to it. Migrate its useful behavior to patterns, then delete it when the samples no longer need it.

## Phase 0: the loop and a playable template (1 week)

Goal: my agent can scaffold, run, simulate, verify, and render assets from a clean clone.

| ID | Work | Acceptance evidence |
| --- | --- | --- |
| 0.1 | Bootstrap script: restore, build, test, run the template | `git clone && ./bootstrap.sh` works with only the SDK installed |
| 0.2 | `systemic new --template systemic` creates a playable game: one quantity, one scheduled rule, one tag toggle, one generic list, one asset, save, and a headless test | Window opens; `--headless --ticks 600` changes state |
| 0.3 | Add `run`, `simulate`, `verify`, `dump`, `save`, `load`, and `replay` commands | Each works headless on the template |
| 0.4 | `systemic simulate --seconds 3600 --policy greedy --csv` runs a balance pass | One game hour in under 10 seconds |
| 0.5 | Add `--json` and one error envelope: `code`, `message`, `file`, `path`, `line`, `column`, `docs`, `hint` | Snapshot tests for success and each diagnostic class |
| 0.6 | Document exit codes: 0 success, 1 content error, 2 usage, 3 runtime, 4 verification mismatch | README and `--help` agree |
| 0.7 | Rewrite `AGENTS.md`: build, test, run, simulate, content map, determinism rules, common failures | Every command is copy-paste runnable |
| 0.8 | Add `docs/agents/` with one task per file: state, fact, selector, effect, rule, tag toggle, save, asset | Each under 80 lines and linked to a runnable example |
| 0.9 | README claims match the new target | No claim without a gate artifact |
| 0.10 | Asset loop: template ships a procedural icon, actor, and panel; `assets sheet`, `validate`, and `atlas` work | Contact sheet and atlas from content; no PNG except the font |

Exit gate G0: from a clean clone, my agent runs `./bootstrap.sh`, scaffolds the template, plays it, simulates one hour, and verifies a save, using `AGENTS.md` alone. Under 15 minutes.

Stop rule: if G0 needs a human step, do not start Phase 1.

## Phase 1: the composition layer (5 weeks)

Goal: the rule model can express producers, upgrades, talents, merges, prestige, and workers without any of those being framework concepts.

Started 2026-10-30: 1.1 audit is recorded in
[`phase-1-incremental-audit.md`](phase-1-incremental-audit.md). 1.2 is implemented for
declared facts with typed payloads, including payloads on emitted and scheduled facts.
1.3 is implemented as a bounded entity selector with tag, property, spatial, nearest,
aggregate, and formula conditions. The next slice is 1.4 effects.

| ID | Work | Acceptance evidence |
| --- | --- | --- |
| 1.1 | Audit `Systemic.Incremental`; list every genre noun and every behavior worth keeping | A removal note and a pattern mapping |
| 1.2 | Facts: named events, source and target, payload schema, deterministic dispatch | Two different mechanics use the same fact pipeline |
| 1.3 | Queries: selectors by tag, property, range, nearest, aggregate, count, and formula | Motivation and Extra Housing compile |
| 1.4 | Effects: quantity ops, spawn, despawn, transfer, emit, schedule, tag, and action invocation | Merge and Hiking Buddy compile |
| 1.5 | Modifiers: add, multiply, set, min, max, stacking, duration, scope, and rank awareness | A timed status and a permanent upgrade share one modifier path |
| 1.6 | Rules and rule bundles: guards, priority, while conditions, enable and disable, deterministic order | A talent tag toggles a bundle; a status expires |
| 1.7 | Time: tick, schedule, delay, cooldown, and deterministic fast-forward | Eight hours of fast-forward matches online within one percent |
| 1.8 | Generic UI binding: lists, toggles, costs, cooldowns, tooltips, buy amounts, and actions | A choice list and an upgrade list use the same widgets |
| 1.9 | Generic save: scopes, state graph, migration, content fingerprint | Save and load across a reset and a content change |
| 1.10 | Pattern pack: producer, upgrade, talent, merge, prestige, worker, offline, status, unlock | Eight fragments each with a minimal example and a test; offline is host catch-up over the same rules, with no offline content |
| 1.11 | Port all 41 talents as tags and rules, one file per group plus default | 41/41, each at most 12 behavior lines, zero C# |
| 1.12 | One headless test per talent, positive and negative | 41 tests green; each names the talent |
| 1.13 | Asset DSL: primitives, outline, shading, gradient, dither, symmetry, parts, frames, atlas metadata | The target recipe compiles |
| 1.14 | Asset commands: `sheet`, `validate`, `atlas`; labeled manifest | Sheet and atlas from content |
| 1.15 | Quality checks: palette, alpha, bounds, pivot, frames, silhouette, contrast | A bad recipe fails with a useful diagnostic |
| 1.16 | Port the slice's art to recipes; delete hand-made atlases and the Aseprite dependency | Only the font remains imported |

Exit gate G1: 41/41 talents as general rules and tags, zero framework genre types. The pattern pack covers producer, upgrade, merge, prestige, and worker with tests. `Systemic.Incremental` is frozen, and no new genre API was added.

Stop rules:
- If a pattern needs a framework feature, the feature must be expressed as a general primitive and proven with a second mechanic.
- If a talent needs a new game action, add one action, not a talent class.
- If the rule model starts to look like a scripting language, stop and name the missing concept.

## Phase 2: two games, one framework (3 weeks)

Goal: a Gnorp-like game with all 41 talents and a merge-focused game with no talents, both on the same primitives.

| ID | Work | Acceptance evidence |
| --- | --- | --- |
| 2.1 | Game A: rock, shards, retrieval, four gnorp types, six structures, twelve upgrades as patterns, all 41 talents as rules and tags, prestige, fast-forward | Playable; all 41 selectable |
| 2.2 | Game B: fruit tiers, combine rules, producers, workers, prestige, no talents | Playable; no talent tag or rule |
| 2.3 | Build B without changing the framework | Diff shows content only |
| 2.4 | Generic HUD: resources, rates, choice list, upgrade list, cooldowns, tooltips, fast-forward summary | Both games use the same widgets |
| 2.5 | Feedback: floating numbers, flashes, particles, screen shake | Every purchase and hit has feedback |
| 2.6 | Balance pass with `simulate` for both games | CSV and a tuning note per game |
| 2.7 | Save, reload, fast-forward, and replay at 1x and 4x | Same hash after reload; fast-forward within one percent |
| 2.8 | All art procedural except the font; contact sheet accepted | Atlas loads; no PNG | 

Exit gate G2: both games are playable for two hours. Game A has all 41 talents. Game B has none. The framework diff between them is empty. 10,000 actors and 50,000 particles hold 60 Hz. Save, fast-forward, and prestige work in both.

Stop rules:
- If Game B needs a framework change, the primitive is too specific. Fix it before continuing.
- If Game A needs a per-talent class, the composition layer failed.
- Three weeks is the cap.

## Phase 3: agent evaluation (1 week, overlaps Phase 2)

Goal: measure the agent on mechanics, not on a tutorial.

| ID | Work | Acceptance evidence |
| --- | --- | --- |
| 3.1 | Task corpus of 14 tasks across both games | Each has a prompt, a starting state, a verification command, and a token budget |
| 3.2 | Harness: fresh checkout, my daily model, three attempts, pass@3, transcripts | One command runs the suite and writes a report |
| 3.3 | Error taxonomy: schema, rule composition, UI binding, save, fast-forward, determinism, build | Every failure classified; recurring classes become diagnostics |
| 3.4 | Baseline the current tree | Near-zero pass rate kept as the number to beat |
| 3.5 | Run before each gate; block on regression | Report checked in under `artifacts/agent-eval/` |

The 14 tasks:

1. Add a quantity and a scheduled rule that increases it.
2. Add a cost and a rank, with a modifier that reads the rank.
3. Express Motivation as a tag and a rule.
4. Express Project Constellation as a scheduled rule.
5. Add a tag toggle list with group and point rules.
6. Build a three-tier merge chain from rules.
7. Express prestige as a reset rule with a preserved scope.
8. Fast-forward eight hours and show a summary.
9. Format a value at 1e15 and bind it to the HUD.
10. Add a worker behavior with carry and return.
11. Add a save migration after a schema change.
12. Rebalance production in Game B.
13. Generate a new creature recipe with idle and work frames.
14. Generate eight UI icons in one style and pass the sheet.

Exit gate G3: pass@3 at or above 80 percent on 14 tasks, median time to a verified change under 10 minutes, median token use under 60k per task, no task below 50 percent.

Stop rule: if G3 is under 50 percent, do not add features. Redesign the content surfaces from the transcripts.

## Phase 4: dogfood and polish (1 week)

Goal: both games are worth opening again.

| ID | Work | Acceptance evidence |
| --- | --- | --- |
| 4.1 | Three play sessions of at least 30 minutes across both games | A punch list |
| 4.2 | Fix the top five friction points | Each fix has a test or a content diff |
| 4.3 | Tune the first hour of each game with `simulate` | Time-to-tier within the target band |
| 4.4 | Add audio only if the silence hurts | One cue per purchase, hit, unlock, and reset |
| 4.5 | Save migration and replay across a reset | Load and hash tests |
| 4.6 | Tag `v0.1` locally | Bootstrap from that tag works |

Exit gate G4: both games are playable from a clean clone at `v0.1`, and every earlier gate still passes.

## Creation contract

1. Clean clone to playable template: under 15 minutes.
2. New game idea to playable loop: under one day.
3. A game with different mechanics, no framework change: under two days.
4. New talent: at most 12 behavior lines, under five minutes.
5. New merge chain: at most 10 lines per tier.
6. New producer, upgrade, or status: content only, under 10 minutes.
7. New mechanic: content rules plus existing game actions; a new action only when two mechanics need it.
8. New asset: under 10 minutes, at most three sheet passes.
9. Edit to visible change: under 2 seconds.
10. Simulate one game hour: under 10 seconds.
11. Verify everything: one command, `systemic verify`.
12. Every failure has a code, a location, and a next action.

## Performance contract

| Tier | Workload | Budget | Gate |
| --- | --- | --- | --- |
| Game | 10,000 actors, 50,000 particles, 20 active rule bundles, 60 Hz | p95 ≤ 8 ms, zero simulation allocation | both games |
| Colony | 1,000 actors, 12,000 motes, 60 Hz | p95 ≤ 4 ms, zero simulation allocation | Game B |
| Content | 2,000 entities through rules | p95 ≤ 5 ms, ≤ 4 KiB per tick | existing gate |
| Balance | 1 hour of game time, 1,000 actors, headless | ≤ 10 seconds | `simulate` |
| Assets | 100 recipes, 4 states, 24px | sheet ≤ 30 s, atlas ≤ 2 s | `assets` |
| Headroom | 100,000 actors, movement and simple behavior | p95 ≤ 16 ms | optional, not a release gate |

The 1M and 10M tiers stay as diagnostics. They are not release gates and not on the front page.

## What this plan deletes or freezes

- Delete the 1M and 10M agent gates from the critical path and the README headline.
- Delete the 60 Hz million-agent claim.
- Delete the behavior layer built for 1M agents.
- Freeze `Systemic.Incremental`. No new resources, producers, upgrades, roles, or advancements. Move what is useful into the pattern pack and delete it when unused.
- No framework type, method, or module named Talent, Merge, Prestige, Upgrade, Producer, or Colony.
- Delete per-talent C#. A talent that needs a class is a failed gate.
- Delete the second hosting API and the second economy layer.
- Freeze the expression language. Either a game uses it or it goes.
- Stop hand-building a HUD per sample. One generic UI path.
- Delete hand-made PNG atlases and the Aseprite dependency. Fonts and audio stay imported.
- No editor, new renderer, networking, or multiplayer.

## Resourcing and sequence

One developer, 11 to 13 weeks, with a three-week buffer.

| Week | Phase | Milestone |
| --- | --- | --- |
| 1 | 0 | Bootstrap, playable template, run and simulate commands, `AGENTS.md` |
| 2-6 | 1 | Facts, queries, effects, modifiers, rule bundles, patterns, 41 talents, assets, UI, save |
| 6-8 | 2 | Game A, Game B, no framework diff, HUD, feedback, balance |
| 8-9 | 3 | Task corpus, harness, baseline, pass@3 gate |
| 9-10 | 4 | Play sessions, fixes, audio if needed, `v0.1` |
| 11-13 | buffer | Fix what the gates expose; do not add scope |

Critical path: queries, effects, rule bundles, the 41-talent pack, Game B without a framework change, and the asset pipeline. Everything else can slip.

## Risks and kill criteria

| Risk | Likelihood | Response | Kill criterion |
| --- | --- | --- | --- |
| Genre nouns leak into the framework | High | No Talent, Merge, Prestige, Upgrade, Producer, or Colony types; review every primitive against two mechanics | A genre type appears: reject the change |
| General primitives are too low-level for the agent | High | Pattern pack, recipes, examples, and eval tasks | Agent pass@3 under 50 percent: simplify the primitives, not add genre APIs |
| The 41 talents force Gnorp-specific hooks | High | Express them as tags, rules, and reusable game actions; prove with Game B | A hook serves only one talent: make it a game action or cut it |
| Game B needs a framework change | Medium | Fix the primitive so both games use it | A content-only diff is impossible: stop and redesign |
| Rule model becomes a scripting language | High | Closed vocabulary, named verbs, load-time validation | Arbitrary expressions or callbacks appear: reject |
| Procedural art looks samey or unreadable | High | Style guide, palette, silhouette and contrast checks, contact sheet | Two sheets rejected: import a curated pack for that category |
| Asset DSL becomes a paint program | High | Named primitives; add one only when three assets need it | More than eight new primitives in a week: stop |
| Solo time runs out | Medium | Phase gates, two games, no external obligations | Week 13 without G3: finish Game A, stop framework work |

## Definition of done

1. Clean clone, bootstrap, play the template.
2. My agent takes a one-paragraph idea to a playable game in under one day.
3. All 41 Gnorp talents are general rules and tags, each at most 12 behavior lines, zero C#, with a headless test.
4. Merging, prestige, producers, upgrades, and workers are content patterns, not framework features.
5. A second game with merging and no talents runs on the same primitives with no framework diff.
6. 90 percent of art is procedural, reviewed as a contact sheet and packed into an atlas; only the font and audio are imported.
7. 10,000 actors and 50,000 particles hold 60 Hz within 8 ms p95 with 20 active rule bundles.
8. One game I play for more than two hours, built with the kit, tagged `v0.1`.
9. 80 percent pass@3 on 14 tasks across both games.
10. No framework type named Talent, Merge, Prestige, Upgrade, Producer, or Colony.
11. README claims match the gates, with artifacts.

If item 5 is false, the framework is a Gnorp engine. If item 10 is false, the framework is a genre kit. Either one fails the goal.
