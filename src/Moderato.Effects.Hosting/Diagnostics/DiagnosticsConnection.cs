namespace Moderato.Effects.Hosting;

internal sealed class DiagnosticsConnection(IEffectDiagnostics diagnostics) : IDisposable
{
    private readonly object gate = new();
    private IEffectDiagnostics? target = diagnostics;

    internal void Report(ExitCase failure)
    {
        lock (gate)
        {
            if (target is { } current)
            {
                current.Report(failure);
                return;
            }
        }

        // Late callbacks must not retain or call a disposed Host's diagnostic implementation.
        if (EffectDiagnostics.UnobservedFailureHandler is { } fallback)
        {
            fallback(failure);
        }
        else
        {
            Console.Error.WriteLine(failure.Value is Die die
                ? $"Moderato.Effects.Hosting late diagnostic: {die.Exception}"
                : "Moderato.Effects.Hosting late diagnostic: unobserved error");
        }
    }

    public void Dispose()
    {
        lock (gate) target = null;
    }
}
