using System.Text.Encodings.Web;
using System.Text.Json;
using Systemic.Content;
using Systemic.Host;
using Systemic.Presentation;

namespace Systemic.Preview;

/// <summary>Safe static HTML preview for compiled recipes and UI layouts; it does not execute pack code.</summary>
public static class ContentPreviewPage
{
    private const int MaximumRecipes = 256;
    private const long MaximumPixelBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.Default,
    };

    public static string Render(ContentPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var presentation = pack.Modules.Select(module => module.Content).OfType<PresentationModuleContent>().SingleOrDefault()
            ?? throw new InvalidOperationException("Content preview requires the Presentation module.");
        if (presentation.Visuals.Recipes.Length > MaximumRecipes)
        {
            throw new ArgumentOutOfRangeException(nameof(pack), $"The local preview is limited to {MaximumRecipes} visual recipes.");
        }

        long pixelBytes = 0;
        long pixelWork = 0;
        foreach (var recipe in presentation.Visuals.Recipes)
        {
            if (recipe.CanvasWidth is < 1 or > 256 || recipe.CanvasHeight is < 1 or > 256)
            {
                throw new ArgumentOutOfRangeException(nameof(pack), $"Visual recipe '{recipe.Id}' has unsupported preview dimensions.");
            }

            foreach (var state in StatesFor(recipe))
            {
                pixelBytes += (long)recipe.CanvasWidth * recipe.CanvasHeight * 4;
                if (pixelBytes > MaximumPixelBytes)
                {
                    throw new ArgumentOutOfRangeException(nameof(pack),
                        $"The local preview is limited to {MaximumPixelBytes / (1024 * 1024)} MiB of generated pixels.");
                }

                pixelWork = checked(pixelWork + ProceduralRasterizer.EstimatePixelWorkForState(recipe, state));
                if (pixelWork > ProceduralRasterizer.MaximumPixelWork)
                {
                    throw new ArgumentOutOfRangeException(nameof(pack),
                        $"The local preview is limited to {ProceduralRasterizer.MaximumPixelWork} raster work operations.");
                }
            }
        }

        var recipes = presentation.Visuals.Recipes.SelectMany(recipe =>
            StatesFor(recipe).Select(state =>
            {
                var pixels = ProceduralRasterizer.Rasterize(recipe, visualState: state);
                return new PreviewRecipe(recipe.Id, state, pixels.Width, pixels.Height,
                    Convert.ToBase64String(pixels.Pixels.Span));
            })).ToArray();
        var materialized = pack.CreateWorld(seed: 0, valueScale: 1);
        var game = new SystemicGameBuilder(pack, materialized).Build();
        var layouts = presentation.Ui.Layouts.Select(layout =>
            new PreviewLayout(layout.Id, game.EvaluateUi(layout.Id))).ToArray();
        var payload = JsonSerializer.Serialize(new PreviewPayload(
            pack.Manifest.Name,
            pack.Manifest.TickRateHz,
            recipes,
            layouts,
            presentation.Catalog.Timelines,
            presentation.Catalog.Bindings), JsonOptions);

        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>Systemic content preview</title>
              <style>
                :root { color-scheme: dark; font: 15px system-ui, sans-serif; background: #151922; color: #e9edf5; }
                body { margin: 2rem auto; max-width: 1100px; padding: 0 1rem; }
                header, section { border: 1px solid #354052; border-radius: 10px; padding: 1rem; margin-bottom: 1rem; }
                #recipes { display: grid; grid-template-columns: repeat(auto-fit, minmax(220px, 1fr)); gap: 1rem; }
                article { background: #202735; padding: 1rem; border-radius: 8px; }
                canvas { width: 160px; height: 160px; image-rendering: pixelated; background: #11151c; border: 1px solid #46536b; }
                .ui-node { border-left: 2px solid #506b91; margin: .35rem 0 .35rem .75rem; padding-left: .6rem; }
                .ui-row { display: flex; flex-wrap: wrap; gap: .6rem; align-items: center; }
                .ui-icon { width: 48px; height: 48px; image-rendering: pixelated; background: #11151c; border: 1px solid #46536b; }
                .ui-icon.is-active { border-color: #f2d18a; box-shadow: 0 0 12px #ff9948; }
                #timeline-preview { image-rendering: pixelated; width: 256px; height: 256px; background: #11151c; border: 1px solid #46536b; }
                .timeline-controls { display: flex; flex-wrap: wrap; gap: .75rem; align-items: center; }
                #timeline-seek { flex: 1 1 240px; }
                #timeline-binding { color: #aebad0; }
                .ui-bar { width: min(280px, 100%); height: 12px; background: #11151c; border: 1px solid #46536b; }
                .ui-bar-fill { height: 100%; background: linear-gradient(90deg, #547c83, #f2d18a); }
                .ui-action { opacity: .7; cursor: default; }
                output { display: inline-block; margin-left: .5rem; color: #f2d18a; }
                code { color: #9cc9ff; }
              </style>
            </head>
            <body>
              <header><h1 id="game-title"></h1><p id="game-meta"></p></header>
              <section><h2>Visual recipes</h2><div id="recipes"></div></section>
              <section><h2>Presentation timelines</h2>
                <div class="timeline-controls">
                  <label for="timeline-select">Timeline</label><select id="timeline-select"></select>
                  <button id="timeline-play" type="button">Play</button>
                  <input id="timeline-seek" type="range" min="0" max="0" value="0" aria-label="Timeline tick">
                  <output id="timeline-tick">0</output>
                </div>
                <p id="timeline-binding"></p><canvas id="timeline-preview"></canvas>
              </section>
              <section><h2>UI layouts</h2><div id="layouts"></div></section>
              <script id="preview-data" type="application/json">{{payload}}</script>
              <script>
                "use strict";
                const data = JSON.parse(document.getElementById("preview-data").textContent);
                document.getElementById("game-title").textContent = data.name;
                document.getElementById("game-meta").textContent = `${data.tickRateHz} simulation steps per second · read-only content preview`;
                const recipeById = new Map(data.recipes.map(recipe => [`${recipe.id}|${recipe.state || ""}`, recipe]));
                const recipesById = new Map();
                for (const recipe of data.recipes) {
                  if (!recipesById.has(recipe.id)) recipesById.set(recipe.id, []);
                  recipesById.get(recipe.id).push(recipe);
                }
                function drawRecipe(recipe, canvas) {
                  canvas.width = recipe.width; canvas.height = recipe.height;
                  const context = canvas.getContext("2d");
                  const binary = atob(recipe.pixels);
                  const rgba = new Uint8ClampedArray(binary.length);
                  for (let i = 0; i < binary.length; i++) rgba[i] = binary.charCodeAt(i);
                  context.putImageData(new ImageData(rgba, recipe.width, recipe.height), 0, 0);
                }
                const frameCanvases = new Map();
                function getFrameCanvas(recipeId, state) {
                  const key = `${recipeId}|${state || ""}`;
                  if (frameCanvases.has(key)) return frameCanvases.get(key);
                  const recipe = recipeById.get(key) || recipeById.get(`${recipeId}|`);
                  if (!recipe) return null;
                  const canvas = document.createElement("canvas");
                  drawRecipe(recipe, canvas);
                  frameCanvases.set(key, canvas);
                  return canvas;
                }
                const recipes = document.getElementById("recipes");
                for (const [recipeId, variants] of recipesById) {
                  const recipe = variants.find(item => !item.state) || variants[0];
                  const card = document.createElement("article");
                  const title = document.createElement("h3");
                  title.textContent = recipeId;
                  const canvas = document.createElement("canvas");
                  drawRecipe(recipe, canvas);
                  card.append(title, canvas);
                  for (const variant of variants.filter(item => item.state)) {
                    const label = document.createElement("span"); label.textContent = variant.state;
                    const stateCanvas = document.createElement("canvas");
                    drawRecipe(variant, stateCanvas);
                    card.append(label, stateCanvas);
                  }
                  recipes.append(card);
                }
                function renderNode(node, parent) {
                  const element = document.createElement("div");
                  element.className = `ui-node ui-${node.type}`;
                  if (node.type === "icon") {
                    const recipe = recipeById.get(`${node.visualRecipeId}|${node.visualState || ""}`)
                      || recipeById.get(`${node.visualRecipeId}|`);
                    if (recipe) {
                      const canvas = document.createElement("canvas");
                      canvas.className = "ui-icon";
                      canvas.dataset.recipe = node.visualRecipeId;
                      canvas.dataset.state = node.visualState || "";
                      if (node.visualState === "active") canvas.classList.add("is-active");
                      drawRecipe(recipe, canvas);
                      element.append(canvas);
                    }
                    element.title = `${node.text || node.visualRecipeId}${node.hasValue ? ` · state ${node.numberValue}` : ""}`;
                  } else if (node.type === "bar") {
                    const label = document.createElement("span");
                    label.textContent = node.text || "Progress";
                    const track = document.createElement("div");
                    track.className = "ui-bar";
                    track.setAttribute("role", "progressbar");
                    const fill = document.createElement("div");
                    fill.className = "ui-bar-fill";
                    const progress = Math.max(0, Math.min(1, node.progress || 0));
                    fill.style.width = `${progress * 100}%`;
                    track.setAttribute("aria-valuenow", String(Math.round(progress * 100)));
                    track.setAttribute("aria-valuemin", "0"); track.setAttribute("aria-valuemax", "100");
                    track.append(fill); element.append(label, track);
                  } else if (node.type === "button") {
                    const button = document.createElement("button");
                    button.className = "ui-action"; button.disabled = true;
                    button.textContent = node.text || node.action || "Action";
                    button.title = `Host action: ${node.action}`;
                    element.append(button);
                  } else {
                    const label = document.createElement("span");
                    label.textContent = node.text || node.type;
                    element.append(label);
                    if (node.hasValue) {
                      const output = document.createElement("output");
                      output.textContent = node.textValue ?? node.numberValue?.toLocaleString(undefined, { maximumFractionDigits: 2 }) ?? "";
                      element.append(output);
                    }
                  }
                  parent.append(element);
                  for (const child of node.children) renderNode(child, element);
                }
                const layouts = document.getElementById("layouts");
                for (const layout of data.layouts) {
                  const container = document.createElement("article");
                  const title = document.createElement("h3"); title.textContent = layout.id;
                  container.append(title); layouts.append(container); renderNode(layout.root, container);
                }
                const timelineSelect = document.getElementById("timeline-select");
                const timelineSeek = document.getElementById("timeline-seek");
                const timelineCanvas = document.getElementById("timeline-preview");
                const timelineContext = timelineCanvas.getContext("2d");
                const timelineTick = document.getElementById("timeline-tick");
                const timelineBinding = document.getElementById("timeline-binding");
                const playButton = document.getElementById("timeline-play");
                timelineCanvas.width = 128; timelineCanvas.height = 128;
                for (const timeline of data.timelines) {
                  const option = document.createElement("option");
                  option.value = timeline.id; option.textContent = timeline.id;
                  timelineSelect.append(option);
                }
                let animation = 0;
                let playing = false;
                let startedAt = 0;
                let startTick = 0;
                function selectedTimeline() { return data.timelines.find(item => item.id === timelineSelect.value); }
                function renderTimeline(tick) {
                  const timeline = selectedTimeline();
                  if (!timeline) return;
                  const keyframes = timeline.keyframes;
                  let left = keyframes[0];
                  let right = null;
                  for (let i = 1; i < keyframes.length; i++) {
                    if (keyframes[i].tick > tick) { right = keyframes[i]; break; }
                    left = keyframes[i];
                  }
                  const progress = right ? (tick - left.tick) / (right.tick - left.tick) : 0;
                  const mix = (a, b) => a + (b - a) * progress;
                  const x = right ? mix(left.offsetX, right.offsetX) : left.offsetX;
                  const y = right ? mix(left.offsetY, right.offsetY) : left.offsetY;
                  const scale = right ? mix(left.scale, right.scale) : left.scale;
                  const opacity = right ? mix(left.opacity, right.opacity) : left.opacity;
                  timelineContext.clearRect(0, 0, timelineCanvas.width, timelineCanvas.height);
                  const pixels = getFrameCanvas(timeline.visualRecipeId, left.visualState);
                  if (pixels) {
                    timelineContext.save();
                    timelineContext.globalAlpha = opacity;
                    timelineContext.translate(timelineCanvas.width / 2 + x, timelineCanvas.height / 2 + y);
                    timelineContext.scale(scale, scale);
                    timelineContext.drawImage(pixels, -pixels.width / 2, -pixels.height / 2);
                    timelineContext.restore();
                  }
                  timelineTick.value = `${tick} / ${timeline.durationTicks - 1} ticks · ${timeline.particleCount} particles`;
                  timelineSeek.value = String(tick);
                }
                function updateTimeline() {
                  const timeline = selectedTimeline();
                  if (!timeline) { timelineBinding.textContent = "No timelines are defined in this pack."; return; }
                  timelineSeek.max = String(timeline.durationTicks - 1);
                  const bindings = data.bindings.filter(binding => binding.timelineId === timeline.id);
                  timelineBinding.textContent = bindings.length
                    ? bindings.map(binding => `${binding.event} → ${binding.anchor}`).join(" · ")
                    : "No fact bindings; this timeline can still be sampled by a host adapter.";
                  renderTimeline(Math.min(Number(timelineSeek.value) || 0, timeline.durationTicks - 1));
                }
                function animate(now) {
                  const timeline = selectedTimeline();
                  if (!playing || !timeline) return;
                  let tick = startTick + Math.floor((now - startedAt) * data.tickRateHz / 1000);
                  if (timeline.loop) tick %= timeline.durationTicks;
                  else if (tick >= timeline.durationTicks) {
                    tick = timeline.durationTicks - 1; playing = false; playButton.textContent = "Play";
                  }
                  timelineSeek.value = String(tick); renderTimeline(tick);
                  if (playing) animation = requestAnimationFrame(animate);
                }
                timelineSelect.addEventListener("change", () => {
                  playing = false; cancelAnimationFrame(animation); playButton.textContent = "Play"; updateTimeline();
                });
                timelineSeek.addEventListener("input", () => {
                  playing = false; cancelAnimationFrame(animation); playButton.textContent = "Play";
                  renderTimeline(Number(timelineSeek.value));
                });
                playButton.addEventListener("click", () => {
                  const timeline = selectedTimeline();
                  if (!timeline) return;
                  if (playing) {
                    playing = false; cancelAnimationFrame(animation); playButton.textContent = "Play"; return;
                  }
                  if (!timeline.loop && Number(timelineSeek.value) >= timeline.durationTicks - 1) timelineSeek.value = "0";
                  playing = true; startedAt = performance.now(); startTick = Number(timelineSeek.value);
                  playButton.textContent = "Pause"; animation = requestAnimationFrame(animate);
                });
                updateTimeline();
              </script>
            </body>
            </html>
            """;
    }

    private sealed record PreviewPayload(
        string Name,
        int TickRateHz,
        IReadOnlyList<PreviewRecipe> Recipes,
        IReadOnlyList<PreviewLayout> Layouts,
        IReadOnlyList<PresentationTimeline> Timelines,
        IReadOnlyList<PresentationCueBinding> Bindings);

    private sealed record PreviewRecipe(string Id, string? State, int Width, int Height, string Pixels);
    private sealed record PreviewLayout(string Id, UiNodeState Root);

    private static string?[] StatesFor(VisualRecipe recipe)
    {
        var stateNames = recipe.Layers.Select(layer => layer.State)
            .Concat(recipe.Parts.IsDefaultOrEmpty
                ? []
                : recipe.Parts.SelectMany(part => part.Recipe.Layers.Select(layer => layer.State)))
            .Where(state => state is not null)
            .Select(state => state!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        return [null, .. stateNames];
    }
}
