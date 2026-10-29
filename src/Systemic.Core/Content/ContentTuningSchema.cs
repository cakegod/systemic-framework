using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace Systemic.Content;

/// <summary>
/// An opt-in schema for a JSON object containing a properties object and a tags array. Unknown and
/// duplicate fields are errors. Defaults are applied only when a declared property is absent.
/// The schema and all successful results are immutable and may be reused across documents.
/// </summary>
public sealed class ContentTuningSchema
{
    public const int MaximumProperties = 256;
    public const int MaximumTags = 128;
    public const int MaximumDocumentBytes = 1_048_576;
    private static readonly UTF8Encoding s_utf8 = new(false, true);
    private readonly ImmutableDictionary<string, TuningProperty> _properties;
    private readonly ImmutableArray<string> _requiredTags;

    public ContentTuningSchema(IEnumerable<TuningProperty> properties, IEnumerable<string>? requiredTags = null)
    {
        ArgumentNullException.ThrowIfNull(properties);
        var declarations = ImmutableDictionary.CreateBuilder<string, TuningProperty>(StringComparer.Ordinal);
        foreach (var property in properties)
        {
            if (property is null || declarations.Count >= MaximumProperties || !declarations.TryAdd(property.Name, property))
            {
                throw new ArgumentException("Tuning properties must be unique and within the declaration limit.", nameof(properties));
            }
        }

        var tags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tag in requiredTags ?? [])
        {
            if (string.IsNullOrWhiteSpace(tag) || tag.Length > 128 || tags.Count >= MaximumTags || !tags.Add(tag))
            {
                throw new ArgumentException("Required tags must be unique, bounded names.", nameof(requiredTags));
            }
        }

