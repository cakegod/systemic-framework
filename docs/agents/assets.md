# Procedural assets

Art is a recipe in `visuals.json`. Recipes have a palette, a canvas, and layers. Supported shapes today are `rect`, `ellipse`, and `line`.

```sh
systemic assets validate my-game
systemic assets sheet my-game --out artifacts/assets.png --scale 8
systemic assets atlas my-game --out content/atlas.png --manifest content/atlas.json
```

`validate` rasterizes every recipe and reports empty canvases, duplicate IDs, out-of-range canvases, and invalid palettes. `sheet` renders a labeled contact sheet for review. `atlas` packs every recipe at source resolution and writes the frame rectangles.

Before accepting an asset batch:

- Check the sheet at 4x and 8x on a light and a dark background.
- Check the same asset at 1x in the game.
- Keep one palette and one light direction per category.
- Reject silhouettes that read as another category.
- Do not add a hand-drawn PNG. Fix the recipe.

The current recipe schema has no animation frames, outlines, or shading. Those are Phase 1 additions. Until then, use layered rectangles and ellipses and keep assets small.
