using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

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

        AnalyzeScopeUses(context, function, body, scopes);
        AnalyzeCatchClauses(context, body, scopes);
        AnalyzeBlockingWaits(context, body);
    }

    private static void AnalyzeScopeUses(SyntaxNodeAnalysisContext context, SyntaxNode function, SyntaxNode body, ImmutableArray<IParameterSymbol> scopes)
    {
        var reportedNested = new HashSet<SyntaxNode>();
        foreach (var identifier in body.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (context.SemanticModel.GetSymbolInfo(identifier, context.CancellationToken).Symbol is not IParameterSymbol parameter
                || !scopes.Contains(parameter, SymbolEqualityComparer.Default))
            {
                continue;
            }

            var owner = identifier.Ancestors().First(IsFunction);
            if (owner != function)
            {
                if (reportedNested.Add(owner))
                    context.ReportDiagnostic(Diagnostic.Create(Diagnostics.ScopeInNestedFunction, identifier.GetLocation(), parameter.Name));
                continue;
            }

            if (identifier.Parent is MemberAccessExpressionSyntax access && access.Expression == identifier)
            {
                var name = access.Name.Identifier.ValueText;
                if (steps.Contains(name) && InCleanup(context.SemanticModel, identifier, function))
                    context.ReportDiagnostic(Diagnostic.Create(Diagnostics.StepInCleanup, access.GetLocation(), access.ToString()));
                if (resources.Contains(name) && InLoop(identifier, function))
                    context.ReportDiagnostic(Diagnostic.Create(Diagnostics.ResourceInLoop, access.GetLocation(), access.ToString()));
                if (asyncSteps.Contains(name) && access.Parent is InvocationExpressionSyntax step && !AwaitedOrReturned(step, function))
                    context.ReportDiagnostic(Diagnostic.Create(Diagnostics.StepNotAwaited, step.GetLocation(), access.ToString()));
            }
            else if (Escapes(context.SemanticModel, identifier))
            {
                context.ReportDiagnostic(Diagnostic.Create(Diagnostics.ScopeEscapes, identifier.GetLocation(), parameter.Name));
            }
        }
    }

    // A step still running at the block's end races its releases, so it must be awaited or returned.
    private static bool AwaitedOrReturned(InvocationExpressionSyntax step, SyntaxNode function)
    {
        SyntaxNode node = step;
        while (true)
        {
            switch (node.Parent)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    node = parenthesized;
                    continue;
                case MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ConfigureAwait", Parent: InvocationExpressionSyntax configured } access
                    when access.Expression == node:
                    node = configured;
                    continue;
                case ConditionalExpressionSyntax conditional when node == conditional.WhenTrue || node == conditional.WhenFalse:
                    node = conditional;
                    continue;
                case SwitchExpressionArmSyntax arm when node == arm.Expression && arm.Parent is SwitchExpressionSyntax expression:
                    node = expression;
                    continue;
                case AwaitExpressionSyntax or ReturnStatementSyntax:
                    return true;
                case var parent:
                    return parent == function;
            }
        }
    }

    // A finally block, or a catch that also catches ScopeFailure, runs after an Err too.
    private static bool InCleanup(SemanticModel model, SyntaxNode node, SyntaxNode function)
    {
        foreach (var ancestor in node.Ancestors().TakeWhile(a => a != function))
        {
            if (ancestor is FinallyClauseSyntax) return true;
            if (ancestor is CatchClauseSyntax clause && CatchesScopeFailure(model, clause)) return true;
        }

        return false;
    }

    private static bool InLoop(SyntaxNode node, SyntaxNode function)
        => node.Ancestors().TakeWhile(a => a != function)
            .Any(static a => a is ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax);

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

    private static void AnalyzeCatchClauses(SyntaxNodeAnalysisContext context, SyntaxNode body, ImmutableArray<IParameterSymbol> scopes)
    {
        foreach (var clause in body.DescendantNodes(static n => !IsFunction(n)).OfType<CatchClauseSyntax>())
        {
            if (clause.Filter is not null || !CatchesScopeFailure(context.SemanticModel, clause) || Rethrows(context.SemanticModel, clause)) continue;
            if (clause.Parent is not TryStatementSyntax tryStatement || !UsesScope(context.SemanticModel, tryStatement.Block, scopes)) continue;
            context.ReportDiagnostic(Diagnostic.Create(Diagnostics.CatchAllInGen, clause.CatchKeyword.GetLocation()));
        }
    }

    private static bool CatchesScopeFailure(SemanticModel model, CatchClauseSyntax clause)
    {
        if (clause.Filter is not null) return false;
        if (clause.Declaration is null) return true;
        return model.GetTypeInfo(clause.Declaration.Type).Type?.ToDisplayString() is "System.Exception";
    }

    // Only a catch whose every path ends in a bare throw lets ScopeFailure through.
    private static bool Rethrows(SemanticModel model, CatchClauseSyntax clause)
    {
        var statements = clause.Block.DescendantNodes(static n => !IsFunction(n)).ToList();
        if (!statements.OfType<ThrowStatementSyntax>().Any(static t => t.Expression is null)) return false;
        if (statements.OfType<ReturnStatementSyntax>().Any()) return false;
        return model.AnalyzeControlFlow(clause.Block) is { Succeeded: true, EndPointIsReachable: false };
    }

    // A nested Gen block catches its own ScopeFailure, so only this block's scope counts.
    private static bool UsesScope(SemanticModel model, SyntaxNode node, ImmutableArray<IParameterSymbol> scopes)
        => node.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Any(i => model.GetSymbolInfo(i).Symbol is IParameterSymbol p && scopes.Contains(p, SymbolEqualityComparer.Default));

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
            if (blocks) context.ReportDiagnostic(Diagnostic.Create(Diagnostics.BlockingWait, access.Name.GetLocation(), name));
        }
    }
}
