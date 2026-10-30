namespace Systemic.Kernel;

/// <summary>Portable integer trigonometry for Q32.32 angles expressed in full turns.</summary>
/// <remarks>
/// One turn is a full revolution; a positive quarter-turn is counterclockwise. Angles wrap every
/// turn. Cardinal values are exact. Other values use a fixed 61-step CORDIC approximation with
/// Q62 intermediates and are rounded to Q32.32; results are deterministic, not a promise of
/// correctly rounded mathematical sine or cosine. This algorithm and its constants form part of
/// the replay format and must be versioned if changed.
/// </remarks>
public static class DeterministicTrig
{
    // atan(2^-i) / (2*pi), in Q62 turns, rounded to nearest even. The gain is 1 / K in Q62.
    private const long Gain = 2_800_459_870_029_452_954L;
    private static readonly long[] Angles =
    [
        576460752303423488L, 340304653033718298L, 179807632645220259L,
        91273161881380487L, 45813697873323707L, 22929182573009054L,
        11467389120678282L, 5734044481687724L, 2867065987018958L,
        1433538461969102L, 716769914547871L, 358385042719534L,
        179192532040472L, 89596267355325L, 44798133844548L, 22399066943135L,
        11199533474175L, 5599766737413L, 2799883368747L, 1399941684379L,
        699970842190L, 349985421095L, 174992710548L, 87496355274L, 43748177637L,
        21874088818L, 10937044409L, 5468522205L, 2734261102L, 1367130551L,
        683565276L, 341782638L, 170891319L, 85445659L, 42722830L, 21361415L,
        10680707L, 5340354L, 2670177L, 1335088L, 667544L, 333772L, 166886L,
        83443L, 41722L, 20861L, 10430L, 5215L, 2608L, 1304L, 652L, 326L,
        163L, 81L, 41L, 20L, 10L, 5L, 3L, 1L, 1L
    ];

    /// <summary>Returns (sine, cosine) for the supplied angle, measured in full turns.</summary>
    public static (Fixed64 Sin, Fixed64 Cos) SinCos(Fixed64 turns)
    {
        const long full = Fixed64.Scale;
        const long half = full / 2;
        const long quarter = full / 4;
        var angle = turns.RawValue % full;
        if (angle > half)
        {
            angle -= full;
        }

        if (angle < -half)
        {
            angle += full;
        }

        if (angle == 0)
        {
            return (Fixed64.Zero, Fixed64.One);
        }

        if (angle == quarter)
        {
            return (Fixed64.One, Fixed64.Zero);
        }

        if (angle == -quarter)
        {
            return (-Fixed64.One, Fixed64.Zero);
        }

        if (angle == half || angle == -half)
        {
            return (Fixed64.Zero, -Fixed64.One);
        }

        var invert = false;
        if (angle > quarter) { angle -= half; invert = true; }
        if (angle < -quarter) { angle += half; invert = true; }
        var z = angle << 30; // Q32 turns to Q62 turns
        long x = Gain, y = 0;
        for (var i = 0; i < Angles.Length; i++)
        {
            var oldX = x;
            if (z >= 0)
            {
                x -= y >> i;
                y += oldX >> i;
                z -= Angles[i];
            }
            else
            {
                x += y >> i;
                y -= oldX >> i;
                z += Angles[i];
            }
        }
        if (invert) { x = -x; y = -y; }
        return (Fixed64.FromRaw((long)Fixed64.RoundDivide(y, 1L << 30)),
            Fixed64.FromRaw((long)Fixed64.RoundDivide(x, 1L << 30)));
    }
}
