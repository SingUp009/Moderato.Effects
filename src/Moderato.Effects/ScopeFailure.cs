namespace Moderato.Effects;

/// <summary>Unwinds a Gen block on an Err; do not catch it, or exclude it with <c>when (ex is not ScopeFailure)</c>.</summary>
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
