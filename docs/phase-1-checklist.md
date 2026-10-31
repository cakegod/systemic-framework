# Phase 1 execution checklist

Goal: finish the code-first composition layer and the procedural asset pipeline so Game A is
built from interacting systems and can produce player-authored strategies, not a rigid
authored tree.

Gate G1: 41/41 talents are data plus behavior functions with host-level positive and
negative scenarios and an expressive-agency note. Each opens a distinct way to play, has a
competing-choice comparison, and names two synergies; no choice is only a numeric upgrade.
At least three play-tested combination strategies that no content branch names are recorded
from general rules. The neutral pattern library is used by both games, the framework has no
genre type, save and replay carry the module version, and the asset sheet and atlas are
accepted. The legacy JSON rule engine, expression language, and rule schema are deleted.

Status: the old rule-engine work (1.1-1.12) is frozen and scheduled for deletion. The asset
work (1.13-1.16) remains. Code-first migration starts at 1.17; 1.17 through 1.20 are
complete, and 1.21 is next. Every unchecked item is a TDD slice: failing test first,
smallest implementation to green, then refactor.

The architecture decision is in
[ADR 0004](adr/0004-code-first-simulation.md). Behavior is C# systems and commands. JSON is
data. Do not add JSON rule conditions, effects, subjects, schedules, expressions, or facts.

## Execution rules

- Do not add a framework type, method, or module named Talent, Merge, Prestige, Upgrade,
  Producer, or Colony. Those are game-side patterns.
- A pattern that needs a framework feature must express it as a general primitive and
  prove it with a second, different mechanic.
- Systems are stateless. Timers, cooldowns, ranks, selection, and counters live in the World
  or a saved extension.
- A green unit test that manually dispatches a fact or sets a behavior input is not host
  evidence. The scenario must run through the game's host with real systems.
- A choice must pass the [expressive-agency test](design/expressive-agency.md). A strictly
  better stat upgrade is a failed choice, even when it is balanced.
- A combination must arise through general rules. A branch named after one pair of mechanics
  is a special case: generalise the primitive or cut the combination.
- A game whose every viable strategy is enumerated in content is a menu. Interaction
  surface and emergent play are acceptance evidence, not extra polish.
- Every item closes with a test, a content artifact, or a command transcript. "It works"
  is not evidence.
- Keep the acceptance evidence in the repository, next to the code or content it proves.

## Legacy work (frozen)

| ID | Work | Status |
| --- | --- | --- |
| 1.1-1.12 | JSON rule engine, facts, queries, effects, modifiers, bundles, time, UI, save, pattern pack, 41-talent reference corpus and tests | legacy; reference only; delete by 1.26 |

Do not extend this list. The legacy engine is deleted after the code-first games and tests
replace it.

## Asset work

| ID | Work | Acceptance evidence | Depends on | Status |
| --- | --- | --- | --- | --- |
| 1.13 | Asset DSL: primitives, outline, shading, gradient, dither, symmetry, parts, frames, atlas metadata | The target `gnorp.slammer` recipe compiles; every new primitive has a raster test | none | pending |
| 1.14 | Asset commands: `sheet`, `validate`, `atlas`, labeled manifest | Sheet, atlas, and manifest are produced from content; a bad recipe names its file and path | 1.13 | pending |
| 1.15 | Quality checks: palette, alpha, bounds, pivot, frames, silhouette, contrast | A bad recipe fails once for each check with a useful diagnostic; a good batch passes | 1.13, 1.14 | pending |
| 1.16 | Port the slice's art to recipes; delete hand-made atlases and the Aseprite dependency | Only the font remains imported; the contact sheet is accepted; the game loads the generated atlas | 1.13, 1.14, 1.15 | pending |

## Code-first migration

