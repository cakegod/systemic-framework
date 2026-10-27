using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Systemic.Host;
using Systemic.Rules;

namespace Systemic.Content;

public interface ISystemicModuleContent
{
}

public interface ISystemicContentModule
{
    string Id { get; }
    ISystemicModuleContent Compile(ContentModuleContext context, JsonElement declaration);
    void Configure(SystemicGameBuilder builder, ISystemicModuleContent content);
}

/// <summary>
/// Module compilation inputs. Use these readers for pack files so their exact bytes
/// become part of the pack's content fingerprint. Other inputs require a caller-supplied
/// fingerprint on the resulting <see cref="ContentPack"/>.
/// </summary>
public sealed class ContentModuleContext(
    string rootDirectory,
    GameManifest manifest,
    CompiledEntities entities,
    CompiledRuleSet rules,
    ContentReferenceCatalog references,
    string? entitiesSourceFile = null,
    string? rulesSourceFile = null)
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 64,
    };

    public string RootDirectory { get; } = Path.GetFullPath(rootDirectory);
    public GameManifest Manifest { get; } = manifest;
    public CompiledEntities Entities { get; } = entities;
    public CompiledRuleSet Rules { get; } = rules;
    public ContentReferenceCatalog References { get; } = references;
    public string? EntitiesSourceFile { get; } = entitiesSourceFile;
    public string? RulesSourceFile { get; } = rulesSourceFile;

    internal ContentFingerprintBuilder? Fingerprint { get; init; }

    public JsonElement ReadJson(string relativePath)
    {
        var path = ResolveContentPath(relativePath);
        var bytes = ContentFileReader.ReadBytes(path, relativePath, Fingerprint);
        try
        {
            return ParseJson(bytes);
        }
        catch (JsonException exception)
        {
            throw new ContentPackException([new ContentDiagnostic("content.invalid_json", relativePath,
                exception.Path ?? "$", exception.Message)]);
        }
    }

    public T ReadFile<T>(JsonElement declaration, string fileKey, string fieldPath)
    {
        return ReadFile<T>(declaration, fileKey, fieldPath, out _);
    }

    public T ReadFile<T>(JsonElement declaration, string fileKey, string objectPath, out string relativePath)
    {
        relativePath = ReadPath(declaration, fileKey, objectPath);
        var fieldPath = MemberPath(objectPath, fileKey);
        return ReadFile<T>(relativePath, fieldPath);
    }

    public T ReadFile<T>(
        JsonElement declaration,
        string fileKey,
        string objectPath,
        string schemaKey,
        string schemaText,
        out string relativePath)
    {
        relativePath = ReadPath(declaration, fileKey, objectPath);
        var fieldPath = MemberPath(objectPath, fileKey);
        return ReadFile<T>(relativePath, fieldPath, (schemaKey, schemaText));
    }

    public JsonElement ReadJson(JsonElement declaration, string fileKey, string objectPath, out string relativePath)
    {
        relativePath = ReadPath(declaration, fileKey, objectPath);
        return ReadJson(relativePath, MemberPath(objectPath, fileKey));
    }

    public JsonElement ReadJson(
        JsonElement declaration,
        string fileKey,
        string objectPath,
        string schemaKey,
        string schemaText,
        out string relativePath)
    {
        relativePath = ReadPath(declaration, fileKey, objectPath);
        return ReadJson(relativePath, MemberPath(objectPath, fileKey), schemaKey, schemaText);
    }

    public static string ReadPath(JsonElement declaration, string fileKey, string objectPath)
    {
        if (declaration.ValueKind != JsonValueKind.Object
            || !declaration.TryGetProperty(fileKey, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new ContentPackException($"Module file key '{fileKey}' is required.", MemberPath(objectPath, fileKey));
        }

        return value.GetString()!;
    }

    private T ReadFile<T>(
        string relativePath,
        string fieldPath,
        (string Key, string Text)? schema = null)
    {
        try
        {
            var path = ResolveContentPath(relativePath);
            var bytes = schema is { } contentSchema
                ? ContentFileReader.ReadValidatedBytes(path, relativePath, contentSchema.Key, contentSchema.Text, Fingerprint)
                : ContentFileReader.ReadBytes(path, relativePath, Fingerprint);
            return JsonSerializer.Deserialize<T>(bytes, s_jsonOptions)
                ?? throw new ContentPackException($"Module content file '{relativePath}' is empty.", fieldPath);
        }
        catch (ContentPackException exception) when (exception.FieldPath is null && exception.Diagnostics.IsEmpty)
        {
            throw new ContentPackException(exception.Message, fieldPath, exception);
        }
        catch (JsonException exception)
        {
            throw new ContentPackException([new ContentDiagnostic("content.invalid_json", relativePath,
                exception.Path ?? "$", exception.Message)]);
        }
    }

    private JsonElement ReadJson(string relativePath, string fieldPath)
    {
        try
        {
            return ReadJson(relativePath);
        }
        catch (ContentPackException exception) when (exception.FieldPath is null && exception.Diagnostics.IsEmpty)
        {
            throw new ContentPackException(exception.Message, fieldPath, exception);
        }
    }

    private JsonElement ReadJson(string relativePath, string fieldPath, string schemaKey, string schemaText)
    {
        try
        {
            var path = ResolveContentPath(relativePath);
            var bytes = ContentFileReader.ReadValidatedBytes(path, relativePath, schemaKey, schemaText, Fingerprint);
            return ParseJson(bytes);
        }
        catch (ContentPackException exception) when (exception.FieldPath is null && exception.Diagnostics.IsEmpty)
        {
            throw new ContentPackException(exception.Message, fieldPath, exception);
        }
        catch (JsonException exception)
        {
            throw new ContentPackException([new ContentDiagnostic("content.invalid_json", relativePath,
                exception.Path ?? "$", exception.Message)]);
        }
    }

    private static string MemberPath(string objectPath, string property) =>
        objectPath == "$" ? $"$.{property}" : $"{objectPath}.{property}";

    private static JsonElement ParseJson(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        return document.RootElement.Clone();
    }

    private string ResolveContentPath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
        {
            throw new ContentPackException("Module content paths must be relative to the pack root.");
        }

        return ContentPathResolver.ResolveWithinRoot(
            RootDirectory, relativePath, "Module content path", "Module content file");
    }
}

