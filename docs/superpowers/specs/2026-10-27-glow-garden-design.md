# Glow Garden prototype

## Intent

Build a playable, single-screen incremental sample in `samples/glow-garden` using C#, MonoGame, and Systemic. Pods grow, Burstlings burst them, Gatherers carry scattered Glow back to the colony, and the player spends that one currency on a visibly growing swarm. Simple automatic work and surprising talent combinations matter more than numerical optimization.

## Approaches

The user selected a separate MonoGame sample with a headless validation mode. Extending the boids sample would blur its purpose; a browser host would introduce a second rendering stack. Reuse the existing .NET 10 and MonoGame versions and Systemic fixed-step hosting. Leave existing samples and framework behavior unchanged.

## Playable scope

- One fixed garden view with a colony, a few initial plants, two Burstlings, and four Gatherers. Low-density pixel silhouettes and a dark palette with bright Glow make work visible. All graphics and readable UI are generated locally without an asset pipeline.
- Plants regrow pods. Burstlings automatically seek ripe plants, leap, and burst pods. Physical Glow motes scatter and settle; Gatherers seek motes, collect a bounded load, and return to the colony before currency is credited. No manual unit orders or production ratios.
- A small starting Glow balance permits an immediate purchase. Buttons hire either creature. Prices rise with count, and harvesting pays for further purchases. Target dozens of creatures in a short session, with bounded storage for up to roughly a thousand creatures and thousands of motes; no 100k performance claim.
- Two upgrade panels: Creatures (Burstling work rate, Gatherer speed/capacity) and Magic & Growth (pod growth, pod yield). Each has repeatable ranks, single and bulk buying, and a free reset refunding exactly that tree's spent Glow. Hired creatures remain. These choices and exact prices are builder decisions, surfaced in UI.
- Six toggleable talents, at most three active together, available immediately for prototype experimentation: Echo adds a delayed nearby impact; Seedstorm launches seeds that establish bounded new plants; Magnetism pulls nearby motes toward Gatherers; Chain Reaction bursts neighboring ripe pods; Living Projectile lets a Burstling bounce to another ripe plant; Clone creates short-lived Burstling copies after successful combos. Effects must be observable and composable. Bound reaction depth, temporary copies, plants, and particle counts; excess creation never allocates without limit. UI explains effects and active slots. Prestige unlocking is intentionally deferred.
- Use Gum Forms for the interface, as subsequently requested by the user, with code-built panels and controls following the MonoGame Gum tutorial. No Gum editor step is required. Pointer buttons and keyboard shortcuts; pause, restart, and speed controls. On-screen instructions and a README explain the loop, controls, and limitations. No save/load, Channelers, prestige, audio, networking, or external art in this slice.

## Simulation and integration

Use Systemic content loading and `SystemicGame` fixed-step execution for both the graphical game and headless runs. Register a garden `ISystemicSystem` for domain behavior. Keep the model independent of MonoGame graphics/input so tests can advance the actual same simulation.

The user subsequently requested stronger framework integration before UI work. Plants, creatures, and Glow motes must use authoritative Kernel populations with typed generated columns, not parallel custom lists. Use the World's deterministic draw stream. Publish actual pod bursts and collection/deposit facts; authored Core rules must select/emit talent reactions based on active talent tags, and the sample's domain system handles spatial/movement work through supported observer/commit hooks. Removing a talent rule must remove its actual effect, proving the rules are functional rather than telemetry. Preserve stable bounded iteration, all existing gameplay, and the public state views for UI. Exercise Kernel population transaction rollback in tests. Where practical, use population spatial queries to avoid the worst repeated nearest scans. Do not claim full sample save/rollback until scratch, progression, and delayed effects are covered; full save/load remains outside this playable slice. No framework internals need changing.

## Validation and deliverables

A runnable sample project, authored content, README, deterministic headless smoke command, and focused automated tests covering production-to-bank flow, affordability, bulk costs, exact independent refunds, active talent limits, actual talent effects/combinations, finite bounds, and repeatable seeds. Include a bounded automated progression scenario rather than crediting arbitrary currency in the normal game. Add the sample/tests to the solution if appropriate. Build Release and run tests. If a graphical device is available, run a finite render smoke check; otherwise explicitly report rendering as unverified. Performance gates in the existing repository are hardware-sensitive and remain separate from sample correctness.

Implementation details such as exact panel placement, numerical tuning, sprite shapes, and internal data layout are left to the builder. This document proposes the initial balance; playtesting can refine it.
