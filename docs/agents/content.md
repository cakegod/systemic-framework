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

## Conditions

- `hasTag`: subject has a tag.
- `propertyAtLeast`: subject property meets a threshold.
- `expression`: bounded arithmetic over properties and event aggregates.
- `chance`: seeded probability.

## Effects

- `adjustProperty`: add to a numeric property.
- `setModifier`: typed, staged, optionally timed modifier.
- `addTag`: add a tag to an entity.
- `emitEvent`: dispatch another fact.

If a mechanic cannot be expressed, add a general primitive or a game action. Do not add a genre noun to the framework.