public sealed class ContentReferenceCatalog
{
    private readonly Dictionary<(string Category, string Id), string> _sources = [];
    private readonly List<ContentReferenceRequirement> _requirements = [];

    public void Add(string category, string id, string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (!_sources.TryAdd((category, id), sourcePath))
        {
            throw new InvalidOperationException($"Content reference '{category}:{id}' is already registered.");
        }
    }

    public bool Contains(string category, string id) => _sources.ContainsKey((category, id));

    public bool TryGetSourcePath(string category, string id, out string sourcePath) =>
        _sources.TryGetValue((category, id), out sourcePath!);

    public void Require(string category, string id, string sourcePath, string fieldPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldPath);
        _requirements.Add(new ContentReferenceRequirement(category, id, sourcePath, fieldPath));
    }

    internal void ValidateRequired()
    {
        var diagnostics = _requirements
            .Where(requirement => !Contains(requirement.Category, requirement.Id))
            .Select(requirement => new ContentDiagnostic("content.reference_missing", requirement.SourcePath,
                requirement.FieldPath,
                $"Unknown {requirement.Category} reference '{requirement.Id}'."))
            .ToArray();
        if (diagnostics.Length > 0)
        {
            throw new ContentPackException(diagnostics);
        }
    }

    private sealed record ContentReferenceRequirement(string Category, string Id, string SourcePath, string FieldPath);
}

