# ADR 0001 — Populations become rule subjects

- Status: Accepted
- Date: 2026-10-28
- Gate: D1 in `docs/framework-remediation-plan.md`

## Context

Rules, modifiers, grants, tags, links and facts address `Entity`: a heap object with an
`ImmutableHashSet<string>` tag set and a `Dictionary<string, double>` property bag. `Population<TKind>`
is the only store that scales, and nothing bridges the two, so a rule can never touch a population row.
That is root cause R1, and it is what makes "systemic" and "huge" mutually exclusive today.

The alternative whole-framework answer is to stop using entities as rule subjects and keep only
populations. That would delete the heterogeneous, authorable content model that every sample and module
uses today.

## Decision

Populations become rule subjects alongside entities, shipped as a tiered handoff rather than a
replacement:

1. `RuleSubject` gains two addressings: `Entity(id)` for content entities, `Row(populationId, handle)`
   for population rows.
2. Property names in `AdjustProperty` and `SetModifier` compile against the addressed schema at load.
   An unknown property fails the load, not the tick.
3. Entities keep their current semantics. A rule may address an entity or a row, and both kinds run
   through one `RuleEngine`, one transaction per tick and one depth budget.

The crowd target for this work is 10,000,000 rows, measured at ~31 ms per transactional tick for four
int32 columns on the target machine class (see *Crowd tier at ten million* in the remediation plan).
That number is the stop point: if P3.8 cannot demonstrate one authored rule over 10M rows inside that
budget with zero warmed allocations, the claim narrows to "systemic for dozens, dense for millions"
and row-addressed rules stay out of the README.

## Consequences

- `RuleSubject`, `RuleCondition` and `RuleEffect` change shape, and so does the core rules schema. The
  rules content version bumps; a rule that names a property its schema lacks is rejected at load.
- Facts must carry a row address, so `SimulationFact` and its queue gain an address form that is
  serialized into replay artifacts. The replay format already versions its inputs (version 1 today).
- Population column writes are already journaled at page granularity, so row effects roll back
  correctly from the first day. The remaining journal work is cost: a full-buffer edit at 10M rows
  spends ~10 ms per tick copying pages, which dirty-row capture removes.
- Determinism: column values are unmanaged and can be integral, but `double` properties cannot enter
  the row path if cross-platform state hashes are required. P3.6 gates that claim.
- Rendering, input and audio stay outside the framework; a 10M crowd is a simulation number, not a
  frame-rate promise.
