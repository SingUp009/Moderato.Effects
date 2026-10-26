using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks.Sources;
using Moderato.Effects.Internal;
using Moderato.Results;

namespace Moderato.Effects;

/// <summary>
/// The handle a Gen block uses to run effects. An Err ends the block, and its <c>finally</c> blocks and <c>using</c> declarations still run.
/// Use it only inside its own block, from one logical flow at a time.
/// </summary>
/// <typeparam name="E">The error the block can fail with.</typeparam>
/// <typeparam name="R">The environment the block runs with.</typeparam>
public sealed class Scope<E, R>
{
    private readonly RunContext context;
    private readonly Cancellation cancellation;
    private readonly Releases<R> releases;
    // An unawaited step records from another thread, so recording and the block's end take turns.
    private readonly Lock gate = new();
    private ImmutableArray<Suppressed>.Builder? suppressed;
    private int omitted;
    private ExceptionDispatchInfo? rethrown;
    private OperationCanceledException? interruption;
    private bool rethrownConsequential;
    private bool failed;
    private E error = default!;
    private int busy;
    private bool closed;
    private PendingStep? running;
    private List<PendingStep>? unobserved;

    private Scope(R env, RunContext context)
    {
        Env = env;
        cancellation = new Cancellation(context.CancellationToken);
        releases = new Releases<R>(cancellation);
        this.context = releases.BodyContext;
    }

    /// <summary>The environment the block runs with.</summary>
    public R Env { get; }

    /// <summary>The token the block runs with.</summary>
    public CancellationToken CancellationToken => context.CancellationToken;

    /// <summary>An effect that runs the block each time it runs.</summary>
    /// <param name="body">The block.</param>
    /// <returns>An effect whose value is what the block returns.</returns>
    public static Effect<T, E, R> Gen<T>(Func<Scope<E, R>, ValueTask<T>> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return new((env, context) => new Scope<E, R>(env, context).RunBodyAsync(body));
    }

    /// <summary>Runs an effect and returns its value. An Err ends the block.</summary>
    /// <param name="effect">The effect.</param>
    /// <returns>The value.</returns>
    public ValueTask<T> Run<T>(Effect<T, E, R> effect)
        => Step(effect, static (effect, scope) => effect.Run(scope.Env, scope.context));

    /// <summary>Acquires a resource and returns its value. It is released when the block ends, in reverse order of acquisition. An Err ends the block.</summary>
    /// <param name="resource">The resource.</param>
    /// <returns>The value.</returns>
    public ValueTask<T> Acquire<T>(Resource<T, E, R> resource)
        => Step(resource, static (resource, scope) => resource.Acquire(scope.Env, scope.context, scope.releases));

    /// <summary>Starts the effect as a fiber that runs alongside the block. When the block ends, the fiber is canceled and the block waits for it.</summary>
    /// <param name="effect">The effect.</param>
    /// <returns>The running fiber. Get its outcome with <c>scope.Run(fiber.Join())</c>.</returns>
    public ValueTask<Fiber<T, F, R>> Fork<T, F>(Effect<T, F, R> effect) => Acquire<Fiber<T, F, R>>(effect.Fork());

    /// <summary>Returns the value of a result. An Err ends the block.</summary>
    /// <param name="result">The result.</param>
    /// <returns>The value.</returns>
    public T Get<T>(Result<T, E> result)
    {
        Enter();
        try
        {
            ThrowIfFailed();
            if (result.TryGetOk(out var value)) return value;
            if (result.TryGetErr(out var error)) throw Record(error);
            throw ThrowHelper.UninitializedResultException();
        }
        finally
        {
            Leave();
        }
    }

    /// <summary>Ends the block with the error. Write it as <c>throw scope.Fail(error)</c> so the compiler knows the code after it does not run.</summary>
    /// <param name="error">The error.</param>
    /// <returns>Never returns.</returns>
    [DoesNotReturn]
    public Exception Fail(E error)
    {
        Enter();
        ScopeFailure failure;
        try
        {
            failure = Record(error);
        }
        finally
        {
            Leave();
        }

        throw failure;
    }

    /// <summary>Registers a release action that runs when the block ends, however it ends, in reverse order of registration.</summary>
    /// <param name="release">Builds the release from how the block ended.</param>
    public void Defer(Func<ExitCase, Effect<Unit, Never, R>> release)
    {
        ArgumentNullException.ThrowIfNull(release);
        Enter();
        try
        {
            releases.Add(release);
        }
        finally
        {
            Leave();
        }
    }

