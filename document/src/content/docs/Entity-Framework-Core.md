---
title: Entity Framework Core
---

# Entity Framework Core

Install `Moderato.Effects.EntityFrameworkCore`. See [Installation](/installation/) for package setup and source builds.

Use `EfDatabase.Transaction` to give one execution a fresh DbContext and an owned transaction. Keep business errors in your application's error union and include `DatabaseFailure` for database outcomes that need to reach the caller.

## Run the SQLite CRUD sample

The [complete sample](https://github.com/SingUp009/Moderato.Effects/tree/main/samples/Moderato.Effects.EntityFrameworkCore.Samples) is a small ConsoleAppFramework application. From the repository root:

```sh
dotnet run --project samples/Moderato.Effects.EntityFrameworkCore.Samples -- create --id 1 --name Coffee
dotnet run --project samples/Moderato.Effects.EntityFrameworkCore.Samples -- read --id 1
dotnet run --project samples/Moderato.Effects.EntityFrameworkCore.Samples -- update --id 1 --name Tea
dotnet run --project samples/Moderato.Effects.EntityFrameworkCore.Samples -- delete --id 1
```

SQLite stores the data in `orders.db` in the working directory. Use `--database /path/to/orders.db` to choose another file. Repeating create returns `ALREADY_EXISTS`; reading a missing order returns `NOT_FOUND`.

## Describe the transaction

The sample's application error and environments are:

```csharp
[Error]
public partial union OrderError(
    OrderNotFound, OrderAlreadyExists, InvalidOrder, DatabaseFailure);

public sealed record AppEnv(IDbContextFactory<OrdersDb> Contexts);
public sealed record OrderDb(OrdersDb Context);
```

The named error records and `OrdersDb` are application types defined in the sample. Its shared transaction boundary is:

```csharp
static Effect<OrderView, OrderError, AppEnv> WithDatabase(
    Func<OrdersDb, Scope<OrderError, OrderDb>, ValueTask<OrderView>> body)
    => EfDatabase<AppEnv>.Transaction(
        contexts: env => env.Contexts,
        environment: (_, db) => new OrderDb(db),
        body: Scope<OrderError, OrderDb>.Gen(
            scope => body(scope.Env.Context, scope)));
```

The factory is borrowed. Each execution creates and owns its context and transaction. A successful body commits after its managed children and inner releases complete. An unsuccessful body does not commit. Rollback and disposal run as cleanup with an uncancelable token; cleanup failures follow the [Core failure rules](/failures/#which-failure-wins).

A successful commit is not changed to interruption merely because cancellation arrives afterward. An uncertain commit result does not prove that the database rolled back.

## Choose which save failures are expected

Inside a Gen block, always await the scope-aware save:

```csharp
await db.SaveChangesAsync(scope, MapConcurrencyFailure);

static bool MapConcurrencyFailure(DatabaseFailure failure, out OrderError error)
{
    if (failure.Value is OptimisticConcurrencyConflict)
    {
        error = failure;
        return true;
    }
    error = default;
    return false;
}
```

Returning true chooses an `Err` in the scope's error type. Without a mapper, or when it returns false, a save failure remains a Die carrying the original exception. Including `DatabaseFailure` in the error union does not automatically turn every save exception into an Err.

`OptimisticConcurrencyConflict` means EF reported `DbUpdateConcurrencyException`. Configure concurrency tokens in your model when you need stale-value detection; the sample does not configure one. SQLite busy/locked errors are not the same as EF optimistic concurrency conflicts. No automatic conflict resolution or retry is performed.

## Standard database failures

`DatabaseFailure.Value` contains one of these cases. `DatabaseFailure.Cause` retains the original exception.

| Case | Meaning |
|---|---|
| ConstraintViolation | A recognized constraint failure, with a `ConstraintKind` |
| OptimisticConcurrencyConflict | EF reported an optimistic save conflict |
| CommitNotConfirmed | Commit was attempted, but its outcome cannot safely be confirmed |
| UnclassifiedDatabaseFailure | A failure without a more specific classification |

Constraint kinds are `Unique` (including a primary key), `ForeignKey`, `NotNull`, `Check` and `Other`. SQLite constraint classification uses provider error codes, not message parsing. The library does not guess a constraint's name or which application rule it represents. The sample maps a unique failure to `OrderAlreadyExists` because that schema has only one unique constraint: its primary key.

The transaction requires an error type containing `DatabaseFailure`. An unmapped `CommitNotConfirmed` is returned through that case as Err. A confirmed commit rejection without a successful custom mapper remains a Die. A later cleanup failure can still become the primary Die and retain the earlier error as context.

Do not blindly retry an unconfirmed commit: the write may already exist. Reconcile with the database or use an application idempotency protocol first.

## Current limits

Initial provider validation covers SQLite. EF optimistic concurrency classification uses EF's public exception type, but this is not validation of every database provider. Retrying execution strategies and a context with an existing transaction are rejected. There is no transaction nesting, automatic retry, Outbox or guessed provider metadata.

Keep each context private to its execution. Parallel children must not use one DbContext concurrently. PostgreSQL, real network failure behavior and EF integration Native AOT have not been validated.
