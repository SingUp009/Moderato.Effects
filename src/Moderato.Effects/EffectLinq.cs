using Moderato.Effects.Internal;

namespace Moderato.Effects;

/// <summary>LINQ query syntax for <see cref="Effect{T, E, R}"/>, short-circuiting on the first failure; a step that cannot fail mixes with any error type.</summary>
public static class EffectLinq
{
    /// <summary><c>select</c></summary>
    public static Effect<U, E, R> Select<T, E, R, U>(this Effect<T, E, R> source, Func<T, U> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return source.Map(selector);
    }

    /// <summary><c>from … from</c></summary>
    public static Effect<U, E, R> SelectMany<T, E, R, U>(this Effect<T, E, R> source, Func<T, Effect<U, E, R>> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return source.AndThen(selector);
    }

    /// <summary><c>from … from … select</c></summary>
    public static Effect<V, E, R> SelectMany<T, E, R, U, V>(
        this Effect<T, E, R> source, Func<T, Effect<U, E, R>> selector, Func<T, U, V> resultSelector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(resultSelector);
        return Bind(source, selector, resultSelector, static outcome => outcome);
    }

    /// <summary><c>from … from … select</c> where the second step cannot fail.</summary>
    public static Effect<V, E, R> SelectMany<T, E, R, U, V>(
        this Effect<T, E, R> source, Func<T, Effect<U, Never, R>> selector, Func<T, U, V> resultSelector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(resultSelector);
        return Bind(source, selector, resultSelector, static outcome => outcome.WithErrorType<E>());
    }

    /// <summary><c>from … from … select</c> where the first step cannot fail.</summary>
    public static Effect<V, E, R> SelectMany<T, E, R, U, V>(
        this Effect<T, Never, R> source, Func<T, Effect<U, E, R>> selector, Func<T, U, V> resultSelector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(resultSelector);
        Effect<T, E, R> widened = source;
        return Bind(widened, selector, resultSelector, static outcome => outcome);
    }

    /// <summary><c>from … from … select</c> where neither step can fail.</summary>
    public static Effect<V, Never, R> SelectMany<T, R, U, V>(
        this Effect<T, Never, R> source, Func<T, Effect<U, Never, R>> selector, Func<T, U, V> resultSelector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(resultSelector);
        return Bind(source, selector, resultSelector, static outcome => outcome);
    }

    // Projects the next outcome directly, since building a Map per run would allocate.
    private static Effect<V, E, R> Bind<T, E, R, U, F, V>(Effect<T, E, R> source,
        Func<T, Effect<U, F, R>> next, Func<T, U, V> project, Func<Outcome<U, F>, Outcome<U, E>> widen)
    {
        // Kept with the query so suspended runs carry only their outcome and context.
        Func<Outcome<U, F>, (Outcome<T, E> outcome, RunContext context), Outcome<V, E>> complete = (raw, state) =>
        {
            var end = widen(raw);
            if (end.Kind != OutcomeKind.Ok) return end.WithValueType<V>().After(state.outcome);

            Outcome<V, E> projected;
            try
            {
                projected = Outcome<V, E>.Ok(project(state.outcome.Value, end.Value)).After(end);
            }
            catch (Exception exception)
            {
                projected = Defects.From<V, E>(exception, state.context).Replacing(end);
            }

            return projected.After(state.outcome);
        };
        return source.Then((next, complete), static (outcome, env, context, state) => outcome.Kind == OutcomeKind.Ok
            ? Steps.Then(state.next(outcome.Value).Run(env, context), (outcome, context), state.complete)
            : new ValueTask<Outcome<V, E>>(outcome.WithValueType<V>()));
    }
}
