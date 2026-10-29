using System.Collections.Immutable;

namespace Systemic.Rules;

/// <summary>
/// An ordinal, case-sensitive event identifier. Its value is the same string used by authored rules.
/// The default value is invalid and is rejected wherever a configured identifier is required.
/// </summary>
public readonly record struct FactTypeId
{
    private readonly string? _value;

    public FactTypeId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        _value = value;
    }

    public string Value => _value ?? string.Empty;
    public bool IsValid => !string.IsNullOrWhiteSpace(_value);
    public override string ToString() => Value;
}

/// <summary>
/// An exact integer position. Callers choose a consistent unit and origin; no floating-point
/// conversion or world-coordinate inference is performed by the fact or aggregation APIs.
/// </summary>
public readonly record struct SimulationFactPosition(long X, long Y);

public enum SimulationFactValueKind
{
    None,
    Integer,
    Number,
    Text,
    Boolean
}

/// <summary>A single immutable, bounded primitive payload value.</summary>
public readonly record struct SimulationFactValue
{
    public const int MaximumTextLength = 256;
    private readonly long _integer;
    private readonly double _number;
    private readonly string? _text;
    private readonly bool _boolean;

    private SimulationFactValue(SimulationFactValueKind kind, long integer = 0, double number = 0,
        string? text = null, bool boolean = false)
    {
        Kind = kind;
        _integer = integer;
        _number = number;
        _text = text;
        _boolean = boolean;
    }

    public SimulationFactValueKind Kind { get; }
    public long Integer => Kind == SimulationFactValueKind.Integer ? _integer : throw WrongKind();
    public double Number => Kind == SimulationFactValueKind.Number ? _number : throw WrongKind();
    public string Text => Kind == SimulationFactValueKind.Text ? _text! : throw WrongKind();
    public bool Boolean => Kind == SimulationFactValueKind.Boolean ? _boolean : throw WrongKind();

    public static SimulationFactValue FromInteger(long value) => new(SimulationFactValueKind.Integer, integer: value);
    public static SimulationFactValue FromBoolean(bool value) => new(SimulationFactValueKind.Boolean, boolean: value);

    public static SimulationFactValue FromNumber(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "A payload number must be finite.");
        }

        return new SimulationFactValue(SimulationFactValueKind.Number, number: value);
    }

    public static SimulationFactValue FromText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > MaximumTextLength)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "A payload text value exceeds its length limit.");
        }

        return new SimulationFactValue(SimulationFactValueKind.Text, text: value);
    }

    private static InvalidOperationException WrongKind() => new("The payload value has a different kind.");
}

public readonly record struct SimulationFactPayloadEntry(string Key, SimulationFactValue Value);

/// <summary>
/// Immutable payload with at most 16 uniquely named values, each key at most 64 UTF-16 code units
/// and each text value at most 256. Entries are copied and ordered by key using ordinal comparison.
/// </summary>
public sealed class SimulationFactPayload
{
    public const int MaximumEntries = 16;
    public const int MaximumKeyLength = 64;

    public SimulationFactPayload(IEnumerable<SimulationFactPayloadEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var copied = new List<SimulationFactPayloadEntry>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (copied.Count == MaximumEntries)
            {
                throw new ArgumentException("The payload exceeds its entry limit.", nameof(entries));
            }

            if (string.IsNullOrWhiteSpace(entry.Key) || entry.Key.Length > MaximumKeyLength
                || entry.Value.Kind == SimulationFactValueKind.None || !names.Add(entry.Key))
            {
                throw new ArgumentException("Payload keys must be unique and bounded; values must be initialized.", nameof(entries));
            }

            copied.Add(entry);
        }

        Entries = [.. copied.OrderBy(entry => entry.Key, StringComparer.Ordinal)];
    }

    public ImmutableArray<SimulationFactPayloadEntry> Entries { get; }

    public bool TryGetValue(string key, out SimulationFactValue value)
    {
        ArgumentNullException.ThrowIfNull(key);
        foreach (var entry in Entries)
        {
            if (string.Equals(entry.Key, key, StringComparison.Ordinal))
            {
                value = entry.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
