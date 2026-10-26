using System.Collections.Immutable;

namespace Moderato.Effects.Internal;

/// <summary>Sends failures that no outcome could carry to <see cref="EffectDiagnostics.UnobservedFailureHandler"/>.</summary>
internal static class UnobservedFailures
{
    /// <summary>Reports the defects among failures that are about to be dropped.</summary>
    internal static void ReportDefects(ImmutableArray<Suppressed> suppressed)
    {
        foreach (var failure in suppressed)
        {
            if (failure.Case is Die) Report(failure.Case);
        }
    }

    /// <summary>Reports a failure that would otherwise disappear.</summary>
    internal static void Report(ExitCase failure)
    {
        var handler = EffectDiagnostics.UnobservedFailureHandler;
        if (handler is null)
        {
            Console.Error.WriteLine($"Moderato.Effects unobserved failure: {Describe(failure)}");
            return;
        }

        try
        {
            handler(failure);
        }
        catch (Exception exception)
        {
            // A diagnostics hook must not turn into a failure of the work it observes.
            Console.Error.WriteLine($"Moderato.Effects unobserved failure handler threw: {exception}");
        }
    }

    private static string Describe(ExitCase failure) => failure.Value switch
    {
        Die die => die.Exception.ToString(),
        Failed failed => $"unjoined Err: {failed.Error}",
        var other => other?.ToString() ?? "",
    };
}
