# Glow Garden

A single-screen incremental colony sample on a 1920×1280 garden. Noise-generated thickets break up the ground into organic shapes that workers must walk around, and pods grow scattered across the whole map rather than ringed around the colony. Pink Burstlings scout for ripe pods and burst them into physical Glow; blue Gatherers forage on foot, following the scent trails that bursts and pickups leave between food and home, carrying bounded loads back to the colony. Workers perceive only their local surroundings: with nothing in sight they walk their own heading, and only after a long fruitless search do they commit to a distant target. Only returned Glow becomes spendable. Yellow dots below a Gatherer show its actual carried load; shadows and leap height follow worker movement. Burst rings, Echo sparks, seed flashes and combo flashes are Presentation-module cues driven by authored fact timelines, not by renderer polling. Small green and purple dots still mark pending seeds and delayed Echo impacts.

## Run

Requires .NET 10; graphics require MonoGame DesktopGL's SDL/OpenGL native support and a display. Gum.MonoGame **2026.5.8.1** supplies packaged default fonts/visuals. All interface controls are built in C# Forms following the MonoGame Gum tutorial, without the Gum editor or a content compiler. Plants, workers, motes, thickets and scene art are generated procedurally. The fixed window is 1280×800; the world is drawn at 0.42 scale into the garden panel, with worker, pod and mote sprites sized for that zoom.

```sh
dotnet run --project samples/glow-garden -c Release
dotnet run --project samples/glow-garden -c Release -- --headless --ticks 3600 --seed 42
dotnet run --project samples/glow-garden -c Release -- --verify-render --seed 42 --screenshot /tmp/glow-garden.png
dotnet test tests/Systemic.GlowGarden.Tests -c Release
```

`--headless` runs finite seeded autoplay on the actual host: it selects Seedstorm, Magnetism and Echo, hires alternating workers and buys growth using only available Glow. It reports real bursts, deposits, loose/carried Glow and counts, and exits nonzero if no production or deposit occurred. Short tick counts can intentionally fail this workflow. `--ticks` accepts 1..100000 and requires headless mode; `--seed` accepts any 32-bit integer. Unknown, duplicate, missing and conflicting arguments fail with usage text. The default seed is 42 and headless duration is 3600 ticks.

`--verify-render` selects Echo, Seedstorm and Magnetism, runs 480 frames at 4×, checks actual production/deposits and color diversity in separate garden and UI regions, optionally saves the rendered frame as PNG, and exits. This detects blank rendering; manual screenshot and pointer checks remain necessary for readability and interaction. It is a finite graphics smoke, not a performance benchmark. In a Linux virtual display use e.g. `DISPLAY=:99 LIBGL_ALWAYS_SOFTWARE=1 SDL_AUDIODRIVER=dummy` with a provisioned display server.

## Controls

Every action has a labeled Gum button. Tab/Shift+Tab moves focus; Enter activates a focused button. Space is reserved for global pause.

| Keys | Action |
| --- | --- |
| B / G | Hire Burstling / Gatherer at shown cost |
| 1 / 2 / 3 | Burstling work rate / Gatherer speed / capacity |
| 4 / 5 | Pod growth / yield |
| X | Toggle single purchase / buy maximum affordable ranks |
| C / M | Refund exactly the Creatures / Magic & Growth tree's investment |
| Q / W / E | Toggle Echo / Seedstorm / Magnetism |
| A / S / D | Toggle Chain Reaction / Living Projectile / Clone |
| Space / F | Pause or resume / cycle 1×, 2×, 4× speed |
| R / Esc | Fresh restart with the same seed / quit |

Hiring prices rise with worker count. Repeatable upgrades cap at rank 25. Work rate reduces Burstling cooldown from 30 ticks to at least 3; speed starts at 3 pixels/tick with +0.3 per rank; capacity starts at 3 Glow with +1 per rank. Growth starts at one pod per 240 ticks, increasing by 12% of base per rank; yield starts at 8 motes and increases by 2 per rank. Each upgrade displays its next price. Bulk buying stops at affordability or rank cap. Each refund returns that tree's exact accumulated costs once; workers, other-tree ranks, talents and carried loads survive. Refunds never reset the lifetime banked counter.

All six talents are available immediately, with at most three active. Turn one off to free a slot. Echo schedules a nearby delayed burst; Seedstorm establishes new plants; Magnetism attracts loose Glow; Chain Reaction bursts nearby ripe pods; Living Projectile bounces a worker to another ripe pod; Clone creates temporary Burstling copies after successful combos. Talent buttons show descriptions, active state and how many reactions the authored rules have selected. The top balance is spendable Glow; lifetime banked counts actual deposits independently of purchases/refunds. The work upgrade shows the current rest between actions in seconds; yield shows Glow per pod. The lower garden panel separates cumulative pod production, loose Glow, carried loads and returned Glow. It also shows average pods/second and banked Glow/second since restart, using simulated game time (zero before time advances). Pausing freezes that time basis; speed changes how quickly it advances.

## Architecture and limits

`GardenGame` uses `GardenHost.Game.Advance(elapsed)` for Systemic fixed-step execution, while headless uses the same host's `Step`. `GardenSession.Restart` disposes and replaces the entire host, clock and World. UI callbacks always resolve the current host; restart neither recreates graphics resources nor retains old model commands. Gum is initialized with V3 default visuals and updated/drawn through `GumService.Default`. Explicit absolute control dimensions and a dark palette keep all controls visible at the fixed window size.

