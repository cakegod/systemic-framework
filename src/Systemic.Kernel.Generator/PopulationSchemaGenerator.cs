using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Systemic.Kernel.Generator;

[Generator]
public sealed class PopulationSchemaGenerator : IIncrementalGenerator
{
    private const string SchemaAttribute = "Systemic.Kernel.PopulationSchemaAttribute";
    private const string ColumnAttribute = "Systemic.Kernel.PopulationColumnAttribute";
    private const string PopulationKind = "Systemic.Kernel.IPopulationKind";
    private const string PopulationSchema = "Systemic.Kernel.IPopulationSchema";
    private const string CodecInterface = "Systemic.Kernel.IPopulationCodec<T>";
    private static readonly DiagnosticDescriptor InvalidSchema = new(
        "PTBK001", "Invalid population schema", "Invalid population schema '{0}': {1}",
        "PopulationSchema", DiagnosticSeverity.Error, isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var schemas = context.SyntaxProvider.ForAttributeWithMetadataName(
            SchemaAttribute,
            static (node, _) => node is StructDeclarationSyntax,
            static (syntax, _) => (INamedTypeSymbol)syntax.TargetSymbol);
        context.RegisterSourceOutput(schemas, static (output, schema) => Emit(output, schema));
    }

    private static void Emit(SourceProductionContext output, INamedTypeSymbol schema)
    {
        if (Validate(schema, out var columns, out var id, out var version, out var enableHandles) is { } error)
        {
            output.ReportDiagnostic(Diagnostic.Create(InvalidSchema,
                schema.Locations.FirstOrDefault(), schema.Name, error));
            return;
        }

        var namespaceName = schema.ContainingNamespace.IsGlobalNamespace
            ? null
            : schema.ContainingNamespace.ToDisplayString();
        var schemaType = schema.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var kindInterface = "global::" + PopulationKind;
        var populationSchemaInterface = $"global::{PopulationSchema}<{schemaType}>";
        var builder = new StringBuilder("#nullable enable\n");
        if (namespaceName is not null)
        {
            builder.AppendLine($"namespace {namespaceName};");
        }

        var accessibility = schema.DeclaredAccessibility == Accessibility.Public ? "public" : "internal";
        builder.AppendLine($"{accessibility} partial struct {Escape(schema.Name)} : {kindInterface}, {populationSchemaInterface}");
        builder.AppendLine("{");
        builder.AppendLine($"    public static string Id => {SymbolDisplay.FormatLiteral(id!, quote: true)};");
        builder.AppendLine($"    public static int Version => {version};");
        EmitInitializer(builder, schemaType, Escape(schema.Name) + "Population", columns!);
        builder.AppendLine("}");

        var companion = Escape(schema.Name) + "Population";
        builder.AppendLine($"{accessibility} static class {companion}");
        builder.AppendLine("{");
        builder.AppendLine($"    public static global::Systemic.Kernel.Population<{schemaType}> Define(");
        builder.AppendLine("        global::Systemic.Kernel.World world, bool? enableHandles = null) =>");
        builder.AppendLine($"        world.DefinePopulation<{schemaType}>(enableHandles ?? {enableHandles.ToString().ToLowerInvariant()});");
        foreach (var column in columns!)
        {
            builder.AppendLine($"    public static global::Systemic.Kernel.PopulationColumnKey<{column.TypeName}> {column.KeyProperty} {{ get; }} =");
            builder.AppendLine($"        new({SymbolDisplay.FormatLiteral(column.Id, quote: true)}, {version}, {column.CodecExpression});");
            builder.AppendLine($"    public static global::Systemic.Kernel.SystemAccess {Escape("Read" + column.Name)} {{ get; }} =");
            builder.AppendLine($"        global::Systemic.Kernel.SystemAccess.ReadColumn<{schemaType}, {column.TypeName}>({column.KeyProperty});");
            builder.AppendLine($"    public static global::Systemic.Kernel.SystemAccess {Escape("Write" + column.Name)} {{ get; }} =");
            builder.AppendLine($"        global::Systemic.Kernel.SystemAccess.WriteColumn<{schemaType}, {column.TypeName}>({column.KeyProperty});");
        }

        builder.AppendLine($"    public static Columns Bind(global::Systemic.Kernel.Population<{schemaType}> population) => new(population);");
        builder.AppendLine("    /// <summary>A detached snapshot of one row. Edit its values and pass it to WriteRow or RangeWriter.Write.</summary>");
        builder.AppendLine("    public struct Row");
        builder.AppendLine("    {");
        foreach (var column in columns!)
        {
            builder.AppendLine($"        public {column.TypeName} {Escape(column.Name)} {{ get; set; }}");
        }
        builder.AppendLine("    }");

        builder.AppendLine("    /// <summary>Borrowed writable views of a checked live row range.</summary>");
        builder.AppendLine("    /// <remarks>Finish using the writer before structural changes, cloning, adoption, buffer swaps, or transaction changes. Concurrent writers must use disjoint ranges.</remarks>");
        builder.AppendLine("    public readonly ref struct RangeWriter");
        builder.AppendLine("    {");
        for (var i = 0; i < columns!.Count; i++)
        {
            builder.AppendLine($"        private readonly global::System.Span<{columns[i].TypeName}> _column{i};");
        }
        builder.AppendLine("        public int Length { get; }");
        builder.Append($"        internal RangeWriter(int length");
        for (var i = 0; i < columns.Count; i++)
        {
            builder.Append($", global::System.Span<{columns[i].TypeName}> column{i}");
        }
        builder.AppendLine(")");
        builder.AppendLine("        {");
        builder.AppendLine("            Length = length;");
        for (var i = 0; i < columns.Count; i++)
        {
            builder.AppendLine($"            _column{i} = column{i};");
        }
        builder.AppendLine("        }");
        builder.AppendLine("        /// <summary>Writes one row by range-relative index; double-buffered columns are written to Next.</summary>");
        builder.AppendLine("        public void Write(int index, Row value)");
        builder.AppendLine("        {");
        builder.AppendLine("            if (index < 0 || index >= Length) throw new global::System.ArgumentOutOfRangeException(nameof(index));");
        for (var i = 0; i < columns.Count; i++)
        {
            builder.AppendLine($"            _column{i}[index] = value.{Escape(columns[i].Name)};");
        }
        builder.AppendLine("        }");
        builder.AppendLine("    }");

        builder.AppendLine("    public readonly struct Columns");
        builder.AppendLine("    {");
        builder.AppendLine($"        private readonly global::Systemic.Kernel.Population<{schemaType}> _population;");
        foreach (var column in columns!)
        {
            var columnType = column.DoubleBuffered
                ? "DoubleBufferedPopulationColumn"
                : "PopulationColumn";
            builder.AppendLine($"        public global::Systemic.Kernel.{columnType}<{column.TypeName}> {Escape(column.Name)} {{ get; }}");
        }

        builder.AppendLine($"        internal Columns(global::Systemic.Kernel.Population<{schemaType}> population)");
        builder.AppendLine("        {");
        builder.AppendLine("            _population = population ?? throw new global::System.ArgumentNullException(nameof(population));");
        foreach (var column in columns!)
        {
            var bindMethod = column.DoubleBuffered ? "DoubleBufferedColumn" : "Column";
            builder.AppendLine($"            this.{Escape(column.Name)} = population.{bindMethod}({column.KeyProperty});");
        }

        builder.AppendLine("        }");
        builder.AppendLine("        /// <summary>Copies the current values of a checked live row into a detached snapshot.</summary>");
        builder.AppendLine("        public Row ReadRow(int row)");
        builder.AppendLine("        {");
        builder.AppendLine("            ValidateRow(row);");
        builder.AppendLine("            return new Row");
        builder.AppendLine("            {");
        foreach (var column in columns)
        {
            var readView = column.DoubleBuffered ? "Current" : "ReadOnlySpan";
            builder.AppendLine($"                {Escape(column.Name)} = this.{Escape(column.Name)}.{readView}[row],");
        }
        builder.AppendLine("            };");
        builder.AppendLine("        }");
        builder.AppendLine("        /// <summary>Writes a checked live row. Double-buffered values are written to Next.</summary>");
        builder.AppendLine("        public void WriteRow(int row, Row value)");
        builder.AppendLine("        {");
        builder.AppendLine("            ValidateRow(row);");
        builder.AppendLine("            WriteRange(new global::Systemic.Kernel.PopulationRange(row, 1)).Write(0, value);");
        builder.AppendLine("        }");
        builder.AppendLine("        /// <summary>Acquires a checked writer for a live row range; buffered columns target Next.</summary>");
        builder.AppendLine("        public RangeWriter WriteRange(global::Systemic.Kernel.PopulationRange range)");
        builder.AppendLine("        {");
        builder.Append("            return new RangeWriter(range.Length");
        foreach (var column in columns)
        {
            var editMethod = column.DoubleBuffered ? "EditNext" : "EditRange";
            builder.Append($", this.{Escape(column.Name)}.{editMethod}(range.Start, range.Length)");
        }
        builder.AppendLine(");");
        builder.AppendLine("        }");
        builder.AppendLine("        private void ValidateRow(int row)");
        builder.AppendLine("        {");
        builder.AppendLine("            if (row < 0 || row >= _population.Count) throw new global::System.ArgumentOutOfRangeException(nameof(row));");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
        builder.AppendLine("}");

        // Roslyn compares hint names case-insensitively and rejects punctuation such as '@'.
        // Hex-encode the full name so case, keywords and non-ASCII identifiers remain distinct.
        var hintName = new StringBuilder("Population_");
        foreach (var octet in Encoding.UTF8.GetBytes(schemaType)) hintName.Append(octet.ToString("x2"));
        output.AddSource($"{hintName}.g.cs", SourceText.From(builder.ToString(), Encoding.UTF8));
    }

