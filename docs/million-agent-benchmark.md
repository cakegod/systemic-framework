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

`--mode safe` is the default. It wraps each complete movement tick in a `World`
transaction, including the cost of capturing rollback state and committing. The
explicit `--mode optimized` uses the same population columns and update loop
outside a transaction: a failure during a tick can leave partially updated state.
`--mode both` runs both in that order and checks their final state hashes match.
`--million-agents --self-test` checks small hand-derived examples for both modes.

The workload contains exactly 1,000,000 population rows, each with four signed
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

## Example observation

On 2026-10-27, a Release run in the shared development container used 20 warm-up
ticks and 100 measured ticks per mode. The reported CPU was an AMD EPYC 9V74,
with Debian 13 and .NET 10.0.14 x64. The process reported 4 available processors,
affinity and effective cpuset `0-4`, cgroup `cpu.max` of `400000 100000`
(four CPUs of quota), and `memory.max` of 32 GiB. Other development activity was
present, so this is a single shared-container observation.

| Mode | p50 | p95 | Maximum | Process allocations |
| --- | ---: | ---: | ---: | ---: |
| Safe, transactional | 1.444 ms | 5.515 ms | 23.660 ms | 0 B |
| Optimized, no rollback | 0.881 ms | 1.278 ms | 18.444 ms | 0 B |

Both modes validated all 1,000,000 rows and reported state hash
`6d85a88dbfda5105`. Their p95s met the 8 ms target in this run; the larger maxima
show that individual ticks exceeded it. These values describe the workload and
environment above and are not measurements of a full game or another CPU.
