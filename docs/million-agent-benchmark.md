# Million-agent movement benchmark

This benchmark measures one narrow, deterministic kernel workload on the machine
where it runs. It is not a full-game benchmark and does not predict rendering,
physics, pathfinding, rule dispatch, scheduling, save/load, or host overhead. The
8 ms p95 value is a target for this workload, not a claim about other hardware.

Run a Release build:

```sh
dotnet run -c Release --project benchmarks/Systemic.Benchmarks -- \
  --million-agents --mode both --warmup-ticks 20 --ticks 100
```

`--agents N` sets the row count (default 1,000,000, useful up to 100,000,000) and `--target-ms N`
sets the p95 target the report compares against (default 8.0). The crowd tier is measured at ten
million rows:

```sh
dotnet run -c Release --project benchmarks/Systemic.Benchmarks -- \
  --million-agents --mode both --agents 10000000 --warmup-ticks 5 --ticks 20 --target-ms 35
```

`--mode safe` is the default. It wraps each complete movement tick in a `World`
transaction, including the cost of capturing rollback state and committing. The
explicit `--mode optimized` uses the same population columns and update loop
outside a transaction: a failure during a tick can leave partially updated state.
`--mode both` runs both in that order and checks their final state hashes match.
`--million-agents --self-test` checks small hand-derived examples for both modes.

The workload contains exactly `--agents` population rows (1,000,000 by default), each with four signed
32-bit integer columns (`x`, `y`, `vx`, `vy`). For zero-based row `i`, initialize:

```text
x  = (i mod 4096) - 2048
y  = ((17 * i) mod 4096) - 2048
vx = (i mod 7) - 3
vy = ((i / 7) mod 7) - 3       (integer division)
```

Each tick, in a single thread, applies `x = x + vx` and `y = y + vy` with explicit
two's-complement 32-bit wrapping; velocities do not change. No spatial index,
collision detection, per-agent entities, LOD, GPU, or worker scheduler is used.
Initialization, post-run validation, and reporting are outside timed ticks. Every
row and velocity is checked against the closed-form expected state after warm-up
and measured ticks. A noncryptographic FNV-1a state hash helps compare runs; it is
not an authentication or replay-integrity mechanism.

The report includes warm-up and sample counts; wall-clock p50, p95 and maximum
tick duration; process-wide and calling-thread allocated bytes; bytes per tick;
GC collection counts; and the target comparison. Percentiles use nearest rank
(`ceil(p * sampleCount)`, one-based). Allocation counts include managed activity
from the whole benchmark process during the measurement interval and can include
runtime background activity. Wall time includes scheduling interruptions and GC.
Results return success when the workload validates even if p95 exceeds the target;
inspect `targetMet` rather than treating exit status as a performance guarantee.

The environment report identifies the available CPU model, operating system,
runtime and architectures, process-visible CPU count and Linux affinity mask.
On cgroup v2 it reports `cpu.max`, effective CPU set and memory limit at the
process cgroup and each visible ancestor. `cpu.max` shows quota and period in
microseconds (`max` means no quota at that level); the tightest relevant ancestor,
affinity, host contention and hypervisor can constrain actual CPU time. If a
platform or cgroup format is unavailable the report says so. Record these details,
sample counts, command and concurrent load alongside any reported result; a
virtualized CPU model does not establish performance on an older desktop CPU.

## Example observations

Intel i5-4430 at 3.0 GHz, four visible CPUs, .NET 10 Release, zero allocations in every row:

| Rows | Mode | p50 | p95 | Peak RSS |
| --- | --- | --- | --- | --- |
| 1M | optimized | 2.02 ms | 2.66 ms | — |
| 1M | safe-transactional | 2.58 ms | 3.00 ms | — |
| 10M | optimized | 21.02 ms | 23.43 ms | 196 MB |
| 10M | safe-transactional | 30.86 ms | 31.99 ms | 274 MB |

The per-row cost is flat (~2.1 ns) up to ten million, but the transaction does not scale the same way:
its +0.55 ms at 1M becomes +9.8 ms at 10M, because the journal copies 160 MB that no longer fits in
cache. This workload is movement only — no rule dispatch, spatial query, collision or rendering — so
ten million rows here means ten million simulated positions, not ten million thinking agents.

On 2026-10-27, three safe Release runs and one optimized Release run in the shared
development container used 20 warm-up ticks and 100 measured ticks per run. The
reported CPU was an AMD EPYC 9V74, with Debian 13 and .NET 10.0.14 x64. The process
reported 4 available processors, affinity and effective cpuset `0-4`, cgroup
`cpu.max` of `400000 100000` (four CPUs of quota), and `memory.max` of 32 GiB.
Other development activity was present, so these are shared-container observations.

| Mode | p50 | p95 | Maximum | Process allocations | Sampling |
| --- | ---: | ---: | ---: | ---: | --- |
| Safe, transactional | 1.338 ms | 1.697 ms | 3.294 ms | 0 B | Median of 3 runs |
| Optimized, no rollback | 0.768 ms | 0.919 ms | 4.947 ms | 0 B | 1 run |

The three safe runs had p50 values 1.351, 1.338 and 1.274 ms and p95 values
2.098, 1.697 and 1.600 ms. Both modes validated all 1,000,000 rows and reported
state hash `6d85a88dbfda5105`; all measured runs reported 0 B process allocation.
The safe p50 median is below the 1.6467 ms limit (10% above the 1.497 ms baseline).
These values describe the workload and environment above and are not measurements
of a full game or another CPU.
