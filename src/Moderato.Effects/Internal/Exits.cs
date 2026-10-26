using Moderato.Results;

namespace Moderato.Effects.Internal;

internal static class Exits
{
    /// <summary>The public view of an outcome. Defects suppressed by an Ok, Err or Interrupted are reported, since Exit has nowhere to carry them.</summary>
    internal static Exit<T, E> From<T, E>(Outcome<T, E> outcome)
    {
        switch (outcome.Kind)
        {
            case OutcomeKind.Ok:
                UnobservedFailures.ReportDefects(outcome.Suppressed);
                return new(new Ok<T>(outcome.Value));
            case OutcomeKind.Err:
                UnobservedFailures.ReportDefects(outcome.Suppressed);
                return new(new Err<E>(outcome.Error));
            case OutcomeKind.Die:
                return new(ExitCases.ToDie(outcome));
            default:
                UnobservedFailures.ReportDefects(outcome.Suppressed);
                return new(new Interrupted());
        }
    }
}
