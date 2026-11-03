using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Moderato.Effects.SourceGenerator;

internal sealed class CaseGraph(Compilation compilation, CancellationToken cancellationToken)
{
    internal const int MAX_CASE_WORK = 4096;
    internal const int MAX_TYPE_SHAPE_NODES = 256;
    private int caseWork;
    private string[]? overlappingCases;
    private readonly Dictionary<ITypeSymbol, string> identities = new(SymbolEqualityComparer.Default);

    internal string Limit { get; private set; } = "";

    internal DiagnosticInfo Failure(Location location, string target)
        => overlappingCases is { } cases
            ? DiagnosticInfo.Create(Diagnostics.OverlappingCases, location, cases)
            : DiagnosticInfo.Create(Diagnostics.CaseGraphTooComplex, location, target, Limit);

    internal bool TryCases(INamedTypeSymbol union, out ImmutableArray<ITypeSymbol> cases, int pathLength = 1)
    {
        cases = Shapes.Cases(union);
        foreach (var type in cases)
            if (!Inspect(type, pathLength)) return false;
        if (Shapes.MemberProvider(union) is not null)
            cases = [.. cases.GroupBy(Identity).Select(static group => group.FirstOrDefault(Shapes.AdmitsNull) ?? group.First())];
        return true;
    }

    internal bool CheckOverlap(INamedTypeSymbol union, ImmutableArray<ITypeSymbol> cases, int index)
    {
        var current = cases[index];
        var identity = Identity(current);
        for (var previous = 0; previous < index; previous++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Identity(cases[previous]) == identity || !MayOverlap(cases[previous], current)) continue;
            overlappingCases = [Shapes.Name(union), Shapes.Name(cases[previous]), Shapes.Name(current)];
            return false;
        }

