using System.Collections.Immutable;

namespace Moderato.Effects.Internal;

/// <summary>Runs an effect against a timer that cancels it, waiting for it to finish.</summary>
internal static class Timeouts
{
    private sealed class Deadline(CancellationToken parent) : Cancellation(parent)
    {
        internal long FailureOrder { get; private set; }

        internal void Expire()
        {
            if (!TryBeginCancel(CancellationReason.Timeout)) return;
            FailureOrder = FailureSequence.Next();
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
            using var cancellation = new Deadline(context.CancellationToken);
            var innerContext = new RunContext(cancellation.Token);
            var timer = clock.CreateTimer(
                static state => ((Deadline)state!).Expire(), cancellation, after, Timeout.InfiniteTimeSpan);
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
            timeoutOrder = cancellation.FailureOrder;
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
            var omitted = inner.Omitted;
            foreach (var failure in inner.Suppressed.AsSpan())
            {
                if (failure.Case is Interrupted) continue;
                if (suppressed.Count < SuppressedLimit.MAX) suppressed.Add(failure);
                else omitted++;
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
        var retained = Math.Min(inner.Suppressed.Length, SuppressedLimit.MAX - 1);
        return timeout.After([new Suppressed(failure, Relation.Consequence), .. inner.Suppressed.AsSpan()[..retained]],
            inner.Omitted + inner.Suppressed.Length - retained);
    }
}
