using System.Runtime.CompilerServices;

namespace Moderato.Effects.Internal;

/// <summary>Runs Retry and Repeat as a loop, so many recurrences do not nest tasks (F15).</summary>
internal static class Recurrence
{
    internal static Effect<T, E, R> Retry<T, E, R, TPolicy>(
        Effect<T, E, R> effect, Func<R, RunContext, TPolicy> start)
        where TPolicy : struct, IRecurrencePolicy<RetryContext<E>, R>
    {
        effect = effect.Validate();
        return new((env, context) => RetryAsync(effect, start, env, context));
    }

    internal static Effect<T, E, R> Repeat<T, E, R, TPolicy>(
        Effect<T, E, R> effect, Func<R, RunContext, TPolicy> start)
        where TPolicy : struct, IRecurrencePolicy<RepeatContext<T>, R>
    {
        effect = effect.Validate();
        return new((env, context) => RepeatAsync(effect, start, env, context));
    }

    private static async ValueTask<Outcome<T, E>> RetryAsync<T, E, R, TPolicy>(
        Effect<T, E, R> effect, Func<R, RunContext, TPolicy> start,
        R env, RunContext context)
        where TPolicy : struct, IRecurrencePolicy<RetryContext<E>, R>
    {
        TPolicy policy;
        try
        {
            policy = start(env, context);
        }
        catch (Exception exception)
        {
            return Defects.From<T, E>(exception, context);
        }

        for (var attempt = 1; ; attempt++)
        {
            var attemptTracking = context.Observation?.HasRecording == true
                ? EffectObservation.Begin("attempt", context, $"{effect.TrackingName} {attempt}") : null;
            var outcome = await TraceTasks.Observe(RunOnceAsync(effect, env, context.WithObservation(attemptTracking)), attemptTracking).ConfigureAwait(false);
            if (outcome.Kind != OutcomeKind.Err) return outcome;
            if (context.CancellationToken.IsCancellationRequested) return Interrupt(Outcome<bool, Never>.Interrupted(), outcome);

            var decision = await DecideAsync(policy, new RetryContext<E>(attempt, outcome.Error), env, context).ConfigureAwait(false);
            if (decision.Kind != OutcomeKind.Ok) return Interrupt<T, E>(decision, outcome);
            if (!decision.Value) return outcome.After(decision);
            if (context.CancellationToken.IsCancellationRequested) return Canceled(decision, outcome, context);

            // A retried attempt's suppressed failures are dropped, but its defects are still reported.
            UnobservedFailures.ReportDefects(outcome.Suppressed, context);
            UnobservedFailures.ReportDefects(decision.Suppressed, context);
        }
    }

    private static async ValueTask<Outcome<T, E>> RepeatAsync<T, E, R, TPolicy>(
        Effect<T, E, R> effect, Func<R, RunContext, TPolicy> start,
        R env, RunContext context)
        where TPolicy : struct, IRecurrencePolicy<RepeatContext<T>, R>
    {
        TPolicy policy;
        try
        {
            policy = start(env, context);
        }
        catch (Exception exception)
        {
            return Defects.From<T, E>(exception, context);
        }

        for (var repetition = 1; ; repetition++)
        {
            var attemptTracking = context.Observation?.HasRecording == true
                ? EffectObservation.Begin("attempt", context, $"{effect.TrackingName} {repetition}") : null;
            var outcome = await TraceTasks.Observe(RunOnceAsync(effect, env, context.WithObservation(attemptTracking)), attemptTracking).ConfigureAwait(false);
            if (outcome.Kind != OutcomeKind.Ok) return outcome;
            if (context.CancellationToken.IsCancellationRequested) return Interrupt(Outcome<bool, Never>.Interrupted(), outcome);

            var decision = await DecideAsync(policy, new RepeatContext<T>(repetition, outcome.Value), env, context).ConfigureAwait(false);
            if (decision.Kind != OutcomeKind.Ok) return Interrupt<T, E>(decision, outcome);
            if (!decision.Value) return outcome.After(decision);
            if (context.CancellationToken.IsCancellationRequested) return Canceled(decision, outcome, context);

            UnobservedFailures.ReportDefects(outcome.Suppressed, context);
            UnobservedFailures.ReportDefects(decision.Suppressed, context);
        }
    }

    // Pooling is safe because every call site awaits this ValueTask exactly once.
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private static async ValueTask<Outcome<T, E>> RunOnceAsync<T, E, R>(Effect<T, E, R> effect, R env, RunContext context)
    {
        try
        {
            return await effect.Run(env, context).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return Defects.From<T, E>(exception, context);
        }
    }

    // Pooling is safe because every call site awaits this ValueTask exactly once.
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private static async ValueTask<Outcome<bool, Never>> DecideAsync<TState, R, TPolicy>(
        TPolicy policy, TState state, R env, RunContext context)
        where TPolicy : struct, IRecurrencePolicy<TState, R>
    {
        try
        {
            return await policy.Decide(state, env, context).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return Defects.From<bool, Never>(exception, context);
        }
    }

