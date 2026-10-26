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
            Write(failure, static failure => $"Moderato.Effects unobserved failure: {Describe(failure)}");
            return;
        }

        try
        {
            handler(failure);
        }
        catch (Exception exception)
        {
            Write(exception, static exception => $"Moderato.Effects unobserved failure handler threw: {exception}");
        }
    }

    // Reporting must not turn into a failure of the work it observes (EffectDiagnostics): a closed stderr or a ToString that throws
    // while building the message is dropped, since there is nowhere left to report it.
    private static void Write<TState>(TState state, Func<TState, string> message)
    {
        try
        {
            Console.Error.WriteLine(message(state));
        }
        catch (Exception)
        {
        }
    }

    private static string Describe(ExitCase failure) => failure.Value switch
    {
        Die die => die.Exception.ToString(),
        Failed failed => $"unjoined Err: {failed.Error}",
        var other => other?.ToString() ?? "",
    };
}
