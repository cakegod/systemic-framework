using System.Globalization;

namespace Systemic.Kernel;

/// <summary>
/// A signed Q32.32 number backed by a <see cref="long"/>. Its range is
/// [-2147483648, 2147483648 - 2^-32], in increments of 2^-32.
/// </summary>
/// <remarks>
/// Conversions and arithmetic round to nearest, ties to even. Overflow throws even in an unchecked
/// caller. Multiplication and division use integer intermediates; no binary floating-point math is
/// involved. Explicit decimal conversion on output is rounded to decimal's available precision.
/// Persist <see cref="RawValue"/> to retain the exact Q32.32 bit pattern.
/// </remarks>
public readonly struct Fixed64 : IEquatable<Fixed64>, IComparable<Fixed64>
{
    internal const long Scale = 1L << 32;
    private readonly long _raw;

    private Fixed64(long raw) => _raw = raw;

    /// <summary>The underlying signed Q32.32 value.</summary>
    public long RawValue => _raw;
    public static Fixed64 Zero => new(0);
    public static Fixed64 One => new(Scale);

    /// <summary>Creates a value from its exact signed Q32.32 representation.</summary>
    public static Fixed64 FromRaw(long raw) => new(raw);

    public static explicit operator Fixed64(int value) => new((long)value * Scale);
    public static explicit operator Fixed64(long value) => new(checked((long)((Int128)value * Scale)));

    /// <summary>Converts an exact decimal input to the nearest Q32.32 value, ties to even.</summary>
    public static explicit operator Fixed64(decimal value)
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);
        UInt128 coefficient = unchecked((uint)bits[0]) | ((UInt128)unchecked((uint)bits[1]) << 32)
            | ((UInt128)unchecked((uint)bits[2]) << 64);
        var scale = (bits[3] >> 16) & 0xff;
        UInt128 divisor = 1;
        for (var i = 0; i < scale; i++) divisor *= 10;
        var numerator = coefficient << 32;
        var quotient = numerator / divisor;
        var remainder = numerator % divisor;
        var twiceRemainder = remainder * 2;
        if (twiceRemainder > divisor || (twiceRemainder == divisor && (quotient & 1) != 0))
            quotient++;

        var negative = (bits[3] & int.MinValue) != 0;
        var maximumMagnitude = negative ? (UInt128)long.MaxValue + 1 : (UInt128)long.MaxValue;
        if (quotient > maximumMagnitude) throw new OverflowException();
        return new Fixed64(checked((long)(negative ? -(Int128)quotient : (Int128)quotient)));
    }

    /// <summary>Converts to decimal, rounded if more digits are needed than decimal can represent.</summary>
    public static explicit operator decimal(Fixed64 value) => (decimal)value._raw / Scale;

    /// <summary>Rounds to the nearest integer, ties to even, and checks the destination range.</summary>
    public static explicit operator int(Fixed64 value) => checked((int)RoundDivide(value._raw, Scale));
    /// <summary>Rounds to the nearest integer, ties to even.</summary>
    public static explicit operator long(Fixed64 value) => checked((long)RoundDivide(value._raw, Scale));

    public static Fixed64 operator +(Fixed64 left, Fixed64 right) => new(checked(left._raw + right._raw));
    public static Fixed64 operator -(Fixed64 left, Fixed64 right) => new(checked(left._raw - right._raw));
    public static Fixed64 operator -(Fixed64 value) => new(checked(-value._raw));
    public static Fixed64 operator *(Fixed64 left, Fixed64 right) =>
        new(checked((long)RoundDivide((Int128)left._raw * right._raw, Scale)));
    public static Fixed64 operator /(Fixed64 left, Fixed64 right) =>
        new(checked((long)RoundDivide((Int128)left._raw * Scale, right._raw)));

    public static bool operator ==(Fixed64 left, Fixed64 right) => left._raw == right._raw;
    public static bool operator !=(Fixed64 left, Fixed64 right) => left._raw != right._raw;
    public static bool operator <(Fixed64 left, Fixed64 right) => left._raw < right._raw;
    public static bool operator >(Fixed64 left, Fixed64 right) => left._raw > right._raw;
    public static bool operator <=(Fixed64 left, Fixed64 right) => left._raw <= right._raw;
    public static bool operator >=(Fixed64 left, Fixed64 right) => left._raw >= right._raw;
    public bool Equals(Fixed64 other) => _raw == other._raw;
    public override bool Equals(object? obj) => obj is Fixed64 other && Equals(other);
    public override int GetHashCode() => _raw.GetHashCode();
    public int CompareTo(Fixed64 other) => _raw.CompareTo(other._raw);
    public override string ToString() => ((decimal)this).ToString(CultureInfo.InvariantCulture);

    // All callers supply numerators whose absolute values are below 2^127 and denominators below
    // 2^64, so sign normalization and doubled remainders cannot overflow Int128.
    internal static Int128 RoundDivide(Int128 numerator, Int128 denominator)
    {
        if (denominator == 0) throw new DivideByZeroException();
        if (denominator < 0)
        {
            denominator = -denominator;
            numerator = -numerator;
        }
        var quotient = numerator / denominator;
        var remainder = numerator % denominator;
        var twiceRemainder = Int128.Abs(remainder) * 2;
        if (twiceRemainder > denominator || (twiceRemainder == denominator && (quotient & 1) != 0))
            quotient += numerator < 0 ? -1 : 1;
        return quotient;
    }
}

/// <summary>A two-dimensional vector whose components use checked Q32.32 arithmetic.</summary>
public readonly struct FixedVector2 : IEquatable<FixedVector2>
{
    public FixedVector2(Fixed64 x, Fixed64 y) { X = x; Y = y; }
    public Fixed64 X { get; }
    public Fixed64 Y { get; }
    public static FixedVector2 Zero => new(Fixed64.Zero, Fixed64.Zero);

    public static FixedVector2 operator +(FixedVector2 left, FixedVector2 right) => new(left.X + right.X, left.Y + right.Y);
    public static FixedVector2 operator -(FixedVector2 left, FixedVector2 right) => new(left.X - right.X, left.Y - right.Y);
    public static FixedVector2 operator -(FixedVector2 value) => new(-value.X, -value.Y);
    public static FixedVector2 operator *(FixedVector2 value, Fixed64 scalar) => new(value.X * scalar, value.Y * scalar);
    public static FixedVector2 operator *(Fixed64 scalar, FixedVector2 value) => value * scalar;
    public static FixedVector2 operator /(FixedVector2 value, Fixed64 scalar) => new(value.X / scalar, value.Y / scalar);

    /// <summary>Computes the sum of two individually rounded products; each operation is checked.</summary>
    public static Fixed64 Dot(FixedVector2 left, FixedVector2 right) => left.X * right.X + left.Y * right.Y;
    public Fixed64 LengthSquared() => Dot(this, this);
    public static bool operator ==(FixedVector2 left, FixedVector2 right) => left.Equals(right);
    public static bool operator !=(FixedVector2 left, FixedVector2 right) => !left.Equals(right);
    public bool Equals(FixedVector2 other) => X == other.X && Y == other.Y;
    public override bool Equals(object? obj) => obj is FixedVector2 other && Equals(other);
    public override int GetHashCode() => unchecked((X.GetHashCode() * 397) ^ Y.GetHashCode());
    public override string ToString() => $"({X}, {Y})";
}
