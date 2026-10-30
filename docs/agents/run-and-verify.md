# Run and verify a game

Validate content with the CLI:

```sh
systemic validate my-game
```

The CLI owns content validation and asset generation. The game executable owns the host,
because simulation behavior is C# systems and commands.

## Headless run

```sh
dotnet run --project my-game -- --headless --ticks 600
```

The game host prints the final tick, entity count, and state hash. A focused game exposes
the same run through a small test or command so the result is reproducible.

## Verify

```sh
dotnet run --project my-game -- verify --ticks 600
```

`verify` builds the same world twice from the same seed, compares the hashes, saves and
restores one run, and reports whether the restore matches.

Use `--seed` to reproduce a run. The seed is part of the state hash contract.

```sh
dotnet run --project my-game -- --headless --ticks 600 --seed 42
```

A verification failure exits with code 4. Do not change the expected hash to make it pass.
Find the state that differs.

## Balance pass

```sh
dotnet run --project my-game -- simulate --seconds 3600 --csv artifacts/simulate.csv
```

`simulate` fast-forwards the same systems and commands. The CSV has one row per entity
property per sampled second. Read it to tune rates and costs without playing.

## Content-only packs

A pack with no game behavior still validates and can be inspected. It has no simulation
loop. Do not add a `rules.json` to make a content-only pack run; add a game module.

## Legacy path

The old `systemic run`, `simulate`, and `verify` commands operate on the frozen JSON rule
engine. They are not the target path. Port the game to a module before adding behavior.
