using System.Globalization;
using Systemic.Kernel;

namespace Systemic.Tests.Kernel;

public sealed class DeterministicPrimitivesTests
{
    [Fact]
    public void Fixed_conversions_use_q32_32_and_correctly_round_decimal_boundaries()
    {
        Assert.Equal(5_368_709_120L, ((Fixed64)1.25m).RawValue);
        Assert.Equal(-9_663_676_416L, ((Fixed64)(-2.25m)).RawValue);
        Assert.Equal(0L, ((Fixed64)0.0000000001164153218269348144m).RawValue);
        Assert.Equal(1L, ((Fixed64)0.0000000001164153218269348145m).RawValue);
        Assert.Equal(-1L, ((Fixed64)(-0.0000000001164153218269348145m)).RawValue);
        Assert.Equal(9_223_372_036_811_826_135L, ((Fixed64)2_147_483_647.99m).RawValue);
        Assert.Equal(1.25m, (decimal)Fixed64.FromRaw(5_368_709_120L));
        Assert.Equal(int.MinValue, (int)(Fixed64)int.MinValue);
        Assert.Equal(2, (int)(Fixed64)1.5m);
        Assert.Equal(2, (int)(Fixed64)2.5m);
        Assert.Equal(-2, (int)(Fixed64)(-2.5m));
        Assert.Throws<OverflowException>(() => _ = (Fixed64)2_147_483_648m);
        Assert.Throws<OverflowException>(() => _ = (int)Fixed64.FromRaw(long.MaxValue));
    }

    [Fact]
    public void Fixed_arithmetic_rounds_half_to_even_for_both_signs()
    {
        var half = (Fixed64)0.5m;
        Assert.Equal(0, (Fixed64.FromRaw(1) * half).RawValue);
        Assert.Equal(2, (Fixed64.FromRaw(3) * half).RawValue);
        Assert.Equal(0, (Fixed64.FromRaw(-1) * half).RawValue);
        Assert.Equal(-2, (Fixed64.FromRaw(-3) * half).RawValue);
        Assert.Equal(0, (Fixed64.FromRaw(1) / (Fixed64)2).RawValue);
        Assert.Equal(2, (Fixed64.FromRaw(3) / (Fixed64)2).RawValue);
        Assert.Equal(-2, (Fixed64.FromRaw(-3) / (Fixed64)2).RawValue);
        Assert.Equal(-2, (Fixed64.FromRaw(3) / (Fixed64)(-2)).RawValue);
        Assert.Equal((Fixed64)1.5m, (Fixed64)2.25m - (Fixed64)0.75m);
        Assert.Equal((Fixed64)3m, (Fixed64)1.5m * (Fixed64)2m);
        Assert.Equal((Fixed64)(-0.75m), (Fixed64)1.5m / (Fixed64)(-2m));
    }

    [Fact]
    public void Fixed_arithmetic_and_conversions_check_overflow_and_division_by_zero()
    {
        var maximum = Fixed64.FromRaw(long.MaxValue);
        var minimum = Fixed64.FromRaw(long.MinValue);
        Assert.Throws<OverflowException>(() => _ = maximum + Fixed64.FromRaw(1));
        Assert.Throws<OverflowException>(() => _ = minimum - Fixed64.FromRaw(1));
        Assert.Throws<OverflowException>(() => _ = -minimum);
        Assert.Throws<OverflowException>(() => _ = maximum * (Fixed64)2);
        Assert.Throws<OverflowException>(() => _ = minimum / (Fixed64)(-1));
        Assert.Throws<DivideByZeroException>(() => _ = Fixed64.One / Fixed64.Zero);
        Assert.Throws<OverflowException>(() => _ = (Fixed64)long.MaxValue);
    }

