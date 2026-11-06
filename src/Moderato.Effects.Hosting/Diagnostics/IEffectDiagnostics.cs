namespace Moderato.Effects.Hosting;

/// <summary>Receives unobserved failures from effects started by one Host.</summary>
public interface IEffectDiagnostics
{
    /// <summary>Reports a failure without changing the effect's result.</summary>
    /// <remarks>Calls may be concurrent. Implementations must be thread-safe.</remarks>
    void Report(ExitCase failure);
}
