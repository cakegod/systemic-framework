# ADR 0004 — Code-first simulation, JSON data

- Status: Accepted
- Date: 2026-10-31
- Supersedes: the JSON rule engine, the expression language, and the typed composition proposal

## Context

The project needs one developer plus an agent to build original games that create expressive
choices, using Gnorp and Idle Colony as references rather than templates. The games must be
deterministic, saveable, replayable, free of framework genre types, and designed for
expressive agency through systemic emergence: choosing must feel like deciding what kind of
player to become, and general rules must be able to combine into strategies the developer
did not enumerate. The framework can enforce systemic composition; it cannot guarantee that
a game is synergetic or emergent. Those are properties of the game's design, demonstrated
in play. Picking a bigger number or following a prewritten branch is not enough.

The JSON rule layer was an attempt to make behavior data. It became an underpowered
programming language:

- It has conditions and effects, but no loops, iteration, formula-valued effects, or
  per-entity schedules.
- It needs loader, compiler, expression, query, and engine code to interpret a rule file.
- It hides routing in strings. A schedule can target one entity while the rule expects
  another, and the pack still validates.
- The 41-talent pack needed flags, test-only inputs, and manual fact dispatch to appear to
  work.
- Its tests passed while the host path did nothing.

Replacing that layer with a typed C# rule builder would be a third language. It would still
need an IR, a builder API, route validation, and a second execution model. The project does
not need non-programmer authoring, hot reload, or external content authors. It needs the
smallest surface that can express the games.

## Decision

The simulation is code-first. C# systems and commands own behavior. JSON is data.

### One execution model

`ISystemicSystem` is the only behavior unit. Systems run on the deterministic host
schedule with declared `SystemAccess`. Commands handle player, UI, and test input.

The JSON rule engine, expression language, rule compiler, rule schema, and rule history
state are removed. The old path remains only as a frozen legacy runtime until the samples
and tests no longer use it.

### World is the only simulation state

Systems are stateless step functions. Timers, cooldowns, ranks, selection, progress, and
counters live in entity properties, tags, modifiers, or population columns.

Derived caches are allowed only when they can be rebuilt after load. Anything that cannot
be rebuilt is state and belongs in the World or a saved extension.

This makes save and replay straightforward: capture the World, the population state, and
the composition version. There is no rule history, scheduled-fact table, or cooldown table
to serialize.

### JSON is data

JSON declares:

- `game.json`: manifest, tick rate, content paths, optional module declarations.
- `entities.json`: entities, archetypes, tags, numeric properties, links, visual recipe IDs.
- Tuning documents registered by modules.
- `ui.json`, `visuals.json`, `presentation.json`: data-bound UI, procedural recipes,
  timelines, and cues.

JSON does not declare rules, conditions, effects, schedules, expressions, or facts.

`rules.json` is a legacy reference format. New packs do not have one. The loader keeps it
only while existing samples depend on it.

### Game behavior is game-side C#

