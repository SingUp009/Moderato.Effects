using Moderato.Effects.Internal;
using Moderato.Results;

namespace Moderato.Effects;

/// <summary>Creates effects for the environment <typeparamref name="R"/>.</summary>
/// <typeparam name="R">The environment the effects run with.</typeparam>
public static class Effect<R>
{
    /// <summary>An effect that succeeds with the value. The value is computed once, when this is called.</summary>
    /// <param name="value">The value.</param>
    /// <returns>An effect that cannot fail.</returns>
    public static Effect<T, Never, R> Succeed<T>(T value)
    {
        var outcome = Outcome<T, Never>.Ok(value);
        return new((_, _) => new(outcome));
    }

    /// <summary>An effect that fails with the error. Name the error type, as in <c>Fail&lt;AppError&gt;(new NotFound(id))</c>, so it converts to the effect you return.</summary>
    /// <param name="error">The expected error.</param>
    /// <returns>An effect that cannot succeed.</returns>
    public static Effect<Never, E, R> Fail<E>(E error)
    {
        var outcome = Outcome<Never, E>.Err(error);
        return new((_, _) => new(outcome));
    }

    /// <summary>An effect that ends in the result.</summary>
    /// <param name="result">The result.</param>
    /// <returns>An effect with the result's value and error.</returns>
    /// <exception cref="InvalidOperationException">The result is <c>default</c>.</exception>
    public static Effect<T, E, R> FromResult<T, E>(Result<T, E> result)
    {
        if (!result.HasValue) ThrowHelper.ThrowUninitializedResult();
        var outcome = Steps.FromResult(result);
        return new((_, _) => new(outcome));
    }

    /// <summary>An effect that runs synchronous code each time it runs. Exceptions are defects.</summary>
    /// <param name="body">The code.</param>
    /// <returns>An effect that cannot fail.</returns>
    public static Effect<T, Never, R> Sync<T>(Func<T> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return new((_, context) => Steps.Run<T, Never>(body, context));
    }

    /// <summary>An effect that runs asynchronous code each time it runs. Exceptions are defects.</summary>
    /// <param name="body">The code. Receives the run's cancellation token.</param>
    /// <returns>An effect that cannot fail.</returns>
    public static Effect<T, Never, R> Async<T>(Func<CancellationToken, ValueTask<T>> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return new((_, context) => Steps.RunAsync<T, Never>(body, context));
    }

    /// <summary>An effect that runs asynchronous code returning a Result each time it runs. Exceptions are defects.</summary>
    /// <param name="body">The code. Receives the run's cancellation token.</param>
    /// <returns>An effect with the result's value and error.</returns>
    public static Effect<T, E, R> FromResultAsync<T, E>(Func<CancellationToken, ValueTask<Result<T, E>>> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return new((_, context) =>
        {
            var task = Steps.RunAsync<Result<T, E>, E>(body, context);
            return Steps.Then(task, static outcome => outcome.Kind == OutcomeKind.Ok ? Steps.FromResult(outcome.Value) : outcome.WithValueType<T>());
        });
    }

    /// <summary>A typed try/catch: exceptions of <typeparamref name="TException"/> become the error, anything else is a defect. Write the handler as <c>(JsonException ex) =&gt; ...</c>.</summary>
    /// <param name="body">The code.</param>
    /// <param name="map">Turns a caught exception into the error.</param>
    /// <returns>An effect that fails with the mapped error.</returns>
    public static Effect<T, E, R> Try<TException, T, E>(Func<T> body, Func<TException, E> map)
        where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(map);
        return new((_, context) => new(Catching.Map(Steps.Run<T, E>(body, context).Result, map, context)));
    }

    /// <summary>A try/catch that turns any exception into the error, like <c>Result.Try</c>.</summary>
    /// <param name="body">The code.</param>
    /// <param name="map">Turns a caught exception into the error.</param>
    /// <returns>An effect that fails with the mapped error.</returns>
    public static Effect<T, E, R> Try<T, E>(Func<T> body, Func<Exception, E> map) => Try<Exception, T, E>(body, map);

    /// <summary>The asynchronous form of <see cref="Try{TException, T, E}(Func{T}, Func{TException, E})"/>.</summary>
    /// <param name="body">The code. Receives the run's cancellation token.</param>
    /// <param name="map">Turns a caught exception into the error.</param>
    /// <returns>An effect that fails with the mapped error.</returns>
    public static Effect<T, E, R> TryAsync<TException, T, E>(Func<CancellationToken, ValueTask<T>> body, Func<TException, E> map)
        where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(map);
        return new((_, context) => Steps.Then(Steps.RunAsync<T, E>(body, context), outcome => Catching.Map(outcome, map, context)));
    }

    /// <summary>The asynchronous form of <see cref="Try{T, E}(Func{T}, Func{Exception, E})"/>.</summary>
    /// <param name="body">The code. Receives the run's cancellation token.</param>
    /// <param name="map">Turns a caught exception into the error.</param>
    /// <returns>An effect that fails with the mapped error.</returns>
    public static Effect<T, E, R> TryAsync<T, E>(Func<CancellationToken, ValueTask<T>> body, Func<Exception, E> map)
        => TryAsync<Exception, T, E>(body, map);

    /// <summary>An effect that reads from the environment each time it runs. Exceptions are defects.</summary>
    /// <param name="read">Reads from the environment.</param>
    /// <returns>An effect that cannot fail.</returns>
    public static Effect<T, Never, R> Access<T>(Func<R, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        return new((env, context) => Steps.Run<T, Never>(() => read(env), context));
    }

    /// <summary>An effect that runs asynchronous code against the environment each time it runs. Exceptions are defects.</summary>
    /// <param name="read">The code. Receives the environment and the run's cancellation token.</param>
    /// <returns>An effect that cannot fail.</returns>
    public static Effect<T, Never, R> AccessAsync<T>(Func<R, CancellationToken, ValueTask<T>> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        return new((env, context) => Steps.RunAsync<T, Never>(ct => read(env, ct), context));
    }

    /// <summary>The environment itself.</summary>
    public static Effect<R, Never, R> Env { get; } = new((env, _) => new(Outcome<R, Never>.Ok(env)));
}

file static class Catching
{
    /// <summary>Turns a defect of the caught type into the error. The run's own cancellation is an interruption, not a defect, so it is never caught.</summary>
    internal static Outcome<T, E> Map<TException, T, E>(Outcome<T, E> outcome, Func<TException, E> map, RunContext context)
        where TException : Exception
    {
        if (outcome.Kind != OutcomeKind.Die || outcome.Defect!.SourceException is not TException caught) return outcome;
        // ScopeFailure is a Gen block's control flow, so catching it would replace the block's Err.
        if (caught is ScopeFailure) return outcome;

        try
        {
            return Outcome<T, E>.Err(map(caught));
        }
        catch (Exception exception)
        {
            // The mapper threw while handling the defect, which stays as earlier context.
            return Defects.From<T, E>(exception, context)
                .After([new Suppressed(ExitCases.Die(outcome.Defect!), Relation.Sequential), .. outcome.Suppressed], outcome.Omitted);
        }
    }
}
