using System.Diagnostics.CodeAnalysis;

namespace Moderato.Effects;

/// <summary>The run ended because a defect (an unexpected exception) escaped.</summary>
/// <param name="Exception">The defect.</param>
/// <param name="Suppressed">Failures alongside the defect that were not chosen as the outcome.</param>
public sealed record Die(Exception Exception, IReadOnlyList<Suppressed> Suppressed)
{
    /// <summary>Enables <c>Die(var exception)</c> patterns.</summary>
    /// <param name="exception">The defect.</param>
    public void Deconstruct(out Exception exception) => exception = Exception;

    /// <summary>How many suppressed failures were left out for being too many.</summary>
    public int OmittedSuppressed { get; init; }
}

/// <summary>The run ended because the caller's token was canceled.</summary>
public sealed record Interrupted;

/// <summary>The scope finished with a value.</summary>
public sealed record Succeeded;

/// <summary>The scope finished with an expected error.</summary>
/// <param name="Error">The type-erased error, a union unwrapped to its case.</param>
public sealed record Failed(object? Error);

/// <summary>How a scope finished, untyped; passed to release actions.</summary>
public union ExitCase(Succeeded, Failed, Die, Interrupted)
{
    /// <summary>Whether this contains a non-null case.</summary>
    public bool HasValue => Value is not null;

    /// <summary>Access to the Succeeded case.</summary>
    /// <param name="succeeded">The success case, when this is one.</param>
    /// <returns>True if Succeeded.</returns>
    public bool TryGetValue([NotNullWhen(true)] out Succeeded? succeeded)
    {
        succeeded = Value as Succeeded;
        return succeeded is not null;
    }

    /// <summary>Access to the Failed case.</summary>
    /// <param name="failed">The expected failure, when this is one.</param>
    /// <returns>True if Failed.</returns>
    public bool TryGetValue([NotNullWhen(true)] out Failed? failed)
    {
        failed = Value as Failed;
        return failed is not null;
    }

    /// <summary>Access to the Die case.</summary>
    /// <param name="die">The defect, when this is one.</param>
    /// <returns>True if Die.</returns>
    public bool TryGetValue([NotNullWhen(true)] out Die? die)
    {
        die = Value as Die;
        return die is not null;
    }

    /// <summary>Access to the Interrupted case.</summary>
    /// <param name="interrupted">The interruption, when this is one.</param>
    /// <returns>True if Interrupted.</returns>
    public bool TryGetValue([NotNullWhen(true)] out Interrupted? interrupted)
    {
        interrupted = Value as Interrupted;
        return interrupted is not null;
    }
}

/// <summary>How a suppressed failure relates to the outcome that was kept.</summary>
public enum Relation
{
    /// <summary>It came from a sequential part of the computation, such as its body or release.</summary>
    Sequential,

    /// <summary>It came from independent work, such as a parallel sibling.</summary>
    Parallel,

    /// <summary>It was classified as a consequence of cancellation when observed.</summary>
    Consequence,
}

/// <summary>A failure that was not chosen as the outcome.</summary>
/// <param name="Case">How it failed, never <see cref="Succeeded"/>.</param>
/// <param name="Relation">How it relates to the outcome that was kept.</param>
public sealed record Suppressed(ExitCase Case, Relation Relation)
{
    /// <summary>How it failed, never <see cref="Succeeded"/>.</summary>
    public ExitCase Case { get; init; } = Case.Value is null or Succeeded
        ? throw new ArgumentException("A suppressed failure must be a Failed, Die or Interrupted case.", nameof(Case))
        : Case;
}
