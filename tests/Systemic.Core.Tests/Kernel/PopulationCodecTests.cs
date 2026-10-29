using System.Numerics;
using Systemic.Kernel;

namespace Systemic.Tests.Kernel;

public sealed class PopulationCodecTests
{
    private static readonly bool[] Booleans = [false, true];
    private static readonly byte[] Bytes = [0, 255];
    private static readonly sbyte[] SignedBytes = [sbyte.MinValue, sbyte.MaxValue];
    private static readonly char[] Characters = ['\0', 'é'];
    private static readonly short[] Shorts = [short.MinValue, short.MaxValue];
    private static readonly ushort[] UnsignedShorts = [ushort.MinValue, ushort.MaxValue];
    private static readonly int[] Integers = [int.MinValue, int.MaxValue];
    private static readonly uint[] UnsignedIntegers = [uint.MinValue, uint.MaxValue];
    private static readonly long[] Longs = [long.MinValue, long.MaxValue];
    private static readonly ulong[] UnsignedLongs = [ulong.MinValue, ulong.MaxValue];
    private static readonly float[] Singles = [float.NegativeInfinity, -0.0f, float.NaN];
    private static readonly double[] Doubles = [double.NegativeInfinity, -0.0, double.NaN];
    private static readonly decimal[] Decimals = [decimal.MinValue, -0.0m, decimal.MaxValue];
    private static readonly Vector2[] Vector2s = [new Vector2(float.NaN, -0.0f)];
    private static readonly Vector3[] Vector3s = [new Vector3(1, -2, 3)];
    private static readonly Vector4[] Vector4s = [new Vector4(1, -2, 3, 4)];

    [Fact]
    public void Blittable_codecs_encode_the_stable_little_endian_wire_format()
    {
        AssertBytes(PopulationCodecs.Int32, [-2], [0xFE, 0xFF, 0xFF, 0xFF]);
        AssertBytes(PopulationCodecs.UInt64, [0x0102_0304_0506_0708UL], [0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01]);
        AssertBytes(PopulationCodecs.Double, [1.5, -0.0], [0, 0, 0, 0, 0, 0, 0xF8, 0x3F, 0, 0, 0, 0, 0, 0, 0, 0x80]);
        AssertBytes(PopulationCodecs.Single, [1.5f], [0, 0, 0xC0, 0x3F]);
        AssertBytes(PopulationCodecs.Char, ['A'], [0x41, 0x00]);
        AssertBytes(PopulationCodecs.Vector2, [new Vector2(1, 2)], [0, 0, 0x80, 0x3F, 0, 0, 0, 0x40]);
        AssertBytes(PopulationCodecs.Vector3, [new Vector3(1, 2, 3)],
            [0, 0, 0x80, 0x3F, 0, 0, 0, 0x40, 0, 0, 0x40, 0x40]);
    }

    [Fact]
    public void Non_blittable_codecs_validate_their_wire_values()
    {
        AssertBytes(PopulationCodecs.Boolean, [true], [1]);
        Assert.Throws<FormatException>(() => PopulationCodecs.Boolean.Decode([2], new bool[1]));
        AssertBytes(PopulationCodecs.Decimal, [1.5m], [0x0F, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0]);
    }

    [Fact]
    public void Every_built_in_codec_round_trips_its_values()
    {
        RoundTrip(PopulationCodecs.Boolean, Booleans);
        RoundTrip(PopulationCodecs.Byte, Bytes);
        RoundTrip(PopulationCodecs.SByte, SignedBytes);
        RoundTrip(PopulationCodecs.Char, Characters);
        RoundTrip(PopulationCodecs.Int16, Shorts);
        RoundTrip(PopulationCodecs.UInt16, UnsignedShorts);
        RoundTrip(PopulationCodecs.Int32, Integers);
        RoundTrip(PopulationCodecs.UInt32, UnsignedIntegers);
        RoundTrip(PopulationCodecs.Int64, Longs);
        RoundTrip(PopulationCodecs.UInt64, UnsignedLongs);
        RoundTrip(PopulationCodecs.Single, Singles);
        RoundTrip(PopulationCodecs.Double, Doubles);
        RoundTrip(PopulationCodecs.Decimal, Decimals);
        RoundTrip(PopulationCodecs.Vector2, Vector2s);
        RoundTrip(PopulationCodecs.Vector3, Vector3s);
        RoundTrip(PopulationCodecs.Vector4, Vector4s);
    }

