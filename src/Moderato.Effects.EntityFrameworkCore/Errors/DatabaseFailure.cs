namespace Moderato.Effects.EntityFrameworkCore;

/// <summary>A database failure or an unconfirmed commit. <c>default</c> is invalid.</summary>
public union DatabaseFailure(ConstraintViolation, CommitNotConfirmed, UnclassifiedDatabaseFailure)
{
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
    public UnclassifiedDatabaseFailure(Exception cause)
    {
        ArgumentNullException.ThrowIfNull(cause);
        Cause = cause;
    }

    public Exception Cause { get; }
}

/// <summary>A commit whose result could not be confirmed.</summary>
public sealed record CommitNotConfirmed
{
    public CommitNotConfirmed(Exception cause)
    {
        ArgumentNullException.ThrowIfNull(cause);
        Cause = cause;
    }

    public Exception Cause { get; }
}

/// <summary>A constraint rejected by the database.</summary>
public sealed record ConstraintViolation
{
    public ConstraintViolation(ConstraintKind kind, Exception cause)
    {
        ArgumentNullException.ThrowIfNull(cause);
        Kind = kind;
        Cause = cause;
    }

    public ConstraintKind Kind { get; }

    public Exception Cause { get; }
}

/// <summary>The reported kind of constraint violation.</summary>
public enum ConstraintKind
{
    Other,
    /// <summary>A unique or primary key constraint.</summary>
    Unique,
    ForeignKey,
    NotNull,
    Check,
}

/// <summary>Maps a database failure to an application error.</summary>
/// <param name="error">The application error, read only when this returns true.</param>
public delegate bool TryMapDatabaseFailure<E>(DatabaseFailure failure, out E error);
