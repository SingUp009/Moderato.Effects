namespace Moderato.Effects;

/// <summary>LINQ query syntax for <see cref="Effect{T, E, R}"/>. The first failure short-circuits. A step that cannot fail mixes with steps of any error type.</summary>
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
        return source.AndThen(t => selector(t).Map(u => resultSelector(t, u)));
    }

    /// <summary><c>from … from … select</c> where the second step cannot fail.</summary>
    public static Effect<V, E, R> SelectMany<T, E, R, U, V>(
        this Effect<T, E, R> source, Func<T, Effect<U, Never, R>> selector, Func<T, U, V> resultSelector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(resultSelector);
        return source.AndThen<V>(t => selector(t).Map(u => resultSelector(t, u)));
    }

    /// <summary><c>from … from … select</c> where the first step cannot fail.</summary>
    public static Effect<V, E, R> SelectMany<T, E, R, U, V>(
        this Effect<T, Never, R> source, Func<T, Effect<U, E, R>> selector, Func<T, U, V> resultSelector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(resultSelector);
        Effect<T, E, R> widened = source;
        return widened.AndThen(t => selector(t).Map(u => resultSelector(t, u)));
    }

    /// <summary><c>from … from … select</c> where neither step can fail.</summary>
    public static Effect<V, Never, R> SelectMany<T, R, U, V>(
        this Effect<T, Never, R> source, Func<T, Effect<U, Never, R>> selector, Func<T, U, V> resultSelector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(resultSelector);
        return source.AndThen(t => selector(t).Map(u => resultSelector(t, u)));
    }
}
