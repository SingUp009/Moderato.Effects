using System.Runtime.ExceptionServices;

namespace Moderato.Effects.Internal;

/// <summary>Builds <see cref="ExitCase"/> values from outcomes.</summary>
internal static class ExitCases
{
    private static readonly Succeeded succeeded = new();
    private static readonly Interrupted interrupted = new();

    internal static ExitCase From<T, E>(Outcome<T, E> outcome) => outcome.Kind switch
    {
        OutcomeKind.Ok => succeeded,
        OutcomeKind.Err => Failed(outcome.Error),
        OutcomeKind.Die => ToDie(outcome),
        _ => interrupted,
    };

    /// <summary>The primary failure without its separately carried context, or <see langword="null"/> if it succeeded.</summary>
    internal static ExitCase? FailureOf<T, E>(Outcome<T, E> outcome)
        => outcome.Kind switch
        {
            OutcomeKind.Ok => (ExitCase?)null,
            OutcomeKind.Die => Die(outcome.Defect!),
            _ => From(outcome),
        };

    internal static Die ToDie<T, E>(Outcome<T, E> outcome)
        => new(outcome.Defect!.SourceException, outcome.Suppressed) { OmittedSuppressed = outcome.Omitted };

    internal static ExitCase Die(ExceptionDispatchInfo defect) => new Die(defect.SourceException, []);

    /// <summary>A failure holding the error, a union unwrapped to its case so <c>Failed(NotFound n)</c> matches.</summary>
    /// <returns>The failure, or a <see cref="Die"/> with the error as context when a union's value throws.</returns>
    internal static ExitCase Failed(object? error)
    {
        try
        {
            while (error is System.Runtime.CompilerServices.IUnion union) error = union.Value;
        }
        catch (Exception exception)
        {
            // The union whose value threw is kept as it is, so nothing reads that value again.
            return new Die(exception, [new Suppressed(new Failed(error), Relation.Sequential)]);
        }

        return new Failed(error);
    }

    internal static Interrupted Interrupted => interrupted;
}
