# Moderato.Effects

[Effect](https://effect.website) for C#

```csharp
// async / await
async Task<Profile> GetUserProfileAsync(int id)
{
    // Which errors can this throw? Which services does it touch?
    // Can it be canceled? Is it safe to retry?
    // The signature cannot tell you.
}
```

```csharp
using Moderato.Effects;

static Effect<Profile, AppError, R> GetUserProfile<R>(int id) where R : IHas<IUserRepository>, IHas<TimeProvider> =>
    Scope<AppError, R>.Gen(async scope =>
    {
        var user = await scope.Run(FindUser<R>(id));      // an Err stops the block here
        await scope.Run(CheckAccess<R>(user));
        var now = scope.Env.Resolve<TimeProvider>().GetUtcNow();
        return new Profile(user.Name, now);
    });

var result = await GetUserProfile<AppEnv>(id)
    .Timeout(TimeSpan.FromSeconds(5))
    .Retry(Schedule.Exponential(TimeSpan.FromMilliseconds(100)).UpTo(3), when: e => e is TimeoutError)
    .RunAsync(env, cancellationToken);
```

`Effect<Profile, AppError, R>` is a description of work: it succeeds with a `Profile`, fails with an `AppError`, and needs an environment `R` that provides the listed services. Nothing runs until `RunAsync`, and every run starts over, which is what makes `Timeout`, `Retry` and `Effect.All` composable.

```csharp
var message = result switch
{
    Ok<Profile>(var profile)         => profile.Name,
    Err<AppError>(NotFound n)        => $"no user {n.Id}",
    Err<AppError>(Forbidden f)       => $"forbidden: {f.Reason}",
    Err<AppError>(TimeoutError t)    => $"timed out after {t.After}",
};
```

Results are [Moderato.Results](https://github.com/SingUp009/Moderato.Results) `Result<T, E>` values, so everything that works with a `Result` works here.

**Warning:** .NET 11 is in preview, and so is this library.

## Table of contents

- [Moderato.Effects](#moderatoeffects)
  - [Table of contents](#table-of-contents)
  - [Requirements](#requirements)
  - [Effect\<T, E, R\>](#effectt-e-r)
  - [Creating effects](#creating-effects)
  - [Running effects](#running-effects)
  - [Gen blocks](#gen-blocks)
  - [Operators](#operators)
  - [Error unions](#error-unions)
  - [Environments](#environments)
  - [Resources](#resources)
  - [Time](#time)
  - [Concurrency](#concurrency)
  - [Retry and repeat](#retry-and-repeat)
  - [Failures](#failures)
  - [Analyzers](#analyzers)
  - [Caveats](#caveats)
  - [Installation](#installation)
  - [Changelog](#changelog)
  - [License](#license)

## Requirements

- .NET 11 SDK (`11.0.100-rc.2` or later), `net11.0`
- C# 15

The library is Native AOT compatible (`IsAotCompatible`). The package includes source generators and analyzers.

## Effect\<T, E, R\>

| Parameter | Meaning |
|---|---|
| `T` | The value on success |
| `E` | The expected error (usually a union). `Never` means it cannot fail |
| `R` | The environment: the services the effect needs, listed as `IHas<TService>` constraints |

An effect is lazy and can run any number of times. `default(Effect<T, E, R>)` is invalid; create effects with the factories below.

`Never` has no values. An `Effect<T, Never, R>` can stand in for an `Effect<T, E, R>` of any `E`, and an `Effect<Never, E, R>` (from `Fail`) for any `T`.

## Creating effects

Factories live on `Effect<R>`, so you name the environment once and the rest is inferred.

```csharp
Effect<R>.Succeed(42);                                     // Effect<int, Never, R>
Effect<R>.Fail(new NotFound(id));                          // Effect<Never, NotFound, R>
Effect<R>.FromResult(Validate(input));                     // Effect<T, E, R>
Effect<R>.Sync(() => File.ReadAllText(path));              // runs on every run
Effect<R>.Async(ct => LoadAsync(ct));                      // ValueTask<T>
Effect<R>.FromResultAsync(ct => FindAsync(id, ct));        // ValueTask<Result<T, E>>
Effect<R>.Try(() => int.Parse(text), (FormatException e) => new Invalid(text));
Effect<R>.Access(env => env.Resolve<TimeProvider>().GetUtcNow());
```

`Succeed(DateTime.UtcNow)` reads the clock once, when the effect is built. Use `Sync` for anything that should happen on each run.

`Try` and `TryAsync` turn the exception type you name into an Err; any other exception is a defect. Exceptions escaping `Sync` and `Async` are defects too (see [Failures](#failures)).

## Running effects

```csharp
Result<Profile, AppError> result = await effect.RunAsync(env, cancellationToken);
Exit<Profile, AppError> exit = await effect.RunExitAsync(env, cancellationToken);
```

- `RunAsync` returns the `Result`. A defect is rethrown as the original exception, and cancellation by your token throws `OperationCanceledException`.
- `RunExitAsync` never throws. `Exit<T, E>` is `Ok<T>`, `Err<E>`, `Die` or `Interrupted`.

Effects that need nothing use `EmptyEnvironment`; effects that only need the system clock and `Random.Shared` use `DefaultEnvironment`. Both can run without an environment argument: `effect.RunAsync(cancellationToken)`.

## Gen blocks

A Gen block is an `async` lambda that runs effects step by step. It is the main way to write effects.

```csharp
Scope<AppError, R>.Gen(async scope => { ... });             // E and R from the Scope type
Effect.Gen(async (Scope<AppError, R> scope) => { ... });    // E and R from the parameter type
```

| Member | Does |
|---|---|
| `await scope.Run(effect)` | Runs an effect and returns its value. An Err ends the block |
| `scope.Get(result)` | Returns the value of a `Result`. An Err ends the block |
| `throw scope.Fail(error)` | Ends the block with the error. `throw` tells the compiler the code after it does not run |
| `scope.Defer(exit => ...)` | Registers a release that runs when the block ends, however it ends |
| `await scope.Acquire(resource)` | Acquires a resource that is released when the block ends |
| `await scope.Fork(effect)` | Starts a fiber that runs alongside the block |
| `scope.Env`, `scope.CancellationToken` | The environment and the run's token |

`Run` and `Get` also accept effects and results whose error is a case of the block's error union.

Inside the block you can write any C#. A few rules keep it safe:

- **After an Err, later steps do not run.** Even if you catch the exception that unwinds the block (`ScopeFailure`), the next `scope.Run` rethrows it, and the block ends with the first Err.
- **Write cleanup with `scope.Defer` or `Acquire`, not `finally`.** A `scope.Run` in a `finally` block does not run after an Err.
- **Catch specific exceptions.** A catch-all swallows `ScopeFailure`; add `when (ex is not ScopeFailure)`.
- **Use a scope only in its own block.** Not in a nested lambda, a nested Gen block, or after the block ends.

The analyzers report each of these (see [Analyzers](#analyzers)).

## Operators

| Operator | Does |
|---|---|
| `Map`, `MapErr` | Transform the value or the error |
| `AndThen`, `AndThrough` | Continue with another effect; `AndThrough` keeps the original value |
| `OrElse` | Recover from the error with another effect |
| `Tap`, `TapErr` | Run a side effect on the value or the error |
| `Match`, `UnwrapOr`, `UnwrapOrElse` | Turn both outcomes into a value; the error becomes `Never` |
| `Attempt` | Turn the error into a value: `Effect<Result<T, E>, Never, R>` |
| `Widen<F>()` | Widen the error to a union that has it as a case |
| `CatchCase((NotFound nf) => ...)` | Recover from one case of the error union |
| `Catch<NotFound, AccessError>(...)` | Recover from one case and remove it from the error type |
| `Provide(build)`, `WidenEnv<R2>()` | Run with an environment built from another one |
| `OnThreadPool()` | Start the effect on the thread pool |

LINQ query syntax works for effects and resources. The first failure short-circuits; a step that cannot fail mixes with steps of any error type.

```csharp
var order =
    from user in FindUser<R>(id)
    from cart in LoadCart<R>(user)
    select new Order(user, cart);
```

## Error unions

Mark a `partial union` with `[Error]` and the source generator lets each case stand in for the union.

```csharp
public record NotFound(int Id);
public record Forbidden(string Reason);

[Error]
public partial union AppError(NotFound, Forbidden, TimeoutError);
```

This is what `scope.Run(FindUser<R>(id))` uses when `FindUser` fails with `NotFound` inside a `Scope<AppError, R>`. LINQ and `Effect.All` cannot choose a union for you; call `.Widen<AppError>()` on a step that fails with a case. A case that is itself a union counts too: with `union DbError(ConnFailed, QueryFailed)` as a case of `AppError`, an effect failing with `ConnFailed` also runs in an `AppError` block.

To remove one case from the error type, declare the rest as its own union:

```csharp
[Error, RestOf<AppError>]
public partial union AccessError(Forbidden, TimeoutError);

Effect<User, AccessError, R> orGuest = FindUser<R>(id).Catch<NotFound, AccessError>(nf => Effect<R>.Succeed(User.Guest));
```

Unions without the attribute get nothing generated. You can also implement `IUnionCase<TSelf, TCase>` and `IUnionRest<TSelf, TSource, TCase>` yourself.

## Environments

An effect lists the services it needs as `IHas<TService>` constraints on `R`, and reads them with `Resolve`.

```csharp
public interface IProfileDeps : IHas<IUserRepository>, IHas<IAuditLogger>, IHas<TimeProvider>;

public static class Profiles<R> where R : IProfileDeps
{
    public static Effect<User?, Never, R> Find(int id) =>
        Effect<R>.AccessAsync((env, ct) => env.Resolve<IUserRepository>().FindAsync(id, ct));
}
```

`[Environment]` implements `IHas<T>` for each primary constructor parameter. `[Expose<T>]` provides a parameter as another type.

```csharp
[Environment]
public sealed partial record AppEnv(IUserRepository Users, IAuditLogger Audit, TimeProvider Clock) : IProfileDeps;

[Environment]
public sealed partial record TestEnv([Expose<TimeProvider>] FakeTimeProvider Clock, IUserRepository Users);
```

An environment is a plain record, so a DI container can build it. Register it with the shortest lifetime of the services it holds:

```csharp
builder.Services.AddScoped<AppEnv>();

app.MapGet("/users/{id}", async (int id, AppEnv env, CancellationToken ct) =>
    await Profiles<AppEnv>.GetUserProfile(id).RunAsync(env, ct) switch { ... });
```

`IHas<T>` is invariant, and two parameters providing the same type are an error, so `Resolve` is never ambiguous.

## Resources

A resource is a value that must be released after use.

```csharp
static Resource<DbConnection, DbError, R> Open<R>() where R : IHas<IDbFactory> =>
    Resource.Make(acquire: Connect<R>(), release: (conn, exit) => Close<R>(conn));

await Open<R>().Use(conn => Query<R>(conn)).RunAsync(env);   // released after Use, however it ends

var tx = await scope.Acquire(BeginTransaction<R>(conn));     // released when the Gen block ends
scope.Defer(exit => exit is Succeeded ? Commit<R>(tx) : Rollback<R>(tx));
```

- Releases run in reverse order of acquisition, with an uncanceled token, after the work has finished.
- Each release receives an `ExitCase`: `Succeeded`, `Failed`, `Die` or `Interrupted`. It is fixed before the first release runs, so a failing release does not change what the others see.
- A release that throws ends the work with a defect; the other releases still run.
- Resources compose with LINQ: `from conn in Open<R>() from tx in Begin<R>(conn) select (conn, tx)`.

## Time

Time-based operators need `IHas<TimeProvider>`, so a test can pass a `FakeTimeProvider`.

```csharp
Effect.Sleep<R>(TimeSpan.FromSeconds(1));
effect.Timeout(TimeSpan.FromSeconds(5));                       // E has a TimeoutError case, or E is Never
effect.Timeout(TimeSpan.FromSeconds(5), t => new Slow(t.After)); // your own error
effect.TimeoutResult(TimeSpan.FromSeconds(5));                 // Effect<Result<T, TimeoutError>, E, R>
```

A timeout is cooperative:

| | |
|---|---|
| Requests cancellation when the time is up | Yes |
| Waits for the work and its releases to finish | Yes |
| Returns within the time limit | No |
| Stops work that ignores its token | No |

## Concurrency

```csharp
var (user, orders) = await scope.Run(Effect.All(FindUser<R>(id), FindOrders<R>(id), Concurrency.Parallel));
var pages = Effect.All(urls.Select(Fetch<R>), Concurrency.Parallel.WithMaxDegree(4));
```

| Operator | Result |
|---|---|
| `Effect.All(effects, concurrency)` | Every value. The first failure cancels the rest |
| `Effect.AllSettled(effects, concurrency)` | Every `Exit`, without cancelling anything |
| `Effect.AllErrors(effects, concurrency)` | Every value, or every error. For validation |
| `Effect.Race(a, b)` | The first success; if both fail, the first failure |
| `Effect.RaceFirst(a, b)` | Whatever finishes first |
| `scope.Fork(effect)` / `effect.Fork()` | A fiber; get its outcome with `scope.Run(fiber.Join())` |

- Concurrency is `Sequential` by default, so services that are not thread-safe, such as a `DbContext`, stay safe. Put thread-safe services or factories (`IDbContextFactory<T>`) in environments used with `Parallel`.
- Work that loses or is cancelled is always waited for, releases included, before the operator returns.
- A fiber never outlives its scope. When the scope ends, the fiber is cancelled and awaited. A defect in a fiber ends its scope with that defect; an Err in a fiber that is never joined goes to the [diagnostics handler](#failures).

## Retry and repeat

```csharp
effect.Retry(Schedule.Exponential(TimeSpan.FromMilliseconds(100)).WithMaxDelay(TimeSpan.FromSeconds(5)).UpTo(3).Jittered(),
             when: e => e is TimeoutError);

PollStatus<R>(jobId).Repeat(Schedule.Spaced(TimeSpan.FromSeconds(1)).During(TimeSpan.FromMinutes(5)),
                            when: status => status is Running);
```

| Schedule | Means |
|---|---|
| `Schedule.Spaced(d)` | Wait `d` each time |
| `Schedule.Exponential(base, factor = 2)` | Wait `base`, `base × 2`, `base × 4`, … |
| `Schedule.Immediately` | Do not wait |
| `.UpTo(n)` | At most `n` more times (`n + 1` attempts in all) |
| `.During(total)` | Stop once `total` has passed since the first attempt |
| `.WithMaxDelay(max)` | Never wait longer than `max` |
| `.Jittered()` | Spread each wait between 80% and 120%. Needs `IHas<Random>` |

- `Retry` retries Err only. Defects and cancellation are never retried. Each attempt runs the effect from the start, so its resources are released per attempt.
- `Repeat` stops at the first Err, and otherwise returns the last value.
- Without `UpTo` or `During`, a schedule never stops.

When the decision depends on the error, write the policy as an effect:

```csharp
effect.Retry(ctx => ctx.Gen<R>(async scope =>
{
    if (ctx.Attempt >= 5 || ctx.Error is not RateLimited limited) return false;
    await scope.Run(Effect.Sleep<R>(limited.RetryAfter));
    return true;
}));
```

## Failures

| Outcome | Means | `RunAsync` |
|---|---|---|
| `Ok<T>` | Success | `Ok` |
| `Err<E>` | An expected error, in the type | `Err` |
| `Die` | A defect: an exception nobody expected | Rethrows it |
| `Interrupted` | Your token cancelled the run | Throws `OperationCanceledException` |

- An `OperationCanceledException` counts as `Interrupted` only when your token was cancelled. Any other exception, including another token's cancellation such as an `HttpClient` timeout, is a defect. Wrap it with `Try` to make it an Err.
- When several things fail at once, one outcome is chosen: a defect first, then your cancellation, then the failure that started the cancellation. The others are kept in `Die.Suppressed`, each with a `Relation` (`Sequential`, `Parallel` or `Consequence`). `RunAsync` attaches them to the rethrown exception's `Data` under `"Moderato.Effects.Suppressed"`.
- A defect that no outcome can carry, such as one suppressed under an Err, goes to `EffectDiagnostics.UnobservedFailureHandler`, together with Errs of fibers that were never joined. By default they are written to `Console.Error`.

```csharp
EffectDiagnostics.UnobservedFailureHandler = failure => logger.LogWarning("Unobserved failure: {Failure}", failure);
```

## Analyzers

| ID | Severity | Reports |
|---|---|---|
| MEFF301 | Warning | A catch-all in a Gen block that swallows `ScopeFailure` |
| MEFF302 | Warning | `scope.Run` and other steps in a `finally` block |
| MEFF303 | Warning | A scope used in a nested lambda or a nested Gen block |
| MEFF304 | Warning | A scope stored in a field or returned |
| MEFF305 | Warning | `scope.Acquire`, `Defer` or `Fork` in a loop |
| MEFF306 | Warning | `.Result`, `.Wait()` or `GetResult()` in a Gen block |
| MEFF307 | Info | The system clock used inside an effect instead of `TimeProvider` |
| MEFF308, MEFF309 | Error | Why a `Catch<TCase, F>` call does not compile |

The source generators report MEFF001 to MEFF202 for declarations they cannot handle. To treat clock use as a warning, add `dotnet_diagnostic.MEFF307.severity = warning` to `.editorconfig`.

## Caveats

### default(Effect) is an invalid state

`default(Effect<T, E, R>)` and `default(Resource<T, E, R>)` throw `InvalidOperationException` when used. Always create them with the factories.

### An effect that cannot fail cannot continue into one that can

`AndThen` keeps the error type, so it cannot turn an `Effect<User?, Never, R>` into one that fails with `NotFound`. Give the first effect the error type you want, or write the step as a Gen block:

```csharp
Effect<User?, NotFound, R> find = Effect<R>.AccessAsync((env, ct) => env.Resolve<IUserRepository>().FindAsync(id, ct));

Scope<NotFound, R>.Gen(async scope =>
{
    var user = await scope.Env.Resolve<IUserRepository>().FindAsync(id, scope.CancellationToken);
    return user ?? throw scope.Fail(new NotFound(id));
});
```

The same applies to `Use` on a resource that cannot fail, and to `Effect.All` over effects whose error types differ: give the variables the error type you want.

### Lambdas that build an effect inline need a type

C# does not infer a lambda's return type through an implicit conversion. Name the value type:

```csharp
effect.AndThen<int>(n => n > 0 ? Effect<R>.Succeed(n) : Effect<R>.Fail<AppError>(new NotFound(n)));
```

### Unions have no positional patterns

Match the union, then the case: `error is DbError db && db is ConnFailed c`, not `error is DbError(ConnFailed c)`.

### A spurious CS8655 in loops

The .NET 11 RC2 compiler reports CS8655 ("does not handle some null inputs") on an exhaustive switch over a union result when, inside a loop, the result comes from a call that passes `default` for a `CancellationToken`, including an omitted optional token. Pass a token variable or `CancellationToken.None`.

### An Err in a Gen block costs an exception

A Gen block unwinds with an exception, so an Err there costs a few microseconds. Effects favour safety over raw speed. For hot paths that fail often, prefer combinators or plain `Result`. Run `bench/Moderato.Effects.Benchmarks` to measure on your machine.

### Captured variables are shared across runs

A Gen block runs again on every run and gets a new scope, but variables captured from outside the lambda are shared between runs.

### The diagnostics handler is global

`EffectDiagnostics.UnobservedFailureHandler` is process-wide. Tests that replace it should not run in parallel with each other.

## Installation

```
dotnet add package Moderato.Effects --prerelease
```

### From source

```
dotnet build
dotnet test -c Release
dotnet run --project samples/Moderato.Effects.Samples
dotnet run -c Release --project bench/Moderato.Effects.Benchmarks -- --filter '*'
```

## Changelog

See [CHANGELOG.md](https://github.com/SingUp009/Moderato.Effects/blob/main/CHANGELOG.md). Breaking changes are listed first for every release.

## License

MIT. See [LICENSE](https://github.com/SingUp009/Moderato.Effects/blob/main/LICENSE).
