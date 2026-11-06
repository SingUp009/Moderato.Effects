using System.Diagnostics.CodeAnalysis;
using Moderato.Results;

namespace Moderato.Effects;

/// <summary>Runs effects whose environment the library provides.</summary>
public static class EnvironmentRunExtensions
{
    private const string TOKEN_JUSTIFICATION = "Overloads differ by environment; legacy token defaults remain and options require an explicit token.";

    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = TOKEN_JUSTIFICATION)]
    [SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Justification = TOKEN_JUSTIFICATION)]
    public static ValueTask<Result<T, E>> RunAsync<T, E>(this Effect<T, E, EmptyEnvironment> effect, CancellationToken cancellationToken = default)
        => effect.RunAsync(EmptyEnvironment.Value, cancellationToken);

    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = TOKEN_JUSTIFICATION)]
    [SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Justification = TOKEN_JUSTIFICATION)]
    public static ValueTask<Exit<T, E>> RunExitAsync<T, E>(this Effect<T, E, EmptyEnvironment> effect, CancellationToken cancellationToken = default)
        => effect.RunExitAsync(EmptyEnvironment.Value, cancellationToken);

    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = TOKEN_JUSTIFICATION)]
    [SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Justification = TOKEN_JUSTIFICATION)]
    public static ValueTask<Result<T, E>> RunAsync<T, E>(this Effect<T, E, DefaultEnvironment> effect, CancellationToken cancellationToken = default)
        => effect.RunAsync(DefaultEnvironment.Value, cancellationToken);

    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = TOKEN_JUSTIFICATION)]
    [SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Justification = TOKEN_JUSTIFICATION)]
    public static ValueTask<Exit<T, E>> RunExitAsync<T, E>(this Effect<T, E, DefaultEnvironment> effect, CancellationToken cancellationToken = default)
        => effect.RunExitAsync(DefaultEnvironment.Value, cancellationToken);

    public static ValueTask<Result<T, E>> RunAsync<T, E>(this Effect<T, E, EmptyEnvironment> effect, CancellationToken cancellationToken, EffectRunOptions options)
        => effect.RunAsync(EmptyEnvironment.Value, cancellationToken, options);

    public static ValueTask<Exit<T, E>> RunExitAsync<T, E>(this Effect<T, E, EmptyEnvironment> effect, CancellationToken cancellationToken, EffectRunOptions options)
        => effect.RunExitAsync(EmptyEnvironment.Value, cancellationToken, options);

    public static ValueTask<Result<T, E>> RunAsync<T, E>(this Effect<T, E, DefaultEnvironment> effect, CancellationToken cancellationToken, EffectRunOptions options)
        => effect.RunAsync(DefaultEnvironment.Value, cancellationToken, options);

    public static ValueTask<Exit<T, E>> RunExitAsync<T, E>(this Effect<T, E, DefaultEnvironment> effect, CancellationToken cancellationToken, EffectRunOptions options)
        => effect.RunExitAsync(DefaultEnvironment.Value, cancellationToken, options);
}
