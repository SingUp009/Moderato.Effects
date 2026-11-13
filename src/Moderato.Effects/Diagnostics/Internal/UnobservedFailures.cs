using System.Collections.Immutable;

namespace Moderato.Effects.Internal;

/// <summary>Sends failures that no outcome could carry to the run's diagnostic handler.</summary>
internal static class UnobservedFailures
{
    /// <summary>Reports the defects among failures that are about to be dropped.</summary>
    internal static void ReportDefects(ImmutableArray<Suppressed> suppressed, RunContext context)
    {
        foreach (var failure in suppressed)
        {
            if (failure.Case is Die) Report(failure.Case, context);
        }
    }

    /// <summary>Reports the defects and Errs that an interruption drops, since Interrupted carries no context.</summary>
    internal static void ReportInterrupted(ImmutableArray<Suppressed> suppressed, RunContext context)
    {
        foreach (var failure in suppressed)
        {
            if (failure.Case is Die or Failed) Report(failure.Case, context);
        }
    }

    /// <summary>Reports a failure that would otherwise disappear.</summary>
    internal static void Report(ExitCase failure, RunContext context)
        => Report(failure, context.UnobservedFailureHandler);

    internal static void Report(ExitCase failure, Action<ExitCase>? runHandler)
    {
        var handler = runHandler ?? EffectDiagnostics.UnobservedFailureHandler;
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

    // Reporting must never fail the work it observes, so its own failures are dropped.
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
