using Gum.Forms;
using Gum.Forms.Controls;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using MonoGameGum;
using Systemic.Kernel;
using Systemic.Presentation;
using Color = Microsoft.Xna.Framework.Color;
using Vector2 = Microsoft.Xna.Framework.Vector2;

namespace GlowGarden;

public sealed class GardenGame : Game
{
    private readonly GardenOptions _options;
    private readonly GardenSession _session;
    private SpriteBatch _batch = null!;
    private Texture2D _pixel = null!, _disc = null!, _terrain = null!, _debugField = null!;
    private RenderTarget2D _frame = null!;
    private RasterizerState _gardenClipState = null!;
    private GardenUi _ui = null!;
    private KeyboardState _previous;
    private int _frames;
    private readonly PresentationCueWindow _cueWindow = new(capacity: 512, maxAgeTicks: 300);
    private readonly GardenTextureCache<(string Recipe, string State), Texture2D> _cueTextures = new(64);
    private Dictionary<string, VisualRecipe> _cueRecipes = new(StringComparer.Ordinal);
    private static readonly Rectangle GardenClip = new(20, 120, 806, 537);
    private PresentationViewport _viewport = CreateViewport();
    private int _visibleObjects, _culledObjects;
    private int _debugFieldTick = -1;
    internal static PresentationViewport CreateViewport() => new(
        new Bounds2(GardenSimulation.Width / 2, GardenSimulation.Height / 2,
            GardenSimulation.Width / 2, GardenSimulation.Height / 2),
        new Bounds2(423, 388.5f, 403, 268.5f));
    public GardenHost Host => _session.Host;
    public bool Paused { get; private set; }
    public bool Bulk { get; set; }
    public int Speed { get; private set; } = 1;
    internal bool DebugOverlayEnabled { get; private set; }
    internal PresentationViewport Viewport => _viewport;
    internal long DisplayDroppedCount => _cueWindow.DroppedCount;
    internal long ExpiredCueCount => _cueWindow.ExpiredCount;
    internal int ActiveCueCount => _cueWindow.Count;
    internal int CachedCueTextureCount => _cueTextures.Count;
    internal int VisibleObjectCount => _visibleObjects;
    internal int CulledObjectCount => _culledObjects;
    public string SaveStatus { get; private set; } = "[F5] Save   [F9] Load";
    private static string SavePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlowGarden", "garden.json");

