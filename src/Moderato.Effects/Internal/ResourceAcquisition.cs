namespace Moderato.Effects.Internal;

internal readonly record struct ResourceEnvironment<R>(R Environment, Releases<R> Releases);

internal sealed class ResourceAcquisition<T, E, R>(Effect<T, E, ResourceEnvironment<R>> effect)
{
    internal Effect<T, E, ResourceEnvironment<R>> Effect { get; } = effect;

    internal ValueTask<Outcome<T, E>> Run(R environment, RunContext context, Releases<R> releases)
        => Effect.Run(new ResourceEnvironment<R>(environment, releases), context);
}
