using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Systemic.Kernel.Generator;

/// <summary>
/// Reports recognizable nondeterminism in gameplay-system implementations, code marked
/// <c>[StrictSimulation]</c>, or files configured with <c>systemic_determinism = strict</c>.
/// A file configured with <c>systemic_determinism = relaxed</c> disables the gameplay-system
/// default, while an enclosing <c>[CosmeticOnly]</c> excludes cosmetic code. This static analysis
/// is a guardrail, not a proof of determinism.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StrictDeterminismAnalyzer : DiagnosticAnalyzer
{
    private const string Category = "Determinism";
    private const string StrictAttribute = "Systemic.Kernel.StrictSimulationAttribute";
    private const string CosmeticAttribute = "Systemic.Kernel.CosmeticOnlyAttribute";
    private const string SystemicSystemInterface = "ISystemicSystem";
    private const string ScheduledKernelSystemInterface = "IScheduledKernelSystem";
    private const string SystemicHostAssembly = "Systemic.Core";
    private const string SystemicKernelAssembly = "Systemic.Kernel";
    private const string Guide = "https://github.com/cakegod/systemic-framework/blob/main/docs/api/analyzers.md";

    private static readonly DiagnosticDescriptor Floating = new(
        "SYSDET001", "Floating-point simulation", "Strict simulation uses floating-point {0}; use deterministic numeric primitives",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: Guide + "#sysdet001");
    private static readonly DiagnosticDescriptor ExternalState = new(
        "SYSDET002", "Nondeterministic external state", "Strict simulation uses nondeterministic {0}; use an explicit deterministic input",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: Guide + "#sysdet002");
    private static readonly DiagnosticDescriptor Ordering = new(
        "SYSDET003", "Uncontrolled execution order", "Strict simulation uses {0}; establish a deterministic execution or iteration order",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: Guide + "#sysdet003");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Floating, ExternalState, Ordering];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeTypeSyntax, SyntaxKind.PredefinedType, SyntaxKind.IdentifierName,
            SyntaxKind.QualifiedName, SyntaxKind.GenericName, SyntaxKind.AliasQualifiedName);
        context.RegisterSyntaxNodeAction(AnalyzeIteration, SyntaxKind.ForEachStatement, SyntaxKind.ForEachVariableStatement);
        context.RegisterOperationAction(AnalyzeOperation, OperationKind.Invocation, OperationKind.ObjectCreation,
            OperationKind.PropertyReference, OperationKind.FieldReference, OperationKind.Literal,
            OperationKind.BinaryOperator, OperationKind.UnaryOperator);
    }

    private static void AnalyzeTypeSyntax(SyntaxNodeAnalysisContext context)
    {
        if (context.Node is not TypeSyntax typeSyntax || IsNestedName(typeSyntax)
            || !IsStrict(context.ContainingSymbol, context.Node.SyntaxTree, context.Options, context.Compilation)) return;

        ITypeSymbol? type;
        if (typeSyntax is PredefinedTypeSyntax || typeSyntax is IdentifierNameSyntax { IsVar: true })
        {
            type = context.SemanticModel.GetTypeInfo(typeSyntax, context.CancellationToken).Type;
        }
        else
        {
            var symbol = context.SemanticModel.GetSymbolInfo(typeSyntax, context.CancellationToken).Symbol;
            type = symbol switch
            {
                ITypeSymbol namedType => namedType,
                IAliasSymbol { Target: ITypeSymbol aliasedType } => aliasedType,
                _ => null
            };
        }

        if (IsFloating(type))
            context.ReportDiagnostic(Diagnostic.Create(Floating, typeSyntax.GetLocation(), "type"));
    }

    private static bool IsNestedName(TypeSyntax node) =>
        node.Parent is QualifiedNameSyntax || node.Parent is AliasQualifiedNameSyntax;

    private static void AnalyzeIteration(SyntaxNodeAnalysisContext context)
    {
        if (!IsStrict(context.ContainingSymbol, context.Node.SyntaxTree, context.Options, context.Compilation)) return;
        var expression = context.Node switch
        {
            ForEachStatementSyntax statement => statement.Expression,
            ForEachVariableStatementSyntax statement => statement.Expression,
            _ => null
        };
        if (expression is null) return;
        if (IsUnorderedIterationSource(expression, context.SemanticModel, context.CancellationToken, context.Node.SpanStart, context.Node))
            context.ReportDiagnostic(Diagnostic.Create(Ordering, expression.GetLocation(), "unordered collection iteration"));
    }

    private static bool IsUnorderedIterationSource(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        CancellationToken cancellationToken,
        int usePosition,
        SyntaxNode iterationNode,
        HashSet<ISymbol>? visitedLocals = null)
    {
        var type = semanticModel.GetTypeInfo(expression, cancellationToken).Type as INamedTypeSymbol;
        if (IsUnorderedCollection(type)) return true;

        if (expression is IdentifierNameSyntax
            && semanticModel.GetSymbolInfo(expression, cancellationToken).Symbol is ILocalSymbol local)
        {
            visitedLocals ??= new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            if (visitedLocals.Add(local))
            {
                var priorAssignments = expression.SyntaxTree.GetRoot(cancellationToken)
                    .DescendantNodes().OfType<AssignmentExpressionSyntax>()
                    .Where(assignment => assignment.SpanStart < usePosition
                        && SymbolEqualityComparer.Default.Equals(
                            semanticModel.GetSymbolInfo(assignment.Left, cancellationToken).Symbol, local))
                    .ToArray();
                var lastUnconditionalAssignment = priorAssignments.LastOrDefault(assignment =>
                    IsUnconditionalAssignmentBeforeUse(assignment, iterationNode));

                if (lastUnconditionalAssignment is null
                    && local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken) is VariableDeclaratorSyntax { Initializer.Value: { } initializer }
                    && IsUnorderedIterationSource(initializer, semanticModel, cancellationToken, usePosition, iterationNode, visitedLocals))
                    return true;

                foreach (var assignment in priorAssignments)
                {
                    if (lastUnconditionalAssignment is not null
                        && assignment.SpanStart < lastUnconditionalAssignment.SpanStart)
                        continue;
                    if (IsUnorderedIterationSource(assignment.Right, semanticModel, cancellationToken, usePosition, iterationNode, visitedLocals))
                        return true;
                }
            }
        }

        if (expression is QueryExpressionSyntax query)
        {
            return IsUnorderedIterationSource(query.FromClause.Expression, semanticModel, cancellationToken, usePosition, iterationNode, visitedLocals)
                || HasUnorderedQuerySources(query.Body, semanticModel, cancellationToken, usePosition, iterationNode, visitedLocals);
        }

        if (expression is not InvocationExpressionSyntax invocation
            || semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method
            || !method.IsExtensionMethod
            || method.ContainingNamespace.ToDisplayString() != "System.Linq")
            return false;

        var isReducedExtensionCall = method.ReducedFrom is not null;
        var source = isReducedExtensionCall && invocation.Expression is MemberAccessExpressionSyntax memberAccess
            ? memberAccess.Expression
            : invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression;
        if (source is not null
            && IsUnorderedIterationSource(source, semanticModel, cancellationToken, usePosition, iterationNode, visitedLocals))
            return !IsStableIdentityOrdering(invocation, method, source, semanticModel, cancellationToken,
                isReducedExtensionCall, usePosition);

        if (!IsMultiSourceLinqOperator(method.Name)) return false;
        var secondarySourceStart = isReducedExtensionCall ? 0 : 1;
        return invocation.ArgumentList.Arguments.Skip(secondarySourceStart)
            .Any(argument => IsUnorderedIterationSource(argument.Expression, semanticModel, cancellationToken, usePosition, iterationNode, visitedLocals));
    }

    private static bool IsStableIdentityOrdering(
        InvocationExpressionSyntax invocation,
        IMethodSymbol method,
        ExpressionSyntax source,
        SemanticModel semanticModel,
        CancellationToken cancellationToken,
        bool isReducedExtensionCall,
        int usePosition)
    {
        if (method.Name is not ("OrderBy" or "OrderByDescending")) return false;
        var selectorIndex = isReducedExtensionCall ? 0 : 1;
        if (invocation.ArgumentList.Arguments.Count != selectorIndex + 1
            || invocation.ArgumentList.Arguments[selectorIndex].Expression is not LambdaExpressionSyntax lambda)
            return false;

        var parameter = lambda switch
        {
            SimpleLambdaExpressionSyntax simple => simple.Parameter,
            ParenthesizedLambdaExpressionSyntax { ParameterList.Parameters.Count: 1 } parenthesized =>
                parenthesized.ParameterList.Parameters[0],
            _ => null
        };
        if (parameter is null || lambda.Body is not ExpressionSyntax body
            || semanticModel.GetDeclaredSymbol(parameter, cancellationToken) is not IParameterSymbol parameterSymbol)
            return false;
        while (body is ParenthesizedExpressionSyntax parenthesizedBody)
            body = parenthesizedBody.Expression;
        if (!SymbolEqualityComparer.Default.Equals(
                semanticModel.GetSymbolInfo(body, cancellationToken).Symbol, parameterSymbol))
            return false;

        var sourceType = semanticModel.GetTypeInfo(source, cancellationToken).Type as INamedTypeSymbol;
        if (sourceType is null || sourceType.TypeArguments.Length != 1
            || method.TypeArguments.Length != 2
            || !SymbolEqualityComparer.Default.Equals(method.TypeArguments[0], sourceType.TypeArguments[0])
            || !SymbolEqualityComparer.Default.Equals(method.TypeArguments[1], sourceType.TypeArguments[0]))
            return false;

        var definition = sourceType.OriginalDefinition;
        if (definition.ContainingNamespace.ToDisplayString() != "System.Collections.Generic"
            || definition.MetadataName != "HashSet`1"
            || !IsDefaultHashSetSource(source, semanticModel, cancellationToken, usePosition))
            return false;

        var elementType = sourceType.TypeArguments[0];
        return elementType.TypeKind == TypeKind.Enum || elementType.SpecialType is
            SpecialType.System_Boolean or SpecialType.System_Byte or SpecialType.System_SByte
            or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Int32
            or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64
            or SpecialType.System_Char or SpecialType.System_Decimal;
    }

    private static bool IsDefaultHashSetSource(
        ExpressionSyntax source,
        SemanticModel semanticModel,
        CancellationToken cancellationToken,
        int usePosition)
    {
        while (source is ParenthesizedExpressionSyntax parenthesized)
            source = parenthesized.Expression;

        if (IsDefaultHashSetConstruction(source, semanticModel, cancellationToken))
            return true;

        if (source is not IdentifierNameSyntax
            || semanticModel.GetSymbolInfo(source, cancellationToken).Symbol is not ILocalSymbol local)
            return false;

        var root = source.SyntaxTree.GetRoot(cancellationToken);
        if (root.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(assignment =>
                assignment.SpanStart < usePosition
                && SymbolEqualityComparer.Default.Equals(
                    semanticModel.GetSymbolInfo(assignment.Left, cancellationToken).Symbol, local)))
            return false;

        return local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken)
                is VariableDeclaratorSyntax { Initializer.Value: { } initializer }
            && IsDefaultHashSetConstruction(initializer, semanticModel, cancellationToken);
    }

    private static bool IsDefaultHashSetConstruction(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
            expression = parenthesized.Expression;

        return expression switch
        {
            ObjectCreationExpressionSyntax creation =>
                creation.ArgumentList?.Arguments.Count is null or 0
                && IsHashSet(semanticModel.GetTypeInfo(creation, cancellationToken).Type),
            ImplicitObjectCreationExpressionSyntax creation =>
                creation.ArgumentList.Arguments.Count == 0
                && IsHashSet(semanticModel.GetTypeInfo(creation, cancellationToken).Type),
            _ => false
        };
    }

    private static bool IsHashSet(ITypeSymbol? type) => type is INamedTypeSymbol named
        && named.OriginalDefinition.MetadataName == "HashSet`1"
        && named.OriginalDefinition.ContainingNamespace.ToDisplayString() == "System.Collections.Generic";

    private static bool IsUnconditionalAssignmentBeforeUse(AssignmentExpressionSyntax assignment, SyntaxNode iterationNode) =>
        assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
        && assignment.Parent is ExpressionStatementSyntax { Parent: BlockSyntax block }
        && iterationNode.AncestorsAndSelf().Contains(block);

    private static bool IsMultiSourceLinqOperator(string methodName) =>
        methodName is "Concat" or "Except" or "ExceptBy" or "GroupJoin" or "Intersect" or "IntersectBy"
            or "Join" or "Union" or "UnionBy" or "Zip";

    private static bool HasUnorderedQuerySources(
        QueryBodySyntax body,
        SemanticModel semanticModel,
        CancellationToken cancellationToken,
        int usePosition,
        SyntaxNode iterationNode,
        HashSet<ISymbol>? visitedLocals)
    {
        foreach (var clause in body.Clauses)
        {
            var source = clause switch
            {
                FromClauseSyntax additionalFrom => additionalFrom.Expression,
                JoinClauseSyntax join => join.InExpression,
                _ => null
            };
            if (source is not null
                && IsUnorderedIterationSource(source, semanticModel, cancellationToken, usePosition, iterationNode, visitedLocals))
                return true;
        }

        return body.Continuation is { } continuation
            && HasUnorderedQuerySources(continuation.Body, semanticModel, cancellationToken, usePosition, iterationNode, visitedLocals);
    }

    private static void AnalyzeOperation(OperationAnalysisContext context)
    {
        if (!IsStrict(context.ContainingSymbol, context.Operation.Syntax.SyntaxTree, context.Options, context.Compilation)) return;
        var operation = context.Operation;
        switch (operation)
        {
            case IInvocationOperation invocation:
                AnalyzeMember(context, invocation.TargetMethod);
                var declaringType = invocation.TargetMethod.ContainingType?.OriginalDefinition.ToDisplayString();
                if (IsFloating(invocation.Type) && declaringType is not ("System.Math" or "System.MathF"))
                    context.ReportDiagnostic(Diagnostic.Create(Floating, invocation.Syntax.GetLocation(), "call result"));
                break;
            case IObjectCreationOperation creation when creation.Constructor is not null:
                AnalyzeMember(context, creation.Constructor);
                break;
            case IPropertyReferenceOperation property:
                AnalyzeMember(context, property.Property);
                if (IsFloating(property.Type))
                    context.ReportDiagnostic(Diagnostic.Create(Floating, property.Syntax.GetLocation(), "property value"));
                break;
            case IFieldReferenceOperation field:
                AnalyzeMember(context, field.Field);
                if (IsFloating(field.Type))
                    context.ReportDiagnostic(Diagnostic.Create(Floating, field.Syntax.GetLocation(), "field value"));
                break;
            case ILiteralOperation or IBinaryOperation or IUnaryOperation when IsFloating(operation.Type):
                context.ReportDiagnostic(Diagnostic.Create(Floating, operation.Syntax.GetLocation(), "arithmetic or value"));
                break;
        }
    }

    private static void AnalyzeMember(OperationAnalysisContext context, ISymbol member)
    {
        var owner = member.ContainingType;
        if (owner is null) return;
        var ownerName = owner.OriginalDefinition.ToDisplayString();
        var name = member.Name;
        if (ownerName == "System.MathF" || (ownerName == "System.Math" && member is IMethodSymbol method
            && (IsFloating(method.ReturnType) || method.Parameters.Any(parameter => IsFloating(parameter.Type)))))
        {
            context.ReportDiagnostic(Diagnostic.Create(Floating, context.Operation.Syntax.GetLocation(), "math"));
            return;
        }
        if (ownerName is "System.Random" or "System.Security.Cryptography.RandomNumberGenerator"
            || (ownerName == "System.Guid" && name == "NewGuid")
            || (ownerName is "System.DateTime" or "System.DateTimeOffset" && name is "Now" or "UtcNow" or "Today")
            || (ownerName == "System.Environment" && name is "TickCount" or "TickCount64")
            || ownerName == "System.Diagnostics.Stopwatch"
            || (ownerName == "System.TimeProvider" && name == "System"))
        {
            context.ReportDiagnostic(Diagnostic.Create(ExternalState, context.Operation.Syntax.GetLocation(), "clock or random source"));
            return;
        }
        if (ownerName == "System.Threading.Tasks.Parallel"
            || ownerName == "System.Linq.ParallelEnumerable"
            || (ownerName == "System.Threading.Tasks.Task" && name is "Run" or "Start" or "ContinueWith")
            || (ownerName.StartsWith("System.Threading.Tasks.TaskFactory", StringComparison.Ordinal)
                && name is "StartNew" or "ContinueWhenAll" or "ContinueWhenAny")
            || (ownerName == "System.Threading.ThreadPool" && name is "QueueUserWorkItem" or "UnsafeQueueUserWorkItem")
            || (ownerName == "System.Threading.Thread" && name == "Start"))
        {
            context.ReportDiagnostic(Diagnostic.Create(Ordering, context.Operation.Syntax.GetLocation(), "uncontrolled parallel work"));
        }
    }

    private static bool IsFloating(ITypeSymbol? type)
    {
        if (type is null) return false;
        if (type.SpecialType is SpecialType.System_Single or SpecialType.System_Double) return true;
        if (type is IArrayTypeSymbol array) return IsFloating(array.ElementType);
        if (type.ToDisplayString() is "System.Half" or "System.Numerics.Complex" or "System.Numerics.Vector2" or "System.Numerics.Vector3"
            or "System.Numerics.Vector4" or "System.Numerics.Quaternion" or "System.Numerics.Matrix3x2"
            or "System.Numerics.Matrix4x4" or "System.Numerics.Plane") return true;
        if (type is INamedTypeSymbol namedType)
        {
            if (namedType.IsTupleType) return namedType.TupleElements.Any(element => IsFloating(element.Type));
            return namedType.TypeArguments.Any(IsFloating);
        }
        return false;
    }

    private static bool IsUnorderedCollection(INamedTypeSymbol? type)
    {
        if (type is null) return false;
        // Dictionary.Keys and .Values are nested collection types on the dictionary.
        if (type.ContainingType is { } container && IsUnorderedCollection(container)) return true;
        var definition = type.OriginalDefinition;
        var ns = definition.ContainingNamespace.ToDisplayString();
        return (ns == "System.Collections.Generic" && definition.MetadataName is "Dictionary`2" or "HashSet`1" or "IDictionary`2" or "ISet`1" or "IReadOnlyDictionary`2" or "IReadOnlySet`1")
            || (ns == "System.Collections.Concurrent" && definition.MetadataName is "ConcurrentDictionary`2" or "ConcurrentBag`1");
    }

    private static bool IsStrict(ISymbol? symbol, SyntaxTree tree, AnalyzerOptions options, Compilation compilation)
    {
        var fileMode = options.AnalyzerConfigOptionsProvider.GetOptions(tree)
            .TryGetValue("systemic_determinism", out var value)
            ? value.Trim()
            : string.Empty;
        var explicitlyStrict = false;
        var gameplaySystem = false;
        for (var current = symbol; current is not null; current = current.ContainingSymbol)
        {
            if (current is INamedTypeSymbol containingType && IsGameplaySystem(containingType, compilation))
                gameplaySystem = true;

            foreach (var attribute in current.GetAttributes())
            {
                var name = attribute.AttributeClass?.ToDisplayString();
                if (name == CosmeticAttribute) return false;
                if (name == StrictAttribute) explicitlyStrict = true;
            }
        }

        // Explicit strict markers retain their override semantics even in a relaxed file.
        if (explicitlyStrict) return true;
        if (string.Equals(fileMode, "strict", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(fileMode, "relaxed", StringComparison.OrdinalIgnoreCase)) return false;
        return gameplaySystem;
    }

    private static bool IsGameplaySystem(INamedTypeSymbol type, Compilation compilation) =>
        Implements(type, SystemicHostAssembly, "Systemic.Host." + SystemicSystemInterface, compilation)
        || Implements(type, SystemicKernelAssembly, "Systemic.Kernel." + ScheduledKernelSystemInterface, compilation);

    private static bool Implements(INamedTypeSymbol type, string assemblyName, string metadataName, Compilation compilation)
    {
        var assembly = compilation.Assembly.Identity.Name == assemblyName
            ? compilation.Assembly
            : compilation.SourceModule.ReferencedAssemblySymbols.FirstOrDefault(candidate => candidate.Identity.Name == assemblyName);
        var frameworkInterface = assembly?.GetTypeByMetadataName(metadataName);
        return frameworkInterface is not null && type.AllInterfaces.Any(implemented =>
            SymbolEqualityComparer.Default.Equals(implemented.OriginalDefinition, frameworkInterface.OriginalDefinition));
    }
}
