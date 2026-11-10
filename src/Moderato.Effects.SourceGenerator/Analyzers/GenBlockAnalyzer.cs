using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Moderato.Effects.SourceGenerator;

/// <summary>Finds scope misuse in a Gen block that the runtime catches late or never (D11).</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class GenBlockAnalyzer : DiagnosticAnalyzer
{
    private static readonly ImmutableHashSet<string> steps = ["Run", "Get", "Acquire", "Fork"];
    private static readonly ImmutableHashSet<string> resources = ["Acquire", "Defer", "Fork"];
    private static readonly ImmutableHashSet<string> asyncSteps = ["Run", "Acquire", "Fork"];

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
    [
        Diagnostics.CatchAllInGen, Diagnostics.StepInCleanup, Diagnostics.ScopeInNestedFunction,
        Diagnostics.ScopeEscapes, Diagnostics.ResourceInLoop, Diagnostics.BlockingWait, Diagnostics.StepNotAwaited,
    ];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterSyntaxNodeAction(
            AnalyzeFunction, SyntaxKind.ParenthesizedLambdaExpression, SyntaxKind.SimpleLambdaExpression, SyntaxKind.AnonymousMethodExpression);
    }

    /// <summary>Whether the type is <c>Moderato.Effects.Scope&lt;E, R&gt;</c>.</summary>
    internal static bool IsScope(ITypeSymbol? type)
        => type is INamedTypeSymbol { Name: "Scope", Arity: 2 } named && named.ContainingNamespace.ToDisplayString() == "Moderato.Effects";

    internal static bool IsFunction(SyntaxNode node) => node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax;

    internal static bool IsNameOf(SemanticModel model, SyntaxNode node)
        => node.Ancestors().OfType<InvocationExpressionSyntax>().Any(invocation => model.GetOperation(invocation) is INameOfOperation);

    private static bool IsGenBlock(SemanticModel model, SyntaxNode node)
        => node is AnonymousFunctionExpressionSyntax function
            && model.GetSymbolInfo(function).Symbol is IMethodSymbol method
            && method.Parameters.Any(static p => IsScope(p.Type));

    private static void AnalyzeFunction(SyntaxNodeAnalysisContext context)
    {
        var function = (AnonymousFunctionExpressionSyntax)context.Node;
        if (context.SemanticModel.GetSymbolInfo(function, context.CancellationToken).Symbol is not IMethodSymbol method) return;
        var scopes = method.Parameters.Where(static p => IsScope(p.Type)).ToImmutableArray();
        if (scopes.IsEmpty) return;

        var body = (SyntaxNode?)function.Block ?? function.ExpressionBody;
        if (body is null) return;

        var scopeFailure = scopes[0].Type.ContainingAssembly?.GetTypeByMetadataName("Moderato.Effects.ScopeFailure");
        AnalyzeScopeUses(context, function, body, scopes, scopeFailure);
        AnalyzeCatchClauses(context, body, scopes, scopeFailure);
        AnalyzeBlockingWaits(context, body);
    }

    private static void AnalyzeScopeUses(SyntaxNodeAnalysisContext context, SyntaxNode function, SyntaxNode body,
        ImmutableArray<IParameterSymbol> scopes, INamedTypeSymbol? scopeFailure)
    {
        var reportedNested = new HashSet<SyntaxNode>();
        foreach (var identifier in body.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (context.SemanticModel.GetSymbolInfo(identifier, context.CancellationToken).Symbol is not IParameterSymbol parameter
                || !scopes.Contains(parameter, SymbolEqualityComparer.Default))
            {
                continue;
            }

            if (IsNameOf(context.SemanticModel, identifier)) continue;

            var owner = identifier.Ancestors().First(IsFunction);
            if (owner != function)
            {
                if (reportedNested.Add(owner))
                    context.ReportDiagnostic(Diagnostic.Create(Diagnostics.ScopeInNestedFunction, identifier.GetLocation(), parameter.Name));
                continue;
            }

            if (identifier.Parent is MemberAccessExpressionSyntax access && access.Expression == identifier
                && access.Parent is InvocationExpressionSyntax step)
            {
                if (context.SemanticModel.GetSymbolInfo(step, context.CancellationToken).Symbol is IMethodSymbol method
                    && !SymbolEqualityComparer.Default.Equals(method.ContainingType.OriginalDefinition, parameter.Type.OriginalDefinition)
                    && !SymbolEqualityComparer.Default.Equals(method.ContainingType,
                        parameter.Type.ContainingAssembly?.GetTypeByMetadataName("Moderato.Effects.ScopeExtensions")))
                {
                    continue;
                }

                var name = access.Name.Identifier.ValueText;
                if (steps.Contains(name) && InCleanup(context.SemanticModel, identifier, function, scopeFailure))
                    context.ReportDiagnostic(Diagnostic.Create(Diagnostics.StepInCleanup, access.GetLocation(), access.ToString()));
                if (resources.Contains(name) && InLoop(identifier, function))
                    context.ReportDiagnostic(Diagnostic.Create(Diagnostics.ResourceInLoop, access.GetLocation(), access.ToString()));
                if (asyncSteps.Contains(name) && !AwaitedOrReturned(context.SemanticModel, step, function))
                    context.ReportDiagnostic(Diagnostic.Create(Diagnostics.StepNotAwaited, step.GetLocation(), access.ToString()));
            }
            else if (Escapes(context.SemanticModel, identifier))
            {
                context.ReportDiagnostic(Diagnostic.Create(Diagnostics.ScopeEscapes, identifier.GetLocation(), parameter.Name));
            }
        }
    }

    // A step still running at the block's end races its releases, so it must be awaited or returned.
    private static bool AwaitedOrReturned(SemanticModel model, InvocationExpressionSyntax step, SyntaxNode function)
    {
        // Gen awaits the callback's outer ValueTask, not an async callback's result value.
        var canReturn = function is AnonymousFunctionExpressionSyntax anonymous && anonymous.AsyncKeyword.IsKind(SyntaxKind.None);
        var taskType = model.GetTypeInfo(step).Type?.ContainingAssembly?.GetTypeByMetadataName("System.Threading.Tasks.Task");
        SyntaxNode node = step;
        while (true)
        {
            switch (node.Parent)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    node = parenthesized;
                    continue;
                case CheckedExpressionSyntax checkedExpression:
                    node = checkedExpression;
                    continue;
                case PostfixUnaryExpressionSyntax suppressed when suppressed.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                    node = suppressed;
                    continue;
                case CastExpressionSyntax cast when model.GetOperation(cast) is IConversionOperation conversion
                    && (conversion.Conversion.IsIdentity || conversion.Conversion.IsReference
                        && SymbolEqualityComparer.Default.Equals(conversion.Type, taskType)):
                    node = cast;
                    continue;
                case MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ConfigureAwait" or "AsTask" or "Preserve", Parent: InvocationExpressionSyntax configured } access
                    when access.Expression == node
                        && IsStepAdapter(model, configured, access.Name.Identifier.ValueText):
                    node = configured;
                    continue;
                case ConditionalExpressionSyntax conditional when node == conditional.WhenTrue || node == conditional.WhenFalse:
                    node = conditional;
                    continue;
                case SwitchExpressionArmSyntax arm when node == arm.Expression && arm.Parent is SwitchExpressionSyntax expression:
                    node = expression;
                    continue;
                case AwaitExpressionSyntax:
                    return true;
                case ReturnStatementSyntax:
                    return canReturn;
                case var parent:
                    return parent == function && canReturn;
            }
        }
    }

    private static bool IsStepAdapter(SemanticModel model, InvocationExpressionSyntax invocation, string name)
    {
        if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method) return name == "ConfigureAwait";
        if (method.IsStatic || method.ContainingType is not { } type) return false;
        if (SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, model.Compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask`1")))
            return name == "ConfigureAwait" || method.Parameters.Length == 0;

        return name == "ConfigureAwait"
            && (SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, model.Compilation.GetTypeByMetadataName("System.Threading.Tasks.Task`1"))
                || SymbolEqualityComparer.Default.Equals(type, model.Compilation.GetTypeByMetadataName("System.Threading.Tasks.Task")));
    }

    // A finally block, or a catch that also catches ScopeFailure, runs after an Err too.
    private static bool InCleanup(SemanticModel model, SyntaxNode node, SyntaxNode function, INamedTypeSymbol? scopeFailure)
    {
        foreach (var ancestor in node.Ancestors().TakeWhile(a => a != function))
        {
            if (ancestor is FinallyClauseSyntax) return true;
            if (ancestor is CatchClauseSyntax clause && CatchesScopeFailure(model, clause, scopeFailure)) return true;
        }

        return false;
    }

    // Setup runs once per loop entry, but an enclosing loop can repeat that entry.
    private static bool InLoop(SyntaxNode node, SyntaxNode function)
        => node.Ancestors().TakeWhile(a => a != function)
            .Any(a => a switch
            {
                ForStatementSyntax loop => loop.Statement.Span.Contains(node.Span)
                    || loop.Condition?.Span.Contains(node.Span) == true
                    || loop.Incrementors.Any(increment => increment.Span.Contains(node.Span)),
                CommonForEachStatementSyntax loop => !loop.Expression.Span.Contains(node.Span),
                WhileStatementSyntax or DoStatementSyntax => true,
                _ => false,
            });

    // Passing the scope on is fine, but storing it where it can outlive the block is not.
    private static bool Escapes(SemanticModel model, IdentifierNameSyntax identifier) => identifier.Parent switch
    {
        AssignmentExpressionSyntax assignment when assignment.Right == identifier
            => model.GetSymbolInfo(assignment.Left).Symbol is IFieldSymbol or IPropertySymbol,
        ReturnStatementSyntax or ArrowExpressionClauseSyntax => true,
        InitializerExpressionSyntax or ExpressionElementSyntax or AnonymousObjectMemberDeclaratorSyntax => true,
        ArgumentSyntax { Parent: TupleExpressionSyntax } => true,
        _ => false,
    };

    private static void AnalyzeCatchClauses(SyntaxNodeAnalysisContext context, SyntaxNode body,
        ImmutableArray<IParameterSymbol> scopes, INamedTypeSymbol? scopeFailure)
    {
        foreach (var clause in body.DescendantNodes(static n => !IsFunction(n)).OfType<CatchClauseSyntax>())
        {
            if (!CatchesScopeFailure(context.SemanticModel, clause, scopeFailure) || Rethrows(context.SemanticModel, clause)) continue;
            if (clause.Parent is not TryStatementSyntax tryStatement || !UsesScope(context.SemanticModel, tryStatement.Block, scopes)) continue;
            context.ReportDiagnostic(Diagnostic.Create(Diagnostics.CatchAllInGen, clause.CatchKeyword.GetLocation()));
        }
    }

    private static bool CatchesScopeFailure(SemanticModel model, CatchClauseSyntax clause, INamedTypeSymbol? scopeFailure)
    {
        if (scopeFailure is null) return false;
        if (clause.Declaration is { } declaration && !MatchesType(model.GetTypeInfo(declaration.Type).Type)) return false;
        if (clause.Filter is null) return true;
        if (model.GetOperation(clause) is not ICatchClauseOperation operation) return false;
        var caught = operation.Locals.FirstOrDefault();
        return Accepts(operation.Filter) == true;

        bool MatchesType(ITypeSymbol? type)
        {
            if (type is null || type.TypeKind == TypeKind.Error) return false;
            var conversion = model.Compilation.ClassifyCommonConversion(scopeFailure, type);
            return conversion.IsIdentity || conversion is { IsImplicit: true, IsReference: true };
        }

        // Filters involving arbitrary user code cannot establish that this catch handles ScopeFailure.
        bool? Accepts(IOperation? filter) => filter switch
        {
            { ConstantValue: { HasValue: true, Value: bool value } } => value,
            IParenthesizedOperation parenthesized => Accepts(parenthesized.Operand),
            IUnaryOperation { OperatorKind: UnaryOperatorKind.Not, OperatorMethod: null } unary => !Accepts(unary.Operand),
            IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalAnd, OperatorMethod: null } binary
                => Accepts(binary.LeftOperand) & Accepts(binary.RightOperand),
            IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalOr, OperatorMethod: null } binary
                => Accepts(binary.LeftOperand) | Accepts(binary.RightOperand),
            IIsTypeOperation test when IsCaught(test.ValueOperand) => MatchesType(test.TypeOperand) != test.IsNegated,
            IIsPatternOperation test when IsCaught(test.Value) => MatchesPattern(test.Pattern),
            _ => null,
        };

        bool IsCaught(IOperation operand)
            => caught is not null && operand is ILocalReferenceOperation local
                && SymbolEqualityComparer.Default.Equals(caught, local.Local);

        bool? MatchesPattern(IPatternOperation pattern) => pattern switch
        {
            ITypePatternOperation type => MatchesType(type.MatchedType),
            IDeclarationPatternOperation { MatchedType: { } type } => MatchesType(type),
            IConstantPatternOperation { Value.ConstantValue: { HasValue: true, Value: null } } => false,
            INegatedPatternOperation negated => !MatchesPattern(negated.Pattern),
            IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.And } binary
                => MatchesPattern(binary.LeftPattern) & MatchesPattern(binary.RightPattern),
            IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.Or } binary
                => MatchesPattern(binary.LeftPattern) | MatchesPattern(binary.RightPattern),
            _ => null,
        };
    }

    // Only a catch whose every path ends in a bare throw lets ScopeFailure through.
    private static bool Rethrows(SemanticModel model, CatchClauseSyntax clause)
    {
        if (model.AnalyzeControlFlow(clause.Block) is not { Succeeded: true, EndPointIsReachable: false } flow
            || !flow.ExitPoints.IsEmpty)
        {
            return false;
        }
        var statements = clause.Block.DescendantNodes(static n => !IsFunction(n)).ToList();
        if (statements.OfType<ThrowExpressionSyntax>().Any()) return false;
        var throws = statements.OfType<ThrowStatementSyntax>().ToList();
        return throws.Count > 0 && throws.All(t => t.Expression is null
            && t.Ancestors().OfType<CatchClauseSyntax>().FirstOrDefault() == clause);
    }

    // A nested Gen block catches its own ScopeFailure, so only this block's scope counts.
    private static bool UsesScope(SemanticModel model, SyntaxNode node, ImmutableArray<IParameterSymbol> scopes)
        => node.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Any(i => model.GetSymbolInfo(i).Symbol is IParameterSymbol p && scopes.Contains(p, SymbolEqualityComparer.Default)
                && !IsNameOf(model, i));

    private static void AnalyzeBlockingWaits(SyntaxNodeAnalysisContext context, SyntaxNode body)
    {
        // A lambda runs on the block's flow, while a nested Gen block is analyzed separately.
        var model = context.SemanticModel;
        foreach (var access in body.DescendantNodes(n => !IsGenBlock(model, n)).OfType<MemberAccessExpressionSyntax>())
        {
            var name = access.Name.Identifier.ValueText;
            if (name is not ("Result" or "Wait" or "GetResult")) continue;

            var receiver = context.SemanticModel.GetTypeInfo(access.Expression, context.CancellationToken).Type;
            var receiverName = receiver?.OriginalDefinition.ToDisplayString();
            var blocks = name switch
            {
                "Result" => receiverName is "System.Threading.Tasks.Task<TResult>" or "System.Threading.Tasks.ValueTask<TResult>",
                "Wait" => receiverName is "System.Threading.Tasks.Task" or "System.Threading.Tasks.Task<TResult>",
                _ => receiverName?.StartsWith("System.Runtime.CompilerServices.", System.StringComparison.Ordinal) == true
                    && receiverName.Contains("Awaiter"),
            };
            if (!blocks) continue;
            if (name == "Result")
            {
                if (access.Ancestors().OfType<InvocationExpressionSyntax>()
                    .Any(invocation => model.GetOperation(invocation, context.CancellationToken) is INameOfOperation))
                {
                    continue;
                }
            }
            else if (access.Parent is not InvocationExpressionSyntax invocation || invocation.Expression != access
                || model.GetSymbolInfo(invocation, context.CancellationToken).Symbol is IMethodSymbol method && !DeclaredOnReceiver(receiver, method))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(Diagnostics.BlockingWait, access.Name.GetLocation(), name));
        }
    }

    private static bool DeclaredOnReceiver(ITypeSymbol? receiver, IMethodSymbol method)
    {
        if (method.ContainingType is not { } owner) return false;
        for (var type = receiver as INamedTypeSymbol; type is not null; type = type.BaseType)
            if (SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, owner.OriginalDefinition)) return true;

        return receiver is INamedTypeSymbol named && named.AllInterfaces.Any(type =>
            SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, owner.OriginalDefinition));
    }
}
