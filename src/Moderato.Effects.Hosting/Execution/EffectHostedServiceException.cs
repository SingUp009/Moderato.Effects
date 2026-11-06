namespace Moderato.Effects.Hosting;

/// <summary>An effect ended abnormally at the Host boundary.</summary>
public sealed class EffectHostedServiceException : Exception
{
    internal EffectHostedServiceException(ExitCase exit)
        : base(exit.Value is Die ? "The hosted effect ended with a defect." : "The hosted effect was interrupted without a stop request.",
            (exit.Value as Die)?.Exception) => Exit = exit;

    /// <summary>The original Die, including suppressed failures, or the independent Interrupted.</summary>
    public ExitCase Exit { get; }
}
