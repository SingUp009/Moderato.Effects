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

    internal Resource(Effect<T, E, ResourceEnvironment<R>> effect)
        => acquire = new ResourceAcquisition<T, E, R>(effect).Run;

    /// <summary>Acquires the value and registers its releases with <c>releases</c>.</summary>
    internal Func<R, RunContext, Releases<R>, ValueTask<Outcome<T, E>>> Acquire
        => acquire ?? ThrowHelper.ThrowUninitializedResource<Func<R, RunContext, Releases<R>, ValueTask<Outcome<T, E>>>>();

    internal Effect<T, E, ResourceEnvironment<R>> Acquisition
    {
        get
        {
            var acquire = Acquire;
            return acquire.Target is ResourceAcquisition<T, E, R> acquisition ? acquisition.Effect
                : new((environment, context) => acquire(environment.Environment, context, environment.Releases), nameof(Acquire));
        }
    }

    /// <summary>A resource that cannot fail to acquire can stand in for one with any error type.</summary>
    public static implicit operator Resource<T, E, R>(Resource<T, Never, R> resource)
    {
        return new(resource.Acquisition.Then<T, E, Unit>(Unit.Value,
            static (outcome, _, _, _) => new(outcome.WithErrorType<E>()), nameof(Widen)));
    }

    /// <summary>Acquires the value, runs <paramref name="use"/> with it, then releases it however the use ended.</summary>
    /// <param name="use">Builds the work using the value, which must not escape it.</param>
    public Effect<U, E, R> Use<U>(Func<T, Effect<U, E, R>> use)
    {
        ArgumentNullException.ThrowIfNull(use);
        var acquire = Acquire;
        return new((env, context) => UseAsync(acquire, use, env, context));

        static async ValueTask<Outcome<U, E>> UseAsync(
            Func<R, RunContext, Releases<R>, ValueTask<Outcome<T, E>>> acquire, Func<T, Effect<U, E, R>> use, R env, RunContext context)
        {
            using var cancellation = new Cancellation(context);
            var releases = new Releases<R>(cancellation);
            using var observation = releases.Observation?.Activate();
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
    public Resource<U, E, R> Map<U>(Func<T, U> mapper)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        return new(Acquisition.Then<U, E, Func<T, U>>(mapper, static (outcome, _, context, map) =>
        {
            if (outcome.Kind != OutcomeKind.Ok) return new(outcome.WithValueType<U>());
            try
            {
                return new(Outcome<U, E>.Ok(map(outcome.Value)).After(outcome));
            }
            catch (Exception exception)
            {
                return new(Defects.From<U, E>(exception, context).After(outcome));
            }
        }));
    }

    public Resource<T, F, R> MapErr<F>(Func<E, F> mapper)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        return new(Acquisition.Then<T, F, Func<E, F>>(mapper, static (outcome, _, context, map) =>
        {
            if (outcome.Kind != OutcomeKind.Err) return new(outcome.WithErrorType<F>());
            try
            {
                return new(outcome.WithError<T, F>(map(outcome.Error)));
            }
            catch (Exception exception)
            {
                return new(Defects.From<T, F>(exception, context).Replacing(outcome));
            }
        }));
    }

    /// <summary>Acquires a dependent second resource; release order is reversed.</summary>
    public Resource<U, E, R> AndThen<U>(Func<T, Resource<U, E, R>> binder)
    {
        ArgumentNullException.ThrowIfNull(binder);
        return new(Acquisition.Bind<U, E, U, E, Func<T, Resource<U, E, R>>>(binder,
            static (outcome, bind, _) => outcome.Kind == OutcomeKind.Ok
                ? new(bind(outcome.Value).Acquisition) : new(outcome.WithValueType<U>()),
            static (next, previous, _, _) => next.After(previous), isolate: true));
    }

    /// <summary>Widens the acquisition error to a union that has it as a case.</summary>
    public Resource<T, F, R> Widen<F>() where F : IUnionCase<F, E> => MapErr(static error => F.From(error));
}

