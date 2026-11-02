using System.Collections.Immutable;
using System.Diagnostics;
using Moderato.Effects.Internal;
using Moderato.Results;

namespace Moderato.Effects;

/// <summary>A lazy, rerunnable description of work that needs <typeparamref name="R"/> and ends with <typeparamref name="T"/> or <typeparamref name="E"/>; <c>default</c> is invalid.</summary>
/// <remarks>Equality and hash codes depend on internal representation and do not establish equivalent computations.</remarks>
[DebuggerDisplay("Effect<{typeof(T).Name,nq}, {typeof(E).Name,nq}, {typeof(R).Name,nq}>")]
public readonly partial struct Effect<T, E, R>
{
    private readonly Result<T, E> constant;
    private readonly Func<R, RunContext, ValueTask<Outcome<T, E>>>? run;

    internal Effect(Func<R, RunContext, ValueTask<Outcome<T, E>>> run) => this.run = run;

    internal Effect(Result<T, E> constant) => this.constant = constant;

    internal Effect<T, E, R> Validate()
        => constant.HasValue || run is not null ? this : ThrowHelper.ThrowUninitializedEffect<Effect<T, E, R>>();

    internal ValueTask<Outcome<T, E>> Run(R environment, RunContext context)
        => constant.HasValue ? new(Steps.FromResult(constant, context))
            : (run ?? ThrowHelper.ThrowUninitializedEffect<Func<R, RunContext, ValueTask<Outcome<T, E>>>>())(environment, context);

    /// <summary>An effect that cannot fail can stand in for one with any error type.</summary>
    public static implicit operator Effect<T, E, R>(Effect<T, Never, R> effect)
    {
        effect = effect.Validate();
        if (effect.constant.TryGetOk(out var value)) return new(new Result<T, E>(new Ok<T>(value)));
        return Deferred(effect);

        static Effect<T, E, R> Deferred(Effect<T, Never, R> effect)
            => new((env, context) => Steps.Then(effect.Run(env, context), static outcome => outcome.WithErrorType<E>()));
    }

    /// <summary>An effect that cannot succeed can stand in for one with any value type.</summary>
    public static implicit operator Effect<T, E, R>(Effect<Never, E, R> effect)
    {
        effect = effect.Validate();
        if (effect.constant.TryGetErr(out var error)) return new(new Result<T, E>(new Err<E>(error)));
        return Deferred(effect);

        static Effect<T, E, R> Deferred(Effect<Never, E, R> effect)
            => new((env, context) => Steps.Then(effect.Run(env, context), static outcome => outcome.WithValueType<T>()));
    }

    /// <summary>An effect that ends in the result.</summary>
    public static implicit operator Effect<T, E, R>(Result<T, E> result) => Effect<R>.FromResult(result);

    /// <summary>Runs the effect, rethrowing defects as thrown and the caller's cancellation as <see cref="OperationCanceledException"/>.</summary>
    /// <param name="environment">The services the effect needs.</param>
    /// <param name="cancellationToken">Cancels the run, which sees a token derived from it.</param>
    /// <returns>The value, or the expected error.</returns>
    public ValueTask<Result<T, E>> RunAsync(R environment, CancellationToken cancellationToken = default)
    {
        Validate();
        if (cancellationToken.IsCancellationRequested) return ValueTask.FromCanceled<Result<T, E>>(cancellationToken);
        if (constant.HasValue) return new(constant);

        try
        {
            var task = Start(environment, cancellationToken);
            return task.IsCompletedSuccessfully ? ToResult(task.Result, cancellationToken) : Await(task, cancellationToken);
        }
        catch (Exception exception)
        {
            return ToResult(Defects.From<T, E>(exception, new RunContext(cancellationToken)), cancellationToken);
        }

        static async ValueTask<Result<T, E>> Await(ValueTask<Outcome<T, E>> task, CancellationToken cancellationToken)
            => await ToResult(await task.ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs the effect and returns how it ended, without throwing.</summary>
    /// <param name="environment">The services the effect needs.</param>
    /// <param name="cancellationToken">Cancels the run, which sees a token derived from it.</param>
    /// <returns>Ok, Err, Die or Interrupted.</returns>
    public ValueTask<Exit<T, E>> RunExitAsync(R environment, CancellationToken cancellationToken = default)
    {
        Validate();
        if (cancellationToken.IsCancellationRequested) return new(new Exit<T, E>(ExitCases.Interrupted));
        if (constant.HasValue) return new(ToExit(Steps.FromResult(constant)));

        try
        {
            var task = Start(environment, cancellationToken);
            return task.IsCompletedSuccessfully ? new(ToExit(task.Result)) : Await(task, cancellationToken);
        }
        catch (Exception exception)
        {
            return new(ToExit(Defects.From<T, E>(exception, new RunContext(cancellationToken))));
        }

        static async ValueTask<Exit<T, E>> Await(ValueTask<Outcome<T, E>> task, CancellationToken cancellationToken)
        {
            try
            {
                return ToExit(await task.ConfigureAwait(false));
            }
            catch (Exception exception)
            {
                return ToExit(Defects.From<T, E>(exception, new RunContext(cancellationToken)));
            }
        }
    }

    private ValueTask<Outcome<T, E>> Start(R environment, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled) return Run(environment, new RunContext(cancellationToken));
        return Isolated(this, environment, cancellationToken);

        static async ValueTask<Outcome<T, E>> Isolated(Effect<T, E, R> effect, R environment, CancellationToken cancellationToken)
        {
            using var caller = Cancellation.ForCaller(cancellationToken);
            Outcome<T, E> outcome;
            try
            {
                outcome = await effect.Run(environment, new RunContext(caller.Token)).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                outcome = Defects.From<T, E>(exception, new RunContext(caller.Token));
            }

            return caller.WithCallbackFailure(outcome);
        }
    }

    private static ValueTask<Result<T, E>> ToResult(Outcome<T, E> outcome, CancellationToken cancellationToken)
    {
        switch (outcome.Kind)
        {
            case OutcomeKind.Ok:
                // Result cannot carry suppressed failures, so their defects are reported here.
                UnobservedFailures.ReportDefects(outcome.Suppressed);
                return new(new Result<T, E>(new Ok<T>(outcome.Value)));
            case OutcomeKind.Err:
                UnobservedFailures.ReportDefects(outcome.Suppressed);
                return new(new Result<T, E>(new Err<E>(outcome.Error)));
            case OutcomeKind.Die:
                var exception = outcome.Defect!.SourceException;
                AttachSuppressed(exception, outcome.Suppressed);
                return ValueTask.FromException<Result<T, E>>(exception);
            default:
                UnobservedFailures.ReportDefects(outcome.Suppressed);
                // An inner cancellation can interrupt the run while the caller's token is not canceled.
                return ValueTask.FromCanceled<Result<T, E>>(cancellationToken.IsCancellationRequested ? cancellationToken : new CancellationToken(canceled: true));
        }
    }

    private static Exit<T, E> ToExit(Outcome<T, E> outcome) => Exits.From(outcome);

    // Overwritten, so a rethrown exception does not accumulate stale entries.
    private static void AttachSuppressed(Exception exception, ImmutableArray<Suppressed> suppressed)
    {
        if (suppressed.IsEmpty) exception.Data.Remove(SUPPRESSED_DATA_KEY);
        else exception.Data[SUPPRESSED_DATA_KEY] = suppressed;
    }

    /// <summary>The <see cref="Exception.Data"/> key holding a rethrown defect's suppressed failures.</summary>
    public const string SUPPRESSED_DATA_KEY = "Moderato.Effects.Suppressed";
}
