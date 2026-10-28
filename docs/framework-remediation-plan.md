# Framework remediation plan

This plan fixes the defects recorded in the architecture review of `9761417`. It is ordered so that
each phase ships on its own, keeps the suite green, and moves one of the three root causes.

The three roots behind every finding:

- **R1 — two storage models.** `Entity` (heap object, `ImmutableHashSet<string>` tags,
  `Dictionary<string,double>` properties) is the only store rules, modifiers, grants, tags, links and
  facts understand. `Population<TKind>` is the only store that scales. Nothing bridges them, so
  "systemic" and "huge" are mutually exclusive.
- **R2 — two dispatch engines in one host.** `SystemicGame` holds both `RuleEngine` (content facts)
  and `EventEngine` (typed extension events), each with its own queue, depth budget, transaction and
  save state.
- **R3 — transaction granularity is per operation, not per tick.** One transaction per dispatched
  fact, one journal copy per live modifier key per tick, and a JSON serialize-and-clone of every
  stateful extension every tick.

Fixing findings one at a time produces thirteen fixes and no core. Fix the roots.

## Status

| ID | State | Evidence |
| --- | --- | --- |
| P0.1 | Done | `src/Systemic.Kernel/EventEngine.Core.cs`; the live `EventEngine` is no longer named `V2`. |
| P0.2 | Done | `SimulationFactAggregator` and its test removed; nothing else referenced them. |
| P0.3 | Open | |
| P1.1 | Done | `RuleEngine.DispatchBatch` shares one transaction and one journal scope; `SystemicGame` dispatches a tick's deferred facts as one batch. Tests: `FactBatchDispatchTests`, `DeferredFactBatchTests`. |
| P1.2 | Done | `PropertyModifier`/`PropertyGrant` store an absolute `ExpiryTick`, so the tick loop no longer rewrites or journals live modifiers. Tests: `ModifierExpiryTests` (expiry timing, live entries unchanged, zero allocation). Accepted break: the save payload names the field `expiryTick`, so documents written before this change no longer load. |
| P1.3 | Done | `PresentationRuntime` uses typed in-memory rollback. `ISaveOnlyStateExtension` marks state the journal already covers, so the four kernel extensions and `BuildChoiceRuntime` no longer capture JSON every tick. Tests: `SaveOnlyStateTests`. |
| P1.4 | Done | `World.EntitiesWithTag` gives the module an O(tagged) query; `Gameplay2DModule` writes through `Commands`, declares `ReadWorld` + `DeferWorldWrites`, and shares a wave. The scheduler now treats a reader beside a buffered writer as compatible. Every built-in system declares its access explicitly. Tests: `TagQueryTests`, `Module_shares_a_scheduling_wave_with_other_deferred_writers`. |
| P1.5 | Done | Content tier in `GateTierTests` (2,000 actors, allocation and 5 ms p95). Kernel tier stays with the benchmark gate runner. The 50k/1k test is now a diagnostic under the approved tiers and reports timings without gating. The crowd tier needs P3 before it can be written. |
| P4.2 | Partial | Save schema is v5 and version 4 is rejected with a clear diagnostic; there is no v4 migration by decision. The replay fixture was regenerated for the new state format. |
| P5.2 | Done | `RollbackPropertyTests` uses FsCheck: random command sequences restore world state on rollback and match a parallel world on commit. |
| P2, P3, P4.1, P4.3, P5.1, P5.3–P5.6 | Open | P3 needs the 6–8 weeks the plan budgets; P2 depends on D2/D3; P4.1 and P5.1/P5.3–P5.6 are separate integrations. |

### Batch rollback semantics

A batch shares one transaction, so a fact whose dispatch rolls back aborts the whole batch: no fact
in it commits, and `RuleDispatchResult.RolledBack` reports it. This is a deliberate change from
per-fact commit. `WorldTransaction.AsBatchMember` is internal and leaves commit, rollback and
disposal with the batch owner while keeping `OnCommit` and `OnRollback` registration working.