    public GardenGame(GardenOptions options)
    {
        _options = options;
        _session = new(options.Seed);
        if (options.VerifyRender)
        {
            Host.Simulation.ToggleTalent(Talent.Echo);
            Host.Simulation.ToggleTalent(Talent.Seedstorm);
            Host.Simulation.ToggleTalent(Talent.Magnetism);
            Speed = 4;
        }
        _ = new GraphicsDeviceManager(this) { PreferredBackBufferWidth = 1280, PreferredBackBufferHeight = 800, SynchronizeWithVerticalRetrace = true };
        Window.Title = "Glow Garden — a little colony of light";
        IsMouseVisible = true;
    }
    protected override void Initialize()
    {
        GumService.Default.Initialize(this, DefaultVisualsVersion.V3);
        GumService.Default.ContentLoader!.XnaContentManager = Content;
        FrameworkElement.KeyboardsForUiControl.Add(GumService.Default.Keyboard);
        // Space is the global pause shortcut; focused controls activate with Enter.
        FrameworkElement.ClickCombos.RemoveAll(combo => combo.PushedKey == Keys.Space);
        _ui = new(this);
        base.Initialize();
    }
    protected override void LoadContent()
    {
        _batch = new(GraphicsDevice);
        _gardenClipState = new RasterizerState { ScissorTestEnable = true, CullMode = CullMode.None };
        _pixel = new(GraphicsDevice, 1, 1);
        _pixel.SetData([Color.White]);
        _disc = new(GraphicsDevice, 32, 32);
        var pixels = new Color[1024];
        for (var y = 0; y < 32; y++) for (var x = 0; x < 32; x++)
            pixels[y * 32 + x] = Vector2.Distance(new(x + .5f, y + .5f), new(16)) <= 16 ? Color.White : Color.Transparent;
        _disc.SetData(pixels);
        _frame = new(GraphicsDevice, 1280, 800);
        _terrain = BuildTerrain();
        _cueRecipes = Host.Game.PresentationContent().Visuals.Recipes
            .ToDictionary(recipe => recipe.Id, StringComparer.Ordinal);
    }
    /// <summary>
    /// Bakes the thicket field once at quarter resolution and draws it with linear filtering, so the
    /// noise shapes stay organic instead of showing the simulation grid.
    /// </summary>
    private Texture2D BuildTerrain()
    {
        const int resolution = 4;
        var width = (int)(GardenSimulation.Width / resolution);
        var height = (int)(GardenSimulation.Height / resolution);
        var pixels = new Color[width * height];
        var obstacles = Host.Simulation.Obstacles;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var density = obstacles.Density((x + 0.5f) * resolution, (y + 0.5f) * resolution);
                var edge = Math.Clamp((density - 0.46f) / 0.12f, 0, 1);
                var shade = (byte)(22 + (38 * Math.Clamp((density - 0.52f) / 0.2f, 0, 1)));
                pixels[(y * width) + x] = new Color(shade, (byte)(shade + 14), (byte)(shade + 10), (byte)(edge * 240));
            }
        }

        var texture = new Texture2D(GraphicsDevice, width, height);
        texture.SetData(pixels);
        return texture;
    }

    private void DrawDebugGrid(GardenSimulation model)
    {
        var scent = model._scent;
        if (_debugField is null || model.Tick < _debugFieldTick || model.Tick - _debugFieldTick >= 30)
        {
            var values = scent.Capture();
            var pixels = new Color[values.Length];
            for (var row = 0; row < scent.Rows; row++)
            {
                for (var column = 0; column < scent.Columns; column++)
                {
                    var index = (row * scent.Columns) + column;
                    var position = new System.Numerics.Vector2((column + .5f) * scent.CellSize,
                        (row + .5f) * scent.CellSize);
                    if (model.Obstacles.IsBlocked(position))
                    {
                        pixels[index] = new Color(242, 83, 60, 110);
                    }
                    else if (values[index] > 0)
                    {
                        var intensity = Math.Clamp(values[index] / 10f, .12f, .72f);
                        pixels[index] = new Color((byte)70, (byte)238, (byte)150, (byte)(intensity * 180));
                    }
                    else
                    {
                        pixels[index] = Color.Transparent;
                    }
                }
            }

            if (_debugField is null)
            {
                _debugField = new Texture2D(GraphicsDevice, scent.Columns, scent.Rows);
            }

            _debugField.SetData(pixels);
            _debugFieldTick = model.Tick;
        }

        _batch.Begin(samplerState: SamplerState.PointClamp, rasterizerState: _gardenClipState);
        _batch.Draw(_debugField, At(System.Numerics.Vector2.Zero), null, Color.White, 0f, Vector2.Zero,
            _viewport.Scale * scent.CellSize, SpriteEffects.None, 0f);
        var color = new Color(119, 184, 177, 95);
        for (var column = 0; column <= scent.Columns; column++)
        {
            var x = Math.Min(column * scent.CellSize, GardenSimulation.Width);
            Line(At(new System.Numerics.Vector2(x, 0)), At(new System.Numerics.Vector2(x, GardenSimulation.Height)), 1, color);
        }
        for (var row = 0; row <= scent.Rows; row++)
        {
            var y = Math.Min(row * scent.CellSize, GardenSimulation.Height);
            Line(At(new System.Numerics.Vector2(0, y)), At(new System.Numerics.Vector2(GardenSimulation.Width, y)), 1, color);
        }
        _batch.End();
    }

    public void TogglePause() => Paused = !Paused;
    public void CycleSpeed() => Speed = Speed == 1 ? 2 : Speed == 2 ? 4 : 1;
    public void Restart()
    {
        _session.Restart();
        Paused = false;
        Speed = 1;
        _cueWindow.Clear();
        _debugFieldTick = -1;
        RefreshTerrain();
    }
    private void RefreshTerrain()
    {
        if (_terrain is null) return;
        var previous = _terrain;
        _terrain = BuildTerrain();
        previous.Dispose();
    }

    private void SaveGarden()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SavePath)!);
            _session.SaveToFile(SavePath);
            SaveStatus = "Garden saved.  [F5] Save   [F9] Load";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or Systemic.Save.SystemicSaveException)
        {
            SaveStatus = $"Save failed: {exception.Message}";
        }
    }

    private void LoadGarden()
    {
        try
        {
            _session.LoadFromFile(SavePath);
            _cueWindow.Clear();
            _debugFieldTick = -1;
            RefreshTerrain();
            SaveStatus = "Garden loaded.  [F5] Save   [F9] Load";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or Systemic.Save.SystemicSaveException or InvalidOperationException)
        {
            SaveStatus = $"Load failed: {exception.Message}";
        }
    }
    protected override void Update(GameTime gameTime)
    {
        var keyboard = Keyboard.GetState();
        bool Pressed(Keys key) => keyboard.IsKeyDown(key) && !_previous.IsKeyDown(key);
        if (Pressed(Keys.Escape)) Exit();
        if (Pressed(Keys.Space)) TogglePause();
        if (Pressed(Keys.F)) CycleSpeed();
        if (Pressed(Keys.R)) Restart();
        if (Pressed(Keys.F5)) SaveGarden();
        if (Pressed(Keys.F9)) LoadGarden();
        if (Pressed(Keys.F3)) DebugOverlayEnabled = !DebugOverlayEnabled;
        if (Pressed(Keys.Home)) _viewport = CreateViewport();
        if (Pressed(Keys.PageUp)) _viewport = _viewport.WithZoom(Math.Min(4, _viewport.Zoom * 1.25f));
        if (Pressed(Keys.PageDown)) _viewport = _viewport.WithZoom(Math.Max(1, _viewport.Zoom / 1.25f));
        var panX = (keyboard.IsKeyDown(Keys.L) ? 1 : 0) - (keyboard.IsKeyDown(Keys.J) ? 1 : 0);
        var panY = (keyboard.IsKeyDown(Keys.K) ? 1 : 0) - (keyboard.IsKeyDown(Keys.I) ? 1 : 0);
        if (panX != 0 || panY != 0)
        {
            var distance = 450f * (float)Math.Clamp(gameTime.ElapsedGameTime.TotalSeconds, 0, .25) / _viewport.Zoom;
            _viewport = _viewport.WithCamera(new Point2(_viewport.CameraCenter.X + panX * distance,
                _viewport.CameraCenter.Y + panY * distance));
        }
        if (Pressed(Keys.X)) Bulk = !Bulk;
        if (Pressed(Keys.B)) Host.Simulation.Hire(CreatureKind.Burstling);
        if (Pressed(Keys.G)) Host.Simulation.Hire(CreatureKind.Gatherer);
        if (Pressed(Keys.C)) Host.Simulation.ResetTree(UpgradeTree.Creatures);
        if (Pressed(Keys.M)) Host.Simulation.ResetTree(UpgradeTree.MagicGrowth);
        Keys[] upgradeKeys = [Keys.D1, Keys.D2, Keys.D3, Keys.D4, Keys.D5];
        Keys[] talentKeys = [Keys.Q, Keys.W, Keys.E, Keys.A, Keys.S, Keys.D];
        for (var i = 0; i < 5; i++) if (Pressed(upgradeKeys[i])) Host.Simulation.Buy((UpgradeKind)i, Bulk);
        for (var i = 0; i < 6; i++) if (Pressed(talentKeys[i])) Host.Simulation.ToggleTalent((Talent)i);
        _previous = keyboard;
        GumService.Default.Update(gameTime);
        if (!Paused) Host.Game.Advance(TimeSpan.FromTicks(gameTime.ElapsedGameTime.Ticks * Speed));
        _cueWindow.Pump(Host.Game.PresentationCues(), Host.Simulation.Tick);
        _ui.Refresh();
        base.Update(gameTime);
    }
    private Vector2 At(System.Numerics.Vector2 p)
    {
        var screen = _viewport.WorldToScreen(new Point2(p.X, p.Y));
        return new Vector2(screen.X, screen.Y);
    }

    private bool IsVisible(System.Numerics.Vector2 position, float screenRadius)
    {
        var radius = screenRadius / _viewport.Scale;
        var visible = _viewport.IsVisible(new Bounds2(position.X, position.Y, radius, radius));
        if (visible) _visibleObjects++;
        else _culledObjects++;
        return visible;
    }

    /// <summary>Fact cues drive burst and talent feedback; position, scale, opacity and particles come from content.</summary>
    private void DrawCue(PresentationCue cue, int tick)
    {
        var age = tick - cue.Tick;
        if (age < 0 || !Host.Game.TrySamplePresentation(cue, age, out var frame)
            || !Host.Game.TryGetPresentationTimeline(cue.TimelineId, out var timeline)
            || !_cueRecipes.TryGetValue(timeline.VisualRecipeId, out var recipe)) return;

        var effectRadius = Math.Abs(frame.Scale) * (Math.Max(recipe.CanvasWidth, recipe.CanvasHeight) / 2f + 30)
            * _viewport.Scale + 2;
        if (!IsVisible(new System.Numerics.Vector2(frame.Position.X, frame.Position.Y), effectRadius)) return;

        var state = frame.VisualState ?? string.Empty;
        var key = (recipe.Id, state);
        if (!_cueTextures.TryGet(key, out var texture))
        {
            texture = _cueTextures.GetOrCreate(key, () =>
            {
                var buffer = ProceduralRasterizer.Rasterize(recipe, cue.VisualSeed, frame.VisualState);
                var result = new Texture2D(GraphicsDevice, buffer.Width, buffer.Height);
                var pixels = new Color[buffer.Width * buffer.Height];
                var rgba = buffer.Pixels.Span;
                for (var i = 0; i < pixels.Length; i++)
                    pixels[i] = new Color(rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]);
                result.SetData(pixels);
                return result;
            }, FlushCueBatch);
        }

        var center = At(new System.Numerics.Vector2(frame.Position.X, frame.Position.Y));
        var opacity = Math.Clamp(frame.Opacity, 0, 1);
        _batch.Draw(texture, center, null, Color.White * opacity, 0f,
            new Vector2(texture.Width / 2f, texture.Height / 2f), _viewport.Scale * frame.Scale, SpriteEffects.None, 0f);

        if (cue.ParticleCount == 0 || recipe.Palette.IsDefaultOrEmpty) return;
        var spark = ColorFromHex(recipe.Palette[^1]) * opacity;
        for (var i = 0; i < cue.ParticleCount; i++)
        {
            var seed = cue.VisualSeed + (ulong)i * 0x9E3779B97F4A7C15UL;
            var angle = (float)(seed % 6283) / 1000f;
            var distance = 8 + (float)((seed >> 20) % 1000) / 1000f * 22;
            var offset = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * distance * frame.Scale * _viewport.Scale;
            Disc(center + offset, _viewport.Zoom, spark);
        }
    }

    private static Color ColorFromHex(string hex) => new(
        Convert.ToByte(hex.Substring(1, 2), 16),
        Convert.ToByte(hex.Substring(3, 2), 16),
        Convert.ToByte(hex.Substring(5, 2), 16),
        hex.Length >= 9 ? Convert.ToByte(hex.Substring(7, 2), 16) : (byte)255);

    private void BeginCueBatch() => _batch.Begin(samplerState: SamplerState.PointClamp,
        blendState: BlendState.NonPremultiplied, rasterizerState: _gardenClipState);

    private void FlushCueBatch()
    {
        _batch.End();
        BeginCueBatch();
    }
    private void Disc(Vector2 center, float radius, Color color) => _batch.Draw(_disc, center, null, color, 0, new(16), radius / 16, SpriteEffects.None, 0);
    private void Line(Vector2 a, Vector2 b, float width, Color color)
    {
        var delta = b - a;
        _batch.Draw(_pixel, a, null, color, MathF.Atan2(delta.Y, delta.X), Vector2.Zero, new Vector2(delta.Length(), width), SpriteEffects.None, 0);
    }
    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.SetRenderTarget(_frame);
        GraphicsDevice.Clear(new Color(10, 20, 28));
        _batch.Begin(samplerState: SamplerState.PointClamp);
        _batch.Draw(_pixel, new Rectangle(16, 116, 820, 543), new Color(13, 34, 38));
        _batch.Draw(_pixel, new Rectangle(846, 116, 422, 680), new Color(17, 32, 42));
        _batch.End();
        var previousScissor = GraphicsDevice.ScissorRectangle;
        GraphicsDevice.ScissorRectangle = GardenClip;
        _batch.Begin(samplerState: SamplerState.LinearClamp, rasterizerState: _gardenClipState);
        _batch.Draw(_terrain, At(System.Numerics.Vector2.Zero), null, Color.White, 0f, Vector2.Zero,
            _viewport.Scale * 4f, SpriteEffects.None, 0f);
        _batch.End();
        var model = Host.Simulation;
        if (DebugOverlayEnabled) DrawDebugGrid(model);
        _batch.Begin(samplerState: SamplerState.PointClamp, rasterizerState: _gardenClipState);
        _visibleObjects = 0;
        _culledObjects = 0;
        var zoom = _viewport.Zoom;
        // Deterministic decorative grass is rendering only: it never consumes World RNG.
        for (var i = 0; i < 190; i++)
        {
            var position = new System.Numerics.Vector2((16 + i * 137 % 782) / .42f, (36 + i * 73 % 475) / .42f);
            if (!IsVisible(position, 4 * zoom)) continue;
            var p = At(position);
            Line(p, p + new Vector2(1.5f, -3) * zoom, zoom, new Color(27, 63, 58));
        }
        if (IsVisible(GardenSimulation.Colony, 18 * zoom))
        {
            var colony = At(GardenSimulation.Colony);
            Disc(colony, 18 * zoom, new Color(24, 65, 69));
            Disc(colony, 12 * zoom, new Color(58, 115, 108));
            Disc(colony, 7 * zoom, new Color(241, 213, 128));
        }
        foreach (var plant in model.Plants)
        {
            if (!IsVisible(plant.Position, 16 * zoom)) continue;
            var p = At(plant.Position);
            Disc(p + new Vector2(0, 4) * zoom, 8 * zoom, new Color(8, 24, 31));
            Line(p + new Vector2(0, 3) * zoom, p - new Vector2(0, 9) * zoom, 2 * zoom, new Color(74, 118, 84));
            Disc(p + new Vector2(-4, -2) * zoom, 3.2f * zoom, new Color(59, 109, 82));
            Disc(p + new Vector2(4, -4) * zoom, 3.2f * zoom, new Color(79, 139, 97));
            Disc(p - new Vector2(0, 9) * zoom, (2.5f + plant.Growth * 4.5f) * zoom, plant.Growth >= 1 ? new Color(247, 215, 106) : new Color(102, 124, 99));
        }
        foreach (var mote in model.Motes)
        {
            if (!IsVisible(mote.Position, 3 * zoom)) continue;
            var p = At(mote.Position);
            Disc(p, 3 * zoom, new Color(91, 92, 43, 70));
            Disc(p, 1.2f * zoom, new Color(255, 231, 139));
        }
        foreach (var creature in model.Creatures)
        {
            if (!IsVisible(creature.Position, 22 * zoom)) continue;
            var ground = At(creature.Position);
            var p = ground - new Vector2(0, MathF.Sin(creature.LeapProgress * MathF.PI) * 12 * zoom);
            Disc(ground + new Vector2(0, 2.5f) * zoom, 3 * zoom, new Color(6, 22, 27));
            if (creature.LeapProgress > 0) Line(ground, p, zoom, new Color(119, 91, 113));
            var color = creature.Kind == CreatureKind.Burstling ? new Color(208, 125, 157) : new Color(113, 194, 187);
            Disc(p, (creature.IsCopy ? 2.5f : 3.5f) * zoom, creature.IsCopy ? new Color(166, 150, 221) : color);
            Disc(p + new Vector2(-1.2f, -1.2f) * zoom, 0.7f * zoom, Color.White);
            Disc(p + new Vector2(1.2f, -1.2f) * zoom, 0.7f * zoom, Color.White);
            for (var i = 0; i < Math.Min(creature.CarriedGlow, 12); i++)
                Disc(p + new Vector2((i % 4 - 1.5f) * 1.8f, 5 + i / 4 * 1.8f) * zoom, 0.9f * zoom, new Color(255, 222, 127));
        }
        foreach (var impact in model.PendingImpacts)
        {
            if (!IsVisible(impact.Position, (Math.Max(0, impact.DueTick - model.Tick) + 3) * zoom)) continue;
            var p = At(impact.Position);
            Disc(p - new Vector2(0, Math.Max(0, impact.DueTick - model.Tick)) * zoom, 2.5f * zoom, impact.IsSeed ? new Color(148, 224, 133) : new Color(189, 155, 239));
            Disc(p, 1.2f * zoom, new Color(86, 104, 111));
        }
        _batch.End();
        BeginCueBatch();
        foreach (var cue in _cueWindow.Cues) DrawCue(cue, model.Tick);
        _batch.End();
        GraphicsDevice.ScissorRectangle = previousScissor;
        GumService.Default.Draw();
        GraphicsDevice.SetRenderTarget(null);
        _batch.Begin();
        _batch.Draw(_frame, Vector2.Zero, Color.White);
        _batch.End();
        if (_options.VerifyRender && ++_frames == 120)
        {
            _viewport = _viewport.WithZoom(2).WithCamera(new Point2(0, 0));
        }
        if (_options.VerifyRender && _frames == 121)
        {
            if (_visibleObjects == 0 || _culledObjects == 0)
                throw new InvalidOperationException("Zoomed render did not exercise viewport culling.");
            var pixels = new Color[1280 * 800];
            _frame.GetData(pixels);
            if (pixels[(300 * 1280) + 830] != new Color(13, 34, 38))
                throw new InvalidOperationException("Garden rendering escaped the viewport clip.");
            _viewport = CreateViewport();
        }
        if (_options.VerifyRender && _frames == 480)
        {
            var pixels = new Color[1280 * 800];
            _frame.GetData(pixels);
            int Variety(Rectangle region)
            {
                var colors = new HashSet<uint>();
                for (var y = region.Top; y < region.Bottom; y++) for (var x = region.Left; x < region.Right; x++) colors.Add(pixels[y * 1280 + x].PackedValue);
                return colors.Count;
            }
            var gardenColors = Variety(new(20, 150, 800, 490));
            var uiColors = Variety(new(850, 120, 410, 670));
            if (model.BurstCount == 0 || model.BankedGlow == 0)
                throw new InvalidOperationException("Render scenario did not produce and bank Glow.");
            if (gardenColors < 10 || uiColors < 10) throw new InvalidOperationException($"Blank render: garden={gardenColors}, UI={uiColors}");
            if (_options.Screenshot != null)
            {
                using var file = File.Create(_options.Screenshot);
                _frame.SaveAsPng(file, 1280, 800);
            }
            Console.WriteLine($"RENDER OK frames={_frames} tick={model.Tick} gardenColors={gardenColors} uiColors={uiColors}");
            Exit();
        }
        base.Draw(gameTime);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _session.Dispose();
            _cueTextures.Dispose();
            _gardenClipState?.Dispose();
            _frame?.Dispose();
            _debugField?.Dispose();
            _disc?.Dispose();
            _terrain?.Dispose();
            _pixel?.Dispose();
            _batch?.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Bounded render-thread LRU. The caller flushes pending sprite draws before an eviction.</summary>
