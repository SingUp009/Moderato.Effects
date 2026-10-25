using Moderato.Results;

namespace Moderato.Effects;

/// <summary>Runs effects whose environment the library provides.</summary>
public static class EnvironmentRunExtensions
{
    /// <summary>Runs an effect that needs nothing.</summary>
    /// <param name="effect">The effect.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The value, or the expected error.</returns>
    public static ValueTask<Result<T, E>> RunAsync<T, E>(this Effect<T, E, EmptyEnvironment> effect, CancellationToken cancellationToken = default)
        => effect.RunAsync(EmptyEnvironment.Value, cancellationToken);

    /// <summary>Runs an effect that needs nothing and returns how it ended, without throwing.</summary>
    /// <param name="effect">The effect.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>Ok, Err, Die or Interrupted.</returns>
    public static ValueTask<Exit<T, E>> RunExitAsync<T, E>(this Effect<T, E, EmptyEnvironment> effect, CancellationToken cancellationToken = default)
        => effect.RunExitAsync(EmptyEnvironment.Value, cancellationToken);

    /// <summary>Runs an effect that needs only the clock and the random number generator.</summary>
    /// <param name="effect">The effect.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The value, or the expected error.</returns>
    public static ValueTask<Result<T, E>> RunAsync<T, E>(this Effect<T, E, DefaultEnvironment> effect, CancellationToken cancellationToken = default)
        => effect.RunAsync(DefaultEnvironment.Value, cancellationToken);

    /// <summary>Runs an effect that needs only the clock and the random number generator, and returns how it ended, without throwing.</summary>
    /// <param name="effect">The effect.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>Ok, Err, Die or Interrupted.</returns>
    public static ValueTask<Exit<T, E>> RunExitAsync<T, E>(this Effect<T, E, DefaultEnvironment> effect, CancellationToken cancellationToken = default)
        => effect.RunExitAsync(DefaultEnvironment.Value, cancellationToken);
}
