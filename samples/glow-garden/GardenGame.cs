using Gum.Forms;
using Gum.Forms.Controls;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using MonoGameGum;
using Vector2 = Microsoft.Xna.Framework.Vector2;

namespace GlowGarden;

public sealed class GardenGame : Game
{
    private readonly GardenOptions _options;
    private readonly GardenSession _session;
    private SpriteBatch _batch = null!;
    private Texture2D _pixel = null!, _disc = null!;
    private RenderTarget2D _frame = null!;
    private GardenUi _ui = null!;
    private KeyboardState _previous;
    private int _frames;
    private readonly List<(Vector2 Position, float Life)> _rings = new(128);
    private readonly Dictionary<int, float> _growth = new();
    private const float Scale = .78f;
    private static readonly Vector2 Origin = new(45, 145);
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
    }
    public void TogglePause() => Paused = !Paused;
    public void CycleSpeed() => Speed = Speed == 1 ? 2 : Speed == 2 ? 4 : 1;
    public void Restart()
    {
        _session.Restart();
        Paused = false;
        Speed = 1;
        _rings.Clear();
        _growth.Clear();
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
        for (var i = _rings.Count - 1; i >= 0; i--)
        {
            var ring = _rings[i];
            ring.Life -= (float)gameTime.ElapsedGameTime.TotalSeconds;
            if (ring.Life <= 0) _rings.RemoveAt(i); else _rings[i] = ring;
        }
        foreach (var plant in Host.Simulation.Plants)
        {
            if (_growth.TryGetValue(plant.Id, out var growth) && growth > plant.Growth && _rings.Count < 128)
                _rings.Add((At(plant.Position), .5f));
            _growth[plant.Id] = plant.Growth;
        }
        _ui.Refresh();
        base.Update(gameTime);
    }
    private static Vector2 At(System.Numerics.Vector2 p) => Origin + new Vector2(p.X, p.Y) * Scale;
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
        // Deterministic decorative grass is rendering only: it never consumes World RNG.
        for (var i = 0; i < 150; i++)
        {
            var p = new Vector2(36 + i * 137 % 782, 156 + i * 73 % 475);
            Line(p, p + new Vector2(2, -4), 1, new Color(27, 63, 58));
        }
        var model = Host.Simulation;
        var colony = At(GardenSimulation.Colony);
        Disc(colony, 32, new Color(24, 65, 69));
        Disc(colony, 20, new Color(58, 115, 108));
        Disc(colony, 11, new Color(241, 213, 128));
        foreach (var plant in model.Plants)
        {
            var p = At(plant.Position);
            Disc(p + new Vector2(0, 6), 13, new Color(8, 24, 31));
            Line(p + new Vector2(0, 5), p - new Vector2(0, 15), 3, new Color(74, 118, 84));
            Disc(p + new Vector2(-6, -3), 5, new Color(59, 109, 82));
            Disc(p + new Vector2(6, -7), 5, new Color(79, 139, 97));
            Disc(p - new Vector2(0, 15), 4 + plant.Growth * 7, plant.Growth >= 1 ? new Color(247, 215, 106) : new Color(102, 124, 99));
        }
        foreach (var mote in model.Motes)
        {
            var p = At(mote.Position);
            Disc(p, 5, new Color(91, 92, 43, 70));
            Disc(p, 2, new Color(255, 231, 139));
        }
        foreach (var creature in model.Creatures)
        {
            var ground = At(creature.Position);
            var p = ground - new Vector2(0, MathF.Sin(creature.LeapProgress * MathF.PI) * 19);
            Disc(ground + new Vector2(0, 4), 5, new Color(6, 22, 27));
            if (creature.LeapProgress > 0) Line(ground, p, 1, new Color(119, 91, 113));
            var color = creature.Kind == CreatureKind.Burstling ? new Color(208, 125, 157) : new Color(113, 194, 187);
            Disc(p, creature.IsCopy ? 4 : 6, creature.IsCopy ? new Color(166, 150, 221) : color);
            Disc(p + new Vector2(-2, -2), 1, Color.White);
            Disc(p + new Vector2(2, -2), 1, Color.White);
            for (var i = 0; i < Math.Min(creature.CarriedGlow, 12); i++)
                Disc(p + new Vector2((i % 4 - 1.5f) * 3, 8 + i / 4 * 3), 1.5f, new Color(255, 222, 127));
        }
        foreach (var impact in model.PendingImpacts)
        {
            var p = At(impact.Position);
            Disc(p - new Vector2(0, Math.Max(0, impact.DueTick - model.Tick)), 4, impact.IsSeed ? new Color(148, 224, 133) : new Color(189, 155, 239));
            Disc(p, 2, new Color(86, 104, 111));
        }
        foreach (var ring in _rings)
        {
            var radius = (1 - ring.Life * 2) * 26 + 7;
            for (var i = 0; i < 24; i++)
            {
                var angle = i * MathF.Tau / 24;
                Disc(ring.Position + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius, 1.5f, new Color(242, 210, 130) * (ring.Life * 2));
            }
        }
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
            _frame?.Dispose();
            _disc?.Dispose();
            _pixel?.Dispose();
            _batch?.Dispose();
        }
        base.Dispose(disposing);
    }
}
