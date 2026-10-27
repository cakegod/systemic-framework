using System.Diagnostics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Systemic.Content;
using Systemic.Host;
using Systemic.Rendering;
using XnaColor = Microsoft.Xna.Framework.Color;
using XnaVector2 = Microsoft.Xna.Framework.Vector2;

namespace Boids.MonoGame;

public sealed class BoidGame : Game
{
    private const int TargetFramesPerSecond = 60;
    private const int WarmupFrames = 60;
    private const int MarkerTextureSize = 32;

    private readonly GraphicsDeviceManager _graphics;
    private readonly int _seed;
    private readonly int _benchmarkSeconds;
    private readonly bool _verifyRender;
    private readonly SystemicGame _simulation;
    private readonly BoidFlockSystem _flockSystem;
    private readonly SwarmScene _swarmScene = new();
    private readonly Stopwatch _benchmarkClock = new();
    private SpriteBatch _spriteBatch = null!;
    private Texture2D _circleTexture = null!;
    private Texture2D _ringTexture = null!;
    private BoidRenderer _renderer = null!;
    private Matrix _worldToScreen;
    private Matrix _worldToClip;
    private KeyboardState _previousKeyboard;
    private TimeSpan _titleElapsed;
    private int _displayFrames;
    private int _warmupFrameCount;
    private int _renderVerifyFrames;
    private int _benchmarkFrames;
    private long _benchmarkUpdates;
    private long _benchmarkTicks;
    private long _advanceTicks;
    private long _advanceOnTickTicks;
    private long _drawingTicks;
    private bool _measuring;
    private bool _renderVerified;

    public BoidGame(int seed, int benchmarkSeconds, bool verifyRender)
    {
        _seed = seed;
        _benchmarkSeconds = benchmarkSeconds;
        _verifyRender = verifyRender;
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 960,
            PreferredBackBufferHeight = 640,
            GraphicsProfile = GraphicsProfile.HiDef,
            IsFullScreen = false,
            HardwareModeSwitch = false,
            SynchronizeWithVerticalRetrace = benchmarkSeconds == 0,
        };
        IsFixedTimeStep = benchmarkSeconds == 0;
        TargetElapsedTime = TimeSpan.FromSeconds(1.0 / TargetFramesPerSecond);
        IsMouseVisible = true;
        Window.Title = "Systemic boid flock";

        var content = ContentPackLoader.Load(Path.Combine(AppContext.BaseDirectory, "content"));
        var materialized = content.CreateWorld(seed, valueScale: 1.0);
        _flockSystem = new BoidFlockSystem(
            materialized.World,
            materialized.EntityIds["flock.main"],
            seed,
            content.Manifest.TickRateHz);
        _simulation = new SystemicGameBuilder(content, materialized)
            .AddService(_flockSystem)
            .AddSystem(_flockSystem)
            .Build();

