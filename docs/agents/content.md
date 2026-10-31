# Author content

> **Status (2026-10-31).** Behavior is C# systems and commands. JSON is data. The JSON rule
> engine is legacy and is deleted after the samples migrate. See
> [ADR 0004](../adr/0004-code-first-simulation.md).

A pack has two required files:

- `game.json` names the manifest, tick rate, and entity file.
- `entities.json` declares entities with tags, numeric properties, links, and optional
  visual recipe IDs.

Optional data lives in `ui.json`, `visuals.json`, and `presentation.json`. Module-specific
tuning documents are declared by the module that owns them.

`rules.json` is a legacy reference format. Do not add one to a new pack, and do not add
conditions, effects, schedules, expressions, or facts to an existing one. Omit the `rules`
property from `game.json` so the loader does not read a rule file at all; a pack that
declares no rules and no schedules builds a host without a rule engine, and its save carries
no rule-history state.

Validate after every edit:

```sh
systemic validate my-game
```

## Entities

An entity is a data row plus tags and properties:

```json
{
  "id": "actor.generator",
  "kind": "actor",
  "tags": ["producer", "ui.choice"],
  "properties": { "rate": 1, "balance": 0, "selected": 0 },
  "visualRecipeId": "icon.generator"
}
```

Tags are behavior gates and UI selectors. Properties are authoritative state. A game
system reads and writes them through the World. Do not add framework genre types to describe
what an entity is; use tags, properties, and game code.

## UI actions are commands

A button declares an action id. The game registers a command handler for the command type
and binds the declared action id to a command factory before the game builds. The UI does not
publish a rule fact.

```json
{ "type": "button", "text": "Buy x10", "action": "buy", "amount": 10 }
```

```csharp
builder.AddCommand<BuyCommand, BuyHandler>();
builder.BindUiAction<BuyCommand>("buy", action =>
    new BuyCommand(action.OwnerEntityId, action.Amount));
```

`InvokeUiAction` resolves the clicked node's amount and runs the command. A bound action
without a registered command handler fails at build, and so does a binding the loaded UI
modules cannot validate. A layout action with no binding is still published as a fact, so
presentation and telemetry observers see it; it returns `Handled = false` because no rule
matched, not because the host dropped it.

A command validates its cost and target, then mutates the World. A command that cannot
afford or allow the purchase calls `context.Reject("insufficient.funds")` and returns; the
host rolls the command back and reports `Rejected` with the reason instead of committing.
The same command can be invoked by a test, a keyboard binding, or the UI once its action
binding is registered. Every command, accepted or rejected, is appended to the host's
command journal with its type, serialized payload, last completed tick, and sequence. The
journal is saved and keeps the most recent 4096 records, so a save records the input that
produced it without growing without bound.

## Events

Events are typed notifications for presentation, telemetry, and decoupling. A system queues
one with `context.EmitEvent(...)`; the host dispatches the tick's queue after its deferred
facts, in system registration order then queue order, inside the tick's transaction. A
rolled-back tick discards its queue. The outer program can also emit with
`game.Emit(...)`.

Register handlers on the builder; they run in registration order inside one World
transaction and receive a read-only World view plus registered services. Simulation
mutations belong in commands and systems, not event handlers.

```csharp
builder.AddEvent<Harvested, HarvestPresentation>();
context.EmitEvent(new Harvested(amount: 7));
```

## Game behavior

Behavior is game-side C#. A game module registers systems and commands. Every choice in its content must pass the
[expressive-agency test](../design/expressive-agency.md):

```csharp
public sealed class GameModule : ISystemicGameModule
{
    public string Id => "game";
    public int Version => 1;

    public void Compose(SystemicGameBuilder builder)
    {
        builder.AddSystem(new EconomySystem());
        builder.AddSystem(new ChoiceSystem());
        builder.AddCommand<SelectChoice, SelectChoiceHandler>();
    }
}
```

