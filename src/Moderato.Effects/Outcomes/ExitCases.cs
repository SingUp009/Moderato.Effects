using System.Diagnostics.CodeAnalysis;

namespace Moderato.Effects;

/// <summary>The run ended because a defect (an unexpected exception) escaped.</summary>
/// <param name="Suppressed">Failures alongside the defect that were not chosen as the outcome.</param>
public sealed record Die(Exception Exception, IReadOnlyList<Suppressed> Suppressed)
{
    /// <summary>The defect that ended the run.</summary>
    public Exception Exception
    {
        get;
        init => field = value ?? throw new ArgumentNullException(nameof(Exception));
    } = Exception ?? throw new ArgumentNullException(nameof(Exception));

    /// <summary>Failures alongside the defect that were not chosen as the outcome.</summary>
    public IReadOnlyList<Suppressed> Suppressed
    {
        get;
        init => field = value ?? throw new ArgumentNullException(nameof(Suppressed));
    } = Suppressed ?? throw new ArgumentNullException(nameof(Suppressed));

    /// <summary>Enables <c>Die(var exception)</c> patterns.</summary>
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

    public bool TryGetValue([NotNullWhen(true)] out Succeeded? succeeded)
    {
        succeeded = Value as Succeeded;
        return succeeded is not null;
    }

    public bool TryGetValue([NotNullWhen(true)] out Failed? failed)
    {
        failed = Value as Failed;
        return failed is not null;
    }

    public bool TryGetValue([NotNullWhen(true)] out Die? die)
    {
        die = Value as Die;
        return die is not null;
    }

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
    public ExitCase Case
    {
        get;
        // A with expression assigns through init, bypassing the constructor's check.
        init => field = Validate(value);
    } = Validate(Case);

    private static ExitCase Validate(ExitCase failure) => failure.Value is null or Succeeded
        ? throw new ArgumentException("A suppressed failure must be a Failed, Die or Interrupted case.", nameof(Case))
        : failure;
}
