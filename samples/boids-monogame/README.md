# MonoGame boids sample

Run from the repository root:

```sh
dotnet run --configuration Release --project samples/boids-monogame/Boids.MonoGame.csproj
```

Pass a seed to reproduce a run:

```sh
dotnet run --configuration Release --project samples/boids-monogame/Boids.MonoGame.csproj -- --seed 41
```

Run a warm, uncapped benchmark and print simulation and rendering CPU time:

```sh
dotnet run --configuration Release --project samples/boids-monogame/Boids.MonoGame.csproj -- --seed 41 --benchmark-seconds 20
```

Run a deterministic headless simulation benchmark without creating a graphics device:

```sh
DOTNET_TieredCompilation=0 dotnet run --configuration Release --project samples/boids-monogame/Boids.MonoGame.csproj -- --headless-benchmark --seed 41 --warmup-ticks 200 --ticks 500
```

Check that the GPU output contains rendered boids:

```sh
dotnet run --configuration Release --project samples/boids-monogame/Boids.MonoGame.csproj -- --seed 41 --verify-render
```

The sample has 100,000 boids, three authored species, six food patches, and one predator. Flock configuration, species, food, and predator remain authored World entities. The World owns the authoritative boid population as typed position, velocity, energy, and species columns; position, velocity, and energy use double buffers. `BoidFlockSystem` owns the spatial grid and feeding arrays as reusable algorithm scratch and advances the population at the authored 30 Hz. Boid and predator energy drive feeding and hunting.

Population column spans and memory views are borrowed from their owning World. Do not retain them across World clone/adoption or population structural changes. The flock exposes current read-only spans for immediate renderer consumption; the MonoGame renderer copies instance data during `Draw` and does not keep a column view. Systems write population state through `EditRange`/`EditNextMemory` so the kernel can preserve clone and transaction semantics. `SystemicGameBuilder` registers the population save extension before the first simulation step. The flock extension also saves the predator's current food target by stable food entity ID, preserving its ongoing hunt across save/load even when another patch becomes richer.

The strict headless command loads the authored content and runs the production scheduled `BoidFlockSystem`. It records every measured tick, fails if any tick is at or above the limit, and fails if the process allocates during the measured interval. `--repeat 3` rebuilds the scenario for each run; per-tick CSVs receive `-run1`, `-run2`, and `-run3` suffixes.

```sh
dotnet run --configuration Release --project samples/boids-monogame/Boids.MonoGame.csproj -- --headless-benchmark --strict-budget --seed 41 --warmup-ticks 1000 --ticks 500 --workers 4 --max-tick-ms 5 --repeat 3 --tick-log artifacts/boids.csv
```

The benchmark suite under `benchmarks/Systemic.Benchmarks` isolates the spatial grid, range scheduler, population transaction journal, and a 50,000-product/1,000-reactive-actor chain workload. Run its strict population-producer, indexed-query, and reactive-chain gates with `dotnet run -c Release --project benchmarks/Systemic.Benchmarks -- --gate --scenario all --warmup-ticks 1000 --ticks 500 --repeat 3 --workers 1 --max-tick-ms 5 --tick-log artifacts/kernel.csv`. The `producer-entity` diagnostic is available for the dynamic `DataEntity` authoring path. Run component benchmarks with `dotnet run -c Release --project benchmarks/Systemic.Benchmarks -- --filter '*PopulationSpatialGridBenchmarks*'`.

The latest managed-runner check capped an AMD EPYC 9V74 container at two CPUs to approximate the target's core count. At 100,000 boids and four workers, three 1,000-warm-up/500-measured runs averaged 8.487, 8.561, and 7.874 ms/tick, with maxima of 45.918, 39.492, and 46.555 ms. Every run failed the strict 5 ms limit; all measured ticks allocated zero bytes. A same-runner worker comparison averaged 13.393, 9.447, and 8.034 ms/tick with 1, 2, and 4 workers. This is an EPYC CPU-time cap, not an i5-6200U or a model of its cache and clock behavior, so only testing that laptop can establish the requested target. The generic grid candidate microbenchmark measured 1.726 ms per 100,000-row lazy early-exit scan versus 4.860 ms when materializing up to 12 candidates first; both paths allocated zero bytes. These component numbers isolate one kernel operation and do not represent the complete boid tick.

The strict kernel gate, with the same two-CPU cap, passed the 50,000-row population producer path (mean 0.029 ms, maximum 0.067 ms) and the 50,000-entity indexed batch query (mean 0.259–0.329 ms, maximum 0.380–1.273 ms), with zero allocations throughout. The combined 50,000-product spatial query plus 1,000-actor reaction path averaged 3.166–3.220 ms and had p95 below 3.5 ms. One of three runs had two ticks over 5 ms; the other two stayed below the every-tick limit. All three allocated zero bytes. No i5-6200U-class device was available for a target-hardware result.

The game starts in a 960×640 window. Press F11 to toggle fullscreen.

The renderer publishes one 20-byte instance per boid when the simulation tick changes, expands them into a reusable vertex array, and uploads that array only when the simulation changes. One indexed draw call renders the particle quads; food and predator markers use `SpriteBatch`. The sample uses MonoGame's built-in `BasicEffect`, so it needs no custom shader build step.

The benchmark runs 60 warm-up frames, disables frame pacing, and measures the requested duration. It reports total FPS, average time inside `SystemicGame.Advance` per update, average time inside calls that advanced logical ticks, and draw submission CPU time. Total FPS includes the game loop and presentation. Results depend on the GPU driver and display setup.
