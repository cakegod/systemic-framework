# Systemic framework: pain points, API friction, and gaps

This document records what it was like to build Glow Garden against the framework at baseline
commit `3e1dd6a`, then tracks the fixes made during the 2026-10-27 framework overhaul. Sections
1–9 preserve the original audit and measured evidence; present-tense descriptions there refer to
that baseline unless marked otherwise. The current-status column below and sections 10–12
supersede baseline statements where implementation has changed. Baseline measurements are
historical and are not claims about the current container or a full game.

Severity means: **blocker** = the straightforward design cannot be built; **major** = it can be
built, but only with a workaround, a performance cliff, or a correctness trap; **minor** = paper
cut that costs time.

Sections 1–6 cover the original correctness, ergonomics, scaling, gaps and priorities. Section 9
is a baseline boilerplate snapshot, section 10 updates the bug outcomes, section 11 checks the
approved production-grade requirements against the current tree, and section 12 separates measured
performance from unresolved optimization proposals.

| # | Baseline finding | Severity | Current status (2026-10-27) |
| --- | --- | --- | --- |
| 1.1 | Parallel per-range population writes raced on undo capture | blocker | Fixed: world transaction journaling and page capture are synchronized; concurrency rollback tests cover shared-page and disjoint-range writes. |
| 1.2 | Undo capture could replace the real exception with a `NullReferenceException` | major | Fixed: cleanup is best-effort and preserves the original capture failure. |
| 1.3 | Grid rebuild did not identify the population or bad coordinates | major | Fixed: named rebuild diagnostics include row, coordinates and bounds; opt-in `Skip` counts invalid rows. |
| 1.4 | No deterministic random source was available to parallel systems | blocker | Fixed at API level with counter-based per-entity/tick draws; Garden uses it for scouting, while obstacle noise still uses a separate stable hash. |
| 1.5 | Structural edits could invalidate borrowed population storage | major | Partial: active population leases block structural changes; ordinary raw spans still cannot be tracked after the caller obtains them. |
| 1.6 | `RemoveSwapBack` changed row identity during parallel passes | major | Partial: handles and deferred commands help, but row indexes remain transient across structural edits. |
| 1.7 | No world seed accessor | minor | Fixed: `World.Seed` exposes the original seed independently of later draws. |
| 1.8 | Transactions did not restore plain domain-system state | major | Partial: the host snapshots registered transactional/stateful systems and Garden includes progression, IDs, scent and pending work; unregistered state and external side effects need explicit handling. |
| 2.1 | Systems used two `Step` methods and publish paths | major | Open: the dual hosting APIs remain. |
| 2.2 | Per-row column writes were verbose and repeated captures | major | Partial: generated row/range writers batch writes across columns; no lifetime-enforced multi-column edit lease exists, and Garden keeps hand-written projections. |
| 2.3 | Content properties were stringly typed and read every tick | major | Fixed for registered schemas: Garden validates tuning at load and uses a typed snapshot; ad hoc property reads remain available. |
| 2.5 | Parallel row writes were not safely supported by the column layer | blocker | Partial: disjoint row writes no longer race during transaction capture; the API does not enforce row ownership or prevent overlapping writes. |
| 2.6 | Facts carried no payload or position | major | Fixed: typed fact IDs, bounded primitive payloads and exact integer positions are available. |
| 2.7 | Cue overflow silently discarded old cues | major | Fixed at API level: configurable overflow policies, counters, expiry and a bounded presentation window are available; games still choose policy and surface counters. |
| 2.8 | Access declarations were strings and `WriteWorld` serialized the wave | minor | Partial: typed column access declarations are available and `WriteWorld` is intentionally exclusive as approved; ad hoc named resources remain string-keyed. |
| 2.9 | Hosts had to prune presentation cues by age | minor | Partial: stream expiry and a reusable cue window exist; the host must still advance/pump the window. |
| 2.10 | Public state projections duplicate generated column layout | minor | Partial: generated row readers/writers reduce access boilerplate; Garden projections remain hand-written. |
| 3.1 | Default scheduler worker cap is four | major | Open: default remains capped at four and there is no startup cap report. |
| 3.2 | No parallel reduction or ordered-merge primitive | major | Fixed: fixed logical partitions merge in partition order, independent of worker count. |
| 3.3 | No nearest-neighbour query in the spatial grid | major | Fixed at API level for floating-point and fixed-point indexes; Garden still has custom targeting paths. |
| 3.4 | Large-radius queries may scan the map | minor | Partial: nearest APIs can early-out but worst cases still visit the grid; Garden's fallback can scan broadly. |
| 3.5 | Whole-buffer undo copies remained on the safe transaction path | major | Open: rollback remains safe by default; no ordinary-column whole-buffer no-copy mode was added. |
| 3.6 | No fact aggregation for high-frequency events | blocker at scale | Fixed at API level with bounded deterministic region/type aggregation; adoption remains game-specific. |
| 3.7 | No per-system instrumentation | minor | Fixed for bounded timing history and budgets; allocation accounting only covers the calling thread. |
| 3.8 | No reusable static blocking geometry | major | Fixed at API level with static blocking fields and deterministic navigation; Garden still owns its custom terrain implementation. |

Other current findings: `WriteWorld` deliberately excludes other systems from its scheduler wave;
resource names remain strings. Garden now uses cue expiry, typed tuning, fact positions, rollback
state, a debug overlay, and exact obstacle traversal. Generic reload preparation validates a
candidate but does not replace a live host. Save integrity checksums and migrations exist, but
save schemas below v4 are rejected; explicit v4+ migrations are supported when the schema advances.
The strict determinism analyzer now checks gameplay-system implementations by default when the
analyzer is included; the existing float-based samples use explicit file-level `relaxed` settings.
Garden gameplay still uses floating-point math, so the approved cross-platform strict-determinism
contract has not been established.

