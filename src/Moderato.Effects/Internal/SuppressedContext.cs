using System.Collections.Immutable;

namespace Moderato.Effects.Internal;

/// <summary>Keeps an ordered prefix of failure context and counts everything omitted.</summary>
internal static class SuppressedContext
{
    internal const int MAX = 100;

    internal static (ImmutableArray<Suppressed> Suppressed, int Omitted) Limit(ImmutableArray<Suppressed> failures, int omitted)
    {
        if (failures.IsDefault) return ([], omitted);
        return failures.Length <= MAX ? (failures, omitted) : (failures[..MAX], omitted + failures.Length - MAX);
    }

    internal static (ImmutableArray<Suppressed> Suppressed, int Omitted) Concat(
        ImmutableArray<Suppressed> first, int firstOmitted, ImmutableArray<Suppressed> second, int secondOmitted)
    {
        var omitted = firstOmitted + secondOmitted;
        if (first.IsDefaultOrEmpty) return Limit(second, omitted);
        if (second.IsDefaultOrEmpty) return Limit(first, omitted);
        if (first.Length >= MAX) return Limit(first, omitted + second.Length);

        var retained = Math.Min(second.Length, MAX - first.Length);
        return (first.AddRange(second.AsSpan()[..retained]), omitted + second.Length - retained);
    }

    internal static (ImmutableArray<Suppressed> Suppressed, int Omitted) Prepend(
        Suppressed failure, ImmutableArray<Suppressed> context, int omitted)
    {
        var tail = context.AsSpan();
        var retained = Math.Min(tail.Length, MAX - 1);
        return ([failure, .. tail[..retained]], omitted + tail.Length - retained);
    }

    internal static int Append(ImmutableArray<Suppressed>.Builder target, Suppressed failure)
    {
        if (target.Count == MAX) return 1;
        target.Add(failure);
        return 0;
    }

    internal static int Append(ImmutableArray<Suppressed>.Builder target, ReadOnlySpan<Suppressed> context)
    {
        var retained = Math.Min(context.Length, MAX - target.Count);
        target.AddRange(context[..retained]);
        return context.Length - retained;
    }
}
