namespace Moderato.Effects.EntityFrameworkCore;

/// <summary>A database failure with its original exception.</summary>
public record DatabaseFailure
{
    /// <summary>Creates a failure preserving its cause.</summary>
    public DatabaseFailure(Exception cause)
    {
        ArgumentNullException.ThrowIfNull(cause);
        Cause = cause;
    }

    /// <summary>The original exception.</summary>
    public Exception Cause { get; }
}

/// <summary>A commit whose result could not be confirmed.</summary>
public sealed record CommitNotConfirmed : DatabaseFailure
{
    /// <summary>Creates an unconfirmed commit preserving its cause.</summary>
    public CommitNotConfirmed(Exception cause) : base(cause) { }
}

/// <summary>A constraint rejected by the database.</summary>
public sealed record ConstraintViolation : DatabaseFailure
{
    /// <summary>Creates a constraint failure preserving its cause.</summary>
    public ConstraintViolation(ConstraintKind kind, Exception cause) : base(cause) => Kind = kind;

    /// <summary>The kind reported by the provider.</summary>
    public ConstraintKind Kind { get; }
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