        return true;
    }

    private string Identity(ITypeSymbol type)
    {
        if (!identities.TryGetValue(type, out var identity)) identities.Add(type, identity = Shapes.Identity(type));
        return identity;
    }

    private bool MayOverlap(ITypeSymbol first, ITypeSymbol second)
    {
        first = PatternType(first);
        second = PatternType(second);
        if (SymbolEqualityComparer.Default.Equals(first, second)) return true;
        if (first is IArrayTypeSymbol firstArray && second is IArrayTypeSymbol secondArray)
        {
            return firstArray.Rank == secondArray.Rank && ArrayElementsMayOverlap(firstArray.ElementType, secondArray.ElementType);
        }
        var compiler = (CSharpCompilation)compilation;
        var forward = compiler.ClassifyConversion(first, second);
        var backward = compiler.ClassifyConversion(second, first);
        if (forward.IsImplicit && (forward.IsReference || forward.IsBoxing)
            || backward.IsImplicit && (backward.IsReference || backward.IsBoxing))
        {
            return true;
        }

        if (first.TypeKind == TypeKind.Interface && second.TypeKind == TypeKind.Interface) return true;
        if (first.TypeKind == TypeKind.Interface) return CanImplement(second, first, forward, backward);
        if (second.TypeKind == TypeKind.Interface) return CanImplement(first, second, forward, backward);
        return forward.IsReference || backward.IsReference;
    }

    private bool CanImplement(ITypeSymbol type, ITypeSymbol contract, Conversion forward, Conversion backward)
    {
        if (type is not IArrayTypeSymbol array) return !type.IsValueType && !type.IsSealed;
        if (forward.IsReference || backward.IsReference) return true;
        return array.IsSZArray && contract is INamedTypeSymbol { TypeArguments.Length: 1 } named
            && named.OriginalDefinition.SpecialType is SpecialType.System_Collections_Generic_IEnumerable_T
                or SpecialType.System_Collections_Generic_ICollection_T or SpecialType.System_Collections_Generic_IList_T
                or SpecialType.System_Collections_Generic_IReadOnlyCollection_T or SpecialType.System_Collections_Generic_IReadOnlyList_T
            && ArrayElementsMayOverlap(array.ElementType, named.TypeArguments[0]);
    }

    // CLR array casts use reduced element types, unlike scalar or generic variance casts.
    private bool ArrayElementsMayOverlap(ITypeSymbol first, ITypeSymbol second)
    {
        if (!first.IsValueType && !second.IsValueType) return MayOverlap(first, second);
        if (!first.IsValueType || !second.IsValueType) return false;
        return SymbolEqualityComparer.Default.Equals(first, second)
            || ReducedType(first) is { } reduced && reduced == ReducedType(second);
    }

    private static SpecialType? ReducedType(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { EnumUnderlyingType: { } underlying }) type = underlying;
        return type.SpecialType switch
        {
            SpecialType.System_SByte or SpecialType.System_Byte => SpecialType.System_SByte,
            SpecialType.System_Int16 or SpecialType.System_UInt16 => SpecialType.System_Int16,
            SpecialType.System_Int32 or SpecialType.System_UInt32 => SpecialType.System_Int32,
            SpecialType.System_Int64 or SpecialType.System_UInt64 => SpecialType.System_Int64,
            SpecialType.System_IntPtr or SpecialType.System_UIntPtr => SpecialType.System_IntPtr,
            _ => null,
        };
    }

    private ITypeSymbol PatternType(ITypeSymbol type)
        => type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            ? nullable.TypeArguments[0]
            : type.TypeKind == TypeKind.Dynamic ? compilation.GetSpecialType(SpecialType.System_Object) : type;

    internal bool Inspect(ITypeSymbol type, int pathLength = 1)
    {
        cancellationToken.ThrowIfCancellationRequested();
        caseWork += pathLength;
        if (caseWork <= MAX_CASE_WORK) return CheckTypeShape(type);
        Limit = $"{MAX_CASE_WORK} case-path segments";
        return false;
    }

    internal bool CheckTypeShape(ITypeSymbol type)
    {
        var pending = new Stack<ITypeSymbol>();
        pending.Push(type);
        var nodes = 0;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++nodes > MAX_TYPE_SHAPE_NODES)
            {
                Limit = $"{MAX_TYPE_SHAPE_NODES} nodes in a type expression";
                return false;
            }

            var current = pending.Pop();
            if (current is IArrayTypeSymbol array)
            {
                pending.Push(array.ElementType);
            }
            else if (current is IPointerTypeSymbol pointer)
            {
                pending.Push(pointer.PointedAtType);
            }
            else if (current is IFunctionPointerTypeSymbol function)
            {
                pending.Push(function.Signature.ReturnType);
                foreach (var parameter in function.Signature.Parameters) pending.Push(parameter.Type);
            }
            else if (current is INamedTypeSymbol named)
            {
                if (named.ContainingType is { } containing) pending.Push(containing);
                foreach (var argument in named.TypeArguments) pending.Push(argument);
            }
        }

        return true;
    }

    internal bool TryReachable(INamedTypeSymbol union, out Dictionary<string, List<CasePath>> found)
    {
        found = new Dictionary<string, List<CasePath>>(StringComparer.Ordinal);
        if (!CheckTypeShape(union)) return false;
        if (!TryCases(union, out var initial)) return false;
        var onPath = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default) { union };
        var pending = new Stack<Frame>();
        pending.Push(new Frame(union, initial, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty, null));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = pending.Peek();
            if (frame.Index == frame.Cases.Length)
            {
                pending.Pop();
                onPath.Remove(frame.Type);
                continue;
            }

            var index = frame.Index++;
            var @case = frame.Cases[index];
            if (!CheckOverlap(frame.Type, frame.Cases, index))
            {
                found.Clear();
                return false;
            }

            var name = Shapes.Name(@case);
            var steps = frame.Path.Add(name);
            var patterns = frame.Patterns.Add(Shapes.PatternType(@case));
            var key = Identity(@case);
            if (!found.TryGetValue(key, out var paths)) found[key] = paths = [];
            var hidden = frame.FileLocal ?? Shapes.FileLocalReference(@case)?.Name;
            paths.Add(new CasePath(steps.ToEquatableArray(), patterns.ToEquatableArray(), frame.SingleNullable && Shapes.AdmitsNull(@case), hidden));

            if (@case is INamedTypeSymbol nested && Shapes.IsUnion(nested) && onPath.Add(nested))
            {
                if (!TryCases(nested, out var nestedCases, steps.Length + 1))
                {
                    found.Clear();
                    return false;
                }
                pending.Push(new Frame(nested, nestedCases, steps, patterns, hidden));
            }
        }

        return true;
    }

    private sealed class Frame
    {
        internal INamedTypeSymbol Type { get; }
        internal ImmutableArray<ITypeSymbol> Cases { get; }
        internal ImmutableArray<string> Path { get; }
        internal ImmutableArray<string> Patterns { get; }
        internal string? FileLocal { get; }
        internal bool SingleNullable { get; }
        internal int Index { get; set; }

        internal Frame(INamedTypeSymbol type, ImmutableArray<ITypeSymbol> cases, ImmutableArray<string> path, ImmutableArray<string> patterns, string? fileLocal)
        {
            Type = type;
            Cases = cases;
            Path = path;
            Patterns = patterns;
            FileLocal = fileLocal;
            SingleNullable = Cases.Count(Shapes.AdmitsNull) == 1;
        }
    }
}
