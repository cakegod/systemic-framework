# Systemic framework: pain points, API friction, and gaps

This document records what it is actually like to build a game on Systemic, written after
building the Glow Garden sample through several rounds of real work: grid-based nearest-target
queries, converting a system to the parallel scheduler, a 1,000,000-row population experiment,
pheromone-trail foraging, and procedurally generated blocking terrain. Every item below was hit
in that work or found by reading the kernel, core, and presentation sources while fixing it.

Severity means: **blocker** = the straightforward design cannot be built; **major** = it can be
built, but only with a workaround, a performance cliff, or a correctness trap; **minor** = paper
cut that costs time.

Sections 1–6 cover correctness, ergonomics, scaling, gaps and priorities. Section 9 maps where
boilerplate concentrates, section 10 lists every bug found while building the sample, section 11
is a production-grade gap analysis, and section 12 documents the performance limits with concrete
proposals for extracting the best possible throughput.

| # | Pain point | Severity |
| --- | --- | --- |
| 1.1 | Parallel per-range population writes race on undo capture | blocker |
| 1.2 | Undo capture replaces the real exception with a `NullReferenceException` | major |
| 1.3 | Grid rebuild throws on a bad row without naming the population | major |
| 1.4 | One mutable draw stream, unusable from a parallel system | blocker |
| 1.5 | Structural edits invalidate borrowed spans with no guard | major |
| 1.6 | `RemoveSwapBack` invalidates row identity used by parallel passes | major |
| 2.1 | Systems must implement two `Step` methods and two publish paths | major |
| 2.2 | Per-row column writes are verbose and do one undo capture per column | major |
| 2.3 | Content properties are stringly typed and read every tick | major |
| 2.5 | `ForEachRange` documents parallel row writes the columns cannot support | blocker |
| 2.6 | Facts carry no payload and no position | major |
| 2.7 | Cue stream drops the oldest cue silently | major |
| 3.1 | Default scheduler worker cap is four | major |
| 3.2 | No parallel reduction or ordered-merge primitive | major |
| 3.3 | No nearest-neighbour query in the spatial grid | major |
| 3.5 | Double-buffered undo snapshots the whole buffer every tick | major |
| 3.6 | No fact aggregation, so high-frequency events cannot be represented | blocker at scale |
| 3.8 | No static-geometry index for blocking volumes | major |

---

## 1. Determinism and correctness hazards

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

## 2. API ergonomics

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

## 3. Performance and scaling

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

## 4. What is missing (must-have)

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

## 5. Nice to have

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

## 6. Prioritised recommendations

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

## 7. What worked well

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

## 8. Evidence from Glow Garden

Numbers from this machine (Release, single process), so the claims above are not theoretical:

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

## 9. Boilerplate hotspots

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

Every item below was observed in this work, not hypothesised. "Blame" distinguishes framework
defects from sample-side mistakes the framework made easy to commit.

| ID | Symptom | Root cause | Blame | Status |
| --- | --- | --- | --- | --- |
| B1 | `NullReferenceException` at `PopulationColumn.cs:337` on the first tick of a 1M-row rotation | Concurrent `EditNext` per worker range corrupts the shared free-batch `Stack` used by undo capture | Framework (1.1) | Worked around: capture once serially, write through disjoint `Memory<T>` slices |
| B2 | The NRE above hid the real failure and pointed at the catch block | `CapturePageUndo`'s catch recycles a possibly-null batch and can throw while handling | Framework | Unfixed; worked around by never hitting B1 |
| B3 | Hard crash "Position at row 6 is non-finite or outside the grid bounds" during a system step | `PopulationSpatialGrid2D.Rebuild` throws on any out-of-bounds row; message names no population, value or bounds | Framework robustness (1.3) | Unfixed; sample clamps now |
| B4 | Plants spawned past the bottom of the map after the world grew | The 3-row scatter still divided the height by 2, from when there were two rows | Sample | Fixed (divisor 3) |
| B5 | Worker positions left the map after wall-following | Steering rotates away from the desired direction and can step outward at the border | Sample | Fixed (clamp before writing population rows) |
| B6 | Workers oscillated in place, looking like they had given up | Wall tangent was derived from the field gradient and multiplied by a stored side; crossing a corridor midline flipped the gradient and reversed the step | Sample | Fixed (deviate by a stored angle from the desired direction instead) |
| B7 | Workers left a wall, then walked straight back into it | `ClearPath` sampled 12 points regardless of distance, so it missed thin thickets | Sample | Fixed (sample every grid cell, capped at 64) |
| B8 | Workers wedged permanently in a "free" pinch | Blocking used a bilinear field while corridors were carved per cell, so a 2-cell corridor was not walkable by the point test; every axis slide was blocked | Sample + framework gap (no walkability type) | Fixed (cell-exact blocking plus an escape to the cell centre and a free neighbour) |
| B9 | Seeds and starting pods could sit inside a thicket, unreachable | Placement sampled once and accepted the result | Sample | Fixed (bounded retries, 12 attempts) |
| B10 | Gatherers wandered despite fresh food on the map | Trails decayed below the follow threshold at the colony end; recruitment only worked near the food | Sample tuning | Fixed (re-stamp the trail on every pickup, slower decay, threshold 0.4) |
| B11 | Shipped worker cap was 16,000 while the README and UI said 1,000 | A temporary stress-measurement edit was never reverted | Sample | Fixed in review |
| B12 | Smoke numbers changed (1031 bursts to a different value) with no gameplay change | Switching from linear scans to grid queries changed resolution order | Framework documentation gap | Accepted; ordering is not part of the documented contract |

