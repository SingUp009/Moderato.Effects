using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Moderato.Effects.SourceGenerator;

/// <summary>Finds code inside effects that reads the system clock instead of the environment's <c>TimeProvider</c> (D14).</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ClockAnalyzer : DiagnosticAnalyzer
{
    private const string USE_TIME_PROVIDER = "'TimeProvider' from the environment";
    private const string USE_SLEEP = "'Effect.Sleep'";

    private static readonly ImmutableDictionary<string, string> properties = new[]
    {
        "System.DateTime.Now", "System.DateTime.UtcNow", "System.DateTime.Today",
        "System.DateTimeOffset.Now", "System.DateTimeOffset.UtcNow",
        "System.TimeProvider.System", "System.Environment.TickCount", "System.Environment.TickCount64",
    }.ToImmutableDictionary(static p => p, static _ => USE_TIME_PROVIDER);

    private static readonly ImmutableDictionary<string, string> methods = new[]
    {
        ("System.Threading.Tasks.Task.Delay", USE_SLEEP),
        ("System.Threading.Thread.Sleep", USE_SLEEP),
        ("System.Diagnostics.Stopwatch.GetTimestamp", USE_TIME_PROVIDER),
        ("System.Diagnostics.Stopwatch.StartNew", USE_TIME_PROVIDER),
    }.ToImmutableDictionary(static p => p.Item1, static p => p.Item2);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Diagnostics.SystemClock];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterOperationAction(static ctx =>
        {
            var property = ((IPropertyReferenceOperation)ctx.Operation).Property;
            Check(ctx, $"{property.ContainingType.ToDisplayString()}.{property.Name}", properties);
        }, OperationKind.PropertyReference);
        context.RegisterOperationAction(static ctx =>
        {
            var method = ((IInvocationOperation)ctx.Operation).TargetMethod;
            // An overload taking a TimeProvider uses that clock, and TimeProvider.System is reported separately.
            if (method.Parameters.Any(static p => p.Type.ToDisplayString() == "System.TimeProvider")) return;
            Check(ctx, $"{method.ContainingType.ToDisplayString()}.{method.Name}", methods);
        }, OperationKind.Invocation);
    }

    private static void Check(OperationAnalysisContext context, string member, ImmutableDictionary<string, string> watched)
    {
        if (!watched.TryGetValue(member, out var instead)) return;
        if (context.Operation.SemanticModel is not { } model || !InEffect(model, context.Operation.Syntax)) return;

        var shortName = string.Join(".", member.Split('.').Skip(member.Count(static c => c == '.') - 1));
        context.ReportDiagnostic(Diagnostic.Create(Diagnostics.SystemClock, context.Operation.Syntax.GetLocation(), shortName, instead));
    }

    // Effect code = a Gen block ∪ arguments to Effect<R> factories, Succeed's value included.
    private static bool InEffect(SemanticModel model, SyntaxNode node)
    {
        foreach (var ancestor in node.Ancestors())
        {
            switch (ancestor)
            {
                case AnonymousFunctionExpressionSyntax function
                    when model.GetSymbolInfo(function).Symbol is IMethodSymbol method && method.Parameters.Any(static p => GenBlockAnalyzer.IsScope(p.Type)):
                    return true;
                case ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax invocation } } when IsFactory(model, invocation):
                    return true;
                case MemberDeclarationSyntax:
                    return false;
            }
        }

        return false;
    }

    private static bool IsFactory(SemanticModel model, InvocationExpressionSyntax invocation)
    {
        var info = model.GetSymbolInfo(invocation);
        var method = info.Symbol as IMethodSymbol ?? info.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
        return method?.ContainingType.OriginalDefinition.ToDisplayString() == "Moderato.Effects.Effect<R>";
    }
}
