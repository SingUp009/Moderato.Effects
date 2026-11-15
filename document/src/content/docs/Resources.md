---
title: Resources
description: Acquire, compose, and release resources in Moderato.Effects. Learn how scoped cleanup handles success, failure, and cancellation.
---

# Resources

`Resource<T, E, R>` describes a value with a managed lifetime, such as a connection, transaction, or lock. A successful acquisition registers a release that runs when the owning scope ends.

The excerpts below use application-specific types and effects.

```csharp
static Resource<DbConnection, DbError, R> OpenConnection<R>() where R : IHas<IDbFactory> =>
    Resource.Make(
        acquire: Connect<R>(),
        release: (connection, exit) => Close<R>(connection));
```

- `acquire` is an `Effect<T, E, R>`.
- `release` returns an `Effect<Unit, Never, R>`. It receives the value and an `ExitCase`: `Succeeded`, `Failed`, `Die` or `Interrupted`.

## Using a resource

Use `Use` to give an operation its own cleanup scope. Use `scope.Acquire` in a single `Gen` block when several releases need the same body `ExitCase`.

```csharp
// Use: released when the use ends
var rows = OpenConnection<R>().Use(connection => Query<R>(connection));

// In a Gen block: released when the block ends
var connection = await scope.Acquire(OpenConnection<R>());
```

Resources acquired in a Gen block are released when it ends. A nested `Use` finishes its cleanup before the outer body ends, so a defect in that cleanup can become the outcome the outer release receives. See the [two-resource example](/agent-guide/#manage-a-resource-with-asynchronous-cleanup).

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
- If a release throws, the remaining releases still run. A release failure is a defect (`Die`); body and release failures are combined using the priority rules in [Failures](/failures/).
- A release that returns `Interrupted` despite its uncanceled token did not complete. It becomes a `Die` with `InvalidOperationException`; all remaining releases still run.
- All releases in a scope receive the same `ExitCase`, captured before cleanup starts. Resources composed with `AndThen` share a scope when used together. If one resource must commit before another releases, express that with the order you acquire them in.

The `ExitCase` makes commit-or-rollback simple:

```csharp
static Resource<Transaction, DbError, R> BeginTransaction<R>(DbConnection connection) where R : IHas<IDbFactory> =>
    Resource.Make(
        acquire: Begin<R>(connection),
        release: (tx, exit) => exit is Succeeded ? Commit<R>(tx) : Rollback<R>(tx));
```

`Failed` carries the error with its type erased; for a union error it holds the case, so `exit is Failed(NotFound nf)` matches.

## Defer

In a Gen block, `scope.Defer` registers a release action. Run the transaction's acquisition effect first, then register the action for when the scope ends.

```csharp
var tx = await scope.Run(Begin<R>(connection));
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
Resource<AppEnv, AppError, EmptyEnvironment> environment = OpenEnvironment();
Effect<Report, AppError, EmptyEnvironment> app =
    environment.Use(env => BuildReport<AppEnv>().Provide<EmptyEnvironment>(_ => env));
await app.RunAsync();
```
