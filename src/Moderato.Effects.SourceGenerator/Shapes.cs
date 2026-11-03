using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
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
internal sealed record CasePath(EquatableArray<string> Steps, EquatableArray<string> Patterns, bool HoldsNull = false, string? FileLocal = null, string? GlobalNameFailure = null)
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

    public static string Identity(ITypeSymbol type)
        => string.Concat(type.ToDisplayParts(identity).Select(static part => part.Symbol switch
        {
            IDynamicTypeSymbol => "object",
            INamedTypeSymbol { SpecialType: SpecialType.None } named => $"{part}@{named.ContainingAssembly?.Identity}",
            _ => part.ToString(),
        }));

    private static string Display(ITypeSymbol type, SymbolDisplayFormat format)
        => string.Concat(type.ToDisplayParts(format).Select(static part =>
            part.Symbol is IDynamicTypeSymbol ? "object" : part.ToString()));

    // The compiler adds [Union] at emit; IUnion alone does not enable union patterns.
    public static bool IsUnion(ITypeSymbol type)
        => type.DeclaringSyntaxReferences.Any(static reference => reference.GetSyntax() is TypeDeclarationSyntax { Keyword.Text: "union" })
            || type.GetAttributes().Any(static a => a.AttributeClass?.ToDisplayString() == "System.Runtime.CompilerServices.UnionAttribute");

    public static INamedTypeSymbol? MemberProvider(INamedTypeSymbol union)
        => union.GetTypeMembers("IUnionMembers", 0).FirstOrDefault(static type => type.TypeKind == TypeKind.Interface);

    /// <summary>The parameter types of a union's constructors or provider factories.</summary>
    public static ImmutableArray<ITypeSymbol> Cases(INamedTypeSymbol union)
    {
        var provider = MemberProvider(union);
        var members = provider is null
            ? union.InstanceConstructors
            : provider.GetMembers("Create").OfType<IMethodSymbol>()
                .Concat(provider.AllInterfaces.SelectMany(static type => type.GetMembers("Create").OfType<IMethodSymbol>()))
                .Where(method => method.IsStatic && method.Arity == 0 && SymbolEqualityComparer.Default.Equals(method.ReturnType, union));
        return [.. members
            .Where(method => method.DeclaredAccessibility == Accessibility.Public && method.Parameters.Length == 1
                && method.Parameters[0].RefKind is RefKind.None or RefKind.In
                && (provider is not null || !SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, union)))
            .Select(static method => method.Parameters[0].Type)];
    }

    public static string? GlobalNameFailure(ITypeSymbol type, Compilation compilation)
    {
        foreach (var named in NamedTypes(type))
        {
            var original = (named.TupleUnderlyingType ?? named).OriginalDefinition;
            if (original.TypeKind != TypeKind.Error && !SymbolEqualityComparer.Default.Equals(original.ContainingAssembly, compilation.Assembly))
            {
                var metadataName = original.MetadataName;
                for (var outer = original.ContainingType; outer is not null; outer = outer.ContainingType)
                    metadataName = outer.MetadataName + "+" + metadataName;
                if (!original.ContainingNamespace.IsGlobalNamespace)
                    metadataName = original.ContainingNamespace.ToDisplayString(unescapedNamespace) + "." + metadataName;
                if (!SymbolEqualityComparer.Default.Equals(original, compilation.GetTypeByMetadataName(metadataName)))
                    return $"{Name(named)} ({original.ContainingAssembly?.Name})";
            }
        }
        return null;
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

    // Hint names disallow '@'.
    private static readonly SymbolDisplayFormat unescapedNamespace = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces);

    // A keyword name such as @class must keep its @.
    private static string Escape(string name) => SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None ? name : "@" + name;

    /// <summary>The file-local type contained in a type reference, if any.</summary>
    public static INamedTypeSymbol? FileLocalReference(ITypeSymbol type)
        => NamedTypes(type).FirstOrDefault(static named => named.IsFileLocal);

    private static IEnumerable<INamedTypeSymbol> NamedTypes(ITypeSymbol type)
    {
        var pending = new Stack<ITypeSymbol>();
        pending.Push(type);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current is IArrayTypeSymbol array) pending.Push(array.ElementType);
            if (current is IPointerTypeSymbol pointer) pending.Push(pointer.PointedAtType);
            if (current is not INamedTypeSymbol named) continue;
            yield return named;
            for (var index = named.TypeArguments.Length - 1; index >= 0; index--) pending.Push(named.TypeArguments[index]);
            if (named.ContainingType is { } containing) pending.Push(containing);
        }
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

    /// <summary>Statements converting a case through the unions on its path.</summary>
    public static string Widen(CasePath path, string value, string destination, string indent)
    {
        var statements = new StringBuilder();
        // Typed assignments select union creation; explicit casts can unbox or downcast the leaf.
        for (var i = path.Steps.Length - 2; i >= 0; i--)
        {
            var next = $"widen{i}";
            statements.Append($"{indent}{path.Steps.Items[i]} {next} = {value};\n");
            value = next;
        }
        return statements.Append($"{indent}{destination} {value};\n").ToString();
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

    public static bool AdmitsNull(ITypeSymbol type)
        => type.NullableAnnotation == NullableAnnotation.Annotated
            || type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };

    /// <summary>MEFF005 for each direct case of <paramref name="union"/> that admits null, which is indistinguishable from <c>default</c>.</summary>
    public static IEnumerable<DiagnosticInfo> NullableCases(INamedTypeSymbol union, Location location)
    {
        var cases = Cases(union).Where(AdmitsNull);
        if (MemberProvider(union) is not null) cases = cases.GroupBy(Identity).Select(static group => group.First());
        return cases.Select(Name)
            .Select(c => DiagnosticInfo.Create(Diagnostics.NullableCase, location, union.Name, c.Substring(c.LastIndexOf('.') + 1)));
    }

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