    [Fact]
    public void Handle_generations_are_deterministic_per_world()
    {
        static ulong[] Generations()
        {
            var world = new World(seed: 77, valueScale: 1);
            var population = world.DefinePopulation<HandledKind>(enableHandles: true);
            population.AddRange(4);
            population.AddRange(2);
            return [.. world.CapturePopulationState().Populations[0].HandleSlots.Select(slot => slot.Generation)];
        }

        Assert.Equal(Generations(), Generations());
    }

    [Fact]
    public void Population_state_keeps_the_two_argument_shape_existing_callers_use()
    {
        var world = new World(seed: 79, valueScale: 1);
        var population = world.DefinePopulation<HandledKind>(enableHandles: true);
        population.AddRange(1);

        var captured = world.CapturePopulationState();
        var (version, populations) = captured;
        var rebuilt = new PopulationWorldState(version, populations) { HandleGeneration = 7 };

        Assert.Equal(PopulationWorldState.CurrentVersion, version);
        Assert.Same(captured.Populations, populations);
        Assert.True(captured.HandleGeneration > 0);
        Assert.Equal(7, rebuilt.HandleGeneration);
    }

    [Fact]
    public void A_rolled_back_spawn_does_not_change_the_next_generation_a_load_issues()
    {
        var world = new World(seed: 78, valueScale: 1);
        var population = world.DefinePopulation<HandledKind>(enableHandles: true);
        population.AddRange(1);
        using (var transaction = world.BeginTransaction())
        {
            population.AddRange(1);
            transaction.Rollback();
        }

        var loadedWorld = new World(seed: 78, valueScale: 1);
        var loaded = loadedWorld.DefinePopulation<HandledKind>(enableHandles: true);
        loadedWorld.RestorePopulationState(world.CapturePopulationState());

        var continued = population.AddRange(1);
        var restored = loaded.AddRange(1);

        Assert.Equal(population.GetHandle(continued.Start).Generation,
            loaded.GetHandle(restored.Start).Generation);
    }

    private static void AssertBytes<T>(IPopulationCodec<T> codec, T[] values, byte[] expected)
        where T : unmanaged
    {
        var data = new byte[codec.GetEncodedLength(values.Length)];
        codec.Encode(values, data);
        Assert.Equal(expected, data);
    }

    private readonly struct HandledKind : IPopulationKind
    {
        public static string Id => "test.codec-handles";

        public static int Version => 1;
    }

    private static void RoundTrip<T>(IPopulationCodec<T> codec, T[] values) where T : unmanaged
    {
        var data = new byte[codec.GetEncodedLength(values.Length)];
        codec.Encode(values, data);
        var restored = new T[values.Length];
        codec.Decode(data, restored);
        for (var index = 0; index < values.Length; index++)
        {
            if (typeof(T) == typeof(float))
            {
                Assert.Equal(BitConverter.SingleToInt32Bits((float)(object)values[index]),
                    BitConverter.SingleToInt32Bits((float)(object)restored[index]));
            }
            else if (typeof(T) == typeof(double))
            {
                Assert.Equal(BitConverter.DoubleToInt64Bits((double)(object)values[index]),
                    BitConverter.DoubleToInt64Bits((double)(object)restored[index]));
            }
            else
            {
                Assert.Equal(values[index], restored[index]);
            }
        }
    }
}
