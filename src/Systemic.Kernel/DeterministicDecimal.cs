using System.Numerics;

namespace Systemic.Kernel;

/// <summary>Explicitly scaled decimal operations for deterministic economic calculations.</summary>
/// <remarks>
/// Inputs are finite .NET decimals. Operations use exact integer rational intermediates, then
/// round once to the requested decimal scale using ties to even. The result always has exactly
/// that scale (0 through 28), including trailing zeros; zero has a canonical positive sign.
/// If the resulting scaled coefficient cannot fit in 96 bits, an <see cref="OverflowException"/>
/// is thrown, even if the numerical value could fit at a smaller scale. Persist both the value
/// and its scale. Division by zero throws <see cref="DivideByZeroException"/>.
/// </remarks>
public static class DeterministicDecimal
{
    private static readonly BigInteger MaximumCoefficient = (BigInteger.One << 96) - 1;

    /// <summary>Rounds a decimal to the given fixed scale, with ties to even.</summary>
    public static decimal Round(decimal value, int scale)
    {
        ValidateScale(scale);
        var (coefficient, inputScale) = Parts(value);
        return Result(coefficient, Power10(inputScale), scale);
    }

    /// <summary>Adds two values exactly and rounds the sum to the given fixed scale.</summary>
    public static decimal Add(decimal left, decimal right, int scale)
    {
        ValidateScale(scale);
        var (a, aScale) = Parts(left);
        var (b, bScale) = Parts(right);
        return Result(a * Power10(bScale) + b * Power10(aScale), Power10(aScale + bScale), scale);
    }

    /// <summary>Subtracts two values exactly and rounds the difference to the given fixed scale.</summary>
    public static decimal Subtract(decimal left, decimal right, int scale)
    {
        ValidateScale(scale);
        var (a, aScale) = Parts(left);
        var (b, bScale) = Parts(right);
        return Result(a * Power10(bScale) - b * Power10(aScale), Power10(aScale + bScale), scale);
    }

    /// <summary>Multiplies two values exactly and rounds the product to the given fixed scale.</summary>
    public static decimal Multiply(decimal left, decimal right, int scale)
    {
        ValidateScale(scale);
        var (a, aScale) = Parts(left);
        var (b, bScale) = Parts(right);
        return Result(a * b, Power10(aScale + bScale), scale);
    }

    /// <summary>Divides two values exactly and rounds the quotient to the given fixed scale.</summary>
    public static decimal Divide(decimal left, decimal right, int scale)
    {
        ValidateScale(scale);
        var (a, aScale) = Parts(left);
        var (b, bScale) = Parts(right);
        if (b.IsZero)
        {
            throw new DivideByZeroException();
        }

        return Result(a * Power10(bScale), b * Power10(aScale), scale);
    }

    private static (BigInteger Coefficient, int Scale) Parts(decimal value)
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);
        BigInteger coefficient = unchecked((uint)bits[0]) | ((BigInteger)unchecked((uint)bits[1]) << 32)
            | ((BigInteger)unchecked((uint)bits[2]) << 64);
        if ((bits[3] & int.MinValue) != 0)
        {
            coefficient = -coefficient;
        }

        return (coefficient, (bits[3] >> 16) & 0xff);
    }

    private static decimal Result(BigInteger numerator, BigInteger denominator, int scale)
    {
        if (denominator.Sign < 0) { numerator = -numerator; denominator = -denominator; }
        numerator *= Power10(scale);
        var negative = numerator.Sign < 0;
        var quotient = BigInteger.DivRem(BigInteger.Abs(numerator), denominator, out var remainder);
        var compare = (remainder * 2).CompareTo(denominator);
        if (compare > 0 || (compare == 0 && !quotient.IsEven))
        {
            quotient++;
        }

        if (quotient > MaximumCoefficient)
        {
            throw new OverflowException();
        }

        var low = (uint)(quotient & uint.MaxValue);
        var middle = (uint)((quotient >> 32) & uint.MaxValue);
        var high = (uint)((quotient >> 64) & uint.MaxValue);
        return new decimal(unchecked((int)low), unchecked((int)middle), unchecked((int)high),
            negative && !quotient.IsZero, (byte)scale);
    }

    private static BigInteger Power10(int scale) => BigInteger.Pow(10, scale);
    private static void ValidateScale(int scale)
    {
        if (scale < 0 || scale > 28)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), "Scale must be between 0 and 28.");
        }
    }
}
