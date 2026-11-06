using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Moderato.Effects.SourceGenerator;

/// <summary>Explains the CS0315 that <c>Catch&lt;TCase, F&gt;</c> fails with (D13).</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CatchAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Diagnostics.NotRestOf, Diagnostics.RestLeavesOutAnother, Diagnostics.CaseGraphTooComplex, Diagnostics.OverlappingCases];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax
            {
                Name: GenericNameSyntax { Identifier.ValueText: "Catch", TypeArgumentList.Arguments.Count: 2 } name,
            } access)
        {
            return;
        }

        var model = context.SemanticModel;
        if (model.GetTypeInfo(access.Expression, context.CancellationToken).Type is not INamedTypeSymbol effect
            || effect.OriginalDefinition.ToDisplayString() != "Moderato.Effects.Effect<T, E, R>"
            || model.GetTypeInfo(name.TypeArgumentList.Arguments[0], context.CancellationToken).Type is not { } caught
            || model.GetTypeInfo(name.TypeArgumentList.Arguments[1], context.CancellationToken).Type is not INamedTypeSymbol rest)
        {
            return;
        }

        if (model.GetSymbolInfo(invocation, context.CancellationToken).Symbol is IMethodSymbol)
        {
            return;
        }

        var source = effect.TypeArguments[1];
        var location = name.TypeArgumentList.Arguments[1].GetLocation();
        var graph = new CaseGraph(model.Compilation, context.CancellationToken);
        if (rest.AllInterfaces.Any(i => i.OriginalDefinition.ToDisplayString() == "Moderato.Effects.IUnionRest<TSelf, TSource, TCase>"
            && SameType(i.TypeArguments[1], source)
            && SameType(i.TypeArguments[2], caught)))
        {
            return;
        }

        var declared = rest.GetAttributes().Any(a => a.AttributeClass is { IsGenericType: true } c
            && c.ConstructedFrom.ToDisplayString() == "Moderato.Effects.RestOfAttribute<E>"
            && SameType(c.TypeArguments[0], source));
        if (graph.Limit.Length != 0)
        {
            context.ReportDiagnostic(graph.Failure(location, rest.Name).ToDiagnostic());
            return;
        }
        if (!declared)
        {
            context.ReportDiagnostic(Diagnostic.Create(Diagnostics.NotRestOf, location, rest.Name, source.Name));
            return;
        }

        if (source is not INamedTypeSymbol sourceUnion) return;
        if (!graph.CheckTypeShape(sourceUnion) || !graph.TryReachable(rest, out var held))
        {
            context.ReportDiagnostic(graph.Failure(location, rest.Name).ToDiagnostic());
            return;
        }

        if (!graph.TryCases(sourceUnion, out var cases))
        {
            context.ReportDiagnostic(graph.Failure(location, rest.Name).ToDiagnostic());
            return;
        }
        for (var index = 0; index < cases.Length; index++)
        {
            if (graph.CheckOverlap(sourceUnion, cases, index)) continue;
            context.ReportDiagnostic(graph.Failure(location, rest.Name).ToDiagnostic());
            return;
        }

        var leftOut = cases.Where(c => !held.ContainsKey(Shapes.Identity(c))).ToList();
        if (leftOut.Count == 1 && !SameType(leftOut[0], caught))
        {
            context.ReportDiagnostic(graph.Limit.Length != 0
                ? graph.Failure(location, rest.Name).ToDiagnostic()
                : Diagnostic.Create(Diagnostics.RestLeavesOutAnother, location, rest.Name, leftOut[0].Name, caught.Name));
        }

        bool SameType(ITypeSymbol first, ITypeSymbol second)
            => graph.CheckTypeShape(first) && graph.CheckTypeShape(second)
                && ((CSharpCompilation)model.Compilation).ClassifyConversion(first, second).IsIdentity;
    }
}
