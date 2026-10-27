using System.Collections.Immutable;

namespace Moderato.Effects.Internal;

/// <summary>Runs an effect against a timer that cancels it, waiting for it to finish.</summary>
internal static class Timeouts
{
    internal static Effect<U, F, R> Create<T, E, U, F, R>(
        Effect<T, E, R> effect, TimeSpan after, Func<Outcome<T, E>, Outcome<U, F>> passthrough, Func<TimeoutError, Outcome<U, F>> timedOut)
        where R : IHas<TimeProvider>
    {
        ThrowIfInvalid(after);
        effect = effect.Validate();
        var error = new TimeoutError(after);
        return new((env, context) => RunAsync(effect, env, context, after, error, passthrough, timedOut));
    }

    /// <summary>The longest wait <c>Task.Delay</c> and the system timer accept.</summary>
    internal static TimeSpan LongestWait { get; } = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    // Rejected here, so an overlong wait is a usage error rather than a defect at run time.
    internal static void ThrowIfInvalid(TimeSpan after)
    {
        if (after == Timeout.InfiniteTimeSpan) return;
        ArgumentOutOfRangeException.ThrowIfLessThan(after, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(after, LongestWait);
    }

    private static async ValueTask<Outcome<U, F>> RunAsync<T, E, U, F, R>(
        Effect<T, E, R> effect, R env, RunContext context, TimeSpan after, TimeoutError error,
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
                    inner = await effect.Run(env, innerContext).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    inner = Defects.From<T, E>(exception, innerContext);
                }

                timedOutFirst = !cancellation.TrySettle() && cancellation.Reason == CancellationReason.Timeout;
            }

            // Taken after disposing the timer, which waits for its callback.
            inner = cancellation.WithCallbackFailure(inner);
        }
        catch (Exception exception)
        {
            return Defects.From<U, F>(exception, context);
        }

        // F12: the caller's cancellation outranks the timer.
        if (!timedOutFirst || context.CancellationToken.IsCancellationRequested) return passthrough(inner);

        if (inner.Kind == OutcomeKind.Die && !inner.Consequential)
        {
            // An independent defect outranks the timeout, checked before onTimeout so that it cannot hide the defect.
            var suppressed = ImmutableArray.CreateBuilder<Suppressed>();
            suppressed.Add(new Suppressed(ExitCases.Failed(error), Relation.Sequential));
            foreach (var failure in inner.Suppressed.AsSpan())
                if (failure.Case is not Interrupted) suppressed.Add(failure);
            return inner.WithSuppressed(suppressed.DrainToImmutable(), inner.Omitted).WithTypes<U, F>();
        }

        Outcome<U, F> timeout;
        try
        {
            timeout = timedOut(error);
        }
        catch (Exception exception)
        {
            timeout = Defects.From<U, F>(exception, context);
        }

        return AfterCanceled(timeout, inner);
    }

    // The canceled effect's outcome follows from the timeout, so it stays as context.
    private static Outcome<U, F> AfterCanceled<T, E, U, F>(Outcome<U, F> timeout, Outcome<T, E> inner) => inner.Kind switch
    {
        OutcomeKind.Die => timeout.After([new Suppressed(ExitCases.Die(inner.Defect!), Relation.Consequence), .. inner.Suppressed], inner.Omitted),
        OutcomeKind.Err => timeout.After([new Suppressed(ExitCases.Failed(inner.Error), Relation.Consequence), .. inner.Suppressed], inner.Omitted),
        _ => timeout.After(inner),
    };
}
