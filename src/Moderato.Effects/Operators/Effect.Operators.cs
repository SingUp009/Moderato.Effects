using Moderato.Effects.Internal;
using Moderato.Results;

namespace Moderato.Effects;

public readonly partial struct Effect<T, E, R>
{
    public Effect<U, E, R> Map<U>(Func<T, U> mapper)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        return Then<U, E, Func<T, U>>(mapper, static (outcome, _, _, map) => new(outcome.Kind == OutcomeKind.Ok
            ? Outcome<U, E>.Ok(map(outcome.Value)).After(outcome)
            : outcome.WithValueType<U>()));
    }

    public Effect<T, F, R> MapErr<F>(Func<E, F> mapper)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        return Then<T, F, Func<E, F>>(mapper, static (outcome, _, _, map) => new(outcome.Kind == OutcomeKind.Err
            ? outcome.WithError<T, F>(map(outcome.Error))
            : outcome.WithErrorType<F>()));
    }

    public Effect<U, E, R> AndThen<U>(Func<T, Effect<U, E, R>> binder)
    {
        ArgumentNullException.ThrowIfNull(binder);
        return new(new AndThenInstruction<T, U, E, R>(Validate(), binder));
    }

    /// <summary>Runs a check on the value and keeps the value if the check succeeds.</summary>
    public Effect<T, E, R> AndThrough<U>(Func<T, Effect<U, E, R>> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        return Bind<U, E, T, E, Func<T, Effect<U, E, R>>>(check, static (outcome, checkStep, _) => outcome.Kind == OutcomeKind.Ok
            ? new(checkStep(outcome.Value)) : new(outcome), static (next, previous, _, _) => (next.Kind == OutcomeKind.Ok
                ? Outcome<T, E>.Ok(previous.Value).After(next)
                : next.WithValueType<T>()).After(previous));
    }

    /// <summary>Recovers from the error with the next effect.</summary>
    public Effect<T, F, R> OrElse<F>(Func<E, Effect<T, F, R>> binder)
    {
        ArgumentNullException.ThrowIfNull(binder);
        return Bind<T, F, T, F, Func<E, Effect<T, F, R>>>(binder, static (outcome, recover, context) =>
        {
            if (outcome.Kind != OutcomeKind.Err) return new(outcome.WithErrorType<F>());
            var recovery = recover(outcome.Error).Validate();
            UnobservedFailures.ReportDefects(outcome.Suppressed, context);
            return new(recovery);
        }, static (next, _, _, _) => next);
    }

    public Effect<T, E, R> Tap(Action<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Then<T, E, Action<T>>(action, static (outcome, _, _, tap) =>
        {
            if (outcome.Kind == OutcomeKind.Ok) tap(outcome.Value);
            return new(outcome);
        });
    }

    public Effect<T, E, R> TapErr(Action<E> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Then<T, E, Action<E>>(action, static (outcome, _, _, tap) =>
        {
            if (outcome.Kind == OutcomeKind.Err) tap(outcome.Error);
            return new(outcome);
        });
    }

    /// <summary>Runs an effect on the error, retaining the error if observation fails.</summary>
    /// <returns>An effect with the original outcome when observation succeeds.</returns>
    public Effect<T, E, R> TapErr<U>(Func<E, Effect<U, Never, R>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Then<T, E, Func<E, Effect<U, Never, R>>>(action, static async (outcome, environment, context, tap) =>
        {
            if (outcome.Kind != OutcomeKind.Err) return outcome;
            try
            {
                var observed = await tap(outcome.Error).Validate().Run(environment, context).ConfigureAwait(false);
                if (observed.Kind != OutcomeKind.Ok) return observed.WithTypes<T, E>().Replacing(outcome);
                var (suppressed, omitted) = SuppressedContext.Concat(outcome.Suppressed, outcome.Omitted, observed.Suppressed, observed.Omitted);
                return outcome.WithSuppressed(suppressed, omitted);
            }
            catch (Exception exception)
            {
                return Defects.From<T, E>(exception, context).Replacing(outcome);
            }
        });
    }

    public Effect<U, Never, R> Match<U>(Func<T, U> ok, Func<E, U> err)
    {
        ArgumentNullException.ThrowIfNull(ok);
        ArgumentNullException.ThrowIfNull(err);
        return Then<U, Never, (Func<T, U> ok, Func<E, U> err)>((ok, err), static (outcome, _, context, handlers) => new(outcome.Kind switch
        {
            OutcomeKind.Ok => Outcome<U, Never>.Ok(handlers.ok(outcome.Value)).After(outcome),
            OutcomeKind.Err => Handled(outcome, handlers.err(outcome.Error), context),
            _ => outcome.WithTypes<U, Never>(),
        }));
    }

    public Effect<T, Never, R> UnwrapOr(T fallback)
        => Then<T, Never, T>(fallback, static (outcome, _, context, value) => new(Recover(outcome, static (fallback, _) => fallback, value, context)));

    public Effect<T, Never, R> UnwrapOrElse(Func<E, T> fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        return Then<T, Never, Func<E, T>>(fallback, static (outcome, _, context, getValue) => new(Recover(outcome, static (fallback, error) => fallback(error), getValue, context)));
    }

    /// <summary>Makes the error a value to match on instead of short-circuiting.</summary>
    public Effect<Result<T, E>, Never, R> Attempt() => Then<Result<T, E>, Never, Unit>(Unit.Value, static (outcome, _, context, _) => new(outcome.Kind switch
    {
        OutcomeKind.Ok => Outcome<Result<T, E>, Never>.Ok(new Result<T, E>(new Ok<T>(outcome.Value))).After(outcome),
        OutcomeKind.Err => Handled(outcome, new Result<T, E>(new Err<E>(outcome.Error)), context),
        _ => outcome.WithTypes<Result<T, E>, Never>(),
    }));

    /// <summary>Handles one case and narrows the error to the <c>[RestOf&lt;E&gt;]</c> union of the remaining cases.</summary>
    public Effect<T, F, R> Catch<TCase, F>(Func<TCase, Effect<T, F, R>> handler) where F : IUnionRest<F, E, TCase>
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Bind<T, F, T, F, Func<TCase, Effect<T, F, R>>>(handler, static (outcome, handle, context) =>
        {
            if (outcome.Kind != OutcomeKind.Err) return new(outcome.WithErrorType<F>());
            if (!F.TrySplit(outcome.Error, out var caught, out var rest)) return new(outcome.WithError<T, F>(rest));
            var recovery = handle(caught).Validate();
            UnobservedFailures.ReportDefects(outcome.Suppressed, context);
            return new(recovery);
        }, static (next, _, _, _) => next);
    }

    /// <summary>Widens the error to a union that has it as a case.</summary>
    public Effect<T, F, R> Widen<F>() where F : IUnionCase<F, E> => MapErr(static error => F.From(error));

    /// <summary>Runs with an environment built from a wider one.</summary>
    public Effect<T, E, R2> Provide<R2>(Func<R2, R> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        return new(new ProvideInstruction<T, E, R, R2>(Validate(), build));
    }

    /// <summary>Runs with an environment that provides at least what this effect needs.</summary>
    public Effect<T, E, R2> WidenEnv<R2>() where R2 : R
    {
        var effect = Validate();
        if (effect.constant.HasValue) return new(effect.constant, effect.trackingName, effect.trackingLocation);
        return Deferred(effect);

        static Effect<T, E, R2> Deferred(Effect<T, E, R> effect)
            => new(new ProvideInstruction<T, E, R, R2>(effect, static environment => environment));
    }

    /// <summary>A resource running the effect as a fiber, canceled and awaited on release (in a Gen block, use <see cref="Scope{E, R}.Fork{T, F}"/>).</summary>
    public Resource<Fiber<T, E, R>, Never, R> Fork() => Fiber<T, E, R>.Fork(this);

    /// <summary>Starts the effect on the thread pool, so CPU-bound work does not hold up the caller.</summary>
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

    private static Outcome<T, Never> Recover<TState>(Outcome<T, E> outcome, Func<TState, E, T> fallback, TState state, RunContext context) => outcome.Kind switch
    {
        OutcomeKind.Ok => Outcome<T, Never>.Ok(outcome.Value).After(outcome),
        OutcomeKind.Err => Handled(outcome, fallback(state, outcome.Error), context),
        _ => outcome.WithTypes<T, Never>(),
    };

    // A handled Err drops its suppressed failures, but their defects must still be reported.
    private static Outcome<U, Never> Handled<U>(Outcome<T, E> outcome, U value, RunContext context)
    {
        UnobservedFailures.ReportDefects(outcome.Suppressed, context);
        return Outcome<U, Never>.Ok(value);
    }
}