Notes on the interesting ones:

- **B1/B2** are the only two that are unambiguously framework defects. B1 is a latent crash in the
  API the framework advertises for parallel work; B2 turned a diagnosable bug into a mystery.
- **B3, B4, B5** are the same class: data validation that throws at the point of use, far from the
  producer, with a message that does not identify the producer.
- **B6–B8** are the "agents look broken" class. They are sample bugs, but all three exist because
  the framework offers spatial *queries* and nothing about *movement*, so every sample invents
  steering, walkability and unsticking from scratch. The sample needed 441 lines for terrain plus
  wall-following plus an escape hatch, and still had three iterations of "workers stop trying".
- **B10** is a tuning bug that only appears at scale: the mechanic worked in a small test and
  failed when trails had to survive long enough for foragers to use them.
- None of these were caught by the framework's own tests. Two were hard process crashes, and one
  (B1) was intermittent, which is the worst combination for a framework that promises determinism.

---

## 11. What would make this production grade

"Production grade" here means: deterministic and replayable, recoverable from bad data, observable
when slow, versioned when saved, and safe to hand to content authors. The current framework is a
strong prototype kernel with several of those pieces present and none of them finished.

### 11.1 Determinism contract

Present: seeded `DrawStream`, population undo, declared-access scheduling, `CaptureState`/
`RestoreState` on extensions, deterministic tests in the sample.

Missing:

- Per-row randomness usable from parallel systems (see 1.4).
- Ordered reduction for parallel passes (see 3.2).
- A documented floating-point policy. All simulation math is `float` and `MathF`; cross-platform
  results are untested, and `MathF.Sin/Cos` are not guaranteed identical across runtimes. A
  fixed-point or "integer where it matters" policy, or at least a documented supported platform
  set, is required before replay files can be trusted.
- A replay artifact: `(seed, content hash, input log, tick count)` plus a run hash per tick.
- A CI gate that runs the same replay on every supported platform and compares hashes.
- Determinism across worker counts is *claimed* but the sample's tests only compare two hosts at
  the default count. A test that runs 1, 2, 4 and 8 workers and compares state is cheap and would
  catch regressions in the scheduler.

### 11.2 Failure containment

Present: population schema validation on restore; bounded queues; clamps at content use sites.

Missing:

- A step-level error boundary: a failing system should fail the tick, roll back, and surface a
  diagnostic record, not kill the process from inside a grid rebuild.
- Validation that reports the producer: population ID, row, value and bounds in the message (B3).
- A quarantine path for invalid rows (skip + counter + log) for decorative or non-critical
  populations, with a strict mode for tests.
- No `catch` block anywhere in the kernel that can throw while handling (B2).
- Frame-budget enforcement: if a step exceeds the budget, the host should log the offending
  systems and take a policy action (skip, degrade, slow down the clock) instead of drifting.

### 11.3 Observability

Present: `PresentationCueStream.DroppedCount`; the benchmarks project has its own gate runner.

Missing:

