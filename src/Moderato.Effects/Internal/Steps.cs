using Moderato.Results;

namespace Moderato.Effects.Internal;

/// <summary>Bridges user code and outcomes, staying synchronous while the work does.</summary>
internal static class Steps
{
    /// <summary>Continues with the outcome of a step.</summary>
    internal static ValueTask<Outcome<C, D>> Then<A, B, C, D>(ValueTask<Outcome<A, B>> task, Func<Outcome<A, B>, Outcome<C, D>> next)
    {
        return task.IsCompletedSuccessfully ? new(next(task.Result)) : Await(task, next);

        static async ValueTask<Outcome<C, D>> Await(ValueTask<Outcome<A, B>> task, Func<Outcome<A, B>, Outcome<C, D>> next)
            => next(await task.ConfigureAwait(false));
    }

    /// <summary>Continues with explicit state, avoiding a per-run closure.</summary>
    internal static ValueTask<Outcome<C, D>> Then<A, B, C, D, TState>(
        ValueTask<Outcome<A, B>> task, TState state, Func<Outcome<A, B>, TState, Outcome<C, D>> next)
    {
        return task.IsCompletedSuccessfully ? new(next(task.Result, state)) : Await(task, state, next);

        static async ValueTask<Outcome<C, D>> Await(
            ValueTask<Outcome<A, B>> task, TState state, Func<Outcome<A, B>, TState, Outcome<C, D>> next)
            => next(await task.ConfigureAwait(false), state);
    }

    /// <summary>Runs synchronous user code; exceptions become outcomes.</summary>
    internal static ValueTask<Outcome<T, E>> Run<T, E>(Func<T> body, RunContext context)
    {
        try
        {
            return new(Outcome<T, E>.Ok(body()));
        }
        catch (Exception exception)
        {
            return new(Defects.From<T, E>(exception, context));
        }
    }

    /// <summary>Runs synchronous user code with explicit state.</summary>
    internal static ValueTask<Outcome<T, E>> Run<T, E, TState>(Func<TState, T> body, TState state, RunContext context)
    {
        try
        {
            return new(Outcome<T, E>.Ok(body(state)));
        }
        catch (Exception exception)
        {
            return new(Defects.From<T, E>(exception, context));
        }
    }

    /// <summary>Runs asynchronous user code; exceptions become outcomes.</summary>
    internal static ValueTask<Outcome<T, E>> RunAsync<T, E>(Func<CancellationToken, ValueTask<T>> body, RunContext context)
    {
        ValueTask<T> task;
        try
        {
            task = body(context.CancellationToken);
        }
        catch (Exception exception)
        {
            return new(Defects.From<T, E>(exception, context));
        }

        return task.IsCompletedSuccessfully ? new(Outcome<T, E>.Ok(task.Result)) : Await<T, E>(task, context);
    }

    /// <summary>Runs asynchronous user code with explicit state.</summary>
    internal static ValueTask<Outcome<T, E>> RunAsync<T, E, TState>(
        Func<TState, CancellationToken, ValueTask<T>> body, TState state, RunContext context)
    {
        ValueTask<T> task;
        try
        {
            task = body(state, context.CancellationToken);
        }
        catch (Exception exception)
        {
            return new(Defects.From<T, E>(exception, context));
        }

        return task.IsCompletedSuccessfully ? new(Outcome<T, E>.Ok(task.Result)) : Await<T, E>(task, context);
    }

    private static async ValueTask<Outcome<T, E>> Await<T, E>(ValueTask<T> task, RunContext context)
    {
        try
        {
            return Outcome<T, E>.Ok(await task.ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            return Defects.From<T, E>(exception, context);
        }
    }

    /// <summary>Runs asynchronous Result-producing code directly into an outcome.</summary>
    internal static ValueTask<Outcome<T, E>> RunResultAsync<T, E>(Func<CancellationToken, ValueTask<Result<T, E>>> body, RunContext context)
    {
        ValueTask<Result<T, E>> task;
        try
        {
            task = body(context.CancellationToken);
        }
        catch (Exception exception)
        {
            return new(Defects.From<T, E>(exception, context));
        }

        return task.IsCompletedSuccessfully ? new(FromResult(task.Result)) : Await(task, context);

        static async ValueTask<Outcome<T, E>> Await(ValueTask<Result<T, E>> task, RunContext context)
        {
            Result<T, E> result;
            try
            {
                result = await task.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                return Defects.From<T, E>(exception, context);
            }

            return FromResult(result);
        }
    }

    /// <summary>Converts a Result into an outcome; a default Result is a defect.</summary>
    internal static Outcome<T, E> FromResult<T, E>(Result<T, E> result)
    {
        if (result.TryGetOk(out var value)) return Outcome<T, E>.Ok(value);
        if (result.TryGetErr(out var error)) return Outcome<T, E>.Err(error);
        return Outcome<T, E>.Die(System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ThrowHelper.UninitializedResultException()));
    }
}
