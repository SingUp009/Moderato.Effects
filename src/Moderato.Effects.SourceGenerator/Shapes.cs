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

/// <summary>Where and how a partial declaration is written; only the hint name is unescaped.</summary>
internal sealed record Declaration(string Namespace, string Keyword, string Name, string Hint)
{
    public string Open() => Namespace.Length == 0 ? "" : $"namespace {Namespace};\n\n";
}

/// <summary>The cases from a union down to one type; <paramref name="HoldsNull"/> means a null in the last union belongs to it.</summary>
internal sealed record CasePath(EquatableArray<string> Steps, EquatableArray<string> Patterns, bool HoldsNull = false, string? FileLocal = null)
{
    public string Target => Steps.Items[Steps.Length - 1];

    public bool IsDirect => Steps.Length == 1;
}

internal static class Shapes
{
    public static readonly SymbolDisplayFormat FullName = SymbolDisplayFormat.FullyQualifiedFormat
        .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private static readonly SymbolDisplayFormat identity = SymbolDisplayFormat.FullyQualifiedFormat
        .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.ExpandValueTuple);

    public static string Name(ITypeSymbol type) => Display(type, FullName);

    public static string Identity(ITypeSymbol type) => Display(type, identity);

    private static string Display(ITypeSymbol type, SymbolDisplayFormat format)
        => string.Concat(type.ToDisplayParts(format).Select(static part =>
            part.Symbol is IDynamicTypeSymbol ? "object" : part.ToString()));

    // The compiler adds [Union] at emit; IUnion alone does not enable union patterns.
    public static bool IsUnion(ITypeSymbol type)
        => type.DeclaringSyntaxReferences.Any(static reference => reference.GetSyntax() is TypeDeclarationSyntax { Keyword.Text: "union" })
            || type.GetAttributes().Any(static a => a.AttributeClass?.ToDisplayString() == "System.Runtime.CompilerServices.UnionAttribute");

    /// <summary>A union's case types = the parameter types of its public one-parameter constructors.</summary>
    public static ImmutableArray<ITypeSymbol> Cases(INamedTypeSymbol union)
        => [.. union.InstanceConstructors
            .Where(c => c.DeclaredAccessibility == Accessibility.Public && c.Parameters.Length == 1
                && !SymbolEqualityComparer.Default.Equals(c.Parameters[0].Type, union))
            .Select(static c => c.Parameters[0].Type)];

    public static bool IsPartial(TypeDeclarationSyntax node) => node.Modifiers.Any(static m => m.IsKind(SyntaxKind.PartialKeyword));

    public static Declaration Declare(INamedTypeSymbol type, TypeDeclarationSyntax node)
    {
        var keyword = node is RecordDeclarationSyntax record && !record.ClassOrStructKeyword.IsKind(SyntaxKind.None)
            ? $"{record.Keyword.Text} {record.ClassOrStructKeyword.Text}"
            : node.Keyword.Text;
        var global = type.ContainingNamespace.IsGlobalNamespace;
        var hint = global ? type.Name : $"{type.ContainingNamespace.ToDisplayString(unescapedNamespace)}.{type.Name}";
        return new Declaration(global ? "" : type.ContainingNamespace.ToDisplayString(), keyword, Escape(type.Name), hint);
    }

    // Hint names disallow '@'.
    private static readonly SymbolDisplayFormat unescapedNamespace = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces);

    // A keyword name such as @class must keep its @.
    private static string Escape(string name) => SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None ? name : "@" + name;

    /// <summary>The file-local type contained in a type reference, if any.</summary>
    public static INamedTypeSymbol? FileLocalReference(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array) return FileLocalReference(array.ElementType);
        if (type is IPointerTypeSymbol pointer) return FileLocalReference(pointer.PointedAtType);
        if (type is not INamedTypeSymbol named) return null;
        if (named.IsFileLocal) return named;
        if (named.ContainingType is { } containing && FileLocalReference(containing) is { } outer) return outer;
        foreach (var argument in named.TypeArguments)
            if (FileLocalReference(argument) is { } hidden) return hidden;
        return null;
    }

    /// <summary>The diagnostic for a target the generators cannot handle yet, if any.</summary>
    public static DiagnosticInfo? Unsupported(string attribute, INamedTypeSymbol type, Location location)
    {
        if (FileLocalReference(type) is { } hidden)
            return DiagnosticInfo.Create(Diagnostics.FileLocalNotSupported, location, attribute, hidden.Name);
        if (type.ContainingType is { } outer)
            return DiagnosticInfo.Create(Diagnostics.NestedNotSupported, location, attribute, type.Name, outer.Name);
        if (type.IsGenericType)
            return DiagnosticInfo.Create(Diagnostics.GenericNotSupported, location, attribute, type.Name);
        return null;
    }

    /// <summary>A conversion from a case at the end of <paramref name="path"/> up to the union at its start.</summary>
    public static string Widen(CasePath path, string value)
    {
        // Casting through each middle union, innermost first, reaches the outermost case.
        var expression = value;
        for (var i = path.Steps.Length - 2; i >= 0; i--) expression = $"({path.Steps.Items[i]}){expression}";
        return expression;
    }

    /// <summary>The type as a pattern names it, without the outer <c>?</c>.</summary>
    public static string PatternType(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            type = nullable.TypeArguments[0];
        return Display(type.WithNullableAnnotation(NullableAnnotation.NotAnnotated),
            FullName.AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.ExpandValueTuple));
    }

    /// <summary>The direct case a null value belongs to: the only one admitting null, else none.</summary>
    public static string? NullCase(IEnumerable<string> directCases)
    {
        var admitting = directCases.Where(static c => c.EndsWith("?", StringComparison.Ordinal)).Take(2).ToList();
        return admitting.Count == 1 ? admitting[0] : null;
    }

    /// <summary>MEFF005 for each direct case of <paramref name="union"/> that admits null, which is indistinguishable from <c>default</c>.</summary>
    public static IEnumerable<DiagnosticInfo> NullableCases(INamedTypeSymbol union, Location location)
        => Cases(union).Select(Name).Where(static c => c.EndsWith("?", StringComparison.Ordinal))
            .Select(c => DiagnosticInfo.Create(Diagnostics.NullableCase, location, union.Name, c.Substring(c.LastIndexOf('.') + 1)));

    /// <summary>A pattern narrowing <paramref name="value"/> along <paramref name="path"/> to a null in its last union.</summary>
    public static string NarrowToNull(CasePath path, string value)
    {
        var tests = new List<string>();
        var current = value;
        for (var i = 0; i < path.Steps.Length - 1; i++)
        {
            tests.Add($"{current} is {path.Patterns.Items[i]} null{i}");
            current = $"null{i}";
        }

        tests.Add($"{current} is null");
        return string.Join(" && ", tests);
    }

    /// <summary>A pattern test that narrows <paramref name="value"/> through each case of <paramref name="path"/> into <paramref name="result"/>.</summary>
    public static string Narrow(CasePath path, string value, string result)
    {
        var tests = new List<string>();
        var current = value;
        for (var i = 0; i < path.Steps.Length; i++)
        {
            var name = i == path.Steps.Length - 1 ? result : $"step{i}";
            tests.Add($"{current} is {path.Patterns.Items[i]} {name}");
            current = name;
        }

        return string.Join(" && ", tests);
    }
}
