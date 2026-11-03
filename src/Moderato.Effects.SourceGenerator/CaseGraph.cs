using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace Moderato.Effects.SourceGenerator;

internal sealed class CaseGraph(CancellationToken cancellationToken)
{
    internal const int MAX_CASE_WORK = 4096;
    internal const int MAX_TYPE_SHAPE_NODES = 256;
    private int caseWork;

    internal string Limit { get; private set; } = "";

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
        var onPath = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default) { union };
        var pending = new Stack<Frame>();
        pending.Push(new Frame(union, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty, null));
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

            var @case = frame.Cases[frame.Index++];
            if (!Inspect(@case, frame.Path.Length + 1))
            {
                found.Clear();
                return false;
            }

            var name = Shapes.Name(@case);
            var steps = frame.Path.Add(name);
            var patterns = frame.Patterns.Add(Shapes.PatternType(@case));
            var key = Shapes.Identity(@case);
            if (!found.TryGetValue(key, out var paths)) found[key] = paths = [];
            var hidden = frame.FileLocal ?? Shapes.FileLocalReference(@case)?.Name;
            paths.Add(new CasePath(steps.ToEquatableArray(), patterns.ToEquatableArray(), frame.SingleNullable && AdmitsNull(@case), hidden));

            if (@case is INamedTypeSymbol nested && Shapes.IsUnion(nested) && onPath.Add(nested))
                pending.Push(new Frame(nested, steps, patterns, hidden));
        }

        return true;
    }

    private static bool AdmitsNull(ITypeSymbol type)
        => type.NullableAnnotation == NullableAnnotation.Annotated
            || type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };

    private sealed class Frame
    {
        internal INamedTypeSymbol Type { get; }
        internal ImmutableArray<ITypeSymbol> Cases { get; }
        internal ImmutableArray<string> Path { get; }
        internal ImmutableArray<string> Patterns { get; }
        internal string? FileLocal { get; }
        internal bool SingleNullable { get; }
        internal int Index { get; set; }

        internal Frame(INamedTypeSymbol type, ImmutableArray<string> path, ImmutableArray<string> patterns, string? fileLocal)
        {
            Type = type;
            Cases = Shapes.Cases(type);
            Path = path;
            Patterns = patterns;
            FileLocal = fileLocal;
            SingleNullable = Cases.Count(AdmitsNull) == 1;
        }
    }
}
