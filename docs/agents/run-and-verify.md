# Run and verify a pack

Create, run, and check a game:

```sh
systemic new /tmp/demo
systemic validate /tmp/demo
systemic run /tmp/demo --ticks 600 --json
systemic verify /tmp/demo --ticks 600 --json
```

`run` steps the headless host and prints the final tick, entity count, and state hash. `verify` builds the same world twice from the same seed, compares the hashes, saves and restores one run, and reports whether the restore matches.

Use `--seed` to reproduce a run. The seed is part of the state hash contract.

```sh
systemic run /tmp/demo --ticks 600 --seed 42 --json
```

A verification failure exits with code 4. Do not change the expected hash to make it pass. Find the state that differs.

## Balance pass

```sh
systemic simulate /tmp/demo --seconds 3600 --csv artifacts/simulate.csv
```

`simulate` fast-forwards the same rules. The CSV has one row per entity property per sampled second. Read it to tune rates and costs without playing.