---

## 1. Determinism and correctness hazards at the audit baseline

### 1.1 Parallel per-range population writes race on undo capture (blocker)

`ScheduledSystemContext.ForEachRange` (src/Systemic.Kernel/DeterministicSystemSchedule.cs:550)
hands disjoint row ranges to workers, and the project README explicitly tells system authors they
may "write disjoint population rows". The column layer does not support that. Every
`EditRange` / `EditNext` call records undo pages through
`PopulationColumnBase.CapturePageUndo` (PopulationColumn.cs:325), which pops from a shared
`Stack<PopulationPageUndoBatch>` (`:281`) with no lock. Two workers capturing pages of the same
column at the same time corrupt that stack. In Glow Garden this showed up as an intermittent
`NullReferenceException` on the first tick of a 1M-row rotation, and it took a while to trace
because of 1.2.

The workaround in the sample: claim the whole destination buffer with `EditNextMemory(0, count)`
on one thread, then give each worker a disjoint slice of that `Memory<T>`. That works, but it is
undocumented and requires knowing that `Memory<T>.Span` on disjoint slices is safe while
`EditNext` is not.

What would fix it: a first-class parallel write API, e.g.
`Population<T>.EditParallel(PopulationColumn<T> column, PopulationRange range)` that reserves
undo pages up front under a lock (or allocates one batch per page before dispatch), plus a note
in `ForEachRange` on the supported way to write. Even better: state the rule as "one capture per
column per tick, then parallel slices" and provide the helper.

### 1.2 Undo capture replaces the real exception with a `NullReferenceException` (major)

`CapturePageUndo` catches everything, calls `batch.Reset()`, pushes the batch back, and rethrows:

```csharp
catch
{
    batch.Reset();                 // batch can be null here
    _freePageUndoBatches.Push(batch);
    throw;
}
```

When the stack corruption from 1.1 causes `Pop()` to return `null`, the catch itself throws an
NRE, which replaces the original exception. The stack trace then points at line 337 with no
inner exception, and the actual failure is invisible. Two small fixes: do not recycle a batch
whose `Capture` failed, and use `catch (Exception) { ...; throw; }` that never runs code that can
throw.

### 1.3 Grid rebuild throws on a bad row without naming the population (major)

`PopulationSpatialGrid2D.ValidatePositions` (PopulationSpatialGrid2D.cs:407) throws
`ArgumentOutOfRangeException("positions", "Position at row 6 is non-finite or outside the grid
bounds.")` during `Rebuild`. A row number with no population name, no position value, and no
bounds is almost useless in a system that rebuilds three grids per tick. It also means an
out-of-bounds position is a hard crash rather than a clamp or a skipped row, so one bad move
kills the frame. The message should name the population, the row's coordinates, and the grid
bounds; and there should be a strict/lenient mode, because "ignore and continue" is the right
answer for a decorative population.

This bit us twice: a wall-following step near the garden edge pushed a worker out of bounds, and
then a three-row plant scatter used the wrong row count and put plants past `Height`.

### 1.4 One mutable draw stream, unusable from a parallel system (blocker)

`World.Draws` is a single `DrawStream` (DrawStream.cs) with `NextFloat()` mutating `_state`. There
is no documented thread safety, and even if it were locked, sharing it across workers would make
the result depend on scheduling. A parallel `DecideRange` therefore cannot draw random numbers,
which kills the most natural implementation of wandering, jitter, per-agent variation, and
spawning. The sample derives per-row noise from `(row, tick, stableId)` with a hand-written hash
instead.

What is missing: a deterministic per-row stream, e.g.
`World.Draws.ForRow(row).NextFloat()`, or a `DrawStream` snapshot per partition that is combined
in a fixed order. A documented "do not use Draws inside ForEachRange" rule would at least stop
people from silently breaking determinism.

### 1.5 Structural edits invalidate borrowed spans, with no guard (major)

`ReadOnlySpan`/`Span` views from columns are borrowed (the doc comments say so). `AddRange`,
`RemoveSwapBack`, or `Reserve` while a span is alive invalidates it, and the failure mode is
reading garbage or an `IndexOutOfRangeException` far from the cause. There is no generation
check, no `IsValid`, no debug assert. Glow Garden collects motes into a tombstone (`Value = 0`)
and compacts once per tick because adding a mote during the worker pass would invalidate the
mote positions span that every worker reads. That pattern is not in the docs and every sample
author will reinvent it.

A `Population<T>.BeginStructuralEdit()` scope that throws if a span is outstanding, or a
`Population<T>.AppendDeferred(...)` that queues spawns until the end of the step, would remove a
whole class of bugs.

### 1.6 `RemoveSwapBack` invalidates row identity used by parallel passes (major)

Structural removal is swap-back, so a row index is only stable until the next removal.
`PopulationHandle<T>` exists and works, but most column APIs are row-indexed. In a scheduled
system the parallel pass holds row indices from the start of the tick; any serial removal (copy
expiry, in the sample) renumbers rows underneath the arrays that were written during the pass.
The sample is careful to only remove before the parallel pass, but nothing enforces it, and the
symptom is a subtly wrong decision rather than a crash.

The docs should state the ordering rule explicitly, or the scheduler could provide a
`DeferStructuralChanges` phase.

### 1.7 No world seed accessor (minor)

`World` exposes `Draws` but no `Seed`. To generate deterministic terrain that must not consume
draws, the sample reads `world.Draws.CaptureState().State` and uses it as a noise seed. That is
the *current* state, not the seed, so the terrain changes if anything draws before construction.
A `World.Seed` (or a dedicated `World.StructuralSeed`) would make generation order-independent.

### 1.8 Rollback stops at populations and world data (major)