    private ValueTask<T> Step<T, TStep>(TStep step, Func<TStep, Scope<E, R>, ValueTask<Outcome<T, E>>> start)
    {
        Enter();
        ValueTask<Outcome<T, E>> task;
        try
        {
            ThrowIfFailed();
            task = start(step, this);
        }
        catch
        {
            Leave();
            throw;
        }

        if (!task.IsCompletedSuccessfully)
        {
            // The previous step was not awaited (MEFF310), so the block's end settles it.
            if (Volatile.Read(ref running) is { Observed: false } previous) (unobserved ??= []).Add(previous);
            var pending = new PendingStep<T>();
            Volatile.Write(ref running, pending);
            _ = CompleteAsync(task, pending);
            return pending.Result;
        }

        Leave();
        return new(Unwrap(task.Result));

        async ValueTask CompleteAsync(ValueTask<Outcome<T, E>> task, PendingStep<T> pending)
        {
            Outcome<T, E> outcome;
            try
            {
                outcome = await task.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // A step that throws instead of ending in an outcome must still release its waiters.
                outcome = Defects.From<T, E>(exception, context);
            }
            finally
            {
                Leave();
            }

            T value = default!;
            Exception? failure = null;
            lock (gate)
            {
                // Whether this Err becomes the block's own, read before Unwrap records it.
                var recordsError = outcome.Kind == OutcomeKind.Err && !failed;
                try
                {
                    value = Unwrap(outcome);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }

                // Finished with the recording, so the block's end sees both or neither.
                pending.Finish(outcome, recordsError);
            }

            // Outside the lock, since the awaiting body may continue inline.
            if (failure is null) pending.Succeed(value);
            else pending.Fail(failure);
        }
    }

    /// <summary>A step that did not finish at once. It knows whether the body read its result, so a failure nobody saw is not lost.</summary>
    private abstract class PendingStep
    {
        private readonly TaskCompletionSource<ExitCase?> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int observed;

        /// <summary>Whether the body read the step's result (or its failure).</summary>
        internal bool Observed => Volatile.Read(ref observed) == 1;

        /// <summary>Completes with the step's failure, if any, once its outcome is recorded on the scope.</summary>
        internal Task<ExitCase?> Done => done.Task;

        internal ExceptionDispatchInfo? Defect { get; private set; }

        internal bool Consequential { get; private set; }

        /// <summary>Whether the step's Err became the block's own error, so the block already carries it.</summary>
        internal bool RecordedError { get; private set; }

        protected void Observe() => Volatile.Write(ref observed, 1);

        protected void Finish(ExitCase? failure, ExceptionDispatchInfo? defect, bool consequential, bool recordedError)
        {
            Defect = defect;
            Consequential = consequential;
            RecordedError = recordedError;
            done.SetResult(failure);
        }
    }

    private sealed class PendingStep<T> : PendingStep, IValueTaskSource<T>
    {
        private ManualResetValueTaskSourceCore<T> core;

        internal ValueTask<T> Result => new(this, core.Version);

        internal void Finish(Outcome<T, E> outcome, bool recordedError)
            => Finish(ExitCases.FailureOf(outcome), outcome.Kind == OutcomeKind.Die ? outcome.Defect : null, outcome.Consequential, recordedError);

        internal void Succeed(T value) => core.SetResult(value);

        internal void Fail(Exception exception) => core.SetException(exception);

        public T GetResult(short token)
        {
            Observe();
            return core.GetResult(token);
        }

        public ValueTaskSourceStatus GetStatus(short token) => core.GetStatus(token);

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => core.OnCompleted(continuation, state, token, flags);
    }

