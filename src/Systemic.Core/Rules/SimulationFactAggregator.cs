using System.Collections.Immutable;
using System.Numerics;

namespace Systemic.Rules;

public enum FactAggregationResult { Added, TypeNotConfigured, MissingPosition, InvalidFact, FactLimitReached, GroupLimitReached }
public readonly record struct SimulationFactRegion(long X, long Y);
public readonly record struct SimulationFactAggregate(FactTypeId Type, SimulationFactRegion Region, int Count, double TotalAmount);
public sealed class SimulationFactAggregator
{
    private readonly HashSet<FactTypeId> _types;
    private readonly long _regionWidth, _regionHeight;
    private readonly int _maxFacts, _maxGroups;
    private readonly Dictionary<(FactTypeId Type, SimulationFactRegion Region), (int Count, BigInteger Amount)> _groups = [];

    public SimulationFactAggregator(IEnumerable<FactTypeId> types, long regionWidth, long regionHeight, int maxFacts, int maxGroups)
    {
        ArgumentNullException.ThrowIfNull(types);
        _types = new HashSet<FactTypeId>(types);
        if (_types.Count == 0 || _types.Any(type => !type.IsValid))
            throw new ArgumentException("At least one valid fact type is required.", nameof(types));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(regionWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(regionHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFacts);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxGroups);
        _regionWidth = regionWidth;
        _regionHeight = regionHeight;
        _maxFacts = maxFacts;
        _maxGroups = maxGroups;
    }

    public int Count { get; private set; }

    public FactAggregationResult TryAdd(SimulationFact fact)
    {
        if (string.IsNullOrWhiteSpace(fact.Type) || fact.SourceId < 0 || fact.TargetId < 0
            || fact.Depth < 0 || !double.IsFinite(fact.Amount)) return FactAggregationResult.InvalidFact;
        var type = new FactTypeId(fact.Type);
        if (!_types.Contains(type)) return FactAggregationResult.TypeNotConfigured;
        if (fact.Position is not { } position) return FactAggregationResult.MissingPosition;
        if (Count >= _maxFacts) return FactAggregationResult.FactLimitReached;
        var region = new SimulationFactRegion(FloorDivide(position.X, _regionWidth), FloorDivide(position.Y, _regionHeight));
        var key = (type, region);
        if (!_groups.TryGetValue(key, out var group) && _groups.Count >= _maxGroups)
            return FactAggregationResult.GroupLimitReached;
        _groups[key] = (group.Count + 1, group.Amount + ExactUnits(fact.Amount));
        Count++;
        return FactAggregationResult.Added;
    }

    /// <summary>Returns a deterministic type, region-row, region-column order without consuming the batch.</summary>
    public ImmutableArray<SimulationFactAggregate> Snapshot() => [.. _groups
        .OrderBy(pair => pair.Key.Type.Value, StringComparer.Ordinal)
        .ThenBy(pair => pair.Key.Region.Y).ThenBy(pair => pair.Key.Region.X)
        .Select(pair => new SimulationFactAggregate(pair.Key.Type, pair.Key.Region,
            pair.Value.Count, ToDouble(pair.Value.Amount)))];

    public ImmutableArray<SimulationFactAggregate> Drain()
    {
        var result = Snapshot(); // If rounding overflows, retain the complete batch for recovery.
        Clear();
        return result;
    }

    public void Clear() { _groups.Clear(); Count = 0; }

    private static long FloorDivide(long value, long divisor)
    {
        var quotient = value / divisor;
        return value % divisor < 0 ? quotient - 1 : quotient;
    }

    // Every finite binary64 value is an integer multiple of 2^-1074. Keeping that integer
    // makes the sum independent of producer ordering and rounds only once at snapshot time.
    private static BigInteger ExactUnits(double value)
    {
        var bits = (ulong)BitConverter.DoubleToInt64Bits(value);
        var exponent = (int)((bits >> 52) & 0x7ff);
        var fraction = bits & 0xfffffffffffffUL;
        var units = exponent == 0
            ? new BigInteger(fraction)
            : new BigInteger(fraction | (1UL << 52)) << (exponent - 1);
        return (bits >> 63) == 0 ? units : -units;
    }

    private static double ToDouble(BigInteger units)
    {
        if (units.IsZero) return 0;
        var negative = units.Sign < 0;
        var magnitude = BigInteger.Abs(units);
        var length = checked((int)magnitude.GetBitLength());
        ulong bits;
        if (length <= 52) bits = (ulong)magnitude;
        else
        {
            var shift = length - 53;
            var significand = magnitude >> shift;
            if (shift > 0)
            {
                var remainder = magnitude - (significand << shift);
                var half = BigInteger.One << (shift - 1);
                if (remainder > half || (remainder == half && !significand.IsEven)) significand++;
            }
            if (significand == (BigInteger.One << 53)) { significand >>= 1; shift++; }
            var exponent = shift + 1;
            if (exponent >= 0x7ff) throw new OverflowException("Aggregated fact amount exceeds the finite double range.");
            bits = ((ulong)exponent << 52) | ((ulong)significand & 0xfffffffffffffUL);
        }
        if (negative) bits |= 1UL << 63;
        return BitConverter.Int64BitsToDouble(unchecked((long)bits));
    }
}