`World.BeginTransaction` + population undo works, and `ISystemicStatefulExtension` gives
extensions a `CaptureState`/`RestoreState` pair. But a domain system's derived state (trails,
cooldowns held in plain arrays, id counters, progression) is outside that contract;
`SystemicScheduledContext.OnRollback` is the manual escape hatch. The sample documents that it
does not support save/rollback at all, which is honest but also means the framework's strongest
feature cannot be demonstrated by its own game sample.

What would help: a per-system `ISystemicStatefulExtension` registration that the host snapshots
automatically at step boundaries, and a documented list of what is *not* rolled back (facts,
cue streams, presentation identity).

---

## 2. API ergonomics at the audit baseline

### 2.1 Two `Step` methods, two contexts, two publish paths (major)

`ISystemicSystem.Step(SystemicStepContext)` and `ISystemicScheduledSystem.Step(
ScheduledSystemContext, SystemicScheduledContext)` both exist, with different `Publish` routes.
A system that supports both (the sample does, because the tests and the legacy host path use the
unscheduled one) ends up with a mutable `Action<SimulationFact> _publish` field assigned at the
top of each `Step`, and a `ExecuteCore(tick, kernel: null)` that branches on which context it
has. This is plumbing every system author will write.

A single adapter — e.g. `ISystemicSystem` gains a `Step(SystemicContext)` with the scheduled
system wrapped during registration — would remove the duplication. At minimum, the sample-grade
pattern should be in the docs.

### 2.2 Per-row column writes are verbose and expensive (major)

Getting a row out is fine (`PlantStates` projects columns into a `record struct`). Setting one is:

```csharp
Columns.StableId.EditRange(index, 1)[0] = value.Id;
Columns.Position.EditRange(index, 1)[0] = value.Position;
Columns.Growth.EditRange(index, 1)[0] = value.Growth;
```

Each `EditRange` call captures undo pages again, so a 7-column row costs 7 capture attempts and
7 span constructions. There is no `Set(row, value)` or bulk setter, and the generated `Columns`
type does not generate row accessors. For a 1M-row population, per-row writes through this
surface are not viable at all.

Suggested: generated `column[row]` indexers or a `WriteRow(int row, in TState value)` helper, and
a batch write that captures pages once for the whole range.

### 2.3 Content properties are stringly typed and re-read every tick (major)

`entities.json` properties are read with `Param("garden.worker.scoutRadius", 110)` — a string
lookup on a dictionary, per parameter, per tick (the sample reads five per tick). Typos silently
fall back to the default; there is no schema for sample-specific property names, so a misspelled
`garden.worker.patience` just changes behaviour with no warning. There is also no range
validation at load; every read clamps at use, which means an authoring error is invisible until
someone plays long enough to notice.

A content schema module per game (declared property names, types, ranges, defaults) with
load-time validation and a typed accessor would pay for itself immediately.

### 2.5 `ForEachRange` promises parallel row writes the column layer cannot support (blocker)

See 1.1. This is both a correctness hazard and an API trust problem: the one API whose whole
purpose is parallel work points at an unsafe operation.

### 2.6 Facts carry no payload and no position (major)

`SimulationFact` (src/Systemic.Core/Rules/RuleEngine.cs:6) is
`(string Type, int SourceId, int TargetId, int Depth, double Amount)`. There is no place for
structured data (which pod, which talent, which region), and no position. The sample solves the
position problem by moving a hidden `garden.impact` entity's spatial bounds before publishing, so
the presentation module can capture where the event happened. That is a clever hack and a clear
sign the type is missing a field.

Suggested: `SimulationFact` with an optional `ReadOnlyMemory<byte>` payload or a generic
`SimulationFact<T>`, plus `Position` as a first-class field (or a documented "anchor entity"
concept, since the entity exists for exactly this).

### 2.7 The cue stream drops the oldest cue silently (major)

`PresentationCueStream` is bounded at 1024 by default, 65,536 max, and on overflow drops the
oldest cue and increments `DroppedCount`. There is no callback, no per-fact-type policy, and no
way to say "coalesce" or "sample". A game that emits one cue per pickup will silently lose
effects under load, and the only signal is a counter nobody reads. At a million agents, cues must
be aggregated before they reach the stream — but the module offers no aggregation hook.

### 2.8 Access declarations are strings, and `WriteWorld` is a global lock (minor)

