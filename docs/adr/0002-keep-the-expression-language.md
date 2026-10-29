# ADR 0002 — Keep the expression language

- Status: Accepted
- Date: 2026-10-28
- Gate: D2 in `docs/framework-remediation-plan.md`

## Context

`src/Systemic.Core/Rules/Expressions.cs` and `WorldExpressionScope.cs` are ~965 lines implementing a
bounded expression language: arithmetic, comparisons, boolean operators, `min`/`max`/`clamp`/`floor`,
and the aggregate selectors `count`, `rate` and `event_count` over the rule engine's event history.
`RuleConditionKind.Expression` compiles one such formula into a rule condition.

The gate asked whether anything needs it, with the recommendation to delete it and keep only
`RuleEffectKind` unless a named content feature does.

Two findings decide it:

- No sample content uses it. `grep '"expression"' samples/*/content/*.json` finds nothing; every
  shipped sample conditions on `hasTag` and `propertyAtLeast`.
- Nothing else can express its job. `PropertyAtLeast` compares one property against a constant. The
  aggregate conditions the framework targets — collection per minute versus damage output, a cap on
  procs in a rolling window, a refund of the last ten seconds of income — need `rate()` and
  `event_count()` over a window, and `RuleCondition` has no other form that carries a window.

The talent vocabulary the framework is being validated against (41 patterns in
`tests/Systemic.Integration.Tests/Fixtures/talent-pattern-coverage.json`) names `rules.expression` for
ten of them, including the rate comparison, the staged-combo scaling and the threshold launch speed.

## Decision

Keep the language, frozen, and keep `RuleEffectKind` as the effect vocabulary. P4.3 does not retire it.

"Frozen" means: no new functions, operators or scope values without a named content feature that needs
them, and no per-row evaluation on the crowd path. Expressions are the authorable escape hatch for a
handful of conditioned rules, not a general-purpose scripting layer.

## Consequences

- 965 lines of evaluator stay in the tree and stay a determinism liability: they are `double`-based
  and must not feed row-addressed simulation state without P3.6.
- The evaluator stays off the 10M path. A crowd rule that needs a per-row expression is a P3.7 sink
  candidate ("N rows crossed a threshold") rather than 10M evaluations per tick.
- `ExpressionCompiler` remains the seam a future compaction could replace, so the deletion option is
  not lost if content never adopts it.
