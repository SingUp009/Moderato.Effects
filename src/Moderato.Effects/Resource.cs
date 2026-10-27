using System.Diagnostics;
using Moderato.Effects.Internal;
using Moderato.Results;

namespace Moderato.Effects;

/// <summary>A lazy description of a value released after use, through <see cref="Use{U}"/> or <see cref="Scope{E, R}.Acquire{T}"/>; <c>default</c> is invalid.</summary>
[DebuggerDisplay("Resource<{typeof(T).Name,nq}, {typeof(E).Name,nq}, {typeof(R).Name,nq}>")]
public readonly struct Resource<T, E, R>
{
    private readonly Func<R, RunContext, Releases<R>, ValueTask<Outcome<T, E>>>? acquire;

    internal Resource(Func<R, RunContext, Releases<R>, ValueTask<Outcome<T, E>>> acquire) => this.acquire = acquire;

    /// <summary>Acquires the value and registers its releases with <c>releases</c>.</summary>
    internal Func<R, RunContext, Releases<R>, ValueTask<Outcome<T, E>>> Acquire
        => acquire ?? ThrowHelper.ThrowUninitializedResource<Func<R, RunContext, Releases<R>, ValueTask<Outcome<T, E>>>>();

    /// <summary>A resource that cannot fail to acquire can stand in for one with any error type.</summary>
    public static implicit operator Resource<T, E, R>(Resource<T, Never, R> resource)
    {
        var acquire = resource.Acquire;
        return new((env, context, releases) => Steps.Then(acquire(env, context, releases), static outcome => outcome.WithErrorType<E>()));
    }

    /// <summary>Acquires the value, runs <paramref name="use"/> with it, then releases it however the use ended.</summary>
    /// <param name="use">Builds the work using the value, which must not escape it.</param>
    /// <returns>An effect whose outcome is the use's outcome.</returns>
    public Effect<U, E, R> Use<U>(Func<T, Effect<U, E, R>> use)
    {
        ArgumentNullException.ThrowIfNull(use);
        var acquire = Acquire;
        return new((env, context) => UseAsync(acquire, use, env, context));

        static async ValueTask<Outcome<U, E>> UseAsync(
            Func<R, RunContext, Releases<R>, ValueTask<Outcome<T, E>>> acquire, Func<T, Effect<U, E, R>> use, R env, RunContext context)
        {
            using var cancellation = new Cancellation(context.CancellationToken);
            var releases = new Releases<R>(cancellation);
            var body = releases.BodyContext;
            Outcome<U, E> outcome;
            Outcome<T, E> acquired;
            try
            {
                acquired = await acquire(env, body, releases).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                acquired = Defects.From<T, E>(exception, body);
            }

            if (acquired.Kind != OutcomeKind.Ok)
            {
                outcome = acquired.WithValueType<U>();
            }
            else
            {
                try
                {
                    outcome = await use(acquired.Value).Run(env, body).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    outcome = Defects.From<U, E>(exception, body);
                }

                outcome = outcome.After(acquired);
            }

            return await releases.ReleaseAsync(outcome, env).ConfigureAwait(false);
        }
    }

    /// <summary>Transforms the value; the release still gets the original.</summary>
    /// <param name="mapper">Transforms the value.</param>
    /// <returns>A resource with the transformed value.</returns>
    public Resource<U, E, R> Map<U>(Func<T, U> mapper)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        var acquire = Acquire;
        return new((env, context, releases) => Steps.Then(acquire(env, context, releases), (mapper, context), static (outcome, state) =>
        {
            if (outcome.Kind != OutcomeKind.Ok) return outcome.WithValueType<U>();
            try
            {
                return Outcome<U, E>.Ok(state.mapper(outcome.Value)).After(outcome);
            }
            catch (Exception exception)
            {
                return Defects.From<U, E>(exception, state.context).After(outcome);
            }
        }));
    }

    /// <summary>Transforms the acquisition error.</summary>
    /// <param name="mapper">Transforms the error.</param>
    /// <returns>A resource with the transformed error.</returns>
    public Resource<T, F, R> MapErr<F>(Func<E, F> mapper)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        var acquire = Acquire;
        return new((env, context, releases) => Steps.Then(acquire(env, context, releases), (mapper, context), static (outcome, state) =>
        {
            if (outcome.Kind != OutcomeKind.Err) return outcome.WithErrorType<F>();
            try
            {
                return Outcome<T, F>.Err(state.mapper(outcome.Error)).After(outcome);
            }
            catch (Exception exception)
            {
                return Defects.From<T, F>(exception, state.context).Replacing(outcome);
            }
        }));
    }

    /// <summary>Acquires a dependent second resource; release order is reversed.</summary>
    /// <param name="binder">Builds the second resource from this one's value.</param>
    /// <returns>A resource for the second value.</returns>
    public Resource<U, E, R> AndThen<U>(Func<T, Resource<U, E, R>> binder)
    {
        ArgumentNullException.ThrowIfNull(binder);
        var acquire = Acquire;
        return new((env, context, releases) => AcquireBothAsync(acquire, binder, env, context, releases));

        static async ValueTask<Outcome<U, E>> AcquireBothAsync(
            Func<R, RunContext, Releases<R>, ValueTask<Outcome<T, E>>> acquire, Func<T, Resource<U, E, R>> binder,
            R env, RunContext context, Releases<R> releases)
        {
            var first = await acquire(env, context, releases).ConfigureAwait(false);
            if (first.Kind != OutcomeKind.Ok) return first.WithValueType<U>();
            try
            {
                return (await binder(first.Value).Acquire(env, context, releases).ConfigureAwait(false)).After(first);
            }
            catch (Exception exception)
            {
                return Defects.From<U, E>(exception, context).After(first);
            }
        }
    }

    /// <summary>Widens the acquisition error to a union that has it as a case.</summary>
    /// <returns>A resource with the union as its error.</returns>
    public Resource<T, F, R> Widen<F>() where F : IUnionCase<F, E> => MapErr(static error => F.From(error));
}

