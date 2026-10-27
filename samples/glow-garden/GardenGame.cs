using Gum.Forms;
using Gum.Forms.Controls;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using MonoGameGum;
using Systemic.Presentation;
using Color = Microsoft.Xna.Framework.Color;
using Vector2 = Microsoft.Xna.Framework.Vector2;

namespace GlowGarden;

public sealed class GardenGame : Game
{
    private readonly GardenOptions _options;
    private readonly GardenSession _session;
    private SpriteBatch _batch = null!;
    private Texture2D _pixel = null!, _disc = null!, _terrain = null!;
    private RenderTarget2D _frame = null!;
    private GardenUi _ui = null!;
    private KeyboardState _previous;
    private int _frames;
    private readonly List<PresentationCue> _cues = new(256);
    private readonly Dictionary<(string Recipe, string State), Texture2D> _cueTextures = new();
    private Dictionary<string, VisualRecipe> _cueRecipes = new(StringComparer.Ordinal);
    private const float Scale = .42f;
    private static readonly Vector2 Origin = new(20, 120);
    public GardenHost Host => _session.Host;
    public bool Paused { get; private set; }
    public bool Bulk { get; set; }
    public int Speed { get; private set; } = 1;

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

    public void TogglePause() => Paused = !Paused;
    public void CycleSpeed() => Speed = Speed == 1 ? 2 : Speed == 2 ? 4 : 1;
    public void Restart()
    {
        _session.Restart();
        Paused = false;
        Speed = 1;
        _cues.Clear();
    }
    protected override void Update(GameTime gameTime)
    {
        var keyboard = Keyboard.GetState();
        bool Pressed(Keys key) => keyboard.IsKeyDown(key) && !_previous.IsKeyDown(key);
        if (Pressed(Keys.Escape)) Exit();
        if (Pressed(Keys.Space)) TogglePause();
        if (Pressed(Keys.F)) CycleSpeed();
        if (Pressed(Keys.R)) Restart();
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
        while (Host.Game.PresentationCues().TryDequeue(out var cue))
        {
            if (_cues.Count == 512) _cues.RemoveAt(0);
            _cues.Add(cue);
        }
        for (var i = _cues.Count - 1; i >= 0; i--)
        {
            if (Host.Simulation.Tick - _cues[i].Tick > 300) _cues.RemoveAt(i);
        }
        _ui.Refresh();
        base.Update(gameTime);
    }
    private static Vector2 At(System.Numerics.Vector2 p) => Origin + new Vector2(p.X, p.Y) * Scale;

    /// <summary>Fact cues drive burst and talent feedback; position, scale, opacity and particles come from content.</summary>
    private void DrawCue(PresentationCue cue, int tick)
    {
        var age = tick - cue.Tick;
        if (age < 0 || !Host.Game.TrySamplePresentation(cue, age, out var frame)
            || !Host.Game.TryGetPresentationTimeline(cue.TimelineId, out var timeline)
            || !_cueRecipes.TryGetValue(timeline.VisualRecipeId, out var recipe)) return;

        var state = frame.VisualState ?? string.Empty;
        if (!_cueTextures.TryGetValue((recipe.Id, state), out var texture))
        {
            var buffer = ProceduralRasterizer.Rasterize(recipe, cue.VisualSeed, frame.VisualState);
            texture = new Texture2D(GraphicsDevice, buffer.Width, buffer.Height);
            var pixels = new Color[buffer.Width * buffer.Height];
            var rgba = buffer.Pixels.Span;
            for (var i = 0; i < pixels.Length; i++)
                pixels[i] = new Color(rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]);
            texture.SetData(pixels);
            _cueTextures[(recipe.Id, state)] = texture;
        }

        var center = At(new System.Numerics.Vector2(frame.Position.X, frame.Position.Y));
        var opacity = Math.Clamp(frame.Opacity, 0, 1);
        _batch.Draw(texture, center, null, Color.White * opacity, 0f,
            new Vector2(texture.Width / 2f, texture.Height / 2f), Scale * frame.Scale, SpriteEffects.None, 0f);

