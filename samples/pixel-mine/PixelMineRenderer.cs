using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using XnaColor = Microsoft.Xna.Framework.Color;

namespace PixelMine;

/// <summary>Typed-pixel world view: one texture pixel per mine cell, uploaded for the visible window only.</summary>
public sealed class PixelMineRenderer : IDisposable
{
    private const int BallTextureSize = 16;
    private const int PrismGlowTextureSize = 32;
    private const int VisibilityPadding = 3;
    private readonly GraphicsDevice _device;
    private readonly PixelMineWorld _world;
    private readonly Texture2D _pixelTexture;
    private readonly Texture2D _ballTexture;
    private readonly Texture2D _prismGlowTexture;
    private Texture2D _worldTexture;
    private XnaColor[] _pixels = [];
    private byte[] _materials = [];
    private byte[] _depths = [];

    public PixelMineRenderer(GraphicsDevice device, PixelMineWorld world)
    {
        _device = device;
        _world = world;
        _worldTexture = new Texture2D(device, 1, 1);
        _pixelTexture = new Texture2D(device, 1, 1);
        _ballTexture = CreateBallTexture(device);
        _prismGlowTexture = CreatePrismGlowTexture(device);
        _pixelTexture.SetData([XnaColor.White]);
        ViewWidth = 1;
        ViewHeight = 1;
    }

    public int ViewLeft { get; private set; }

    public int ViewTop { get; private set; }

    public int ViewWidth { get; private set; }

    public int ViewHeight { get; private set; }

    public Texture2D Texture => _worldTexture;

    public Texture2D White => _pixelTexture;

    internal Texture2D PrismGlowTexture => _prismGlowTexture;

    public void Resize(int width, int height)
    {
        width = Math.Clamp(width, 1, 1024);
        height = Math.Clamp(height, 1, 1024);
        if (width == _worldTexture.Width && height == _worldTexture.Height)
        {
            return;
        }

        _worldTexture.Dispose();
        _worldTexture = new Texture2D(_device, width, height);
        _pixels = new XnaColor[width * height];
        var paddedLength = (width + (VisibilityPadding * 2)) * (height + (VisibilityPadding * 2));
        _materials = new byte[paddedLength];
        _depths = new byte[paddedLength];
    }

