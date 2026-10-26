using System.Collections.Immutable;

namespace Moderato.Effects.Internal;

/// <summary>Runs an effect against a timer: the timer cancels the effect, and the result waits until the effect has finished.</summary>
internal static class Timeouts
{
    internal static Effect<U, F, R> Create<T, E, U, F, R>(
        Effect<T, E, R> effect, TimeSpan after, Func<Outcome<T, E>, Outcome<U, F>> passthrough, Func<TimeoutError, Outcome<U, F>> timedOut)
        where R : IHas<TimeProvider>
    {
        ThrowIfInvalid(after);
        var run = effect.Run;
        var error = new TimeoutError(after);
        return new((env, context) => RunAsync(run, env, context, after, error, passthrough, timedOut));
    }

    /// <summary>The longest wait <c>Task.Delay</c> and the system timer accept.</summary>
    internal static TimeSpan LongestWait { get; } = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    // Rejected here, so a wait the timer cannot hold is a usage error rather than a defect when the effect runs.
    internal static void ThrowIfInvalid(TimeSpan after)
    {
        if (after == Timeout.InfiniteTimeSpan) return;
        ArgumentOutOfRangeException.ThrowIfLessThan(after, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(after, LongestWait);
    }

    private static async ValueTask<Outcome<U, F>> RunAsync<T, E, U, F, R>(
        Func<R, RunContext, ValueTask<Outcome<T, E>>> run, R env, RunContext context, TimeSpan after, TimeoutError error,
        Func<Outcome<T, E>, Outcome<U, F>> passthrough, Func<TimeoutError, Outcome<U, F>> timedOut)
        where R : IHas<TimeProvider>
    {
        Outcome<T, E> inner;
        bool timedOutFirst;
        try
        {
            var clock = env.Resolve<TimeProvider>();
            using var cancellation = new Cancellation(context.CancellationToken);
            var innerContext = new RunContext(cancellation.Token);
            var timer = clock.CreateTimer(
                static state => ((Cancellation)state!).TryCancel(CancellationReason.Timeout), cancellation, after, Timeout.InfiniteTimeSpan);
            await using (timer.ConfigureAwait(false))
            {
                try
                {
                    inner = await run(env, innerContext).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    inner = Defects.From<T, E>(exception, innerContext);
                }

                timedOutFirst = !cancellation.TrySettle() && cancellation.Reason == CancellationReason.Timeout;
            }

            // After the timer is disposed, which waits for a running timer callback, so its callback failures are in.
            inner = cancellation.WithCallbackFailure(inner);
        }
        catch (Exception exception)
        {
            return Defects.From<U, F>(exception, context);
        }

        // The caller's cancellation outranks the timer (F12): the outcome is whatever the caller's cancellation made of it.
        if (!timedOutFirst || context.CancellationToken.IsCancellationRequested) return passthrough(inner);

        Outcome<U, F> timeout;
        try
        {
            timeout = timedOut(error);
        }
        catch (Exception exception)
        {
            return Defects.From<U, F>(exception, context).After(inner);
        }

        return Decide(inner, timeout, error);
    }

    private static Outcome<U, F> Decide<T, E, U, F>(Outcome<T, E> inner, Outcome<U, F> timeout, TimeoutError error)
    {
        switch (inner.Kind)
        {
            case OutcomeKind.Die when !inner.Consequential:
                // An independent defect, typically from a release action, outranks the timeout; the timeout becomes context.
                var suppressed = ImmutableArray.CreateBuilder<Suppressed>();
                suppressed.Add(new Suppressed(ExitCases.Failed(error), Relation.Sequential));
                suppressed.AddRange(inner.Suppressed.Where(static s => s.Case is not Interrupted));
                return inner.WithSuppressed(suppressed.ToImmutable(), inner.Omitted).WithTypes<U, F>();
            case OutcomeKind.Die:
                return timeout.After([new Suppressed(ExitCases.Die(inner.Defect!), Relation.Consequence), .. inner.Suppressed], inner.Omitted);
            case OutcomeKind.Err:
                return timeout.After([new Suppressed(ExitCases.Failed(inner.Error), Relation.Consequence), .. inner.Suppressed], inner.Omitted);
            default:
                return timeout.After(inner);
        }
    }
}
