using Moderato.Effects.Internal;

namespace Moderato.Effects;

/// <summary>Operations that need the error to be a union with a given case.</summary>
public static class EffectUnionExtensions
{
    /// <summary>Recovers from one case of the error union, as in <c>(NotFound nf) =&gt; ...</c>.</summary>
    /// <param name="effect">The effect.</param>
    /// <param name="handler">Builds the recovery from the case.</param>
    /// <returns>An effect with the same error type.</returns>
    public static Effect<T, E, R> CatchCase<T, E, R, TCase>(this Effect<T, E, R> effect, Func<TCase, Effect<T, E, R>> handler)
        where E : IUnionCase<E, TCase>
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Catch(effect, handler);
    }

    // A separate overload, since a handler returning Effect<T, Never, R> would leave E ambiguous.
    /// <summary>Recovers from one case of the error union with an effect that cannot fail.</summary>
    /// <param name="effect">The effect.</param>
    /// <param name="handler">Builds the recovery from the case.</param>
    /// <returns>An effect with the same error type.</returns>
    public static Effect<T, E, R> CatchCase<T, E, R, TCase>(this Effect<T, E, R> effect, Func<TCase, Effect<T, Never, R>> handler)
        where E : IUnionCase<E, TCase>
    {
        ArgumentNullException.ThrowIfNull(handler);
        return effect.Then<T, E>((outcome, env, context) =>
        {
            if (outcome.Kind != OutcomeKind.Err || !E.TryGet(outcome.Error, out var caught)) return new(outcome);
            UnobservedFailures.ReportDefects(outcome.Suppressed);
            return Steps.Then(handler(caught).Run(env, context), static end => end.WithErrorType<E>());
        });
    }

    private static Effect<T, E, R> Catch<T, E, R, TCase>(Effect<T, E, R> effect, Func<TCase, Effect<T, E, R>> handler)
        where E : IUnionCase<E, TCase>
        => effect.Then<T, E>((outcome, env, context) =>
        {
            if (outcome.Kind != OutcomeKind.Err || !E.TryGet(outcome.Error, out var caught)) return new(outcome);
            UnobservedFailures.ReportDefects(outcome.Suppressed);
            return handler(caught).Run(env, context);
        });
}