    /// <summary>Refresh the visible window; the texture is sized to it and offset by ViewLeft/ViewTop.</summary>
    public void Update(int left, int top, int width, int height)
    {
        Resize(width, height);
        ViewLeft = left;
        ViewTop = top;
        ViewWidth = width;
        ViewHeight = height;

        var paddedWidth = width + (VisibilityPadding * 2);
        var paddedHeight = height + (VisibilityPadding * 2);
        var paddedLeft = left - VisibilityPadding;
        var paddedTop = top - VisibilityPadding;
        _world.EnsureRegion(paddedLeft, paddedTop, paddedWidth, paddedHeight);

        var materials = _materials;
        for (var y = 0; y < paddedHeight; y++)
        {
            var row = y * paddedWidth;
            for (var x = 0; x < paddedWidth; x++)
            {
                materials[row + x] = (byte)_world.MaterialAt(paddedLeft + x, paddedTop + y);
            }
        }

        PixelMineVisibility.BuildDepth(materials, _depths, paddedWidth, paddedHeight);
        var data = _pixels;
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var index = ((y + VisibilityPadding) * paddedWidth) + x + VisibilityPadding;
                data[row + x] = ToColor((Material)materials[index], _depths[index], left + x, top + y);
            }
        }

        _worldTexture.SetData(data);
    }

    public void DrawWorld(SpriteBatch batch, Rectangle destination) =>
        batch.Draw(_worldTexture, destination, XnaColor.White);

    public void DrawPrismGlows(SpriteBatch batch, int cellSize)
    {
        var paddedWidth = ViewWidth + (VisibilityPadding * 2);
        var origin = new Vector2(PrismGlowTextureSize / 2f, PrismGlowTextureSize / 2f);
        var scale = (cellSize * 2.5f) / PrismGlowTextureSize;
        for (var y = 0; y < ViewHeight; y++)
        {
            for (var x = 0; x < ViewWidth; x++)
            {
                var index = ((y + VisibilityPadding) * paddedWidth) + x + VisibilityPadding;
                if ((Material)_materials[index] != Material.Prism)
                {
                    continue;
                }

                var center = new Vector2((x + 0.5f) * cellSize, (y + 0.5f) * cellSize);
                batch.Draw(_prismGlowTexture, center, null, new XnaColor(198, 120, 255), 0f, origin, scale,
                    SpriteEffects.None, 0f);
            }
        }
    }

    public void DrawHitFlash(SpriteBatch batch, Rectangle cell, float scale, int alpha)
    {
        var expandX = (int)MathF.Ceiling(cell.Width * (scale - 1f) * 0.5f);
        var expandY = (int)MathF.Ceiling(cell.Height * (scale - 1f) * 0.5f);
        var expanded = new Rectangle(cell.X - expandX, cell.Y - expandY,
            cell.Width + (expandX * 2), cell.Height + (expandY * 2));
        batch.Draw(_pixelTexture, expanded, new XnaColor(255, 248, 200, alpha));
    }

    public bool IsVisibleCell(int worldX, int worldY)
    {
        var x = worldX - ViewLeft;
        var y = worldY - ViewTop;
        if ((uint)x >= ViewWidth || (uint)y >= ViewHeight)
        {
            return false;
        }

        var paddedWidth = ViewWidth + (VisibilityPadding * 2);
        var index = ((y + VisibilityPadding) * paddedWidth) + x + VisibilityPadding;
        return _depths[index] <= 3;
    }

    public void DrawBall(SpriteBatch batch, Vector2 screen, float radius, BallKind kind)
    {
        var color = kind switch
        {
            BallKind.Smash => new XnaColor(255, 116, 84),
            BallKind.Directional => new XnaColor(126, 226, 255),
            _ => new XnaColor(255, 144, 0)
        };
        var origin = new Vector2(BallTextureSize / 2f, BallTextureSize / 2f);
        batch.Draw(_ballTexture, screen, null, new XnaColor(10, 8, 14), 0f, origin, (radius + 1.5f) * 2f / BallTextureSize, SpriteEffects.None, 0f);
        batch.Draw(_ballTexture, screen, null, color, 0f, origin, radius * 2f / BallTextureSize, SpriteEffects.None, 0f);
    }

    public void DrawObjectMarker(SpriteBatch batch, Vector2 screen, float size, XnaColor color) =>
        batch.Draw(_pixelTexture, screen, null, color, 0f, new Vector2(0.5f, 0.5f), size, SpriteEffects.None, 0f);

    public void DrawLine(SpriteBatch batch, Vector2 from, Vector2 to, float thickness, XnaColor color)
    {
        var delta = to - from;
        var length = delta.Length();
        if (length < 0.01f)
        {
            return;
        }

        batch.Draw(
            _pixelTexture,
            from,
            null,
            color,
            MathF.Atan2(delta.Y, delta.X),
            new Vector2(0f, 0.5f),
            new Vector2(length, thickness),
            SpriteEffects.None,
            0f);
    }

    public void Dispose()
    {
        _worldTexture.Dispose();
        _pixelTexture.Dispose();
        _ballTexture.Dispose();
        _prismGlowTexture.Dispose();
    }

    internal static bool IsBallPixel(int x, int y)
    {
        var dx = (x * 2) + 1 - BallTextureSize;
        var dy = (y * 2) + 1 - BallTextureSize;
        return (dx * dx) + (dy * dy) <= BallTextureSize * BallTextureSize;
    }

    private static Texture2D CreateBallTexture(GraphicsDevice device)
    {
        var pixels = new XnaColor[BallTextureSize * BallTextureSize];
        for (var y = 0; y < BallTextureSize; y++)
        {
            for (var x = 0; x < BallTextureSize; x++)
            {
                if (IsBallPixel(x, y))
                {
                    pixels[(y * BallTextureSize) + x] = XnaColor.White;
                }
            }
        }

        var texture = new Texture2D(device, BallTextureSize, BallTextureSize);
        texture.SetData(pixels);
        return texture;
    }

    private static Texture2D CreatePrismGlowTexture(GraphicsDevice device)
    {
        var pixels = new XnaColor[PrismGlowTextureSize * PrismGlowTextureSize];
        for (var y = 0; y < PrismGlowTextureSize; y++)
        {
            for (var x = 0; x < PrismGlowTextureSize; x++)
            {
                var dx = x + 0.5f - (PrismGlowTextureSize / 2f);
                var dy = y + 0.5f - (PrismGlowTextureSize / 2f);
                var distance = MathF.Sqrt((dx * dx) + (dy * dy)) / (PrismGlowTextureSize / 2f);
                var alpha = (byte)(Math.Clamp(1f - distance, 0f, 1f) * 100f);
                pixels[(y * PrismGlowTextureSize) + x] = new XnaColor(alpha, alpha, alpha, alpha);
            }
        }

        var texture = new Texture2D(device, PrismGlowTextureSize, PrismGlowTextureSize);
        texture.SetData(pixels);
        return texture;
    }

    private static XnaColor ToColor(Material material, byte depth, int x, int y)
    {
        if (material == Material.Air || depth is 0 or > 3)
        {
            return new XnaColor(4, 18, 8);
        }

        var hash = unchecked(((uint)x * 374761393u) + ((uint)y * 668265263u));
        hash ^= hash >> 13;
        hash *= 1274122517u;
        hash ^= hash >> 16;
        var variant = hash & 3;
        var color = material switch
        {
            Material.Dirt => variant < 2 ? new XnaColor(0, 210, 20) : new XnaColor(20, 178, 26),
            Material.DirtDark => variant < 2 ? new XnaColor(172, 0, 12) : new XnaColor(206, 12, 12),
            Material.Clay => variant < 2 ? new XnaColor(240, 54, 0) : new XnaColor(255, 104, 0),
            Material.Stone => variant < 2 ? new XnaColor(0, 204, 12) : new XnaColor(228, 190, 0),
            Material.Deep => variant < 2 ? new XnaColor(198, 198, 198) : new XnaColor(222, 222, 222),
            Material.Prism => new XnaColor(198, 120, 255),
            Material.Gold => new XnaColor(255, 224, 48),
            _ => new XnaColor(4, 18, 8)
        };
        var shade = depth switch { 1 => 255, 2 => 156, _ => 78 };
        return new XnaColor((color.R * shade) / 255, (color.G * shade) / 255, (color.B * shade) / 255);
    }
}