### Save-only state

`ISaveOnlyStateExtension` is a marker: save and load still call `CaptureState`, but the host skips
the per-tick snapshot because World transactions already journal the state. A type may implement it
only when every tick mutation is World state or registers rollback on a World transaction. Batch
rollback actions registered on a nested scope are inherited by the enclosing scope on commit, which
is what makes this safe for `BuildChoiceRuntime`.

## Invariants

1. Every phase must end with `dotnet build Systemic.sln -c Release` clean at zero warnings and the
   full test suite green. No phase is merged with its own gate tier red.
2. Any change to `SystemicSaveCodec` state must ship with a version bump and a migration, per
   `README.md`.
3. Public API additions are deliberate: after P4.1 they require a `PublicAPI.Shipped.txt` diff.
4. Determinism is a hard requirement: no phase may introduce a time, thread-count, hash-order or
   platform-dependent value into simulation state.

## Decision gates

| ID | Decision | Recommendation | Blocks |
| --- | --- | --- | --- |
| D1 | Subject model: A tiered handoff, B populations as rule subjects, C populations only | **B, shipped as A first** | P3.* |
| D2 | Keep the 898-line expression language or keep only `RuleEffectKind` | **Delete the language, keep the effect list** unless a named content feature needs it | P3.1 |
| D3 | Does the typed `EventEngine` path have a user outside the Boids sample? | Investigate; if no, P2 becomes a deletion and saves a week | P2.1 |

D1 is recorded as an ADR before P3.1 starts. P3.8 is the stop point: if the 100k-crowd demo cannot
be built on the bridge, stop at A and narrow the README claim to *"systemic for dozens, dense for
millions."*

## Phase 0 — delete before building (2 days)

| ID | Work | Files |
| --- | --- | --- |
| P0.1 | Rename `EventEngineV2.cs` to `EventEngine.Core.cs`. It declares the live `EventEngine`; `EventEngine.cs` declares `TriggerContext`/`Trigger<TEvent>`. No behaviour change. | `src/Systemic.Kernel/EventEngineV2.cs` |
| P0.2 | Delete `SimulationFactAggregator` and its tests. It is referenced only by its own tests, and region-quantized fact aggregation is a gameplay decision, replaced by P3.7. | `src/Systemic.Core/Rules/SimulationFactAggregator.cs` |
| P0.3 | Collapse `SpatialIndex` (entity bounds) and `PopulationSpatialGrid2D` (row bounds) onto one grid algorithm parameterized by an offset table. Keep both public facades; delete the second implementation. | `src/Systemic.Kernel/SpatialIndex.cs`, `src/Systemic.Kernel/PopulationSpatialGrid2D.cs` |

Acceptance: build clean, suite green, no public API removed.

## Phase 1 — make the tick cheap and honest (1 week)

| ID | Work | Acceptance |
| --- | --- | --- |
| P1.1 | Add `RuleEngine.DispatchBatch(World, ReadOnlySpan<SimulationFact>, SimulationFactObserver?)`: one transaction, one journal scope, one depth and fact budget across the batch. `SystemicGame.DispatchDeferredFacts` gathers all queues into one span and calls it once. | Instrumented-journal test asserts exactly one transaction for 1,000 deferred facts; dispatch cost per fact no longer includes transaction setup. |
| P1.2 | Lazy modifier expiry: store absolute `ExpiryTick` instead of `RemainingTicks`; drop expired entries on read and write; run the `AdvanceModifiers` sweep only when a live-expiry counter is non-zero. Removes the per-key `[.. items]` journal copy and the `(expiredMemberships ??= [])` allocation. | Zero journal records on a tick with 10,000 live, unexpired modifiers. |
| P1.3 | Remove JSON from tick rollback. `SystemicGame.cs` `RegisterJsonRollback` is save-path only; per-tick rollback requires `ISystemicTransactionalState`/`ISystemicReusableTransactionalState`, and reaching the JSON path during `Step` throws. | Warmed allocation gate passes with Presentation and Incremental registered. |
| P1.4 | Declare `Access` on every built-in system (none in `src/modules` override it today, so all inherit exclusive `WriteWorld`); replace `Publish` with `DeferFact` where effects can wait a tick; move projectiles to a population schema to delete the `foreach (var entity in world.Entities)` scan in `Gameplay2DModule`. | No built-in system inherits `WriteWorld`; 2D module cost scales with projectile count, not entity count. |
| P1.5 | Split the gate into three tiers: kernel (populations, 5 ms p95, zero allocated), content (entities and rules, 2,000 actors, ≤ 4 KB/tick), crowd (100k rows, one rule, zero allocated). Update the README and `docs/million-agent-benchmark.md`. | Each tier has an owner and a number; the current single gate is retired. |

