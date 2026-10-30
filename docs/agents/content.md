# Author content

A pack has three required files:

- `game.json` names the manifest and the rules and entity files.
- `entities.json` declares entities with tags, numeric properties, and optional visual recipe IDs.
- `rules.json` declares schedules, rules, conditions, and effects.

Edit JSON, then validate:

```sh
systemic validate my-game
```

## Add a scheduled producer

1. Add a target entity with a writable property.
2. Add a schedule whose event fires every N ticks and names a source and target entity.
3. Add a rule on that event. Guard it with a tag on the source.
4. Apply `adjustProperty` to the target.

```json
{
  "id": "demo.pulse",
  "event": "demo.tick",
  "everyTicks": 60,
  "sourceEntityId": "actor.generator",
  "targetEntityId": "resource.energy"
}
```

The schedule and the rule are general primitives. A producer is a pattern, not a framework type.

## Add a toggle

Give an entity the `ui.choice` tag and a `selected` property. Rules can check the tag. A generic list UI binds to entities with the tag. Do not ask for a Talent type.

## Invoke a UI action

A button declares an `action` id, and the game publishes that id as one fact through
`InvokeUiAction`. The id is the fact type, the owning entity is both source and target, and
the button's `amount` travels on the fact, so a rule can scale by the quantity the player
chose.

```json
{ "type": "button", "text": "Buy x10", "action": "ui.buy", "amount": 10 }
```

```csharp
var result = game.InvokeUiAction("hud", "ui.buy", game.EntityId("actor.buyer"));
```

The owning entity is required: a fact aimed at no entity matches no entity-addressed rule.
`result.Handled` reports whether a rule actually matched, not merely that the fact was
published, so a button no rule listens for returns `false`.

## Declare facts and payloads

Add a `facts` array to `rules.json` when a fact should carry typed data. Each fact
names its payload fields and their kinds: `integer`, `number`, `text`, or `boolean`.

```json
{
  "facts": [
    {
      "id": "fruit.combine",
      "payload": [
        { "key": "tier", "kind": "integer" },
        { "key": "label", "kind": "text" }
      ]
    }
  ]
}
```

A rule or schedule attaches values by key:

```json
{
  "kind": "emitEvent",
  "event": "fruit.combine",
  "payload": [
    { "key": "tier", "value": 3 },
    { "key": "label", "value": "pear" }
  ]
}
```

When `facts` is present, every `on` event, `emitEvent`, and schedule event must be
declared. Payload fields are optional per emission, but a supplied key must match a
declared field and its kind. Duplicate keys, unknown fields, and wrong value kinds
are content errors with a path to the offending value.

## Rule bundles and cooldowns

A bundle is a named group of rules that runs only while a tag is present. Give the rule a
`bundle` name and declare the bundle once. A timed tag grant turns it on and its expiry
turns it off, so a status effect does not need a second rule to clean up after itself.

```json
{
  "bundles": [ { "id": "talent.hiking", "enabledByTag": "talent.hiking" } ],
  "rules": [
    { "id": "talent.hiking.boost", "on": "talent.tick", "bundle": "talent.hiking",
      "priority": 0, "cooldownTicks": 30,
      "effects": [ { "kind": "adjustProperty", "subject": "target", "property": "speed", "amount": 1 } ] }
  ]
}
```

`cooldownTicks` is the minimum number of ticks between two matches of the same rule for
the same source and target. A cooled-down rule is skipped, so it costs no effects.

## Conditions

- `hasTag`: subject has a tag.
- `propertyAtLeast`: subject property meets a threshold.
- `expression`: bounded arithmetic over properties and event aggregates.
- `chance`: seeded probability.
- `query`: selects entities by tag, property, radius, or nearest order, then tests an aggregate.

## Query entities

A query condition selects live entities and derives a count, sum, minimum, maximum,
average, or nearest distance. The selector is bounded and deterministic. Results are
ordered by entity ID unless `order` is `nearest`.

```json
{
  "kind": "query",
  "selector": {
    "tag": "housing",
    "property": "built",
    "propertyComparison": "atLeast",
    "propertyValue": 1,
    "origin": "source",
    "radius": 8,
    "order": "nearest",
    "limit": 4
  },
  "aggregate": "sum",
  "aggregateProperty": "built",
  "formula": "query.value >= 7 && query.count > 0"
}
```

Use `comparison` and `value` instead of `formula` for a simple threshold. A formula can
read `query.count`, `query.value`, `query.nearest`, and `query.matched`, plus the normal
expression values such as `tick` and `source.<property>`.

The spatial filter uses the selected origin entity's bounds. An entity without bounds
matches no range query. `limit` applies after ordering. A non-count aggregate over an
empty selection fails the condition, so `query.matched` distinguishes zero from a
real zero sum. An origin or a match without bounds has no distance, so a formula that
reads `query.nearest` fails the condition instead of aborting the dispatch; formulas
that read only `query.count` or `query.value` still run.

## Effects

- `adjustProperty`: change a numeric property. `quantity` picks how: `add` (default), `set`, `multiply`, `min`, or `max`.
- `clearScope`: despawn every entity carrying the given scope tag, for example `scope.run`, and leave the other scopes intact. It addresses the whole scope, so it takes only `tag`; `subject`, `row`, and `factRow` are rejected at load rather than silently ignored.
- `setModifier`: typed, staged, optionally timed modifier. `operation` is `add`, `multiply`, `set`, `min`, or `max`. Add `fromProperty` to make the amount follow another property at read time, which is how an upgrade rank drives its own effects.
- `addTag`, `removeTag`: change an entity's tags.
- `emitEvent`: dispatch another fact immediately.
- `schedule`: dispatch a fact after `delayTicks`. `DispatchDue` on the rule engine runs the facts whose tick has arrived.
- `spawn`: create an entity from an authored template at the fact position, or where the subject stands.
- `despawn`: mark the subject for removal, applied when the world prunes removed entities.
- `transfer`: move up to `amount` of `property` from the subject into the fact target's `targetProperty`. A source that holds less moves what it has.
- `action`: invoke a game action the game registered by name. Actions are domain verbs such as strike or deposit, and every rule can share one.

Row subjects accept property adjustments, tags, and modifiers. Spawn, despawn, transfer, schedule, and actions address entities, because a population row has no world position, no removal lifetime, and no entity to hand an action.

If a mechanic cannot be expressed, add a general primitive or a game action. Do not add a genre noun to the framework.

## Copy a pattern

`samples/pattern-pack` is a content-only reference for nine mechanics. Each is a rule fragment in
`rules.json`, listed in `useFragments`, so a game copies the fragment it needs and changes the IDs.

| Pattern | Composition |
| --- | --- |
| Producer | A schedule emits `pattern.produce`; a rule adds to the target's balance |
| Upgrade | A purchase fact spends currency, raises `rank`, and a modifier follows that rank |
| Talent | A rule bundle enabled by a tag, so one tag adds behaviour and removing it stops it |
| Merge | Two subject entities despawn and a higher tier spawns in their place |
| Prestige | A grant rule pays the meta entity, then `clearScope` removes the run entities |
| Worker | One population row per worker; a system publishes a fact per row and one rule pays it |
| Status | A timed `setModifier` that expires after its duration |
| Unlock | A rule adds a tag, and other rules read that tag |
| Offline | A catch-up fact that grants elapsed production in one dispatch |

The pack has no UI and no assets: it proves the rule vocabulary, not a game. Validate it with
`systemic validate samples/pattern-pack`. Its tests live in `tests/Systemic.PatternPack.Tests`, one
per pattern, each asserting the active and the inactive case.
