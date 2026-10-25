namespace Moderato.Effects.Internal;

/// <summary>Where defects go that no outcome could carry.</summary>
internal static class UnobservedFailures
{
    /// <summary>Reports a defect that would otherwise disappear.</summary>
    internal static void Report(Exception exception) => Console.Error.WriteLine($"Moderato.Effects unobserved defect: {exception}");
}
