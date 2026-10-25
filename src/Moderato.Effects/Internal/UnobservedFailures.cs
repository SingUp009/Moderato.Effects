namespace Moderato.Effects.Internal;

/// <summary>Where defects go that no outcome could carry.</summary>
internal static class UnobservedFailures
{
    /// <summary>Reports the defects among failures that are about to be dropped.</summary>
    internal static void ReportDefects(System.Collections.Immutable.ImmutableArray<Suppressed> suppressed)
    {
        foreach (var failure in suppressed)
        {
            if (failure.Case is Die die) Report(die.Exception);
        }
    }

    /// <summary>Reports a defect that would otherwise disappear.</summary>
    internal static void Report(Exception exception) => Console.Error.WriteLine($"Moderato.Effects unobserved defect: {exception}");
}