- Per-system timing and allocation stats on `SystemicGame` (mean/p95/max, allocations per tick).
- Tick budget assertions in the sample's CI, not only in the separate benchmarks harness.
- Counters surfaced rather than hidden: dropped cues, capped intents, rejected spawns, stuck
  agents, out-of-bounds clamps. The sample had to hand-roll a stuck-agent detector with a
  `StallTicks` column.
- Structured logging with system ID, tick and entity/population context.
- A debug overlay: draw the spatial grid, walkability field, scent field, and scheduler waves.
  Two of the bugs above (B6, B8) would have been obvious in a ten-second visual check.

### 11.4 Save, load and migration

Present: versioned `PopulationWorldState`, per-column codecs, `RestorePopulationState`,
`ISystemicStatefulExtension` for extension state.

Missing:

- `SystemicGame.CaptureState()` composing clock, tick, draw stream, entity data, populations and
  every registered extension, with a single version and a content hash.
- Migrations: the save version is checked, but there is no `IMigration` story; adding a column
  changes the schema and old saves cannot be loaded (the sample simply does not save).
- Atomic writes, checksums, and a documented compatibility policy.
- A sample that demonstrates save/load, rather than a README paragraph explaining why it cannot.

### 11.5 Content pipeline

Present: JSON content, rule compilation, validation at load, clamping at use.

Missing:

- A schema per game module: property names, types, ranges, defaults, required tags. A typo in
  `garden.worker.patience` currently changes behaviour silently.
- Typed accessors generated from that schema, so systems do not do string lookups per tick.
- Authoring diagnostics with file, line and property name.
- Hot reload for iteration.
- A single source of truth for tuning: today the same value lives in JSON, in a code default, and
  in the README.

### 11.6 Concurrency safety

Present: declared access, deterministic waves, `ForEachRange`.

Missing:

- A safe parallel write API (1.1) and ownership documentation.
- Debug-mode instrumentation: record which thread captured a column's page this tick and assert
  single ownership; assert that a structural edit did not happen while a span was borrowed.
- Analyzers or runtime checks for the obvious mistakes: `Publish` inside a parallel pass, writing
  an undeclared population, editing a column from two ranges of the same system.
- A documented answer for `Draws` in parallel code.

### 11.7 Tooling and CI

Present: benchmarks gate, sample tests, CLI smoke modes.

Missing:

- A replay/diff harness usable from a test: run N ticks, hash the state, compare against a stored
  hash. Today determinism tests instantiate two hosts in one process, which cannot catch
  cross-run or cross-platform drift.
- Golden screenshots for the render path; `--verify-render` checks color diversity, not appearance.
- A headless "no agent stuck / no agent out of bounds / no unreachable free cell" invariant check
  in the sample's test suite (all of these were ad hoc scratch tests).
- Profiler integration and allocation budgets in the sample.
- A documented support matrix (OS, runtime, GPU backend) with CI on each.

### 11.8 API stability and analyzers

- A semantic-versioning policy for kernel types; the current surface mixes stable concepts
  (`Population<T>`, handles) with prototype ones (`EditNext` semantics).
- Generated event constants instead of magic strings.
- `[Obsolete]` migration paths when column schemas or access modes change.
- Public API review for the rough edges in section 2 (`Bounds2` argument order, `EditRange(row,1)`,
  dual `Step` hosting).

### 11.9 Definition of done

| Requirement | Status |
| --- | --- |
| Deterministic replay with a hash, verified in CI across platforms | missing |
| Randomness available to parallel systems without breaking determinism | missing |
| One bad entity cannot crash the process | missing (hard throw in grid rebuild) |
| Every dropped/skipped/rejected event is counted and visible | partial (cue drops only) |
| Per-system timing and allocations available at runtime | missing |
| Full game save/load with version and migration | missing (populations only) |
| Content schema validated with file/line diagnostics | missing |
| Parallel population writes are safe by construction | missing (B1) |
| Debug overlay for grids, fields and scheduler waves | missing |
| Flagship sample demonstrates save, replay and profiling | missing |

Rough sizing, if this were planned as work rather than a wish list:

- **Small (days):** clamp-and-report validation, don't-throw catch blocks, `World.Seed`, worker-cap
  log, per-system timing counters, cue counter surfacing, sample invariant tests.
