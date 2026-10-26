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
        return Bind(source, (value, env, context) => selector(value).Run(env, context), resultSelector);
    }

    /// <summary><c>from … from … select</c> where the second step cannot fail.</summary>
    public static Effect<V, E, R> SelectMany<T, E, R, U, V>(
        this Effect<T, E, R> source, Func<T, Effect<U, Never, R>> selector, Func<T, U, V> resultSelector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(resultSelector);
        return Bind<T, E, R, U, V>(source, (value, env, context) =>
            Steps.Then(selector(value).Run(env, context), static outcome => outcome.WithErrorType<E>()), resultSelector);
    }

    /// <summary><c>from … from … select</c> where the first step cannot fail.</summary>
    public static Effect<V, E, R> SelectMany<T, E, R, U, V>(
        this Effect<T, Never, R> source, Func<T, Effect<U, E, R>> selector, Func<T, U, V> resultSelector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(resultSelector);
        Effect<T, E, R> widened = source;
        return Bind(widened, (value, env, context) => selector(value).Run(env, context), resultSelector);
    }

    /// <summary><c>from … from … select</c> where neither step can fail.</summary>
    public static Effect<V, Never, R> SelectMany<T, R, U, V>(
        this Effect<T, Never, R> source, Func<T, Effect<U, Never, R>> selector, Func<T, U, V> resultSelector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(resultSelector);
        return Bind(source, (value, env, context) => selector(value).Run(env, context), resultSelector);
    }

    // Project directly from the next outcome instead of constructing a Map on
    // every run. The source value and projection travel as value-type state.
    private static Effect<V, E, R> Bind<T, E, R, U, V>(Effect<T, E, R> source,
        Func<T, R, RunContext, ValueTask<Outcome<U, E>>> next, Func<T, U, V> project)
        => source.Then<V, E>((outcome, env, context) => outcome.Kind == OutcomeKind.Ok
            ? Steps.Then(next(outcome.Value, env, context), (outcome, project, context), static (end, state) =>
            {
                if (end.Kind != OutcomeKind.Ok) return end.WithValueType<V>().After(state.outcome);

                Outcome<V, E> projected;
                try
                {
                    projected = Outcome<V, E>.Ok(state.project(state.outcome.Value, end.Value)).After(end);
                }
                catch (Exception exception)
                {
                    projected = Defects.From<V, E>(exception, state.context).Replacing(end);
                }

                return projected.After(state.outcome);
            })
            : new(outcome.WithValueType<V>()));
}
