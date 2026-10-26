using Moderato.Effects.Internal;
using Moderato.Results;

namespace Moderato.Effects;

/// <summary>
/// Forked work that runs alongside its parent and never outlives the parent's scope: when the scope ends, the fiber is canceled
/// and the scope waits for it. A defect in the fiber ends the parent with that defect. An Err shows only through <see cref="Join"/>;
/// if the fiber is never joined, it goes to <see cref="EffectDiagnostics.UnobservedFailureHandler"/>.
/// A fiber kept after its scope has ended was canceled by it; joining it then is interrupted.
/// </summary>
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

    /// <summary>Waits for the fiber and takes on its outcome. Canceling the waiter does not cancel the fiber.</summary>
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
            // Only an outcome the joiner actually received counts as observed; a canceled wait leaves it to the release.
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

        // A callback that threw while the fiber was being canceled is a defect of the release, raised only after the fiber has ended.
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
                // The defect came after the parent's body had ended, so it fails the release instead.
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
