using System.Collections.Immutable;

namespace Moderato.Effects.Internal;

/// <summary>Runs Retry and Repeat as a loop, so many recurrences do not nest tasks (F15).</summary>
internal static class Recurrence
{
    internal static Effect<T, E, R> Retry<T, E, R, TPolicy>(
        Effect<T, E, R> effect, Func<R, RunContext, TPolicy> start)
        where TPolicy : struct, IRecurrencePolicy<RetryContext<E>, R>
    {
        var run = effect.Run;
        return new((env, context) => RetryAsync(run, start, env, context));
    }

    internal static Effect<T, E, R> Repeat<T, E, R, TPolicy>(
        Effect<T, E, R> effect, Func<R, RunContext, TPolicy> start)
        where TPolicy : struct, IRecurrencePolicy<RepeatContext<T>, R>
    {
        var run = effect.Run;
        return new((env, context) => RepeatAsync(run, start, env, context));
    }

    private static async ValueTask<Outcome<T, E>> RetryAsync<T, E, R, TPolicy>(
        Func<R, RunContext, ValueTask<Outcome<T, E>>> run, Func<R, RunContext, TPolicy> start,
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
            var outcome = await RunOnceAsync(run, env, context).ConfigureAwait(false);
            if (outcome.Kind != OutcomeKind.Err) return outcome;
            if (context.CancellationToken.IsCancellationRequested) return Interrupt(Outcome<bool, Never>.Interrupted(), outcome);

            var decision = await DecideAsync(policy, new RetryContext<E>(attempt, outcome.Error), env, context).ConfigureAwait(false);
            if (decision.Kind != OutcomeKind.Ok) return Interrupt<T, E>(decision, outcome);
            if (!decision.Value) return outcome.After(decision);
            if (context.CancellationToken.IsCancellationRequested) return Canceled(decision, outcome);

            // A retried attempt's suppressed failures are dropped, but its defects are still reported.
            UnobservedFailures.ReportDefects(outcome.Suppressed);
            UnobservedFailures.ReportDefects(decision.Suppressed);
        }
    }

    private static async ValueTask<Outcome<T, E>> RepeatAsync<T, E, R, TPolicy>(
        Func<R, RunContext, ValueTask<Outcome<T, E>>> run, Func<R, RunContext, TPolicy> start,
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
            var outcome = await RunOnceAsync(run, env, context).ConfigureAwait(false);
            if (outcome.Kind != OutcomeKind.Ok) return outcome;
            if (context.CancellationToken.IsCancellationRequested) return Interrupt(Outcome<bool, Never>.Interrupted(), outcome);

            var decision = await DecideAsync(policy, new RepeatContext<T>(repetition, outcome.Value), env, context).ConfigureAwait(false);
            if (decision.Kind != OutcomeKind.Ok) return Interrupt<T, E>(decision, outcome);
            if (!decision.Value) return outcome.After(decision);
            if (context.CancellationToken.IsCancellationRequested) return Canceled(decision, outcome);

            UnobservedFailures.ReportDefects(outcome.Suppressed);
            UnobservedFailures.ReportDefects(decision.Suppressed);
        }
    }

    private static async ValueTask<Outcome<T, E>> RunOnceAsync<T, E, R>(Func<R, RunContext, ValueTask<Outcome<T, E>>> run, R env, RunContext context)
    {
        try
        {
            return await run(env, context).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return Defects.From<T, E>(exception, context);
        }
    }

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
    private static Outcome<T, E> Canceled<T, E>(Outcome<bool, Never> decision, Outcome<T, E> last)
    {
        UnobservedFailures.ReportDefects(decision.Suppressed);
        return Interrupt(Outcome<bool, Never>.Interrupted(), last);
    }

    // The policy failed, so the last attempt's outcome stays as context.
    private static Outcome<T, E> Interrupt<T, E>(Outcome<bool, Never> decision, Outcome<T, E> last)
    {
        var failure = decision.WithTypes<T, E>();
        if (decision.Kind == OutcomeKind.Interrupted)
        {
            UnobservedFailures.ReportDefects(last.Suppressed);
            return failure;
        }

        ImmutableArray<Suppressed> context = ExitCases.FailureOf(last) is { } lastFailure
            ? [new Suppressed(lastFailure, Relation.Sequential), .. last.Suppressed]
            : last.Suppressed;
        return failure.WithSuppressed(failure.Suppressed.AddRange(context), failure.Omitted + last.Omitted);
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
    internal ValueTask<Outcome<bool, Never>> WaitBeforeAsync(int recurrence, R env, RunContext context)
    {
        // One Retry at a time uses this clock, so its private Random needs no lock.
        if (spec.Next(recurrence, time.GetElapsedTime(started), random) is not { } delay) return new(Outcome<bool, Never>.Ok(false));
        return WaitAsync(delay, env, context);

        static async ValueTask<Outcome<bool, Never>> WaitAsync(TimeSpan delay, R env, RunContext context)
        {
            await Task.Delay(delay, Services.Resolve<TimeProvider, R>(env), context.CancellationToken).ConfigureAwait(false);
            return Outcome<bool, Never>.Ok(true);
        }
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
        => when is not null && !when(state.Error) ? new(Outcome<bool, Never>.Ok(false)) : clock.WaitBeforeAsync(state.Attempt, env, context);
}

internal readonly struct ScheduledRepeat<T, R>(ScheduleSpec spec, R env, Func<T, bool>? when) : IRecurrencePolicy<RepeatContext<T>, R>
    where R : IHas<TimeProvider>
{
    private readonly ScheduleClock<R> clock = new(spec, env);

    public ValueTask<Outcome<bool, Never>> Decide(RepeatContext<T> state, R env, RunContext context)
        => when is not null && !when(state.Value) ? new(Outcome<bool, Never>.Ok(false)) : clock.WaitBeforeAsync(state.Repetition, env, context);
}