- **Medium (weeks):** nearest-neighbour queries, safe parallel writes, per-row RNG, typed content
  accessors, ordered reduction, replay harness, debug overlay.
- **Large (a milestone):** static geometry plus pathing, full save with migrations, event payloads
  and aggregation, cross-platform determinism policy, analyzers and API stability guarantees.

---

## 12. Performance: what is lacking, and how to get the best literal performance

The kernel's performance *model* is sound: unmanaged structs, per-field columns, borrowed spans,
generated typed access, allocation-free queries with reused lists, and a declared-access scheduler.
What is missing is the layer that turns that model into consistently fast code: the framework
neither removes accidental costs nor makes the remaining costs visible, and it offers no
escape hatches when a sample outgrows the safe path.

### 12.1 Where the time actually goes (measured here)

| Scenario | Result | What dominates |
| --- | --- | --- |
| 50k workers, linear nearest-target scans | ~7.6 s/tick | O(n·m) scans |
| 50k workers, grid queries, 4 scheduled workers | ~15 ms/tick | grid queries + serial resolution tail |
| Same, pinned to 1 worker | ~34 ms/tick | the same work, no parallelism |
| Same, legacy serial system | ~55 ms/tick | no partitioning |
| 1M-row rotation, double-buffered column | ~2.9 ms/tick | memory traffic: ~16 MB read/write + ~8 MB undo snapshot |
| 50k workers with Magnetism | >1 s/tick | one creature-grid query per mote per tick |
| 32 workers, 64 plants, 12k motes, 3 talents | ~27 ms/tick | Magnetism queries and fact dispatch |
| Sample render, ~1000 workers | up to 16 `SpriteBatch.Draw` calls per worker (from the draw code; not measured on a display) | per-entity draw calls, not simulation |

Two lessons from those numbers: at small scale the serial resolution tail limits how far the
scheduler can help (4 workers bought only ~2.3×), and at large scale the bottleneck is memory
traffic or a repeated query whose cost was never designed down. Neither is visible from inside the
framework.

### 12.2 Structural performance gaps

| Gap | Cost | Evidence |
| --- | --- | --- |
| Validation on every call | `ValidateRange` + `EnsureActive` per `EditRange`/span access; `ValidatePositions` walks every row on every grid rebuild, in Release | grid rebuild validates all 12k mote positions per tick even when only a few moved |
| Per-row write path | `EditRange(row, 1)` revalidates, re-captures pages and builds a span per field, per row | 29 call sites; a 7-column row write is 7 captures |
| Undo costs are unavoidable | Full-buffer snapshot for whole-range edits, page copies even for a full rewrite, no opt-out | ~8 MB/tick for the 1M-row rotation |
| Grid rebuild is O(n) per tick | Every position re-bucketed even when it did not move a cell | 12k motes + 1000 workers + plants, every tick |
| Grid queries have no filter pushdown | Callers fetch candidates then test columns (ripe, value > 0, kind) | `NearestRipe` loops candidates and re-reads growth |
| No batch column operations | Every per-row operation is scalar C#; branchy agent logic blocks auto-vectorisation | movement, clamping, decay all scalar loops |
| Trigonometry in hot loops | `MathF.Sin/Cos/Atan2` per agent per tick for wandering and headings | ~20–40 cycles each, millions per second at scale |
| Global scheduler default | 4 workers, and `WriteWorld` serialises the wave | sample had to opt in explicitly |
| Serial resolution tail | All ordered effects (pickups, bursts, facts) resolve in one thread after the parallel pass | 50k-worker tick spends a large share here |
| Per-event dispatch | String-keyed fact types, per-fact rule lookup, per-cue lock | hundreds of facts/tick at modest load |
| Projection interfaces | `IReadOnlyList<TState>` indexers are interface calls with a span per field | `Plants`/`Creatures`/`Motes` in UI and tests |
| Render path | One `SpriteBatch.Draw` per shadow, body, eye and carried mote; no instancing | ~1000 workers × up to 16 draws |
| LOH pressure | 1M-row columns are 8 MB arrays; growth reallocates and copies | `Reserve` at construction helps only if you know the cap |
| No hot/cold split | A row contains physics, AI state and presentation-only fields in separate columns, but systems touch them at the same cadence | cache lines are wasted on fields the loop does not read |

### 12.3 Proposals: how to make the fast path the default

