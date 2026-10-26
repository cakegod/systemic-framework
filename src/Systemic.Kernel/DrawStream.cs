using System.Numerics;

namespace Systemic.Kernel;

public readonly record struct DrawStreamSnapshot(ulong State, ulong Increment, int Draws);

public sealed class DrawStream
{
    private const ulong StateMultiplier = 6364136223846793005UL;
    private const ulong DefaultIncrement = 1442695040888963407UL;

    private ulong _state;
    private ulong _increment;

    public DrawStream(int seed)
    {
        Seed(seed);
    }

    public int Draws { get; private set; }

    public void Seed(int seed)
    {
        _increment = (DefaultIncrement << 1) | 1UL;
        _state = 0UL;
        NextUInt32();
        _state += unchecked((ulong)(long)seed);
        NextUInt32();
        Draws = 0;
    }

    public DrawStream Clone() => (DrawStream)MemberwiseClone();

    public DrawStreamSnapshot CaptureState() => new(_state, _increment, Draws);

    public void RestoreState(DrawStreamSnapshot state)
    {
        if (state.Draws < 0 || (state.Increment & 1UL) == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(state), "Draw stream state must have a non-negative draw count and odd increment.");
        }

        _state = state.State;
        _increment = state.Increment;
        Draws = state.Draws;
    }

    internal DrawStreamSnapshot Capture() => CaptureState();

    internal void Restore(DrawStreamSnapshot state) => RestoreState(state);

    public float NextFloat()
    {
        Draws++;

        var protoExponentOffset = NextUInt32();
        if (protoExponentOffset == 0)
        {
            return 0f;
        }

        var significand = NextUInt32() | 0x80000001U;
        var exponent = -32 - BitOperations.LeadingZeroCount(protoExponentOffset);

        return MathF.ScaleB((float)significand, exponent);
    }

    private uint NextUInt32()
    {
        var oldState = _state;
        _state = unchecked(oldState * StateMultiplier + (_increment | 1UL));

        var xorshifted = (uint)(((oldState >> 18) ^ oldState) >> 27);
        var rotation = (int)(oldState >> 59);

        return (xorshifted >> rotation) | (xorshifted << ((-rotation) & 31));
    }
}
