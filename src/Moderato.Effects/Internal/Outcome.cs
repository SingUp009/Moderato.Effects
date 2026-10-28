using System.Collections.Immutable;
using System.Runtime.ExceptionServices;

namespace Moderato.Effects.Internal;

internal enum OutcomeKind : byte
{
    Ok = 1,
    Err = 2,
    Die = 3,
    Interrupted = 4,
}

internal static class SuppressedLimit
{
    /// <summary>The most suppressed failures one outcome keeps; the rest are only counted.</summary>
    internal const int MAX = 100;
}

internal static class FailureSequence
{
    private static long sequence;

    internal static long Next() => Interlocked.Increment(ref sequence);

    // Subtraction preserves ordering across the signed counter wrap.
    internal static bool Before(long first, long second) => unchecked(first - second) < 0;
}

/// <summary>The internal result of one run: the primary outcome plus the failures it suppressed.</summary>
internal readonly struct Outcome<T, E>
{
    internal readonly T Value;
    internal readonly E Error;
    internal readonly ExceptionDispatchInfo? Defect;
    internal readonly ImmutableArray<Suppressed> Suppressed;
    internal readonly int Omitted;
    internal readonly OutcomeKind Kind;
    internal readonly long FailureOrder;

    /// <summary>A failure observed after its token was canceled, a consequence of the cancellation.</summary>
    internal readonly bool Consequential;

    private Outcome(OutcomeKind kind, T value, E error, long failureOrder = 0, bool consequential = false)
    {
        Kind = kind;
        Value = value;
        Error = error;
        Suppressed = [];
        FailureOrder = failureOrder;
        Consequential = consequential;
    }

    private Outcome(
        OutcomeKind kind, T value, E error, ExceptionDispatchInfo? defect, bool consequential, ImmutableArray<Suppressed> suppressed, int omitted, long failureOrder)
    {
        Kind = kind;
        Value = value;
        Error = error;
        Defect = defect;
        Consequential = consequential;
        FailureOrder = failureOrder;
        if (suppressed.IsDefault) suppressed = [];
        if (suppressed.Length > SuppressedLimit.MAX)
        {
            omitted += suppressed.Length - SuppressedLimit.MAX;
            suppressed = suppressed[..SuppressedLimit.MAX];
        }

        Suppressed = suppressed;
        Omitted = omitted;
    }

    internal static Outcome<T, E> Ok(T value) => new(OutcomeKind.Ok, value, default!);

    internal static Outcome<T, E> Err(E error, bool consequential = false, long? failureOrder = null)
        => new(OutcomeKind.Err, default!, error, failureOrder ?? FailureSequence.Next(), consequential);

    internal static Outcome<T, E> Die(ExceptionDispatchInfo defect, ImmutableArray<Suppressed> suppressed = default, bool consequential = false, int omitted = 0, long? failureOrder = null)
        => new(OutcomeKind.Die, default!, default!, defect, consequential, suppressed, omitted, failureOrder ?? FailureSequence.Next());

    internal static Outcome<T, E> Interrupted() => new(OutcomeKind.Interrupted, default!, default!);

    /// <summary>Carries a non-Ok outcome over to another value type.</summary>
    internal Outcome<U, E> WithValueType<U>() => Kind == OutcomeKind.Ok
        ? throw new InvalidOperationException("An Ok outcome cannot change its value type.")
        : new(Kind, default!, Error, Defect, Consequential, Suppressed, Omitted, FailureOrder);

    /// <summary>Carries a Die or Interrupted outcome over to other value and error types.</summary>
    internal Outcome<U, F> WithTypes<U, F>() => Kind is OutcomeKind.Ok or OutcomeKind.Err
        ? throw new InvalidOperationException("Only Die and Interrupted outcomes can change both types.")
        : new(Kind, default!, default!, Defect, Consequential, Suppressed, Omitted, FailureOrder);

    /// <summary>Carries a non-Err outcome over to another error type.</summary>
    internal Outcome<T, F> WithErrorType<F>() => Kind == OutcomeKind.Err
        ? throw new InvalidOperationException("An Err outcome cannot change its error type.")
        : new(Kind, Value, default!, Defect, Consequential, Suppressed, Omitted, FailureOrder);

    /// <summary>Transforms an Err without changing its origin.</summary>
    internal Outcome<U, F> WithError<U, F>(F error) => Kind != OutcomeKind.Err
        ? throw new InvalidOperationException("Only an Err outcome can transform its error.")
        : new(Kind, default!, error, null, Consequential, Suppressed, Omitted, FailureOrder);

    /// <summary>Puts failures suppressed by earlier steps in front of this outcome's own.</summary>
    internal Outcome<T, E> After(ImmutableArray<Suppressed> earlier, int earlierOmitted = 0)
    {
        if (earlier.IsDefaultOrEmpty && earlierOmitted == 0) return this;
        if (!earlier.IsDefault && earlier.Length >= SuppressedLimit.MAX)
            return new(Kind, Value, Error, Defect, Consequential, earlier, Omitted + earlierOmitted + Suppressed.Length, FailureOrder);
        if (!earlier.IsDefault && earlier.Length + Suppressed.Length > SuppressedLimit.MAX)
        {
            var retained = SuppressedLimit.MAX - earlier.Length;
            return new(Kind, Value, Error, Defect, Consequential, earlier.AddRange(Suppressed.AsSpan()[..retained]),
                Omitted + earlierOmitted + Suppressed.Length - retained, FailureOrder);
        }

        return new(Kind, Value, Error, Defect, Consequential, earlier.IsDefault ? Suppressed : earlier.AddRange(Suppressed), Omitted + earlierOmitted, FailureOrder);
    }

    /// <summary>Puts the failures suppressed by an earlier outcome in front of this outcome's own.</summary>
    internal Outcome<T, E> After<U, F>(Outcome<U, F> earlier) => After(earlier.Suppressed, earlier.Omitted);

    /// <summary>Follows an outcome this one replaced, keeping its Err as context.</summary>
    internal Outcome<T, E> Replacing<U, F>(Outcome<U, F> earlier)
    {
        if (earlier.Kind != OutcomeKind.Err) return After(earlier);
        var failure = new Suppressed(ExitCases.Failed(earlier.Error), Relation.Sequential);
        var retained = Math.Min(earlier.Suppressed.Length, SuppressedLimit.MAX - 1);
        return After([failure, .. earlier.Suppressed.AsSpan()[..retained]], earlier.Omitted + earlier.Suppressed.Length - retained);
    }

    /// <summary>This outcome with its suppressed failures replaced.</summary>
    internal Outcome<T, E> WithSuppressed(ImmutableArray<Suppressed> suppressed, int omitted)
        => new(Kind, Value, Error, Defect, Consequential, suppressed, omitted, FailureOrder);
}

/// <summary>Turns exceptions escaping user code into outcomes.</summary>
internal static class Defects
{
    /// <summary>Own-token OCE = interruption; anything else is a defect, consequential if the token was canceled.</summary>
    internal static Outcome<T, E> From<T, E>(Exception exception, RunContext context)
    {
        var canceled = context.CancellationToken.IsCancellationRequested;
        if (exception is OperationCanceledException && canceled) return Outcome<T, E>.Interrupted();
        var order = FailureSequence.Next();
        return Outcome<T, E>.Die(ExceptionDispatchInfo.Capture(exception), consequential: canceled, failureOrder: order);
    }
}