internal sealed class GardenTextureCache<TKey, TTexture> : IDisposable where TKey : notnull where TTexture : class, IDisposable
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TTexture Texture)>> _entries = [];
    private readonly LinkedList<(TKey Key, TTexture Texture)> _recent = [];

    public GardenTextureCache(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    public int Count => _entries.Count;

    public bool TryGet(TKey key, out TTexture texture)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            _recent.Remove(entry);
            _recent.AddFirst(entry);
            texture = entry.Value.Texture;
            return true;
        }

        texture = null!;
        return false;
    }

    public TTexture GetOrCreate(TKey key, Func<TTexture> create, Action flush)
    {
        ArgumentNullException.ThrowIfNull(create);
        ArgumentNullException.ThrowIfNull(flush);
        if (TryGet(key, out var cached)) return cached;

        if (_entries.Count == _capacity)
        {
            flush();
            var oldest = _recent.Last!;
            _recent.RemoveLast();
            _entries.Remove(oldest.Value.Key);
            oldest.Value.Texture.Dispose();
        }

        var texture = create();
        _entries.Add(key, _recent.AddFirst((key, texture)));
        return texture;
    }

    public void Dispose()
    {
        foreach (var entry in _recent) entry.Texture.Dispose();
        _entries.Clear();
        _recent.Clear();
    }
}
