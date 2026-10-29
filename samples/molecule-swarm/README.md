# Molecule swarm

A tower defense against an overwhelming swarm: thousands of molecules pour in from the map border,
follow a flow field toward the nucleus in the middle, and break themselves against whatever the
player has mounted on the plateaus. The run is meant to be lost — the swarm's health grows
exponentially and the number of shelves is fixed.

```sh
dotnet run --configuration Release --project samples/molecule-swarm -- --autoplay
```

Drop `--autoplay` to play it yourself; the bot buys emitters and upgrades, which is a decent way to
watch the game before trying it.

| Input | Action |
| --- | --- |
| Left click (empty shelf) | Place the selected emitter |
| Left click (emitter) | Upgrade it |
| `1` / `2` / `3` | Select gun / mortar / laser |
| `Space` | Start the next wave immediately |
| `P` | Pause |
| `R` | Restart after the nucleus falls |
| `Esc` / `Q` | Quit |

The window is SDL2 plus OpenGL 3.3, imported directly from `libSDL2-2.0.so.0` and `libGL.so.1`, so
the sample is Linux-only like the other direct-GL samples. It needs no content pack: the arena is
baked from `Terrain`, and the 5×7 font is defined in code.

## The game

- **Nucleus**: 100 HP in the middle of the map. A molecule that reaches it is absorbed and takes 1 HP
  (grunt, runner) or 4 HP (brute). At zero the run ends and the result screen reports the wave,
  kills and absorbed molecules.
- **Arena**: a dark plain crossed by four plateaus and four pools. The swarm has to funnel through
  one of the four gates between them, which is what turns a wave into four rivers of molecules.
- **Shelves**: emitters mount on the lit lip along the top of each plateau, on a 32-pixel grid, so
  they stand above the lanes and shoot down into them.
- **Emitters**: gun (cheap, fast, single target), mortar (slow, 46-unit splash, leads its target),
  laser (fast damage tick, hits everything within 11 units of the beam). Each upgrades to level 6 for
  +42% damage and +6% range per level.
- **Economy**: molecules pay 1 energy (4 for a brute); clearing a wave pays 30 + 8×wave. Starting
  energy is 520.
- **Waves**: size grows with `380 + 200 × wave^1.3` up to 6,000, molecule health with `1.28^(wave-1)`,
  spawn rate with `420 + 80 × wave` up to 1,400 a second, and brutes and runners become more common as
  the waves climb. Between waves there is a 4.5-second breather.

## How it works

Simulation (`Simulation/`):

- `Sim.cs` owns the tick order, the waves, the economy and the nucleus.
- `Swarm.cs` is the swarm as a Kernel population: one row per molecule, with `position`, `velocity`,
  `hp`, `speed`, `seed`, `stuck` and `kind` columns declared on a `Population<Molecule>` that
  `World.DefinePopulation` hands out. Rows are world state, so the swarm clones, saves and rolls back
  with the world it is part of; nothing the crowd does lives in a side table.
- `FlowField.cs` is a shortest-path field around the rock, smoothed into one direction per cell and
  sampled bilinearly, so the swarm walks like a fluid instead of a grid.
- `Terrain.cs` is the static arena: distance to the nearest feature is the single source of truth for
  the walkable ground and for the baked image.
- `Emplacements.cs` is the emitters and their shells, with the mount grid and all emitter numbers.
- `Effects.cs` is everything drawn but never simulated: plasma stains, scorch, tracers, blast rings.

The population is touched through scoped access, never through raw storage: `Swarm.Move` takes one
access over the whole population and edits the position, velocity and stuck columns through the
spans the scope hands it; `Swarm.Resolve` applies the damage the emitters staged during the tick,
adds the wave's arrivals, drops the absorbed rows and rebuilds the targeting grid from a second,
read-only access. Everything outside those scopes — turret aiming, the renderer — reads the
snapshot the swarm keeps of the last tick, so no draw or aim code can hold a row it does not own.

