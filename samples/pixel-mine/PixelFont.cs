using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace PixelMine;

/// <summary>5x7 bitmap font built into one texture strip; no content pipeline needed.</summary>
public sealed class PixelFont : IDisposable
{
    private const string Charset = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ :./-+%x<>!?[]()";
    private const int GlyphWidth = 5;
    private const int GlyphHeight = 7;
    private const int GlyphStride = 6;

    private static readonly int[][] s_rows =
    [
        [0b01110, 0b10001, 0b10011, 0b10101, 0b11001, 0b10001, 0b01110],
        [0b00100, 0b01100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110],
        [0b01110, 0b10001, 0b00001, 0b00010, 0b00100, 0b01000, 0b11111],
        [0b11111, 0b00010, 0b00100, 0b00010, 0b00001, 0b10001, 0b01110],
        [0b00010, 0b00110, 0b01010, 0b10010, 0b11111, 0b00010, 0b00010],
        [0b11111, 0b10000, 0b11110, 0b00001, 0b00001, 0b10001, 0b01110],
        [0b00110, 0b01000, 0b10000, 0b11110, 0b10001, 0b10001, 0b01110],
        [0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b01000, 0b01000],
        [0b01110, 0b10001, 0b10001, 0b01110, 0b10001, 0b10001, 0b01110],
        [0b01110, 0b10001, 0b10001, 0b01111, 0b00001, 0b00010, 0b01100],
        [0b01110, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001],
        [0b11110, 0b10001, 0b10001, 0b11110, 0b10001, 0b10001, 0b11110],
        [0b01110, 0b10001, 0b10000, 0b10000, 0b10000, 0b10001, 0b01110],
        [0b11110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b11110],
        [0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b11111],
        [0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b10000],
        [0b01110, 0b10001, 0b10000, 0b10111, 0b10001, 0b10001, 0b01111],
        [0b10001, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001],
        [0b11111, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b11111],
        [0b00111, 0b00010, 0b00010, 0b00010, 0b00010, 0b10010, 0b01100],
        [0b10001, 0b10010, 0b10100, 0b11000, 0b10100, 0b10010, 0b10001],
        [0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b11111],
        [0b10001, 0b11011, 0b10101, 0b10101, 0b10001, 0b10001, 0b10001],
        [0b10001, 0b11001, 0b10101, 0b10011, 0b10001, 0b10001, 0b10001],
        [0b01110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110],
        [0b11110, 0b10001, 0b10001, 0b11110, 0b10000, 0b10000, 0b10000],
        [0b01110, 0b10001, 0b10001, 0b10001, 0b10101, 0b10010, 0b01101],
        [0b11110, 0b10001, 0b10001, 0b11110, 0b10100, 0b10010, 0b10001],
        [0b01111, 0b10000, 0b10000, 0b01110, 0b00001, 0b00001, 0b11110],
        [0b11111, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100],
        [0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110],
        [0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01010, 0b00100],
        [0b10001, 0b10001, 0b10001, 0b10101, 0b10101, 0b11011, 0b10001],
        [0b10001, 0b10001, 0b01010, 0b00100, 0b01010, 0b10001, 0b10001],
        [0b10001, 0b10001, 0b01010, 0b00100, 0b00100, 0b00100, 0b00100],
        [0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b10000, 0b11111],
        [0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b00000],
        [0b00000, 0b00100, 0b00100, 0b00000, 0b00100, 0b00100, 0b00000],
        [0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b00100, 0b00100],
        [0b00001, 0b00010, 0b00010, 0b00100, 0b01000, 0b01000, 0b10000],
        [0b00000, 0b00000, 0b00000, 0b11111, 0b00000, 0b00000, 0b00000],
        [0b00000, 0b00100, 0b00100, 0b11111, 0b00100, 0b00100, 0b00000],
        [0b11001, 0b11010, 0b00100, 0b00100, 0b01000, 0b01011, 0b10011],
        [0b00000, 0b00000, 0b10001, 0b01010, 0b00100, 0b01010, 0b10001],
        [0b00010, 0b00100, 0b01000, 0b10000, 0b01000, 0b00100, 0b00010],
        [0b01000, 0b00100, 0b00010, 0b00001, 0b00010, 0b00100, 0b01000],
        [0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00000, 0b00100],
        [0b01110, 0b10001, 0b00001, 0b00010, 0b00100, 0b00000, 0b00100],
        [0b01110, 0b01000, 0b01000, 0b01000, 0b01000, 0b01000, 0b01110],
        [0b01110, 0b00010, 0b00010, 0b00010, 0b00010, 0b00010, 0b01110],
        [0b00010, 0b00100, 0b01000, 0b01000, 0b01000, 0b00100, 0b00010],
        [0b01000, 0b00100, 0b00010, 0b00010, 0b00010, 0b00100, 0b01000]
    ];

    private readonly Texture2D _atlas;
    private readonly int[] _indexByCharacter = new int[128];

    public PixelFont(GraphicsDevice device)
    {
        Array.Fill(_indexByCharacter, -1);
        for (var i = 0; i < Charset.Length; i++)
        {
            _indexByCharacter[Charset[i]] = i;
        }

        var width = Charset.Length * GlyphStride;
        var pixels = new Color[width * GlyphHeight];
        for (var glyph = 0; glyph < s_rows.Length; glyph++)
        {
            var rows = s_rows[glyph];
            for (var y = 0; y < GlyphHeight; y++)
            {
                for (var x = 0; x < GlyphWidth; x++)
                {
                    if ((rows[y] & (1 << (GlyphWidth - 1 - x))) != 0)
                    {
                        pixels[(y * width) + (glyph * GlyphStride) + x] = Color.White;
                    }
                }
            }
        }

        _atlas = new Texture2D(device, width, GlyphHeight);
        _atlas.SetData(pixels);
    }

    public int Height(float scale) => (int)(GlyphHeight * scale);

    public int Measure(string text, float scale) => (int)(text.Length * GlyphStride * scale);

    public void Draw(SpriteBatch batch, string text, int x, int y, float scale, Color color)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var code = text[i] < 128 ? text[i] : '?';
            var glyph = _indexByCharacter[code];
            if (glyph < 0)
            {
                glyph = _indexByCharacter['?'];
            }

            batch.Draw(
                _atlas,
                new Vector2(x + (i * GlyphStride * scale), y),
                new Rectangle(glyph * GlyphStride, 0, GlyphWidth, GlyphHeight),
                color,
                0f,
                Vector2.Zero,
                scale,
                SpriteEffects.None,
                0f);
        }
    }

    public void Dispose() => _atlas.Dispose();
}
