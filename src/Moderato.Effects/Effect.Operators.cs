using Moderato.Effects.Internal;
using Moderato.Results;

namespace Moderato.Effects;

public readonly partial struct Effect<T, E, R>
{
    /// <summary>Transforms the value; an Err passes through.</summary>
    /// <param name="mapper">Transforms the value.</param>
    /// <returns>An effect with the transformed value.</returns>
    public Effect<U, E, R> Map<U>(Func<T, U> mapper)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        return Then<U, E, Func<T, U>>(mapper, static (outcome, _, _, map) => new(outcome.Kind == OutcomeKind.Ok
            ? Outcome<U, E>.Ok(map(outcome.Value)).After(outcome)
            : outcome.WithValueType<U>()));
    }

    /// <summary>Transforms the error; an Ok passes through.</summary>
    /// <param name="mapper">Transforms the error.</param>
    /// <returns>An effect with the transformed error.</returns>
    public Effect<T, F, R> MapErr<F>(Func<E, F> mapper)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        return Then<T, F, Func<E, F>>(mapper, static (outcome, _, _, map) => new(outcome.Kind == OutcomeKind.Err
            ? outcome.WithError<T, F>(map(outcome.Error))
            : outcome.WithErrorType<F>()));
    }

    /// <summary>Continues with the next effect unless this one fails.</summary>
    /// <param name="binder">Builds the next effect from the value.</param>
    /// <returns>The next effect's outcome.</returns>
    public Effect<U, E, R> AndThen<U>(Func<T, Effect<U, E, R>> binder)
    {
        ArgumentNullException.ThrowIfNull(binder);
        return Then<U, E, Func<T, Effect<U, E, R>>>(binder, static (outcome, env, context, bind) => outcome.Kind == OutcomeKind.Ok
            ? Steps.Then(bind(outcome.Value).Run(env, context), outcome, static (next, previous) => next.After(previous))
            : new(outcome.WithValueType<U>()));
    }

    /// <summary>Runs a check on the value and keeps the value if the check succeeds.</summary>
    /// <param name="check">Builds the check from the value.</param>
    /// <returns>An effect with the original value, or the check's failure.</returns>
    public Effect<T, E, R> AndThrough<U>(Func<T, Effect<U, E, R>> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        return Then<T, E, Func<T, Effect<U, E, R>>>(check, static (outcome, env, context, checkStep) => outcome.Kind == OutcomeKind.Ok
            ? Steps.Then(checkStep(outcome.Value).Run(env, context), outcome, static (next, previous) => (next.Kind == OutcomeKind.Ok
                ? Outcome<T, E>.Ok(previous.Value).After(next)
                : next.WithValueType<T>()).After(previous))
            : new(outcome));
    }

    /// <summary>Recovers from the error with the next effect.</summary>
    /// <param name="binder">Builds the recovery from the error.</param>
    /// <returns>The recovery's outcome.</returns>
    public Effect<T, F, R> OrElse<F>(Func<E, Effect<T, F, R>> binder)
    {
        ArgumentNullException.ThrowIfNull(binder);
        return Then<T, F, Func<E, Effect<T, F, R>>>(binder, static (outcome, env, context, recover) =>
        {
            if (outcome.Kind != OutcomeKind.Err) return new(outcome.WithErrorType<F>());
            UnobservedFailures.ReportDefects(outcome.Suppressed);
            return recover(outcome.Error).Run(env, context);
        });
    }

    /// <summary>Runs a side effect on the value.</summary>
    /// <param name="action">The side effect.</param>
    /// <returns>An effect with the same outcome.</returns>
    public Effect<T, E, R> Tap(Action<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Then<T, E, Action<T>>(action, static (outcome, _, _, tap) =>
        {
            if (outcome.Kind == OutcomeKind.Ok) tap(outcome.Value);
            return new(outcome);
        });
    }

    /// <summary>Runs a side effect on the error.</summary>
    /// <param name="action">The side effect.</param>
    /// <returns>An effect with the same outcome.</returns>
    public Effect<T, E, R> TapErr(Action<E> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Then<T, E, Action<E>>(action, static (outcome, _, _, tap) =>
        {
            if (outcome.Kind == OutcomeKind.Err) tap(outcome.Error);
            return new(outcome);
        });
    }

    /// <summary>Folds the value and the error into one value.</summary>
    /// <param name="ok">Folds the value.</param>
    /// <param name="err">Folds the error.</param>
    /// <returns>An effect that cannot fail.</returns>
    public Effect<U, Never, R> Match<U>(Func<T, U> ok, Func<E, U> err)
    {
        ArgumentNullException.ThrowIfNull(ok);
        ArgumentNullException.ThrowIfNull(err);
        return Then<U, Never, (Func<T, U> ok, Func<E, U> err)>((ok, err), static (outcome, _, _, handlers) => new(outcome.Kind switch
        {
            OutcomeKind.Ok => Outcome<U, Never>.Ok(handlers.ok(outcome.Value)).After(outcome),
            OutcomeKind.Err => Handled(outcome, handlers.err(outcome.Error)),
            _ => outcome.WithTypes<U, Never>(),
        }));
    }

    /// <summary>Replaces the error with a fallback value.</summary>
    /// <param name="fallback">The value to use on an Err.</param>
    /// <returns>An effect that cannot fail.</returns>
    public Effect<T, Never, R> UnwrapOr(T fallback)
        => Then<T, Never, T>(fallback, static (outcome, _, _, value) => new(Recover(outcome, static (fallback, _) => fallback, value)));

    /// <summary>Replaces the error with a value built from it.</summary>
    /// <param name="fallback">Builds the value from the error.</param>
    /// <returns>An effect that cannot fail.</returns>
    public Effect<T, Never, R> UnwrapOrElse(Func<E, T> fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        return Then<T, Never, Func<E, T>>(fallback, static (outcome, _, _, getValue) => new(Recover(outcome, static (fallback, error) => fallback(error), getValue)));
    }

    /// <summary>Makes the error a value to match on instead of short-circuiting.</summary>
    /// <returns>An effect that cannot fail, whose value is the result.</returns>
    public Effect<Result<T, E>, Never, R> Attempt() => Then<Result<T, E>, Never>(static (outcome, _, _) => new(outcome.Kind switch
    {
        OutcomeKind.Ok => Outcome<Result<T, E>, Never>.Ok(new Result<T, E>(new Ok<T>(outcome.Value))).After(outcome),
        OutcomeKind.Err => Handled(outcome, new Result<T, E>(new Err<E>(outcome.Error))),
        _ => outcome.WithTypes<Result<T, E>, Never>(),
    }));

    /// <summary>Handles one case and narrows the error to the <c>[RestOf&lt;E&gt;]</c> union of the remaining cases.</summary>
    /// <param name="handler">Builds the recovery from the case.</param>
    /// <returns>An effect whose error is the rest of the union.</returns>
    public Effect<T, F, R> Catch<TCase, F>(Func<TCase, Effect<T, F, R>> handler) where F : IUnionRest<F, E, TCase>
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Then<T, F, Func<TCase, Effect<T, F, R>>>(handler, static (outcome, env, context, handle) =>
        {
            if (outcome.Kind != OutcomeKind.Err) return new(outcome.WithErrorType<F>());
            if (!F.TrySplit(outcome.Error, out var caught, out var rest)) return new(outcome.WithError<T, F>(rest));
            UnobservedFailures.ReportDefects(outcome.Suppressed);
            return handle(caught).Run(env, context);
        });
    }

    /// <summary>Widens the error to a union that has it as a case.</summary>
    /// <returns>An effect with the union as its error.</returns>
    public Effect<T, F, R> Widen<F>() where F : IUnionCase<F, E> => MapErr(static error => F.From(error));

    /// <summary>Runs with an environment built from a wider one.</summary>
    /// <param name="build">Builds this effect's environment from the outer one.</param>
    /// <returns>An effect for the outer environment.</returns>
    public Effect<T, E, R2> Provide<R2>(Func<R2, R> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        var effect = Validate();
        return new((outer, context) =>
        {
            R inner;
            try
            {
                inner = build(outer);
            }
            catch (Exception exception)
            {
                return new(Defects.From<T, E>(exception, context));
            }

            return effect.Run(inner, context);
        });
    }

    /// <summary>Runs with an environment that provides at least what this effect needs.</summary>
    /// <returns>An effect for the wider environment.</returns>
    public Effect<T, E, R2> WidenEnv<R2>() where R2 : R
    {
        var effect = Validate();
        if (effect.constant.HasValue) return new(effect.constant);
        return Deferred(effect);

        static Effect<T, E, R2> Deferred(Effect<T, E, R> effect)
            => new((env, context) => effect.Run(env, context));
    }

    /// <summary>A resource running the effect as a fiber, canceled and awaited on release (in a Gen block, use <see cref="Scope{E, R}.Fork{T, F}"/>).</summary>
    /// <returns>The resource of the running fiber.</returns>
    public Resource<Fiber<T, E, R>, Never, R> Fork() => Fiber<T, E, R>.Fork(this);

    /// <summary>Starts the effect on the thread pool, so CPU-bound work does not hold up the caller.</summary>
    /// <returns>The same effect, started on the thread pool.</returns>
    public Effect<T, E, R> OnThreadPool()
    {
        var effect = Validate();
        return new((env, context) => new(Task.Run(async () =>
        {
            try
            {
                return await effect.Run(env, context).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                return Defects.From<T, E>(exception, context);
            }
        })));
    }

    /// <summary>Continues with this effect's outcome; exceptions from <paramref name="next"/> are defects.</summary>
    internal Effect<U, F, R> Then<U, F>(Func<Outcome<T, E>, R, RunContext, ValueTask<Outcome<U, F>>> next)
    {
        var effect = Validate();
        return new((env, context) =>
        {
            var task = effect.Run(env, context);
            return task.IsCompletedSuccessfully ? Continue(next, task.Result, env, context) : Await(task, next, env, context);
        });

        static async ValueTask<Outcome<U, F>> Await(
            ValueTask<Outcome<T, E>> task, Func<Outcome<T, E>, R, RunContext, ValueTask<Outcome<U, F>>> next, R env, RunContext context)
            => await Continue(next, await task.ConfigureAwait(false), env, context).ConfigureAwait(false);

        static ValueTask<Outcome<U, F>> Continue(
            Func<Outcome<T, E>, R, RunContext, ValueTask<Outcome<U, F>>> next, Outcome<T, E> outcome, R env, RunContext context)
        {
            try
            {
                return next(outcome, env, context);
            }
            catch (Exception exception)
            {
                return new(Defects.From<U, F>(exception, context).Replacing(outcome));
            }
        }
    }

    /// <summary>Continues with query-owned state.</summary>
    internal Effect<U, F, R> Then<U, F, TState>(TState state,
        Func<Outcome<T, E>, R, RunContext, TState, ValueTask<Outcome<U, F>>> next)
    {
        var effect = Validate();
        return new((env, context) =>
        {
            var task = effect.Run(env, context);
            return task.IsCompletedSuccessfully ? Continue(task.Result, env, context) : Await(task, env, context);
        });

        // The query owns state, so a suspended run needs only a reference to it.
        async ValueTask<Outcome<U, F>> Await(ValueTask<Outcome<T, E>> task, R env, RunContext context)
            => await Continue(await task.ConfigureAwait(false), env, context).ConfigureAwait(false);

        ValueTask<Outcome<U, F>> Continue(Outcome<T, E> outcome, R env, RunContext context)
        {
            try
            {
                return next(outcome, env, context, state);
            }
            catch (Exception exception)
            {
                return new(Defects.From<U, F>(exception, context).Replacing(outcome));
            }
        }
    }

    private static Outcome<T, Never> Recover<TState>(Outcome<T, E> outcome, Func<TState, E, T> fallback, TState state) => outcome.Kind switch
    {
        OutcomeKind.Ok => Outcome<T, Never>.Ok(outcome.Value).After(outcome),
        OutcomeKind.Err => Handled(outcome, fallback(state, outcome.Error)),
        _ => outcome.WithTypes<T, Never>(),
    };

    // A handled Err drops its suppressed failures, but their defects must still be reported.
    private static Outcome<U, Never> Handled<U>(Outcome<T, E> outcome, U value)
    {
        UnobservedFailures.ReportDefects(outcome.Suppressed);
        return Outcome<U, Never>.Ok(value);
    }
}
