# Save and replay

Save a run after a fixed number of ticks:

```sh
systemic save my-game --out artifacts/run.json --ticks 600
systemic load my-game --save artifacts/run.json --json
```

`save` writes a checksummed, content-fingerprinted document. `load` restores it and prints the tick and state hash. A fingerprint mismatch means the content changed; create a new save or add a migration.

## Replay

```sh
systemic replay record my-game --out artifacts/run.replay.json --ticks 600
systemic replay verify my-game --save artifacts/run.replay.json
```

A recording stores the seed, content fingerprint, initial hash, and one hash per tick. Verification rebuilds the world and compares every tick. A mismatch exits with code 4.

Replay inputs are empty in Phase 0 because the template has no input mapping. When a game adds input, apply the same input at the same tick in the recording and verification paths.

Do not edit a replay artifact by hand. Re-record it.
