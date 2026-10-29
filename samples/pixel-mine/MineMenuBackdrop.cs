using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace PixelMine;

internal sealed class MineMenuBackdrop : IDisposable
{
    private static readonly Vector2 s_center = new(960, 400);
    private static readonly Vector2 s_radii = new(304, 272);
    private static readonly Color[] s_rockColors =
    [
        new(184, 20, 12), new(239, 55, 6), new(255, 112, 0), new(220, 184, 12),
        new(28, 180, 28), new(168, 177, 156), new(120, 63, 28)
    ];

    private readonly Texture2D _terrain;
    private readonly Vector2[] _positions =
    [
        new(845, 305), new(920, 510), new(1035, 290), new(1110, 430), new(985, 380)
    ];
    private readonly Vector2[] _velocities =
    [
        new(90, 71), new(-74, -95), new(-89, 77), new(78, -83), new(61, 101)
    ];
    private readonly Vector2[] _impacts = new Vector2[5];
    private readonly float[] _impactTimes = new float[5];
    private float _elapsed;

    public float Elapsed => _elapsed;

    public static ReadOnlySpan<Color> RockColors => s_rockColors;

    public MineMenuBackdrop(GraphicsDevice device)
    {
        const int width = 40;
        const int height = 25;
        var pixels = new Color[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var dx = ((x + 0.5f) * 32 - s_center.X) / s_radii.X;
                var dy = ((y + 0.5f) * 32 - s_center.Y) / s_radii.Y;
                var distance = (dx * dx) + (dy * dy);
                var color = new Color(4, 18, 8);
                if (x >= 18 && distance >= 0.9f)
                {
                    var hash = unchecked((uint)(x * 374761393) + (uint)(y * 668265263));
                    hash = (hash ^ (hash >> 13)) * 1274122517u;
                    var rock = s_rockColors[hash % s_rockColors.Length];
                    var shade = distance < 1.08f ? 1f : distance < 1.3f ? 0.72f : distance < 1.65f ? 0.42f : 0.22f;
                    color = new Color((int)(rock.R * shade), (int)(rock.G * shade), (int)(rock.B * shade));
                }

                if ((x, y) is (20, 12) or (29, 3) or (38, 17))
                {
                    color = new Color(198, 120, 255);
                }

                pixels[(y * width) + x] = color;
            }
        }

        _terrain = new Texture2D(device, width, height);
        _terrain.SetData(pixels);
    }

    public void Update(TimeSpan elapsed)
    {
        var seconds = Math.Clamp((float)elapsed.TotalSeconds, 0f, 0.05f);
        _elapsed += seconds;
        for (var index = 0; index < _positions.Length; index++)
        {
            _impactTimes[index] = Math.Max(0, _impactTimes[index] - seconds);
            if (AdvanceBall(ref _positions[index], ref _velocities[index], seconds))
            {
                var offset = _positions[index] - s_center;
                var normal = Vector2.Normalize(new Vector2(offset.X / (s_radii.X * s_radii.X), offset.Y / (s_radii.Y * s_radii.Y)));
                _impacts[index] = _positions[index] + (normal * 18f);
                _impactTimes[index] = 0.18f;
            }
        }
    }

    internal static bool AdvanceBall(ref Vector2 position, ref Vector2 velocity, float seconds)
    {
        var next = position + (velocity * seconds);
        var offset = next - s_center;
        var dx = offset.X / s_radii.X;
        var dy = offset.Y / s_radii.Y;
        if ((dx * dx) + (dy * dy) > 0.82f)
        {
            var normal = Vector2.Normalize(new Vector2(offset.X / (s_radii.X * s_radii.X), offset.Y / (s_radii.Y * s_radii.Y)));
            velocity = Vector2.Reflect(velocity, normal);
            return true;
        }

        position = next;
        return false;
    }

    public void Draw(SpriteBatch batch, PixelMineRenderer renderer)
    {
        batch.Draw(_terrain, new Rectangle(0, 0, 1280, 800), Color.White);
        var glow = new Color(198, 120, 255) * (0.55f + (0.12f * MathF.Sin(_elapsed * 2f)));
        batch.Draw(renderer.PrismGlowTexture, new Rectangle(616, 360, 80, 80), glow);
        batch.Draw(renderer.PrismGlowTexture, new Rectangle(904, 72, 80, 80), glow);
        batch.Draw(renderer.PrismGlowTexture, new Rectangle(1192, 520, 80, 80), glow);

        for (var index = 0; index < _positions.Length; index++)
        {
            if (_impactTimes[index] > 0)
            {
                var impact = _impacts[index];
                var cell = new Rectangle((int)impact.X / 32 * 32, (int)impact.Y / 32 * 32, 32, 32);
                renderer.DrawHitFlash(batch, cell, 1f, (int)(140 * _impactTimes[index] / 0.18f));
            }

            var kind = index == 2 ? BallKind.Directional : index == 3 ? BallKind.Smash : BallKind.Basic;
            renderer.DrawBall(batch, _positions[index], 5f, kind);
        }
    }

    public void Dispose() => _terrain.Dispose();
}
