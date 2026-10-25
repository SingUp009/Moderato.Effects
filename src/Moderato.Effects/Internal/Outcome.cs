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

/// <summary>The internal result of one run: the primary outcome plus the failures it suppressed.</summary>
internal readonly struct Outcome<T, E>
{
    internal readonly T Value;
    internal readonly E Error;
    internal readonly ExceptionDispatchInfo? Defect;
    internal readonly ImmutableArray<Suppressed> Suppressed;
    internal readonly OutcomeKind Kind;

    private Outcome(OutcomeKind kind, T value, E error, ExceptionDispatchInfo? defect, ImmutableArray<Suppressed> suppressed)
    {
        Kind = kind;
        Value = value;
        Error = error;
        Defect = defect;
        Suppressed = suppressed.IsDefault ? [] : suppressed;
    }

    internal static Outcome<T, E> Ok(T value) => new(OutcomeKind.Ok, value, default!, null, []);

    internal static Outcome<T, E> Err(E error) => new(OutcomeKind.Err, default!, error, null, []);

    internal static Outcome<T, E> Die(ExceptionDispatchInfo defect, ImmutableArray<Suppressed> suppressed = default)
        => new(OutcomeKind.Die, default!, default!, defect, suppressed);

    internal static Outcome<T, E> Interrupted() => new(OutcomeKind.Interrupted, default!, default!, null, []);

    /// <summary>Carries a non-Ok outcome over to another value type.</summary>
    internal Outcome<U, E> WithValueType<U>() => Kind == OutcomeKind.Ok
        ? throw new InvalidOperationException("An Ok outcome cannot change its value type.")
        : new(Kind, default!, Error, Defect, Suppressed);

    /// <summary>Carries a non-Err outcome over to another error type.</summary>
    internal Outcome<T, F> WithErrorType<F>() => Kind == OutcomeKind.Err
        ? throw new InvalidOperationException("An Err outcome cannot change its error type.")
        : new(Kind, Value, default!, Defect, Suppressed);
}

/// <summary>Turns exceptions escaping user code into outcomes.</summary>
internal static class Defects
{
    /// <summary>OCE from the run's own token is an interruption; everything else is a defect.</summary>
    internal static Outcome<T, E> From<T, E>(Exception exception, RunContext context)
        => exception is OperationCanceledException && context.CancellationToken.IsCancellationRequested
            ? Outcome<T, E>.Interrupted()
            : Outcome<T, E>.Die(ExceptionDispatchInfo.Capture(exception));
}
