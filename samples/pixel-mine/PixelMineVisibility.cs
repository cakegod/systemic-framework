namespace PixelMine;

internal static class PixelMineVisibility
{
    private const byte HiddenDepth = 4;

    public static void BuildDepth(ReadOnlySpan<byte> materials, Span<byte> depths, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var length = checked(width * height);
        if (materials.Length < length || depths.Length < length)
        {
            throw new ArgumentException("Material and depth buffers must cover the full image.");
        }

        for (var index = 0; index < length; index++)
        {
            depths[index] = materials[index] == (byte)Material.Air ? (byte)0 : HiddenDepth;
        }

        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var index = row + x;
                var depth = depths[index];
                if (x > 0)
                {
                    depth = (byte)Math.Min(depth, depths[index - 1] + 1);
                }

                if (y > 0)
                {
                    depth = (byte)Math.Min(depth, depths[index - width] + 1);
                }

                depths[index] = depth;
            }
        }

        for (var y = height - 1; y >= 0; y--)
        {
            var row = y * width;
            for (var x = width - 1; x >= 0; x--)
            {
                var index = row + x;
                var depth = depths[index];
                if (x + 1 < width)
                {
                    depth = (byte)Math.Min(depth, depths[index + 1] + 1);
                }

                if (y + 1 < height)
                {
                    depth = (byte)Math.Min(depth, depths[index + width] + 1);
                }

                depths[index] = depth;
            }
        }
    }
}
