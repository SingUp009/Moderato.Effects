# SQLite CRUD CLI

ConsoleAppFramework + EF Core SQLite + the local EF integration implementation.
The four operations return `Effect<OrderView, OrderError, AppEnv>`.
`T` is the order, `E` includes not-found/duplicate/invalid input and standard database failures,
and `R` supplies a context factory.

From the repository root, using the .NET 11 SDK specified by `global.json`:

```sh
dotnet run --project samples/Moderato.Effects.Sqlite.Cli -- create --id 1 --name Coffee
dotnet run --project samples/Moderato.Effects.Sqlite.Cli -- read --id 1
dotnet run --project samples/Moderato.Effects.Sqlite.Cli -- update --id 1 --name Tea
dotnet run --project samples/Moderato.Effects.Sqlite.Cli -- delete --id 1
```

Each command prints `OK 1: ...`. SQLite persists data in `orders.db` in the working directory.
Every command accepts `--database /path/to/orders.db`; an absent database is created automatically.
Use `--help` or `create --help` to see ConsoleAppFramework's generated help.

Repeating create with the same ID returns `ALREADY_EXISTS`. Reading, updating or deleting
a missing ID returns `NOT_FOUND`. A nonpositive create/update ID or blank name returns `INVALID`.
Exit codes: success `0`, business error `2`, defect `1`, interrupted Effect `130`.
Unexpected database exceptions remain defects; they are not automatically retried.

## What is new in this sample

- `Program.cs`: ConsoleAppFramework registration, SQLite setup and result display.
- `Orders.cs`: four application Effects and a private `WithDatabase` helper that uses
  existing `Resource`/`Scope` APIs to dispose each command's context.
- `OrdersDb.cs`: the ordinary EF entity, context and factory.

The sample references `src/Moderato.Effects.EntityFrameworkCore`. `WithDatabase` uses
`EfDatabase.Transaction`, and saves use the scope-aware overload. `OrderError` includes
`DatabaseFailure`, a closed union containing constraint, unconfirmed-commit and unclassified
database failures. An unconfirmed commit can be returned without a custom mapper.

Each command runs in an owned transaction with a fresh context. There is no Outbox or
automatic retry. An unconfirmed commit is shown as a standard database error, not retried.
Concurrent modification between lookup and save is not converted
to a business error. SQLite's schema has only the order primary key as a unique constraint;
if more unique constraints are added, the duplicate classifier must be refined.
