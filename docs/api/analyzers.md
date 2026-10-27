# Analyzer reference

`Systemic.Kernel.Generator` supplies the population schema generator and two analyzer families. In this repository, every project under `samples/` receives it through `samples/Directory.Build.targets`. A game project elsewhere should reference `Systemic.Kernel` normally and reference `Systemic.Kernel.Generator` with `OutputItemType="Analyzer"` and `ReferenceOutputAssembly="false"`.

## Strict determinism

The `SYSDET` rules are enabled errors. By default, they analyze implementations of `ISystemicSystem` and `IScheduledKernelSystem`. Apply `[StrictSimulation]` or set `systemic_determinism = strict` in `.editorconfig` to opt other code in. `systemic_determinism = relaxed` disables the gameplay-system default for a file, while `[CosmeticOnly]` excludes its annotated code even if `[StrictSimulation]` is also present. An explicit `[StrictSimulation]` marker still opts its code in when the file is relaxed.

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

The current unshipped `SYSACC` rules are enabled errors for recognized `ISystemicSystem` code:

| Rule | Diagnostic |
| --- | --- |
| [`SYSACC001`](#sysacc001) | Immediate `SystemicSystemContext` World operations (`GetWritableWorld`, `SetProperty`, `Publish`, and `Execute`) need `SystemAccess.WriteWorld`. |
| [`SYSACC002`](#sysacc002) | `DeferPopulationCommands` needs `SystemAccess.DeferWorldWrites` or `SystemAccess.WriteWorld`. |
| [`SYSACC003`](#sysacc003) | Direct population edits inside a `ForEachRange` callback can escape the assigned range or mutate whole buffers; use range-local writes and move whole-buffer operations out of the callback. |
| [`SYSACC004`](#sysacc004) | Direct reads and writes of generated population column views need the matching generated column access declaration. |
| [`SYSACC005`](#sysacc005) | Immediate World operations and mutations through `SystemicSystemContext.Commands` are unsafe inside a `ForEachRange` callback. |

<a id="sysacc001"></a>
### SYSACC001

Declares missing immediate World access for direct calls to `GetWritableWorld`, `SetProperty`, `Publish`, or `Execute` on `SystemicSystemContext`. Add `SystemAccess.WriteWorld` when the operation is required.

<a id="sysacc002"></a>
### SYSACC002

Declares missing deferred population access for a direct call to `DeferPopulationCommands`. Add `SystemAccess.DeferWorldWrites` or `SystemAccess.WriteWorld` before creating the partition-local buffers.

<a id="sysacc003"></a>
### SYSACC003

Flags direct calls to `EditRange`, `EditMemory`, `EditNext`, and `EditNextMemory` inside a recognized range callback because they do not encode the callback's assigned range. It also flags `EditBothBuffers` and `Swap`, which operate on whole buffers. Use the generated `Columns.WriteRange(range)` writer for row-local edits and move whole-buffer operations outside the callback.

<a id="sysacc004"></a>
### SYSACC004

Flags recognized direct reads or writes through generated population column views when the corresponding `Read*` or `Write*` access is missing. Declare all columns touched by the operation.

<a id="sysacc005"></a>
### SYSACC005

Flags direct immediate World operations and direct mutations through `SystemicSystemContext.Commands` inside a recognized range callback. Queue World changes for the wave barrier instead.

`SYSACC001` and `SYSACC002` understand direct access initializers and private static readonly `ImmutableArray<SystemAccess>` values with direct collection or array initializers. They skip access declarations whose value cannot be resolved from those forms. A constant `WriteResource("$world")` is treated as `WriteWorld`; an unrelated constant resource name is known non-World access, while a nonconstant resource name makes the declaration unknown. For per-column checks, use the generated `Read*` and `Write*` declarations.

The preferred pattern is to state the actual column access in `ISystemicSystem.Access`, borrow the population once, and use the callback's `PopulationRange` for writes. Queue World and population mutations through the context's deferred APIs and let the schedule apply them at a barrier.

## Analysis boundaries

These diagnostics are a static guardrail, not a proof of determinism, race freedom, or complete access coverage. They inspect direct, statically resolvable access declarations and recognized call sites. They do not perform whole-program alias analysis or interprocedural proof. Computed or unknown `Access` expressions are skipped when the analyzer cannot resolve them. Mutable `SystemAccess[]` backing fields are intentionally skipped; the analyzer resolves private static readonly `ImmutableArray<SystemAccess>` initializers with direct contents. Custom or indirect access helpers, aliases of contexts, populations, columns, or command buffers, and indirect calls may not be reported. Other column API forms can also fall outside the checks.

`SYSACC005` analyzes direct uses of `SystemicSystemContext.Commands` inside a recognized `ForEachRange` callback. It does not track a command-buffer alias, nor does it track a `World` or `Entity` passed into the callback as its `state` value and then mutated there. `SYSACC003` flags direct `EditRange`, `EditMemory`, `EditNext`, and `EditNextMemory` calls in range callbacks, plus whole-buffer `EditBothBuffers` and `Swap` calls; it cannot follow an indirect helper that performs the edit.

The schedule also relies on truthful resource declarations and range callbacks that write only their assigned rows. Use tests and code review alongside analyzer diagnostics. A clean analyzer build only means no diagnostic was found in the patterns it recognizes.