    private static string? Validate(
        INamedTypeSymbol schema,
        out List<ColumnModel>? columns,
        out string? id,
        out int version,
        out bool enableHandles)
    {
        columns = null;
        id = null;
        version = 0;
        enableHandles = false;
        if (schema.Arity != 0 || schema.ContainingType is not null)
        {
            return "the schema must be a non-generic top-level struct";
        }

        if (schema.DeclaringSyntaxReferences.Any(reference =>
                reference.GetSyntax() is not StructDeclarationSyntax declaration
                || !declaration.Modifiers.Any(SyntaxKind.PartialKeyword)))
        {
            return "the schema and all declarations must be partial";
        }

        if (schema.IsReadOnly || schema.IsRefLikeType || schema.TypeKind != TypeKind.Struct
            || !schema.IsUnmanagedType)
        {
            return "the schema must be an unmanaged, non-readonly, non-ref struct";
        }

        if (schema.AllInterfaces.Any(type => type.ToDisplayString() == PopulationKind
                || type.OriginalDefinition.ToDisplayString().StartsWith(PopulationSchema + "<", StringComparison.Ordinal))
            || schema.GetMembers("Id").Length > 0 || schema.GetMembers("Version").Length > 0
            || schema.GetMembers("InitializeRange").Length > 0)
        {
            return "Id, Version, InitializeRange, and population interfaces are generated and must not be declared";
        }

        var attribute = schema.GetAttributes().FirstOrDefault(item =>
            item.AttributeClass?.ToDisplayString() == SchemaAttribute);
        if (attribute is null || attribute.ConstructorArguments.Length != 2
            || attribute.ConstructorArguments[0].Value is not string schemaId
            || string.IsNullOrWhiteSpace(schemaId)
            || attribute.ConstructorArguments[1].Value is not int schemaVersion || schemaVersion <= 0)
        {
            return "the schema needs a non-empty stable ID and positive version";
        }

        id = schemaId;
        version = schemaVersion;
        enableHandles = attribute.NamedArguments.FirstOrDefault(pair => pair.Key == "EnableHandles")
            .Value.Value as bool? ?? false;
        var companionName = schema.Name + "Population";
        if (schema.ContainingNamespace.GetTypeMembers(companionName).Length > 0)
        {
            return $"generated companion type '{companionName}' already exists";
        }

        columns = [];
        var columnIds = new HashSet<string>(StringComparer.Ordinal);
        if (schema.GetMembers().OfType<IPropertySymbol>().Any(property => !property.IsStatic))
        {
            return "population columns must be declared as fields";
        }

        foreach (var field in schema.GetMembers().OfType<IFieldSymbol>().Where(field => !field.IsImplicitlyDeclared))
        {
            var columnAttribute = field.GetAttributes().FirstOrDefault(item =>
                item.AttributeClass?.ToDisplayString() == ColumnAttribute);
            if (field.IsStatic || field.IsConst)
            {
                continue;
            }

            if (columnAttribute is null)
            {
                return $"instance field '{field.Name}' must be marked with PopulationColumn";
            }

            if (!field.Type.IsUnmanagedType)
            {
                return $"column '{field.Name}' must be an instance field of an unmanaged type";
            }

            if (columnAttribute.ConstructorArguments.FirstOrDefault().Value is not string columnId
                || string.IsNullOrWhiteSpace(columnId) || !columnIds.Add(columnId))
            {
                return $"column '{field.Name}' needs a unique, non-empty stable ID";
            }

            var typeName = field.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var doubleBuffered = columnAttribute.NamedArguments
                .FirstOrDefault(pair => pair.Key == "DoubleBuffered").Value.Value as bool? ?? false;
            var codecType = columnAttribute.NamedArguments
                .FirstOrDefault(pair => pair.Key == "CodecType").Value.Value as ITypeSymbol;
            var codecExpression = GetCodecExpression(field.Type, codecType);
            if (codecExpression is null)
            {
                return $"column '{field.Name}' uses unsupported type '{field.Type.ToDisplayString()}'; provide a matching codec";
            }

            columns.Add(new ColumnModel(field.Name, columnId, typeName, doubleBuffered,
                Escape(field.Name) + "Key", codecExpression));
        }

        if (columns.Count == 0)
        {
            return "the schema must declare at least one PopulationColumn field";
        }

        // Column properties, the row snapshot, and the companion expose different sets of names.
        // Detect collisions here so a schema error points to the declaration, rather than leaving
        // consumers with compiler errors in generated code.
        var columnMembers = new HashSet<string>(StringComparer.Ordinal)
            { "Columns", "ReadRow", "WriteRow", "WriteRange", "ValidateRow", "_population" };
        var rowMembers = new HashSet<string>(StringComparer.Ordinal) { "Row" };
        var companionMembers = new HashSet<string>(StringComparer.Ordinal)
            { companionName, "Define", "Bind", "Columns", "Row", "RangeWriter" };
        foreach (var column in columns)
        {
            if (!columnMembers.Add(column.Name) || !rowMembers.Add(column.Name))
            {
                return $"column '{column.Name}' conflicts with a generated member named '{column.Name}'";
            }
            foreach (var name in new[] { column.Name + "Key", "Read" + column.Name, "Write" + column.Name })
            {
                if (!companionMembers.Add(name))
                {
                    return $"column '{column.Name}' conflicts with a generated member named '{name}'";
                }
            }
        }

        return null;
    }

