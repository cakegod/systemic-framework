using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using XnaColor = Microsoft.Xna.Framework.Color;

namespace PixelMine;

public sealed class MineGame : Game
{
    private const int ScreenWidth = 1280;
    private const int ScreenHeight = 800;
    private const int HudMargin = 26;
    private const int IconCellSize = 24;
    private const int IconSize = IconCellSize * 2;
    private const float TypeLabel = 1.5f;
    private const float TypeValue = 2f;
    private const float TypeDisplay = 3f;
    private const int UpgradeStripHeight = 100;
    private const int UpgradeItemWidth = 76;
    private const int UpgradeItemGap = 12;
    private const int UpgradeCount = MineHudFocus.UpgradeCount;
    private const int HitFlashCapacity = 24;
    private const int HitFlashDuration = 6;
    private const int DustPopDuration = 12;
    private const int DustPopCooldown = 24;
    private const float MinimumZoom = 2f / 3f;
    private const float MaximumZoom = 16f;

    private static readonly XnaColor s_ballColor = new(255, 144, 0);
    private static readonly XnaColor s_outline = new(10, 7, 4, 235);
    private static readonly XnaColor s_label = new(226, 190, 132);
    private static readonly XnaColor s_muted = new(214, 192, 158);
    private static readonly XnaColor s_prismColor = new(206, 152, 255);
    private static readonly XnaColor s_minedColor = new(154, 216, 104);
    private static readonly XnaColor s_goldColor = new(255, 216, 64);
    private static readonly XnaColor s_keyLabel = new(226, 190, 132, 220);
    private static readonly XnaColor s_brass = new(158, 115, 65);
    private static readonly XnaColor s_ivory = new(255, 235, 193);
    private static readonly XnaColor s_obsidian = new(18, 23, 21);
    private static readonly Rectangle s_resources = new(HudMargin, HudMargin, 302, 124);
    private static readonly Rectangle s_upgradeStrip =
        new((ScreenWidth - (UpgradeCount * UpgradeItemWidth) - ((UpgradeCount - 1) * UpgradeItemGap)) / 2,
            ScreenHeight - HudMargin - UpgradeStripHeight,
            (UpgradeCount * UpgradeItemWidth) + ((UpgradeCount - 1) * UpgradeItemGap),
            UpgradeStripHeight);
    private static readonly Rectangle s_upgradeDetail = new((ScreenWidth - 560) / 2, s_upgradeStrip.Top - 168, 560, 144);
    private static readonly Rectangle s_ballStatus = new(ScreenWidth - HudMargin - 280, HudMargin, 280, 124);
    private static readonly Rectangle s_ballDetail = new(ScreenWidth - HudMargin - 300, HudMargin + 140, 300, 112);
    private static readonly Rectangle s_compass = new((ScreenWidth - 272) / 2, HudMargin, 272, 60);

    private enum HudIcon
    {
        Dust,
        Prism,
        Ball,
        Mined,
        Power,
        Speed,
        Balls,
        Auto,
        Rod,
        Smash,
        Drill
    }

    private readonly GraphicsDeviceManager _graphics;
    private readonly MineOptions _options;
    private readonly PixelMineHost _host;
    private readonly PixelMineSimulation _simulation;
    private readonly MineMenu _menu;
    private readonly MineHudFocus _hudFocus = new();
    private readonly HitFlash[] _hitFlashes = new HitFlash[HitFlashCapacity];
    private SpriteBatch _batch = null!;
    private PixelFont _font = null!;
    private Texture2D _iconAtlas = null!;
    private readonly RasterizerState _hudClip = new() { ScissorTestEnable = true };
    private PixelMineRenderer _renderer = null!;
    private MineMenuBackdrop _menuBackdrop = null!;
    private RenderTarget2D _frame = null!;
    private KeyboardState _keyboard;
    private KeyboardState _previousKeyboard;
    private MouseState _previousMouse;
    private float _zoom = 4f;
    private int _cameraLeft;
    private int _cameraTop;
    private BallKind _selected = BallKind.Basic;
    private bool _paused;
    private long _previousDust;
    private long _pendingDustGain;
    private long _dustPopAmount;
    private int _dustPopTicks;
    private int _dustPopCooldown;
    private int _nextHitFlash;
    private int _frames;
    private float _menuBallY = 450f;

    public MineGame(MineOptions options)
    {
        _options = options;
        _host = PixelMineHost.Create(options.Seed);
        _simulation = _host.Simulation;
        _menu = new MineMenu(options.VerifyRender);
        _previousDust = _simulation.Dust;

        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = ScreenWidth,
            PreferredBackBufferHeight = ScreenHeight,
            GraphicsProfile = GraphicsProfile.HiDef,
            IsFullScreen = false,
            HardwareModeSwitch = false,
            SynchronizeWithVerticalRetrace = true
        };
        IsFixedTimeStep = true;
        TargetElapsedTime = TimeSpan.FromSeconds(1.0 / 60.0);
        IsMouseVisible = true;
        Window.Title = "Systemic pixel mine";

