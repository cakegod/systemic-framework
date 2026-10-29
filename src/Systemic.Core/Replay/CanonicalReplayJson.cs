using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Systemic.Replay;

internal static class CanonicalReplayJson
{
    internal const int MaximumInputLength = 1_048_576;

    public static string Normalize(string json) => Normalize(json, MaximumInputLength);

    public static string Normalize(string json, int maximumLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLength);
        if (json.Length > maximumLength) throw new ArgumentException("Replay JSON exceeds the input limit.", nameof(json));
        try
        {
            using var document = JsonDocument.Parse(json);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream)) Write(writer, document.RootElement);
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException exception) { throw new ArgumentException("Replay JSON is invalid.", nameof(json), exception); }
    }

    private static void Write(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var names = new HashSet<string>(StringComparer.Ordinal);
                var properties = element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal);
                foreach (var property in properties)
                {
                    if (!names.Add(property.Name)) throw new ArgumentException($"Duplicate JSON property '{property.Name}'.");
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) Write(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(Number(element.GetRawText()));
                break;
            default: element.WriteTo(writer); break;
        }
    }

    private static string Number(string raw)
    {
        var negative = raw[0] == '-';
        var number = negative ? raw[1..] : raw;
        var exponentAt = number.IndexOfAny(['e', 'E']);
        var exponent = 0;
        if (exponentAt >= 0)
        {
            if (!int.TryParse(number.AsSpan(exponentAt + 1), NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out exponent) || Math.Abs((long)exponent) > 1_000_000)
                throw new ArgumentException("JSON number exponent is outside the supported replay range.");
            number = number[..exponentAt];
        }
        var point = number.IndexOf('.');
        var fractionalDigits = point < 0 ? 0 : number.Length - point - 1;
        var digits = number.Replace(".", "", StringComparison.Ordinal).TrimStart('0');
        if (digits.Length == 0) return negative ? "-0" : "0";
        var trailing = digits.Length;
        while (trailing > 1 && digits[trailing - 1] == '0') trailing--;
        exponent = checked(exponent - fractionalDigits + digits.Length - trailing);
        digits = digits[..trailing];
        var decimalPlace = (long)digits.Length + exponent;
        string value;
        if (decimalPlace is > 0 and <= 1024)
        {
            value = decimalPlace >= digits.Length
                ? digits + new string('0', (int)decimalPlace - digits.Length)
                : digits.Insert((int)decimalPlace, ".");
        }
        else if (decimalPlace is <= 0 and >= -1024)
            value = "0." + new string('0', (int)-decimalPlace) + digits;
        else
            value = digits[0] + (digits.Length > 1 ? "." + digits[1..] : "") + "e"
                + (decimalPlace - 1).ToString(CultureInfo.InvariantCulture);
        return negative ? "-" + value : value;
    }
}
