# Phase 1.1: Incremental module audit

The Incremental module is frozen as of Phase 1. No new resources, producers, upgrades,
roles, or choice features are added. This note lists what the module owns, what is worth
moving to general primitives, and which nouns must not appear in the framework.

## Genre nouns in the module

| Symbol | Genre noun | Why it cannot stay in the framework |
| --- | --- | --- |
| `ResourceDefinition` | Resource | A quantity is an entity property, not a framework type |
| `ProducerDefinition` | Producer | A producer is a schedule plus an effect |
| `UpgradeDefinition` | Upgrade | An upgrade is a counter, a cost, and a modifier |
| `ActorRoleDefinition` | Role | A role is a set of tags and capabilities |
| `BuildChoiceGroupDefinition` | Build choice group | A choice is a tagged entity plus generic UI binding |
| `BuildChoiceDefinition` | Build choice | Selection, cost, cooldown, and prerequisites are properties and rules |
| `IncrementalCatalog` | Economy | One content module's compiled view of the above |

These names remain temporarily because porting the samples and the pattern pack is not
finished. They are a migration source, not a model to extend.

## Behaviour worth keeping

| Current behaviour | General primitive or pattern |
| --- | --- |
| Per-interval production | Schedule emits a fact; a rule applies a quantity effect |
| Rate multiplier | Property plus staged modifier |
| Balance capacity clamp | Formula effect or game action |
| Upgrade cost and rank | Transactional purchase action plus a rank property |
| Upgrade reaction event | Fact emitted after a committed purchase |
| Role assignment | Add tags and capabilities to an entity |
| Choice unlock threshold | `propertyAtLeast` condition on the owner |
| Choice selection limit and free choice | Tagged entities plus group properties and rules |
| Choice cooldown and active window | Timed modifiers or tick properties |
| Choice prerequisites and exclusions | Tags plus rule guards |
| Choice UI state | Generic binding over entity properties |
| Saved runtime state | Generic save graph, not module-specific save fields |

## Pattern mapping

| Pattern | Composition |
| --- | --- |
| Producer | Schedule, tag guard, quantity effect, rate property |
| Upgrade | Cost fact or action, rank property, modifier reads rank |
| Talent | Tagged entity, toggle rule, rule bundle gated on the tag |
| Merge | `combine` fact with two entity subjects, despawn and spawn effects |
| Prestige | `reset` fact, scope clear, preserved meta entity, grant formula |
| Worker | Population, columns, tags, a game system; content points at it |
| Offline | Host fast-forward over elapsed ticks; the same rules run |
| Status | Timed modifier, removal on expiry |
| Unlock | Rule adds a tag or property; other rules read it |

## Removal note

The module can be deleted once no sample loads `systemic.incremental` and the pattern pack
covers production, purchase, choice, and role behaviour. Until then:

- Do not add fields or behaviours to the module.
- New mechanics go into core primitives or the pattern pack.
- The module's compiled content is not a second public economy model.
- A game that needs a new economy concept either composes it from core primitives or
  proves the missing primitive with a second mechanic.

## First migration slice

Phase 1.2 starts with facts because every other pattern depends on them. Declared facts
give an event a name, an optional typed payload, and a load-time contract. A merge emits
`combine` with a tier payload. A strike emits `strike` with damage and critical payloads.
Both use the same dispatch path, payload validation, trace, and replay.