    private static void EmitInitializer(
        StringBuilder builder,
        string schemaType,
        string companionName,
        List<ColumnModel> columns)
    {
        builder.AppendLine($"    public static void InitializeRange(");
        builder.AppendLine($"        global::Systemic.Kernel.Population<{schemaType}> population,");
        builder.AppendLine("        global::Systemic.Kernel.PopulationRange range,");
        builder.AppendLine($"        global::System.ReadOnlySpan<{schemaType}> values)");
        builder.AppendLine("    {");
        builder.AppendLine("        if (values.Length != range.Length)");
        builder.AppendLine("        {");
        builder.AppendLine("            throw new global::System.ArgumentException(\"The schema value count must match the population range.\", nameof(values));");
        builder.AppendLine("        }");
        builder.AppendLine($"        var columns = {companionName}.Bind(population);");
        for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
        {
            var column = columns[columnIndex];
            var editName = "editColumn" + columnIndex;
            var editMethod = column.DoubleBuffered ? "EditBothBuffers" : "EditRange";
            builder.AppendLine($"        var {editName} = columns.{Escape(column.Name)}.{editMethod}(range.Start, range.Length);");
        }

        builder.AppendLine("        for (var index = 0; index < values.Length; index++)");
        builder.AppendLine("        {");
        for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
        {
            var column = columns[columnIndex];
            var editName = "editColumn" + columnIndex;
            var valueName = $"values[index].{Escape(column.Name)}";
            if (column.DoubleBuffered)
            {
                builder.AppendLine($"            {editName}.Current[index] = {valueName};");
                builder.AppendLine($"            {editName}.Next[index] = {valueName};");
            }
            else
            {
                builder.AppendLine($"            {editName}[index] = {valueName};");
            }
        }

        builder.AppendLine("        }");
        builder.AppendLine("    }");
    }

