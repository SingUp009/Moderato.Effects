namespace Moderato.Effects.Internal;

/// <summary>The per-run context that travels alongside the environment.</summary>
internal readonly struct RunContext(CancellationToken cancellationToken)
{
    /// <summary>The token the current step runs with.</summary>
    internal CancellationToken CancellationToken { get; } = cancellationToken;
}
