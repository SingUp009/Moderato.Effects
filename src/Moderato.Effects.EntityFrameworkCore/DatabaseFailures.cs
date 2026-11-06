using System.Runtime.ExceptionServices;
using Microsoft.Data.Sqlite;

namespace Moderato.Effects.EntityFrameworkCore;

internal static class DatabaseFailures
{
    internal static E Rethrow<E>(Exception cause)
    {
        ExceptionDispatchInfo.Throw(cause);
        return default!;
    }

    internal static DatabaseFailure From(Exception cause)
    {
        var provider = cause is Microsoft.EntityFrameworkCore.DbUpdateException { InnerException: { } inner } ? inner : cause;
        if (provider is SqliteException { SqliteErrorCode: 19 } sqlite)
        {
            return new ConstraintViolation(sqlite.SqliteExtendedErrorCode switch
            {
                1555 or 2067 => ConstraintKind.Unique,
                787 => ConstraintKind.ForeignKey,
                1299 => ConstraintKind.NotNull,
                275 => ConstraintKind.Check,
                _ => ConstraintKind.Other,
            }, cause);
        }
        return new DatabaseFailure(cause);
    }

    internal static DatabaseFailure FromCommit(Exception cause)
    {
        // Only known rejection codes establish failure; transport errors do not establish commit outcome.
        if (cause is SqliteException { SqliteErrorCode: 5 or 6 or 19 }) return From(cause);
        return new CommitNotConfirmed(cause);
    }
}
