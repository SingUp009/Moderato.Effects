namespace Moderato.Effects.Internal;

/// <summary>The per-run context that travels alongside the environment.</summary>
internal readonly struct RunContext(CancellationToken cancellationToken, Action<ExitCase>? unobservedFailureHandler = null)
{
    /// <summary>The token the current step runs with.</summary>
    internal CancellationToken CancellationToken { get; } = cancellationToken;

    internal Action<ExitCase>? UnobservedFailureHandler { get; } = unobservedFailureHandler;

    internal RunContext WithCancellation(CancellationToken cancellationToken)
        => new(cancellationToken, UnobservedFailureHandler);
}
