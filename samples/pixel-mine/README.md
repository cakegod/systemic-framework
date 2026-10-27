# MonoGame pixel mine

A playable take on 10 Million Pixels: a mine of exactly ten million pixels, eroded by bouncing balls. Dust from mined rock pays for more balls and upgrades; prisms unlock the smash and drill balls; one golden pixel is hidden deep in the rock and ends the run.

```sh
dotnet run --configuration Release --project samples/pixel-mine/PixelMine.csproj
```

Pass a seed to reproduce a mine:

```sh
dotnet run --configuration Release --project samples/pixel-mine/PixelMine.csproj -- --seed 41
```

Run the deterministic autoplay headless (no graphics device) and print a summary:

```sh
dotnet run --configuration Release --project samples/pixel-mine/PixelMine.csproj -- --headless --ticks 12000 --seed 41
```

Check that the GPU output contains a rendered mine, HUD, and balls; optionally save the frame:

```sh
dotnet run --configuration Release --project samples/pixel-mine/PixelMine.csproj -- --verify-render --screenshot /tmp/pixel-mine.png
```

Stress the simulation with 20,000 balls at a 16x speed multiplier (10 measured ticks by default):

```sh
dotnet run --configuration Release --project samples/pixel-mine/PixelMine.csproj -- --headless --stress-balls 20000 --stress-speed 16
```

Use `--ticks N` to change the measured duration, `--stress-balls` accepts 10,000..50,000, and `--stress-speed` accepts 8x..32x. The command warms up for two ticks and reports mean, p95, and maximum milliseconds per tick; it does not enforce a machine-specific timing threshold.

## Controls

Choose START or SETTINGS with the mouse or the up/down arrows and Enter. In SETTINGS, use left/right or click to change VSync and the FPS limit (30, 60, 120, or unlimited); Esc returns to the title. VSync may cap the FPS at the display refresh rate. Settings apply for the current session.

| Input | Action |
| --- | --- |
| Mouse click | Fire the selected ball toward the click |
| `1`-`7` | Buy power, speed, ball slots, auto-fire, the gold rod, smash ball, drill ball |
| `Tab` | Cycle ball type (basic, smash, drill) |
| `WASD` / arrows | Pan (`Shift` for fast pan) |
| Mouse wheel | Zoom from 7px to 160px cells (default 40px) |
| `Space` | Pause |
| `F11` | Toggle fullscreen |
| `Esc` | Quit |

## Rules

The field is 4000x2500 = 10,000,000 cells, generated in 64x64 chunks from deterministic noise on first touch. The whole field is solid except for a compact launch pocket at the map center. Each cell renders 40 screen pixels at the default zoom, while round balls stay about 10 pixels wide. Only exposed surfaces and the next two cells inward are shown; the inner layers darken toward the rock, while deeper cells blend into the background. Dirt, clay, stone, and deep rock differ in hardness and dust yield; prisms glow through rock; the golden pixel sits in the deepest band. Balls are rows of a Kernel population, so all ball state is world state that clones and saves with the world. Physics and the economy use integer math; world generation uses deterministic floating-point noise. Balls bounce and persist until they leave the map; the smash ball damages a radius, and the drill ball tunnels straight through without bouncing. The golden pixel publishes `pixel.golden.found`, and the authored `rules.json` rule tags the mine and adds a win.

The sample consumes Core and Kernel as a library: authored content (entities, rules, manifest), a world entity per resource, a generated population for the balls, `DeterministicRandom` seeded per tick, facts and rules, the fixed-step host, and typed-pixel rendering. Presentation, Incremental, Gameplay2D, save encoding, and the scheduler are not used; the mine owns its pixel arrays and the game owns its camera and HUD.

## Verification

`tests/Systemic.PixelMine.Tests` covers the ten-million-cell dimension, shot costs, seed determinism, dust spending, autoplay reaching the golden pixel, and the authored rule awarding the win.
