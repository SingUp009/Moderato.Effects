namespace Moderato.Effects;

/// <summary>
/// Where failures go that no outcome can carry: defects dropped along with a handled Err, and Errs of forked work that was never joined.
/// It is for diagnostics only and never changes an outcome.
/// </summary>
public static class EffectDiagnostics
{
    private static Action<ExitCase>? s_handler;

    /// <summary>
    /// Receives each unobserved failure: a <see cref="Die"/> or a <see cref="Failed"/>. <see langword="null"/> restores the default,
    /// which writes to <see cref="Console.Error"/>. Exceptions thrown by the handler are ignored.
    /// </summary>
    public static Action<ExitCase>? UnobservedFailureHandler
    {
        get => Volatile.Read(ref s_handler);
        set => Volatile.Write(ref s_handler, value);
    }
}
