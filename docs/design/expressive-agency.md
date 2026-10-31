# Design bar: expressive agency through systemic emergence

The framework exists to make games where players can author their own play. The player
should feel that they are not merely becoming stronger, but becoming something of their own
choosing.

The developer does not hand the player a finished strategy. The developer supplies general
rules, pressures, verbs, constraints, and legible feedback. The player combines them into
strategies, goals, and identities that were never written down as a branch. That is the
point of a systemic game.

The design aims for three properties that must hold together:

- **Systemic:** the same general rules apply across many objects, resources, and
  situations. A rule is not a named one-off outcome.
- **Synergetic:** mechanics modify each other. A choice changes what other choices and
  systems do, not only one number.
- **Emergent:** combinations produce coherent behavior that is not explicitly authored as
  a separate branch. The player can discover, explain, and reproduce it.

The framework can enforce the first and make the other two possible. It cannot guarantee
synergy or emergence; those are properties of a well-designed game, discovered in play.

Emergence is not randomness, hidden scripting, or a developer pretending not to have
designed the system. It is the visible consequence of simple rules interacting. The
developer authors the grammar; the player writes strategies in it.

Gnorp is a reference for some of this interaction, not a template. Do not copy its
mechanics because they exist. Use them to test whether the composition layer can express a
distinct choice, then design original systems and choices.

A choice is not good because it is balanced. It is good when it opens a distinct, desirable
future, makes the player's identity visible, and creates new interactions for the player to
explore.

## The test for every choice

Every talent, upgrade, merge branch, prestige option, or unlock must answer these questions.

1. **Identity.** What kind of player does this let me become? "More damage" is not an
   identity. "I fight at low progress and turn risk into tempo" is.
2. **Distinct future.** What can I do after choosing this that I could not do before? The
   answer must be a new action, strategy, target, resource loop, or constraint, not only a
   larger number.
3. **Meaningful trade-off.** What does this make harder, slower, riskier, or impossible?
   If there is no cost, opportunity cost, or changed failure mode, the choice is probably
   not a choice.
4. **No dominant option.** There must be at least two situations where a different option
   is the better expression of a different playstyle. "Always take this" is a failed
   choice.
5. **Synergy.** The choice must interact with at least two existing mechanics in a way the
   player can discover and exploit. It should create combinations, not sit beside them.
6. **Emergence.** Do those interactions produce at least one strategy or outcome that
   exists only because the parts meet? If every combination is a named branch, the game is
   a menu, not a system.
7. **Expression.** The choice must support at least two recognizable playstyles. It should
   not collapse every build into the same loop with different numbers.
8. **Experiment.** The player should be able to understand the choice, try it, and change
   direction without the game becoming unplayable. The option should invite a test, not
   require a wiki.

A choice can be strong. It cannot be strictly better. A choice can be simple. It cannot be
only a number. A game can be balanced. It cannot consist only of prewritten answers.

## Player-authored strategies

The developer supplies the possibility space. The player supplies the plan.

The developer provides:

- rules with legible causes and consequences;
- several pressures that pull in different directions;
- general tools that interact across systems;
- feedback that makes a discovered interaction visible;
- enough runway after a commitment for a strategy to develop;
- room to recover, change direction, and experiment.

The player provides:

- which tools to combine;
- what to optimise, risk, or protect;
- the order and timing of commitments;
- the goal that makes the build coherent;
- the identity the build expresses.

The authorship question is not whether the player picks one of the developer's prewritten
identities. It is whether a player can describe a coherent strategy the developer never
wrote down, and whether the game supports it through general rules rather than a special
case added after the fact.

Authored choices still matter. They are seeds, constraints, and invitations. They are not
the whole strategy space.

## What the framework enforces

The framework's enforcement is structural. It provides one execution model, general state
and mutation primitives, deterministic time, explicit access declarations, and save/replay
over the same composition. It keeps genre APIs and a second behavior language out of the
framework, so a game cannot quietly turn every combination into a framework feature.