    // Without this, an attempt or policy that ignores the token could run forever.
    private static Outcome<T, E> Canceled<T, E>(Outcome<bool, Never> decision, Outcome<T, E> last, RunContext context)
    {
        UnobservedFailures.ReportDefects(decision.Suppressed, context);
        return Interrupt(Outcome<bool, Never>.Interrupted(), last);
    }

    // The policy failed or was interrupted, so the last attempt's outcome stays as context.
    private static Outcome<T, E> Interrupt<T, E>(Outcome<bool, Never> decision, Outcome<T, E> last)
    {
        var failure = decision.WithTypes<T, E>();
        var lastFailure = ExitCases.FailureOf(last);
        if (failure.Suppressed.Length == SuppressedContext.MAX)
            return failure.WithSuppressed(failure.Suppressed, failure.Omitted + last.Omitted + last.Suppressed.Length + (lastFailure is null ? 0 : 1));

        if (lastFailure is { } retainedFailure)
        {
            var retained = Math.Min(last.Suppressed.Length, SuppressedContext.MAX - failure.Suppressed.Length - 1);
            return failure.WithSuppressed([.. failure.Suppressed, new Suppressed(retainedFailure, Relation.Sequential), .. last.Suppressed.AsSpan()[..retained]],
                failure.Omitted + last.Omitted + last.Suppressed.Length - retained);
        }

        return failure.WithSuppressed(last.Suppressed, last.Omitted).After(failure);
    }
}

/// <summary>Turns a schedule into waits for one run, with its own start time and random number generator.</summary>
internal readonly struct ScheduleClock<R> where R : IHas<TimeProvider>
{
    private readonly ScheduleSpec spec;
    private readonly TimeProvider time;
    private readonly long started;
    private readonly Random? random;

    internal ScheduleClock(ScheduleSpec spec, R env)
    {
        this.spec = spec;
        time = Services.Resolve<TimeProvider, R>(env);
        started = time.GetTimestamp();
        if (spec.Jittered) random = Derive(((IHas<Random>)env).Resolve());
    }

    /// <summary>Waits before recurrence <paramref name="recurrence"/>, or decides to stop.</summary>
    internal ValueTask<Outcome<bool, Never>> WaitBeforeAsync<T>(int recurrence, T value, Func<T, bool>? when, R env, RunContext context)
    {
        if (when is not null && (recurrence > spec.MaxRecurrences
            || spec.MaxElapsed is { } limit && time.GetElapsedTime(started) >= limit
            || !when(value)))
        {
            return new(Outcome<bool, Never>.Ok(false));
        }

        // The predicate can use up the rest of the time budget.
        var elapsed = time.GetElapsedTime(started);
        if (recurrence > spec.MaxRecurrences || elapsed >= spec.MaxElapsed) return new(Outcome<bool, Never>.Ok(false));

        // One Retry at a time uses this clock, so its private Random needs no lock.
        return WaitAsync(spec.Delay(recurrence, random), env, context);
    }

    // The schedule policy returns this only to DecideAsync, which awaits it once.
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private static async ValueTask<Outcome<bool, Never>> WaitAsync(TimeSpan delay, R env, RunContext context)
    {
        await Task.Delay(delay, Services.Resolve<TimeProvider, R>(env), context.CancellationToken).ConfigureAwait(false);
        return Outcome<bool, Never>.Ok(true);
    }

    // Only a seed is taken, once per run under a lock, so a non-thread-safe Random survives parallel retries (F6, F23).
    private static Random Derive(Random shared)
    {
        int seed;
        lock (shared) seed = shared.Next();
        return new Random(seed);
    }
}

/// <summary>A policy carried by value through one recurrence run.</summary>
internal interface IRecurrencePolicy<TState, R>
{
    // Implementations may pool this ValueTask, so every consumer must await it exactly once.
    ValueTask<Outcome<bool, Never>> Decide(TState state, R env, RunContext context);
}

internal readonly struct EffectPolicy<TState, R>(Func<TState, Effect<bool, Never, R>> policy) : IRecurrencePolicy<TState, R>
{
    public ValueTask<Outcome<bool, Never>> Decide(TState state, R env, RunContext context) => policy(state).Run(env, context);
}

internal readonly struct ScheduledRetry<E, R>(ScheduleSpec spec, R env, Func<E, bool>? when) : IRecurrencePolicy<RetryContext<E>, R>
    where R : IHas<TimeProvider>
{
    private readonly ScheduleClock<R> clock = new(spec, env);

    public ValueTask<Outcome<bool, Never>> Decide(RetryContext<E> state, R env, RunContext context)
        => clock.WaitBeforeAsync(state.Attempt, state.Error, when, env, context);
}

internal readonly struct ScheduledRepeat<T, R>(ScheduleSpec spec, R env, Func<T, bool>? when) : IRecurrencePolicy<RepeatContext<T>, R>
    where R : IHas<TimeProvider>
{
    private readonly ScheduleClock<R> clock = new(spec, env);

    public ValueTask<Outcome<bool, Never>> Decide(RepeatContext<T> state, R env, RunContext context)
        => clock.WaitBeforeAsync(state.Repetition, state.Value, when, env, context);
}