    private static string? GetCodecExpression(ITypeSymbol type, ITypeSymbol? customCodec)
    {
        if (customCodec is not null)
        {
            var expectedInterface = CodecInterface;
            var matching = customCodec.AllInterfaces.Any(candidate =>
                candidate.OriginalDefinition.ToDisplayString() == expectedInterface
                && SymbolEqualityComparer.Default.Equals(candidate.TypeArguments[0], type));
            var hasPublicDefaultConstructor = customCodec is INamedTypeSymbol named
                && !named.IsAbstract
                && named.InstanceConstructors.Any(constructor => constructor.Parameters.Length == 0
                    && constructor.DeclaredAccessibility == Accessibility.Public);
            return matching && hasPublicDefaultConstructor
                ? $"new {customCodec.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}()"
                : null;
        }

        var codecName = type.SpecialType switch
        {
            SpecialType.System_Boolean => "Boolean",
            SpecialType.System_Byte => "Byte",
            SpecialType.System_SByte => "SByte",
            SpecialType.System_Char => "Char",
            SpecialType.System_Int16 => "Int16",
            SpecialType.System_UInt16 => "UInt16",
            SpecialType.System_Int32 => "Int32",
            SpecialType.System_UInt32 => "UInt32",
            SpecialType.System_Int64 => "Int64",
            SpecialType.System_UInt64 => "UInt64",
            SpecialType.System_Single => "Single",
            SpecialType.System_Double => "Double",
            SpecialType.System_Decimal => "Decimal",
            _ => type.ToDisplayString() switch
            {
                "System.Numerics.Vector2" => "Vector2",
                "System.Numerics.Vector3" => "Vector3",
                "System.Numerics.Vector4" => "Vector4",
                _ => null
            }
        };
        return codecName is null ? null : $"global::Systemic.Kernel.PopulationCodecs.{codecName}";
    }

    private static string Escape(string identifier) =>
        SyntaxFacts.GetKeywordKind(identifier) == SyntaxKind.None ? identifier : "@" + identifier;

    private static string Sanitize(string identifier) =>
        "_" + new string([.. identifier.Select(character => char.IsLetterOrDigit(character) ? character : '_')]);

    private sealed class ColumnModel(
        string name,
        string id,
        string typeName,
        bool doubleBuffered,
        string keyProperty,
        string codecExpression)
    {
        public string Name { get; } = name;
        public string Id { get; } = id;
        public string TypeName { get; } = typeName;
        public bool DoubleBuffered { get; } = doubleBuffered;
        public string KeyProperty { get; } = keyProperty;
        public string CodecExpression { get; } = codecExpression;
    }
}