        if (cue.ParticleCount == 0 || recipe.Palette.IsDefaultOrEmpty) return;
        var spark = ColorFromHex(recipe.Palette[^1]) * opacity;
        for (var i = 0; i < cue.ParticleCount; i++)
        {
            var seed = cue.VisualSeed + (ulong)i * 0x9E3779B97F4A7C15UL;
            var angle = (float)(seed % 6283) / 1000f;
            var distance = 8 + (float)((seed >> 20) % 1000) / 1000f * 22;
            var offset = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * distance * frame.Scale * Scale;
            Disc(center + offset, 1f, spark);
        }
    }

    private static Color ColorFromHex(string hex) => new(
        Convert.ToByte(hex.Substring(1, 2), 16),
        Convert.ToByte(hex.Substring(3, 2), 16),
        Convert.ToByte(hex.Substring(5, 2), 16),
        hex.Length >= 9 ? Convert.ToByte(hex.Substring(7, 2), 16) : (byte)255);
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
        _batch.Begin(samplerState: SamplerState.LinearClamp);
        _batch.Draw(_terrain, Origin, null, Color.White, 0f, Vector2.Zero, Scale * 4f, SpriteEffects.None, 0f);
        _batch.End();
        _batch.Begin(samplerState: SamplerState.PointClamp);
        // Deterministic decorative grass is rendering only: it never consumes World RNG.
        for (var i = 0; i < 190; i++)
        {
            var p = new Vector2(36 + i * 137 % 782, 156 + i * 73 % 475);
            Line(p, p + new Vector2(1.5f, -3), 1, new Color(27, 63, 58));
        }
        var model = Host.Simulation;
        var colony = At(GardenSimulation.Colony);
        Disc(colony, 18, new Color(24, 65, 69));
        Disc(colony, 12, new Color(58, 115, 108));
        Disc(colony, 7, new Color(241, 213, 128));
        foreach (var plant in model.Plants)
        {
            var p = At(plant.Position);
            Disc(p + new Vector2(0, 4), 8, new Color(8, 24, 31));
            Line(p + new Vector2(0, 3), p - new Vector2(0, 9), 2, new Color(74, 118, 84));
            Disc(p + new Vector2(-4, -2), 3.2f, new Color(59, 109, 82));
            Disc(p + new Vector2(4, -4), 3.2f, new Color(79, 139, 97));
            Disc(p - new Vector2(0, 9), 2.5f + plant.Growth * 4.5f, plant.Growth >= 1 ? new Color(247, 215, 106) : new Color(102, 124, 99));
        }
        foreach (var mote in model.Motes)
        {
            var p = At(mote.Position);
            Disc(p, 3, new Color(91, 92, 43, 70));
            Disc(p, 1.2f, new Color(255, 231, 139));
        }
        foreach (var creature in model.Creatures)
        {
            var ground = At(creature.Position);
            var p = ground - new Vector2(0, MathF.Sin(creature.LeapProgress * MathF.PI) * 12);
            Disc(ground + new Vector2(0, 2.5f), 3, new Color(6, 22, 27));
            if (creature.LeapProgress > 0) Line(ground, p, 1, new Color(119, 91, 113));
            var color = creature.Kind == CreatureKind.Burstling ? new Color(208, 125, 157) : new Color(113, 194, 187);
            Disc(p, creature.IsCopy ? 2.5f : 3.5f, creature.IsCopy ? new Color(166, 150, 221) : color);
            Disc(p + new Vector2(-1.2f, -1.2f), 0.7f, Color.White);
            Disc(p + new Vector2(1.2f, -1.2f), 0.7f, Color.White);
            for (var i = 0; i < Math.Min(creature.CarriedGlow, 12); i++)
                Disc(p + new Vector2((i % 4 - 1.5f) * 1.8f, 5 + i / 4 * 1.8f), 0.9f, new Color(255, 222, 127));
        }
        foreach (var impact in model.PendingImpacts)
        {
            var p = At(impact.Position);
            Disc(p - new Vector2(0, Math.Max(0, impact.DueTick - model.Tick)), 2.5f, impact.IsSeed ? new Color(148, 224, 133) : new Color(189, 155, 239));
            Disc(p, 1.2f, new Color(86, 104, 111));
        }
        _batch.End();
        _batch.Begin(samplerState: SamplerState.PointClamp, blendState: BlendState.NonPremultiplied);
        foreach (var cue in _cues) DrawCue(cue, model.Tick);
        _batch.End();
        GumService.Default.Draw();
        GraphicsDevice.SetRenderTarget(null);
        _batch.Begin();
        _batch.Draw(_frame, Vector2.Zero, Color.White);
        _batch.End();
        if (_options.VerifyRender && ++_frames == 480)
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
            foreach (var texture in _cueTextures.Values) texture.Dispose();
            _cueTextures.Clear();
            _frame?.Dispose();
            _disc?.Dispose();
            _terrain?.Dispose();
            _pixel?.Dispose();
            _batch?.Dispose();
        }
        base.Dispose(disposing);
    }
}
