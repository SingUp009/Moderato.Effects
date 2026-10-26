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
    private ImmutableArray<Suppressed>.Builder? suppressed;
    private int omitted;
    private ExceptionDispatchInfo? rethrown;
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
            // The step before this one was left behind without being awaited (MEFF310); it is settled when the block ends.
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
            finally
            {
                Leave();
            }

            // Finished only after Unwrap, so the block never reads the scope's state while the step is still recording on it.
            T value;
            try
            {
                value = Unwrap(outcome);
            }
            catch (Exception exception)
            {
                pending.Finish(outcome);
                pending.Fail(exception);
                return;
            }

            pending.Finish(outcome);
            pending.Succeed(value);
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

        protected void Observe() => Volatile.Write(ref observed, 1);

        protected void Finish(ExitCase? failure, ExceptionDispatchInfo? defect, bool consequential)
        {
            Defect = defect;
            Consequential = consequential;
            done.SetResult(failure);
        }
    }

    private sealed class PendingStep<T> : PendingStep, IValueTaskSource<T>
    {
        private ManualResetValueTaskSourceCore<T> core;

        internal ValueTask<T> Result => new(this, core.Version);

        internal void Finish(Outcome<T, E> outcome)
            => Finish(ExitCases.FailureOf(outcome), outcome.Kind == OutcomeKind.Die ? outcome.Defect : null, outcome.Consequential);

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
        Outcome<T, E> outcome;
        try
        {
            var value = await body(this).ConfigureAwait(false);
            outcome = failed ? Outcome<T, E>.Err(error) : Outcome<T, E>.Ok(value);
        }
        catch (ScopeFailure failure) when (ReferenceEquals(failure.Owner, this))
        {
            outcome = Outcome<T, E>.Err(error);
        }
        catch (Exception exception)
        {
            // A Die rethrown by Run keeps its own classification; the token may have been canceled only afterwards.
            outcome = rethrown is { } defect && ReferenceEquals(exception, defect.SourceException)
                ? Outcome<T, E>.Die(defect, consequential: rethrownConsequential)
                : Defects.From<T, E>(exception, context);
            // An Err that came first is kept as context for the defect or interruption that replaced it.
            if (failed) AddSuppressed(new Suppressed(ExitCases.Failed(error), Relation.Sequential));
        }

        outcome = await SettleUnobservedAsync(outcome).ConfigureAwait(false);

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

    // A step the body did not await (MEFF310) would outlive the block, and a resource it acquired afterwards would never be
    // released; one that already ended would take its failure with it. Running ones are canceled and waited for, so what they
    // registered is released with the rest. A step the body awaited is the body's business, even if it caught the failure.
    private async ValueTask<Outcome<T, E>> SettleUnobservedAsync<T>(Outcome<T, E> outcome)
    {
        var steps = unobserved ?? [];
        if (Volatile.Read(ref running) is { Observed: false } last) steps.Add(last);
        if (steps.Count == 0) return outcome;

        if (steps.Exists(static step => !step.Done.IsCompleted)) cancellation.TryCancel(CancellationReason.ParentScopeClosed);
        foreach (var step in steps)
        {
            if (await step.Done.ConfigureAwait(false) is not { Value: not Interrupted } failure || step.Observed) continue;

            // The step's failure was recorded on this scope; a block that would otherwise succeed ends with the first one,
            // as it would have if the step had been awaited. A block that already failed keeps its own failure first.
            if (outcome.Kind == OutcomeKind.Ok && step.Defect is { } defect)
                outcome = Outcome<T, E>.Die(defect, consequential: step.Consequential);
            else if (outcome.Kind == OutcomeKind.Ok && failure.Value is Failed)
                outcome = Outcome<T, E>.Err(error);
            else
                AddSuppressed(new Suppressed(failure, Relation.Sequential));
        }

        return outcome;
    }

    private T Unwrap<T>(Outcome<T, E> outcome)
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
                throw new OperationCanceledException(context.CancellationToken);
        }
    }

    private ScopeFailure Record(E error)
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

    private void ThrowIfFailed()
    {
        // F20: once an Err has ended the block, later steps must not run, even if a catch swallowed the ScopeFailure.
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
