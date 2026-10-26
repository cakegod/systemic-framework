# Glow Garden

A small single-screen incremental colony sample. Pods grow automatically; pink Burstlings seek ripe pods and burst them into physical Glow. Blue Gatherers collect bounded loads and return to the central colony. Only returned Glow becomes spendable. Yellow dots below a Gatherer show its actual carried load; shadows and leap height follow worker movement. Rings mark observed pod bursts; green falling dots mark pending seeds and purple dots mark delayed Echo impacts.

## Run

Requires .NET 10; graphics require MonoGame DesktopGL's SDL/OpenGL native support and a display. Gum.MonoGame **2026.5.8.1** supplies packaged default fonts/visuals. All interface controls are built in C# Forms following the MonoGame Gum tutorial, without the Gum editor or a content compiler. Plants, workers, motes and scene art are generated procedurally. The fixed window is 1280×800.

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

All six talents are available immediately, with at most three active. Turn one off to free a slot. Echo schedules a nearby delayed burst; Seedstorm establishes new plants; Magnetism attracts loose Glow; Chain Reaction bursts nearby ripe pods; Living Projectile bounces a worker to another ripe pod; Clone creates temporary Burstling copies after successful combos. Talent buttons show descriptions and active state. The top balance is spendable Glow; lifetime banked counts actual deposits independently of purchases/refunds. The work upgrade shows the current rest between actions in seconds; yield shows Glow per pod. The lower garden panel separates cumulative pod production, loose Glow, carried loads and returned Glow. It also shows average pods/second and banked Glow/second since restart, using simulated game time (zero before time advances). Pausing freezes that time basis; speed changes how quickly it advances.

## Architecture and limits

`GardenGame` uses `GardenHost.Game.Advance(elapsed)` for Systemic fixed-step execution, while headless uses the same host's `Step`. `GardenSession.Restart` disposes and replaces the entire host, clock and World. UI callbacks always resolve the current host; restart neither recreates graphics resources nor retains old model commands. Gum is initialized with V3 default visuals and updated/drawn through `GumService.Default`. Explicit absolute control dimensions and a dark palette keep all controls visible at the fixed window size.

Plants, creatures (including temporary-copy lifetime) and motes live in authoritative Kernel populations with generated typed columns. The public renderer views project those live columns, with stable IDs independent of dense rows. The World owns seeded random draws. Actual bursts, collection and deposits publish facts; authored Core rules select talent reactions by active tags. Commit observers enqueue bounded domain intents, which drain iteratively after commits. UI never forks this model or synthesizes talent effects.

Caps: 1000 hired workers, 128 temporary copies, 64 plants, 12000 motes, 256 delayed impacts and 8192 talent intents per tick; chain depth is three with at most 16 bursts per originating reaction. Excess creation stops at these bounds. Nearest-mote selection remains a linear scan; there is no thousand-worker frame-time or 100k entity performance promise.

Save/load, prestige, Channelers, audio and networking are deferred. Full sample save/rollback is unsupported: progression, ID generators, diagnostics, delayed impacts and transient intent scratch are not save extensions. Population transaction tests demonstrate population-only rollback, not rollback of economy debits or commit-phase domain effects. Restart is the supported fresh-state operation.
