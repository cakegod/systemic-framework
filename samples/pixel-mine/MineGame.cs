using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using XnaColor = Microsoft.Xna.Framework.Color;

namespace PixelMine;

public sealed class MineGame : Game
{
    private const int ScreenWidth = 1280;
    private const int ScreenHeight = 800;
    private const int HudTop = 640;
    private const int HitFlashCapacity = 24;
    private const int HitFlashDuration = 6;
    private const int DustPopDuration = 12;
    private const int DustPopCooldown = 24;
    private const float MinimumZoom = 2f / 3f;
    private const float MaximumZoom = 16f;

    private static readonly XnaColor s_ballColor = new(255, 144, 0);

    private readonly GraphicsDeviceManager _graphics;
    private readonly MineOptions _options;
    private readonly PixelMineHost _host;
    private readonly PixelMineSimulation _simulation;
    private readonly MineMenu _menu;
    private readonly HitFlash[] _hitFlashes = new HitFlash[HitFlashCapacity];
    private SpriteBatch _batch = null!;
    private PixelFont _font = null!;
    private PixelMineRenderer _renderer = null!;
    private MineMenuBackdrop _menuBackdrop = null!;
    private RenderTarget2D _frame = null!;
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
    private long _simulationRemainder;
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

    private int ViewHeight => HudTop / PixelScale;

    protected override void LoadContent()
    {
        _batch = new SpriteBatch(GraphicsDevice);
        _font = new PixelFont(GraphicsDevice);
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
        }

        for (var index = 0; index < _simulation.Upgrades.Count; index++)
        {
            if (Pressed(keyboard, Keys.D1 + index))
            {
                _simulation.RequestUpgrade((UpgradeKind)index);
            }
        }

        HandleZoom(mouse);
        var cursorPosition = CanvasPosition(mouse);

        if (mouse.LeftButton == ButtonState.Pressed
            && _previousMouse.LeftButton == ButtonState.Released
            && cursorPosition.Y < HudTop)
        {
            var worldX = _cameraLeft + (int)(cursorPosition.X / PixelScale);
            var worldY = _cameraTop + (int)(cursorPosition.Y / PixelScale);
            _simulation.RequestShot(worldX, worldY, _selected);
        }

