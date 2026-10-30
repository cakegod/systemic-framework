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
real zero sum.

## Effects

- `adjustProperty`: change a numeric property. `quantity` picks how: `add` (default), `set`, `multiply`, `min`, or `max`.
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
