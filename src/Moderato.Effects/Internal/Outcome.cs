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

/// <summary>The internal result of one run: the primary outcome plus the failures it suppressed.</summary>
internal readonly struct Outcome<T, E>
{
    internal readonly T Value;
    internal readonly E Error;
    internal readonly ExceptionDispatchInfo? Defect;
    internal readonly ImmutableArray<Suppressed> Suppressed;
    internal readonly int Omitted;
    internal readonly OutcomeKind Kind;

    /// <summary>A Die thrown after its token was canceled, a consequence of the cancellation.</summary>
    internal readonly bool Consequential;

    private Outcome(
        OutcomeKind kind, T value, E error, ExceptionDispatchInfo? defect, bool consequential, ImmutableArray<Suppressed> suppressed, int omitted)
    {
        Kind = kind;
        Value = value;
        Error = error;
        Defect = defect;
        Consequential = consequential;
        if (suppressed.IsDefault) suppressed = [];
        if (suppressed.Length > SuppressedLimit.MAX)
        {
            omitted += suppressed.Length - SuppressedLimit.MAX;
            suppressed = suppressed[..SuppressedLimit.MAX];
        }

        Suppressed = suppressed;
        Omitted = omitted;
    }

    internal static Outcome<T, E> Ok(T value) => new(OutcomeKind.Ok, value, default!, null, false, [], 0);

    internal static Outcome<T, E> Err(E error) => new(OutcomeKind.Err, default!, error, null, false, [], 0);

    internal static Outcome<T, E> Die(ExceptionDispatchInfo defect, ImmutableArray<Suppressed> suppressed = default, bool consequential = false, int omitted = 0)
        => new(OutcomeKind.Die, default!, default!, defect, consequential, suppressed, omitted);

    internal static Outcome<T, E> Interrupted() => new(OutcomeKind.Interrupted, default!, default!, null, false, [], 0);

    /// <summary>Carries a non-Ok outcome over to another value type.</summary>
    internal Outcome<U, E> WithValueType<U>() => Kind == OutcomeKind.Ok
        ? throw new InvalidOperationException("An Ok outcome cannot change its value type.")
        : new(Kind, default!, Error, Defect, Consequential, Suppressed, Omitted);

    /// <summary>Carries a Die or Interrupted outcome over to other value and error types.</summary>
    internal Outcome<U, F> WithTypes<U, F>() => Kind is OutcomeKind.Ok or OutcomeKind.Err
        ? throw new InvalidOperationException("Only Die and Interrupted outcomes can change both types.")
        : new(Kind, default!, default!, Defect, Consequential, Suppressed, Omitted);

    /// <summary>Carries a non-Err outcome over to another error type.</summary>
    internal Outcome<T, F> WithErrorType<F>() => Kind == OutcomeKind.Err
        ? throw new InvalidOperationException("An Err outcome cannot change its error type.")
        : new(Kind, Value, default!, Defect, Consequential, Suppressed, Omitted);

    /// <summary>Puts failures suppressed by earlier steps in front of this outcome's own.</summary>
    internal Outcome<T, E> After(ImmutableArray<Suppressed> earlier, int earlierOmitted = 0)
        => earlier.IsDefaultOrEmpty && earlierOmitted == 0
            ? this
            : new(Kind, Value, Error, Defect, Consequential, earlier.IsDefault ? Suppressed : earlier.AddRange(Suppressed), Omitted + earlierOmitted);

    /// <summary>Puts the failures suppressed by an earlier outcome in front of this outcome's own.</summary>
    internal Outcome<T, E> After<U, F>(Outcome<U, F> earlier) => After(earlier.Suppressed, earlier.Omitted);

    /// <summary>Follows an outcome this one replaced, keeping its Err as context.</summary>
    internal Outcome<T, E> Replacing<U, F>(Outcome<U, F> earlier) => earlier.Kind == OutcomeKind.Err
        ? After([new Suppressed(ExitCases.Failed(earlier.Error), Relation.Sequential), .. earlier.Suppressed], earlier.Omitted)
        : After(earlier);

    /// <summary>This outcome with its suppressed failures replaced.</summary>
    internal Outcome<T, E> WithSuppressed(ImmutableArray<Suppressed> suppressed, int omitted)
        => new(Kind, Value, Error, Defect, Consequential, suppressed, omitted);
}

/// <summary>Turns exceptions escaping user code into outcomes.</summary>
internal static class Defects
{
    /// <summary>Own-token OCE = interruption; anything else is a defect, consequential if the token was canceled.</summary>
    internal static Outcome<T, E> From<T, E>(Exception exception, RunContext context)
    {
        var canceled = context.CancellationToken.IsCancellationRequested;
        return exception is OperationCanceledException && canceled
            ? Outcome<T, E>.Interrupted()
            : Outcome<T, E>.Die(ExceptionDispatchInfo.Capture(exception), consequential: canceled);
    }
}