Plants, creatures (including temporary-copy lifetime) and motes live in authoritative Kernel populations with generated typed columns. The public renderer views project those live columns, with stable IDs independent of dense rows. The World owns seeded random draws. Actual bursts, collection and deposits publish facts; authored Core rules select talent reactions by active tags. Fact observers enqueue bounded domain intents, which drain at the end of the step outside rule dispatch. UI never forks this model or synthesizes talent effects.

Per-worker work is a scheduled system. Each tick rebuilds `PopulationSpatialGrid2D` indexes for plants and motes, then decides worker movement over disjoint row ranges with `ForEachRange`; the pass reads shared grids and writes only row-indexed decision arrays, so it publishes nothing and changes no population structure. Bursts, mote pickups and deposits then resolve serially in row order, which keeps first-come collection and stable fact order regardless of worker count. Nearest targets use an expanding radius query: a hit inside the queried radius is the true nearest because anything outside it is farther away. Magnetism also uses a grid but runs per mote, so its cost grows with motes multiplied by nearby creatures.

Terrain is generated once per seed: two octaves of value noise become an 8 px walkability field, and a flood fill from the colony carves a corridor into any pocket it cannot reach, so the free space is always one connected region and no pod is ever sealed off. Workers test the cell they step into and slide along the edge; when the direct line stays blocked they commit to one side of the thicket and follow the boundary, and after 90 ticks of that they change tack instead of pressing on. Rendering bakes the same field into a quarter-resolution texture with linear filtering, so the visual edge is smooth even though movement is cell-exact.

Foraging is local, not omniscient. A worker perceives ripe pods or loose Glow only inside `garden.worker.scoutRadius`; with nothing in sight it follows a trail or walks its stored heading, turning on its own cadence, and only after `garden.worker.patience` fruitless ticks does it commit to the full-map nearest query, staying committed until it arrives. Bursts and pickups stamp `GardenScent`, a coarse float grid whose trail tapers from the food end toward the colony, so climbing the gradient always means moving toward food. The grid decays by `garden.trail.decay` each tick and values below `garden.trail.threshold` are ignored, so a trail recruits workers onto fresh food and fades once the pod has been stripped. Scouting turns use deterministic per-row noise rather than the World draw stream, because the decision pass runs in parallel; worker counts therefore never change the result.

Talent and worker tuning is content, not code: delay, expiry, radius, offset, pull, burst limit, clone lifetime, the three talent slots and the forage knobs (scout radius, patience, trail strength, threshold and decay) are properties on the `garden` entity in `entities.json`, read with clamping at use. `garden.seedstorm.offset` is now the minimum distance between a seed and its parent pod, because seeds otherwise land anywhere in the garden. Each talent rule also owns its accounting through an `adjustProperty` effect on a `garden.talent.<name>.uses` property, so removing a rule removes both the reaction and its counter. The per-tick `garden.tick` pulse is an authored schedule in `rules.json`, not a system publish. Spatial consequences (moving workers, bursting pods, spawning motes) still belong to the domain system because Core rules cannot address populations; content owns selection, parameters, accounting and cadence.

Feedback comes from the optional Presentation module. `presentation.json` declares four timelines and seven bindings for bursts, Echo, Seedstorm and combo reactions. Emitted facts use a `garden.impact` entity as their target; the domain moves that entity's spatial bounds before publishing, so the module captures the real impact position at dispatch time (even when several facts share a step) and emits commit-gated cues. The MonoGame host dequeues cues, samples each timeline at the current age for position, scale, opacity and visual state, rasterizes the referenced recipe once per state and draws it with the timeline's particle count. Magnetism emits no cue because it reacts every tick; its feedback is the visible mote motion.

Caps: 1000 hired workers, 128 temporary copies, 64 plants, 12000 motes, 256 delayed impacts and 8192 talent intents per tick; chain depth is three with at most 16 bursts per originating reaction. Excess creation stops at these bounds.

The map grew to four times its old area and gained thickets, so trips are longer still: the same 3600-tick headless run bursts about 200 pods and banks about 550 Glow, against roughly 1030 and 3380 when six pods ringed a 960×640 colony and workers beelined at the nearest target. Nine pods and nine founding workers keep the early game moving, and `garden.worker.scoutRadius`/`patience` are tuned for the larger map. Loose Glow still accumulates faster than a small crew can carry it; that is the intended cost of foraging, and hiring or capacity upgrades are the answer.

Scaling is bounded by per-worker candidate work, not by population iteration. On one development machine (Release), temporarily raising the caps to 50k workers, 1k plants and 12k motes cost about 15 ms per tick with the scheduled loop on four workers, about 35 ms pinned to one scheduled worker, and about 55 ms in the legacy serial system; before grid targeting the same load took about 7.6 s per tick. The shipped caps stay at 1000 workers and 64 plants. Magnetism at 50k workers and 12k motes still costs over a second per tick because every mote queries the creature grid, so extreme-density talent use is not supported and no 60 Hz claim is made at that load.

Save/load, prestige, Channelers, audio and networking are deferred. Full sample save/rollback is unsupported: progression, ID generators, diagnostics, delayed impacts, presentation cues and transient intent scratch are not save extensions. Population transaction tests demonstrate population-only rollback, not rollback of economy debits or commit-phase domain effects. Restart is the supported fresh-state operation.
