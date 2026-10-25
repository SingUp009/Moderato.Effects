namespace Moderato.Effects;

/// <summary>The run ended because a defect (an unexpected exception) escaped.</summary>
/// <param name="Exception">The defect.</param>
/// <param name="Suppressed">Failures that happened alongside the defect and were not chosen as the outcome.</param>
public sealed record Die(Exception Exception, IReadOnlyList<Suppressed> Suppressed)
{
    /// <summary>Enables <c>Die(var exception)</c> patterns.</summary>
    /// <param name="exception">The defect.</param>
    public void Deconstruct(out Exception exception) => exception = Exception;

    /// <summary>How many suppressed failures were left out of <see cref="Suppressed"/> because there were too many.</summary>
    public int OmittedSuppressed { get; init; }
}

/// <summary>The run ended because the caller's token was canceled.</summary>
public sealed record Interrupted;

/// <summary>The scope finished with a value.</summary>
public sealed record Succeeded;

/// <summary>The scope finished with an expected error.</summary>
/// <param name="Error">The error, with its type erased.</param>
public sealed record Failed(object? Error);

/// <summary>How a scope finished, without its value or error types. Passed to release actions.</summary>
public union ExitCase(Succeeded, Failed, Die, Interrupted);

/// <summary>How a suppressed failure relates to the outcome that was kept.</summary>
public enum Relation
{
    /// <summary>It happened afterwards, such as in a release action after the body.</summary>
    Sequential,

    /// <summary>It happened at the same time, such as in a parallel sibling.</summary>
    Parallel,

    /// <summary>It happened because the work was canceled.</summary>
    Consequence,
}

/// <summary>A failure that was not chosen as the outcome.</summary>
/// <param name="Case">How it failed. Never <see cref="Succeeded"/>.</param>
/// <param name="Relation">How it relates to the outcome that was kept.</param>
public sealed record Suppressed(ExitCase Case, Relation Relation);