Phase 1 is the point where the red gate should turn green for the kernel tier. Stop and measure
before Phase 2.

## Phase 2 — one dispatch path (2 weeks)

| ID | Work | Acceptance |
| --- | --- | --- |
| P2.1 | Route `EventEngine` emissions into the same fact batch and transaction as content rules, with one depth budget and one ordering. Remove `_extensionEngine` and `EventEngineStatefulExtension` once the typed path is a facade over the batch. | One transaction per tick with both content rules and typed triggers active; a content fact chain and a typed chain observe each other in the same tick. |
| P2.2 | If `ActionPipeline`, `Selectors`, `IPayment`, `IActionEffect` and `ActionContext` have no caller outside `EventEngine` after P2.1, delete them. | Public API loses the second effect vocabulary, or a documented typed-trigger user justifies keeping it. |

## Phase 3 — the bridge that makes the thesis true (6–8 weeks)

| ID | Work | Acceptance |
| --- | --- | --- |
| P3.1 | Introduce `RuleSubject` with two addressings (`Entity(id)` and `Row(populationId, handle)`); compile `AdjustProperty`/`SetModifier` property names to column keys per schema, and reject unknown properties at load. Content schema version bump. | A rule naming a property its schema lacks fails at load, not at runtime. |
| P3.2 | Tags become a `ulong` mask column with a per-schema tag registry (64 tags), replacing `ImmutableHashSet<string>` on the row path. | Tag test and tag effect are O(1) with no hashing and no allocation. |
| P3.3 | Modifiers become fixed inline slot columns (`ModifierSlot8`: 8 × (ushort key, float value, int expiryTick)) instead of `Dictionary<(int,string), List<PropertyModifier>>`. | `SetModifier` never allocates; 100k rows carry modifiers without heap growth. |
| P3.4 | Row value journal: extend `PopulationStructureUndoBatch` with dirty-row capture so fact transactions roll back column values, not just structure. | Rollback property test (P5.2) passes for random value and structure edits. |
| P3.5 | Facts address rows by generation-checked `PopulationHandle<TKind>`; a despawn mid-chain is detected rather than silently retargeting. | Replay test covers despawn during a reaction chain. |
| P3.6 | Fixed-point (Q16.16) simulation positions for population schemas. Float positions under `MathF`/SIMD are where cross-platform determinism dies and no library fixes it. | State hash is identical across x64 and arm64 for the same seed and inputs. |
| P3.7 | `PopulationSink`: the per-game aggregation entry point that turns "N rows crossed a threshold" into one fact, so rule churn is bounded by design. Replaces the deleted aggregator. | Crowd rules process a bounded number of facts per tick independent of row count. |
| P3.8 | **Forcing demo:** 100k agents, one authored rule ("predators tagged X within radius of prey tagged Y deal damage"), one transaction per tick, zero warmed allocations, behaviour emerging from the content vocabulary entities already use. | Passes the crowd gate and builds from content JSON alone. |

## Phase 4 — API hygiene and migration (1 week)

