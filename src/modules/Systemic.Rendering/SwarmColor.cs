namespace Systemic.Rendering;

public static class SwarmColor
{
    public static uint Pack(byte red, byte green, byte blue, byte alpha = 255) =>
        red | (uint)green << 8 | (uint)blue << 16 | (uint)alpha << 24;

    public static (byte Red, byte Green, byte Blue, byte Alpha) Unpack(uint rgba) =>
        ((byte)rgba, (byte)(rgba >> 8), (byte)(rgba >> 16), (byte)(rgba >> 24));
}
