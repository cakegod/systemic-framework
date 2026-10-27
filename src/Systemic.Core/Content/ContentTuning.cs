using System.Collections.Immutable;

namespace Systemic.Content;

public enum TuningValueKind
{
    None,
    Number,
    Integer,
    Boolean,
    Text
}

/// <summary>An immutable primitive value in a validated tuning document.</summary>
public readonly record struct TuningValue
{
    private readonly double _number;
    private readonly long _integer;
    private readonly bool _boolean;
    private readonly string? _text;

    private TuningValue(TuningValueKind kind, double number = 0, long integer = 0, bool boolean = false, string? text = null)
    {
        Kind = kind;
        _number = number;
        _integer = integer;
        _boolean = boolean;
        _text = text;
    }

    public TuningValueKind Kind { get; }
    public double Number => Kind == TuningValueKind.Number ? _number : throw WrongKind();
    public long Integer => Kind == TuningValueKind.Integer ? _integer : throw WrongKind();
    public bool Boolean => Kind == TuningValueKind.Boolean ? _boolean : throw WrongKind();
    public string Text => Kind == TuningValueKind.Text ? _text! : throw WrongKind();

    internal static TuningValue FromNumber(double value) => new(TuningValueKind.Number, number: value);
    internal static TuningValue FromInteger(long value) => new(TuningValueKind.Integer, integer: value);
    internal static TuningValue FromBoolean(bool value) => new(TuningValueKind.Boolean, boolean: value);
    internal static TuningValue FromText(string value) => new(TuningValueKind.Text, text: value);
    private static InvalidOperationException WrongKind() => new("The tuning value has a different type.");
}

/// <summary>A typed property declaration with inclusive ranges and an optional default.</summary>
public sealed class TuningProperty
{
    private TuningProperty(string name, TuningValueKind kind, bool required, TuningValue? defaultValue,
        double? numberMinimum = null, double? numberMaximum = null,
        long? integerMinimum = null, long? integerMaximum = null, int maxLength = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(name));
        }

        Name = name;
        Kind = kind;
        Required = required;
        DefaultValue = defaultValue;
        NumberMinimum = numberMinimum;
        NumberMaximum = numberMaximum;
        IntegerMinimum = integerMinimum;
        IntegerMaximum = integerMaximum;
        MaxLength = maxLength;
    }

    public string Name { get; }
    public TuningValueKind Kind { get; }
    public bool Required { get; }
    public TuningValue? DefaultValue { get; }
    public double? NumberMinimum { get; }
    public double? NumberMaximum { get; }
    public long? IntegerMinimum { get; }
    public long? IntegerMaximum { get; }
    public int MaxLength { get; }

    public static TuningProperty Number(string name, double? minimum = null, double? maximum = null,
        double? defaultValue = null, bool required = false)
    {
        if ((minimum is { } min && !double.IsFinite(min)) || (maximum is { } max && !double.IsFinite(max))
            || minimum > maximum || (defaultValue is { } value && (!double.IsFinite(value) || value < minimum || value > maximum)))
        {
            throw new ArgumentOutOfRangeException(nameof(defaultValue), "Number bounds and default must be finite and consistent.");
        }

        return new TuningProperty(name, TuningValueKind.Number, required,
            defaultValue is { } number ? TuningValue.FromNumber(number) : null, minimum, maximum);
    }

    public static TuningProperty Integer(string name, long? minimum = null, long? maximum = null,
        long? defaultValue = null, bool required = false)
    {
        if (minimum > maximum || (defaultValue is { } value && (value < minimum || value > maximum)))
        {
            throw new ArgumentOutOfRangeException(nameof(defaultValue), "Integer bounds and default must be consistent.");
        }

        return new TuningProperty(name, TuningValueKind.Integer, required,
            defaultValue is { } integer ? TuningValue.FromInteger(integer) : null,
            integerMinimum: minimum, integerMaximum: maximum);
    }

    public static TuningProperty Boolean(string name, bool? defaultValue = null, bool required = false) =>
        new(name, TuningValueKind.Boolean, required,
            defaultValue is { } boolean ? TuningValue.FromBoolean(boolean) : null);

    public static TuningProperty Text(string name, int maxLength = 256, string? defaultValue = null, bool required = false)
    {
        if (maxLength is < 1 or > 4096 || defaultValue?.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLength), "Text limit or default is invalid.");
        }

        return new TuningProperty(name, TuningValueKind.Text, required,
            defaultValue is not null ? TuningValue.FromText(defaultValue) : null, maxLength: maxLength);
    }
}

/// <summary>One-based source line and one-based UTF-8 byte column accompany the JSON path.</summary>
public sealed record ContentTuningDiagnostic(string Code, string File, string Path, int Line, int Column, string Message);

public sealed class ContentTuningException : Exception
{
    internal ContentTuningException(IEnumerable<ContentTuningDiagnostic> diagnostics)
        : this(diagnostics.ToImmutableArray()) { }

    private ContentTuningException(ImmutableArray<ContentTuningDiagnostic> diagnostics)
        : base(string.Join(Environment.NewLine, diagnostics.Select(d => $"{d.File}:{d.Line}:{d.Column} {d.Path} [{d.Code}] {d.Message}")))
    {
        Diagnostics = diagnostics;
    }

    public ImmutableArray<ContentTuningDiagnostic> Diagnostics { get; }
}

/// <summary>Validated immutable tuning values and canonical, ordinal-sorted tags.</summary>
public sealed class TuningDocument
{
    private readonly ImmutableDictionary<string, TuningValue> _values;

    internal TuningDocument(ImmutableDictionary<string, TuningValue> values, IEnumerable<string> tags)
    {
        _values = values;
        Tags = Array.AsReadOnly(tags.Order(StringComparer.Ordinal).ToArray());
    }

    public IReadOnlyDictionary<string, TuningValue> Values => _values;
    public IReadOnlyList<string> Tags { get; }
    public bool TryGetValue(string name, out TuningValue value) => _values.TryGetValue(name, out value);
    public double GetNumber(string name) => _values[name].Number;
    public long GetInteger(string name) => _values[name].Integer;
    public bool GetBoolean(string name) => _values[name].Boolean;
    public string GetText(string name) => _values[name].Text;
}