        _properties = declarations.ToImmutable();
        _requiredTags = [.. tags.Order(StringComparer.Ordinal)];
    }

    public TuningDocument Parse(string json, string sourceFile)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFile);
        try
        {
            if (s_utf8.GetByteCount(json) > MaximumDocumentBytes)
            {
                throw SizeError(sourceFile);
            }

            return Parse(s_utf8.GetBytes(json), sourceFile);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ContentTuningException([new("tuning.invalid_json", sourceFile, "$", 1, 1, exception.Message)]);
        }
    }

    /// <summary>Parses UTF-8 JSON and reports errors with file, JSON path, line, and byte column.</summary>
    public TuningDocument Parse(ReadOnlyMemory<byte> json, string sourceFile)
        => ParseCore(json, sourceFile, "$", allowEntityMetadata: false);

    /// <summary>
    /// Validates one entity embedded in a complete entities JSON file. Diagnostics retain their
    /// original file line/column and are qualified with the entity array path.
    /// </summary>
    public TuningDocument ParseEntity(ReadOnlyMemory<byte> entitiesJson, string sourceFile, string entityId)
        => ParseEntity(entitiesJson, sourceFile, entityId, merged: null);

    /// <summary>Validates one entity after archetype inheritance.</summary>
    /// <param name="entitiesJson">The complete entities JSON the entity is embedded in.</param>
    /// <param name="sourceFile">The file name reported in diagnostics.</param>
    /// <param name="entityId">The entity to validate.</param>
    /// <param name="merged">
    /// The compiled entity after archetype inheritance. Properties and tags it inherits satisfy a
    /// required declaration even though the entity's own JSON does not spell them out.
    /// </param>
    public TuningDocument ParseEntity(ReadOnlyMemory<byte> entitiesJson, string sourceFile, string entityId,
        EntityDefinition? merged)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(entitiesJson, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException exception)
        {
            var position = SourceLocations.FromJsonException(entitiesJson.Span, exception);
            throw new ContentTuningException([new("tuning.invalid_json", sourceFile, exception.Path ?? "$",
                position.Line, position.Column, exception.Message)]);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("entities"u8, out var entities)
                || entities.ValueKind != JsonValueKind.Array)
            {
                throw new ContentTuningException([new("tuning.entity_missing", sourceFile, "$.entities", 1, 1,
                    "The entities document has no entities array.")]);
            }

            JsonElement match = default;
            var matchIndex = -1;
            var index = 0;
            var duplicate = false;
            foreach (var entity in entities.EnumerateArray())
            {
                if (entity.ValueKind == JsonValueKind.Object
                    && entity.TryGetProperty("id"u8, out var id)
                    && id.ValueKind == JsonValueKind.String
                    && string.Equals(id.GetString(), entityId, StringComparison.Ordinal))
                {
                    if (matchIndex >= 0) duplicate = true;
                    else { match = entity; matchIndex = index; }
                }
                index++;
            }

            if (duplicate)
            {
                throw new ContentTuningException([new("tuning.entity_duplicate", sourceFile, "$.entities",
                    1, 1, $"Entity '{entityId}' is declared more than once.")]);
            }
            if (matchIndex < 0)
            {
                throw new ContentTuningException([new("tuning.entity_missing", sourceFile, "$.entities",
                    1, 1, $"Entity '{entityId}' is not declared.")]);
            }

            var entityBytes = s_utf8.GetBytes(match.GetRawText());
            if (entityBytes.Length > MaximumDocumentBytes)
            {
                throw SizeError(sourceFile);
            }

            var start = entitiesJson.Span.IndexOf(entityBytes);
            if (start < 0)
            {
                throw new InvalidOperationException("Could not map the parsed entity back to its source bytes.");
            }

            var basePosition = SourceLocations.FromOffset(entitiesJson.Span, start);
            try
            {
                return ParseCore(entityBytes, sourceFile, $"$.entities[{matchIndex}]",
                    allowEntityMetadata: true, merged);
            }
            catch (ContentTuningException exception)
            {
                throw new ContentTuningException(exception.Diagnostics.Select(diagnostic => diagnostic with
                {
                    Line = basePosition.Line + diagnostic.Line - 1,
                    Column = diagnostic.Line == 1
                        ? basePosition.Column + diagnostic.Column - 1
                        : diagnostic.Column,
                }));
            }
        }
    }

    private TuningDocument ParseCore(ReadOnlyMemory<byte> json, string sourceFile, string pathPrefix,
        bool allowEntityMetadata, EntityDefinition? merged = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFile);
        if (json.Length > MaximumDocumentBytes)
        {
            throw SizeError(sourceFile);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException exception)
        {
            var position = SourceLocations.FromJsonException(json.Span, exception);
            throw new ContentTuningException([new("tuning.invalid_json", sourceFile, exception.Path ?? "$",
                position.Line, position.Column, exception.Message)]);
        }

        using (document)
        {
            ValidateStrings(json.Span, sourceFile);
            var locations = SourceLocations.Create(json.Span);
            var diagnostics = new List<ContentTuningDiagnostic>();
            var values = ImmutableDictionary.CreateBuilder<string, TuningValue>(StringComparer.Ordinal);
            var tags = new HashSet<string>(StringComparer.Ordinal);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                Add("tuning.type", "$", locations.First("$"), "The tuning document must be an object.");
                throw new ContentTuningException(diagnostics);
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in root.EnumerateObject())
            {
                var locationPath = AppendPath("$", field.Name);
                var path = Qualify(pathPrefix, locationPath);
                var position = locations.Take(locationPath);
                if (!seen.Add(field.Name))
                {
                    Add("tuning.duplicate", path, position, "The field is declared more than once.");
                    continue;
                }

                if (field.Name == "properties")
                {
                    ReadProperties(field.Value, locationPath, path, position);
                }
                else if (field.Name == "tags")
                {
                    ReadTags(field.Value, locationPath, path, position);
                }
                else if (allowEntityMetadata && field.Name is "id" or "kind" or "archetype" or "capabilities"
                    or "links" or "visualRecipeId" or "bounds" or "removeTags" or "removeCapabilities")
                {
                    continue;
                }
                else
                {
                    Add("tuning.unknown_field", path, position, "Unknown tuning field.");
                }
            }

            foreach (var declaration in _properties.Values.OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                if (values.ContainsKey(declaration.Name))
                {
                    continue;
                }

                if (TryReadInheritedValue(declaration, merged, out var inherited))
                {
                    values.Add(declaration.Name, inherited);
                }
                else if (declaration.DefaultValue is { } fallback)
                {
                    values.Add(declaration.Name, fallback);
                }
                else if (declaration.Required)
                {
                    var locationPath = AppendPath(AppendPath("$", "properties"), declaration.Name);
                    Add("tuning.required", Qualify(pathPrefix, locationPath),
                        locations.FirstOrRoot("$.properties"), "A required property is missing or invalid.");
                }
            }

            foreach (var required in _requiredTags)
            {
                if (merged is not null && merged.Tags.Contains(required, StringComparer.Ordinal))
                {
                    tags.Add(required);
                }

                if (!tags.Contains(required))
                {
                    Add("tuning.required_tag", Qualify(pathPrefix, "$.tags"),
                        locations.FirstOrRoot("$.tags"), $"Required tag '{required}' is missing.");
                }
            }

            if (diagnostics.Count > 0)
            {
                throw new ContentTuningException(diagnostics);
            }

            return new TuningDocument(values.ToImmutable(), tags);

            void Add(string code, string path, SourcePosition position, string message) =>
                diagnostics.Add(new ContentTuningDiagnostic(code, sourceFile, path, position.Line, position.Column, message));

            void ReadProperties(JsonElement element, string locationPath, string path, SourcePosition position)
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    Add("tuning.type", path, position, "Properties must be an object.");
                    return;
                }

                var names = new HashSet<string>(StringComparer.Ordinal);
                var count = 0;
                foreach (var property in element.EnumerateObject())
                {
                    if (++count > MaximumProperties)
                    {
                        Add("tuning.limit", path, position, "The property count exceeds the limit.");
                        break;
                    }

                    var propertyLocationPath = AppendPath(locationPath, property.Name);
                    var propertyPath = Qualify(pathPrefix, propertyLocationPath);
                    var propertyPosition = locations.Take(propertyLocationPath);
                    if (!names.Add(property.Name))
                    {
                        Add("tuning.duplicate", propertyPath, propertyPosition, "The property is declared more than once.");
                        continue;
                    }

                    if (!_properties.TryGetValue(property.Name, out var declaration))
                    {
                        Add("tuning.unknown_property", propertyPath, propertyPosition, "The property is not declared by the schema.");
                        continue;
                    }

                    if (!TryReadValue(declaration, property.Value, out var value, out var failure))
                    {
                        Add(failure, propertyPath, propertyPosition,
                            failure == "tuning.range" ? "The property is outside its declared range or length." : "The property has the wrong type or is not finite.");
                        continue;
                    }

                    values.Add(property.Name, value);
                }
            }

            void ReadTags(JsonElement element, string locationPath, string path, SourcePosition position)
            {
                if (element.ValueKind != JsonValueKind.Array)
                {
                    Add("tuning.type", path, position, "Tags must be an array of names.");
                    return;
                }

                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (index >= MaximumTags)
                    {
                        Add("tuning.limit", path, position, "The tag count exceeds the limit.");
                        break;
                    }

                    var itemLocationPath = $"{locationPath}[{index++}]";
                    var itemPath = Qualify(pathPrefix, itemLocationPath);
                    var itemPosition = locations.Take(itemLocationPath);
                    if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()) || item.GetString()!.Length > 128)
                    {
                        Add("tuning.type", itemPath, itemPosition, "A tag must be a nonempty name of at most 128 characters.");
                    }
                    else if (!tags.Add(item.GetString()!))
                    {
                        Add("tuning.duplicate", itemPath, itemPosition, "The tag is declared more than once.");
                    }
                }
            }
        }
    }

    private static string Qualify(string prefix, string path) =>
        prefix == "$" ? path : prefix + path[1..];

    // Archetype-inherited properties are merged before they reach here, and every entity property is
    // a double, so only the numeric and integer declarations can match them.
    private static bool TryReadInheritedValue(TuningProperty property, EntityDefinition? merged,
        out TuningValue value)
    {
        value = default;
        if (merged is null || !merged.Properties.TryGetValue(property.Name, out var number))
        {
            return false;
        }

        switch (property.Kind)
        {
            case TuningValueKind.Number:
                if (!double.IsFinite(number)
                    || number < property.NumberMinimum || number > property.NumberMaximum)
                {
                    return false;
                }

                value = TuningValue.FromNumber(number);
                return true;
            case TuningValueKind.Integer:
                if (number != Math.Truncate(number)
                    || number < property.IntegerMinimum || number > property.IntegerMaximum)
                {
                    return false;
                }

                value = TuningValue.FromInteger((long)number);
                return true;
            default:
                return false;
        }
    }

    private static bool TryReadValue(TuningProperty property, JsonElement input, out TuningValue value, out string failure)
    {
        value = default;
        failure = "tuning.type";
        switch (property.Kind)
        {
            case TuningValueKind.Number when input.ValueKind == JsonValueKind.Number && input.TryGetDouble(out var number) && double.IsFinite(number):
                if (number < property.NumberMinimum || number > property.NumberMaximum)
                {
                    failure = "tuning.range";
                    return false;
                }

                value = TuningValue.FromNumber(number);
                return true;
            case TuningValueKind.Integer when input.ValueKind == JsonValueKind.Number && input.TryGetInt64(out var integer):
                if (integer < property.IntegerMinimum || integer > property.IntegerMaximum)
                {
                    failure = "tuning.range";
                    return false;
                }

                value = TuningValue.FromInteger(integer);
                return true;
            case TuningValueKind.Boolean when input.ValueKind is JsonValueKind.True or JsonValueKind.False:
                value = TuningValue.FromBoolean(input.GetBoolean());
                return true;
            case TuningValueKind.Text when input.ValueKind == JsonValueKind.String:
                var text = input.GetString()!;
                if (text.Length > property.MaxLength)
                {
                    failure = "tuning.range";
                    return false;
                }

                value = TuningValue.FromText(text);
                return true;
            default:
                return false;
        }
    }

    private static ContentTuningException SizeError(string sourceFile) =>
        new([new("tuning.size", sourceFile, "$", 1, 1, "The tuning document exceeds the byte limit.")]);

    private static void ValidateStrings(ReadOnlySpan<byte> bytes, string sourceFile)
    {
        // JsonDocument validates structure before it decodes strings. Decode each token now so
        // malformed UTF-8 or unpaired escaped surrogates become source diagnostics, not runtime
        // exceptions from later property, tag, or value access.
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 16 });
        while (reader.Read())
        {
            if (reader.TokenType is not JsonTokenType.String and not JsonTokenType.PropertyName) continue;
            try
            {
                _ = reader.GetString();
            }
            catch (Exception exception) when (exception is InvalidOperationException or DecoderFallbackException)
            {
                var position = SourceLocations.FromOffset(bytes, (int)reader.TokenStartIndex);
                throw new ContentTuningException([new("tuning.invalid_json", sourceFile, "$",
                    position.Line, position.Column, "A JSON string contains invalid Unicode.")]);
            }
        }
    }

    private static string AppendPath(string prefix, string name)
    {
        if (name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_')
            && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
        {
            return $"{prefix}.{name}";
        }

        var result = new StringBuilder(prefix).Append("['");
        foreach (var character in name)
        {
            switch (character)
            {
                case '\\': result.Append("\\\\"); break;
                case '\'': result.Append("\\'"); break;
                case '\b': result.Append("\\b"); break;
                case '\f': result.Append("\\f"); break;
                case '\n': result.Append("\\n"); break;
                case '\r': result.Append("\\r"); break;
                case '\t': result.Append("\\t"); break;
                default:
                    if (char.IsControl(character)) result.Append("\\u").Append(((int)character).ToString("x4"));
                    else result.Append(character);
                    break;
            }
        }

        return result.Append("']").ToString();
    }

    private readonly record struct SourcePosition(int Line, int Column);

    private sealed class SourceLocations
    {
        private readonly Dictionary<string, List<int>> _offsets = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _used = new(StringComparer.Ordinal);
        private readonly int[] _lineStarts;

        private SourceLocations(ReadOnlySpan<byte> bytes)
        {
            var lines = new List<int> { 0 };
            for (var i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] == (byte)'\n' ||
                    (bytes[i] == (byte)'\r' && (i + 1 == bytes.Length || bytes[i + 1] != (byte)'\n')))
                {
                    lines.Add(i + 1);
                }
            }

            _lineStarts = [.. lines];
        }

        public static SourceLocations Create(ReadOnlySpan<byte> bytes)
        {
            var locations = new SourceLocations(bytes);
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 16 });
            reader.Read();
            locations.Add("$", (int)reader.TokenStartIndex);
            if (reader.TokenType != JsonTokenType.StartObject) return locations;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var name = reader.GetString()!;
                var path = AppendPath("$", name);
                locations.Add(path, (int)reader.TokenStartIndex);
                reader.Read();
                if (name == "properties" && reader.TokenType == JsonTokenType.StartObject)
                {
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                    {
                        var propertyPath = AppendPath(path, reader.GetString()!);
                        locations.Add(propertyPath, (int)reader.TokenStartIndex);
                        reader.Read();
                        reader.Skip();
                    }
                }
                else if (name == "tags" && reader.TokenType == JsonTokenType.StartArray)
                {
                    var index = 0;
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        locations.Add($"{path}[{index++}]", (int)reader.TokenStartIndex);
                        reader.Skip();
                    }
                }
                else
                {
                    reader.Skip();
                }
            }

            return locations;
        }

        public static SourcePosition FromJsonException(ReadOnlySpan<byte> bytes, JsonException exception)
        {
            // System.Text.Json counts LF characters when reporting parse errors. Convert its
            // position to an absolute byte offset, then use our CR/CRLF-aware line map.
            var remainingLines = exception.LineNumber.GetValueOrDefault();
            var offset = 0;
            while (remainingLines > 0 && offset < bytes.Length)
            {
                if (bytes[offset++] == (byte)'\n') remainingLines--;
            }

            offset = (int)Math.Min(bytes.Length, offset + exception.BytePositionInLine.GetValueOrDefault());
            return new SourceLocations(bytes).Position(offset);
        }

        public static SourcePosition FromOffset(ReadOnlySpan<byte> bytes, int offset) =>
            new SourceLocations(bytes).Position(offset);

        private void Add(string path, int offset)
        {
            if (!_offsets.TryGetValue(path, out var offsets))
            {
                offsets = [];
                _offsets.Add(path, offsets);
            }

            offsets.Add(offset);
        }

        public SourcePosition Take(string path)
        {
            if (!_offsets.TryGetValue(path, out var offsets)) return First("$");
            var index = _used.GetValueOrDefault(path);
            _used[path] = index + 1;
            return Position(offsets[Math.Min(index, offsets.Count - 1)]);
        }

        public SourcePosition First(string path) => Position(_offsets[path][0]);
        public SourcePosition FirstOrRoot(string path) => _offsets.ContainsKey(path) ? First(path) : First("$");

        private SourcePosition Position(int offset)
        {
            var index = Array.BinarySearch(_lineStarts, offset);
            if (index < 0) index = ~index - 1;
            return new SourcePosition(index + 1, offset - _lineStarts[index] + 1);
        }
    }
}