The goal is not "always fast" — that is not achievable — but "never accidentally slow, and always
visible when slow". Four principles and the concrete changes behind them.

**A. Zero-cost abstractions in Release**

- Compile out range/bounds validation except in Debug or a checked mode:
  `EditRangeUnchecked`, `GetUnchecked`, `[Conditional("DEBUG")] Validate(...)`. The generated
  accessors should emit the unchecked form by default and the checked form under a define.
- `MethodImpl(AggressiveInlining)` on `Vector2`-sized helpers, column indexers and query cell math.
- Avoid interface dispatch in hot loops: concrete `Population<T>` enumerators (struct), and
  generated `TState` indexers on the concrete type rather than through `IReadOnlyList<T>`.
- Pre-size and reuse: thread-static scratch lists (the sample does this for grid queries), pool
  anything larger than a few KB.

**B. Remove the copy: undo should cost what the change costs**

- `EditNextWhole()` for double-buffered columns that rewrite every row: skip page capture entirely,
  keep only the role-swap undo. This alone halves the cost of the 1M-row tier.
- Per-column `UndoMode.None` for derived/cosmetic state (presentation ghosts, ambient motion,
  cached aggregates), with the transaction recording "not restored" so the contract is explicit.
- Sparse undo: capture only the rows actually written (a bitset + row copies), instead of whole
  pages, when the edit is sparse. The undo batch already exists; it just needs a sparse variant.
- Batch row writes: `EditRows(range)` returns per-column spans after a single capture, so a
  seven-field row write is one capture and one validation, not seven.

**C. Make memory traffic and math the bottleneck you chose**

- Batch column operations with SIMD: `column.Add(scalar)`, `Clamp(min, max)`, `Scale`, `Lerp`,
  `DistanceSquared(a, b, results)`, implemented with `Vector256`/`Vector128` and
  `System.Numerics.Tensors.TensorPrimitives` (available on .NET 10). A 1M-row decay/rotation then
  runs at memory bandwidth instead of scalar issue rate.
- Homogeneous lanes: bucket rows by behaviour (cooldown, carrying, kind) so the inner loop has no
  branches and vectorises. The sample's `DecideRange` is branchy by construction; a
  "worklist per state" layout would be branch-free and much faster.
- Replace per-tick trig with a precomputed 4,096-entry sin/cos table or integer angles; steering
  wants a normalized direction, not an angle. This is one of the cheapest wins in agent code.
- Hot/cold column split by update cadence (position/velocity vs. presentation fields) and keep hot
  fields adjacent in memory; consider archetype chunking (16 KB chunks, SoA within a chunk) if
  systems read most columns of a row anyway.
- Incremental spatial index: `Move(row, oldPosition, newPosition)` with intrusive cell lists
  (head/next arrays) makes insertion/removal O(1) and removes the per-tick rebuild entirely. Keep
  `Rebuild` for cold starts and bulk changes.
- Nearest queries with early exit and a per-cell cached minimum: `TryFindNearest` using a
  two-level grid (coarse cells for far, fine cells for near) avoids scanning the map for a distant
  target.

**D. Parallelism without contention**

- Default to all logical cores, or at least log the cap (see 3.1).
- Deterministic work stealing: split into many small chunks, hand them out with an atomic counter,
  and merge results by chunk index. Load balancing without sacrificing determinism.
- Per-partition undo journals merged in partition order, instead of one global journal; the
  contention point disappears and the merge stays deterministic.
- `context.Reduce(seed, partitionSelector, combine)` so the resolution tail can stay parallel
  (sums, min/max, flags) with a fixed combine order.
- Split simulation and presentation onto separate threads with a double-buffered read model; the
  renderer never blocks the tick, and cue sampling moves off the hot path.
- Publish facts into a per-thread ring and merge at the step boundary instead of locking the cue
  stream per event; aggregate by (type, region) before dispatch.

**E. Make cost visible**

- `SystemicGame.TickStats`: per-system wall time (mean/p95/max), allocations, queue depth, worker
  utilisation, number of facts dispatched, cues dropped, rows validated. This is the difference
  between "it feels slow" and "Magnetism costs 1.2 s".
- A built-in budget mode: `TickBudget(TimeSpan)` → warn (or degrade) when a system exceeds its
  share; per-system budgets declared alongside access.
