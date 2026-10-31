# Analyzer reference

`Systemic.Kernel.Generator` supplies the population schema generator and two analyzer families; every shipped library also references `Microsoft.CodeAnalysis.PublicApiAnalyzers` for its public surface, described under [Public API surface](#public-api-surface). In this repository, every project under `samples/` receives it through `samples/Directory.Build.targets`. A game project elsewhere should reference `Systemic.Kernel` normally and reference `Systemic.Kernel.Generator` with `OutputItemType="Analyzer"` and `ReferenceOutputAssembly="false"`.

## Strict determinism

The `SYSDET` rules are enabled errors. By default, they analyze implementations of `ISystemicSystem` and `IScheduledKernelSystem`. This is the primary determinism guardrail now that game behavior lives in C# systems rather than JSON rules. Apply `[StrictSimulation]` or set `systemic_determinism = strict` in `.editorconfig` to opt other code in. `systemic_determinism = relaxed` disables the gameplay-system default for a file, while `[CosmeticOnly]` excludes its annotated code even if `[StrictSimulation]` is also present. An explicit `[StrictSimulation]` marker still opts its code in when the file is relaxed.

| Rule | Detects |
| --- | --- |
| [`SYSDET001`](#sysdet001) | Recognized floating-point types and values, arithmetic, and math calls in strict simulation. |
| [`SYSDET002`](#sysdet002) | Recognized clock and random sources, including time-of-day values, `Stopwatch`, `Random`, random-number generators, and `Guid.NewGuid`. |
| [`SYSDET003`](#sysdet003) | Recognized unordered collection iteration and uncoordinated parallel work, including `Parallel`, task scheduling, and thread-pool calls. |

<a id="sysdet001"></a>
### SYSDET001

Flags recognized floating-point types and values, floating-point arithmetic, and recognized math calls in strict code. Prefer the Kernel deterministic numeric primitives for authoritative state.

<a id="sysdet002"></a>
### SYSDET002

Flags recognized ambient clock and random sources such as `DateTime.Now`, `DateTime.UtcNow`, `Stopwatch`, `Random`, `Guid.NewGuid`, and selected `Environment` and `TimeProvider` members. Pass deterministic inputs into systems instead.

<a id="sysdet003"></a>
### SYSDET003

Flags recognized unordered collection enumeration and uncoordinated parallel APIs such as `Parallel`, task scheduling, and thread-pool work. Establish a stable order before iteration and use the Kernel schedule or range operations for controlled parallel work. A local `HashSet<T>` constructed without an explicit comparer and containing stable boolean, numeric, character, or enum values is recognized when ordered by the identity selector using default ordering. Explicit set or ordering comparers, other set sources, and selectors that can produce tied keys remain flagged because the analyzer cannot prove those orders are stable. Default string ordering remains flagged because its collation can depend on the current culture.

Choose deterministic numeric primitives and explicit inputs for authoritative simulation. Establish stable iteration order before processing unordered collections. Use the Kernel scheduler or range APIs for parallel simulation work, and review the operations inside the callback as well as the scheduling API itself.

## System access and range callbacks

`SYSACC001`–`SYSACC006` and `SYSACC008` are enabled errors for recognized `ISystemicSystem` code; `SYSACC007` is informational:

| Rule | Diagnostic |
| --- | --- |
| [`SYSACC001`](#sysacc001) | Immediate `SystemicSystemContext` World operations (`GetWritableWorld`, `SetProperty`, and `Publish`) need `SystemAccess.WriteWorld`. |
| [`SYSACC002`](#sysacc002) | `DeferPopulationCommands` needs `SystemAccess.DeferWorldWrites` or `SystemAccess.WriteWorld`. |
| [`SYSACC003`](#sysacc003) | Buffer swaps and both-buffer edits are unsafe inside a range callback. |
| [`SYSACC004`](#sysacc004) | Scoped population column reads and writes need matching per-column access declarations. |
| [`SYSACC005`](#sysacc005) | Immediate World operations and mutations through `SystemicSystemContext.Commands` are unsafe inside a range callback. |
| [`SYSACC006`](#sysacc006) | A parallel range callback cannot read all rows of a single-buffered column that it writes. |
| [`SYSACC007`](#sysacc007) | A system that does not declare `Access` runs with the default exclusive `WriteWorld` declaration. |
| [`SYSACC008`](#sysacc008) | `DeferFact` needs `SystemAccess.DeferWorldWrites` or `SystemAccess.WriteWorld`. |

<a id="sysacc001"></a>
### SYSACC001

Declares missing immediate World access for direct calls to `GetWritableWorld`, `SetProperty`, or `Publish` on `SystemicSystemContext`. Add `SystemAccess.WriteWorld` when the operation is required.

<a id="sysacc002"></a>
### SYSACC002

Declares missing deferred population access for a direct call to `DeferPopulationCommands`. Add `SystemAccess.DeferWorldWrites` or `SystemAccess.WriteWorld` before creating the partition-local buffers.

<a id="sysacc003"></a>
### SYSACC003

Flags `PopulationAccessScope.EditBoth` and `DoubleBufferedPopulationColumn.Swap` inside a recognized range callback. Scheduler scopes only expose the assigned range for writes, and parallel scopes reject both-buffer edits at runtime. Initialize both buffers and swap roles outside the parallel callback.

<a id="sysacc004"></a>
### SYSACC004

Flags recognized `PopulationAccessScope` reads and writes when the corresponding generated `Read*` or `Write*` column declaration is missing. `WriteWorld` orders the system exclusively, but does not grant column access. Full reads of single-buffered columns need `Read*`; a `Write*` declaration also allows partition-local reads. The current buffer of a double-buffered column can be read with either a read or write declaration while writes target `Next`.

<a id="sysacc006"></a>
### SYSACC006

Flags `PopulationAccessScope.ReadAll` for a single-buffered column inside `ForEachPopulationRange` when the system also declares writes to that column. Other partitions can mutate those rows at the same time. Use `ReadRange` for the callback's assigned rows, or move cross-partition reads to a separate immutable/double-buffered column. The runtime enforces the same restriction even when the analyzer cannot recognize the access declaration or call site.

<a id="sysacc007"></a>
### SYSACC007

Reports an informational diagnostic at a system type that omits `ISystemicSystem.Access`. The default is `SystemAccess.WriteWorld`, so the system runs in a wave by itself even when it only touches one population. Declare the generated `Read*`/`Write*` column access, or `DeferWorldWrites`, to let compatible systems share a wave. The diagnostic is informational because exclusive execution is correct, just slower.

<a id="sysacc008"></a>
### SYSACC008

Declares missing deferred World access for a direct call to `DeferFact`. Add `SystemAccess.DeferWorldWrites` (shared wave) or `SystemAccess.WriteWorld` (exclusive) before queueing facts, and call it from the system body rather than a range callback, where the queue would be shared across workers.

<a id="sysacc005"></a>
### SYSACC005

Flags direct immediate World operations and direct mutations through `SystemicSystemContext.Commands` inside a recognized `ForEachRange` or `ForEachPopulationRange` callback. Queue World changes for the wave barrier instead.

`SYSACC001` and `SYSACC002` understand direct access initializers and private static readonly `ImmutableArray<SystemAccess>` values with direct collection or array initializers. They skip access declarations whose value cannot be resolved from those forms. A constant `WriteResource("$world")` is treated as `WriteWorld`; an unrelated constant resource name is known non-World access, while a nonconstant resource name makes the declaration unknown. For per-column checks, use the generated `Read*` and `Write*` declarations.

The preferred pattern is to state the actual column access in `ISystemicSystem.Access` and pass the population to `SystemicSystemContext.ForEachPopulationRange`. The callback receives a scoped capability with its assigned range and column operations; generated `Columns.WriteRange(ref scope)` binds its writer to that range and checks its lifetime token on every write. Keep an explicit population lease only when row identity must span phases or a callback reads another population. Queue World and population structure changes through deferred APIs and let the schedule apply them at a barrier.

## Public API surface

Each shipped library (`Systemic.Kernel`, `Systemic.Core`, `Systemic.Rendering`, `Systemic.Incremental`, `Systemic.Presentation`, `Systemic.Gameplay2D`) references `Microsoft.CodeAnalysis.PublicApiAnalyzers` and carries two files in its project directory:

- `PublicAPI.Shipped.txt` — the surface that has shipped. Treat it as a contract: changing a line is a breaking change for anyone compiling against the library.
- `PublicAPI.Unshipped.txt` — additions and changes for the next release. It starts with `#nullable enable`, which makes nullability part of the declaration.

The analyzer project and the CLI tool publish no library contract and do not carry the files.

`RS0016` fails the build when a public symbol is not declared in either file, so a new public member cannot land by accident. `RS0017`, `RS0022`, `RS0024`–`RS0027` and `RS0037` cover the reverse direction: a declared symbol that no longer exists, a constructor that changed, a parameter rename, an additional or removed optional parameter, and a nullability change. When the analyzer reports `RS0016`, generate the missing entries instead of typing them:

```sh
dotnet format analyzers src/Systemic.Core --diagnostics RS0016
```

That appends the exact declaration text to `PublicAPI.Unshipped.txt`. At release time a maintainer moves those lines into `PublicAPI.Shipped.txt`, which is the diff a release moves into `PublicAPI.Shipped.txt`; additions stay in `Unshipped` until then.

## Analysis boundaries

These diagnostics are a static guardrail, not a proof of determinism, race freedom, or complete access coverage. They inspect direct, statically resolvable access declarations and recognized call sites. They do not perform whole-program alias analysis or interprocedural proof. Computed or unknown `Access` expressions are skipped when the analyzer cannot resolve them. Mutable `SystemAccess[]` backing fields are intentionally skipped; the analyzer resolves private static readonly `ImmutableArray<SystemAccess>` initializers with direct contents. Custom or indirect access helpers, aliases of contexts, populations, columns, or command buffers, and indirect calls may not be reported. Other column API forms can also fall outside the checks.

`SYSACC005` analyzes direct uses of `SystemicSystemContext.Commands` inside recognized range callbacks. It does not prove arbitrary alias or helper behavior. `SYSACC003` checks `PopulationAccessScope.EditBoth` and direct `Swap` calls; scope-mediated row writes are checked against their declared columns by `SYSACC004`.

The schedule also relies on truthful resource declarations and range callbacks that write only their assigned rows. Runtime scopes enforce declared column modes, callback thread, population ownership, and range bounds. Use tests and code review alongside analyzer diagnostics; a clean analyzer build only means no diagnostic was found in the patterns it recognizes.
