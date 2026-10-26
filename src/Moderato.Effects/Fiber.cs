using Moderato.Effects.Internal;
using Moderato.Results;

namespace Moderato.Effects;

/// <summary>Forked work that its parent's scope cancels and awaits when it ends; its defect fails the parent, and its Err shows only through <see cref="Join"/> or, if unjoined, <see cref="EffectDiagnostics.UnobservedFailureHandler"/>.</summary>
/// <typeparam name="T">The value.</typeparam>
/// <typeparam name="E">The error.</typeparam>
/// <typeparam name="R">The environment.</typeparam>
public sealed class Fiber<T, E, R>
{
    private readonly Cancellation cancellation;
    private readonly Task<Outcome<T, E>> task;
    private bool reportedToOwner;
    private int joined;

    private Fiber(Func<R, RunContext, ValueTask<Outcome<T, E>>> run, R env, Releases<R> owner, Cancellation cancellation)
    {
        this.cancellation = cancellation;
        var context = new RunContext(cancellation.Token);
        task = Task.Run(async () =>
        {
            Outcome<T, E> outcome;
            try
            {
                outcome = await run(env, context).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                outcome = Defects.From<T, E>(exception, context);
            }

            if (outcome.Kind == OutcomeKind.Die && !outcome.Consequential)
                reportedToOwner = owner.TryFailFromChild(outcome.WithTypes<Unit, Never>());
            return outcome;
        });
    }

    /// <summary>Waits for the fiber and takes on its outcome; canceling the wait leaves the fiber running.</summary>
    /// <returns>An effect with the fiber's outcome.</returns>
    public Effect<T, E, R> Join() => new((_, context) => JoinAsync(context));

    internal static Resource<Fiber<T, E, R>, Never, R> Fork(Effect<T, E, R> effect)
    {
        var run = effect.Run;
        return new((env, context, releases) =>
        {
            var fiber = new Fiber<T, E, R>(run, env, releases, new Cancellation(context.CancellationToken));
            releases.Add(_ => new((_, _) => fiber.ReleaseAsync()));
            return new(Outcome<Fiber<T, E, R>, Never>.Ok(fiber));
        });
    }

    private async ValueTask<Outcome<T, E>> JoinAsync(RunContext context)
    {
        try
        {
            var outcome = await task.WaitAsync(context.CancellationToken).ConfigureAwait(false);
            // Only an outcome the joiner received counts as observed.
            Volatile.Write(ref joined, 1);
            return outcome;
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            return Outcome<T, E>.Interrupted();
        }
    }

    private async ValueTask<Outcome<Unit, Never>> ReleaseAsync()
    {
        cancellation.TryCancel(CancellationReason.ParentScopeClosed);
        var outcome = await task.ConfigureAwait(false);
        var callbackFailure = cancellation.TakeCallbackFailure();
        cancellation.Dispose();
        var released = Observe(outcome);
        if (callbackFailure is null) return released;

        // A callback that threw during cancellation fails the release once the fiber has ended.
        return released.Kind == OutcomeKind.Die
            ? released.After([new Suppressed(ExitCases.Die(callbackFailure), Relation.Sequential)])
            : Outcome<Unit, Never>.Die(callbackFailure);
    }

    private Outcome<Unit, Never> Observe(Outcome<T, E> outcome)
    {
        if (Volatile.Read(ref joined) == 1) return Outcome<Unit, Never>.Ok(Unit.Value);

        switch (outcome.Kind)
        {
            case OutcomeKind.Die when !outcome.Consequential && !reportedToOwner:
                // The parent's body had already ended, so the defect fails the release.
                return outcome.WithTypes<Unit, Never>();
            case OutcomeKind.Die when outcome.Consequential:
                UnobservedFailures.Report(ExitCases.ToDie(outcome));
                break;
            case OutcomeKind.Err:
                UnobservedFailures.Report(ExitCases.Failed(outcome.Error));
                UnobservedFailures.ReportDefects(outcome.Suppressed);
                break;
            case OutcomeKind.Ok or OutcomeKind.Interrupted:
                UnobservedFailures.ReportDefects(outcome.Suppressed);
                break;
        }

        return Outcome<Unit, Never>.Ok(Unit.Value);
    }
}
