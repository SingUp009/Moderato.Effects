namespace Moderato.Effects.Internal;

internal readonly record struct ResourceEnvironment<R>(R Environment, Releases<R> Releases);

internal sealed class ResourceAcquisition<T, E, R>(Effect<T, E, ResourceEnvironment<R>> effect)
{
    internal Effect<T, E, ResourceEnvironment<R>> Effect { get; } = effect;

    internal ValueTask<Outcome<T, E>> Run(R environment, RunContext context, Releases<R> releases)
    {
        var observation = EffectObservation.Begin("acquire", context);
        if (observation is null) return Effect.Run(new ResourceEnvironment<R>(environment, releases), context);
        return RunObserved(environment, context, releases, observation);
    }

    private async ValueTask<Outcome<T, E>> RunObserved(R environment, RunContext context, Releases<R> releases, EffectObservation observation)
    {
        using var active = observation.Activate();
        try
        {
            var outcome = await Effect.Run(new ResourceEnvironment<R>(environment, releases), context.WithObservation(observation)).ConfigureAwait(false);
            observation.Finish(outcome);
            return outcome;
        }
        catch (Exception exception)
        {
            observation.Finish(Defects.From<T, E>(exception, context));
            throw;
        }
    }
}
