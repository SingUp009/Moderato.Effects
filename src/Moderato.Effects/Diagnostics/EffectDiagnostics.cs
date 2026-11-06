namespace Moderato.Effects;

/// <summary>Receives failures no outcome can carry, for diagnostics only.</summary>
public static class EffectDiagnostics
{
    private static Action<ExitCase>? handler;

    /// <summary>Receives each unobserved <see cref="Die"/> or <see cref="Failed"/>; <see langword="null"/> restores the <see cref="Console.Error"/> default, and handler exceptions are ignored.</summary>
    public static Action<ExitCase>? UnobservedFailureHandler
    {
        get => Volatile.Read(ref handler);
        set => Volatile.Write(ref handler, value);
    }
}
