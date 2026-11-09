using System.Diagnostics;
using Moderato.Effects.Internal;
using Moderato.Results;

namespace Moderato.Effects;

public readonly partial struct Effect<T, E, R>
{
    private async ValueTask<Result<T, E>> RunResultObserved(R environment, RunContext context)
        => await ToResult(await ObserveRoot(environment, context).ConfigureAwait(false), context).ConfigureAwait(false);

    private async ValueTask<Exit<T, E>> RunExitObserved(R environment, RunContext context)
        => ToExit(await ObserveRoot(environment, context).ConfigureAwait(false), context);

    private async ValueTask<Outcome<T, E>> ObserveRoot(R environment, RunContext context)
    {
        var previous = Activity.Current;
        var observation = EffectObservation.Begin("run", context);
        EffectObservation.SetCurrent(observation?.Activity ?? previous);
        Outcome<T, E> outcome;
        try
        {
            outcome = context.CancellationToken.IsCancellationRequested
                ? Outcome<T, E>.Interrupted()
                : await Start(environment, context.WithObservation(observation)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            outcome = Defects.From<T, E>(exception, context);
        }
        finally
        {
            EffectObservation.SetCurrent(previous);
        }
        observation?.Finish(outcome);
        return outcome;
    }
}
