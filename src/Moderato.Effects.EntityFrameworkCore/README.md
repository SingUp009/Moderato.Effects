# Moderato.Effects.EntityFrameworkCore

Unreleased implementation under review. Core is unchanged. Initial database verification uses
EF Core / Microsoft.Data.Sqlite 10.0.9 and SQLitePCLRaw native SQLite 3.53.3 on .NET 11 RC3.
No Native AOT, PostgreSQL or network-failure validation is claimed.

The error union includes `DatabaseFailure`. This lets the transaction return an unconfirmed
commit as `Err` without requiring a custom mapping callback:

```csharp
[Error]
public partial union OrderError(OrderNotFound, OrderAlreadyExists, DatabaseFailure);
```

`EfDatabase<R>.Transaction` obtains a fresh context from a borrowed `IDbContextFactory`, starts
an owned transaction, runs the body with the context in its environment, waits for the body,
then commits. The factory is not disposed. Neither context nor transaction may escape the body.
The body must await its database operations and must not commit, roll back or dispose them.
Unjoined child `Err` retains Core's existing semantics; it is not automatically promoted.

Saving uses native EF APIs, or `db.SaveChangesAsync(scope, saveFailure)` to map selected update
failures to application errors. The mapping can be omitted. Unmapped ordinary save/update
exceptions remain `Die`. The adapter does not make a DbContext safe for concurrent operations.
The save overload uses `Scope.Run`, so existing operation-start checks and pending-step draining
also apply. Draining an unawaited operation is not a substitute for awaiting its result.

Commit failures can be mapped separately using `commitFailure`. Both callbacks share the same
delegate signature, so a mapping can be reused. Unmapped confirmed rejections remain defects.
An unconfirmed commit, including cancellation after the commit call has begun, defaults to
`Err(DatabaseFailure holding CommitNotConfirmed)`. This is different from cancellation before
the call, which remains interruption. A successful commit is never undone by later cancellation
or a failed disposal. Exceptions, not guesses about server state, are preserved as `Cause`.

Rollback and transaction/context disposal are attempted separately. They use Core's failure
aggregation. A cleanup defect can become primary, retaining a body error as suppressed context.
If a mapping callback throws, its defect retains the original database failure through Core's
error mapping rules.
Rollback after an unconfirmed commit is best effort and cannot prove the prior commit outcome.
No retry or idempotency is provided. Retrying EF execution strategies and pre-existing
transactions are rejected before starting the owned transaction.

SQLite constraint codes are decoded without parsing messages. Constraint names, tables and
schemas are not inferred; this initial implementation does not expose that metadata. The
managed SQLite dependency supports this decoder; the consuming application supplies the native
SQLite provider, normally through `Microsoft.EntityFrameworkCore.Sqlite`. Other provider errors
keep their original cause. Only SQLite's known rejection codes are recognized at commit in
this initial implementation; other provider commit exceptions are conservatively unconfirmed.
This is not PostgreSQL support or exhaustive provider classification.

See `samples/Moderato.Effects.Sqlite.Cli` for a runnable ConsoleAppFramework CRUD application.
