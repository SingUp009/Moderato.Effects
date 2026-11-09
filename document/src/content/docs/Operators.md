---
title: Operators
---

# Operators

Every operator returns a new effect; the work it describes stays deferred until execution.

## Values and errors

| Operator | Result type | Does |
|---|---|---|
| `Map(x => ...)` | `Effect<U, E, R>` | Transforms the value |
| `MapErr(e => ...)` | `Effect<T, F, R>` | Transforms the error |
| `AndThen(x => effect)` | `Effect<U, E, R>` | Continues with the next effect |
| `AndThrough(x => effect)` | `Effect<T, E, R>` | Runs a check, keeping the value |
| `OrElse(e => effect)` | `Effect<T, F, R>` | Recovers with the next effect |
| `Tap(x => ...)` / `TapErr(e => ...)` | same | Runs an action on the value or the error |
| `Match(ok, err)` | `Effect<U, Never, R>` | Folds both into one value |
| `UnwrapOr(fallback)` / `UnwrapOrElse(e => ...)` | `Effect<T, Never, R>` | Replaces an Err with a value |
| `Attempt()` | `Effect<Result<T, E>, Never, R>` | Makes the error a value |

`TapErr<U>(Func<E, Effect<U, Never, R>>)` also accepts an observation effect, so environment access and asynchronous work compose through Effect. A successful observer leaves the original Err; a failing observer retains that Err as Sequential context. It is observation, not recovery: consume a successfully handled Err explicitly afterward.

When recompiled, `TapErr(error => BuildEffect(error))` selects the effect-returning overload and executes the returned effect. Use an explicit `Action<E>` if only the callback's synchronous side effects are intended. Existing action callbacks and throw-only lambdas keep their signatures.

## Deep composition

`Map`, `AndThen`, `OrElse`, environment conversions and LINQ use an iterative executor for deep composition. An `AndThen` binder can return another deeply composed effect. Descriptions remain lazy, rerunnable and safe to run concurrently; synchronous steps can still finish synchronously.

The library keeps execution state per run. Captured mutable values and services in a shared environment still need your own concurrency discipline.

Deep runs allocate continuation storage. Building a description once avoids rebuilding it on each run, but does not cache results. Arbitrary C# recursion inside callbacks and arbitrary nesting of control wrappers such as `Retry`, `Timeout` and `Gen` are outside this stack-safety guarantee.

## Reassociating effects

Pure transformations and explicit environment dependencies make compositions easier to reason about. Keep construction of a description free of side effects; place work that should happen on every run inside `Sync`, `Async`, `Access` or a Gen block.

Do not assume that arbitrary regrouping preserves ambient .NET state such as `AsyncLocal`. User callbacks are ordinary C# and can observe async execution boundaries. Pass a dependency explicitly through `R`, or pass the value as an effect result, when the program relies on it. Reasoning about equivalent compositions assumes pure callbacks and explicit dependencies; ambient state and side effects during description construction do not meet those assumptions.

`Attempt` lets a `switch` handle every case, with the compiler checking that none is missing:

```csharp
var found = await scope.Run(FindUser<R>(id).Attempt());
var user = found switch
{
    Ok<User>(var u)            => u,
    Err<AppError>(NotFound)    => User.Guest,
    Err<AppError>(Forbidden f) => throw scope.Fail(f),
};
```

## Error unions

| Operator | Error type | Does |
|---|---|---|
| `CatchCase((NotFound nf) => effect)` | unchanged | Handles one case; others pass through |
| `Catch<NotFound, AccessError>(nf => effect)` | `AccessError` | Handles one case and removes it from the type |
| `Widen<F>()` | `F` | Widens an error to a union that has it as a case |

See [Error unions](/error-unions/).

## Environment and threads

| Operator | Does |
|---|---|
| `Provide<R2>(r2 => r)` | Runs with an environment built from another one |
| `WidenEnv<R2>()` | Runs with an environment that provides at least `R` |
| `OnThreadPool()` | Starts the effect on the thread pool, for CPU-bound work |
| `Fork()` | A resource that runs the effect as a fiber (see [Concurrency](/concurrency/)) |

## LINQ query syntax

```csharp
var profile =
    from user in FindUser<R>(id)
    from now  in Effect<R>.Access(env => env.Resolve<TimeProvider>().GetUtcNow())
    select new Profile(user.Name, now);
```

The first failure short-circuits. A step that cannot fail (`Never`) mixes with steps of any error type. Steps with different error types do not combine on their own, because C# cannot pick the union for you; use `Widen<F>()` or a Gen block. Once `if` or loops appear, a Gen block reads better.

## Type inference tips

- When a lambda returns different effect types on each branch, write the type argument:

```csharp
attempt.AndThen<int>(n => n > 0 ? Effect<R>.Succeed(n) : Effect<R>.Fail<AppError>(new NotFound(n)));
```

  With `<int>`, both branches convert to `Effect<int, AppError, R>`. Without it the call does not compile (CS0411).
- A `Result<T, E>` converts to `Effect<T, E, R>` where the target type is known. Where inference is needed, use `Effect<R>.FromResult(result)`.
- When mixing `Never` and another error type in `Effect.All`, specify its type arguments or give its input collection a common element type. Typing only the result variable does not help. See [Type inference](/concurrency/#type-inference), or use `scope.Run` in a Gen block.
