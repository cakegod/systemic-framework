# Crowd million

A content-only pack for the crowd tier: one million prey rows, one authored rule evaluated per row
per tick, and one aggregate fact instead of a fact per row. There is no C# game here — the rules and
the entity list are the content, and `benchmarks/Systemic.Benchmarks` drives the rows that the
content talks about.

## The content

`content/rules.json` has two rules:

- `crowd.prey.burn` runs on `crowd.prey.think` for every row of the `crowd.prey` population. It
  matches a row that carries the `burning` tag and still has at least one energy, and spends one
  energy. Both the condition and the effect name the row with `factRow`, the row-addressed subject
  form.
- `crowd.report.standing` runs when the `crowd.prey.standing` fact arrives and increments
  `crowd.prey.report` on the row the fact is about.

`content/entities.json` is empty on purpose: the sample is about population rows, not entities.

## Run it

```sh
dotnet run -c Release --project benchmarks/Systemic.Benchmarks -- \
  --gate --scenario crowd --warmup-ticks 200 --ticks 500 --repeat 1
```

The gate builds the pack through `ContentPackLoader`, so a broken rule fails the run before any tick
is measured. Use `--agents N` for a different row count and `--content-root <dir>` to point at
another pack. Validate the content on its own with:

```sh
dotnet run --project src/tools/Systemic.Cli -- validate samples/crowd-million/content
```

## What the run does per tick

1. Moves one eighth of the crowd (the crowd is split into eight movement buckets).
   `crowd.prey.position` and `crowd.prey.velocity` are `Fixed64`
   columns, so the move is exact and the validation can assert bits rather than a tolerance.
2. Evaluates `crowd.prey.burn` against every one of the million rows in one scope. The rule's first
   condition is a tag, so the scan reads one packed tag bit per row and only evaluates the full rule
   for rows that carry it.
3. Collects the sink over `crowd.prey.energy`. The sink counts the rows that are still standing and
   appends at most one row-addressed fact carrying that count, and nothing while the count is
   unchanged, so the fact volume does not follow the row count.

## What the validation asserts

- Every row's position equals its velocity times the number of moves its bucket received.
- Burning rows lost exactly one energy per tick and every other row is untouched.
- Only the anchor row (row 0) carries the report count, and it equals the number of facts the sink
  emitted — a sink that never dispatched, or a dispatch that never landed, fails the run.

## Measured here

On an i5-4430 at 3.0 GHz running .NET 10 Release, gate parameters `--warmup-ticks 200 --ticks 500`,
two consecutive runs: p50 2.87 / 2.81 ms, p95 3.24 / 3.18 ms, maximum 4.3 / 3.9 ms, zero bytes
allocated, all ticks inside the 10 ms simulation share of a 30 Hz frame. The gate fails on any tick
at or over budget, so a machine that is busy with something else will fail it: run it as a dedicated
process, as the other gate tiers are run.
