using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Moderato.Effects.SourceGenerator;

/// <summary>Implements <c>IUnionRest</c> on <c>[RestOf&lt;E&gt;]</c> unions for <c>Catch</c>.</summary>
[Generator]
public sealed class RestOfGenerator : IIncrementalGenerator
{
    private sealed record Rest(string Case, string Pattern, CasePath Path);

    private sealed record Model(
        Declaration? Declaration, string Source, string Caught, string CaughtPattern, string? NullCase, EquatableArray<Rest> Rests, EquatableArray<DiagnosticInfo> Diagnostics);

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

        // F16: only a direct case of the source can be left out.
        var held = Shapes.Reachable(type);
        var sourceCases = Shapes.Cases(source);
        var missing = sourceCases.Where(c => !held.ContainsKey(Shapes.Identity(c))).ToList();
        if (missing.Count != 1)
        {
            var leftOut = missing.Count == 0 ? "none" : string.Join(", ", missing.Select(c => Short(Shapes.Name(c))));
            return Failed(DiagnosticInfo.Create(Diagnostics.RestOfWrongCases, location, type.Name, source.Name, leftOut));
        }

        var caughtKey = Shapes.Identity(missing[0]);
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var @case in sourceCases.Where(c => Shapes.Identity(c) != caughtKey))
        {
            allowed.Add(Shapes.Identity(@case));
            if (@case is INamedTypeSymbol union && Shapes.IsUnion(union)) allowed.UnionWith(Shapes.Reachable(union).Keys);
        }

        var extras = Extras(type, allowed);
        if (extras.Count > 0)
            return Failed(DiagnosticInfo.Create(Diagnostics.RestOfExtraCases, location, type.Name, source.Name, string.Join(", ", extras.Select(Short))));

        var rests = sourceCases
            .Where(c => Shapes.Identity(c) != caughtKey)
            .Select(c => new Rest(Shapes.Name(c), Shapes.PatternType(c), held[Shapes.Identity(c)].OrderBy(static p => p.Steps.Length).First()))
            .ToEquatableArray();
        // An [Error] union reports its nullable cases itself.
        var isError = type.GetAttributes().Any(static a => a.AttributeClass?.ToDisplayString() == "Moderato.Effects.ErrorAttribute");
        var nullable = isError ? default : Shapes.NullableCases(type, location).ToEquatableArray();
        return new Model(Shapes.Declare(type, node), Shapes.Name(source), Shapes.Name(missing[0]), Shapes.PatternType(missing[0]), Shapes.NullCase(sourceCases.Select(Shapes.Name)), rests, nullable);
    }

    // A rest case ∉ E's cases is extra, unless it is a union grouping E's cases.
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
                if (allowed.Contains(Shapes.Identity(@case))) continue;
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

    private static Model Failed(DiagnosticInfo diagnostic) => new(null, "", "", "", null, default, new[] { diagnostic }.ToEquatableArray());

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

        sb.Append($"            case {model.CaughtPattern} found:\n                caught = found;\n                rest = default!;\n                return true;\n");
        var i = 0;
        foreach (var rest in model.Rests)
        {
            sb.Append($"            case {rest.Pattern} other{i}:\n                caught = default!;\n");
            sb.Append($"                rest = {Shapes.Widen(rest.Path, $"other{i}")};\n                return false;\n");
            i++;
        }

        sb.Append("            default:\n");
        sb.Append($"                throw new global::System.InvalidOperationException(\"The {Short(model.Source)} is default (uninitialized).\");\n");
        sb.Append("        }\n    }\n}\n");
        return sb.ToString();
    }
}