The project then enforces the game-side rules: no per-combination handler, no hidden state,
no named branch standing in for an interaction, and a host test for every claimed synergy.
Those rules keep the game systemic in structure.

What none of that can enforce is that the result is interesting. Synergy and emergence are
outcomes of the game's design and the player's discovery. They are confirmed by playtests
and combination tests, not promised by the framework.

## Anti-patterns

Reject these as the whole choice:

- `+20% damage`, `+1 resource per tick`, or `-10% cost` with no changed decision.
- A strict upgrade with no downside, opportunity cost, or build requirement.
- An option that is always correct regardless of the player's strategy.
- A choice that only changes the speed of the same loop.
- A choice that is invisible in play and only appears in a tooltip.
- A choice that requires a new framework genre type to exist.
- A scripted branch for every pair of mechanics, disguised as a system.
- A choice tree where every viable strategy is enumerated in content.
- A special case added to make one interaction work instead of generalising the rule.
- A synergy that exists only in a tooltip, a scripted animation, or a one-off event.

A number can be part of a choice. It cannot be the choice. A branch can be part of a game.
It cannot be the whole game.

## What a good choice looks like

A good choice changes at least one of these:

- **Target selection:** what the player attacks, builds, collects, or protects.
- **Timing:** when the player acts, waits, banks, or spends.
- **Risk:** what the player risks, what can fail, and how failure is recovered.
- **Resource conversion:** what is produced, consumed, stored, or wasted.
- **Space:** where the player stands, builds, routes, or defends.
- **Tempo:** whether the build is early, explosive, steady, or recursive.
- **Constraint:** what the player gives up or can no longer do.
- **Fantasy:** what the player is pretending to be while making decisions.
- **Interaction:** which existing rules, resources, or systems it changes.
- **Composition:** what third behavior appears when two mechanics meet.
- **Discovery:** how the player notices the interaction through play.

The last three are what turn a list of choices into a systemic game.

## The 41 reference talents

The 41 wiki talents are an expressiveness stress test and a design audit, not a quota of
formulas. Several are raw stat increases. Those are not automatically acceptable. They are
seeds for the mechanics, not the complete strategy space.

For each talent, write a one-paragraph design note:

- the identity it expresses;
- the new decisions it opens;
- the cost or trade-off;
- at least two synergies;
- one interaction whose result follows from the rules but is not written into the content
  as a named outcome;
- the playstyles it supports;
- the situation where a competing choice is better;
- one combination to watch for that the rules permit beyond the option's stated purpose.

If the note cannot be written, redesign the talent into a distinct choice or replace it with
one that passes. The gate is **41 meaningful choices**, not 41 copied percentage bonuses,
plus evidence that the choices combine into play the list did not spell out.

Passing the audit is necessary, not sufficient. The final evidence is a playtest where a
strategy the content did not name works, and a player can explain why.

## Mechanical acceptance

A talent is accepted only when all of these are true:

- it is a data record plus a behavior function;
- it passes the eight-question test;
- it has a host-level positive scenario and a negative scenario;
- one of its synergies is exercised in a host scenario through general rules, without a
  per-combination handler;
- it survives save/load and replay;
- it has no flag without a consumer and no test-only property;
- it does not require a framework genre type;
- it changes play in a way a player can see, not only a number in a tooltip.

## Review questions

Use these in every content review:

1. What does this choice let the player stop doing, start doing, or do differently?
2. What does it make worse?
3. Which other mechanics does it combine with?
4. What new behavior appears only when those mechanics meet?
5. What is the competing choice in the same situation?
6. Would a player describe this as a strategy or as a stat?
7. If this were removed, would the build feel different?
8. Is the choice legible from play, or only from documentation?
9. Did we need a special case to make the combination work? If yes, generalise the rules or
   cut the special case.
10. Could a player find a coherent strategy here that is not written down in our content?

If the answer to question 6 is "a stat," the choice is not done. If every answer to question
10 is already enumerated in a design document, the game is not systemic enough yet.
