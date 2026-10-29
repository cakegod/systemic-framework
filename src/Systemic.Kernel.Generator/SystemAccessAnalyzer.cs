using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using System.Threading;

namespace Systemic.Kernel.Generator;

/// <summary>Checks declared World access and unsafe population edits in range callbacks.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SystemAccessAnalyzer : DiagnosticAnalyzer
{
    private const string SystemicCoreAssembly = "Systemic.Core";
    private const string SystemicKernelAssembly = "Systemic.Kernel";
    private const string SystemicSystemMetadataName = "Systemic.Host.ISystemicSystem";
    private const string SystemicSystemContextMetadataName = "Systemic.Host.SystemicSystemContext";
    private const string SystemAccessMetadataName = "Systemic.Kernel.SystemAccess";
    private const string PopulationColumnMetadataName = "Systemic.Kernel.PopulationColumn`1";
    private const string DoubleBufferedPopulationColumnMetadataName = "Systemic.Kernel.DoubleBufferedPopulationColumn`1";
    private const string PopulationColumnKeyMetadataName = "Systemic.Kernel.PopulationColumnKey`1";
    private const string PopulationAccessScopeMetadataName = "Systemic.Kernel.PopulationAccessScope`1";
    private const string WorldCommandBufferMetadataName = "Systemic.Kernel.WorldCommandBuffer";
    private const string ImmutableArrayMetadataName = "System.Collections.Immutable.ImmutableArray`1";
    private const string Guide = "https://github.com/cakegod/systemic-framework/blob/main/docs/api/analyzers.md";

    private static readonly DiagnosticDescriptor ImmediateWorldAccess = new(
        id: "SYSACC001",
        title: "Immediate World access is undeclared",
        messageFormat: "SystemicSystemContext.{0} requires SystemAccess.WriteWorld in Access",
        category: "SystemAccess",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Immediate World operations require the system's Access declaration to include SystemAccess.WriteWorld.",
        helpLinkUri: Guide + "#sysacc001");

    private static readonly DiagnosticDescriptor DeferredPopulationAccess = new(
        id: "SYSACC002",
        title: "Deferred population commands are undeclared",
        messageFormat: "SystemicSystemContext.DeferPopulationCommands requires SystemAccess.DeferWorldWrites or SystemAccess.WriteWorld in Access",
        category: "SystemAccess",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Deferred population commands require the system to declare deferred or exclusive World write access.",
        helpLinkUri: Guide + "#sysacc002");

    private static readonly DiagnosticDescriptor DirectRangeMutation = new(
        id: "SYSACC003",
        title: "Direct population mutations are unsafe in a range callback",
        messageFormat: "PopulationColumn.{0} is unsafe inside a range callback. {1}.",
        category: "SystemAccess",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Direct population column edits and buffer swaps inside a range callback can escape the worker's assigned range or race across workers.",
        helpLinkUri: Guide + "#sysacc003");

    private static readonly DiagnosticDescriptor ColumnAccess = new(
        id: "SYSACC004",
        title: "Generated population column access is undeclared",
        messageFormat: "Generated population column '{0}' is used for {1} access, but Access must include {2}",
        category: "SystemAccess",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Direct generated population column reads and writes require matching per-column access declarations.",
        helpLinkUri: Guide + "#sysacc004");

    private static readonly DiagnosticDescriptor UnsafeRangeMutation = new(
        id: "SYSACC005",
        title: "World mutation is unsafe inside a range callback",
        messageFormat: "Immediate World operations and shared WorldCommandBuffer mutations are unsafe inside a range callback",
        category: "SystemAccess",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Range callbacks can run concurrently, so immediate World mutations and shared command-buffer writes are not safe there.",
        helpLinkUri: Guide + "#sysacc005");

    private static readonly DiagnosticDescriptor ParallelWholeColumnRead = new(
        id: "SYSACC006",
        title: "Whole-column read overlaps parallel writes",
        messageFormat: "Parallel range callbacks cannot read all rows of writable single-buffered column '{0}'; use ReadRange for the assigned rows or read an immutable snapshot",
        category: "SystemAccess",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A parallel callback may read only its assigned rows from a single-buffered column that any partition writes.",
        helpLinkUri: Guide + "#sysacc006");

    private static readonly DiagnosticDescriptor DeferredFactAccess = new(
        id: "SYSACC008",
        title: "Deferred fact dispatch is undeclared",
        messageFormat: "SystemicSystemContext.DeferFact requires SystemAccess.DeferWorldWrites or SystemAccess.WriteWorld in Access",
        category: "SystemAccess",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Deferred facts dispatch at the end of the tick and require deferred or exclusive World write access.",
        helpLinkUri: Guide + "#sysacc008");

    private static readonly DiagnosticDescriptor ExclusiveDefaultAccess = new(
        id: "SYSACC007",
        title: "System runs with exclusive World access",
        messageFormat: "System '{0}' does not declare Access, so it runs exclusively; declare the columns it uses to share a schedule wave",
        category: "SystemAccess",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "ISystemicSystem.Access defaults to SystemAccess.WriteWorld, which excludes other systems from the scheduling wave.",
        helpLinkUri: Guide + "#sysacc007");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [ImmediateWorldAccess, DeferredPopulationAccess, DirectRangeMutation, ColumnAccess, UnsafeRangeMutation,
            ParallelWholeColumnRead, ExclusiveDefaultAccess, DeferredFactAccess];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(startContext =>
        {
            var systemicSystem = GetSystemicType(startContext.Compilation, SystemicSystemMetadataName, SystemicCoreAssembly);
            var systemicSystemContext = GetSystemicType(startContext.Compilation, SystemicSystemContextMetadataName, SystemicCoreAssembly);
            var systemAccess = GetSystemicType(startContext.Compilation, SystemAccessMetadataName, SystemicKernelAssembly);
            var populationColumn = GetSystemicType(startContext.Compilation, PopulationColumnMetadataName, SystemicKernelAssembly);
            var doubleBufferedPopulationColumn = GetSystemicType(
                startContext.Compilation, DoubleBufferedPopulationColumnMetadataName, SystemicKernelAssembly);
            var populationColumnKey = GetSystemicType(startContext.Compilation, PopulationColumnKeyMetadataName, SystemicKernelAssembly);
            var populationAccessScope = GetSystemicType(startContext.Compilation, PopulationAccessScopeMetadataName, SystemicKernelAssembly);
            var worldCommandBuffer = GetSystemicType(startContext.Compilation, WorldCommandBufferMetadataName, SystemicKernelAssembly);
            var immutableArray = startContext.Compilation.GetTypeByMetadataName(ImmutableArrayMetadataName);
            if (immutableArray?.ContainingAssembly.Identity.Name != "System.Collections.Immutable")
                immutableArray = null;

            if (systemicSystem is null || systemicSystemContext is null || systemAccess is null
                || populationColumn is null || doubleBufferedPopulationColumn is null
                || populationColumnKey is null || populationAccessScope is null || worldCommandBuffer is null)
                return;

            var accessProperty = systemicSystem.GetMembers("Access").OfType<IPropertySymbol>().FirstOrDefault();
            if (accessProperty is null)
                return;

            startContext.RegisterSymbolAction(symbolContext => AnalyzeDefaultAccess(
                    symbolContext, systemicSystem, accessProperty),
                SymbolKind.NamedType);
            startContext.RegisterSyntaxNodeAction(syntaxContext => AnalyzeInvocation(
                    syntaxContext, systemicSystem, accessProperty, systemicSystemContext, systemAccess,
                    populationColumn, doubleBufferedPopulationColumn, populationColumnKey, populationAccessScope,
                    worldCommandBuffer, immutableArray),
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.InvocationExpression);
            startContext.RegisterSyntaxNodeAction(syntaxContext => AnalyzeColumnProperty(
                    syntaxContext, systemicSystem, accessProperty, systemAccess,
                    populationColumn, doubleBufferedPopulationColumn, populationColumnKey, immutableArray),
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.SimpleMemberAccessExpression);
        });
    }

    private static void AnalyzeDefaultAccess(
        SymbolAnalysisContext context,
        INamedTypeSymbol systemicSystem,
        IPropertySymbol accessProperty)
    {
        if (context.Symbol is not INamedTypeSymbol type || type.TypeKind != TypeKind.Class
            || !type.AllInterfaces.Any(iface => SymbolEqualityComparer.Default.Equals(iface, systemicSystem)))
            return;

        var implementation = type.FindImplementationForInterfaceMember(accessProperty);
        if (implementation is not null && implementation.ContainingType?.TypeKind != TypeKind.Interface
            && !SymbolEqualityComparer.Default.Equals(implementation, accessProperty))
            return;

        var location = type.Locations.FirstOrDefault(candidate => candidate.IsInSource);
        if (location is not null)
            context.ReportDiagnostic(Diagnostic.Create(ExclusiveDefaultAccess, location, type.Name));
    }

    private static INamedTypeSymbol? GetSystemicType(Compilation compilation, string metadataName, string assemblyName)
    {
        var type = compilation.GetTypeByMetadataName(metadataName);
        return type?.ContainingAssembly.Identity.Name == assemblyName ? type : null;
    }

    private static void AnalyzeInvocation(
        SyntaxNodeAnalysisContext context,
        INamedTypeSymbol systemicSystem,
        IPropertySymbol accessProperty,
        INamedTypeSymbol systemicSystemContext,
        INamedTypeSymbol systemAccess,
        INamedTypeSymbol populationColumn,
        INamedTypeSymbol doubleBufferedPopulationColumn,
        INamedTypeSymbol populationColumnKey,
        INamedTypeSymbol populationAccessScope,
        INamedTypeSymbol worldCommandBuffer,
        INamedTypeSymbol? immutableArray)
    {
        if (context.Node is not InvocationExpressionSyntax invocationSyntax
            || context.SemanticModel.GetOperation(invocationSyntax, context.CancellationToken) is not IInvocationOperation invocation)
            return;

        var method = invocation.TargetMethod;
        if (SymbolEqualityComparer.Default.Equals(method.ContainingType?.OriginalDefinition, systemicSystemContext))
        {
            if (method.Name is "GetWritableWorld" or "SetProperty" or "Publish")
            {
                var access = GetDeclaredAccess(context, systemicSystem, accessProperty, systemAccess,
                    populationColumn, doubleBufferedPopulationColumn, populationColumnKey, immutableArray);
                if (access.IsKnown && !access.HasWriteWorld)
                {
                    context.ReportDiagnostic(Diagnostic.Create(ImmediateWorldAccess, invocationSyntax.GetLocation(), method.Name));
                }
                if (IsInsideForEachRangeCallback(invocationSyntax, context.SemanticModel, systemicSystemContext, context.CancellationToken))
                    context.ReportDiagnostic(Diagnostic.Create(UnsafeRangeMutation, invocationSyntax.GetLocation()));
                return;
            }

            if (method.Name == "DeferPopulationCommands")
            {
                var access = GetDeclaredAccess(context, systemicSystem, accessProperty, systemAccess,
                    populationColumn, doubleBufferedPopulationColumn, populationColumnKey, immutableArray);
                if (access.IsKnown && !access.HasWriteWorld && !access.HasDeferWorldWrites)
                    context.ReportDiagnostic(Diagnostic.Create(DeferredPopulationAccess, invocationSyntax.GetLocation()));
                return;
            }

            if (method.Name == "DeferFact")
            {
                var access = GetDeclaredAccess(context, systemicSystem, accessProperty, systemAccess,
                    populationColumn, doubleBufferedPopulationColumn, populationColumnKey, immutableArray);
                if (access.IsKnown && !access.HasWriteWorld && !access.HasDeferWorldWrites)
                    context.ReportDiagnostic(Diagnostic.Create(DeferredFactAccess, invocationSyntax.GetLocation()));
                if (IsInsideForEachRangeCallback(invocationSyntax, context.SemanticModel, systemicSystemContext, context.CancellationToken))
                    context.ReportDiagnostic(Diagnostic.Create(UnsafeRangeMutation, invocationSyntax.GetLocation()));
                return;
            }
        }

        if (SymbolEqualityComparer.Default.Equals(method.ContainingType?.OriginalDefinition, worldCommandBuffer)
            && IsWorldCommandMutation(method.Name)
            && IsDirectContextCommandsReceiver(invocation, systemicSystemContext)
            && IsInsideForEachRangeCallback(invocationSyntax, context.SemanticModel, systemicSystemContext, context.CancellationToken))
        {
            context.ReportDiagnostic(Diagnostic.Create(UnsafeRangeMutation, invocationSyntax.GetLocation()));
            return;
        }

        if (IsDirectPopulationEdit(method, populationColumn, doubleBufferedPopulationColumn)
            && IsInsideForEachRangeCallback(invocationSyntax, context.SemanticModel, systemicSystemContext, context.CancellationToken))
        {
            context.ReportDiagnostic(Diagnostic.Create(DirectRangeMutation, invocationSyntax.GetLocation(),
                method.Name, GetRangeMutationGuidance(method.Name)));
        }

        if (SymbolEqualityComparer.Default.Equals(method.ContainingType?.OriginalDefinition, populationAccessScope)
            && method.Name == "EditBoth"
            && IsInsideForEachRangeCallback(invocationSyntax, context.SemanticModel, systemicSystemContext, context.CancellationToken))
        {
            context.ReportDiagnostic(Diagnostic.Create(DirectRangeMutation, invocationSyntax.GetLocation(),
                "EditBothBuffers", GetRangeMutationGuidance("EditBothBuffers")));
        }

        if (TryGetScopedColumnAccess(invocation, context.SemanticModel, populationAccessScope, systemAccess,
                populationColumn, doubleBufferedPopulationColumn, populationColumnKey,
                out var scopedColumn, out var scopedWrite, out var partitionRead))
        {
            var declaredAccess = GetDeclaredAccess(context, systemicSystem, accessProperty, systemAccess,
                populationColumn, doubleBufferedPopulationColumn, populationColumnKey, immutableArray);
            ReportMissingColumnAccess(context, invocationSyntax.GetLocation(), scopedColumn, scopedWrite, partitionRead,
                declaredAccess);
            if (invocation.TargetMethod.Name == "ReadAll"
                && (invocation.TargetMethod.Parameters.Length == 1
                    || (invocation.TargetMethod.Parameters.Length == 2
                        && invocation.TargetMethod.TypeArguments.Length == 2
                        && invocation.TargetMethod.ContainingType.TypeArguments.Length == 1
                        && SymbolEqualityComparer.Default.Equals(
                            invocation.TargetMethod.TypeArguments[0],
                            invocation.TargetMethod.ContainingType.TypeArguments[0])))
                && SymbolEqualityComparer.Default.Equals(scopedColumn.Type.OriginalDefinition, populationColumn)
                && IsInsideForEachRangeCallback(invocationSyntax, context.SemanticModel,
                    systemicSystemContext, context.CancellationToken)
                && ContainsColumn(declaredAccess.WriteColumns, scopedColumn))
            {
                context.ReportDiagnostic(Diagnostic.Create(ParallelWholeColumnRead,
                    invocationSyntax.GetLocation(), scopedColumn.Name));
            }
        }
        else if (IsDirectPopulationWrite(method, populationColumn, doubleBufferedPopulationColumn)
            && TryGetDirectColumnProperty(invocationSyntax, context.SemanticModel, populationColumn,
                doubleBufferedPopulationColumn, populationColumnKey, systemAccess, out var directColumn))
        {
            ReportMissingColumnAccess(context, invocationSyntax.GetLocation(), directColumn, isWrite: true, partitionRead: false,
                GetDeclaredAccess(context, systemicSystem, accessProperty, systemAccess,
                    populationColumn, doubleBufferedPopulationColumn, populationColumnKey, immutableArray));
        }
    }

    private static AccessAnalysis GetDeclaredAccess(
        SyntaxNodeAnalysisContext context,
        INamedTypeSymbol systemicSystem,
        IPropertySymbol accessProperty,
        INamedTypeSymbol systemAccess,
        INamedTypeSymbol populationColumn,
        INamedTypeSymbol doubleBufferedPopulationColumn,
        INamedTypeSymbol populationColumnKey,
        INamedTypeSymbol? immutableArray)
    {
        var systemType = context.ContainingSymbol?.ContainingType;
        if (systemType is null || !systemType.AllInterfaces.Any(iface =>
                SymbolEqualityComparer.Default.Equals(iface, systemicSystem)))
            return AccessAnalysis.Unknown;

        var implementation = systemType.FindImplementationForInterfaceMember(accessProperty);
        if (implementation is null || implementation.ContainingType?.TypeKind == TypeKind.Interface
            || SymbolEqualityComparer.Default.Equals(implementation, accessProperty))
        {
            // ISystemicSystem.Access defaults to the exclusive WriteWorld declaration.
            return AccessAnalysis.Default;
        }

        if (implementation is not IPropertySymbol property)
            return AccessAnalysis.Unknown;

        var declaration = property.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken);
        var expression = GetAccessExpression(declaration);
        if (expression is null)
            return AccessAnalysis.Unknown;

        var expressionSemanticModel = ReferenceEquals(expression.SyntaxTree, context.SemanticModel.SyntaxTree)
            ? context.SemanticModel
            : GetSemanticModelForOtherTree(context.Compilation, expression.SyntaxTree);
        if (!TryGetDirectAccessItems(expression, expressionSemanticModel, context.Compilation,
                context.CancellationToken, systemAccess, immutableArray, out var items, out var semanticModel))
            return AccessAnalysis.Unknown;

        var hasWriteWorld = false;
        var hasDeferredWrite = false;
        var readColumns = ImmutableArray.CreateBuilder<IPropertySymbol>();
        var writeColumns = ImmutableArray.CreateBuilder<IPropertySymbol>();
        foreach (var item in items)
        {
            var symbol = semanticModel.GetSymbolInfo(item, context.CancellationToken).Symbol;
            if (symbol is IPropertySymbol propertySymbol
                && SymbolEqualityComparer.Default.Equals(propertySymbol.ContainingType?.OriginalDefinition, systemAccess))
            {
                switch (propertySymbol.Name)
                {
                    case "ReadWorld":
                        break;
                    case "WriteWorld":
                        hasWriteWorld = true;
                        break;
                    case "DeferWorldWrites":
                        hasDeferredWrite = true;
                        break;
                    default:
                        return AccessAnalysis.Unknown;
                }
            }
            else if (symbol is IMethodSymbol methodSymbol
                     && SymbolEqualityComparer.Default.Equals(methodSymbol.ContainingType?.OriginalDefinition, systemAccess)
                     && methodSymbol.Name is "ReadColumn" or "WriteColumn")
            {
                if (TryGetGeneratedGenericColumnAccess(item, semanticModel,
                        methodSymbol, systemAccess, populationColumn, doubleBufferedPopulationColumn,
                        populationColumnKey, out var generatedColumn, out var generatedWrite))
                {
                    (generatedWrite ? writeColumns : readColumns).Add(generatedColumn);
                }
                // An unresolved generic key does not authorize any generated column. Callers can
                // use the generated Read<Name>/Write<Name> property when the key is not static.
            }
            else if (symbol is IMethodSymbol resourceFactory
                     && SymbolEqualityComparer.Default.Equals(resourceFactory.ContainingType?.OriginalDefinition, systemAccess)
                     && resourceFactory.Name is "ReadResource" or "WriteResource")
            {
                var resourceExpression = item;
                while (resourceExpression is ParenthesizedExpressionSyntax parenthesized)
                    resourceExpression = parenthesized.Expression;
                if (resourceExpression is not InvocationExpressionSyntax resourceInvocation
                    || resourceInvocation.ArgumentList.Arguments.Count != 1)
                    return AccessAnalysis.Unknown;
                var resourceName = semanticModel.GetConstantValue(
                    resourceInvocation.ArgumentList.Arguments[0].Expression, context.CancellationToken);
                if (!resourceName.HasValue || resourceName.Value is not string resource)
                    return AccessAnalysis.Unknown;
                if (resourceFactory.Name == "WriteResource" && resource == "$world")
                    hasWriteWorld = true;
                // An unrelated custom resource does not make generated-column permissions unknown.
            }
            else if (symbol is IPropertySymbol generatedAccess
                     && TryGetGeneratedAccessColumn(generatedAccess, systemAccess, populationColumn,
                         doubleBufferedPopulationColumn, populationColumnKey, out var generatedColumn, out var generatedWrite))
            {
                (generatedWrite ? writeColumns : readColumns).Add(generatedColumn);
            }
            else
            {
                return AccessAnalysis.Unknown;
            }
        }

        return new AccessAnalysis(isKnown: true, hasWriteWorld, hasDeferredWrite,
            readColumns.ToImmutable(), writeColumns.ToImmutable());
    }

    private static bool TryGetGeneratedAccessColumn(
        IPropertySymbol accessProperty,
        INamedTypeSymbol systemAccess,
        INamedTypeSymbol populationColumn,
        INamedTypeSymbol doubleBufferedPopulationColumn,
        INamedTypeSymbol populationColumnKey,
        out IPropertySymbol columnProperty,
        out bool isWrite)
    {
        columnProperty = null!;
        isWrite = false;
        if (accessProperty.IsStatic == false
            || !SymbolEqualityComparer.Default.Equals(accessProperty.Type, systemAccess))
            return false;

        var prefix = accessProperty.Name.StartsWith("Read", StringComparison.Ordinal) ? "Read"
            : accessProperty.Name.StartsWith("Write", StringComparison.Ordinal) ? "Write"
            : null;
        if (prefix is null || accessProperty.Name.Length == prefix.Length)
            return false;

        var columnsType = accessProperty.ContainingType.GetTypeMembers("Columns").FirstOrDefault();
        if (columnsType is null)
            return false;
        var fieldName = accessProperty.Name.Substring(prefix.Length);
        var candidate = columnsType.GetMembers(fieldName).OfType<IPropertySymbol>().FirstOrDefault();
        if (candidate is null || !IsGeneratedColumnProperty(candidate, systemAccess, populationColumn,
                doubleBufferedPopulationColumn, populationColumnKey))
            return false;

        columnProperty = candidate;
        isWrite = prefix == "Write";
        return true;
    }

    private static bool TryGetGeneratedGenericColumnAccess(
        ExpressionSyntax accessExpression,
        SemanticModel semanticModel,
        IMethodSymbol accessMethod,
        INamedTypeSymbol systemAccess,
        INamedTypeSymbol populationColumn,
        INamedTypeSymbol doubleBufferedPopulationColumn,
        INamedTypeSymbol populationColumnKey,
        out IPropertySymbol columnProperty,
        out bool isWrite)
    {
        columnProperty = null!;
        isWrite = false;
        while (accessExpression is ParenthesizedExpressionSyntax parenthesized)
            accessExpression = parenthesized.Expression;
        if (accessExpression is not InvocationExpressionSyntax invocation
            || invocation.ArgumentList.Arguments.Count != 1
            || GetSymbol(semanticModel, invocation.ArgumentList.Arguments[0].Expression) is not IPropertySymbol keyProperty
            || !keyProperty.IsStatic
            || !keyProperty.Name.EndsWith("Key", StringComparison.Ordinal)
            || keyProperty.Name.Length <= "Key".Length
            || keyProperty.Type is not INamedTypeSymbol keyType
            || !SymbolEqualityComparer.Default.Equals(keyType.OriginalDefinition, populationColumnKey)
            || keyType.TypeArguments.Length != 1
            || accessMethod.TypeArguments.Length != 2
            || !SymbolEqualityComparer.Default.Equals(keyType.TypeArguments[0], accessMethod.TypeArguments[1]))
            return false;

        var companion = keyProperty.ContainingType;
        var defineMethod = companion.GetMembers("Define").OfType<IMethodSymbol>()
            .FirstOrDefault(method => method.IsStatic && method.ReturnType is INamedTypeSymbol);
        if (defineMethod?.ReturnType is not INamedTypeSymbol populationType
            || populationType.TypeArguments.Length != 1
            || !SymbolEqualityComparer.Default.Equals(populationType.TypeArguments[0], accessMethod.TypeArguments[0]))
            return false;

        var columnsType = companion.GetTypeMembers("Columns").FirstOrDefault();
        var generatedColumn = columnsType?.GetMembers(keyProperty.Name.Substring(0, keyProperty.Name.Length - "Key".Length))
            .OfType<IPropertySymbol>().FirstOrDefault();
        if (generatedColumn is null || !IsGeneratedColumnProperty(generatedColumn, systemAccess,
                populationColumn, doubleBufferedPopulationColumn, populationColumnKey))
            return false;

        columnProperty = generatedColumn;
        isWrite = accessMethod.Name == "WriteColumn";
        return true;
    }

    private static bool IsGeneratedColumnProperty(
        IPropertySymbol columnProperty,
        INamedTypeSymbol systemAccess,
        INamedTypeSymbol populationColumn,
        INamedTypeSymbol doubleBufferedPopulationColumn,
        INamedTypeSymbol populationColumnKey)
    {
        var columnsType = columnProperty.ContainingType;
        var companion = columnsType.ContainingType;
        if (columnsType.Name != "Columns" || companion is null || !companion.IsStatic
            || !companion.GetTypeMembers("Columns").Any(type => SymbolEqualityComparer.Default.Equals(type, columnsType)))
            return false;

        var bindExists = companion.GetMembers("Bind").OfType<IMethodSymbol>().Any(method => method.IsStatic
            && SymbolEqualityComparer.Default.Equals(method.ReturnType, columnsType));
        if (!bindExists)
            return false;

        if (columnProperty.Type is not INamedTypeSymbol columnType
            || !(SymbolEqualityComparer.Default.Equals(columnType.OriginalDefinition, populationColumn)
                || SymbolEqualityComparer.Default.Equals(columnType.OriginalDefinition, doubleBufferedPopulationColumn))
            || columnType.TypeArguments.Length != 1)
            return false;

        var keyProperty = companion.GetMembers(columnProperty.Name + "Key").OfType<IPropertySymbol>().FirstOrDefault();
        if (keyProperty?.Type is not INamedTypeSymbol keyType
            || !SymbolEqualityComparer.Default.Equals(keyType.OriginalDefinition, populationColumnKey)
            || keyType.TypeArguments.Length != 1
            || !SymbolEqualityComparer.Default.Equals(keyType.TypeArguments[0], columnType.TypeArguments[0]))
            return false;

        var readAccess = companion.GetMembers("Read" + columnProperty.Name).OfType<IPropertySymbol>().FirstOrDefault();
        var writeAccess = companion.GetMembers("Write" + columnProperty.Name).OfType<IPropertySymbol>().FirstOrDefault();
        return readAccess is { IsStatic: true } && writeAccess is { IsStatic: true }
            && SymbolEqualityComparer.Default.Equals(readAccess.Type, systemAccess)
            && SymbolEqualityComparer.Default.Equals(writeAccess.Type, systemAccess);
    }

    private static bool TryGetDirectColumnProperty(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        INamedTypeSymbol populationColumn,
        INamedTypeSymbol doubleBufferedPopulationColumn,
        INamedTypeSymbol populationColumnKey,
        INamedTypeSymbol systemAccess,
        out IPropertySymbol columnProperty)
    {
        columnProperty = null!;
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess
            || GetSymbol(semanticModel, memberAccess.Expression) is not IPropertySymbol property
            || !IsGeneratedColumnProperty(property, systemAccess, populationColumn,
                doubleBufferedPopulationColumn, populationColumnKey))
            return false;

        columnProperty = property;
        return true;
    }

    private static bool TryGetScopedColumnAccess(
        IInvocationOperation invocation,
        SemanticModel semanticModel,
        INamedTypeSymbol populationAccessScope,
        INamedTypeSymbol systemAccess,
        INamedTypeSymbol populationColumn,
        INamedTypeSymbol doubleBufferedPopulationColumn,
        INamedTypeSymbol populationColumnKey,
        out IPropertySymbol columnProperty,
        out bool isWrite,
        out bool partitionRead)
    {
        columnProperty = null!;
        isWrite = false;
        partitionRead = false;
        var method = invocation.TargetMethod;
        if (!SymbolEqualityComparer.Default.Equals(method.ContainingType?.OriginalDefinition, populationAccessScope)
            || method.Name is not ("ReadAll" or "ReadRange" or "ReadCurrent" or "ReadCurrentRange"
                or "EditRange" or "EditNext" or "EditBoth")
            || invocation.Arguments.Length == 0)
            return false;

        if (invocation.Arguments[invocation.Arguments.Length - 1].Value.Syntax is not ExpressionSyntax columnExpression || GetSymbol(semanticModel, columnExpression) is not IPropertySymbol property
            || !IsGeneratedColumnProperty(property, systemAccess, populationColumn,
                doubleBufferedPopulationColumn, populationColumnKey))
            return false;

        isWrite = method.Name is "EditRange" or "EditNext" or "EditBoth";
        partitionRead = (method.Name is "ReadRange" or "ReadCurrent" or "ReadCurrentRange")
            && !(method.Name == "ReadCurrent" && method.Parameters.Length > 1);
        if (method.Name == "ReadAll" && method.Parameters.Length > 1)
            partitionRead = false;
        columnProperty = property;
        return true;
    }

    private static ISymbol? GetSymbol(SemanticModel semanticModel, ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
            expression = parenthesized.Expression;
        return semanticModel.GetSymbolInfo(expression).Symbol;
    }

    private static void AnalyzeColumnProperty(
        SyntaxNodeAnalysisContext context,
        INamedTypeSymbol systemicSystem,
        IPropertySymbol accessProperty,
        INamedTypeSymbol systemAccess,
        INamedTypeSymbol populationColumn,
        INamedTypeSymbol doubleBufferedPopulationColumn,
        INamedTypeSymbol populationColumnKey,
        INamedTypeSymbol? immutableArray)
    {
        if (context.Node is not MemberAccessExpressionSyntax memberAccess
            || context.SemanticModel.GetSymbolInfo(memberAccess, context.CancellationToken).Symbol is not IPropertySymbol columnView
            || !IsReadOnlyColumnView(columnView, populationColumn, doubleBufferedPopulationColumn)
            || GetSymbol(context.SemanticModel, memberAccess.Expression) is not IPropertySymbol columnProperty
            || !IsGeneratedColumnProperty(columnProperty, systemAccess, populationColumn,
                doubleBufferedPopulationColumn, populationColumnKey))
            return;

        var access = GetDeclaredAccess(context, systemicSystem, accessProperty, systemAccess,
            populationColumn, doubleBufferedPopulationColumn, populationColumnKey, immutableArray);
        ReportMissingColumnAccess(context, memberAccess.GetLocation(), columnProperty,
            isWrite: false, partitionRead: false, access);
    }

    private static bool IsReadOnlyColumnView(
        IPropertySymbol property,
        INamedTypeSymbol populationColumn,
        INamedTypeSymbol doubleBufferedPopulationColumn)
    {
        var owner = property.ContainingType?.OriginalDefinition;
        return (SymbolEqualityComparer.Default.Equals(owner, populationColumn)
                && property.Name is "ReadOnlySpan" or "ReadOnlyMemory")
            || (SymbolEqualityComparer.Default.Equals(owner, doubleBufferedPopulationColumn)
                && property.Name is "Current" or "CurrentMemory");
    }

    private static void ReportMissingColumnAccess(
        SyntaxNodeAnalysisContext context,
        Location location,
        IPropertySymbol columnProperty,
        bool isWrite,
        bool partitionRead,
        AccessAnalysis access)
    {
        if (!access.IsKnown)
            return;

        var allowed = isWrite
            ? ContainsColumn(access.WriteColumns, columnProperty)
            : ContainsColumn(access.ReadColumns, columnProperty)
                || (partitionRead && ContainsColumn(access.WriteColumns, columnProperty));
        if (allowed)
            return;

        var companionName = columnProperty.ContainingType.ContainingType.Name;
        var required = isWrite
            ? companionName + ".Write" + columnProperty.Name
            : partitionRead
                ? companionName + ".Read" + columnProperty.Name + " or " + companionName + ".Write" + columnProperty.Name
                : companionName + ".Read" + columnProperty.Name;
        context.ReportDiagnostic(Diagnostic.Create(ColumnAccess, location,
            columnProperty.Name, isWrite ? "write" : "read", required));
    }

    private static bool ContainsColumn(ImmutableArray<IPropertySymbol> columns, IPropertySymbol column) =>
        columns.Any(candidate => SymbolEqualityComparer.Default.Equals(candidate, column));

    private static SemanticModel GetSemanticModelForOtherTree(Compilation compilation, SyntaxTree syntaxTree)
    {
        // Access may be inherited from another partial/source file; inspect that direct declaration
        // while preserving symbol identity for SystemAccess values.
#pragma warning disable RS1030
        return compilation.GetSemanticModel(syntaxTree);
#pragma warning restore RS1030
    }

    private static SyntaxNode? GetAccessExpression(SyntaxNode? declaration)
    {
        if (declaration is not PropertyDeclarationSyntax property)
            return null;
        if (property.ExpressionBody is { } expressionBody)
            return expressionBody.Expression;
        if (property.Initializer is { } initializer)
            return initializer.Value;
        if (property.AccessorList is not { } accessors)
            return null;

        var getter = accessors.Accessors.FirstOrDefault(accessor => accessor.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.GetAccessorDeclaration));
        if (getter?.ExpressionBody is { } getterExpressionBody)
            return getterExpressionBody.Expression;
        if (getter?.Body is { Statements.Count: 1 } body
            && body.Statements[0] is ReturnStatementSyntax { Expression: { } returnExpression })
            return returnExpression;
        return null;
    }

    private static bool TryGetDirectAccessItems(
        SyntaxNode expressionNode,
        SemanticModel expressionSemanticModel,
        Compilation compilation,
        CancellationToken cancellationToken,
        INamedTypeSymbol systemAccess,
        INamedTypeSymbol? immutableArray,
        out ImmutableArray<ExpressionSyntax> items,
        out SemanticModel itemSemanticModel)
    {
        itemSemanticModel = expressionSemanticModel;
        if (expressionNode is not ExpressionSyntax expression)
        {
            items = default;
            return false;
        }

        if (TryGetDirectCollectionItems(expression, out items))
            return true;

        while (expression is ParenthesizedExpressionSyntax parenthesized)
            expression = parenthesized.Expression;

        if (expressionSemanticModel.GetSymbolInfo(expression, cancellationToken).Symbol is not IFieldSymbol field
            || field.DeclaredAccessibility != Accessibility.Private
            || !field.IsStatic
            || !field.IsReadOnly
            || !IsImmutableSystemAccessArray(field.Type, systemAccess, immutableArray))
        {
            items = default;
            return false;
        }

        if (field.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken)
            is not VariableDeclaratorSyntax { Initializer.Value: { } fieldInitializer }
            || !TryGetDirectCollectionItems(fieldInitializer, out items))
        {
            items = default;
            return false;
        }

        itemSemanticModel = ReferenceEquals(fieldInitializer.SyntaxTree, expressionSemanticModel.SyntaxTree)
            ? expressionSemanticModel
            : GetSemanticModelForOtherTree(compilation, fieldInitializer.SyntaxTree);
        return true;
    }

    private static bool IsImmutableSystemAccessArray(
        ITypeSymbol fieldType,
        INamedTypeSymbol systemAccess,
        INamedTypeSymbol? immutableArray)
    {
        if (immutableArray is null || fieldType is not INamedTypeSymbol namedType
            || !SymbolEqualityComparer.Default.Equals(namedType.OriginalDefinition, immutableArray)
            || namedType.TypeArguments.Length != 1)
            return false;
        return SymbolEqualityComparer.Default.Equals(namedType.TypeArguments[0], systemAccess);
    }

    private static bool TryGetDirectCollectionItems(ExpressionSyntax expression, out ImmutableArray<ExpressionSyntax> items)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
            expression = parenthesized.Expression;

        switch (expression)
        {
            case CollectionExpressionSyntax collection:
                {
                    var builder = ImmutableArray.CreateBuilder<ExpressionSyntax>();
                    foreach (var element in collection.Elements)
                    {
                        if (element is not ExpressionElementSyntax expressionElement)
                        {
                            items = default;
                            return false;
                        }
                        builder.Add(expressionElement.Expression);
                    }
                    items = builder.ToImmutable();
                    return true;
                }
            case ArrayCreationExpressionSyntax { Initializer: { } initializer }:
                items = [.. initializer.Expressions];
                return true;
            case ImplicitArrayCreationExpressionSyntax { Initializer: { } initializer }:
                items = [.. initializer.Expressions];
                return true;
            case InitializerExpressionSyntax initializer when initializer.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.ArrayInitializerExpression):
                items = [.. initializer.Expressions];
                return true;
            default:
                items = default;
                return false;
        }
    }

    private static bool IsDirectPopulationEdit(
        IMethodSymbol method,
        INamedTypeSymbol populationColumn,
        INamedTypeSymbol doubleBufferedPopulationColumn)
    {
        var declaringType = method.ContainingType?.OriginalDefinition;
        if (SymbolEqualityComparer.Default.Equals(declaringType, populationColumn))
            return method.Name is "EditRange" or "EditNext" or "EditMemory";
        return SymbolEqualityComparer.Default.Equals(declaringType, doubleBufferedPopulationColumn)
            && method.Name is "EditNext" or "EditNextMemory" or "EditBothBuffers" or "Swap";
    }

    private static string GetRangeMutationGuidance(string methodName) => methodName switch
    {
        "EditBothBuffers" => "Initialize both buffers outside the callback; use generated Columns.WriteRange(ref scope).Write(...) for bounded row writes",
        "Swap" => "Move buffer swaps outside the callback; generated Columns.WriteRange(ref scope).Write(...) writes only the assigned rows",
        _ => "Use generated Columns.WriteRange(ref scope).Write(...) to write only the worker's assigned range",
    };

    private static bool IsInsideForEachRangeCallback(
        SyntaxNode invocation,
        SemanticModel semanticModel,
        INamedTypeSymbol systemicSystemContext,
        CancellationToken cancellationToken)
    {
        var callback = invocation.Ancestors().FirstOrDefault(node =>
            node is LambdaExpressionSyntax or AnonymousMethodExpressionSyntax);
        if (callback is null)
            return false;

        var argument = callback.Ancestors().OfType<ArgumentSyntax>().FirstOrDefault();
        if (argument?.Parent?.Parent is not InvocationExpressionSyntax forEachInvocation)
            return false;

        var callbackExpression = argument.Expression;
        while (callbackExpression is ParenthesizedExpressionSyntax parenthesized)
            callbackExpression = parenthesized.Expression;
        if (!ReferenceEquals(callbackExpression, callback))
            return false;

        if (semanticModel.GetSymbolInfo(forEachInvocation, cancellationToken).Symbol is not IMethodSymbol method
            || !SymbolEqualityComparer.Default.Equals(method.ContainingType?.OriginalDefinition, systemicSystemContext)
            || method.Name is not ("ForEachRange" or "ForEachPopulationRange"))
            return false;

        return semanticModel.GetOperation(argument, cancellationToken) is IArgumentOperation { Parameter.Ordinal: 2 };
    }

    private static bool IsDirectPopulationWrite(
        IMethodSymbol method,
        INamedTypeSymbol populationColumn,
        INamedTypeSymbol doubleBufferedPopulationColumn)
    {
        var declaringType = method.ContainingType?.OriginalDefinition;
        if (SymbolEqualityComparer.Default.Equals(declaringType, populationColumn))
            return method.Name is "EditRange" or "EditNext" or "EditMemory";
        return SymbolEqualityComparer.Default.Equals(declaringType, doubleBufferedPopulationColumn)
            && method.Name is "EditNext" or "EditNextMemory" or "EditBothBuffers" or "Swap";
    }

    private static bool IsDirectContextCommandsReceiver(
        IInvocationOperation invocation,
        INamedTypeSymbol systemicSystemContext) =>
        invocation.Instance is IPropertyReferenceOperation { Property.Name: "Commands" } commands
        && SymbolEqualityComparer.Default.Equals(commands.Property.ContainingType?.OriginalDefinition, systemicSystemContext);

    private static bool IsWorldCommandMutation(string methodName) => methodName is
        "Reserve" or "SetProperty" or "Add" or "Remove" or "AddTag" or "RemoveTag"
        or "AddCapability" or "RemoveCapability" or "SetLink" or "RemoveLink"
        or "SetModifier" or "RemoveModifier" or "Grant" or "Revoke"
        or "SetSpatialBounds" or "RemoveSpatialBounds" or "Clear" or "Flush";

    private readonly struct AccessAnalysis(
        bool isKnown,
        bool hasWriteWorld,
        bool hasDeferWorldWrites,
        ImmutableArray<IPropertySymbol> readColumns,
        ImmutableArray<IPropertySymbol> writeColumns)
    {
        public static AccessAnalysis Unknown { get; } = new(false, false, false,
            [], []);
        public static AccessAnalysis Default { get; } = new(true, true, false,
            [], []);
        public bool IsKnown { get; } = isKnown;
        public bool HasWriteWorld { get; } = hasWriteWorld;
        public bool HasDeferWorldWrites { get; } = hasDeferWorldWrites;
        public ImmutableArray<IPropertySymbol> ReadColumns { get; } = readColumns;
        public ImmutableArray<IPropertySymbol> WriteColumns { get; } = writeColumns;
    }
}
