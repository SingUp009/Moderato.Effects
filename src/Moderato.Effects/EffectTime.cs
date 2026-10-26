using Moderato.Effects.Internal;
using Moderato.Results;

namespace Moderato.Effects;

/// <summary>The error of an effect that did not finish in time.</summary>
/// <param name="After">The time limit that was exceeded.</param>
public sealed record TimeoutError(TimeSpan After);

public static partial class Effect
{
    /// <summary>Waits for the duration on the environment's clock, cut short by cancellation.</summary>
    /// <param name="duration">How long, ≤ about 49.7 days; <see cref="Timeout.InfiniteTimeSpan"/> waits until canceled.</param>
    /// <returns>An effect that completes after the duration.</returns>
    public static Effect<Unit, Never, R> Sleep<R>(TimeSpan duration) where R : IHas<TimeProvider>
    {
        Timeouts.ThrowIfInvalid(duration);
        return new((env, context) => Steps.RunAsync<Unit, Never>(
            async cancellationToken =>
            {
                await Task.Delay(duration, env.Resolve<TimeProvider>(), cancellationToken).ConfigureAwait(false);
                return Unit.Value;
            },
            context));
    }
}

/// <summary>Cooperative time limits: the effect is canceled and awaited, releases included.</summary>
public static class EffectTimeExtensions
{
    /// <summary>Fails with <see cref="TimeoutError"/>, a case of the error union, if the effect runs out of time.</summary>
    /// <param name="effect">The effect.</param>
    /// <param name="after">The time limit, at most about 49.7 days (the timer's limit).</param>
    /// <returns>An effect with the same error type.</returns>
    public static Effect<T, E, R> Timeout<T, E, R>(this Effect<T, E, R> effect, TimeSpan after)
        where E : IUnionCase<E, TimeoutError>
        where R : IHas<TimeProvider>
        => Timeouts.Create(effect, after, static outcome => outcome, static error => Outcome<T, E>.Err(E.From(error)));

    /// <summary>Fails with <see cref="TimeoutError"/> if the effect does not finish in time.</summary>
    /// <param name="effect">The effect, which cannot fail otherwise.</param>
    /// <param name="after">The time limit, at most about 49.7 days (the timer's limit).</param>
    /// <returns>An effect whose only error is the timeout.</returns>
    public static Effect<T, TimeoutError, R> Timeout<T, R>(this Effect<T, Never, R> effect, TimeSpan after)
        where R : IHas<TimeProvider>
        => Timeouts.Create(effect, after, static outcome => outcome.WithErrorType<TimeoutError>(), static error => Outcome<T, TimeoutError>.Err(error));

    /// <summary>Fails with <see cref="TimeoutError"/> if the effect does not finish in time.</summary>
    /// <param name="effect">The effect, whose only error is already a timeout.</param>
    /// <param name="after">The time limit, at most about 49.7 days (the timer's limit).</param>
    /// <returns>An effect with the same error type.</returns>
    public static Effect<T, TimeoutError, R> Timeout<T, R>(this Effect<T, TimeoutError, R> effect, TimeSpan after)
        where R : IHas<TimeProvider>
        => Timeouts.Create(effect, after, static outcome => outcome, static error => Outcome<T, TimeoutError>.Err(error));

    /// <summary>Fails with the error <paramref name="onTimeout"/> builds if the effect does not finish in time.</summary>
    /// <param name="effect">The effect.</param>
    /// <param name="after">The time limit, at most about 49.7 days (the timer's limit).</param>
    /// <param name="onTimeout">Builds the error from the timeout.</param>
    /// <returns>An effect with the same error type.</returns>
    public static Effect<T, E, R> Timeout<T, E, R>(this Effect<T, E, R> effect, TimeSpan after, Func<TimeoutError, E> onTimeout)
        where R : IHas<TimeProvider>
    {
        ArgumentNullException.ThrowIfNull(onTimeout);
        return Timeouts.Create(effect, after, static outcome => outcome, error => Outcome<T, E>.Err(onTimeout(error)));
    }

    /// <summary>Succeeds with <c>Err(TimeoutError)</c> if the effect runs out of time, making the timeout a value.</summary>
    /// <param name="effect">The effect.</param>
    /// <param name="after">The time limit, at most about 49.7 days (the timer's limit).</param>
    /// <returns><c>Ok</c> with the effect's value, or <c>Err</c> with the timeout.</returns>
    public static Effect<Result<T, TimeoutError>, E, R> TimeoutResult<T, E, R>(this Effect<T, E, R> effect, TimeSpan after)
        where R : IHas<TimeProvider>
        => Timeouts.Create(
            effect,
            after,
            static outcome => outcome.Kind == OutcomeKind.Ok
                ? Outcome<Result<T, TimeoutError>, E>.Ok(new Result<T, TimeoutError>(new Ok<T>(outcome.Value))).After(outcome)
                : outcome.WithValueType<Result<T, TimeoutError>>(),
            static error => Outcome<Result<T, TimeoutError>, E>.Ok(new Result<T, TimeoutError>(new Err<TimeoutError>(error))));
}