/// <summary>Creates resources.</summary>
public static class Resource
{
    /// <summary>A resource acquired by <paramref name="acquire"/> and released by <paramref name="release"/>.</summary>
    /// <param name="release">Releases the value, given how the use ended, with an uncanceled token.</param>
    public static Resource<T, E, R> Make<T, E, R>(Effect<T, E, R> acquire, Func<T, ExitCase, Effect<Unit, Never, R>> release)
    {
        ArgumentNullException.ThrowIfNull(release);
        acquire = acquire.Validate();
        return new(acquire.Provide<ResourceEnvironment<R>>(static environment => environment.Environment)
            .Then<T, E, Func<T, ExitCase, Effect<Unit, Never, R>>>(release, static (outcome, environment, context, release) =>
        {
            // Registered before any token check, so a cancellation right after cannot leak the value.
            if (outcome.Kind == OutcomeKind.Ok) environment.Releases.Add(outcome.Value, release, context.Observation);

            return new(outcome);
        }));
    }
}

/// <summary>LINQ query syntax for <see cref="Resource{T, E, R}"/>, releasing in reverse order of acquisition.</summary>
public static class ResourceLinq
{
    public static Resource<U, E, R> Select<T, E, R, U>(this Resource<T, E, R> source, Func<T, U> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return source.Map(selector);
    }

    public static Resource<U, E, R> SelectMany<T, E, R, U>(this Resource<T, E, R> source, Func<T, Resource<U, E, R>> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return source.AndThen(selector);
    }

    public static Resource<V, E, R> SelectMany<T, E, R, U, V>(
        this Resource<T, E, R> source, Func<T, Resource<U, E, R>> selector, Func<T, U, V> resultSelector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(resultSelector);
        return SelectManyCore(source, selector, resultSelector, static outcome => outcome);
    }

    public static Resource<V, E, R> SelectMany<T, E, R, U, V>(
        this Resource<T, E, R> source, Func<T, Resource<U, Never, R>> selector, Func<T, U, V> resultSelector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(resultSelector);
        return SelectManyCore(source, selector, resultSelector, static outcome => outcome.WithErrorType<E>());
    }

    public static Resource<V, E, R> SelectMany<T, E, R, U, V>(
        this Resource<T, Never, R> source, Func<T, Resource<U, E, R>> selector, Func<T, U, V> resultSelector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(resultSelector);
        Resource<T, E, R> widened = source;
        return SelectManyCore(widened, selector, resultSelector, static outcome => outcome);
    }

    public static Resource<V, Never, R> SelectMany<T, R, U, V>(
        this Resource<T, Never, R> source, Func<T, Resource<U, Never, R>> selector, Func<T, U, V> resultSelector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(resultSelector);
        return SelectManyCore(source, selector, resultSelector, static outcome => outcome);
    }

    private static Resource<V, E, R> SelectManyCore<T, E, R, U, F, V>(
        Resource<T, E, R> source, Func<T, Resource<U, F, R>> selector, Func<T, U, V> resultSelector,
        Func<Outcome<U, F>, Outcome<U, E>> widen)
    {
        return new(source.Acquisition.Bind((selector, resultSelector, widen), static (first, state, _) => first.Kind == OutcomeKind.Ok
                ? new EffectSelection<U, F, V, E, ResourceEnvironment<R>>(state.selector(first.Value).Acquisition)
                : new EffectSelection<U, F, V, E, ResourceEnvironment<R>>(first.WithValueType<V>()),
            static (raw, first, context, state) =>
        {
            Outcome<U, E> second;
            try
            {
                second = state.widen(raw);
            }
            catch (Exception exception)
            {
                return Defects.From<V, E>(exception, context).After(first);
            }

            if (second.Kind != OutcomeKind.Ok) return second.WithValueType<V>().After(first);
            try
            {
                return Outcome<V, E>.Ok(state.resultSelector(first.Value, second.Value)).After(second).After(first);
            }
            catch (Exception exception)
            {
                return Defects.From<V, E>(exception, context).After(second).After(first);
            }
        }, isolate: true, name: nameof(SelectMany)));
    }
}
