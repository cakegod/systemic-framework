using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Corvus.Text.Json;
using Corvus.Text.Json.Validator;
using Corvus.Text.Json.RuntimeEvaluator;
using SystemJsonElement = System.Text.Json.JsonElement;

namespace Systemic.Content;

internal static class ContentSchemaValidator
{
    private static readonly ConcurrentDictionary<string, Lazy<CachedSchema>> s_schemas = new(StringComparer.Ordinal);
    private static readonly JsonSchema.Options s_options = new(
        additionalSchemaFiles: [],
        allowFileSystemAndHttpResolution: false,
        defaultDialect: JsonSchemaDialect.Draft202012,
        alwaysAssertFormat: false,
        additionalDocumentResolver: null,
        codeGeneration: JsonSchemaCodeGeneration.Disabled);

    public static ImmutableArray<ContentDiagnostic> Validate(
        string schemaKey,
        string schemaText,
        ReadOnlyMemory<byte> json,
        string sourceFile)
    {
        var cached = s_schemas.GetOrAdd(schemaKey, _ => new Lazy<CachedSchema>(
            () => new CachedSchema(schemaText, JsonSchema.FromText(schemaText, schemaKey, s_options)),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        if (!string.Equals(cached.Text, schemaText, StringComparison.Ordinal))
        {
            throw new ContentPackException([new ContentDiagnostic("content.schema_conflict", sourceFile, "$",
                $"Schema ID '{schemaKey}' was reused with different schema text.")]);
        }

        var schema = cached.Schema;
        using var document = JsonDocument.Parse(json);
        using var results = JsonSchemaResultsCollector.Create(JsonSchemaResultsLevel.Detailed);
        if (schema.Validate(json, results))
        {
            return [];
        }

        var failures = new List<SchemaFailure>();
        foreach (var result in results.EnumerateResults())
        {
            if (!result.IsMatch)
            {
                var pointer = result.GetDocumentEvaluationLocationText();
                failures.Add(new SchemaFailure(pointer,
                    PointerToJsonPath(pointer, document.RootElement), result.GetMessageText()));
            }
        }

        var specificFailures = failures.Where(failure => !IsSubschemaSummary(failure.Message)).ToArray();
        var diagnostics = ImmutableArray.CreateBuilder<ContentDiagnostic>();
        foreach (var group in failures.GroupBy(failure => failure.Path, StringComparer.Ordinal))
        {
            var candidates = group.Where(failure => !string.IsNullOrWhiteSpace(failure.Message)
                && !IsSubschemaSummary(failure.Message)).ToArray();
            if (candidates.Length == 0)
            {
                candidates = [.. group.Where(failure => !string.IsNullOrWhiteSpace(failure.Message))];
            }

            if (candidates.Length == 0)
            {
                candidates = [group.First()];
            }

            var leafFailures = candidates.Where(candidate => !IsSubschemaSummary(candidate.Message)
                || !specificFailures.Any(specific => specific.Pointer.StartsWith(candidate.Pointer + "/", StringComparison.Ordinal)))
                .DistinctBy(candidate => candidate.Message, StringComparer.Ordinal);
            foreach (var failure in leafFailures)
            {
                diagnostics.Add(new ContentDiagnostic("content.schema_invalid", sourceFile, failure.Path,
                    string.IsNullOrWhiteSpace(failure.Message) ? "Value does not satisfy the content schema." : failure.Message));
            }
        }

        return diagnostics.ToImmutable();
    }

    private static bool IsSubschemaSummary(string message) =>
        message.Contains("expected to match the subschema", StringComparison.OrdinalIgnoreCase);

    private static string PointerToJsonPath(string pointer, SystemJsonElement root)
    {
        if (pointer.Length == 0)
        {
            return "$";
        }

        var path = new System.Text.StringBuilder("$");
        var current = root;
        foreach (var escapedSegment in pointer[1..].Split('/'))
        {
            var segment = escapedSegment.Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
            if (current.ValueKind == System.Text.Json.JsonValueKind.Array
                && int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
            {
                path.Append('[').Append(index.ToString(CultureInfo.InvariantCulture)).Append(']');
                current = index < current.GetArrayLength() ? current[index] : default;
            }
            else if (current.ValueKind == System.Text.Json.JsonValueKind.Object
                && segment.Length > 0 && (char.IsAsciiLetter(segment[0]) || segment[0] == '_')
                && segment.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'))
            {
                path.Append('.').Append(segment);
                current = current.TryGetProperty(segment, out var property) ? property : default;
            }
            else
            {
                path.Append("['").Append(EscapeJsonPathName(segment)).Append("']");
                current = current.ValueKind == System.Text.Json.JsonValueKind.Object
                    && current.TryGetProperty(segment, out var property) ? property : default;
            }
        }

        return path.ToString();
    }

    private static string EscapeJsonPathName(string name)
    {
        var escaped = new System.Text.StringBuilder(name.Length);
        foreach (var character in name)
        {
            switch (character)
            {
                case '\\':
                    escaped.Append("\\\\");
                    break;
                case '\'':
                    escaped.Append("\\'");
                    break;
                case '\b':
                    escaped.Append("\\b");
                    break;
                case '\f':
                    escaped.Append("\\f");
                    break;
                case '\n':
                    escaped.Append("\\n");
                    break;
                case '\r':
                    escaped.Append("\\r");
                    break;
                case '\t':
                    escaped.Append("\\t");
                    break;
                default:
                    if (char.IsControl(character))
                    {
                        escaped.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        escaped.Append(character);
                    }

                    break;
            }
        }

        return escaped.ToString();
    }

    private sealed record SchemaFailure(string Pointer, string Path, string Message);
    private sealed record CachedSchema(string Text, JsonSchema Schema);
}