    private async ValueTask<Outcome<T, E>> RunBodyAsync<T>(Func<Scope<E, R>, ValueTask<T>> body)
    {
        T value = default!;
        Exception? thrown = null;
        try
        {
            value = await body(this).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            thrown = exception;
        }

        Outcome<T, E> outcome;
        List<PendingStep> steps;
        List<bool> endedWithBody;
        lock (gate)
        {
            if (thrown is not null) outcome = Thrown<T>(thrown);
            else outcome = failed ? Outcome<T, E>.Err(error) : Outcome<T, E>.Ok(value);
            steps = unobserved ?? [];
            if (Volatile.Read(ref running) is { Observed: false } last) steps.Add(last);
            // A step ended by now has its Err on the outcome already.
            endedWithBody = steps.ConvertAll(static step => step.Done.IsCompleted);
        }

        outcome = await SettleUnobservedAsync(outcome, steps, endedWithBody).ConfigureAwait(false);

        closed = true;
        if (suppressed is { Count: > 0 } || omitted > 0) outcome = outcome.After(suppressed?.ToImmutable() ?? [], omitted);
        try
        {
            return await releases.ReleaseAsync(outcome, Env).ConfigureAwait(false);
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private Outcome<T, E> Thrown<T>(Exception exception)
    {
        if (exception is ScopeFailure failure && ReferenceEquals(failure.Owner, this)) return Outcome<T, E>.Err(error);

        // A rethrown Die or inner interruption keeps its kind, whatever the token's state.
        Outcome<T, E> outcome;
        if (rethrown is { } defect && ReferenceEquals(exception, defect.SourceException))
            outcome = Outcome<T, E>.Die(defect, consequential: rethrownConsequential);
        else if (ReferenceEquals(exception, interruption))
            outcome = Outcome<T, E>.Interrupted();
        else
            outcome = Defects.From<T, E>(exception, context);
        // An earlier Err stays as context for what replaced it.
        if (failed) AddSuppressed(new Suppressed(ExitCases.Failed(error), Relation.Sequential));
        return outcome;
    }

    // Unawaited steps (MEFF310) are canceled and awaited, so none outlives the block or loses its failure.
    private async ValueTask<Outcome<T, E>> SettleUnobservedAsync<T>(Outcome<T, E> outcome, List<PendingStep> steps, List<bool> endedWithBody)
    {
        if (steps.Count == 0) return outcome;

        if (endedWithBody.Contains(false)) cancellation.TryCancel(CancellationReason.ParentScopeClosed);
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (await step.Done.ConfigureAwait(false) is not { Value: not Interrupted } failure || step.Observed) continue;

            lock (gate)
            {
                // An Err is already on the outcome unless it is the block's own and the step ended after the snapshot.
                if (failure.Value is Failed && (!step.RecordedError || endedWithBody[i])) continue;

                // An Ok block takes the step's failure, while a failed block keeps its own first.
                if (outcome.Kind == OutcomeKind.Ok && step.Defect is { } defect)
                    outcome = Outcome<T, E>.Die(defect, consequential: step.Consequential);
                else if (outcome.Kind == OutcomeKind.Ok && failure.Value is Failed)
                    outcome = Outcome<T, E>.Err(error);
                else
                    AddSuppressed(new Suppressed(failure, Relation.Sequential));
            }
        }

        return outcome;
    }

    private T Unwrap<T>(Outcome<T, E> outcome)
    {
        lock (gate)
        {
            AddSuppressed(outcome.Suppressed);
            omitted += outcome.Omitted;

            switch (outcome.Kind)
            {
                case OutcomeKind.Ok:
                    return outcome.Value;
                case OutcomeKind.Err:
                    throw Record(outcome.Error);
                case OutcomeKind.Die:
                    rethrown = outcome.Defect;
                    rethrownConsequential = outcome.Consequential;
                    outcome.Defect!.Throw();
                    return default!;
                default:
                    interruption = new OperationCanceledException(context.CancellationToken);
                    throw interruption;
            }
        }
    }

    private ScopeFailure Record(E error)
    {
        lock (gate)
        {
            if (!failed)
            {
                failed = true;
                this.error = error;
            }
            else
            {
                AddSuppressed(new Suppressed(ExitCases.Failed(error), Relation.Sequential));
            }

            return new ScopeFailure(this, this.error);
        }
    }

    private void ThrowIfFailed()
    {
        // F20: after an Err, later steps must not run even if a catch swallowed the ScopeFailure.
        if (failed) throw new ScopeFailure(this, error);
    }

    private void Enter()
    {
        if (closed) throw new InvalidOperationException("The Gen block has ended; its scope can no longer be used.");
        if (Interlocked.Exchange(ref busy, 1) == 1)
        {
            throw new InvalidOperationException(
                "The scope is already running a step. Use it from one flow at a time, and not from a nested Gen block; run effects in parallel with Effect.All instead.");
        }
    }

    private void Leave() => Volatile.Write(ref busy, 0);

    private void AddSuppressed(Suppressed failure)
    {
        suppressed ??= ImmutableArray.CreateBuilder<Suppressed>();
        if (suppressed.Count < SuppressedLimit.MAX) suppressed.Add(failure);
        else omitted++;
    }

    private void AddSuppressed(ImmutableArray<Suppressed> failures)
    {
        foreach (var failure in failures) AddSuppressed(failure);
    }
}

/// <summary>Scope operations that accept a case of the scope's error union.</summary>
public static class ScopeExtensions
{
    /// <summary>Runs an effect whose error is a case of the scope's error union. An Err ends the block.</summary>
    /// <param name="scope">The scope.</param>
    /// <param name="effect">The effect.</param>
    /// <returns>The value.</returns>
    public static ValueTask<T> Run<T, E, ECase, R>(this Scope<E, R> scope, Effect<T, ECase, R> effect)
        where E : IUnionCase<E, ECase>
    {
        ArgumentNullException.ThrowIfNull(scope);
        return scope.Run(effect.Widen<E>());
    }

    /// <summary>Acquires a resource whose error is a case of the scope's error union. An Err ends the block.</summary>
    /// <param name="scope">The scope.</param>
    /// <param name="resource">The resource.</param>
    /// <returns>The value.</returns>
    public static ValueTask<T> Acquire<T, E, ECase, R>(this Scope<E, R> scope, Resource<T, ECase, R> resource)
        where E : IUnionCase<E, ECase>
    {
        ArgumentNullException.ThrowIfNull(scope);
        return scope.Acquire(resource.Widen<E>());
    }

    /// <summary>Returns the value of a result whose error is a case of the scope's error union. An Err ends the block.</summary>
    /// <param name="scope">The scope.</param>
    /// <param name="result">The result.</param>
    /// <returns>The value.</returns>
    public static T Get<T, E, ECase, R>(this Scope<E, R> scope, Result<T, ECase> result)
        where E : IUnionCase<E, ECase>
    {
        ArgumentNullException.ThrowIfNull(scope);
        return scope.Get(result.HasValue ? result.MapErr(static error => E.From(error)) : default);
    }
}
