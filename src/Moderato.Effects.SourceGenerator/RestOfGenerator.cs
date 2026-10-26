using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Moderato.Effects.SourceGenerator;

/// <summary>Implements <c>IUnionRest</c> on <c>[RestOf&lt;E&gt;]</c> unions, so <c>Catch</c> can remove the one case of E they leave out.</summary>
[Generator]
public sealed class RestOfGenerator : IIncrementalGenerator
{
    private sealed record Rest(string Case, CasePath Path);

    private sealed record Model(
        Declaration? Declaration, string Source, string Caught, string? NullCase, EquatableArray<Rest> Rests, EquatableArray<DiagnosticInfo> Diagnostics);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var models = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Moderato.Effects.RestOfAttribute`1",
            static (node, _) => node is TypeDeclarationSyntax,
            static (ctx, _) => Describe((INamedTypeSymbol)ctx.TargetSymbol, (TypeDeclarationSyntax)ctx.TargetNode, ctx.Attributes[0]));

        context.RegisterSourceOutput(models, static (spc, model) =>
        {
            foreach (var diagnostic in model.Diagnostics) spc.ReportDiagnostic(diagnostic.ToDiagnostic());
            if (model.Declaration is { } declaration)
            {
                spc.AddSource($"{declaration.Hint}.RestOf.g.cs", Emit(declaration, model));
            }
        });
    }

    private static Model Describe(INamedTypeSymbol type, TypeDeclarationSyntax node, AttributeData attribute)
    {
        var location = node.Identifier.GetLocation();
        if (!Shapes.IsUnion(type) || !Shapes.IsPartial(node))
            return Failed(DiagnosticInfo.Create(Diagnostics.NeedsPartialUnion, location, "RestOf", type.Name));
        if (Shapes.Unsupported("RestOf", type, location) is { } unsupported) return Failed(unsupported);
        if (attribute.AttributeClass?.TypeArguments.FirstOrDefault() is not INamedTypeSymbol source || !Shapes.IsUnion(source))
        {
            var name = attribute.AttributeClass?.TypeArguments.FirstOrDefault()?.ToDisplayString() ?? "?";
            return Failed(DiagnosticInfo.Create(Diagnostics.RestOfNotUnion, location, name));
        }

        // F16: only direct cases of the source can be left out; nested cases of F still count as held.
        var held = Shapes.Reachable(type);
        var sourceCases = Shapes.Cases(source).Select(Shapes.Name).ToList();
        var missing = sourceCases.Where(c => !held.ContainsKey(c)).ToList();
        if (missing.Count != 1)
        {
            var leftOut = missing.Count == 0 ? "none" : string.Join(", ", missing.Select(Short));
            return Failed(DiagnosticInfo.Create(Diagnostics.RestOfWrongCases, location, type.Name, source.Name, leftOut));
        }

        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var @case in Shapes.Cases(source).Where(c => Shapes.Name(c) != missing[0]))
        {
            allowed.Add(Shapes.Name(@case));
            if (@case is INamedTypeSymbol union && Shapes.IsUnion(union)) allowed.UnionWith(Shapes.Reachable(union).Keys);
        }

        var extras = Extras(type, allowed);
        if (extras.Count > 0)
            return Failed(DiagnosticInfo.Create(Diagnostics.RestOfExtraCases, location, type.Name, source.Name, string.Join(", ", extras.Select(Short))));

        var rests = sourceCases
            .Where(c => c != missing[0])
            .Select(c => new Rest(c, held[c].OrderBy(static p => p.Steps.Length).First()))
            .ToEquatableArray();
        return new Model(Shapes.Declare(type, node), Shapes.Name(source), missing[0], Shapes.NullCase(sourceCases), rests, default);
    }

    // A case of the rest that E cannot produce is extra; a union the rest adds to group E's cases is looked into instead.
    private static List<string> Extras(INamedTypeSymbol rest, HashSet<string> allowed)
    {
        var extras = new List<string>();
        var visited = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default) { rest };
        Visit(rest);
        return extras;

        void Visit(INamedTypeSymbol current)
        {
            foreach (var @case in Shapes.Cases(current))
            {
                var name = Shapes.Name(@case);
                if (allowed.Contains(name)) continue;
                if (@case is INamedTypeSymbol union && Shapes.IsUnion(union))
                {
                    if (visited.Add(union)) Visit(union);
                }
                else if (!extras.Contains(name))
                {
                    extras.Add(name);
                }
            }
        }
    }

    private static Model Failed(DiagnosticInfo diagnostic) => new(null, "", "", null, default, new[] { diagnostic }.ToEquatableArray());

    private static string Short(string fullName) => fullName.Substring(fullName.LastIndexOf('.') + 1);

    private static string Emit(Declaration declaration, Model model)
    {
        var self = declaration.Name;
        var iface = $"global::Moderato.Effects.IUnionRest<{self}, {model.Source}, {model.Caught}>";
        var sb = new StringBuilder();
        sb.Append("// <auto-generated/>\n#nullable enable\n\n").Append(declaration.Open());
        sb.Append($"partial {declaration.Keyword} {self} : {iface}\n{{\n");
        sb.Append($"    static bool {iface}.TrySplit({model.Source} source, out {model.Caught} caught, out {self} rest)\n    {{\n");
        sb.Append("        switch (source)\n        {\n");
        if (model.NullCase == model.Caught)
        {
            sb.Append("            case null:\n                caught = default!;\n                rest = default!;\n                return true;\n");
        }
        else if (model.Rests.FirstOrDefault(r => r.Case == model.NullCase) is { } nullRest)
        {
            sb.Append($"            case null:\n                caught = default!;\n                rest = {Shapes.Widen(nullRest.Path, $"({nullRest.Path.Target})null")};\n                return false;\n");
        }

        sb.Append($"            case {Shapes.PatternType(model.Caught)} found:\n                caught = found;\n                rest = default!;\n                return true;\n");
        var i = 0;
        foreach (var rest in model.Rests)
        {
            sb.Append($"            case {Shapes.PatternType(rest.Case)} other{i}:\n                caught = default!;\n");
            sb.Append($"                rest = {Shapes.Widen(rest.Path, $"other{i}")};\n                return false;\n");
            i++;
        }

        sb.Append("            default:\n");
        sb.Append($"                throw new global::System.InvalidOperationException(\"The {Short(model.Source)} is default (uninitialized).\");\n");
        sb.Append("        }\n    }\n}\n");
        return sb.ToString();
    }
}
