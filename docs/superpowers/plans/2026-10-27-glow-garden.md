# Glow Garden Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Build the approved playable single-screen garden incremental sample.

**Architecture:** A deterministic garden model runs as an `ISystemicSystem` in a content-loaded `SystemicGame`. A MonoGame host renders its state and submits progression commands; headless tests use exactly the same host and model.

**Tech Stack:** .NET 10, existing MonoGame 3.8.4.1, Gum.MonoGame (code-built Forms UI, user-requested), Systemic.Core, xUnit 2.5.3.

**Spec:** `docs/superpowers/specs/2026-10-27-glow-garden-design.md`

## Global Constraints

- One main currency: Glow; two independently refundable upgrade trees.
- Six toggleable talents with at most three active; no Channelers, prestige, or saving.
- Existing samples and framework behavior stay unchanged.
- Use generated graphics and text without external assets or a content compiler.
- Bounded storage; target up to roughly a thousand creatures and thousands of motes, without a 100k claim.
- Simulation scratch is not integrated with framework save/rollback; document this limitation.

## Review Focus

- Insufficient Glow must leave state unchanged (Task 1).
- Bulk buys must stop at affordability and refund exactly once (Task 1).
- Resetting a tree must preserve hired workers, other ranks, and carried Glow (Task 1).
- Chain/clone/seed combinations must terminate at fixed caps (Task 2).
- Pausing or restarting must not retain stale commands or crash graphics resources (Task 3).

### Task 1: Shared model, content, host, and progression

**Files:** Create `samples/glow-garden/{GlowGarden.csproj,GardenSimulation.cs,GardenProgression.cs,GardenHost.cs,content/game.json,content/entities.json,content/rules.json}` and `tests/Systemic.GlowGarden.Tests/{Systemic.GlowGarden.Tests.csproj,GardenTests.cs}`.

**Interfaces:** `GardenHost.Create(int seed)` returns a disposable host exposing `Simulation`, `Game`, and `Step(int ticks)`. `GardenSimulation` implements `ISystemicSystem`; exposes read-only plant, creature, and mote state plus `Glow`, `BankedGlow`, `BurstCount`, and talent diagnostics. `Hire(CreatureKind)`, `Buy(UpgradeKind,bool bulk=false)`, and `ResetTree(UpgradeTree)` mutate progression synchronously between ticks and return success/refund. Counts and prices appear in UI through the same methods/properties.

- [x] Write tests for deterministic seed, nonzero production and banked Glow after 3600 ticks, unaffordable no-op, exact independent refunds, and bulk affordability. Run them and confirm missing model fails.
- [x] Implement content-backed host at 30 Hz, simple plants/creatures/motes, bounded movement/collection, and progression. Start with 2 Burstlings, 4 Gatherers, 30 Glow, 6 plants. Use fixed arrays/lists with explicit limits; cap permanent workers at 1000 total, plants at 64, motes at 12000, and copies at 128. Track each tree's exact spend and allow 25 ranks per upgrade.
- [x] Run focused tests until these behaviors pass.

### Task 2: Six composable talents

**Files:** Create `samples/glow-garden/GardenTalents.cs`; extend model and `GardenTests.cs`.

**Interfaces:** `ToggleTalent(Talent)` returns false when enabling a fourth talent; disabling always succeeds. `Talent` contains Echo, Seedstorm, Magnetism, ChainReaction, LivingProjectile, Clone. Expose effect counts to headless checks and renderer feedback.

- [x] Add failing tests for three-slot limit, seed growth, delayed Echo, chain bursts, bounce impacts, copies expiring, Magnetism changing collection, and bounded combined execution.
- [x] Implement delayed impacts in a bounded queue, breadth-limited reactions, seed establishment, magnet movement, bounce targeting, and finite-lived clones. Effects require ripe targets; iterate deterministically. Increment effect diagnostics only on actual effects.
- [x] Run tests; compare identical seeded runs and stress bounds for several talent loadouts.

### Task 2b: Strengthen Systemic integration (user-requested extension)

**Files:** Add `samples/glow-garden/GardenPopulationKinds.cs` and population-backed state views/helpers as needed; modify model/host/progression/talents, content entities/rules, and focused tests. Keep framework internals unchanged.

**Interfaces:** Preserve GardenHost and read-only simulation/progression APIs consumed by Task 3. Construct the model against the actual host World and register the required fact observer through the builder. Generated population columns are the only authoritative plants/creatures/motes storage. Use World.Draws. Keep stable IDs independent of dense row positions. Existing tests may use new internal scenario helpers in place of reflection into deleted lists.

- [x] Write failing tests proving authoritative World populations own live state, population edits roll back under a real Kernel transaction, World.Draws advances deterministically, and an authored talent rule controls its actual effect.
- [x] Replace lists with generated typed population schemas and supported column edits/structural operations. Reserve existing caps. Preserve readable immutable state views for renderer and all gameplay tests. Handle swap-back removals without stale identities or skipped rows; use supported spatial indexing where it reduces repeated scans without invalidating rows.
- [x] Publish real pod-burst/collection/deposit facts. Store active talent tags on a World entity; authored Core rules select talent events and observer/commit hooks apply actual bounded effects. C# retains spatial targeting, movement, and effect implementations. Trace a real rule reaction and assert removing its authored rule suppresses the corresponding effect. Do not add empty rules that only count telemetry.
- [x] Run all focused Release tests, add actual framework-integration assertions and deterministic combined scenarios, and build the sample. Document remaining limitations accurately. Preserve existing public gameplay and UI contracts; no claim of complete save/rollback for scratch state.

### Task 3: Playable host, readable UI, smoke command, and documentation

**Files:** Create `samples/glow-garden/{Program.cs,GardenGame.cs,GardenUi.cs,README.md}`; update `Systemic.sln` to include sample and tests.

**Interfaces:** `Program` supports normal graphical launch, `--headless --ticks N --seed N`, and a finite `--verify-render` run. `GardenGame` owns world drawing/input; `GardenUi` builds Gum Forms panels/buttons/text and updates labels from the model. Initialize, update, and draw Gum through its documented service; use its packaged default fonts and visuals without an editor or content compiler.

- [x] Implement dark garden scene, planted pods, bouncing workers, scattering Glow, carrying trails, impact rings, colony, and panels. Add click targets and keyboard equivalents for hiring, five upgrades (work rate, speed, capacity, growth, yield), bulk purchasing, two refunds, six talents, pause, speed, and restart. Display balance, rates, counts, costs, active slots, descriptions, and controls.
- [x] Add headless command with a finite seeded scenario reporting actual production and collection and nonzero exit on invalid arguments or broken workflow. Add tests for invalid command values and restart creating fresh deterministic state.
- [x] Add projects to solution and document controls, limits, architecture, run/test commands, deferred features, and graphical verification limits.
- [x] Build Release, run focused tests and headless smoke, format only new files, then run relevant existing suites. Attempt a finite graphical check if display/native dependencies are available. Review and fix material findings before completion.

## Self-review

All approved mechanics map to Tasks 1–3. Prices, art, and exact panel layout are builder choices. Gum is the one new production dependency, explicitly requested by the user. Existing hardware timing failures are reported separately from functional checks. No worktree is needed in the isolated cloud workspace.
