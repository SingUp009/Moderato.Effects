using Moderato.Effects.Internal;
using Moderato.Results;

namespace Moderato.Effects;

/// <summary>
/// Forked work that runs alongside its parent and never outlives the parent's scope: when the scope ends, the fiber is canceled
/// and the scope waits for it. A defect in the fiber ends the parent with that defect. An Err shows only through <see cref="Join"/>;
/// if the fiber is never joined, it goes to <see cref="EffectDiagnostics.UnobservedFailureHandler"/>.
/// </summary>
/// <typeparam name="T">The value.</typeparam>
/// <typeparam name="E">The error.</typeparam>
/// <typeparam name="R">The environment.</typeparam>
public sealed class Fiber<T, E, R>
{
    private readonly Cancellation _cancellation;
    private readonly Task<Outcome<T, E>> _task;
    private bool _reportedToOwner;
    private int _joined;

    private Fiber(Func<R, RunContext, ValueTask<Outcome<T, E>>> run, R env, Releases<R> owner, Cancellation cancellation)
    {
        _cancellation = cancellation;
        var context = new RunContext(cancellation.Token);
        _task = Task.Run(async () =>
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
                _reportedToOwner = owner.TryFailFromChild(outcome.WithTypes<Unit, Never>());
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
        Volatile.Write(ref _joined, 1);
        try
        {
            return await _task.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            return Outcome<T, E>.Interrupted();
        }
    }

    private async ValueTask<Outcome<Unit, Never>> ReleaseAsync()
    {
        _cancellation.TryCancel(CancellationReason.ParentScopeClosed);
        var outcome = await _task.ConfigureAwait(false);
        _cancellation.Dispose();
        if (Volatile.Read(ref _joined) == 1) return Outcome<Unit, Never>.Ok(Unit.Value);

        switch (outcome.Kind)
        {
            case OutcomeKind.Die when !outcome.Consequential && !_reportedToOwner:
                // The defect came after the parent's body had ended, so it fails the release instead.
                return outcome.WithTypes<Unit, Never>();
            case OutcomeKind.Die when outcome.Consequential:
                UnobservedFailures.Report(ExitCases.ToDie(outcome));
                break;
            case OutcomeKind.Err:
                UnobservedFailures.Report(ExitCases.Failed(outcome.Error));
                UnobservedFailures.ReportDefects(outcome.Suppressed);
                break;
            case OutcomeKind.Ok:
                UnobservedFailures.ReportDefects(outcome.Suppressed);
                break;
        }

        return Outcome<Unit, Never>.Ok(Unit.Value);
    }
}
