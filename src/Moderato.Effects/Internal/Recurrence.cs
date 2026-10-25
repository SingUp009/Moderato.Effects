using System.Collections.Immutable;

namespace Moderato.Effects.Internal;

/// <summary>Runs Retry and Repeat as a loop, so many recurrences do not nest tasks (F15).</summary>
internal static class Recurrence
{
    internal static Effect<T, E, R> Retry<T, E, R>(
        Effect<T, E, R> effect, Func<R, RunContext, Func<RetryContext<E>, Effect<bool, Never, R>>> start)
    {
        var run = effect.Run;
        return new((env, context) => RetryAsync(run, start, env, context));
    }

    internal static Effect<T, E, R> Repeat<T, E, R>(
        Effect<T, E, R> effect, Func<R, RunContext, Func<RepeatContext<T>, Effect<bool, Never, R>>> start)
    {
        var run = effect.Run;
        return new((env, context) => RepeatAsync(run, start, env, context));
    }

    private static async ValueTask<Outcome<T, E>> RetryAsync<T, E, R>(
        Func<R, RunContext, ValueTask<Outcome<T, E>>> run, Func<R, RunContext, Func<RetryContext<E>, Effect<bool, Never, R>>> start,
        R env, RunContext context)
    {
        Func<RetryContext<E>, Effect<bool, Never, R>> policy;
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

            var decision = await DecideAsync(() => policy(new RetryContext<E>(attempt, outcome.Error)), env, context).ConfigureAwait(false);
            if (decision.Kind != OutcomeKind.Ok) return Interrupt<T, E>(decision, outcome);
            if (!decision.Value) return outcome.After(decision);

            // The failed attempt is handled by trying again, so its suppressed failures are not carried over; defects among them are still seen.
            UnobservedFailures.ReportDefects(outcome.Suppressed);
            UnobservedFailures.ReportDefects(decision.Suppressed);
        }
    }

    private static async ValueTask<Outcome<T, E>> RepeatAsync<T, E, R>(
        Func<R, RunContext, ValueTask<Outcome<T, E>>> run, Func<R, RunContext, Func<RepeatContext<T>, Effect<bool, Never, R>>> start,
        R env, RunContext context)
    {
        Func<RepeatContext<T>, Effect<bool, Never, R>> policy;
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

            var decision = await DecideAsync(() => policy(new RepeatContext<T>(repetition, outcome.Value)), env, context).ConfigureAwait(false);
            if (decision.Kind != OutcomeKind.Ok) return Interrupt<T, E>(decision, outcome);
            if (!decision.Value) return outcome.After(decision);

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

    private static async ValueTask<Outcome<bool, Never>> DecideAsync<R>(Func<Effect<bool, Never, R>> policy, R env, RunContext context)
    {
        try
        {
            return await policy().Run(env, context).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return Defects.From<bool, Never>(exception, context);
        }
    }

    // The policy died or was interrupted (typically while waiting); the last attempt's outcome stays as context.
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

/// <summary>Turns a schedule into waits for one run of Retry or Repeat: its own start time and its own random number generator.</summary>
internal sealed class ScheduleClock<R> where R : IHas<TimeProvider>
{
    private readonly ScheduleSpec _spec;
    private readonly TimeProvider _time;
    private readonly long _started;
    private readonly Random? _random;

    internal ScheduleClock(ScheduleSpec spec, R env)
    {
        _spec = spec;
        _time = env.Resolve<TimeProvider>();
        _started = _time.GetTimestamp();
        if (spec.Jittered) _random = Derive(((IHas<Random>)env).Resolve());
    }

    /// <summary>Waits before recurrence <paramref name="recurrence"/>, or decides to stop.</summary>
    internal Effect<bool, Never, R> WaitBefore(int recurrence)
    {
        // Only one Retry runs this clock at a time, so its private Random needs no lock.
        if (_spec.Next(recurrence, _time.GetElapsedTime(_started), _random) is not { } delay) return Effect<R>.Succeed(false);
        return Effect.Sleep<R>(delay).Map(static _ => true);
    }

    // The environment's Random is touched once per run, under a lock, so one that is not thread-safe survives parallel retries (F6).
    // Only the seed comes from it; the jitter itself uses the standard algorithm (F23).
    private static Random Derive(Random shared)
    {
        int seed;
        lock (shared) seed = shared.Next();
        return new Random(seed);
    }
}
