using Moderato.Results;

namespace Moderato.Effects.Internal;

internal static class Exits
{
    /// <summary>The public view of an outcome, reporting the defects Exit cannot carry.</summary>
    internal static Exit<T, E> From<T, E>(Outcome<T, E> outcome, RunContext context)
    {
        switch (outcome.Kind)
        {
            case OutcomeKind.Ok:
                UnobservedFailures.ReportDefects(outcome.Suppressed, context);
                return new(new Ok<T>(outcome.Value));
            case OutcomeKind.Err:
                UnobservedFailures.ReportDefects(outcome.Suppressed, context);
                return new(new Err<E>(outcome.Error));
            case OutcomeKind.Die:
                return new(ExitCases.ToDie(outcome));
            default:
                UnobservedFailures.ReportDefects(outcome.Suppressed, context);
                return new(ExitCases.Interrupted);
        }
    }
}
