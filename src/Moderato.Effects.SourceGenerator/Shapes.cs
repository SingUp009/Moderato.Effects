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

/// <summary>Where and how a partial declaration of the target type is written. Names are escaped for code; the hint name is not.</summary>
internal sealed record Declaration(string Namespace, string Keyword, string Name, string Hint)
{
    public string Open() => Namespace.Length == 0 ? "" : $"namespace {Namespace};\n\n";
}

/// <summary>
/// How one type is reached from a union through its cases: the cases on the way, ending with the type itself.
/// <paramref name="HoldsNull"/> says a null value in the last union on the way belongs to this type (see <see cref="Shapes.NullCase"/>).
/// </summary>
internal sealed record CasePath(EquatableArray<string> Steps, bool HoldsNull = false)
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
            var cases = Cases(current);
            var nullCase = NullCase(cases.Select(Name));
            foreach (var @case in cases)
            {
                var steps = path.Add(Name(@case));
                var name = Name(@case);
                if (!found.TryGetValue(name, out var paths)) found[name] = paths = [];
                paths.Add(new CasePath(steps.ToEquatableArray(), name == nullCase));

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
        var global = type.ContainingNamespace.IsGlobalNamespace;
        var hint = global ? type.Name : $"{type.ContainingNamespace.ToDisplayString(unescapedNamespace)}.{type.Name}";
        return new Declaration(global ? "" : type.ContainingNamespace.ToDisplayString(), keyword, Escape(type.Name), hint);
    }

    // Hint names allow no '@', so they use the names as declared.
    private static readonly SymbolDisplayFormat unescapedNamespace = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces);

    // A type may be named with a keyword, such as @class; the generated declaration has to keep the @.
    private static string Escape(string name) => SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None ? name : "@" + name;

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

    /// <summary>The type as a pattern names it: a pattern cannot test for a nullable type, so the outer <c>?</c> goes.</summary>
    public static string PatternType(string name) => name.EndsWith("?", StringComparison.Ordinal) ? name.Substring(0, name.Length - 1) : name;

    /// <summary>
    /// The direct case a null value belongs to: the only one whose type admits null. A type pattern never matches null,
    /// so null is tested on its own; with no such case, or several, null matches none.
    /// </summary>
    public static string? NullCase(IEnumerable<string> directCases)
    {
        var admitting = directCases.Where(static c => c.EndsWith("?", StringComparison.Ordinal)).Take(2).ToList();
        return admitting.Count == 1 ? admitting[0] : null;
    }

    /// <summary>
    /// MEFF005 for each direct case of <paramref name="union"/> that admits null. A union holds only its value, so a null case and
    /// an uninitialized union are the same; the generated code still treats null as that case (see <see cref="NullCase"/>).
    /// </summary>
    public static IEnumerable<DiagnosticInfo> NullableCases(INamedTypeSymbol union, Location location)
        => Cases(union).Select(Name).Where(static c => c.EndsWith("?", StringComparison.Ordinal))
            .Select(c => DiagnosticInfo.Create(Diagnostics.NullableCase, location, union.Name, c.Substring(c.LastIndexOf('.') + 1)));

    /// <summary>A pattern test that narrows <paramref name="value"/> through the cases of <paramref name="path"/> to a null value in its last union.</summary>
    public static string NarrowToNull(CasePath path, string value)
    {
        var tests = new List<string>();
        var current = value;
        for (var i = 0; i < path.Steps.Length - 1; i++)
        {
            tests.Add($"{current} is {PatternType(path.Steps.Items[i])} null{i}");
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
            tests.Add($"{current} is {PatternType(path.Steps.Items[i])} {name}");
            current = name;
        }

        return string.Join(" && ", tests);
    }
}
