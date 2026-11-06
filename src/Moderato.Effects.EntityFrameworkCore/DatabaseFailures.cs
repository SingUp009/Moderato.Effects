using System.Data.Common;
using System.Runtime.ExceptionServices;
using Microsoft.Data.Sqlite;
using static SQLitePCL.raw;

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
        if (provider is SqliteException { SqliteErrorCode: SQLITE_CONSTRAINT } sqlite)
        {
            return new ConstraintViolation(sqlite.SqliteExtendedErrorCode switch
            {
                SQLITE_CONSTRAINT_PRIMARYKEY or SQLITE_CONSTRAINT_UNIQUE => ConstraintKind.Unique,
                SQLITE_CONSTRAINT_FOREIGNKEY => ConstraintKind.ForeignKey,
                SQLITE_CONSTRAINT_NOTNULL => ConstraintKind.NotNull,
                SQLITE_CONSTRAINT_CHECK => ConstraintKind.Check,
                _ => ConstraintKind.Other,
            }, cause);
        }
        return new UnclassifiedDatabaseFailure(cause);
    }

    internal static DatabaseFailure FromCommit(Exception cause, DbTransaction transaction)
    {
        // An interceptor can throw a provider exception after commit; the code alone does not prove rejection.
        if (transaction is SqliteTransaction { Connection: not null }
            && cause is SqliteException { SqliteErrorCode: SQLITE_BUSY or SQLITE_LOCKED or SQLITE_CONSTRAINT })
        {
            return From(cause);
        }
        return new CommitNotConfirmed(cause);
    }
}