A game has a composition module:

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
        builder.AddCommand<BuyUpgrade, BuyUpgradeHandler>();
    }
}
```

A talent is data plus a behavior function, not a framework type:

```json
{
  "id": "talent.default.motivation",
  "group": "default",
  "cost": 1,
  "tags": ["talent.default.motivation"],
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

    // Design-audit example: low progress changes risk and timing, not just damage.
    context.Runner.AddTag("cannot-heal");
    context.Runner.AddModifier("abilityCooldown", ModifierOperation.Multiply, 0.5, ticks: 1);
}
```

The game owns `TalentContext`, `TalentSystem`, and the behavior table. The framework does
not know what a talent is. The same split applies to producers, upgrades, merges,
prestige, workers, status effects, and unlocks.

### Host and commands

`SystemicGameBuilder` gains a module registration path, typed commands, and module version
tracking. The game executable owns its host and exposes headless simulation and verification.
The `systemic` CLI stays responsible for content validation and asset generation.

The CLI may later gain `--game <assembly>`, but the core design does not depend on dynamic
assembly loading.

### Save and replay identity

Save and replay metadata include:

- the content fingerprint for JSON data;
- the game module ID and version;
- the World, populations, and saved extensions.

A behavior change bumps the module version. An old save or replay either migrates or fails
with a clear message. It never loads silently under different mechanics.

### Determinism

The strict determinism analyzer applies to game systems, not just the kernel. Simulation
code uses fixed-point or integer state, stable iteration order, seeded randomness, and no
ambient time. Hot systems avoid per-tick allocation. The host schedule and access
declarations remain the concurrency contract.

## What stays

- `Systemic.Kernel`: World, entities, populations, tags, properties, modifiers, spatial
  indexes, deterministic primitives, transactions.
- `Systemic.Core`: content loading for data, host scheduling, commands, save, replay,
  diagnostics, and CLI-facing content APIs.
- Presentation, Gameplay2D, Rendering, and the asset pipeline.
- `Systemic.Incremental` remains frozen and is deleted after the samples move.

## What is removed

- `RuleEngine` and `CompiledRuleSet`.
- The expression compiler and expression scope.
- `RuleCondition`, `RuleEffect`, `RuleSubject`, rule schedules, rule actions, and rule
  history/cooldown state.
- The JSON rules schema and rule compilation in the content loader.
- Rule-specific tests, sample packs, and documentation.
- The typed rule builder. It is not built.

## Consequences

### Positive

- One language for behavior. Formulas, loops, selection, and conditional effects are
  ordinary C#.
- No route strings, rule compiler, expression language, or rule runtime.
- Systems already have deterministic scheduling, access declarations, transactions, and
  host-level tests.
- Fewer moving parts in save and replay.
- The framework stays genre-neutral while the games keep their genre code.
- Porting a talent is adding data and a small behavior function, not extending a DSL.

### Negative

- A behavior change requires a rebuild.
- Non-programmers cannot author mechanics.
- Game modules need a versioning discipline so old saves are rejected or migrated.
- C# systems can drift into nondeterminism unless the analyzer and review enforce the
  rules.
- The old samples and tests must be ported or retired before the rule engine can be
  deleted.

## Rejected alternatives

- **Extend the JSON rule DSL.** This is building a language and its tooling. The evidence
  says the next missing primitive would be followed by another.
- **Build a typed C# rule builder over the same IR.** This keeps the IR, the second
  execution model, and the route-validation problem while adding another API.
- **Use an embedded scripting language.** Hot reload is not a requirement. Sandboxing,
  determinism, save state, and API surface cost more than they return here.
- **Write the whole game with no JSON.** JSON remains useful for validated data, tuning,
  UI, art, and saves.
- **Cut scope to one game.** This is the fastest route to a game but abandons the second
  game test and the framework thesis.

## Migration

1. Add the game-module API, typed commands, and module version tracking.
2. Build a five-talent host with real systems and data. No `rules.json`.
3. Add module versioning to save and replay.
4. Port the 41 talents as data plus behavior functions with host-level scenarios.
5. Port the pattern pack to neutral game-side C# patterns.
6. Port Game B and prove the framework diff is empty.
7. Delete the legacy rule engine, schema, tests, and documentation.

The first slice is the five-talent host, not a new rule API. If it does not meet the
save/replay and performance gates, the design is wrong before the 41-talent port starts.

## Acceptance

- The 41-reference corpus is ported as data plus behavior functions, with host-level
  positive and negative scenarios and expressive-agency notes. Each opens a distinct way to
  play and has a competing-choice comparison. The result is an original game, not a Gnorp
  clone.
- No talent has its own class, and the framework has no genre type.
- The second game changes game code and data only, not framework source.
- Save and replay include the module version and reject stale behavior.
- The rule engine, expression language, rule schema, and rule tests are deleted.
- 10,000 actors and 50,000 particles hold 60 Hz within the performance contract.
- A new mechanic is a system or command plus data, not a new JSON primitive.
