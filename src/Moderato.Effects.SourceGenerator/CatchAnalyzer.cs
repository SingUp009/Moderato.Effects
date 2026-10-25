using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Moderato.Effects.SourceGenerator;

/// <summary>Explains why <c>Catch&lt;TCase, F&gt;</c> fails its constraint, which the compiler only reports as CS0315 (D13).</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CatchAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Diagnostics.NotRestOf, Diagnostics.RestLeavesOutAnother];

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
            || model.GetTypeInfo(name.TypeArgumentList.Arguments[0]).Type is not { } caught
            || model.GetTypeInfo(name.TypeArgumentList.Arguments[1]).Type is not INamedTypeSymbol rest)
        {
            return;
        }

        var source = effect.TypeArguments[1];
        if (rest.AllInterfaces.Any(i => i.OriginalDefinition.ToDisplayString() == "Moderato.Effects.IUnionRest<TSelf, TSource, TCase>"
            && SymbolEqualityComparer.Default.Equals(i.TypeArguments[1], source)
            && SymbolEqualityComparer.Default.Equals(i.TypeArguments[2], caught)))
        {
            return;
        }

        var location = name.TypeArgumentList.Arguments[1].GetLocation();
        var declared = rest.GetAttributes().Any(a => a.AttributeClass is { IsGenericType: true } c
            && c.ConstructedFrom.ToDisplayString() == "Moderato.Effects.RestOfAttribute<E>"
            && SymbolEqualityComparer.Default.Equals(c.TypeArguments[0], source));
        if (!declared)
        {
            context.ReportDiagnostic(Diagnostic.Create(Diagnostics.NotRestOf, location, rest.Name, source.Name));
            return;
        }

        if (source is not INamedTypeSymbol sourceUnion) return;
        var held = Shapes.Reachable(rest);
        var leftOut = Shapes.Cases(sourceUnion).Where(c => !held.ContainsKey(Shapes.Name(c))).ToList();
        if (leftOut.Count == 1)
            context.ReportDiagnostic(Diagnostic.Create(Diagnostics.RestLeavesOutAnother, location, rest.Name, leftOut[0].Name, caught.Name));
    }
}