/// <summary>Creates resources.</summary>
public static class Resource
{
    /// <summary>A resource acquired by <paramref name="acquire"/> and released by <paramref name="release"/>.</summary>
    /// <param name="acquire">Acquires the value.</param>
    /// <param name="release">Releases the value, given how the use ended, with an uncanceled token.</param>
    /// <returns>The resource.</returns>
    public static Resource<T, E, R> Make<T, E, R>(Effect<T, E, R> acquire, Func<T, ExitCase, Effect<Unit, Never, R>> release)
    {
        ArgumentNullException.ThrowIfNull(release);
        acquire = acquire.Validate();
        return new((env, context, releases) => Steps.Then(acquire.Run(env, context), (release, releases), static (outcome, state) =>
        {
            // Registered before any token check, so a cancellation right after cannot leak the value.
            if (outcome.Kind == OutcomeKind.Ok)
            {
                var value = outcome.Value;
                var release = state.release;
                state.releases.Add(exit => release(value, exit));
            }

            return outcome;
        }));
    }
}

/// <summary>LINQ query syntax for <see cref="Resource{T, E, R}"/>, releasing in reverse order of acquisition.</summary>
public static class ResourceLinq
{
    /// <summary><c>select</c></summary>
    public static Resource<U, E, R> Select<T, E, R, U>(this Resource<T, E, R> source, Func<T, U> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return source.Map(selector);
    }

    /// <summary><c>from … from</c></summary>
    public static Resource<U, E, R> SelectMany<T, E, R, U>(this Resource<T, E, R> source, Func<T, Resource<U, E, R>> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return source.AndThen(selector);
    }

    /// <summary><c>from … from … select</c></summary>
    public static Resource<V, E, R> SelectMany<T, E, R, U, V>(
        this Resource<T, E, R> source, Func<T, Resource<U, E, R>> selector, Func<T, U, V> resultSelector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(resultSelector);
        return source.AndThen(t => selector(t).Map(u => resultSelector(t, u)));
    }

    /// <summary><c>from … from … select</c> where the second resource cannot fail.</summary>
    public static Resource<V, E, R> SelectMany<T, E, R, U, V>(
        this Resource<T, E, R> source, Func<T, Resource<U, Never, R>> selector, Func<T, U, V> resultSelector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(resultSelector);
        return source.AndThen<V>(t => selector(t).Map(u => resultSelector(t, u)));
    }

    /// <summary><c>from … from … select</c> where the first resource cannot fail.</summary>
    public static Resource<V, E, R> SelectMany<T, E, R, U, V>(
        this Resource<T, Never, R> source, Func<T, Resource<U, E, R>> selector, Func<T, U, V> resultSelector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(resultSelector);
        Resource<T, E, R> widened = source;
        return widened.AndThen(t => selector(t).Map(u => resultSelector(t, u)));
    }

    /// <summary><c>from … from … select</c> where neither resource can fail.</summary>
    public static Resource<V, Never, R> SelectMany<T, R, U, V>(
        this Resource<T, Never, R> source, Func<T, Resource<U, Never, R>> selector, Func<T, U, V> resultSelector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(resultSelector);
        return source.AndThen(t => selector(t).Map(u => resultSelector(t, u)));
    }
}
