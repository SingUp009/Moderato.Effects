namespace Moderato.Effects.Http;

/// <summary>Preserves a defect or unexpected interruption at the HTTP boundary.</summary>
public sealed class EffectHttpException : Exception
{
    internal EffectHttpException(ExitCase exit)
        : base(exit.Value is Die ? "The HTTP effect ended with a defect." : "The HTTP effect was interrupted without a request cancellation.",
            (exit.Value as Die)?.Exception) => Exit = exit;

    /// <summary>The original exit, including retained failure information.</summary>
    public ExitCase Exit { get; }
}
