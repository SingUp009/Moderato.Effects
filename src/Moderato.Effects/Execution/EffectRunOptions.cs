namespace Moderato.Effects;

/// <summary>Configures diagnostics for one execution and its children.</summary>
public sealed class EffectRunOptions
{
    /// <summary>Receives unobserved failures; null uses the global diagnostic handler.</summary>
    /// <remarks>May be called concurrently; handler exceptions do not change the result.</remarks>
    public Action<ExitCase>? UnobservedFailureHandler { get; init; }
}
