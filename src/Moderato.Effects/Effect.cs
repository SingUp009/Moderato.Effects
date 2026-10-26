using System.Collections.Immutable;
using System.Diagnostics;
using Moderato.Effects.Internal;
using Moderato.Results;

namespace Moderato.Effects;

/// <summary>
/// A lazy description of work that needs an environment <typeparamref name="R"/>, succeeds with <typeparamref name="T"/>
/// or fails with <typeparamref name="E"/>. Nothing runs until <see cref="RunAsync"/>; every run starts over. <c>default</c> is invalid.
/// </summary>
[DebuggerDisplay("Effect<{typeof(T).Name,nq}, {typeof(E).Name,nq}, {typeof(R).Name,nq}>")]
public readonly partial struct Effect<T, E, R>
{
    private readonly Func<R, RunContext, ValueTask<Outcome<T, E>>>? run;

    internal Effect(Func<R, RunContext, ValueTask<Outcome<T, E>>> run) => this.run = run;

    internal Func<R, RunContext, ValueTask<Outcome<T, E>>> Run
        => run ?? ThrowHelper.ThrowUninitializedEffect<Func<R, RunContext, ValueTask<Outcome<T, E>>>>();

    /// <summary>An effect that cannot fail can stand in for one with any error type.</summary>
    public static implicit operator Effect<T, E, R>(Effect<T, Never, R> effect)
    {
        var run = effect.Run;
        return new((env, context) => Steps.Then(run(env, context), static outcome => outcome.WithErrorType<E>()));
    }

    /// <summary>An effect that cannot succeed can stand in for one with any value type.</summary>
    public static implicit operator Effect<T, E, R>(Effect<Never, E, R> effect)
    {
        var run = effect.Run;
        return new((env, context) => Steps.Then(run(env, context), static outcome => outcome.WithValueType<T>()));
    }

    /// <summary>An effect that ends in the result.</summary>
    public static implicit operator Effect<T, E, R>(Result<T, E> result) => Effect<R>.FromResult(result);

    /// <summary>Runs the effect. Defects are rethrown as they were thrown; the caller's cancellation surfaces as <see cref="OperationCanceledException"/>.</summary>
    /// <param name="environment">The services the effect needs.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The value, or the expected error.</returns>
    public ValueTask<Result<T, E>> RunAsync(R environment, CancellationToken cancellationToken = default)
    {
        var run = Run;
        if (cancellationToken.IsCancellationRequested) return ValueTask.FromCanceled<Result<T, E>>(cancellationToken);

        var task = run(environment, new RunContext(cancellationToken));
        return task.IsCompletedSuccessfully ? ToResult(task.Result, cancellationToken) : Await(task, cancellationToken);

        static async ValueTask<Result<T, E>> Await(ValueTask<Outcome<T, E>> task, CancellationToken cancellationToken)
            => await ToResult(await task.ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs the effect and returns how it ended, without throwing.</summary>
    /// <param name="environment">The services the effect needs.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>Ok, Err, Die or Interrupted.</returns>
    public ValueTask<Exit<T, E>> RunExitAsync(R environment, CancellationToken cancellationToken = default)
    {
        var run = Run;
        if (cancellationToken.IsCancellationRequested) return new(new Exit<T, E>(new Interrupted()));

        var task = run(environment, new RunContext(cancellationToken));
        return task.IsCompletedSuccessfully ? new(ToExit(task.Result)) : Await(task);

        static async ValueTask<Exit<T, E>> Await(ValueTask<Outcome<T, E>> task) => ToExit(await task.ConfigureAwait(false));
    }

    private static ValueTask<Result<T, E>> ToResult(Outcome<T, E> outcome, CancellationToken cancellationToken)
    {
        switch (outcome.Kind)
        {
            case OutcomeKind.Ok:
                // Results' Ok and Err have nowhere to carry suppressed failures, so defects among them would vanish silently.
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
                return ValueTask.FromCanceled<Result<T, E>>(cancellationToken);
        }
    }

    private static Exit<T, E> ToExit(Outcome<T, E> outcome) => Exits.From(outcome);

    // One key, overwritten: an exception thrown again by a later run must not accumulate stale entries.
    private static void AttachSuppressed(Exception exception, ImmutableArray<Suppressed> suppressed)
    {
        if (suppressed.IsEmpty) exception.Data.Remove(SUPPRESSED_DATA_KEY);
        else exception.Data[SUPPRESSED_DATA_KEY] = suppressed;
    }

    /// <summary>The <see cref="Exception.Data"/> key under which <see cref="RunAsync"/> attaches suppressed failures to a rethrown defect.</summary>
    public const string SUPPRESSED_DATA_KEY = "Moderato.Effects.Suppressed";
}