public sealed class ContentModuleRegistry
{
    private readonly Dictionary<string, ISystemicContentModule> _modules = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ContentTuningSchema> _tuningSchemas = new(StringComparer.Ordinal);

    public ContentModuleRegistry Add(ISystemicContentModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentException.ThrowIfNullOrWhiteSpace(module.Id);
        if (!_modules.TryAdd(module.Id, module))
        {
            throw new InvalidOperationException($"Content module '{module.Id}' is already registered.");
        }

        return this;
    }

    /// <summary>Registers a strict tuning schema for one entity in the pack's entities file.</summary>
    public ContentModuleRegistry AddTuningSchema(string entityId, ContentTuningSchema schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        ArgumentNullException.ThrowIfNull(schema);
        if (!_tuningSchemas.TryAdd(entityId, schema))
        {
            throw new InvalidOperationException($"A tuning schema for entity '{entityId}' is already registered.");
        }

        return this;
    }

    internal ImmutableDictionary<string, TuningDocument> CompileTunings(ReadOnlyMemory<byte> entitiesJson, string sourceFile)
    {
        var documents = ImmutableDictionary.CreateBuilder<string, TuningDocument>(StringComparer.Ordinal);
        var diagnostics = ImmutableArray.CreateBuilder<ContentTuningDiagnostic>();
        foreach (var (entityId, schema) in _tuningSchemas.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            try
            {
                documents.Add(entityId, schema.ParseEntity(entitiesJson, sourceFile, entityId));
            }
            catch (ContentTuningException exception)
            {
                diagnostics.AddRange(exception.Diagnostics);
            }
        }

        if (diagnostics.Count > 0)
        {
            throw new ContentTuningException(diagnostics);
        }

        return documents.ToImmutable();
    }

    internal bool HasTuningSchemas => _tuningSchemas.Count != 0;

    internal ImmutableArray<CompiledSystemicModule> CompileDeclared(
        ContentModuleContext context,
        IReadOnlyDictionary<string, JsonElement>? declarations)
    {
        if (declarations is null || declarations.Count == 0)
        {
            return [];
        }

        var compiled = ImmutableArray.CreateBuilder<CompiledSystemicModule>(declarations.Count);
        var diagnostics = new List<ContentDiagnostic>();
        foreach (var (id, declaration) in declarations.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var path = $"$.modules.{id}";
            if (!_modules.TryGetValue(id, out var module))
            {
                diagnostics.Add(new ContentDiagnostic("module.not_registered", "game.json", path,
                    $"No content module registered for '{id}'."));
                continue;
            }

            try
            {
                var content = module.Compile(context, declaration)
                    ?? throw new ContentPackException($"Content module '{id}' returned null compiled content.");
                compiled.Add(new CompiledSystemicModule(module, content));
            }
            catch (ContentPackException exception)
            {
                if (!exception.Diagnostics.IsEmpty)
                {
                    diagnostics.AddRange(exception.Diagnostics);
                }
                else
                {
                    diagnostics.Add(new ContentDiagnostic("module.invalid", "game.json",
                        exception.FieldPath is null ? path : JoinModulePath(path, exception.FieldPath), exception.Message));
                }
            }
            catch (JsonException exception)
            {
                diagnostics.Add(new ContentDiagnostic("module.invalid_json", "game.json", path, exception.Message));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException)
            {
                diagnostics.Add(new ContentDiagnostic("module.io_error", "game.json", path, exception.Message));
            }
        }

        if (diagnostics.Count > 0)
        {
            throw new ContentPackException(diagnostics);
        }

        return compiled.MoveToImmutable();
    }

    private static string JoinModulePath(string modulePath, string fieldPath) =>
        fieldPath.StartsWith('$') ? modulePath + fieldPath[1..] : $"{modulePath}.{fieldPath}";
}

public sealed record CompiledSystemicModule(ISystemicContentModule Module, ISystemicModuleContent Content);