A system is a stateless step function:

```csharp
public sealed class EconomySystem : ISystemicSystem
{
    public string Id => "game.economy";

    public IReadOnlyList<SystemAccess> Access => [SystemAccess.WriteWorld];

    public void Step(SystemicSystemContext context)
    {
        var world = context.GetWritableWorld();
        foreach (var producer in world.EntitiesWithTag("producer"))
        {
            // Read authoritative properties, apply the interval, write the result.
            // No system fields. State lives in the World.
        }
    }
}
```

Keep timers, cooldowns, ranks, selection, and counters in entity properties or population
columns. A system field is a bug unless it is a derived cache rebuilt after load.

## Data versus behavior

Data defines identity, cost, tags, descriptions, tuning, and visuals. Code defines what a
selected choice does. Gnorp is one reference for the mechanical stress test, not a design
to copy. Use it to test expression, then design choices for the game you are actually making.

```json
{
  "id": "talent.default.motivation",
  "group": "default",
  "cost": 1,
  "tags": ["ui.choice", "talent.default.motivation"],
  "description": "Low progress: faster abilities, but no healing."
}
```

```csharp
static void Motivation(in TalentContext context)
{
    if (context.Runner.Progress >= 0.5)
    {
        return;
    }

    // A redesign target: low progress changes risk and timing, not just damage.
    context.Runner.AddTag("cannot-heal");
    context.Runner.AddModifier("abilityCooldown", ModifierOperation.Multiply, 0.5, ticks: 1);
}
```

The game owns the choice context, choice system, and behavior table. The framework does not
know what a talent is.

## Designing choices

A choice must express a playstyle, open a distinct future, carry a trade-off, and interact
with at least two mechanics. A number can be part of it; a number cannot be the whole
choice. "+20% damage" is an upgrade. "Low progress gives faster abilities but disables
healing" is a choice.

Design interaction, not a menu. General rules should combine so the player can find a
coherent strategy the design did not enumerate. Do not encode every pair of mechanics as a
named branch; if a combination needs a special case, generalise the rule or cut the case.
The framework can enforce this structure. It cannot make the combination interesting; that
is confirmed by tests and playtests.

Every talent gets a design note, a competing-choice comparison, and two synergies. If the
note cannot say what the player becomes, what becomes possible, what becomes harder, what
new behavior appears when mechanics meet, and when another choice is better, the talent is
not done. See [expressive agency and emergence](../design/expressive-agency.md).

## Pattern library

Reusable patterns are game-side C# modules, not framework APIs. Keep their names neutral:

| Pattern | Game-side composition |
| --- | --- |
| Producer | An interval system that adds a quantity from a rate |
| Upgrade | A costed command that increments a rank; modifiers read the rank |
| Talent | A data record plus a behavior function gated by a selected tag |
| Merge | A command over two same-tier entities that despawns both and spawns the next |
| Prestige | A command that clears one scope, preserves another, and grants a currency |
| Worker | A population with tags and columns, advanced by a system |
| Status | A timed modifier that expires |
| Unlock | A command or system adds a tag or property; other code checks it |

The pattern library may be opinionated. The framework must not be.

## Determinism

- Authoritative state uses integer or fixed-point math.
- Do not use ambient time, unseeded randomness, or unordered iteration.
- Keep stable entity order; use the deterministic World queries.
- Declare every column and World access a system uses.
- Keep hot systems allocation-free.
- Save and replay carry a game module and content module ID and version. A behavior or
  schema change bumps the version, and an old save that no longer matches is rejected with
  the module and both versions named.

## Assets

Visual recipes are data. A game references recipe IDs, not PNG paths. Validate and render:

```sh
systemic assets validate my-game
systemic assets sheet my-game --out artifacts/assets.png --scale 8
systemic assets atlas my-game --out content/atlas.png --manifest content/atlas.json
```

No hand-drawn PNG except the font.
