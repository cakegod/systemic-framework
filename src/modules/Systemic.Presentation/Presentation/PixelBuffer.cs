namespace Systemic.Presentation;

public readonly record struct Rgba32(byte R, byte G, byte B, byte A = 255);

/// <summary>Immutable source-resolution RGBA pixels suitable for a Godot or other engine adapter.</summary>
public sealed class PixelBuffer
{
    private readonly byte[] _pixels;

    public PixelBuffer(int width, int height, byte[] pixels)
        : this(width, height, pixels, takeOwnership: false)
    {
    }

    internal PixelBuffer(int width, int height, byte[] pixels, bool takeOwnership)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width is < 1 or > 4096 || height is < 1 or > 4096
            || pixels.Length != checked(width * height * 4))
        {
            throw new ArgumentException("Pixel buffer dimensions and RGBA data length do not match.", nameof(pixels));
        }

        Width = width;
        Height = height;
        _pixels = takeOwnership ? pixels : [.. pixels];
    }

    public int Width { get; }
    public int Height { get; }
    public ReadOnlyMemory<byte> Pixels => _pixels;

    public Rgba32 GetPixel(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
        {
            throw new ArgumentOutOfRangeException(nameof(x));
        }

        var index = ((y * Width) + x) * 4;
        return new Rgba32(_pixels[index], _pixels[index + 1], _pixels[index + 2], _pixels[index + 3]);
    }
}