        var ticks = SimulationTicks(ref _simulationRemainder, gameTime.ElapsedGameTime);
        for (var tick = 0; tick < ticks; tick++)
        {
            HandlePan(keyboard);
            UpdateHitFlashTimers();
            if (!_paused)
            {
                _host.Step(1);
                UpdateMiningFeedback();
            }
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
            _renderer.DrawWorld(_batch, new Rectangle(0, 0, ScreenWidth, HudTop));
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

    internal static int SimulationTicks(ref long remainder, TimeSpan elapsed)
    {
        remainder += elapsed.Ticks * 60;
        var ticks = (int)(remainder / TimeSpan.TicksPerSecond);
        remainder %= TimeSpan.TicksPerSecond;
        return ticks;
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
            _batch?.Dispose();
            _frame?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void DrawBalls()
    {
        var kinds = _simulation.BallKinds;
        var positionsX = _simulation.BallX;
        var positionsY = _simulation.BallY;
        for (var row = 0; row < kinds.Length; row++)
        {
            if (kinds[row] < 0)
            {
                continue;
            }

            var screenX = ((positionsX[row] / 65536f) - _cameraLeft) * PixelScale;
            var screenY = ((positionsY[row] / 65536f) - _cameraTop) * PixelScale;
            if (screenX < -32 || screenY < -32 || screenX > ScreenWidth + 32 || screenY > HudTop + 32)
            {
                continue;
            }

            var kind = (BallKind)kinds[row];
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
        _batch.Draw(_renderer.White, new Rectangle(0, HudTop, ScreenWidth, ScreenHeight - HudTop), XnaColor.Black);
        _batch.Draw(_renderer.White, new Rectangle(0, HudTop, ScreenWidth, 2), new XnaColor(70, 60, 80));

        var hud = _batch;
        var dustScale = 2.5f;
        if (_dustPopTicks > 0)
        {
            var progress = 1f - (_dustPopTicks / (float)DustPopDuration);
            dustScale += MathF.Sin(progress * MathF.PI) * 0.45f;
            var alpha = (byte)(255 * (1f - progress));
            _font.Draw(hud, $"+{_dustPopAmount}", 164, HudTop + 8 - (int)(progress * 10f), 1.5f,
                new XnaColor(255, 230, 120, (int)alpha));
        }

        _font.Draw(hud, $"DUST {_simulation.Dust}", 16, HudTop + 12, dustScale, new XnaColor(255, 214, 140));
        _font.Draw(hud, $"PRISMS {_simulation.Prisms}", 320, HudTop + 12, 2.5f, new XnaColor(198, 140, 255));
        _font.Draw(hud, $"BALLS {_simulation.BallCount}/{_simulation.BallLimit}", 620, HudTop + 12, 2.5f, s_ballColor);
        _font.Draw(hud, $"MINED {_simulation.MinedPixels}", 940, HudTop + 12, 2.5f, new XnaColor(140, 200, 255));

        _font.Draw(hud, $"SELECTED {Name(_selected)} (TAB)  {ShotHint()}", 16, HudTop + 44, 2f, new XnaColor(190, 190, 200));

        var panels = _simulation.Upgrades;
        for (var index = 0; index < panels.Count; index++)
        {
            var upgrade = panels[index];
            var x = 16 + (index * 178);
            var y = HudTop + 82;
            var color = upgrade.Level >= upgrade.MaxLevel
                ? new XnaColor(120, 220, 140)
                : upgrade.UsesPrisms && _simulation.Prisms < upgrade.Cost
                    ? new XnaColor(150, 110, 170)
                    : upgrade.UsesPrisms ? new XnaColor(198, 140, 255) : new XnaColor(230, 230, 230);
            var price = upgrade.Level >= upgrade.MaxLevel
                ? "MAX"
                : upgrade.UsesPrisms ? $"{upgrade.Cost} P" : $"{upgrade.Cost}";
            _font.Draw(hud, $"{index + 1} {upgrade.Name}", x, y, 1.7f, color);
            _font.Draw(hud, $"LV{upgrade.Level} {price}", x, y + 18, 1.5f, color);
        }

        _font.Draw(hud, "WASD PAN  WHEEL ZOOM  CLICK SHOOT  1-7 BUY  SPACE PAUSE", 16, ScreenHeight - 28, 1.5f,
            new XnaColor(120, 120, 135));

        if (_simulation.HasRod)
        {
            var (directionX, directionY) = _simulation.GoldDirection;
            var from = new Vector2(ScreenWidth - 150, HudTop - 60);
            var to = from + new Vector2(directionX / 256f, directionY / 256f) * 44f;
            _renderer.DrawLine(_batch, from, to, 5f, new XnaColor(255, 216, 64));
            _font.Draw(hud, $"GOLD {_simulation.GoldDistance}", (int)from.X + 30, (int)from.Y - 8, 1.8f,
                new XnaColor(255, 216, 64));
        }

        if (_simulation.Message.Length > 0)
        {
            _font.Draw(hud, _simulation.Message, 16, HudTop - 34, 2.2f, new XnaColor(255, 236, 140));
        }

        if (_simulation.Won)
        {
            _font.Draw(hud, "GOLDEN PIXEL FOUND - PRESS ESC", 16, HudTop - 70, 2.6f, new XnaColor(255, 216, 64));
        }

        if (_paused)
        {
            _font.Draw(hud, "PAUSED", (ScreenWidth / 2) - 60, (HudTop / 2) - 10, 3f, s_ballColor);
        }
    }

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

        var worldColors = Variety(0, 0, ScreenWidth, HudTop);
        var hudColors = Variety(0, HudTop, ScreenWidth, ScreenHeight - HudTop);

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

            if ((i / ScreenWidth) < HudTop
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
