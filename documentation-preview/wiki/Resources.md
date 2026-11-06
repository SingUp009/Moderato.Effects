# Resources

`Resource<T, E, R>` describes a value that must be released after use: a connection, a transaction, a lock. Its release always runs, and it sees how the use ended.

```csharp
static Resource<DbConnection, DbError, R> OpenConnection<R>() where R : IHas<IDbFactory> =>
    Resource.Make(
        acquire: Connect<R>(),
        release: (connection, exit) => Close<R>(connection));
```

- `acquire` is an `Effect<T, E, R>`.
- `release` returns an `Effect<Unit, Never, R>`. It receives the value and an `ExitCase`: `Succeeded`, `Failed`, `Die` or `Interrupted`.

## Using a resource

There are two ways to get at the value, and both release it:

```csharp
// Use: released when the use ends
var rows = OpenConnection<R>().Use(connection => Query<R>(connection));

// In a Gen block: released when the block ends
var connection = await scope.Acquire(OpenConnection<R>());
```

A Gen block is itself a scope. Resources acquired in it are released when it ends, in reverse order of acquisition.

The value must not escape `Use`; the type system cannot prevent that.

## Composing

```csharp
var both =
    from connection in OpenConnection<R>()
    from tx in BeginTransaction<R>(connection)
    select (connection, tx);

// or: OpenConnection<R>().AndThen(connection => BeginTransaction<R>(connection))
```

`Map` transforms the value; the release still receives the original. A resource that cannot fail (`Never`) mixes with resources of any error type, and converts implicitly to one.

Deep `Map`, `AndThen` and LINQ composition uses the same iterative executor as effects, including resources returned dynamically by a binder. `Use` and `scope.Acquire` preserve reverse release order. Deep execution needs per-run continuation storage; construction and execution allocations are separate costs.

## Release rules

- Releases run in reverse order of acquisition, also for resources acquired in parallel.
- A release runs with an uncanceled token, since a canceled token would stop a rollback.
- The release is registered as soon as the value exists, before anything checks the token, so a cancellation right after acquisition cannot leak it.
- If a release throws, the remaining releases still run. A release failure is a defect (`Die`); body and release failures are combined using the priority rules in [Failures](Failures).
- A release that returns `Interrupted` despite its uncanceled token did not complete. It becomes a `Die` with `InvalidOperationException`; all remaining releases still run.
- The `ExitCase` is fixed when the use ends. A release that fails does not change what later releases see. If one resource must commit before another releases, express that with the order you acquire them in.

The `ExitCase` makes commit-or-rollback simple:

```csharp
static Resource<Transaction, DbError, R> BeginTransaction<R>(DbConnection connection) where R : IHas<IDbFactory> =>
    Resource.Make(
        acquire: Begin<R>(connection),
        release: (tx, exit) => exit is Succeeded ? Commit<R>(tx) : Rollback<R>(tx));
```

`Failed` carries the error with its type erased; for a union error it holds the case, so `exit is Failed(NotFound nf)` matches.

## Defer

In a Gen block, `scope.Defer` registers a release action without a resource:

```csharp
var tx = await scope.Run(BeginTransaction<R>());
scope.Defer(exit => exit is Succeeded ? Commit<R>(tx) : Rollback<R>(tx));
```

## When to use what

| You need | Use |
|---|---|
| plain disposal | `using` / `await using` |
| to see how the work ended (commit or rollback) | `Resource`, `Defer` |
| a release that uses the environment | `Resource`, `Defer` |
| cleanup after an Err in a Gen block | `Defer` or `Acquire`, not `scope.Run` in `finally` |

## In loops

`Acquire`, `Defer` and `Fork` on a Gen block's scope hold the resource until the block ends. Inside a loop, that keeps every iteration's resource alive (MEFF305). Use `resource.Use` or a nested Gen block per iteration.

## Building an environment from resources

Without DI, a resource can build the whole environment, and `Provide` hands it to the effect:

```csharp
Resource<AppEnv, AppError, EmptyEnvironment> environment = ...;
Effect<Report, AppError, EmptyEnvironment> app =
    environment.Use(env => Main<AppEnv>().Provide<EmptyEnvironment>(_ => env));
await app.RunAsync();
```
