using System.Collections.Immutable;
using Moderato.Results;

namespace Moderato.Effects.Internal;

/// <summary>Runs an effect against a timer that cancels it, waiting for it to finish.</summary>
internal static class Timeouts
{
    private sealed class Deadline(RunContext parent) : Cancellation(parent)
    {
        private int expirationStarted;

        internal long FailureOrder { get; private set; }

        internal void Expire()
        {
            if (Interlocked.Exchange(ref expirationStarted, 1) != 0) return;
            // Publish the order before the reason, independently of timer disposal.
            FailureOrder = FailureSequence.Next();
            if (!TryBeginCancel(CancellationReason.Timeout)) return;
            CancelWork();
        }
    }

    internal static Effect<U, F, R> Create<T, E, U, F, R>(
        Effect<T, E, R> effect, TimeSpan after, Func<Outcome<T, E>, Outcome<U, F>> passthrough, Func<TimeoutError, long, Outcome<U, F>> timedOut)
        where R : IHas<TimeProvider>
    {
        ThrowIfInvalid(after);
        effect = effect.Validate();
        var error = new TimeoutError(after);
        if (after == TimeSpan.Zero)
        {
            return new((_, context) =>
            {
                try
                {
                    return new(context.CancellationToken.IsCancellationRequested
                        ? Outcome<U, F>.Interrupted()
                        : timedOut(error, FailureSequence.Next()));
                }
                catch (Exception exception)
                {
                    return new(Defects.From<U, F>(exception, context));
                }
            });
        }
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
        Func<Outcome<T, E>, Outcome<U, F>> passthrough, Func<TimeoutError, long, Outcome<U, F>> timedOut)
        where R : IHas<TimeProvider>
    {
        Outcome<T, E> inner;
        bool timedOutFirst;
        long timeoutOrder;
        try
        {
            var clock = Services.Resolve<TimeProvider, R>(env);
            using var cancellation = new Deadline(context);
            var innerContext = cancellation.Context;
            var timer = clock.CreateTimer(
                static state => ((Deadline)state!).Expire(), cancellation, after, Timeout.InfiniteTimeSpan);
            try
            {
                inner = await effect.Run(env, innerContext).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                inner = Defects.From<T, E>(exception, innerContext);
            }

            timedOutFirst = !cancellation.TrySettle() && cancellation.Reason == CancellationReason.Timeout;
            try
            {
                await timer.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                var defects = ImmutableArray.CreateBuilder<Outcome<Unit, Never>>(1);
                defects.Add(Defects.From<Unit, Never>(exception, context.WithCancellation(CancellationToken.None)));
                inner = Releases<R>.Combine(inner, defects);
            }

            // Taking callback failures closes collection; later failures go to diagnostics.
            inner = cancellation.WithCallbackFailure(inner);
            timeoutOrder = cancellation.FailureOrder;
        }
        catch (Exception exception)
        {
            return Defects.From<U, F>(exception, context);
        }

        return Finish(inner, timedOutFirst, timeoutOrder, context, error, passthrough, timedOut);
    }

    // Finish after the nested run returns, keeping outcome-projection locals off the descent's stack.
    private static Outcome<U, F> Finish<T, E, U, F>(in Outcome<T, E> inner, bool timedOutFirst, long timeoutOrder,
        RunContext context, TimeoutError error, Func<Outcome<T, E>, Outcome<U, F>> passthrough, Func<TimeoutError, long, Outcome<U, F>> timedOut)
    {
        // F12: the caller's cancellation outranks the timer.
        if (!timedOutFirst || context.CancellationToken.IsCancellationRequested) return passthrough(inner);

        if (inner.Kind == OutcomeKind.Die && !inner.Consequential)
        {
            // An independent defect outranks the timeout, checked before onTimeout so that it cannot hide the defect.
            var suppressed = ImmutableArray.CreateBuilder<Suppressed>();
            suppressed.Add(new Suppressed(ExitCases.Failed(error), Relation.Sequential));
            var omitted = inner.Omitted;
            foreach (var failure in inner.Suppressed.AsSpan())
            {
                if (failure.Case is Interrupted) continue;
                omitted += SuppressedContext.Append(suppressed, failure);
            }
            return inner.WithSuppressed(suppressed.DrainToImmutable(), omitted).WithTypes<U, F>();
        }

        Outcome<U, F> timeout;
        try
        {
            timeout = timedOut(error, timeoutOrder);
        }
        catch (Exception exception)
        {
            timeout = Defects.From<U, F>(exception, context);
        }

        return AfterCanceled(timeout, inner);
    }

    // The canceled effect's outcome follows from the timeout, so it stays as context.
    private static Outcome<U, F> AfterCanceled<T, E, U, F>(Outcome<U, F> timeout, Outcome<T, E> inner)
    {
        if (inner.Kind is not (OutcomeKind.Die or OutcomeKind.Err)) return timeout.After(inner);

        var failure = inner.Kind == OutcomeKind.Die ? ExitCases.Die(inner.Defect!) : ExitCases.Failed(inner.Error);
        var (suppressed, omitted) = SuppressedContext.Prepend(new Suppressed(failure, Relation.Consequence), inner.Suppressed, inner.Omitted);
        return timeout.After(suppressed, omitted);
    }
}
