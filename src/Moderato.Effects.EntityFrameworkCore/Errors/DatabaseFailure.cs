namespace Moderato.Effects.EntityFrameworkCore;

/// <summary>A database failure or an unconfirmed commit. <c>default</c> is invalid.</summary>
public union DatabaseFailure(ConstraintViolation, CommitNotConfirmed, UnclassifiedDatabaseFailure)
{
    /// <summary>The original exception.</summary>
    public Exception Cause => this switch
    {
        ConstraintViolation failure => failure.Cause,
        CommitNotConfirmed failure => failure.Cause,
        UnclassifiedDatabaseFailure failure => failure.Cause,
    };
}

/// <summary>A database failure without a more specific standard case.</summary>
public sealed record UnclassifiedDatabaseFailure
{
    /// <summary>Creates an unclassified failure preserving its cause.</summary>
    public UnclassifiedDatabaseFailure(Exception cause)
    {
        ArgumentNullException.ThrowIfNull(cause);
        Cause = cause;
    }

    /// <summary>The original exception.</summary>
    public Exception Cause { get; }
}

/// <summary>A commit whose result could not be confirmed.</summary>
public sealed record CommitNotConfirmed
{
    /// <summary>Creates an unconfirmed commit preserving its cause.</summary>
    public CommitNotConfirmed(Exception cause)
    {
        ArgumentNullException.ThrowIfNull(cause);
        Cause = cause;
    }

    /// <summary>The original exception.</summary>
    public Exception Cause { get; }
}

/// <summary>A constraint rejected by the database.</summary>
public sealed record ConstraintViolation
{
    /// <summary>Creates a constraint failure preserving its cause.</summary>
    public ConstraintViolation(ConstraintKind kind, Exception cause)
    {
        ArgumentNullException.ThrowIfNull(cause);
        Kind = kind;
        Cause = cause;
    }

    /// <summary>The kind reported by the provider.</summary>
    public ConstraintKind Kind { get; }

    /// <summary>The original exception.</summary>
    public Exception Cause { get; }
}

/// <summary>The reported kind of constraint violation.</summary>
public enum ConstraintKind
{
    /// <summary>An unspecified constraint.</summary>
    Other,
    /// <summary>A unique or primary key constraint.</summary>
    Unique,
    /// <summary>A foreign key constraint.</summary>
    ForeignKey,
    /// <summary>A not-null constraint.</summary>
    NotNull,
    /// <summary>A check constraint.</summary>
    Check,
}

/// <summary>Maps a database failure to an application error.</summary>
/// <param name="failure">The observed failure.</param>
/// <param name="error">The application error, read only when this returns true.</param>
/// <returns>Whether the failure was mapped.</returns>
public delegate bool TryMapDatabaseFailure<E>(DatabaseFailure failure, out E error);
