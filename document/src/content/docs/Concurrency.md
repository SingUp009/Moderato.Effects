---
title: Concurrency
---

# Concurrency

.NET runs work on several threads at once. The defaults are chosen for that: effects in a group run one at a time unless you ask otherwise, and nothing outlives the scope that started it.

## Concurrency

```csharp
Effect.All(effects);                                          // one at a time, in order (the default)
Effect.All(effects, Concurrency.Parallel);                    // all at once
Effect.All(effects, Concurrency.Parallel.WithMaxDegree(4));   // at most 4 at a time
```

- `Sequential` runs on the caller's flow. It is the default (`default(Concurrency)`) because services in the environment, such as a `DbContext`, are often not thread-safe.
- `Parallel` starts each effect on the thread pool, so one that blocks before its first `await` cannot keep the others from starting.
- `Concurrency.Sequential.WithMaxDegree(n)` throws; a limit only applies to parallel runs.

To move a single CPU-bound effect off the caller's thread, use `effect.OnThreadPool()`.

## All

```csharp
Effect<(User, Settings), AppError, R> both = Effect.All(FindUser<R>(id), LoadSettings<R>(id), Concurrency.Parallel);
Effect<User[], AppError, R> many = Effect.All(ids.Select(FindUser<R>), Concurrency.Parallel);
```

- Two to eight effects give a tuple; a sequence gives an array in input order.
- **Fail-fast:** the first failure cancels the rest, and the result waits until they have finished, releases included.
- The failure that started the cancellation is the result. Failures of the others caused by that cancellation are kept as context. A defect that happened independently outranks an Err (see [Failures](/failures/)).

The sequence overloads of `All`, `AllSettled` and `AllErrors` capture a copy of their input descriptions when you build the group. They validate the items then, without running the effects. Later changes to the source array or list do not change a built group; each run executes the captured descriptions again. For iterator inputs, enumeration and its exceptions also occur during construction. Captured objects and environment services keep their normal .NET sharing semantics.

### AllSettled and AllErrors

| API | Type | Behavior |
|---|---|---|
| `Effect.AllSettled(effects)` | `Effect<Exit<T, E>[], Never, R>` | Runs every effect to the end and collects how each ended |
| `Effect.AllErrors(effects)` | `Effect<T[], E[], R>` | Collects every error instead of stopping at the first, as input validation needs; a defect still cancels the rest |

If the caller cancels and an effect turns that cancellation into an Err (for example `catch (OperationCanceledException)` followed by `scope.Fail`), `All` and `AllErrors` end with `Interrupted` and keep that Err as context. `AllSettled` collects it like any other Err.

An interrupted child does not always mean the caller canceled: joining a fiber whose scope has ended can also return `Interrupted`. `AllErrors` returns `Interrupted` rather than a partial validation result in that case, unless a defect takes priority. `AllSettled` can represent it directly: with an uncanceled caller, it succeeds with an `Exit[]` containing that child's `Interrupted`. If caller cancellation leaves children unrun or interrupted, the group itself is interrupted.

`AllSettled` also keeps a child's `Die` as an array element; that defect does not by itself make the group die. If the group instead ends interrupted, child defects remain as context and are reported to `EffectDiagnostics.UnobservedFailureHandler` when `RunAsync` or `RunExitAsync` exposes that interruption.

## Race

```csharp
var fastest = Effect.Race(FromPrimary<R>(key), FromReplica<R>(key));
```

| API | Takes | When both fail | A defect |
|---|---|---|---|
| `Race(a, b)` | the first to **succeed** | the first failure; the other is context | ends the race at once |
| `RaceFirst(a, b)` | the first to **finish**, Ok or not | — | on the losing side still outranks the winner |

The loser is canceled and awaited. If the loser's release fails, that defect outranks the winner's result, so a failing release is never dropped.

## Fibers

A fiber is work that runs alongside its parent. It never outlives the parent's scope: when the scope ends, the fiber is canceled and the scope waits for it.

```csharp
var fiber = await scope.Fork(Compute<R>());
// ... other work ...
var value = await scope.Run(fiber.Join());
```

Outside a Gen block, `effect.Fork()` is a resource: acquiring it starts the fiber, releasing it cancels and awaits it.

- A **defect** in a fiber cancels the parent, which ends with that defect.
- An **Err** shows only through `Join`, since the fiber's error type may not fit the parent's. If the fiber is never joined, its Err goes to `EffectDiagnostics.UnobservedFailureHandler`.
- Canceling a `Join` leaves the fiber running; it is still canceled with its scope.
- Closing a scope does not turn an already completed success into interruption. `Join` can still return that value; if the fiber ended with `Interrupted`, it propagates that outcome even when the joining caller has not canceled.

There is no detached fork. Work that must outlive a request belongs in a hosted service.

## Using the scope in parallel

A scope is used from one flow at a time. To run steps in parallel inside a Gen block, run a group:

```csharp
var (user, settings) = await scope.Run(Effect.All(FindUser<R>(id), LoadSettings<R>(id), Concurrency.Parallel));
```

## Type inference

`Effect.All([an effect that cannot fail, an AppError effect])` cannot infer `E`. Specify the type arguments, such as `Effect.All<int, AppError, R>([pure, fallible])`, or give the input collection a common element type:

```csharp
Effect<int, string, EmptyEnvironment>[] steps =
[
    Effect<EmptyEnvironment>.Succeed(1),
    Effect<EmptyEnvironment>.Fail("unavailable")
];
var grouped = Effect.All(steps);
```

Typing only the result variable does not help method type inference. A Gen block is another way to combine steps with different error types.
