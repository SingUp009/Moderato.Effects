using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Moderato.Effects.SourceGenerator;

/// <summary>A diagnostic kept as plain data, so models stay comparable.</summary>
internal sealed record DiagnosticInfo(DiagnosticDescriptor Descriptor, Location? Location, EquatableArray<string> Arguments)
{
    public static DiagnosticInfo Create(DiagnosticDescriptor descriptor, Location? location, params string[] arguments)
        => new(descriptor, location, arguments.ToEquatableArray());

    public Diagnostic ToDiagnostic() => Diagnostic.Create(Descriptor, Location, [.. Arguments.Items]);
}

/// <summary>Where and how a partial declaration of the target type is written.</summary>
internal sealed record Declaration(string Namespace, string Keyword, string Name)
{
    public string Open() => Namespace.Length == 0 ? "" : $"namespace {Namespace};\n\n";
}

/// <summary>How one type is reached from a union through its cases: the cases on the way, ending with the type itself.</summary>
internal sealed record CasePath(EquatableArray<string> Steps)
{
    public string Target => Steps.Items[Steps.Length - 1];

    public bool IsDirect => Steps.Length == 1;
}

internal static class Shapes
{
    public static readonly SymbolDisplayFormat FullName = SymbolDisplayFormat.FullyQualifiedFormat
        .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public static string Name(ITypeSymbol type) => type.ToDisplayString(FullName);

    // Detected by the runtime contract rather than syntax: source unions show IUnion, unions from metadata also carry [Union].
    public static bool IsUnion(ITypeSymbol type)
        => type.AllInterfaces.Any(static i => i.ToDisplayString() == "System.Runtime.CompilerServices.IUnion")
            || type.GetAttributes().Any(static a => a.AttributeClass?.ToDisplayString() == "System.Runtime.CompilerServices.UnionAttribute");

    /// <summary>The case types of a union: the parameter types of its public single-parameter constructors.</summary>
    public static ImmutableArray<ITypeSymbol> Cases(INamedTypeSymbol union)
        => [.. union.InstanceConstructors
            .Where(c => c.DeclaredAccessibility == Accessibility.Public && c.Parameters.Length == 1
                && !SymbolEqualityComparer.Default.Equals(c.Parameters[0].Type, union))
            .Select(static c => c.Parameters[0].Type)];

    /// <summary>Every type reachable through the union's cases, with each way to reach it.</summary>
    public static Dictionary<string, List<CasePath>> Reachable(INamedTypeSymbol union)
    {
        var found = new Dictionary<string, List<CasePath>>(StringComparer.Ordinal);
        var onPath = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default) { union };
        Visit(union, ImmutableArray<string>.Empty);
        return found;

        void Visit(INamedTypeSymbol current, ImmutableArray<string> path)
        {
            foreach (var @case in Cases(current))
            {
                var steps = path.Add(Name(@case));
                var name = Name(@case);
                if (!found.TryGetValue(name, out var paths)) found[name] = paths = [];
                paths.Add(new CasePath(steps.ToEquatableArray()));

                if (@case is INamedTypeSymbol nested && IsUnion(nested) && onPath.Add(nested))
                {
                    Visit(nested, steps);
                    onPath.Remove(nested);
                }
            }
        }
    }

    public static bool IsPartial(TypeDeclarationSyntax node) => node.Modifiers.Any(static m => m.IsKind(SyntaxKind.PartialKeyword));

    public static Declaration Declare(INamedTypeSymbol type, TypeDeclarationSyntax node)
    {
        var keyword = node is RecordDeclarationSyntax record && !record.ClassOrStructKeyword.IsKind(SyntaxKind.None)
            ? $"{record.Keyword.Text} {record.ClassOrStructKeyword.Text}"
            : node.Keyword.Text;
        var ns = type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString();
        return new Declaration(ns, keyword, type.Name);
    }

    /// <summary>The diagnostic for a target the generators cannot handle yet, if any.</summary>
    public static DiagnosticInfo? Unsupported(string attribute, INamedTypeSymbol type, Location location)
    {
        if (type.ContainingType is { } outer)
            return DiagnosticInfo.Create(Diagnostics.NestedNotSupported, location, attribute, type.Name, outer.Name);
        if (type.IsGenericType)
            return DiagnosticInfo.Create(Diagnostics.GenericNotSupported, location, attribute, type.Name);
        return null;
    }

    /// <summary>A conversion from a case at the end of <paramref name="path"/> up to the union at its start.</summary>
    public static string Widen(CasePath path, string value)
    {
        // Each step is a union conversion; casting through the middle unions, innermost first, reaches the outermost case.
        var expression = value;
        for (var i = path.Steps.Length - 2; i >= 0; i--) expression = $"({path.Steps.Items[i]}){expression}";
        return expression;
    }

    /// <summary>A pattern test that narrows <paramref name="value"/> through each case of <paramref name="path"/> into <paramref name="result"/>.</summary>
    public static string Narrow(CasePath path, string value, string result)
    {
        var tests = new List<string>();
        var current = value;
        for (var i = 0; i < path.Steps.Length; i++)
        {
            var name = i == path.Steps.Length - 1 ? result : $"step{i}";
            tests.Add($"{current} is {path.Steps.Items[i]} {name}");
            current = name;
        }

        return string.Join(" && ", tests);
    }
}
