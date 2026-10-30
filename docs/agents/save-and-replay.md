# Save and replay

Save a run after a fixed number of ticks:

```sh
dotnet run --project my-game -- save --out artifacts/run.json --ticks 600
dotnet run --project my-game -- load --save artifacts/run.json
```

`save` writes a checksummed document with:

- the content fingerprint for JSON data;
- the game module ID and version;
- the World, populations, modifiers, grants, and saved extensions.

`load` restores it and prints the tick and state hash. A content fingerprint mismatch means
the data changed. A module version mismatch means the behavior changed. Create a new save or
add a migration; do not silently load different mechanics.

## Replay

```sh
dotnet run --project my-game -- replay record --out artifacts/run.replay.json --ticks 600
dotnet run --project my-game -- replay verify --save artifacts/run.replay.json
```

A recording stores the seed, content fingerprint, module version, initial hash, and one hash
per tick. Verification rebuilds the world and compares every tick. A mismatch exits with
code 4.

Replay inputs are empty in the template until the game adds input mapping. When it does,
apply the same input at the same tick in the recording and verification paths.

Do not edit a replay artifact by hand. Re-record it.
