# Save and replay

Save a run after a fixed number of ticks:

```sh
dotnet run --project my-game -- save --out artifacts/run.json --ticks 600
dotnet run --project my-game -- load --save artifacts/run.json
```

`save` writes a checksummed document with:

- the content fingerprint for JSON data;
- the World, populations, modifiers, grants, and saved extensions.

`load` restores it and prints the tick and state hash. A content fingerprint mismatch means
the data changed. The document also records every game module and content module version;
a mismatch rejects the save and names the module and both versions, so bump the version when
a change makes old state unsafe.

## Replay

```sh
dotnet run --project my-game -- replay record --out artifacts/run.replay.json --ticks 600
dotnet run --project my-game -- replay verify --save artifacts/run.replay.json
```

A recording stores the seed, content fingerprint, initial hash, and one hash per tick.
Verification rebuilds the world and compares every tick. A mismatch exits with code 4.
The recorded state includes the module versions, so a behavior change also fails
verification instead of silently reusing stale state. Verification reports `moduleVersion`
and names both versions when the replay and the host disagree, including when either side
composes a module the other does not.

Replay inputs are empty in the template until the game adds input mapping. When it does,
apply the same input at the same tick in the recording and verification paths.

Do not edit a replay artifact by hand. Re-record it.