`SystemAccess("garden.mote.position", SystemAccessMode.Write)` uses strings that must match
generated constants; a typo means the scheduler cannot see the dependency and may run two systems
concurrently over the same column. `SystemAccess.WriteWorld` serialises the whole wave, which is
the only way to publish immediately, so any system that emits facts is effectively single-threaded.
A capability-based `WriteWorld` (e.g. "may dispatch rules, but declared populations still
parallelise") would be a large design change, but the current rule is blunt.

### 2.9 Presentation timelines require the host to prune cues (minor)

`PresentationTimelineEvaluator.TrySample` returns false for a non-looping timeline once
`ageTicks >= DurationTicks`; the stream itself never expires cues. Every host has to keep a cue
list and prune by age (the sample uses 300 ticks) or the list grows until the cue cap. A
`PresentationCueStream.MaxAge` or an expiry on `PresentationTimeline` would centralise it.

### 2.10 State projections duplicate column layout (minor)

`CreatureState`, `PlantState`, `MoteState` are hand-written `record struct`s that mirror the
generated columns, and the projection indexer reads one span per field
(`Columns.X.ReadOnlySpan[index]`). It is allocation-free but verbose, and adding a column means
editing the record, the getter, and the setter. Generated `TState` projections (or a
`Population<T>.ToStruct(row)` generator) would remove the duplication.

---

## 3. Performance and scaling at the audit baseline

### 3.1 Default scheduler worker cap is four (major)

`DeterministicSystemScheduleBuilder` defaults to `Math.Clamp(Environment.ProcessorCount, 1, 4)`
(DeterministicSystemSchedule.cs:71), justified as "common low-power development hardware". On a
32-core workstation this leaves most of the machine idle and silently caps every scheduled
system; changing it requires an explicit `WithScheduledWorkerCount` call that no sample makes by
default. A
one-line log at startup ("running scheduled systems with 4 of 32 workers") would at least make it
visible.

### 3.2 No parallel reduction or ordered-merge primitive (major)

`ForEachRange` gives you partitioned work but nothing to combine results. Every parallel pass
ends with a serial loop (the sample's `ApplyDecisions` and `ResolveWorkerIntents`), which is
where ordering-dependent behaviour lives: first-come mote collection, stable fact order, and
burst resolution. That serial tail is a hard ceiling — at 50k workers it is a large fraction of
the tick. A deterministic reduce (`context.Reduce(seed, partitionValue, combine)`) with a fixed
partition order would let systems keep parallelism through the resolution step.

### 3.3 The spatial grid has no nearest-neighbour query (major)

`PopulationSpatialGrid2D` offers `QueryRadius`, `QueryRadiusCandidates`, `QueryBounds`, and cell
neighbourhood enumeration — no nearest, no k-nearest, no sorted-by-distance. Every "find the
closest X" is hand-written as an expanding-radius loop (the sample's `NearestRipe` / `NearestMote`
do exactly that), which is easy to get wrong (the proof that "a hit inside the radius is the
true nearest" is not obvious) and cannot early-out per row beyond the radius.

Suggested: `bool TryFindNearest(Vector2 point, float maxRadius, out int row)` and
`int FindKNearest(Vector2 point, float maxRadius, Span<int> rows)` implemented with the grid's
own cell walk, plus a version that takes a predicate/column filter. This is the single highest
value addition for agent games.

### 3.4 Large-radius queries scan the map (minor)

`QueryRadius(point, maxRadius, results)` with a map-sized radius walks every cell. That is what
the "commit to a global target" fallback does, once per idle worker per tick. It is acceptable at
1000 workers, but it is the only way to ask the question.

### 3.5 Double-buffered undo snapshots the whole buffer every tick (major)

`DoubleBufferedPopulationColumn<T>.EditBothBuffers` / `EditNext` capture undo pages, and when the
edit covers the whole population the capture is a full-buffer copy
(`RecordPopulationBuffer`). For the 1M-row ambient experiment that was ~8 MB of copy per tick —
roughly half the tier's 3 ms cost — for state that is decorative and could be recomputed. There is
no way to declare a column "derived, not undoable", and no way to snapshot every N ticks. A
`[PopulationColumn(..., Undo = UndoMode.None)]` or `World.BeginTransaction(TransactionOptions.
SkipPopulations(...))` would let authors make that trade knowingly.

### 3.6 No fact aggregation, so high-frequency events cannot be represented (blocker at scale)

One fact per pickup/burst is fine at 32 workers and impossible at a million. The rule engine
dispatches per fact, the presentation stream is a bounded FIFO, and there is no
"per-region summary" fact type. Any design where many agents produce events (harvesting, combat,
crowds) has to invent its own aggregation and its own pseudo-facts, losing the authored rule
selection that makes the framework interesting.

### 3.7 No instrumentation (minor)

There is no per-system tick timing, no allocation counter, no queue-depth metric. The sample
measured everything with `Stopwatch` around `Step`, and the benchmarks project has its own gate
runner. A `SystemicGame.TickStats` (per system, last/mean/p95, allocations) would turn "the game
feels slow" into a number without writing a harness.

### 3.8 No static-geometry index (major)

Blocking volumes, triggers, spawn areas, and walkability are all things every game needs and the
kernel has no representation for. Glow Garden generates an 8 px walkability field, a bilinear
query, a flood fill that guarantees connectivity, wall-following steering, and an escape hatch —
all in sample code, none reusable. A `StaticField2D` / `BlockingGrid` with connectivity
validation, a walkable query, and a flow/path helper would cover a large class of games.

---

## 4. What was missing at the audit baseline (must-have)

1. **Navigation and avoidance.** The framework can answer "what is near" but not "how do I get
   there". Even a coarse-grid A* or flow field would let agents path around obstacles; the
   sample's hand-rolled wall-following shows the gap. Without it, every non-trivial agent game
   reimplements the bug algorithm, badly.
2. **Blocking/collision primitives.** A registered static field, walkable queries, and a
   connectivity guarantee (no sealed pockets) should be kernel-level, because determinism and
   save state both depend on it.
3. **Nearest-neighbour queries** (see 3.3).
4. **Per-row deterministic randomness** (see 1.4).
5. **Fact payloads and aggregate facts** (see 2.6, 3.6).
6. **A full save contract.** `CapturePopulationState` covers populations; the clock, tick, draw
   stream, extensions, rule counters and pending domain state are assembled by hand, and the
   sample simply does not save. A `SystemicGame.CaptureState()` that composes every registered
   part (with a version) would make the headline feature usable.
7. **Content schema for game-specific data.** Per-module property declarations with types,
   ranges and defaults, validated at load (see 2.3).
8. **Camera/viewport in Presentation.** The world grew 4× and the sample hardcodes
   `Scale`/`Origin` in the renderer. A `PresentationViewport` (world bounds → screen rect, with
   culling) belongs with the visuals, not in each sample.

## 5. Nice to have at the audit baseline

- Parallel reduce / ordered merge (3.2).
- Generated row accessors and `TState` projections (2.2, 2.10).
- Debug overlay: draw the spatial grid, fields, and access waves; a `--debug-grid` switch in the
  sample would have saved a lot of print debugging.
- Deterministic replay files and a "diff two runs" assertion helper; determinism tests today are
  "build two hosts and compare arrays".
- Cue aggregation policy per fact type (2.7).
- Cue texture eviction: `GardenGame` caches a texture per (recipe, state) and never releases them.
- Hot reload of content for iteration.
- `World.Seed` (1.7).
- A documented, tested answer to "may I call `Publish` from `ForEachRange`?" (currently no).
- Event/command vocabulary validation: `Publish("garden.glow.collected")` is a magic string that
  no compiler checks; a generated event constant per content rule would catch typos.

## 6. Prioritised recommendations at the audit baseline

| Priority | Change | Why |
| --- | --- | --- |
| 1 | Safe parallel column writes (`EditParallel` or one-capture-then-slices) + docs | 1.1 is a latent crash in the flagship parallel API |
| 2 | `TryFindNearest` / k-nearest in `PopulationSpatialGrid2D` | every agent game needs it, all of them hand-roll it |
| 3 | Per-row deterministic RNG | parallel systems currently cannot use randomness |
| 4 | Static blocking field with connectivity guarantee + simple pathing | enables a whole genre of samples without per-sample geometry code |
| 5 | Fact payloads + aggregate facts + cue aggregation | required for anything above ~10k events/tick |
| 6 | `SystemicGame.CaptureState()` covering clock, draws and extensions | the sample cannot demonstrate save/load |
| 7 | Content schema + typed properties with load-time validation | removes silent typo bugs and per-tick string lookups |
| 8 | Undo opt-out for derived populations | 1M-row tier pays ~50% of its cost for undo it never needs |
| 9 | Per-system tick stats | makes "is it fast enough" a measurement instead of a harness |
| 10 | Worker-cap visibility and a higher default on many-core hosts | silent 4× underuse |

## 7. What worked well at the audit baseline

Worth keeping in the same document, because the list above can read as one-sided:

- Declared-access scheduling genuinely works: a system that runs in the scheduled phase is
  measurably faster than the legacy serial path at the same load, and declaring which populations
  it writes is a short, explicit list rather than a convention.
- Population handles (`GetHandle`/`TryResolve`) are the right primitive for stable identity under
  swap-back removal.
- The generated column types and codecs are pleasant to work with; adding a column is one
  attribute and a rebuild.
- `World` transactions plus population undo are strong enough to test rollback properly, and the
  sample's rollback tests are short and convincing.
- The content/rules split (authored rules select, the domain applies) held up when adding new
  talent behaviour: the counter, the selection, and the effect stayed separable.
- The spatial grid's `QueryRadius` with a reused `List<int>` is allocation-free and fast; the
  problem is only the missing nearest-neighbour and static-geometry layers on top.

## 8. Historical evidence from Glow Garden

These measurements were collected during the original sample work, before the current overhaul.
The machine, commit, and exact benchmark protocol were not recorded with the figures, so treat
them as directional historical observations rather than reproducible benchmark results.

The original notes described these as Release, single-process measurements, but did not retain the
machine, commit, or full test protocol. They are concrete historical observations rather than
reproducible evidence for the current tree:

- Linear nearest-target scans: 7,611 ms/tick at 50k workers / 1k plants / 12k motes.
- Same load with grid targeting: ~15 ms/tick on four scheduled workers; ~55 ms in the legacy
  serial path.
- 1,000,000 passive rows rotating: ~2.9 ms/tick, including the full-buffer undo snapshot that
  every tick pays for even though the state is derived and could be recomputed.
- 50k workers with Magnetism on: >1 s/tick, because every mote queries the creature grid.
- The parallel `EditNext` race produced an NRE at `PopulationColumn.cs:337` on tick 1 of the
  million-row case; the same code path is what 1.1 describes.
- Procedural thickets: 14.2% coverage, zero unreachable free cells after the connectivity pass,
  zero workers inside a blocked cell over 3,600 ticks, minimum path length 702 px per 300 ticks
  once wall-following and the stall escape were added (before that, individual workers sat still
  for 450+ ticks).

---

## 9. Boilerplate hotspots at the audit baseline

All line counts, touchpoint counts, and rankings in this section describe the baseline sample
snapshot. They have not been recounted against the current, expanded Garden implementation.

Ranked by how much of the sample is plumbing rather than game logic. Glow Garden is 2,154 lines
across eleven files; the interesting simulation logic is a small fraction of that.

| Rank | Area | What repeats | Where | Size |
| --- | --- | --- | --- | --- |
| 1 | Population row projection and writes | One span read per field, one `EditRange(row, 1)[0]` write per field, a mirrored `record struct` | `GardenPopulationStates.cs` (121 lines for three kinds) | 29 `EditRange` calls in the sample; adding a column touches 7 places |
| 2 | Parallel decision plumbing | A read-only span, a decision array, a local, and an apply loop per column | `GardenSimulation.DecideRange` / `ApplyDecisions` | 7 decision arrays, 22 `_decided*` references, ~145-line decide method |
| 3 | Event and talent wiring | Enum member, tag string, fact string, tag switch, fact switch, intent switch, content properties, rule, UI name/description/key, tests | `GardenTalents.cs`, `entities.json`, `rules.json`, `GardenUi.cs`, `GardenGame.cs`, `GardenTests.cs` | 9 touchpoints; 6 intent cases; 42 `"garden."` string literals across two files |
| 4 | Dual system hosting | `_publish` delegate field, `ExecuteCore(tick, kernel?)`, two `Step` overloads, null checks in every method that talks to the host | `GardenSimulation` | ~30 lines and a permanent "which context am I in" branch |
| 5 | Content parameter reads | Default + clamp repeated at every use site | `GardenSimulation` (8), `GardenTalents` (12) | 20 read sites, no load-time validation |
| 6 | Spatial and field infrastructure | Noise sampling, bilinear interpolation, gradient, flood fill, steering, escape | `GardenObstacles.cs` (355 lines), `GardenScent.cs` (86 lines) | 441 lines that every sample with terrain or trails will rewrite |
| 7 | Rendering | Per-entity draw block (shadow, body, eyes, carried dots), terrain bake, sprite cache, world-to-screen constants | `GardenGame.cs` (298 lines) | ~90 lines of draw code that scale and viewport changes force you to revisit |
| 8 | Test harnesses | `Arrange()` poking internals, two-host determinism comparison, throwaway measurement tests | `GardenTests.cs`, `FrameworkIntegrationTests.cs` | 4 scratch harnesses written and deleted in this session alone |
| 9 | Content authoring | The same tuning value exists in `entities.json`, a code default, and the README table | `entities.json` + `Param(...)` calls + README | every new knob is authored three times |

### 9.1 Adding one column to one population (7 edits)

1. `[PopulationColumn("heading")] public float Heading;` in the schema.
2. The public `record struct` field.
3. The projection getter (`Columns.Heading.ReadOnlySpan[index]`).
4. The projection setter (`Columns.Heading.EditRange(index, 1)[0] = ...`).
5. A local span in `DecideRange`.
6. A `_decidedHeadings` array plus its per-branch assignment.
7. A write in `ApplyDecisions`.

None of these can be derived from the schema, and forgetting any one is a silent default rather
than a compile error for the public struct (fields are optional positional parameters).

### 9.2 Adding one talent (9 edits)

`Talent` enum member, `TalentTag` switch, `ObserveFact` fact-type switch, `DrainTalentIntents`
case, `entities.json` properties (parameters plus a `uses` counter plus the tag), `rules.json`
entry, UI label array, UI description array, key binding, and two test theories. Three of the
wiring points are string-keyed in different files, so a rename is a grep exercise.

### 9.3 What would cut the boilerplate

- Generated row accessors and generated `TState` projections from the schema (kills rank 1).
- A `Population<T>.EditRows(range)` batch that captures pages once and returns a writable span
  per column (kills most of rank 2 and all the per-row undo churn).
- First-class events: a `[GameEvent]`-declared constant/literal type plus a rule-to-handler
  registration, instead of three parallel switches keyed by strings (rank 3).
- One system hosting model (rank 4).
- Typed content bindings (`readonly record struct WorkerTuning` resolved once at load) (rank 5).
- Kernel-level field and walkability types (rank 6).

---

## 10. Bugs found while building the sample

The symptoms and original causes below are historical observations from the baseline sample.
"Status" records the current code and whether the original finding was verified or corrected.

| ID | Historical symptom | Root cause | Blame | Current status |
| --- | --- | --- | --- | --- |
| B1 | `NullReferenceException` during the first tick of a 1M-row rotation | Concurrent `EditNext` undo capture touched shared page-journal state | Framework (1.1) | Fixed by synchronized transaction journaling and page capture; concurrent transaction regression tests cover the path. |
| B2 | The NRE hid the original failure and pointed at the catch block | Undo cleanup could throw while handling a failed page capture | Framework | Fixed: cleanup preserves the original exception and does not recycle a failed batch. |
| B3 | An invalid row produced a generic bounds exception during a system step | Grid validation omitted the population ID, coordinate and bounds; no skip policy existed | Framework robustness (1.3) | Fixed: named diagnostics identify the offending row and bounds; opt-in skip mode counts omitted rows. |
| B4 | Plants spawned past the bottom of the map after it grew | Three-row scatter still divided height by two | Sample | Fixed in the sample. |
| B5 | Worker positions left the map after wall-following | Steering could step outward at the border | Sample | Fixed in the sample; movement is bounded before population writes. |
| B6 | Workers oscillated in place | Gradient-derived wall tangent flipped across a corridor | Sample | Fixed in the sample by steering relative to the desired direction. |
| B7 | Workers left a wall then walked back into it | Fixed-step path probes could miss thin thickets | Sample | Fixed: obstacle traversal checks every crossed cell, including corner crossings; focused traversal tests pass. |
| B8 | Workers wedged in a corridor that appeared open | Bilinear movement checks disagreed with cell-carved corridors | Sample plus missing reusable geometry | Fixed in the sample with cell-exact collision and escape; reusable static blocking and deterministic navigation APIs now exist. |
| B9 | Seeds and starting pods could sit inside a thicket | Placement accepted one unchecked random position | Sample | Fixed with bounded valid-position search and counted rejection when no free location exists. |
| B10 | Gatherers wandered while fresh food remained | The historical explanation was low scent at the colony end and recruitment range | Sample tuning | Tuning and trail stamping were changed, but the original causal claim was not independently reproduced; treat it as a plausible explanation, not an established root cause. |
| B11 | Shipped worker cap disagreed with README and UI | A temporary stress-test value remained in sample settings | Sample | Fixed; shipped cap matches the authored documentation. |
| B12 | Burst totals changed after targeting switched from scans to grid queries | The new traversal selected targets in a different deterministic order | Sample algorithm change | Expected behavior change, not evidence of run-to-run nondeterminism. Query APIs document their ordering; changing algorithms can change gameplay choices. |

The baseline framework defects were B1–B3; each now has a regression test and an implementation
fix. B4–B11 were sample-side issues or tuning changes. B10's causal explanation remains
unverified. The baseline test suite did not cover B1–B3; the current suite includes regression
coverage for transaction capture, diagnostic row validation, and skip accounting.

---

## 11. Current production-grade status (checked 2026-10-27)

"Production grade" here means deterministic and replayable, recoverable from bad data, observable
when slow, versioned when saved, and safe to hand to content authors. The overhaul closes several
baseline gaps, but the strict cross-platform determinism requirement remains open. The table is
about implemented code and current evidence; configured CI is not the same as a successful hosted
CI run.

### 11.1 Determinism contract

Implemented: versioned counter-based `DeterministicRandom` values address draws by seed, system,
entity ID, tick and draw index; ordered `ReduceRanges` uses fixed logical partitions independent of
worker count; replay helpers hash caller-supplied canonical state; CI is configured for Linux x64,
Windows x64 and macOS ARM64.

Implemented: when included in a project, `StrictDeterminismAnalyzer` checks implementations of
`ISystemicSystem`, `ISystemicScheduledSystem` and `IScheduledKernelSystem` by default. Explicit
`[StrictSimulation]` and `systemic_determinism = strict` settings enable checks in other code;
`systemic_determinism = relaxed` disables only the gameplay-system default for legacy files, while
`[CosmeticOnly]` excludes presentation code. Glow Garden and Boids currently declare that relaxed
setting because their gameplay still uses floating-point state.

Still open: the analyzer recognizes selected unsafe patterns; it is a guardrail, not a proof of
determinism. It traces common inline LINQ pipelines, local initializers, preceding local
assignments and C# query sources, but does not prove arbitrary data flow or aliasing. Sorting an
unordered source is still reported because tied keys can preserve the source's unstable order.
Glow Garden gameplay still uses
`float`, `Vector2` and `MathF`, and does not use Q32.32 for authoritative decisions.
The checked-in replay test is a small counter-system fixture, not a Garden replay, and does not
compare Garden under different worker counts. The configured platform matrix has not been verified
by a hosted CI result in this workspace. Therefore bitwise-identical gameplay across Linux x64,
Windows x64 and macOS ARM64 is **not established**. Floating-point math also remains suitable for
visual/gameplay prototyping, but it must not be described as cross-platform deterministic.

### 11.2 Failure containment

`SystemicGame.TryStep` returns a structured failure with system ID and tick while the host's
transaction path restores registered system and population state. Commit-callback failures are
reported as possibly post-commit. Spatial-grid `Throw` diagnostics now identify population, row,
coordinate and bounds; opt-in `InvalidPositionPolicy.Skip` preserves row indexes and counts invalid
rows. The default remains strict and throws for invalid authoritative data by design. This is an
exception boundary for callers to handle, not a policy that silently continues every failed tick.

### 11.3 Observability

`SystemicTickStats` records bounded per-system count, last/mean/p95/max elapsed time, current-thread
managed allocations and budget-exceeded count. Worker-thread allocations are not included, and a
budget records an overrun rather than automatically slowing or degrading the game. Garden's F3
overlay shows sampled timings, current-thread allocation totals, scheduler waves and worker count,
plus a bounded walkability/scent view. Cue overflow/expiry and rejected spawn counters exist, but
there is no common structured logging pipeline or automatic display of every domain counter.

### 11.4 Save, load and migration

`SystemicSaveCodec` composes world/entity/population data, the draw state and registered stateful
extensions; the host adds clock and rule-history extensions, while Garden registers its domain
state. Saves are versioned, bound to a content fingerprint, checksummed and can be written through
a flushed temporary file followed by atomic replacement. Schema versions below v4 are rejected;
explicit sequential migrations are supported from v4 onward when the schema advances (the current
schema is v4, so no earlier supported migration step exists yet). Garden supports F5 save and F9
load, and its progression commands register rollback actions while a world transaction is active.
Presentation cue queues and
arbitrary external side effects are not a general durable-state contract; each module must decide
what is part of a save or rollback.

### 11.5 Content pipeline

Registered `ContentTuningSchema` definitions validate types, ranges, required tags and defaults at
load, with file, JSON path and source location diagnostics. The loader validates and fingerprints
the same entity bytes, and typed immutable tuning documents are available to modules. Garden
registers a schema and resolves a typed `GardenTuning` snapshot once rather than performing string
lookups in its simulation loop. This is opt-in per module; unschematized properties remain possible.
`ContentReload.Prepare` compiles and validates a reload candidate without mutating the active host.
It does not migrate live domain state or atomically replace a running game, so it is not live hot
reload.

### 11.6 Concurrency safety

World transaction journaling serializes page capture and preserves disjoint parallel row writes;
`WriteWorld` excludes all other systems from its schedule wave. Range reduction merges in a fixed
order. A population lease prevents structural changes while held, and deferred commands are
available. The API still cannot prove that callers only write their assigned rows, and ordinary
raw spans can outlive the lease or be retained after the caller has stopped respecting its borrow
contract. Column declarations have typed helpers; ad hoc named resources remain string-keyed.

### 11.7 Tooling and CI

Replay/hash infrastructure, a platform-matrix workflow, Garden headless/render smoke modes,
transaction and spatial regression tests, and a bounded million-row movement benchmark now exist.
The replay fixture is not Garden, the workflow has no result in this local checkout, render smoke
checks only basic rendered output rather than visual correctness, and the benchmark is a narrow
CPU movement kernel. There is no full-game cross-platform replay assertion, golden screenshot
suite, or allocation budget covering worker threads.

### 11.8 API stability and remaining ergonomics

Still open: there is no semantic-versioning policy, event IDs and access declarations still expose
strings, there is no generated domain-event vocabulary, and the separate scheduled/unscheduled
system hosting APIs remain. Generated row/range writers reduce repeated writes, but callers do not
get a lifetime-enforced multi-column edit lease or an automatically generated public `TState`
projection; Garden still maintains its hand-written projections. These are API design gaps, not
evidence that the fixed transaction or tuning paths are unsafe.

### 11.9 Definition of done

| Requirement | Current status |
| --- | --- |
| Bitwise deterministic Garden replay, checked in CI across the approved platforms | Open: projects must include the analyzer; recognized system implementations are strict by default once included. Garden gameplay remains floating-point, and CI fixture is not Garden. |
| Randomness available to parallel systems without schedule-dependent draws | Implemented at API level; Garden scouting uses stable IDs and ticks. |
| Fixed-order reduction independent of worker count | Implemented; caller map/merge math must still be deterministic. |
| Invalid spatial rows identify the population and can be counted/skipped | Implemented through strict named diagnostics and opt-in skip policy. |
| Full sample save/load with version, fingerprint and integrity check | Implemented for Garden state, world and registered extensions; module-specific cue policy remains separate. |
| Content schema validated with file/path/location diagnostics | Implemented for registered schemas; not every game property is required to opt in. |
| Parallel population writes safe for disjoint ranges | Partial: synchronized transaction capture supports disjoint writes; overlapping writes and row ownership are not runtime-enforced. |
| Per-system timing, bounded history and runtime display | Implemented; worker-thread allocations are excluded and budgets only count overruns. |
| Debug view for scheduler and spatial/field state | Partial: Garden F3 shows waves, timings, walkability and scent, not every engine counter. |
| Live content hot reload | Partial: candidate preparation/validation exists; host swap and state migration do not. |
| Flagship sample demonstrates save, replay and profiling | Partial: Garden demonstrates save and profiling; cross-platform Garden replay is missing. |
| Million-agent full-game performance claim | Not established. The available benchmark measures a narrow one-million-row movement loop only. |


---

## 12. Performance: evidence and remaining tradeoffs

New diagnostics and benchmark fixtures make some costs measurable, but they do not establish
full-game throughput. Keep workload, safety mode, machine and sampling method attached to every
number. The old figures in section 8 are historical observations with incomplete environment
metadata; they are useful as motivation, not as current benchmark evidence.

### 12.1 Reproducible benchmark observation

The checked-in million-agent benchmark measures one narrow CPU loop: one million rows with four
32-bit integer columns, single-threaded position integration, no spatial index, rules, entities,
renderer, scheduler or GPU. Each reported mode ran 20 warm-up and 100 measured ticks on the shared
container (AMD EPYC 9V74, Debian 13, .NET 10.0.14 x64, four CPUs of cgroup quota). The `safe` mode
wraps each tick in a world transaction; `optimized` deliberately runs without rollback. Exact
workload and limitations are documented in `docs/million-agent-benchmark.md`.

| Mode | p50 | p95 | Maximum | Process allocations |
| --- | ---: | ---: | ---: | ---: |
| Safe, transactional | 1.444 ms | 5.515 ms | 23.660 ms | 0 B |
| Optimized, no rollback | 0.881 ms | 1.278 ms | 18.444 ms | 0 B |

Both runs validated all rows and produced the same final hash. These values are observations from
one shared virtualized machine, not a guarantee for a full game or the historical Core i5 target.
The optimized mode is explicitly unsafe on a mid-tick failure because it may leave partial state.

The solution-wide Release run also exercised the existing generic performance gate with 50,000
products and 1,000 reactive actors. It measured p50 9.148 ms, p95 15.321 ms and p99 22.599 ms
with 0 B reported allocations, against a p95 target below 5 ms. That gate failed on this shared
container; the other 189 Integration tests passed. Do not lower the gate based on this result, and
do not treat it as a measurement of the historical laptop target.

### 12.2 Costs that remain visible in the current code

| Area | Current behavior and evidence |
| --- | --- |
| Safe rollback | The one-million-row example measures 5.515 ms p95 with transactions and 1.278 ms without. The gap is evidence of this workload's rollback cost; it does not isolate every undo copy or predict other systems. |
| Garden targeting | Garden rebuilds its population grids each tick and still has game-specific target selection. Floating and fixed-point nearest/k-nearest APIs and an incremental grid are available for other implementations. |
| Garden resolution | Worker decisions run over disjoint scheduled ranges, then ordered bursts, pickups and deposits resolve serially to preserve first-come outcomes and fact order. No Garden benchmark isolates this tail. |
| Dense talents | Magnetism work scales with motes and nearby creature candidates. The old claim of over one second at 50k workers is historical and was not reproduced with a recorded protocol. |
| Scheduler | The default cap is four workers. `WriteWorld` excludes other systems from the wave by design; this is a deliberate synchronization contract with a possible throughput cost. |
| Metrics | Runtime timing includes bounded mean/p95/max and budgets, but managed allocation totals cover the calling thread only. The Garden overlay samples rather than instrumenting every row operation. |
| Math and data kernels | Fixed-point/decimal primitives and deterministic integer SIMD kernels exist, but Garden gameplay still uses floating-point `Vector2`/`MathF`. The SIMD kernels are not general generated population-column operations. |
| Compute | `IPopulationComputeBackend` and a CPU reference backend define a backend-neutral contract. No GPU backend or end-to-end GPU performance result is included. |
| Rendering | The finite render smoke checks output diversity and can capture a screenshot. No draw-call or display-frame benchmark was recorded, so the old per-worker draw-count estimate is not retained as a measured result. |

### 12.3 Open optimization work

The next performance changes should start from a workload-specific profile. The following remain candidates rather than completed capabilities:

- Add a transaction-safe whole-buffer rewrite path or an explicit undo policy for derived columns. Keep rollback safety as the default; any no-rollback path must name its failure semantics.
- Add generated multi-column range editing that validates and captures once while retaining row-range ownership rules.
- Evaluate whether Garden should use the incremental spatial index instead of rebuilding its indexes every tick; account for movement cost and row identity before changing it.
- Measure the serial Garden resolution phase and dense Magnetism with a reproducible harness before changing ordering or aggregation semantics.
- Make the scheduler's four-worker default visible and compare alternatives on the supported hardware matrix before changing the default.
- Integrate fixed-point math into authoritative Garden decisions only with a replay fixture that checks behavior, not just arithmetic primitives.
- Measure renderer submission on actual display backends before proposing instancing or a render-thread split.
- Keep the CPU compute bridge as the reference; add another backend only with parity tests, cancellation/partial-write tests and evidence on an actual target device.

A 60 Hz, one-million-agent budget of 16.6 ms per tick is a design target, not a measured framework result. It needs separate budgets for simulation, rules, presentation and rendering, and an end-to-end workload on the intended hardware before it can be claimed.
