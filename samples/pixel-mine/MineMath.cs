namespace PixelMine;

/// <summary>Integer-only helpers for the simulation's Q16.16 positions and Q8.8 directions.</summary>
internal static class MineMath
{
    public const int PositionOne = 65536;
    public const int DirectionOne = 256;

    /// <summary>Sixteen unit directions in Q8.8, from +X counter-clockwise.</summary>
    public static readonly (int X, int Y)[] Directions =
    [
        (256, 0), (237, 98), (181, 181), (98, 237), (0, 256), (-98, 237), (-181, 181), (-237, 98),
        (-256, 0), (-237, -98), (-181, -181), (-98, -237), (0, -256), (98, -237), (181, -181), (237, -98)
    ];

    public static int IntegerSquareRoot(long value)
    {
        if (value <= 0)
        {
            return 0;
        }

        var current = value;
        var next = (current + 1) / 2;
        while (next < current)
        {
            current = next;
            next = (current + (value / current)) / 2;
        }

        return (int)current;
    }

    public static void Normalize(ref int x, ref int y)
    {
        var length = IntegerSquareRoot(((long)x * x) + ((long)y * y));
        if (length == 0)
        {
            x = DirectionOne;
            y = 0;
            return;
        }

        x = (int)(((long)x * DirectionOne) / length);
        y = (int)(((long)y * DirectionOne) / length);
    }

    /// <summary>Direction in Q8.8 from one pixel to another; falls back to the first direction when they coincide.</summary>
    public static (int X, int Y) DirectionTo(int fromX, int fromY, int toX, int toY)
    {
        var dx = toX - fromX;
        var dy = toY - fromY;
        var length = IntegerSquareRoot(((long)dx * dx) + ((long)dy * dy));
        if (length < 2)
        {
            return Directions[0];
        }

        return ((int)(((long)dx * DirectionOne) / length), (int)(((long)dy * DirectionOne) / length));
    }
}
