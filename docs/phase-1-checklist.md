# Phase 1 execution checklist

Goal: finish the composition layer and the procedural asset pipeline.

Gate G1: 41/41 talents are general rules and tags, with zero framework genre types. The
pattern pack covers producer, upgrade, merge, prestige, and worker with tests.
`Systemic.Incremental` stays frozen, and no new genre API is added.

Status: 1.1 through 1.7 are complete. 1.8 is the next slice. Every unchecked item below
is a TDD slice: failing test first, smallest implementation to green, then refactor.

## Execution rules

- Do not add a framework type, method, or module named Talent, Merge, Prestige, Upgrade,
  Producer, or Colony. Those are content patterns.
- A pattern that needs a framework feature must express it as a general primitive and
  prove it with a second, different mechanic.
- Every item closes with a test, a content artifact, or a command transcript. "It works"
  is not evidence.
- Keep the acceptance evidence in the repository, next to the code or content it proves.

## Checklist

| ID | Work | Acceptance evidence | Depends on | Status |
| --- | --- | --- | --- | --- |
| 1.1 | Audit `Systemic.Incremental` for genre nouns and behavior worth keeping | `docs/phase-1-incremental-audit.md` lists every noun and maps every behavior | none | done |
| 1.2 | Facts: named events, source and target, typed payload, deterministic dispatch | `facts` declarations, payload validation, emitted and scheduled payloads, three pipeline tests | 1.1 | done |
| 1.3 | Queries: selectors by tag, property, range, nearest, aggregate, count, and formula | `QueryConditionTests` covers a tag count, a property filter, a spatial range, nearest selection, and an aggregate formula; the selector is a bounded content primitive, so Motivation and Extra Housing compile without a genre type | 1.2 | done |
| 1.4 | Effects: quantity ops, spawn, despawn, transfer, emit, schedule, tag, and action invocation | `EffectPrimitiveTests` covers each primitive, spawn rollback, and action invocation; `EffectContentTests` runs merge and Hiking Buddy from JSON only | 1.3 | done |
| 1.5 | Modifiers: add, multiply, set, min, max, stacking, duration, scope, and rank awareness | `ModifierPrimitiveTests` covers clamping, source stacking, timed expiry, and rank-aware reads; `EffectContentTests` runs an upgrade whose modifier follows its rank from JSON | 1.3 | done |
| 1.6 | Rules and rule bundles: guards, priority, while conditions, enable and disable, deterministic order | `RuleBundleAndTimeTests` proves a talent tag enables a bundle and a timed grant expires it; rule order is priority then ID, asserted in `RuleEngineTests` | 1.4, 1.5 | done |
| 1.7 | Time: tick, schedule, delay, cooldown, and deterministic fast-forward | `RuleBundleAndTimeTests` covers a cooldown window and eight hours (1,728,000 ticks) of fast-forward, which matches online exactly | 1.4, 1.5 | done |
| 1.8 | Generic UI binding: lists, toggles, costs, cooldowns, tooltips, buy amounts, actions | A choice list and an upgrade list use the same widgets and bindings; action results are tested | 1.5, 1.6 | pending |
| 1.9 | Generic save: scopes, state graph, migration, content fingerprint | Save and load across a reset and a content change; a migration test covers the old schema | 1.6, 1.7 | pending |
| 1.10 | Pattern pack: producer, upgrade, talent, merge, prestige, worker, offline, status, unlock | Each pattern has a minimal content example and a test; no pattern names a framework type | 1.4, 1.5, 1.6, 1.7, 1.9 | pending |
| 1.11 | Port all 41 talents as tags and rules, one file per group plus default | 41/41 talents authored as content; each at most 12 behavior lines; zero C# | 1.3, 1.4, 1.5, 1.6, 1.10 | pending |
| 1.12 | One headless test per talent, positive and negative | 41 tests green; every test names its talent and asserts both the enabled and disabled case | 1.11 | pending |
| 1.13 | Asset DSL: primitives, outline, shading, gradient, dither, symmetry, parts, frames, atlas metadata | The target `gnorp.slammer` recipe compiles; every new primitive has a raster test | 1.2 | pending |
| 1.14 | Asset commands: `sheet`, `validate`, `atlas`, `preview`, labeled manifest | Sheet, atlas, manifest, and preview are produced from content; a bad recipe names its file and path | 1.13 | pending |
| 1.15 | Quality checks: palette, alpha, bounds, pivot, frames, silhouette, contrast | A bad recipe fails once for each check with a useful diagnostic; a good batch passes | 1.13, 1.14 | pending |
| 1.16 | Port the slice's art to recipes; delete hand-made atlases and Aseprite dependency | Only the font remains imported; the contact sheet is accepted; the game loads the generated atlas | 1.13, 1.14, 1.15 | pending |
| G1 | Phase 1 exit gate | 41/41 talents, pattern pack tests, frozen Incremental module, no genre type in the framework, asset sheet and atlas accepted | all above | pending |

## Sequence

1. 1.3 queries, because selectors unlock effects, bundles, and most talent rules.
2. 1.4 effects, because merge, spawn, transfer, and actions need the query model.
3. 1.5 modifiers and 1.6 bundles, because status, talent, and upgrade patterns share them.
4. 1.7 time and 1.8 UI, because offline, cooldowns, and generic lists depend on both.
5. 1.9 save and 1.10 patterns, because patterns must survive reset and reload.
6. 1.11 and 1.12 talents, one group at a time, with tests in the same change.
7. 1.13 to 1.16 assets, while the talent pack is reviewed in batches.
8. Run G1 from a clean clone and record the commands.

## Stop rules

- If a pattern needs a framework feature, make the primitive general and prove it with a
  second mechanic, or cut the pattern.
- If a talent needs a new game action, add one action used by at least two mechanics. Do
  not add a talent class.
- If the rule model starts to look like a scripting language, stop and name the missing
  concept.
- If the asset DSL gains more than eight new primitives in a week, stop and simplify.
