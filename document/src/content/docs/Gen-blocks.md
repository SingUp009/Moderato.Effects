---
title: Gen blocks
---

# Gen blocks

A `Gen` block lets you write an effect as an `async` lambda. It is the main way to write effects; LINQ query syntax suits short chains (see [Operators](/operators/)).

```csharp
static Effect<Profile, AppError, R> GetUserProfile<R>(int id) where R : IHas<IUserRepository>, IHas<TimeProvider> =>
    Scope<AppError, R>.Gen(async scope =>
    {
        var user = await scope.Run(FindUser<R>(id));       // an Err ends the block here
        if (!user.IsActive) throw scope.Fail(new Forbidden("inactive"));
        var now = scope.Env.Resolve<TimeProvider>().GetUtcNow();
        return new Profile(user.Name, now);
    });
```

Both forms infer the result type `T`:

```csharp
Scope<AppError, R>.Gen(async scope => ...);                    // E and R from the Scope type
Effect.Gen(async (Scope<AppError, R> scope) => ...);           // E and R from the parameter's type
```

Each run calls the lambda again with a new scope. Variables captured from outside the lambda are shared between runs.

## The scope

| Member | Behavior |
|---|---|
| `await scope.Run(effect)` | Runs an effect and returns its value. An Err ends the block. |
| `scope.Get(result)` | Returns the value of a `Result`. An Err ends the block. |
| `throw scope.Fail(error)` | Ends the block with the error. |
| `await scope.Acquire(resource)` | Acquires a resource, released when the block ends (see [Resources](/resources/)). |
| `await scope.Fork(effect)` | Starts a fiber, canceled and awaited when the block ends (see [Concurrency](/concurrency/)). |
| `scope.Defer(exit => ...)` | Registers a release action that runs when the block ends. |
| `scope.Env` | The environment. |
| `scope.CancellationToken` | The token of the current run. |

`Run`, `Get` and `Acquire` also accept an effect, result or resource whose error is one case of the block's error union, such as an `Effect<User, NotFound, R>` in a `Scope<AppError, R>` block.

`Fail` throws immediately. The explicit `throw` also tells the compiler that execution cannot continue, so null checks and return paths are analyzed correctly:

```csharp
var user = await repository.FindAsync(id, scope.CancellationToken);
return user ?? throw scope.Fail(new NotFound(id));
```

## How an Err ends the block

When an Err appears, the scope throws a `ScopeFailure` to unwind the block. `finally` blocks and `using` declarations still run, and the block's result is that Err.

- The scope keeps the first Err. Catching its `ScopeFailure` and returning normally cannot turn it into Ok. Other failures are combined using the [failure precedence rules](/failures/#which-failure-wins).
- After an Err, every later `Run`, `Get`, `Acquire` and `Fork` ends the block again at once, without executing that step's work.
- So `scope.Run` inside `finally` does not run after an Err. Write cleanup with `scope.Defer` or a resource:

```csharp
var tx = await scope.Run(BeginTransaction<R>());
scope.Defer(exit => exit is Succeeded ? Commit<R>(tx) : Rollback<R>(tx));
```

Release actions run when the block ends, in reverse order of registration, and receive an `ExitCase` describing how the block ended (`Succeeded`, `Failed`, `Die` or `Interrupted`).

## Exceptions and try/catch

The lambda is ordinary C#. You do not need `try`/`catch` for expected errors, but you may write it.

- An exception from ordinary code is a [defect](/failures/) (`Die`). To make it an expected error, wrap the call: `scope.Get(Result.Try(...))` or `await scope.Run(Effect<R>.TryAsync(...))`.
- A `catch` for a specific type, such as `catch (JsonException)`, is safe.
- A catch-all `catch` would also catch `ScopeFailure`. Filter it out:

```csharp
try { ... }
catch (Exception ex) when (ex is not ScopeFailure) { ... }
```

- Do not use the scope inside a lambda passed to `Result.Try`: it does not know `ScopeFailure` and would turn it into an ordinary Err.
- If a `catch` throws a different exception, that exception is the result (`Die`) and the first Err is kept as context.

## Rules for the scope

The analyzers report most of these at compile time; the runtime enforces the rest.

- **Await every step**. When the body finishes, the block captures steps whose original ValueTask result has not been read, cancels unfinished ones and waits before releasing resources. An Err or defect from a captured step is retained even if an adapter reads it later. This is a cleanup guarantee, not a detector for every missing await; see the adapter rules below.
- **One flow at a time.** Using the scope from two flows at once throws `InvalidOperationException`. To run effects in parallel, use `scope.Run(Effect.All(..., Concurrency.Parallel))`.
- **Only inside its own block.** Using it after the block has ended, or from a nested Gen block, throws `InvalidOperationException`. To fail the outer block from a nested one, run the nested block with the outer scope: `await scope.Run(inner)`.
- **Loops.** `Acquire`, `Defer` and `Fork` on the outer scope hold every resource until the block ends. Use a nested Gen block or `resource.Use` per iteration.
- **Do not block.** `.Result`, `.Wait()` and `GetAwaiter().GetResult()` block the thread.

A defect already awaited and caught by the body stays handled, even if the step had previously been queued as unobserved.

At runtime, a block never turns a recorded Err into Ok. Code after a failing step is skipped unless `ScopeFailure` is caught; `finally` blocks and registered cleanup still run.

These lifetime guarantees cover work started through scope methods. Ordinary asynchronous calls in the lambda remain ordinary C#: await their returned tasks too.

## ValueTask adapters and failure ownership

`AsTask` and `Preserve` can consume a step's ValueTask result before your code awaits the resulting Task. Always await that result inside the Gen body:

```csharp
var value = await scope.Run(effect).AsTask();
```

If an adapter consumes a defect before the body ends, the exception remains on its faulted Task. Gen can return Ok, and `EffectDiagnostics.UnobservedFailureHandler` does not receive that transferred exception. You must await the Task to handle it. Expected errors are different: the scope records an Err independently, so an adapter cannot turn it into Ok.

If the original result is still unread when the body ends, Gen captures it for cleanup. An adapter reading it afterwards does not remove the captured failure. The timing distinction is another reason to await each step immediately.

An abandoned faulted Task can raise the standard `TaskScheduler.UnobservedTaskException` event during finalization. That depends on GC timing; it is a fallback, not reliable error handling. MEFF310 is a Warning by default. Promote it to Error in applications that should reject this misuse in CI; see [Adopting in an application](/adopting-in-an-application/).

## Threads

A Gen block does not force execution onto a particular thread. By default, `await` captures the current synchronization context when one exists. The library's own awaits use `ConfigureAwait(false)`.
