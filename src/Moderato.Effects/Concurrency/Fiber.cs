using Moderato.Effects.Internal;
using Moderato.Results;

namespace Moderato.Effects;

/// <summary>Forked work that its parent's scope cancels and awaits when it ends; its defect fails the parent, and its Err shows only through <see cref="Join"/> or, if unjoined, the run's diagnostic handler.</summary>
public sealed class Fiber<T, E, R>
{
    private readonly Cancellation cancellation;
    private readonly Task<Outcome<T, E>> task;
    private bool reportedToOwner;
    private int joined;

    private Fiber(Effect<T, E, R> effect, R env, Releases<R> owner, Cancellation cancellation)
    {
        this.cancellation = cancellation;
        var observation = EffectObservation.Begin("fiber", owner.BodyContext);
        var context = cancellation.Context.WithObservation(observation);
        EffectTracker.Track(this, EffectTrackingKind.Fiber);
        task = Task.Run(async () =>
        {
            using var active = observation?.Activate();
            try
            {
                Outcome<T, E> outcome;
                try
                {
                    outcome = await effect.Run(env, context).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    outcome = Defects.From<T, E>(exception, context);
                }

                if (outcome.Kind == OutcomeKind.Die && !outcome.Consequential)
                    reportedToOwner = owner.TryFailFromChild(outcome.WithTypes<Unit, Never>());
                observation?.Finish(outcome);
                return outcome;
            }
            finally
            {
                EffectTracker.Untrack(this);
            }
        });
    }

    /// <summary>Waits for the fiber and takes on its outcome; canceling the wait leaves the fiber running, and a defect that failed the parent comes without the suppressed failures the parent already keeps.</summary>
    public Effect<T, E, R> Join() => new((_, context) => JoinAsync(context));

    internal static Resource<Fiber<T, E, R>, Never, R> Fork(Effect<T, E, R> effect)
    {
        effect = effect.Validate();
        return new((env, context, releases) =>
        {
            var fiber = new Fiber<T, E, R>(effect, env, releases, new Cancellation(context));
            releases.Add(fiber.Release);
            return new(Outcome<Fiber<T, E, R>, Never>.Ok(fiber));
        });
    }

    private Effect<Unit, Never, R> Release(ExitCase _) => new((_, context) => ReleaseAsync(context));

    private async ValueTask<Outcome<T, E>> JoinAsync(RunContext context)
    {
        try
        {
            var outcome = await task.WaitAsync(context.CancellationToken).ConfigureAwait(false);
            // Only an outcome the joiner received counts as observed.
            Volatile.Write(ref joined, 1);
            // The owner already carries a reported defect's suppressed failures, so Join hands over the defect alone.
            return reportedToOwner ? outcome.WithSuppressed([], 0) : outcome;
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            return Outcome<T, E>.Interrupted();
        }
    }

    private async ValueTask<Outcome<Unit, Never>> ReleaseAsync(RunContext context)
    {
        cancellation.TryCancel(CancellationReason.ParentScopeClosed);
        var outcome = await task.ConfigureAwait(false);
        var callbackFailure = cancellation.TakeCallbackFailure();
        cancellation.Dispose();
        var released = Observe(outcome, context);
        if (callbackFailure is null) return released;

        // A callback that threw during cancellation fails the release once the fiber has ended.
        if (released.Kind == OutcomeKind.Die && !FailureSequence.Before(callbackFailure.Order, released.FailureOrder))
            return released.After([new Suppressed(ExitCases.Die(callbackFailure.Dispatch), Relation.Sequential)]);

        var failure = Outcome<Unit, Never>.Die(callbackFailure.Dispatch, failureOrder: callbackFailure.Order);
        if (released.Kind != OutcomeKind.Die) return failure;
        var (suppressed, omitted) = SuppressedContext.Prepend(
            new Suppressed(ExitCases.Die(released.Defect!), Relation.Sequential), released.Suppressed, released.Omitted);
        return failure.WithSuppressed(suppressed, omitted);
    }

    private Outcome<Unit, Never> Observe(Outcome<T, E> outcome, RunContext context)
    {
        if (Volatile.Read(ref joined) == 1) return Outcome<Unit, Never>.Ok(Unit.Value);

        switch (outcome.Kind)
        {
            case OutcomeKind.Die when !outcome.Consequential && !reportedToOwner:
                // The parent's body had already ended, so the defect fails the release.
                return outcome.WithTypes<Unit, Never>();
            case OutcomeKind.Die when outcome.Consequential:
                UnobservedFailures.Report(ExitCases.ToDie(outcome), context);
                break;
            case OutcomeKind.Err:
                UnobservedFailures.Report(ExitCases.Failed(outcome.Error), context);
                UnobservedFailures.ReportDefects(outcome.Suppressed, context);
                break;
            case OutcomeKind.Ok or OutcomeKind.Interrupted:
                UnobservedFailures.ReportDefects(outcome.Suppressed, context);
                break;
        }

        return Outcome<Unit, Never>.Ok(Unit.Value);
    }
}