    [Fact]
    public void Fixed_vector_operations_preserve_q32_values()
    {
        var a = new FixedVector2((Fixed64)3, (Fixed64)4);
        var b = new FixedVector2((Fixed64)(-1), (Fixed64)2);
        Assert.Equal(new FixedVector2((Fixed64)2, (Fixed64)6), a + b);
        Assert.Equal(new FixedVector2((Fixed64)4, (Fixed64)2), a - b);
        Assert.Equal(new FixedVector2((Fixed64)1.5m, (Fixed64)2), a * (Fixed64)0.5m);
        Assert.Equal((Fixed64)5, FixedVector2.Dot(a, b));
        Assert.Equal((Fixed64)25, a.LengthSquared());
    }

    [Fact]
    public void Integer_trigonometry_uses_exact_cardinal_turns_and_wraps()
    {
        Assert.Equal((Fixed64.Zero, Fixed64.One), DeterministicTrig.SinCos(Fixed64.Zero));
        Assert.Equal((Fixed64.One, Fixed64.Zero), DeterministicTrig.SinCos((Fixed64)0.25m));
        Assert.Equal((Fixed64.Zero, -Fixed64.One), DeterministicTrig.SinCos((Fixed64)0.5m));
        Assert.Equal((-Fixed64.One, Fixed64.Zero), DeterministicTrig.SinCos((Fixed64)(-0.25m)));
        Assert.Equal(DeterministicTrig.SinCos((Fixed64)0.125m),
            DeterministicTrig.SinCos((Fixed64)2.125m));
        Assert.Equal(DeterministicTrig.SinCos((Fixed64)(-0.125m)),
            DeterministicTrig.SinCos((Fixed64)0.875m));
    }

    [Fact]
    public void Integer_trigonometry_matches_fixed_reference_vectors()
    {
        var (sine, cosine) = DeterministicTrig.SinCos((Fixed64)0.125m);
        Assert.Equal(3_037_000_500L, sine.RawValue);
        Assert.Equal(3_037_000_500L, cosine.RawValue);
        (sine, cosine) = DeterministicTrig.SinCos(Fixed64.FromRaw(123_456_789));
        Assert.Equal(771_491_659L, sine.RawValue);
        Assert.Equal(4_225_108_838L, cosine.RawValue);
        (sine, cosine) = DeterministicTrig.SinCos(Fixed64.FromRaw(1));
        Assert.Equal(6L, sine.RawValue);
        Assert.Equal(4_294_967_296L, cosine.RawValue);
    }

    [Fact]
    public void Random_version_one_matches_portable_golden_vectors()
    {
        Assert.Equal(1, DeterministicRandom.AlgorithmVersion);
        Assert.Equal(0x70D7928FBC5645D8UL, DeterministicRandom.HashSystemId("movement"));
        Assert.Equal(0xF9A2D2A78E5AB248UL, DeterministicRandom.HashSystemId("café/移動"));
        Assert.Equal(0x617632A0366C4BE8UL, new DeterministicRandom(0, "movement", 0, 0).At(0));
        var random = new DeterministicRandom(1, "movement", 2, 3);
        Assert.Equal(0x9F1F8FBAB315EC18UL, random.At(0));
        Assert.Equal(0x374A2982797EBF1FUL, random.At(1));
        Assert.Equal(0xC50FA89FECD83627UL,
            new DeterministicRandom(0x0123456789ABCDEFUL, "café/移動", 0xFEDCBA9876543210UL, 99).At(123456789));
        Assert.Equal(0x80F130D57A792A3BUL,
            new DeterministicRandom(ulong.MaxValue, "combat", ulong.MaxValue, ulong.MaxValue).At(ulong.MaxValue));
        Assert.Equal(0x9F1F8FBAL, random.UnitFixed(0).RawValue);
    }