        _cameraLeft = Math.Clamp(_simulation.World.SpawnX - (ViewWidth / 2), 0, PixelMineWorld.Width - ViewWidth);
        _cameraTop = Math.Clamp(_simulation.World.SpawnY - (ViewHeight / 2), 0, PixelMineWorld.Height - ViewHeight);
    }

    private int PixelScale => Math.Max(1, (int)MathF.Round(_zoom * 10f));

    private int ViewWidth => ScreenWidth / PixelScale;

    private int ViewHeight => ScreenHeight / PixelScale;

    protected override void LoadContent()
    {
        _batch = new SpriteBatch(GraphicsDevice);
        _font = new PixelFont(GraphicsDevice);
        _iconAtlas = Texture2D.FromFile(GraphicsDevice, Path.Combine(AppContext.BaseDirectory, "content", "hud-relics.png"));
        if (_iconAtlas.Width != Enum.GetValues<HudIcon>().Length * IconCellSize || _iconAtlas.Height != IconCellSize)
        {
            throw new InvalidOperationException("HUD relic atlas must contain eleven 24x24 icons.");
        }

        _renderer = new PixelMineRenderer(GraphicsDevice, _simulation.World);
        _menuBackdrop = new MineMenuBackdrop(GraphicsDevice);
        _frame = new RenderTarget2D(GraphicsDevice, ScreenWidth, ScreenHeight);
    }

    protected override void Update(GameTime gameTime)
    {
        var keyboard = Keyboard.GetState();
        var mouse = Mouse.GetState();

        if (Pressed(keyboard, Keys.F11))
        {
            ToggleFullscreen();
        }

        if (_menu.Screen != MineScreen.Playing)
        {
            if (_menu.Screen == MineScreen.Title)
            {
                _menuBackdrop.Update(gameTime.ElapsedGameTime);
            }

            UpdateMenu(keyboard, mouse);
            if (_menu.Screen == MineScreen.Title)
            {
                var seconds = Math.Clamp((float)gameTime.ElapsedGameTime.TotalSeconds, 0f, 0.05f);
                _menuBallY += (MenuRow(_menu.Selection).Center.Y - _menuBallY) * (1f - MathF.Exp(-12f * seconds));
            }

            _previousKeyboard = keyboard;
            _previousMouse = mouse;
            base.Update(gameTime);
            return;
        }

        if (Pressed(keyboard, Keys.Escape))
        {
            Exit();
        }

        if (Pressed(keyboard, Keys.Space))
        {
            _paused = !_paused;
        }

        if (Pressed(keyboard, Keys.Tab))
        {
            _selected = _selected switch
            {
                BallKind.Basic when _simulation.IsUnlocked(BallKind.Smash) => BallKind.Smash,
                BallKind.Basic => _selected,
                BallKind.Smash when _simulation.IsUnlocked(BallKind.Directional) => BallKind.Directional,
                BallKind.Smash => BallKind.Basic,
                _ => BallKind.Basic
            };

            _hudFocus.InspectBall();
        }

        for (var index = 0; index < _simulation.Upgrades.Count; index++)
        {
            if (Pressed(keyboard, Keys.D1 + index))
            {
                _simulation.RequestUpgrade((UpgradeKind)index);
                _hudFocus.InspectUpgrade(index);
            }
        }

        HandleZoom(mouse);
        var cursorPosition = CanvasPosition(mouse);
        var hoveredUpgrade = _hudFocus.UpgradeReveal > 0f && s_upgradeDetail.Contains(cursorPosition)
            ? _hudFocus.UpgradeIndex
            : UpgradeAt((int)cursorPosition.X, (int)cursorPosition.Y);
        _hudFocus.Update(gameTime.ElapsedGameTime, hoveredUpgrade,
            s_ballStatus.Contains(cursorPosition)
            || (_hudFocus.BallReveal > 0f && s_ballDetail.Contains(cursorPosition)));

        if (mouse.LeftButton == ButtonState.Pressed
            && _previousMouse.LeftButton == ButtonState.Released
            && !IsOverHud(cursorPosition))
        {
            var worldX = _cameraLeft + (int)(cursorPosition.X / PixelScale);
            var worldY = _cameraTop + (int)(cursorPosition.Y / PixelScale);
            _simulation.RequestShot(worldX, worldY, _selected);
        }

        _keyboard = keyboard;
        _host.Game.Clock.Advance(gameTime.ElapsedGameTime, StepSimulation);
        for (var index = 0; index < _simulation.Upgrades.Count; index++)
        {
            _hudFocus.ObserveUpgrade(index, _simulation.Upgrades[index].Level);
        }

        _previousKeyboard = keyboard;
        _previousMouse = mouse;
        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        var worldX = _cameraLeft + (ViewWidth / 2);
        var worldY = _cameraTop + (ViewHeight / 2);
        _cameraLeft = Math.Clamp(worldX - (ViewWidth / 2), 0, PixelMineWorld.Width - ViewWidth);
        _cameraTop = Math.Clamp(worldY - (ViewHeight / 2), 0, PixelMineWorld.Height - ViewHeight);

        GraphicsDevice.SetRenderTarget(_frame);
        if (_menu.Screen == MineScreen.Playing)
        {
            GraphicsDevice.Clear(new XnaColor(12, 10, 16));
            _renderer.Update(_cameraLeft, _cameraTop, ViewWidth, ViewHeight);
        }
        else
        {
            GraphicsDevice.Clear(new XnaColor(8, 24, 17));
        }

        _batch.Begin(samplerState: SamplerState.PointClamp);
        if (_menu.Screen == MineScreen.Playing)
        {
            _renderer.DrawWorld(_batch, new Rectangle(0, 0, ScreenWidth, ScreenHeight));
            _renderer.DrawPrismGlows(_batch, PixelScale);
            DrawHitFlashes();
            DrawBalls();
            DrawHud();
        }
        else
        {
            DrawMenu();
        }

        _batch.End();

        GraphicsDevice.SetRenderTarget(null);
        _batch.Begin(samplerState: SamplerState.PointClamp);
        _batch.Draw(_frame, GraphicsDevice.PresentationParameters.Bounds, XnaColor.White);
        _batch.End();

        _frames++;
        if (_options.VerifyRender && _frames == 150)
        {
            Verify();
        }

        base.Draw(gameTime);
    }

    private void StepSimulation()
    {
        HandlePan(_keyboard);
        UpdateHitFlashTimers();
        if (_paused)
        {
            return;
        }

        _host.Step(1);
        UpdateMiningFeedback();
    }

    private void UpdateMenu(KeyboardState keyboard, MouseState mouse)
    {
        if (Pressed(keyboard, Keys.Escape))
        {
            if (_menu.Screen == MineScreen.Settings)
            {
                _menu.Back();
            }
            else
            {
                Exit();
            }

            return;
        }

        if (Pressed(keyboard, Keys.Up))
        {
            _menu.Move(-1);
        }
        else if (Pressed(keyboard, Keys.Down))
        {
            _menu.Move(1);
        }

        var cursor = CanvasPosition(mouse);
        var clicked = mouse.LeftButton == ButtonState.Pressed && _previousMouse.LeftButton == ButtonState.Released;
        if (clicked || mouse.X != _previousMouse.X || mouse.Y != _previousMouse.Y)
        {
            for (var index = 0; index < _menu.ItemCount; index++)
            {
                if (MenuRow(index).Contains(cursor))
                {
                    _menu.Select(index);
                    if (clicked)
                    {
                        var valueWidth = _font.Measure(FrameLimitText(), 2.5f);
                        var direction = _menu.Screen == MineScreen.Settings && index == 1
                            && cursor.X >= 858 - valueWidth && cursor.X < 858 - (valueWidth / 2) ? -1 : 1;
                        _menu.Activate(direction);
                        ApplySettings();
                    }

                    return;
                }
            }
        }

        if (Pressed(keyboard, Keys.Enter) || Pressed(keyboard, Keys.Space))
        {
            _menu.Activate();
            ApplySettings();
        }
        else if (_menu.Screen == MineScreen.Settings && _menu.Selection < 2
            && (Pressed(keyboard, Keys.Left) || Pressed(keyboard, Keys.Right)))
        {
            _menu.Activate(Pressed(keyboard, Keys.Left) ? -1 : 1);
            ApplySettings();
        }
    }

    private void ApplySettings()
    {
        if (_graphics.SynchronizeWithVerticalRetrace != _menu.VSync)
        {
            _graphics.SynchronizeWithVerticalRetrace = _menu.VSync;
            _graphics.ApplyChanges();
        }

        IsFixedTimeStep = _menu.FrameLimit != 0;
        if (IsFixedTimeStep)
        {
            TargetElapsedTime = TimeSpan.FromSeconds(1.0 / _menu.FrameLimit);
        }
    }

    private void DrawMenu()
    {
        if (_menu.Screen == MineScreen.Title)
        {
            _menuBackdrop.Draw(_batch, _renderer);
            _font.Draw(_batch, "PIXEL", 64, 90, 14f, new XnaColor(255, 214, 140));
            _font.Draw(_batch, "MINE", 64, 198, 14f, new XnaColor(255, 144, 0));
            _font.Draw(_batch, "10 MILLION PIXELS", 68, 330, 2f, new XnaColor(188, 205, 180));
        }
        else
        {
            const string title = "SETTINGS";
            _font.Draw(_batch, title, (ScreenWidth - _font.Measure(title, 6f)) / 2, 150, 6f, new XnaColor(255, 214, 140));
        }

        for (var index = 0; index < _menu.ItemCount; index++)
        {
            var row = MenuRow(index);
            var selected = index == _menu.Selection;
            var titleScreen = _menu.Screen == MineScreen.Title;
            if (titleScreen)
            {
                DrawTitleButton(row, index, selected);
            }
            else
            {
                _batch.Draw(_renderer.White, row, selected ? new XnaColor(77, 57, 43) : new XnaColor(25, 38, 31));
            }

            var label = titleScreen
                ? index == 0 ? "START" : "SETTINGS"
                : index == 0 ? "VSYNC" : index == 1 ? "FPS LIMIT" : "BACK";
            var color = selected ? new XnaColor(255, 214, 140) : new XnaColor(220, 217, 220);
            _font.Draw(_batch, label, row.X + (titleScreen ? 38 : 20), row.Y + (titleScreen ? 26 : 20), titleScreen ? 4f : 3f, color);

            if (_menu.Screen == MineScreen.Settings && index < 2)
            {
                var value = index == 0 ? (_menu.VSync ? "< ON >" : "< OFF >") : FrameLimitText();
                _font.Draw(_batch, value, 858 - _font.Measure(value, 2.5f), row.Y + 22, 2.5f, color);
            }
        }

        if (_menu.Screen == MineScreen.Title)
        {
            var row = MenuRow(_menu.Selection);
            var ballOffset = MenuBallOffset(_menuBackdrop.Elapsed);
            var impact = MathF.Pow((ballOffset - 40f) / 390f, 16f);
            _renderer.DrawHitFlash(_batch, new Rectangle(row.Right - 32, row.Bottom - 32, 32, 32), 1f, (int)(180 * impact));
            _renderer.DrawBall(_batch, new Vector2(row.X + ballOffset, _menuBallY + 30f), 6f, BallKind.Basic);
            _font.Draw(_batch, "ARROWS/ENTER  MOUSE  F11 FULLSCREEN  ESC QUIT", 68, 706, 1.5f, new XnaColor(190, 202, 189));
        }
        else
        {
            const string hint = "UP/DOWN SELECT  LEFT/RIGHT CHANGE  ESC BACK";
            const string note = "VSYNC CAN LIMIT FPS TO DISPLAY RATE";
            _font.Draw(_batch, hint, (ScreenWidth - _font.Measure(hint, 1.5f)) / 2, 670, 1.5f, new XnaColor(190, 202, 189));
            _font.Draw(_batch, note, (ScreenWidth - _font.Measure(note, 1.5f)) / 2, 700, 1.5f, new XnaColor(190, 202, 189));
        }
    }

    private void DrawTitleButton(Rectangle row, int index, bool selected)
    {
        var colors = MineMenuBackdrop.RockColors;
        var shade = selected ? 1f : 0.35f;
        var ballOffset = MenuBallOffset(_menuBackdrop.Elapsed);
        for (var layer = 0; layer < 2; layer++)
        {
            for (var column = 0; column * 32 < row.Width; column++)
            {
                var rock = colors[((column * 3) + (layer * 5) + (index * 2)) % colors.Length];
                var color = new XnaColor((int)(rock.R * shade), (int)(rock.G * shade), (int)(rock.B * shade));
                var light = selected ? Math.Clamp(1f - (MathF.Abs((column * 32) + 16f - ballOffset) / 56f), 0f, 1f) * 0.5f : 0f;
                color = XnaColor.Lerp(color, new XnaColor(255, 248, 200), light);
                var block = new Rectangle(row.X + (column * 32), row.Y + (layer * 40), Math.Min(32, row.Width - (column * 32)), 40);
                _batch.Draw(_renderer.White, block, color);
            }
        }

        var cave = new XnaColor(4, 18, 8);
        _batch.Draw(_renderer.White, new Rectangle(row.X + 24, row.Y + 18, row.Width - 72, 44), cave);
        _batch.Draw(_renderer.White, new Rectangle(row.X + 12, row.Y + 24, row.Width - 52, 32), cave);
    }

    internal static float MenuBallOffset(float elapsed) => 40f + (195f * (MathF.Sin(elapsed * 2.2f) + 1f));

    private Rectangle MenuRow(int index) => _menu.Screen == MineScreen.Title
        ? new Rectangle(64, 410 + (index * 96), 460, 80)
        : new Rectangle(390, 278 + (index * 76), 500, 62);

    private string FrameLimitText() => _menu.FrameLimit == 0 ? "< UNLIMITED >" : $"< {_menu.FrameLimit} >";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _host.Dispose();
            _menuBackdrop?.Dispose();
            _renderer?.Dispose();
            _font?.Dispose();
            _iconAtlas?.Dispose();
            _hudClip.Dispose();
            _batch?.Dispose();
            _frame?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void DrawBalls()
    {
        var balls = _simulation.Balls;
        for (var row = 0; row < balls.Length; row++)
        {
            var ball = balls[row];
            if (ball.Kind < 0)
            {
                continue;
            }

            var screenX = ((ball.X / 65536f) - _cameraLeft) * PixelScale;
            var screenY = ((ball.Y / 65536f) - _cameraTop) * PixelScale;
            if (screenX < -32 || screenY < -32 || screenX > ScreenWidth + 32 || screenY > ScreenHeight + 32)
            {
                continue;
            }

            var kind = (BallKind)ball.Kind;
            _renderer.DrawBall(_batch, new Vector2(screenX, screenY), (kind == BallKind.Smash ? 3.2f : 2.4f) * (_zoom / 2f), kind);
        }

        var cursor = Mouse.GetState();
        _renderer.DrawObjectMarker(_batch, CanvasPosition(cursor), 6f, new XnaColor(255, 255, 255, 120));
    }

    private void UpdateMiningFeedback()
    {
        var dust = _simulation.Dust;
        if (dust > _previousDust)
        {
            _pendingDustGain += dust - _previousDust;
        }

        _previousDust = dust;
        if (_dustPopCooldown > 0)
        {
            _dustPopCooldown--;
        }

        if (_dustPopTicks > 0)
        {
            _dustPopTicks--;
        }

        if (_dustPopCooldown == 0 && _pendingDustGain > 0)
        {
            _dustPopAmount = _pendingDustGain;
            _pendingDustGain = 0;
            _dustPopTicks = DustPopDuration;
            _dustPopCooldown = DustPopCooldown;
        }

        foreach (var hitEvent in _simulation.HitEvents)
        {
            AddHitFlash(hitEvent);
        }
    }

    private void AddHitFlash(MineHitEvent hitEvent)
    {
        for (var index = 0; index < _hitFlashes.Length; index++)
        {
            if (_hitFlashes[index].Life > 0
                && _hitFlashes[index].X == hitEvent.X
                && _hitFlashes[index].Y == hitEvent.Y)
            {
                _hitFlashes[index].Life = HitFlashDuration;
                return;
            }
        }

        _hitFlashes[_nextHitFlash] = new HitFlash
        {
            X = hitEvent.X,
            Y = hitEvent.Y,
            Life = HitFlashDuration
        };
        _nextHitFlash = (_nextHitFlash + 1) % _hitFlashes.Length;
    }

    private void UpdateHitFlashTimers()
    {
        for (var index = 0; index < _hitFlashes.Length; index++)
        {
            if (_hitFlashes[index].Life > 0)
            {
                _hitFlashes[index].Life--;
            }
        }
    }

    private void DrawHitFlashes()
    {
        foreach (var hitFlash in _hitFlashes)
        {
            if (hitFlash.Life <= 0 || !_renderer.IsVisibleCell(hitFlash.X, hitFlash.Y))
            {
                continue;
            }

            var intensity = hitFlash.Life / (float)HitFlashDuration;
            var x = (hitFlash.X - _cameraLeft) * PixelScale;
            var y = (hitFlash.Y - _cameraTop) * PixelScale;
            _renderer.DrawHitFlash(_batch, new Rectangle(x, y, PixelScale, PixelScale), 1f + (0.12f * intensity),
                (int)(190 * intensity));
        }
    }

    private void DrawHud()
    {
        if (_paused || _simulation.Won)
        {
            _batch.Draw(_renderer.White, new Rectangle(0, 0, ScreenWidth, ScreenHeight), new XnaColor(3, 10, 6) * 0.75f);
        }

        DrawResources();
        DrawStatus();
        DrawUpgrades();
        if (_simulation.HasRod)
        {
            DrawCompass();
        }

        if (_simulation.Message.Length > 0)
        {
            DrawToast(_simulation.Message);
        }

        if (_paused)
        {
            DrawBanner("PAUSED", "SPACE TO RESUME", s_ballColor);
        }
        else if (_simulation.Won)
        {
            DrawBanner("GOLDEN PIXEL FOUND", "PRESS ESC TO LEAVE", s_goldColor);
        }

        DrawControls();
    }

    private void DrawHudText(string text, int x, int y, float scale, XnaColor color) =>
        DrawHudText(text, x, y, scale, color, s_outline);

    private void DrawHudText(string text, int x, int y, float scale, XnaColor color, XnaColor outline)
    {
        var thickness = scale >= TypeDisplay ? 2 : 1;
        for (var offsetY = -thickness; offsetY <= thickness; offsetY++)
        {
            for (var offsetX = -thickness; offsetX <= thickness; offsetX++)
            {
                if (offsetX == 0 && offsetY == 0)
                {
                    continue;
                }

                _font.Draw(_batch, text, x + offsetX, y + offsetY, scale, outline);
            }
        }

        _font.Draw(_batch, text, x, y, scale, color);
    }

    private void DrawHudTextRight(string text, int right, int y, float scale, XnaColor color) =>
        DrawHudText(text, right - _font.Measure(text, scale), y, scale, color);

    private void DrawHudTextCentered(string text, int y, float scale, XnaColor color) =>
        DrawHudText(text, (ScreenWidth - _font.Measure(text, scale)) / 2, y, scale, color);

    private void DrawIcon(HudIcon icon, int x, int y, int size = IconSize, float opacity = 1f) =>
        _batch.Draw(_iconAtlas, new Rectangle(x, y, size, size),
            new Rectangle((int)icon * IconCellSize, 0, IconCellSize, IconCellSize), XnaColor.White * opacity);

    private void DrawForgePanel(Rectangle bounds, XnaColor edge, float opacity = 1f)
    {
        var x = bounds.X;
        var y = bounds.Y;
        var w = bounds.Width;
        var h = bounds.Height;
        _batch.Draw(_renderer.White, new Rectangle(x + 4, y, w - 8, h), s_outline * opacity);
        _batch.Draw(_renderer.White, new Rectangle(x, y + 4, w, h - 8), s_outline * opacity);
        _batch.Draw(_renderer.White, new Rectangle(x + 2, y + 6, w - 4, h - 12), s_obsidian * opacity);
        _batch.Draw(_renderer.White, new Rectangle(x + 6, y + 2, w - 12, h - 4), s_obsidian * opacity);

        _batch.Draw(_renderer.White, new Rectangle(x + 6, y + 2, w - 12, 2), edge * opacity);
        _batch.Draw(_renderer.White, new Rectangle(x + 2, y + 6, 2, h - 12), edge * (0.7f * opacity));
        _batch.Draw(_renderer.White, new Rectangle(x + 6, bounds.Bottom - 4, w - 12, 2), edge * (0.4f * opacity));
        _batch.Draw(_renderer.White, new Rectangle(bounds.Right - 4, y + 6, 2, h - 12), edge * (0.4f * opacity));
        _batch.Draw(_renderer.White, new Rectangle(x + 8, y + 4, Math.Min(28, w - 16), 1), s_ivory * (0.5f * opacity));
    }

    private void DrawResources()
    {
        var x = s_resources.X;
        var y = s_resources.Y;
        var dust = _simulation.Dust.ToString();
        var scale = Math.Min(4f, (s_resources.Width - 106f) / _font.Measure(dust, 1f));
        DrawForgePanel(s_resources, s_brass);
        DrawIcon(HudIcon.Dust, x + 8, y + 4, 72);
        DrawHudText("DUST", x + 90, y + 18, TypeLabel, s_label);
        DrawHudText(dust, x + 90, y + 40, scale, s_ivory);

        if (_dustPopTicks > 0)
        {
            var progress = 1f - (_dustPopTicks / (float)DustPopDuration);
            DrawHudTextRight($"+{_dustPopAmount}", s_resources.Right - 16,
                y + 18 - (int)(progress * 8f), TypeLabel, s_goldColor * (1f - progress));
        }

        _batch.Draw(_renderer.White, new Rectangle(x + 18, y + 82, s_resources.Width - 36, 1), s_brass * 0.4f);
        DrawIcon(HudIcon.Prism, x + 20, y + 92, IconCellSize);
        DrawHudText("PRISMS", x + 56, y + 98, TypeLabel, s_muted);
        DrawHudTextRight($"{_simulation.Prisms}", s_resources.Right - 18, y + 94, TypeValue, s_prismColor);
    }

    private void DrawStatus()
    {
        var x = s_ballStatus.X;
        var y = s_ballStatus.Y;
        var right = s_ballStatus.Right - 18;
        var tint = BallTint(_selected);
        var icon = _selected == BallKind.Smash ? HudIcon.Smash
            : _selected == BallKind.Directional ? HudIcon.Drill : HudIcon.Ball;
        var balls = $"{_simulation.BallCount}/{_simulation.BallLimit}";
        DrawForgePanel(s_ballStatus, XnaColor.Lerp(s_brass, tint, _hudFocus.BallReveal));
        DrawIcon(icon, x + 12, y + 14);
        DrawHudText(balls, x + 76, y + 18, TypeDisplay, s_ivory);
        DrawHudText($"{Name(_selected)} BALL", x + 76, y + 50, TypeLabel, tint);
        DrawHudTextRight("[TAB]", right, y + 50, TypeLabel, s_muted);

        _batch.Draw(_renderer.White, new Rectangle(x + 18, y + 82, s_ballStatus.Width - 36, 1), s_brass * 0.4f);
        DrawIcon(HudIcon.Mined, x + 20, y + 92, IconCellSize);
        DrawHudText("MINED", x + 56, y + 98, TypeLabel, s_muted);
        DrawHudTextRight($"{_simulation.MinedPixels}", right, y + 94, TypeValue, s_minedColor);

        var reveal = _hudFocus.BallReveal;
        if (reveal > 0f)
        {
            BeginFocusSurface(s_ballDetail, reveal, tint, s_ballStatus.Center.X);
            DrawIcon(icon, s_ballDetail.X + 14, s_ballDetail.Y + 10, 72, reveal);
            DrawHudText(Name(_selected), s_ballDetail.X + 104, s_ballDetail.Y + 20, TypeDisplay, tint * reveal, s_outline * reveal);
            DrawHudText(ShotHint(), s_ballDetail.X + 104, s_ballDetail.Y + 53, TypeLabel, s_ivory * reveal, s_outline * reveal);
            DrawHudText("TAB CHANGE BALL", s_ballDetail.X + 24, s_ballDetail.Y + 88, TypeLabel, s_muted * reveal, s_outline * reveal);
            EndFocusSurface();
        }
    }

    private void DrawCompass()
    {
        var (directionX, directionY) = _simulation.GoldDirection;
        var direction = new Vector2(directionX / 256f, directionY / 256f);
        var origin = new Vector2(s_compass.Right - 36, s_compass.Center.Y);
        var tip = origin + (direction * 18f);
        var side = new Vector2(-direction.Y, direction.X) * 6f;
        DrawForgePanel(s_compass, s_brass);
        DrawIcon(HudIcon.Rod, s_compass.X + 8, s_compass.Y + 6);
        DrawHudText("GOLD", s_compass.X + 68, s_compass.Y + 12, TypeLabel, s_label);
        DrawHudText($"{_simulation.GoldDistance}", s_compass.X + 68, s_compass.Y + 32, TypeValue, s_goldColor);

        _renderer.DrawLine(_batch, origin - (direction * 12f), tip, 3f, s_goldColor);
        _renderer.DrawLine(_batch, tip, tip - (direction * 8f) + side, 3f, s_goldColor);
        _renderer.DrawLine(_batch, tip, tip - (direction * 8f) - side, 3f, s_goldColor);
    }

    private void DrawUpgrades()
    {
        var upgrades = _simulation.Upgrades;
        for (var index = 0; index < upgrades.Count; index++)
        {
            var upgrade = upgrades[index];
            var kind = (UpgradeKind)index;
            var bounds = UpgradeBounds(index);
            var focused = _hudFocus.UpgradeIndex == index && _hudFocus.UpgradeReveal > 0f;
            var tint = UpgradeTint(kind);
            var balance = upgrade.UsesPrisms ? _simulation.Prisms : _simulation.Dust;
            var maxed = upgrade.Level >= upgrade.MaxLevel;
            var saved = maxed ? 1f : (float)Math.Clamp(balance / (double)upgrade.Cost, 0d, 1d);
            var ready = !maxed && balance >= upgrade.Cost;
            var flash = _hudFocus.UpgradeFlash(index);
            var focus = focused ? 1f - MathF.Pow(1f - _hudFocus.UpgradeReveal, 3f) : 0f;
            var edge = XnaColor.Lerp(ready || maxed ? s_brass : s_brass * 0.55f, tint, focus);
            DrawForgePanel(bounds, XnaColor.Lerp(edge, s_ivory, flash));

            _batch.Draw(_renderer.White, new Rectangle(bounds.X + 8, bounds.Y + 7, 17, 17),
                focused || ready ? new XnaColor(84, 61, 35) : new XnaColor(43, 45, 35));
            DrawHudText($"{index + 1}", bounds.X + 12, bounds.Y + 10, TypeLabel, focused || ready ? s_ivory : s_keyLabel);
            DrawIcon(UpgradeIcon(kind), bounds.X + 14,
                bounds.Y + 21 - (int)(focus * 4f) - (int)(MathF.Sin(flash * MathF.PI) * 6f));
            DrawHudText(upgrade.Name, bounds.Center.X - (_font.Measure(upgrade.Name, TypeLabel) / 2),
                bounds.Y + 70, TypeLabel, focused || ready || maxed ? s_ivory : s_muted);

            for (var segment = 0; segment < 8; segment++)
            {
                var filled = Math.Clamp((saved * 8f) - segment, 0f, 1f);
                var bar = new Rectangle(bounds.X + 8 + (segment * 8), bounds.Bottom - 10, 5, 3);
                _batch.Draw(_renderer.White, bar, new XnaColor(50, 52, 42));
                _batch.Draw(_renderer.White, new Rectangle(bar.X, bar.Y, (int)MathF.Ceiling(bar.Width * filled), bar.Height),
                    maxed ? s_minedColor : tint);
            }

            if (ready)
            {
                DrawHudText("+", bounds.Right - 19, bounds.Y + 10, TypeLabel, s_goldColor);
            }
            else if (maxed)
            {
                _renderer.DrawLine(_batch, new Vector2(bounds.Right - 21, bounds.Y + 15),
                    new Vector2(bounds.Right - 17, bounds.Y + 19), 2f, s_minedColor);
                _renderer.DrawLine(_batch, new Vector2(bounds.Right - 17, bounds.Y + 19),
                    new Vector2(bounds.Right - 10, bounds.Y + 10), 2f, s_minedColor);
            }

            if (flash > 0f)
            {
                var spread = (1f - flash) * 20f;
                for (var spark = 0; spark < 4; spark++)
                {
                    var offset = (spark - 1.5f) * (10f + spread);
                    var lift = 5f + (spread * (spark is 1 or 2 ? 1.4f : 0.7f));
                    _batch.Draw(_renderer.White, new Rectangle(bounds.Center.X + (int)offset, bounds.Top - (int)lift, 3, 3), s_ivory * flash);
                }
            }
        }

        if (_hudFocus.UpgradeIndex >= 0 && _hudFocus.UpgradeReveal > 0f)
        {
            DrawUpgradeDetail(_hudFocus.UpgradeIndex, _hudFocus.UpgradeReveal);
        }
        else
        {
            DrawHudTextCentered("1-7 BUY / HOVER INSPECT", s_upgradeStrip.Y - 22, TypeLabel, s_muted);
        }
    }

    private void BeginFocusSurface(Rectangle bounds, float reveal, XnaColor accent, int originX)
    {
        var eased = 1f - MathF.Pow(1f - reveal, 3f);
        var width = (int)MathHelper.Lerp(UpgradeItemWidth, bounds.Width, eased);
        var height = (int)MathHelper.Lerp(16, bounds.Height, eased);
        var left = (int)MathHelper.Lerp(originX - (UpgradeItemWidth / 2), bounds.Left, eased);
        var surface = new Rectangle(left, bounds.Bottom - height, width, height);
        DrawForgePanel(surface, accent, reveal);

        _batch.End();
        GraphicsDevice.ScissorRectangle = surface;
        _batch.Begin(samplerState: SamplerState.PointClamp, rasterizerState: _hudClip);
    }

    private void EndFocusSurface()
    {
        _batch.End();

        _batch.Begin(samplerState: SamplerState.PointClamp);
    }

    private void DrawUpgradeDetail(int index, float reveal)
    {
        var upgrade = _simulation.Upgrades[index];
        var tint = UpgradeTint((UpgradeKind)index);
        var balance = upgrade.UsesPrisms ? _simulation.Prisms : _simulation.Dust;
        var maxed = upgrade.Level >= upgrade.MaxLevel;
        var currency = upgrade.UsesPrisms ? "PRISMS" : "DUST";
        var price = maxed ? "MAX LEVEL" : $"{upgrade.Cost} {currency}";
        var action = _hudFocus.UpgradeFlash(index) > 0f ? "FORGED"
            : maxed ? "FULLY UPGRADED"
            : balance < upgrade.Cost ? $"NEED {upgrade.Cost - balance} {currency}"
            : _paused ? "RESUME TO BUY" : $"[{index + 1}] BUY UPGRADE";
        var bounds = s_upgradeDetail;
        var slot = UpgradeBounds(index);
        _batch.Draw(_renderer.White, new Rectangle(slot.Center.X - 1, bounds.Bottom, 2, s_upgradeStrip.Top - bounds.Bottom), tint * (0.65f * reveal));
        BeginFocusSurface(bounds, reveal, tint, slot.Center.X);
        DrawIcon(UpgradeIcon((UpgradeKind)index), bounds.X + 18, bounds.Y + 12, 72, reveal);

        DrawHudText(upgrade.Name, bounds.X + 110, bounds.Y + 18, TypeDisplay, s_ivory * reveal, s_outline * reveal);
        DrawHudTextRight($"L{upgrade.Level}/{upgrade.MaxLevel}", bounds.Right - 22, bounds.Y + 24, TypeValue, tint * reveal);
        DrawHudText(UpgradeDescription((UpgradeKind)index), bounds.X + 110, bounds.Y + 54, TypeLabel, s_muted * reveal, s_outline * reveal);
        _batch.Draw(_renderer.White, new Rectangle(bounds.X + 22, bounds.Y + 92, bounds.Width - 44, 1), s_brass * (0.5f * reveal));

        DrawIcon(upgrade.UsesPrisms ? HudIcon.Prism : HudIcon.Dust, bounds.X + 20, bounds.Y + 106, IconCellSize, reveal);
        DrawHudText(price, bounds.X + 56, bounds.Y + 112, TypeValue,
            (upgrade.UsesPrisms ? s_prismColor : s_goldColor) * reveal, s_outline * reveal);
        DrawHudTextRight(action, bounds.Right - 22, bounds.Y + 115, TypeLabel,
            (maxed || balance >= upgrade.Cost ? s_minedColor : s_muted) * reveal);
        EndFocusSurface();
    }

    private void DrawControls()
    {
        if (_paused)
        {
            DrawHudTextCentered("CLICK SHOOT   TAB CHANGE BALL   1-7 BUY", 422, TypeValue, s_muted);
            DrawHudTextCentered("WASD / ARROWS PAN   WHEEL ZOOM", 456, TypeValue, s_muted);
            DrawHudTextCentered("SHIFT FAST PAN   F11 FULLSCREEN   ESC QUIT", 490, TypeValue, s_muted);
            return;
        }

        DrawHudText("CLICK SHOOT", HudMargin, ScreenHeight - HudMargin - 30, TypeLabel, s_muted);
        DrawHudText("TAB BALL", HudMargin, ScreenHeight - HudMargin - 12, TypeLabel, s_muted);
        DrawHudTextRight("SPACE PAUSE / HELP", ScreenWidth - HudMargin, ScreenHeight - HudMargin - 30, TypeLabel, s_muted);
    }

    private void DrawToast(string message)
    {
        var width = _font.Measure(message, TypeValue) + 40;
        var y = _paused || _simulation.Won ? 264 : s_upgradeDetail.Top - 46;
        DrawForgePanel(new Rectangle((ScreenWidth - width) / 2, y, width, 32), s_brass);
        DrawHudTextCentered(message, y + 9, TypeValue, s_ivory);
    }

    private void DrawBanner(string title, string? subtitle, XnaColor accent)
    {
        DrawForgePanel(new Rectangle((ScreenWidth - 660) / 2, 320, 660, _paused ? 216 : 104), s_brass);
        DrawHudTextCentered(title, (ScreenHeight / 2) - 52, 4f, accent);
        if (subtitle is not null)
        {
            DrawHudTextCentered(subtitle, (ScreenHeight / 2) - 18, TypeValue, s_muted);
        }
    }

    internal static Rectangle UpgradeBounds(int index) =>
        new(s_upgradeStrip.X + (index * (UpgradeItemWidth + UpgradeItemGap)), s_upgradeStrip.Y, UpgradeItemWidth, UpgradeStripHeight);

    internal static int UpgradeAt(int x, int y)
    {
        if (!s_upgradeStrip.Contains(x, y))
        {
            return -1;
        }

        var index = (x - s_upgradeStrip.X) / (UpgradeItemWidth + UpgradeItemGap);
        return UpgradeBounds(index).Contains(x, y) ? index : -1;
    }

    private bool IsOverHud(Vector2 point) => IsOverHud((int)point.X, (int)point.Y);

    private bool IsOverHud(int x, int y) =>
        s_upgradeStrip.Contains(x, y)
        || s_resources.Contains(x, y)
        || s_ballStatus.Contains(x, y)
        || (_simulation.HasRod && s_compass.Contains(x, y))
        || (_hudFocus.UpgradeReveal > 0f && s_upgradeDetail.Contains(x, y))
        || (_hudFocus.BallReveal > 0f && s_ballDetail.Contains(x, y));

    private static string UpgradeDescription(UpgradeKind kind) => kind switch
    {
        UpgradeKind.Power => "BREAK HARDER ROCK WITH EVERY HIT",
        UpgradeKind.Speed => "SEND EVERY BALL THROUGH ROCK FASTER",
        UpgradeKind.Capacity => "MAKE ROOM FOR MORE ACTIVE BALLS",
        UpgradeKind.AutoFire => "FIRE BASIC BALLS AUTOMATICALLY",
        UpgradeKind.Rod => "POINT TOWARD THE HIDDEN GOLDEN PIXEL",
        UpgradeKind.Smash => "UNLOCK BALLS THAT BREAK NEARBY PIXELS",
        _ => "UNLOCK BALLS THAT TUNNEL STRAIGHT THROUGH"
    };

    private string ShotHint()
    {
        if (!_simulation.IsUnlocked(_selected))
        {
            return "LOCKED";
        }

        return $"{PixelMineSimulation.ShotCost(_selected)} DUST/SHOT";
    }

    private static string Name(BallKind kind) => kind switch
    {
        BallKind.Smash => "SMASH",
        BallKind.Directional => "DRILL",
        _ => "BASIC"
    };

    private static XnaColor BallTint(BallKind kind) => kind switch
    {
        BallKind.Smash => new XnaColor(255, 110, 70),
        BallKind.Directional => new XnaColor(255, 214, 80),
        _ => new XnaColor(255, 144, 0)
    };

    private static HudIcon UpgradeIcon(UpgradeKind kind) => kind switch
    {
        UpgradeKind.Power => HudIcon.Power,
        UpgradeKind.Speed => HudIcon.Speed,
        UpgradeKind.Capacity => HudIcon.Balls,
        UpgradeKind.AutoFire => HudIcon.Auto,
        UpgradeKind.Rod => HudIcon.Rod,
        UpgradeKind.Smash => HudIcon.Smash,
        _ => HudIcon.Drill
    };

    private static XnaColor UpgradeTint(UpgradeKind kind) => kind switch
    {
        UpgradeKind.Power => new XnaColor(255, 176, 72),
        UpgradeKind.Speed => new XnaColor(255, 226, 104),
        UpgradeKind.Capacity => new XnaColor(255, 144, 0),
        UpgradeKind.AutoFire => new XnaColor(176, 222, 126),
        UpgradeKind.Rod => s_goldColor,
        UpgradeKind.Smash => new XnaColor(255, 110, 70),
        _ => new XnaColor(210, 180, 140)
    };

    private void HandleZoom(MouseState mouse)
    {
        var wheel = mouse.ScrollWheelValue;
        if (wheel == _previousMouse.ScrollWheelValue)
        {
            return;
        }

        var zoom = wheel > _previousMouse.ScrollWheelValue ? _zoom * 2f : _zoom / 2f;
        zoom = Math.Clamp(zoom, MinimumZoom, MaximumZoom);
        if (zoom == _zoom)
        {
            return;
        }

        var centerX = _cameraLeft + (ViewWidth / 2);
        var centerY = _cameraTop + (ViewHeight / 2);
        _zoom = zoom;
        _cameraLeft = Math.Clamp(centerX - (ViewWidth / 2), 0, PixelMineWorld.Width - ViewWidth);
        _cameraTop = Math.Clamp(centerY - (ViewHeight / 2), 0, PixelMineWorld.Height - ViewHeight);
    }

    private void HandlePan(KeyboardState keyboard)
    {
        var step = keyboard.IsKeyDown(Keys.LeftShift) || keyboard.IsKeyDown(Keys.RightShift) ? 6 : 2;
        if (keyboard.IsKeyDown(Keys.A) || keyboard.IsKeyDown(Keys.Left))
        {
            _cameraLeft -= step;
        }

        if (keyboard.IsKeyDown(Keys.D) || keyboard.IsKeyDown(Keys.Right))
        {
            _cameraLeft += step;
        }

        if (keyboard.IsKeyDown(Keys.W) || keyboard.IsKeyDown(Keys.Up))
        {
            _cameraTop -= step;
        }

        if (keyboard.IsKeyDown(Keys.S) || keyboard.IsKeyDown(Keys.Down))
        {
            _cameraTop += step;
        }

        _cameraLeft = Math.Clamp(_cameraLeft, 0, PixelMineWorld.Width - ViewWidth);
        _cameraTop = Math.Clamp(_cameraTop, 0, PixelMineWorld.Height - ViewHeight);
    }

    private bool Pressed(KeyboardState keyboard, Keys key) =>
        keyboard.IsKeyDown(key) && !_previousKeyboard.IsKeyDown(key);

    private Vector2 CanvasPosition(MouseState mouse)
    {
        var presentation = GraphicsDevice.PresentationParameters;
        return new Vector2(
            mouse.X * (ScreenWidth / (float)presentation.BackBufferWidth),
            mouse.Y * (ScreenHeight / (float)presentation.BackBufferHeight));
    }

    private void ToggleFullscreen()
    {
        _graphics.IsFullScreen = !_graphics.IsFullScreen;
        if (_graphics.IsFullScreen)
        {
            var display = GraphicsDevice.Adapter.CurrentDisplayMode;
            _graphics.PreferredBackBufferWidth = display.Width;
            _graphics.PreferredBackBufferHeight = display.Height;
        }
        else
        {
            _graphics.PreferredBackBufferWidth = ScreenWidth;
            _graphics.PreferredBackBufferHeight = ScreenHeight;
        }

        _graphics.ApplyChanges();
    }

    private void Verify()
    {
        var pixels = new XnaColor[ScreenWidth * ScreenHeight];
        _frame.GetData(pixels);
        var presentation = GraphicsDevice.PresentationParameters;
        var display = GraphicsDevice.Adapter.CurrentDisplayMode;
        var windowBounds = Window.ClientBounds;
        if (_graphics.IsFullScreen
            && (presentation.BackBufferWidth != display.Width
                || presentation.BackBufferHeight != display.Height
                || windowBounds.Width != display.Width
                || windowBounds.Height != display.Height))
        {
            throw new InvalidOperationException(
                $"Fullscreen bounds {windowBounds.Width}x{windowBounds.Height}, backbuffer "
                + $"{presentation.BackBufferWidth}x{presentation.BackBufferHeight}, expected {display.Width}x{display.Height}.");
        }

        int Variety(int left, int top, int width, int height)
        {
            var colors = new HashSet<uint>();
            for (var y = top; y < top + height; y++)
            {
                for (var x = left; x < left + width; x++)
                {
                    colors.Add(pixels[(y * ScreenWidth) + x].PackedValue);
                }
            }

            return colors.Count;
        }

        var worldColors = Variety(ScreenWidth / 4, ScreenHeight / 4, ScreenWidth / 2, ScreenHeight / 2);
        var hudColors = Variety(0, 0, ScreenWidth, 140);

        var materials = new bool[8];
        for (var y = _cameraTop; y < _cameraTop + ViewHeight; y++)
        {
            for (var x = _cameraLeft; x < _cameraLeft + ViewWidth; x++)
            {
                materials[(int)_simulation.World.MaterialAt(x, y)] = true;
            }
        }

        var materialKinds = materials.Count(present => present);

        var ballPixels = 0;
        var prismGlowPixels = 0;
        for (var i = 0; i < pixels.Length; i++)
        {
            if (pixels[i].PackedValue == s_ballColor.PackedValue)
            {
                ballPixels++;
            }

            var y = i / ScreenWidth;
            var x = i - (y * ScreenWidth);
            if (!IsOverHud(x, y)
                && pixels[i].B > pixels[i].R
                && pixels[i].R > pixels[i].G
                && pixels[i].B > 100)
            {
                prismGlowPixels++;
            }
        }

        if (worldColors < 4 || hudColors < 8 || ballPixels < 20 || materialKinds < 3 || prismGlowPixels < 20)
        {
            throw new InvalidOperationException(
                $"Blank render: world={worldColors}, hud={hudColors}, ballPixels={ballPixels}, "
                + $"materials={materialKinds}, prismGlowPixels={prismGlowPixels}");
        }

        if (_options.Screenshot != null)
        {
            using var file = File.Create(_options.Screenshot);
            _frame.SaveAsPng(file, ScreenWidth, ScreenHeight);
        }

        Console.WriteLine(
            $"RENDER OK frames={_frames} worldColors={worldColors} hudColors={hudColors} "
            + $"ballPixels={ballPixels} materials={materialKinds} prismGlowPixels={prismGlowPixels} "
            + $"mined={_simulation.MinedPixels} output={presentation.BackBufferWidth}x{presentation.BackBufferHeight}");
        Exit();
    }

    private struct HitFlash
    {
        public int X;
        public int Y;
        public int Life;
    }

}