| ID | Work | Acceptance evidence | Depends on | Status |
| --- | --- | --- | --- | --- |
| 1.17 | Game module API: `ISystemicGameModule`, `AddModule`, typed commands, and module version tracking | A game module composes systems and commands; duplicate module IDs fail; a failed compose poisons the builder | 1.6, 1.7 | done (`SystemicGameModuleTests`, `samples/module-pack` with `Systemic.ModulePack.Tests`) |
| 1.18 | Typed commands and events: record handlers, deterministic dispatch, UI action binding | A UI button invokes a typed command; a missing handler or an unvalidatable binding is a startup error; a system emits a typed event that dispatches after the tick and is discarded on rollback | 1.17 | done (`SystemicTypedEventTests`, `UiCommandActionTests`, `SystemicGameModuleTests`) |
| 1.19 | Data-only content path: `rules.json` optional, no rule compilation or rule engine for new packs, data validation for entities and tuning | A pack with no `rules.json` on disk loads, builds, and steps without a rule engine or rule-history state | 1.17 | done (`ContentPackTests.AContentPackWithNoRulesFileRunsADataOnlyHost`, `SystemicGameModuleTests.APackWithNoRulesRunsWithoutARuleEngine`) |
| 1.20 | Save and replay composition version: module ID, version, content fingerprint, mismatch diagnostics | A changed game or content module version rejects an old save with a clear message; replay hashes still match | 1.17, 1.19 | done (`SystemicGameModuleTests.AChangedModuleVersionRejectsAnOldSave`, `DeterministicReplayTests.ModuleVersionsTravelWithTheReplayAndADifferentCompositionFailsVerification`, `Systemic.ModulePack.Tests`) |
| 1.21 | Five-talent host spike: Motivation, Strength In Numbers, Project Constellation, Hiking Buddy, Deals, plus one pattern | Host scenarios with real systems, positive and negative cases, save/load, replay, and p95 measurement | 1.18, 1.19, 1.20 | pending |
| 1.22 | Port and design-audit the 41-reference corpus as data plus behavior functions | 41/41 host scenarios and expressive-agency notes; each has a competing-choice comparison, two named synergies, and one non-obvious interaction exercised through general rules; numeric-only or clone-only talents redesigned or replaced; no per-talent class; no flag without a consumer; no test-only property | 1.21 | pending |
| 1.23 | Neutral pattern library: interval emitter, costed mutation, tier combiner, scope reset, tag gate, row worker | Game A and Game B reuse the same patterns without framework changes | 1.21 | pending |
| 1.24 | Game A playable with the code-first module | Playable; 41 meaningful choices; at least three recorded combination strategies that no content branch names, exercised through general rules; save, fast-forward, prestige | 1.22, 1.23 | pending |
| 1.25 | Game B playable with merging and no talents | Playable; no talent tag or behavior; framework diff empty | 1.23 | pending |
| 1.26 | Delete the rule engine, expression language, rule schema, rule tests, and rule docs | Build and tests pass without the legacy runtime; no new JSON rule primitives | 1.22, 1.24, 1.25 | pending |
| G1 | Phase 1 exit gate | 41/41 data-plus-behavior reference choices with host scenarios and expressive-agency notes, each with a competing-choice comparison and two synergies, at least three play-tested combination strategies no content branch names, neutral pattern library, no genre type in the framework, module-versioned save/replay, asset sheet and atlas accepted, legacy rule engine deleted | 1.13-1.26 | pending |

## Sequence

1. 1.17 game module and commands, because every code-first slice needs a composition root.
2. 1.18 command dispatch and UI binding, because input is not a rule file.
3. 1.19 data-only loading, because new packs must stop depending on `rules.json`.
4. 1.20 save/replay versioning, because behavior changes must not silently reuse old state.
5. 1.21 five-talent host spike, because it is the cheapest proof of the architecture.
6. 1.22 the 41-reference corpus, one group at a time, with the design audit.
7. 1.23 neutral patterns, then 1.24 and 1.25 for the two games.
8. 1.26 delete the legacy engine only after both games and their tests no longer use it.
9. 1.13 to 1.16 assets in parallel with the migration.
10. Run G1 from a clean clone and record the commands.

## Stop rules

- If a pattern needs a framework feature, make the primitive general and prove it with a
  second mechanic, or cut the pattern.
- If a talent needs a new game command, add one command used by at least two mechanics. Do
  not add a talent class.
- Do not add another JSON condition, effect, subject, schedule, expression, or fact to make
  a mechanic fit.
- If a game-side module starts to look like a framework genre API, move it into game code.
- If a choice is a strict stat upgrade with no trade-off or distinct future, redesign it or
  cut it. Do not accept it because it is balanced.
- If a combination needs a special case, generalise the rules or cut the combination. Do not
  add a branch named after the pair.
- If every viable strategy is already written in content, the game is a menu. Add
  interaction surface, not more options.
- If a system needs state outside the World, move the state into the World or a saved
  extension.
- If the asset DSL gains more than eight new primitives in a week, stop and simplify.
