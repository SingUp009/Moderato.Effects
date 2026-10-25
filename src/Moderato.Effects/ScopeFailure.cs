namespace Moderato.Effects;

/// <summary>
/// Unwinds a Gen block when an Err ends it, so its <c>finally</c> blocks and <c>using</c> declarations run.
/// The Gen block turns it back into the Err. Do not catch it; exclude it with <c>catch (Exception ex) when (ex is not ScopeFailure)</c>.
/// </summary>
public sealed class ScopeFailure : Exception
{
    internal ScopeFailure(object owner, object? error)
        : base("An Err ended the Gen block. Do not catch this exception.")
    {
        Owner = owner;
        Error = error;
    }

    internal object Owner { get; }

    /// <summary>The error that ended the Gen block.</summary>
    public object? Error { get; }
}