Turrets stage damage instead of writing health, and the swarm folds it in during `Resolve`. That
keeps every write to a row inside one scope, and it means a molecule that dies this tick is drawn and
targeted exactly where it stood, then taken out of the grid before the next tick.

Crowd separation is a position correction, not a force: each tick every molecule gives back half of
the overlap with each neighbour inside its spacing radius, so a deep overlap is resolved in a couple
of ticks and the crowd never gains speed from being squeezed. The neighbours come from a grid built
at the top of the tick (and top-up checks against the molecules that arrived since), and the crowd
also walks slower where it is packed, which is what lets a gate drain instead of compressing the
front row into itself.

The rock is a repulsive surface at one sprite radius, so a molecule pressed against a cliff stops
when its body touches rather than sinking in, and a step that would cross the cliff is turned along
the tangent. Anything the crowd does shove into the rock is walked back out along the gradient of the
terrain's distance field, at most a few pixels a tick, and the corrected step becomes the molecule's
velocity so the next tick does not drive into the same cliff again.

Presentation (`Presentation/`):

- `Renderer.cs` is the SDL window, the GL context and the draw order; `GlNative.cs` and
  `SdlNative.cs` are the raw imports.
- `BloomPass.cs` is the bloom: the scene is drawn into a texture, the part bright enough to spill
  light is extracted at a quarter resolution, blurred along each axis three times, and added back
  over the scene. The HUD is drawn after it, so the counters stay crisp.
- `GroundPass.cs` draws the baked terrain, `SpritePass.cs` the point-sprite passes for the swarm,
  plasma, scorch and blasts, `WorldPass.cs` the nucleus, emitters, shots and build preview, and
  `Hud.cs` the health bar, counters, hotbar and banners.
- `ShapeBatch.cs` is one growable buffer of coloured triangles that all of the 2D drawing shares;
  `Glyphs.cs` and `Shaders.cs` are the font and the GLSL.

The molecules are one additive point sprite each: the fragment shader shapes a hot core, a soft body
and a wide, weak halo that is *added* to the dark ground, so the glow is light rather than a painted
disc, and the halo of a packed crowd accumulates into brighter rivers. The point is drawn several
times wider than the collision radius on purpose — the halo is the glow. The kind is the colour:
grunts glow a soft yellow, brutes a deep red, runners a mint green, and each molecule keeps a little
of its own variation around that hue so a crowd does not read as one flat colour.

That light then leaves the sprite: the bloom pass keeps only the bright part of the finished scene,
blurs it at quarter resolution and adds it back. Blooming the *composited* frame rather than the
sprites is what makes a crowd's light spill over the ground and the plateau edges around it, and it
picks up the blasts and the laser beams for free. The threshold sits above the dirt and the pools, so
the ground never fogs up, and the quarter-res buffer keeps the blur to four cheap passes.

`Program.cs` holds the fixed 60 Hz accumulator, the input, and the headless, screenshot and
self-test modes; `Autopilot.cs` is the buyer the demo runs use.

## Verification

Logic, including the click-to-mount mapping, a swarm that has to reach the nucleus with nothing
shooting at it, and a full run of the bot in which no molecule may end up with its body inside the
rock and the crowd may not pile sprites on top of each other:

```sh
dotnet run --configuration Release --project samples/molecule-swarm -- --self-test
```

A full run with the bot at 60 Hz, printing each wave as it starts and how the run ended:

```sh
dotnet run --configuration Release --project samples/molecule-swarm -- \
  --headless --ticks 30000 --seed 41
```

The same command prints its own simulation cost; on the machine this sample was built on it is about
0.3 ms per tick with roughly 700 molecules and 53 emitters.

A rendered frame to inspect (hidden window, no vsync):

```sh
dotnet run --configuration Release --project samples/molecule-swarm -- \
  --screenshot /tmp/molecule.ppm --ticks 620 --seed 41
```