        Content.RootDirectory = "Content";
    }

    protected override void LoadContent()
    {
        _spriteBatch = new SpriteBatch(GraphicsDevice);
        _circleTexture = CreateMarkerTexture(ring: false);
        _ringTexture = CreateMarkerTexture(ring: true);
        _renderer = new BoidRenderer(
            GraphicsDevice,
            _circleTexture,
            _swarmScene,
            _flockSystem.Population,
            _flockSystem.ParticleRadius,
            _flockSystem.StarvedSpeedFactor,
            _flockSystem.Species,
            _flockSystem.SpeciesByBoid);
        UpdateTransforms();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _simulation.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void Update(GameTime gameTime)
    {
        if (GamePad.GetState(PlayerIndex.One).Buttons.Back == ButtonState.Pressed
            || Keyboard.GetState().IsKeyDown(Keys.Escape))
        {
            Exit();
        }

        var keyboard = Keyboard.GetState();
        if (_benchmarkSeconds == 0
            && keyboard.IsKeyDown(Keys.F11)
            && !_previousKeyboard.IsKeyDown(Keys.F11))
        {
            _graphics.ToggleFullScreen();
        }

        _previousKeyboard = keyboard;

        var tickBefore = _simulation.World.LogicalTick;
        var advanceStart = _measuring ? Stopwatch.GetTimestamp() : 0;
        _simulation.Advance(gameTime.ElapsedGameTime);
        if (_measuring)
        {
            var elapsedTicks = Stopwatch.GetTimestamp() - advanceStart;
            var advancedTicks = _simulation.World.LogicalTick - tickBefore;
            _advanceTicks += elapsedTicks;
            _benchmarkUpdates++;
            _benchmarkTicks += advancedTicks;
            if (advancedTicks > 0)
            {
                _advanceOnTickTicks += elapsedTicks;
            }
        }

        _displayFrames++;
        _titleElapsed += gameTime.ElapsedGameTime;
        if (_titleElapsed.TotalSeconds >= 1.0)
        {
            var predator = _flockSystem.PredatorState;
            Window.Title = $"seed {_seed} | {_flockSystem.Population:N0} particles | {_displayFrames} FPS | "
                + $"hungry {_flockSystem.HungryFraction:P0} | predator {(predator.Sated ? "resting" : "hunting")} "
                + $"| threat {predator.FleeRadius:F0}";
            _titleElapsed = TimeSpan.Zero;
            _displayFrames = 0;
        }

        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        if (GraphicsDevice.Viewport.Width > 0 && GraphicsDevice.Viewport.Height > 0)
        {
            UpdateTransforms();
        }

        var drawingStart = _measuring ? Stopwatch.GetTimestamp() : 0;
        GraphicsDevice.Clear(new XnaColor(17, 24, 33));
        _renderer.Draw(
            _flockSystem.Positions,
            _flockSystem.Energies,
            _flockSystem.SpeciesByBoid,
            _simulation.World.LogicalTick,
            _worldToClip);
        DrawMarkers();
        if (_verifyRender && !_renderVerified && ++_renderVerifyFrames >= 5)
        {
            VerifyRenderedBoids();
            _renderVerified = true;
            Exit();
        }

        var drawingEnd = _measuring ? Stopwatch.GetTimestamp() : 0;

        if (_measuring)
        {
            _drawingTicks += drawingEnd - drawingStart;
            _benchmarkFrames++;
            if (_benchmarkClock.Elapsed.TotalSeconds >= _benchmarkSeconds)
            {
                PrintBenchmark();
                Exit();
            }
        }
        else if (_benchmarkSeconds > 0 && ++_warmupFrameCount == WarmupFrames)
        {
            _benchmarkClock.Restart();
            _measuring = true;
        }

        base.Draw(gameTime);
    }

    private void DrawMarkers()
    {
        _spriteBatch.Begin(
            SpriteSortMode.Deferred,
            BlendState.AlphaBlend,
            SamplerState.LinearClamp,
            DepthStencilState.None,
            RasterizerState.CullNone,
            transformMatrix: _worldToScreen);

        foreach (var patch in _flockSystem.FoodPatches)
        {
            var fullness = Math.Clamp(patch.Stock / patch.Capacity, 0.0f, 1.0f);
            var radius = 3.0f + fullness * 8.0f;
            var color = new XnaColor(
                (byte)(220.0f - fullness * 165.0f),
                (byte)(65.0f + fullness * 145.0f),
                (byte)52,
                byte.MaxValue);
            DrawMarker(_circleTexture, patch.Position.X, patch.Position.Y, radius, color);
        }

        var predator = _flockSystem.PredatorState;
        var hunger = 1.0f - Math.Clamp(predator.Energy, 0.0f, 1.0f);
        var threatColor = new XnaColor(242, 74, 96, (int)(90.0f + 90.0f * hunger));
        DrawMarker(_ringTexture, predator.Position.X, predator.Position.Y, predator.FleeRadius, threatColor);
        DrawMarker(_circleTexture, predator.Position.X, predator.Position.Y, 8.0f,
            new XnaColor(247, (int)(62.0f + 130.0f * predator.Energy), 85, 255));
        _spriteBatch.End();
    }

    private void DrawMarker(Texture2D texture, float x, float y, float radius, XnaColor color)
    {
        var origin = new XnaVector2(texture.Width * 0.5f, texture.Height * 0.5f);
        var scale = radius * 2.0f / texture.Width;
        _spriteBatch.Draw(texture, new XnaVector2(x, y), null, color, 0.0f, origin,
            scale, SpriteEffects.None, 0.0f);
    }

    private Texture2D CreateMarkerTexture(bool ring)
    {
        var texture = new Texture2D(GraphicsDevice, MarkerTextureSize, MarkerTextureSize);
        var pixels = new XnaColor[MarkerTextureSize * MarkerTextureSize];
        var center = (MarkerTextureSize - 1) * 0.5f;
        var radius = center - 1.0f;
        for (var y = 0; y < MarkerTextureSize; y++)
        {
            for (var x = 0; x < MarkerTextureSize; x++)
            {
                var distance = MathF.Sqrt(MathF.Pow(x - center, 2) + MathF.Pow(y - center, 2));
                var coverage = ring
                    ? Math.Clamp(1.5f - MathF.Abs(distance - radius), 0.0f, 1.0f)
                    : Math.Clamp(radius + 0.5f - distance, 0.0f, 1.0f);
                pixels[y * MarkerTextureSize + x] = new XnaColor(255, 255, 255, (int)(coverage * 255.0f));
            }
        }

        texture.SetData(pixels);
        return texture;
    }

    private void UpdateTransforms()
    {
        var viewport = GraphicsDevice.Viewport;
        var worldWidth = _flockSystem.WorldWidth;
        var worldHeight = _flockSystem.WorldHeight;
        var viewHeight = MathF.Max(worldHeight, worldWidth * viewport.Height / viewport.Width);
        var viewWidth = viewHeight * viewport.Width / viewport.Height;
        var left = (worldWidth - viewWidth) * 0.5f;
        var right = (worldWidth + viewWidth) * 0.5f;
        var top = (worldHeight - viewHeight) * 0.5f;
        var bottom = (worldHeight + viewHeight) * 0.5f;
        _worldToClip = Matrix.CreateOrthographicOffCenter(left, right, bottom, top, 0.0f, 1.0f);
        var scale = viewport.Width / viewWidth;
        _worldToScreen = Matrix.CreateTranslation(-worldWidth * 0.5f, -worldHeight * 0.5f, 0.0f)
            * Matrix.CreateScale(scale)
            * Matrix.CreateTranslation(viewport.Width * 0.5f, viewport.Height * 0.5f, 0.0f);
    }

    private void PrintBenchmark()
    {
        var seconds = _benchmarkClock.Elapsed.TotalSeconds;
        var advanceMillisecondsPerUpdate = ToMilliseconds(_advanceTicks) / Math.Max(_benchmarkUpdates, 1);
        var advanceMillisecondsPerTick = ToMilliseconds(_advanceOnTickTicks) / Math.Max(_benchmarkTicks, 1);
        var logicalTicksPerUpdate = _benchmarkTicks / (double)Math.Max(_benchmarkUpdates, 1);
        var drawMilliseconds = ToMilliseconds(_drawingTicks) / Math.Max(_benchmarkFrames, 1);
        Console.WriteLine($"frames={_benchmarkFrames} seconds={seconds:F2} fps={_benchmarkFrames / seconds:F1}");
        Console.WriteLine($"advanceCpu={advanceMillisecondsPerUpdate:F3}ms/update "
            + $"({logicalTicksPerUpdate:F3} logical ticks/update)");
        Console.WriteLine($"advanceOnTickCpu={advanceMillisecondsPerTick:F3}ms/tick logicalTicks={_benchmarkTicks}");
        Console.WriteLine($"drawCpu={drawMilliseconds:F3}ms/frame");
    }

    private void VerifyRenderedBoids()
    {
        var viewport = GraphicsDevice.Viewport;
        var pixels = new XnaColor[viewport.Width * viewport.Height];
        GraphicsDevice.GetBackBufferData(pixels);

        var speciesColors = new HashSet<uint>();
        foreach (var species in _flockSystem.Species)
        {
            speciesColors.Add((uint)(byte)species.Red
                | (uint)(byte)species.Green << 8
                | (uint)(byte)species.Blue << 16);
        }

        var visibleBoidPixels = 0;
        var nonBackgroundPixels = 0;
        var left = viewport.Width;
        var top = viewport.Height;
        var right = -1;
        var bottom = -1;
        for (var index = 0; index < pixels.Length; index++)
        {
            var pixel = pixels[index];
            if (pixel.R != 17 || pixel.G != 24 || pixel.B != 33)
            {
                nonBackgroundPixels++;
            }

            var packed = (uint)pixel.R | (uint)pixel.G << 8 | (uint)pixel.B << 16;
            if (speciesColors.Contains(packed))
            {
                visibleBoidPixels++;
                var x = index % viewport.Width;
                var y = index / viewport.Width;
                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        }

        var visibleWidth = right - left + 1;
        var visibleHeight = bottom - top + 1;
        var minimumVisiblePixels = Math.Max(10_000, _flockSystem.Population / 2);
        var coversMostOfTheViewport = visibleWidth >= viewport.Width * 0.7f
            && visibleHeight >= viewport.Height * 0.7f;
        Console.WriteLine($"[RENDER-CHECK] viewport={viewport.Width}x{viewport.Height} "
            + $"species-color-pixels={visibleBoidPixels} bounds={visibleWidth}x{visibleHeight} "
            + $"non-background-pixels={nonBackgroundPixels}");
        if (visibleBoidPixels < minimumVisiblePixels || !coversMostOfTheViewport)
        {
            Environment.ExitCode = 1;
        }
    }

    private static double ToMilliseconds(long stopwatchTicks) =>
        stopwatchTicks * 1000.0 / Stopwatch.Frequency;

    protected override void UnloadContent()
    {
        _flockSystem.Dispose();
        _renderer.Dispose();
        _circleTexture.Dispose();
        _ringTexture.Dispose();
        _spriteBatch.Dispose();
        base.UnloadContent();
    }
}