    [Fact]
    public void Random_draws_depend_on_identity_and_counter_not_execution_order_or_culture()
    {
        var first = new DeterministicRandom(20, "İ/wood", 42, 7);
        var second = new DeterministicRandom(20, "İ/wood", 43, 7);
        var before = new[] { first.At(3), second.At(0), first.At(0), first.At(3) };
        var oldCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var restored = new DeterministicRandom(20, "İ/wood", 42, 7);
            Assert.Equal(before[2], restored.At(0));
            Assert.Equal(before[0], restored.At(3));
            Assert.Equal(before[1], second.At(0));
            Assert.Equal(before[0], before[3]);
            Assert.NotEqual(before[0], new DeterministicRandom(21, "İ/wood", 42, 7).At(3));
            Assert.NotEqual(before[0], new DeterministicRandom(20, "i/wood", 42, 7).At(3));
            Assert.NotEqual(before[0], new DeterministicRandom(20, "İ/wood", 43, 7).At(3));
            Assert.NotEqual(before[0], new DeterministicRandom(20, "İ/wood", 42, 8).At(3));
            Assert.NotEqual(before[0], restored.At(4));
        }
        finally
        {
            CultureInfo.CurrentCulture = oldCulture;
        }
    }

    [Fact]
    public void Random_rejects_missing_or_invalid_system_identifiers()
    {
        Assert.Throws<ArgumentNullException>(() => DeterministicRandom.HashSystemId(null!));
        Assert.Throws<ArgumentException>(() => DeterministicRandom.HashSystemId(""));
        Assert.ThrowsAny<ArgumentException>(() => DeterministicRandom.HashSystemId("\ud800"));
    }

    [Fact]
    public void Decimal_operations_round_exact_results_to_the_requested_scale()
    {
        Assert.Equal(1.24m, DeterministicDecimal.Round(1.235m, 2));
        Assert.Equal(1.24m, DeterministicDecimal.Round(1.245m, 2));
        Assert.Equal(-1.24m, DeterministicDecimal.Round(-1.245m, 2));
        Assert.Equal(0.02m, DeterministicDecimal.Add(0.005m, 0.01m, 2));
        Assert.Equal(-0.02m, DeterministicDecimal.Subtract(0.005m, 0.02m, 2));
        Assert.Equal(2.34m, DeterministicDecimal.Multiply(1.5m, 1.56m, 2));
        Assert.Equal(0.12m, DeterministicDecimal.Divide(1m, 8m, 2));
        Assert.Equal(0.38m, DeterministicDecimal.Divide(3m, 8m, 2));
        Assert.Equal(-0.38m, DeterministicDecimal.Divide(-3m, 8m, 2));
        Assert.Equal(0.3333333333333333333333333333m, DeterministicDecimal.Divide(1m, 3m, 28));
        // Rounding the intermediate product to 28 places first would turn this into a tie
        // and incorrectly round it down to zero at scale 27.
        Assert.Equal(0.000000000000000000000000001m,
            DeterministicDecimal.Multiply(0.0000000000000000000000000001m, 5.00001m, 27));
        Assert.Equal(39614081257132168796771975168m, DeterministicDecimal.Divide(decimal.MaxValue, 2m, 0));
    }

    [Fact]
    public void Decimal_scaling_is_canonical_and_rejects_unrepresentable_results()
    {
        Assert.Equal(decimal.GetBits(1.20m), decimal.GetBits(DeterministicDecimal.Round(1.2m, 2)));
        Assert.Equal(decimal.GetBits(0.00m), decimal.GetBits(DeterministicDecimal.Round(-0.001m, 2)));
        Assert.Throws<OverflowException>(() => DeterministicDecimal.Round(decimal.MaxValue, 1));
        Assert.Throws<OverflowException>(() => DeterministicDecimal.Add(decimal.MaxValue, 1m, 0));
        Assert.Throws<OverflowException>(() => DeterministicDecimal.Multiply(decimal.MaxValue, 2m, 0));
        Assert.Throws<DivideByZeroException>(() => DeterministicDecimal.Divide(1m, 0m, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => DeterministicDecimal.Round(1m, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => DeterministicDecimal.Round(1m, 29));
    }
}
