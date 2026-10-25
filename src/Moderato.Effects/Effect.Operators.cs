using Moderato.Effects.Internal;
using Moderato.Results;

namespace Moderato.Effects;

public readonly partial struct Effect<T, E, R>
{
    /// <summary>Transforms the value. An Err passes through and the mapper is not called.</summary>
    /// <param name="mapper">Transforms the value.</param>
    /// <returns>An effect with the transformed value.</returns>
    public Effect<U, E, R> Map<U>(Func<T, U> mapper)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        return Then<U, E>((outcome, _, _) => new(outcome.Kind == OutcomeKind.Ok
            ? Outcome<U, E>.Ok(mapper(outcome.Value)).After(outcome)
            : outcome.WithValueType<U>()));
    }

    /// <summary>Transforms the error. An Ok passes through and the mapper is not called.</summary>
    /// <param name="mapper">Transforms the error.</param>
    /// <returns>An effect with the transformed error.</returns>
    public Effect<T, F, R> MapErr<F>(Func<E, F> mapper)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        return Then<T, F>((outcome, _, _) => new(outcome.Kind == OutcomeKind.Err
            ? Outcome<T, F>.Err(mapper(outcome.Error)).After(outcome)
            : outcome.WithErrorType<F>()));
    }

    /// <summary>Continues with the next effect. On an Err the binder is not called.</summary>
    /// <param name="binder">Builds the next effect from the value.</param>
    /// <returns>The next effect's outcome.</returns>
    public Effect<U, E, R> AndThen<U>(Func<T, Effect<U, E, R>> binder)
    {
        ArgumentNullException.ThrowIfNull(binder);
        return Then<U, E>((outcome, env, context) => outcome.Kind == OutcomeKind.Ok
            ? Steps.Then(binder(outcome.Value).Run(env, context), next => next.After(outcome))
            : new(outcome.WithValueType<U>()));
    }

    /// <summary>Runs a check on the value and keeps the value if the check succeeds.</summary>
    /// <param name="check">Builds the check from the value.</param>
    /// <returns>An effect with the original value, or the check's failure.</returns>
    public Effect<T, E, R> AndThrough<U>(Func<T, Effect<U, E, R>> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        return Then<T, E>((outcome, env, context) => outcome.Kind == OutcomeKind.Ok
            ? Steps.Then(check(outcome.Value).Run(env, context), next => (next.Kind == OutcomeKind.Ok
                ? Outcome<T, E>.Ok(outcome.Value).After(next)
                : next.WithValueType<T>()).After(outcome))
            : new(outcome));
    }

    /// <summary>Recovers from the error with the next effect. On an Ok the binder is not called.</summary>
    /// <param name="binder">Builds the recovery from the error.</param>
    /// <returns>The recovery's outcome.</returns>
    public Effect<T, F, R> OrElse<F>(Func<E, Effect<T, F, R>> binder)
    {
        ArgumentNullException.ThrowIfNull(binder);
        return Then<T, F>((outcome, env, context) =>
        {
            if (outcome.Kind != OutcomeKind.Err) return new(outcome.WithErrorType<F>());
            UnobservedFailures.ReportDefects(outcome.Suppressed);
            return binder(outcome.Error).Run(env, context);
        });
    }

    /// <summary>Runs a side effect on the value.</summary>
    /// <param name="action">The side effect.</param>
    /// <returns>An effect with the same outcome.</returns>
    public Effect<T, E, R> Tap(Action<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Then<T, E>((outcome, _, _) =>
        {
            if (outcome.Kind == OutcomeKind.Ok) action(outcome.Value);
            return new(outcome);
        });
    }

    /// <summary>Runs a side effect on the error.</summary>
    /// <param name="action">The side effect.</param>
    /// <returns>An effect with the same outcome.</returns>
    public Effect<T, E, R> TapErr(Action<E> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Then<T, E>((outcome, _, _) =>
        {
            if (outcome.Kind == OutcomeKind.Err) action(outcome.Error);
            return new(outcome);
        });
    }

    /// <summary>Folds the value and the error into one value. Nothing runs until the effect does.</summary>
    /// <param name="ok">Folds the value.</param>
    /// <param name="err">Folds the error.</param>
    /// <returns>An effect that cannot fail.</returns>
    public Effect<U, Never, R> Match<U>(Func<T, U> ok, Func<E, U> err)
    {
        ArgumentNullException.ThrowIfNull(ok);
        ArgumentNullException.ThrowIfNull(err);
        return Then<U, Never>((outcome, _, _) => new(outcome.Kind switch
        {
            OutcomeKind.Ok => Outcome<U, Never>.Ok(ok(outcome.Value)).After(outcome),
            OutcomeKind.Err => Handled(outcome, err(outcome.Error)),
            _ => outcome.WithTypes<U, Never>(),
        }));
    }

    /// <summary>Replaces the error with a fallback value.</summary>
    /// <param name="fallback">The value to use on an Err.</param>
    /// <returns>An effect that cannot fail.</returns>
    public Effect<T, Never, R> UnwrapOr(T fallback)
        => Then<T, Never>((outcome, _, _) => new(Recover(outcome, static (fallback, _) => fallback, fallback)));

    /// <summary>Replaces the error with a value built from it.</summary>
    /// <param name="fallback">Builds the value from the error.</param>
    /// <returns>An effect that cannot fail.</returns>
    public Effect<T, Never, R> UnwrapOrElse(Func<E, T> fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        return Then<T, Never>((outcome, _, _) => new(Recover(outcome, static (fallback, error) => fallback(error), fallback)));
    }

    /// <summary>Turns the error into a value, so it can be matched instead of short-circuiting.</summary>
    /// <returns>An effect that cannot fail, whose value is the result.</returns>
    public Effect<Result<T, E>, Never, R> Attempt() => Then<Result<T, E>, Never>(static (outcome, _, _) => new(outcome.Kind switch
    {
        OutcomeKind.Ok => Outcome<Result<T, E>, Never>.Ok(new Result<T, E>(new Ok<T>(outcome.Value))).After(outcome),
        OutcomeKind.Err => Handled(outcome, new Result<T, E>(new Err<E>(outcome.Error))),
        _ => outcome.WithTypes<Result<T, E>, Never>(),
    }));

    /// <summary>Widens the error to a union that has it as a case.</summary>
    /// <returns>An effect with the union as its error.</returns>
    public Effect<T, F, R> Widen<F>() where F : IUnionCase<F, E> => MapErr(static error => F.From(error));

    /// <summary>Runs with an environment built from a wider one.</summary>
    /// <param name="build">Builds this effect's environment from the outer one.</param>
    /// <returns>An effect for the outer environment.</returns>
    public Effect<T, E, R2> Provide<R2>(Func<R2, R> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        var run = Run;
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

            return run(inner, context);
        });
    }

    /// <summary>Runs with an environment that provides at least what this effect needs.</summary>
    /// <returns>An effect for the wider environment.</returns>
    public Effect<T, E, R2> WidenEnv<R2>() where R2 : R
    {
        var run = Run;
        return new((env, context) => run(env, context));
    }

    /// <summary>Continues with this effect's outcome. Exceptions from <paramref name="next"/> are defects.</summary>
    internal Effect<U, F, R> Then<U, F>(Func<Outcome<T, E>, R, RunContext, ValueTask<Outcome<U, F>>> next)
    {
        var run = Run;
        return new((env, context) =>
        {
            var task = run(env, context);
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
                return new(Defects.From<U, F>(exception, context).After(outcome));
            }
        }
    }

    private static Outcome<T, Never> Recover<TState>(Outcome<T, E> outcome, Func<TState, E, T> fallback, TState state) => outcome.Kind switch
    {
        OutcomeKind.Ok => Outcome<T, Never>.Ok(outcome.Value).After(outcome),
        OutcomeKind.Err => Handled(outcome, fallback(state, outcome.Error)),
        _ => outcome.WithTypes<T, Never>(),
    };

    // A handled Err drops its suppressed failures with it; defects among them must still be seen.
    private static Outcome<U, Never> Handled<U>(Outcome<T, E> outcome, U value)
    {
        UnobservedFailures.ReportDefects(outcome.Suppressed);
        return Outcome<U, Never>.Ok(value);
    }
}