| ID | Work |
| --- | --- |
| P4.1 | Make entity mutation command-only (`entity.ToRemove = true` becomes a command), add `Microsoft.CodeAnalysis.PublicApiAnalyzers` with a shipped-API baseline, and require `ImmutableArray<SystemAccess>` fields where access is stored. |
| P4.2 | Save schema v5 with migration from v4; update `docs/api/populations-and-scheduling.md`, `docs/api/system-context.md`, `docs/api/analyzers.md` and the README claim to match what the framework actually does. |
| P4.3 | Retire the expression language if D2 says so: delete `Expressions.cs` and its scope types, keep `RuleEffectKind` as the content vocabulary, and migrate sample content. |

## Phase 5 — delegation (parallel, start P5.1 in Phase 1)

| ID | Replace | With | Deletes / payoff |
| --- | --- | --- | --- |
| P5.1 | Hand-written schema→DTO loaders (`ContentPackLoader`, `Gameplay2DContentLoader`, sibling module loaders) | Corvus code generation from the schemas already used for validation (already a dependency) | The schema/parser drift class of bug; hundreds of loader lines. |
| P5.2 | Hand-rolled rollback and journal tests | **FsCheck** property tests: any random command sequence, then rollback, leaves the state hash unchanged | Journal correctness you cannot reach with examples. Highest-value test investment here. |
| P5.3 | `ProceduralRasterizer` (414 lines) | **SkiaSharp** (MIT) for raster and vector; **StbImageSharp** (public domain) if only PNG decode is needed. Avoid ImageSharp unless the Six Labors licence is acceptable. Golden-image tests only, never in the simulation. | 414 lines and a pixel-format test surface. |
| P5.4 | Offline navmesh baking | **DotRecast** (MIT), baked into content at authoring time; keep the deterministic runtime flow-field/A* | Fiddly, solved geometry work. |
| P5.5 | Golden save/replay fixture comparisons | **Verify** snapshot testing | Hand-rolled comparison code and opaque failures. |
| P5.6 | Bulk population save payloads | **MemoryPack**, or a raw `MemoryMarshal.AsBytes` copy since columns are unmanaged. Keep the JSON envelope, checksum and migrations. | Binary bulk writes without a second hand-rolled format. |
| P5.7 | Hand-rolled `Stack<T>` pools | **Microsoft.Extensions.ObjectPool** | Minor, free correctness. |

### Do not delegate

| Component | Why it stays |
| --- | --- |
| `DeterministicSystemSchedule` (1,612 lines) | No .NET library provides deterministic wave barriers over declared-access systems. This is the differentiator. |
| Transaction journal and rollback | No .NET ECS rolls back (`Arch`, `DefaultEcs`, `Friflo.Engine.ECS` all fail). Adopting one deletes the property replay tests depend on. |
| Population columns | Nothing off the shelf is typed, generated, double-buffered, journaled and scope-checked. |
| Expression evaluation | `DynamicExpresso`/`NCalc` allocate and use expression trees; `Jint` is far slower and drifts across versions. If kept, it is kept as-is. |
| Dense uniform grid broadphase | GIS libraries (`NetTopologySuite`) allocate and are the wrong shape for millions of uniform agents. |
| PCG32 `DrawStream` | 79 lines, correct, deterministic. `System.Random` is not portable across .NET versions. |
| Fixed-step clock | 120 lines with catch-up semantics the host depends on. |

## Risks and stop points

- **Stop after Phase 1** and re-measure. If the kernel tier is still red, the cost is in the journal
  or the scheduler, not in fact dispatch, and Phase 3 will not fix it.
- **Stop after P3.8** and decide A versus full B on evidence, not enthusiasm.
- **Do not start P3 before P1.1 lands.** Row-addressed facts multiply fact volume; batching first is
  what keeps the cost linear.
- Phase 3 is the only phase that can fail on design rather than effort. Budget 6–8 weeks for one
  senior engineer and treat P3.8 as the go/no-go.

## Out of scope

Engine adapters, rendering backends, audio, input, and any gameplay module feature work. This plan
changes how the framework stores, dispatches and rolls back state; it does not add game features.