- A debug overlay that draws the grid, the walkability field, the scheduler waves and per-system
  timings. Two of the bugs in section 10 would have been diagnosed in seconds.

**F. Escape hatches for the last 20%**

- `unsafe`/`Unsafe` accessors for raw column pointers, clearly marked, for kernels that must run at
  memory bandwidth.
- A `FixedUpdateLod` helper: update a fraction of rows per tick with a stable rotation, so a system
  can trade fidelity for a linear cost reduction without rewriting the loop.
- A GPU-compute bridge for homogeneous populations (agents as a structured buffer, economy scalars
  read back once per frame). The framework does not need to own this, but it should not make it
  impossible: positions in, aggregate out.

### 12.4 A performance-first API sketch

```csharp
// Batch writes: one capture, one validation, spans for every column.
using var rows = population.EditRows(range);
var positions = rows.Span(PositionKey);
var headings  = rows.Span(HeadingKey);

// SIMD column kernels (TensorPrimitives underneath).
positions.Add(velocity);
headings.Clamp(-MathF.PI, MathF.PI);
glow.Scale(0.9f);

// Whole-buffer rewrite: no undo copy, role swap only.
var next = trail.EditNextWhole();

// Derived columns opt out of rollback explicitly.
[PopulationColumn("ghost", Undo = UndoMode.None)]

// Incremental index instead of a per-tick rebuild.
index.Move(row, previous, current);

// Deterministic, load-balanced reduction.
var harvested = context.Reduce(0L, partition => partition.SumOf(picked), (a, b) => a + b);

// Batched, typed facts instead of per-event strings and locks.
context.Publish(new FactBatch(factIds, sources, targets, amounts, anchors));

// Per-tick tuning resolved once, no string lookups in the loop.
var tuning = content.Tuning<WorkerTuning>();
```

### 12.5 Determinism versus speed

"Best literal performance" and determinism pull in opposite directions: SIMD-friendly math usually
means floats, floats mean platform variance, and parallel reductions mean ordering choices. The
defensible position for a deterministic framework:

1. Fixed-point or integer math for anything that affects gameplay outcomes; floats for
   presentation only.
2. If floats are used, pin the supported runtime/architecture set, avoid `MathF` transcendentals
   in simulation, and verify hashes in CI on every platform in the matrix.
3. Use a fixed partition count for reductions regardless of worker count; merge in index order.
4. Make the trade-off explicit per system: `[Deterministic]` versus `[Cosmetic]`, where cosmetic
   systems may use floats, parallelism and non-ordered merges, and are excluded from save state.

### 12.6 What a 60 Hz, 1M-agent budget looks like

16.6 ms per tick, of which a sane split is roughly: 8 ms simulation, 3 ms facts/presentation
enqueue, 3 ms rendering submission, 2 ms slack. At 1M agents that is ~8 ns per agent for
simulation — about 25 cycles. That budget only works if, per agent, the tick has: no allocation,
no virtual call, no string, no trig, no page capture, one cache-line-friendly pass over
position/velocity, and neighbour data from an incremental cell index. Any one of those violated
makes the budget unattainable. Of those, the framework imposes page capture, string-keyed dispatch
and interface-based projections on the default path; trig and branching are the sample's own
choices, but nothing in the API steers authors away from them.

### 12.7 Prioritised quick wins

| Win | Effort | Expected effect |
| --- | --- | --- |
| `EditNextWhole` / undo opt-out for derived columns | S | ~40–50% off the 1M-row tier |
| Release-only validation on column access | S | removes per-access checks from every loop |
| Batch row writes (`EditRows`) | S | 7× fewer captures for projection writes |
| `TryFindNearest` with early exit | M | replaces hand-rolled expanding-radius scans |
| Incremental spatial index (`Move`) | M | removes O(n) rebuild per tick |
| SIMD column kernels via `TensorPrimitives` | M | memory-bandwidth-bound movement/decay |
| Deterministic work stealing + `Reduce` | M | removes the serial resolution tail |
| Precomputed trig table / integer angles | S | removes the most expensive per-agent math |
| Fact type IDs instead of strings | S | turns dispatch into an array index |
| Per-system `TickStats` | S | makes every later optimisation measurable |
| Batching/instancing in the render path | M | 15 draws per worker down to 1–2 |
| Homogeneous worklists (bucketed lanes) | L | branch-free, vectorisable agent loops |
