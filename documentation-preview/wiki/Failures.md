# Failures

## Three kinds of failure

| Kind | What it is | Where it goes |
|---|---|---|
| **Err** | an expected error, in `E` | `Err<E>` in the `Result` |
| **Die** | a bug: an exception the effect did not turn into an Err | thrown by `RunAsync` |
| **Interrupted** | an execution was interrupted, including caller cancellation or propagating a fiber's interruption | `OperationCanceledException` from `RunAsync` |

`RunExitAsync` returns all four outcomes as values, without throwing:

```csharp
Exit<Profile, AppError> exit = await GetUserProfile<AppEnv>(id).RunExitAsync(env, ct);

var message = exit switch
{
    Ok<Profile>(var p)       => p.Name,
    Err<AppError>(var e)     => $"error: {e}",
    Die(var exception)       => $"bug: {exception.Message}",
    Interrupted              => "canceled",
};
```

`RunAsync` rethrows a defect's original exception with its original stack trace, so middleware sees the same exception type.

## What counts as Interrupted

An `OperationCanceledException` thrown by ordinary effect code becomes `Interrupted` when that execution's token is canceled. With an uncanceled token, it is a defect. A library operation can also propagate interruption: `Join` can return a fiber's `Interrupted` even when the joining execution's token is not canceled.

`Interrupted`, including the `ExitCase` passed to a release, does not tell you whether an external operation completed before cancellation was observed. Retry only work that is [safe to repeat](Retry-and-repeat#idempotency).

| Cancellation from | Result |
|---|---|
| the caller's token (including app shutdown linked to it) | `Interrupted` |
| `Timeout` | `Err(TimeoutError)` |
| a `Race` loser, an `All` sibling, the children of an ended scope | not visible from outside |
| another token's OCE (such as `HttpClient`'s timeout) while the caller's token is not canceled | `Die` (wrap it with `Try` to make it an Err) |
| any OCE while the caller's token is canceled | `Interrupted`, see [Caveats](Caveats) |
| joining a fiber that ended with `Interrupted` | `Interrupted`, even if the current caller has not canceled |
| a fiber's defect that cancels its parent | the fiber's `Die` |

Some APIs throw `ObjectDisposedException` or `IOException` instead of an OCE when canceled. That is a `Die` by design, so a shutdown can log errors.

### The token an effect sees

`RunAsync` and `RunExitAsync` do not pass the caller's token through. They pass a token of their own that is canceled when the caller's is. Cancellation reaches the effect, but the token is not the same instance. If a callback registered on a library token throws when the caller cancels, the exception does not escape the caller's `Cancel()`; it becomes part of the result.

## Suppressed failures

Concurrency, releases and timeouts can produce several failures at once. The result has one primary failure. A primary `Die` exposes the other failures as context in `Die.Suppressed`:

```csharp
if (exit is Die die)
{
    foreach (var s in die.Suppressed)
        Console.WriteLine($"{s.Relation}: {s.Case.Value}");
}
```

| `Relation` | Meaning |
|---|---|
| `Sequential` | Came from a sequential part of the computation, such as its body or release |
| `Parallel` | Came from independent work, such as a parallel sibling |
| `Consequence` | Classified as a consequence of cancellation when observed |

The labels describe how failures are combined; they are not timestamps relative to the primary failure. If the body returns an Err and a later release dies, the earlier Err is `Sequential` context on the release's Die. `Parallel` does not require failures to be observed at the same instant.

`Suppressed.Case` is an `ExitCase`: `Failed`, `Die` or `Interrupted`. At most 100 are kept per outcome; `Die.OmittedSuppressed` counts the rest. `RunAsync` attaches the suppressed failures to the rethrown exception's `Data` under `Effect<T, E, R>.SUPPRESSED_DATA_KEY`.

Separate throws of the same exception instance are separate failures. A child's failure propagated through `Join` is counted once; if the parent's body throws that exception again in a separate operation, its failure is also kept as context.

Failure presentation unwraps errors through the runtime `System.Runtime.CompilerServices.IUnion` interface. A custom `[Union]` type that only implements an `IUnionMembers` provider stays as the error object in `Failed`; its payload remains available through typed matching. Implement `IUnion` as well when runtime reports should expose the contained case.

When cleanup or diagnostics needs to unwrap an error union, a throwing getter or a reference cycle produces a `Die` that keeps the unreadable error as context. Registered cleanup still runs. Finite chains have no fixed depth limit, and each getter is read once during this projection.

If reading, replacing or removing that metadata fails, `RunAsync` still rethrows the original exception. The metadata failure and the context it could not attach go to `EffectDiagnostics.UnobservedFailureHandler`. Use `RunExitAsync` to receive the context directly in `Die`, without relying on `Exception.Data`.

## Which failure wins

1. **Die**, from a body that failed on its own or from a release. The earliest one wins.
2. **Interrupted**, when the caller canceled before the body ended.
3. **The outcome the cancellation decided**: `Err(TimeoutError)` for `Timeout`, the first independent failure for `All`, the winner for `Race`.
4. **The ordinary Ok or Err.**

A failure classified as a consequence of cancellation is only context (`Consequence`). Classification uses the execution's cancellation state when the library observes the failure; it does not infer its physical cause. An observed failure keeps its classification when propagated. Cleanup failures remain independent. An OCE from the effect's own cancellation is dropped. If the caller canceled and the effect then threw something other than an OCE, with no other failure to report, the result is that `Die`.

| Situation | Result |
|---|---|
| Timeout, then the work ends with an OCE | `Err(TimeoutError)` |
| Timeout, then a release throws | `Die`, with the timeout as context |
| Timeout, then the work throws `ObjectDisposedException` | `Err(TimeoutError)`; the suppressed `Die` is [reported to diagnostics](#failures-nobody-sees) |
| `All`: A fails, and B throws when canceled | `Err(A)`, with B's `Die` as context |
| `All`: A fails, and B dies independently before the cancellation | `Die(B)`, with `Err(A)` as context |
| `Race`: the loser's release throws after a winner is chosen | `Die`; the winner's Ok is dropped |
| The caller cancels while a timeout fires | `Interrupted` |
| The caller cancels, and the work throws `IOException` | `Die` |
| A fiber dies, and the parent's body then ends with an OCE | the fiber's `Die` |

## Failures nobody sees

Some failures have no outcome to travel in:

- defects suppressed under an Err or Ok that reaches `RunAsync` (a `Result` cannot carry them);
- defects suppressed under an Err that `OrElse`, `Retry`, `CatchCase` or `Attempt` handled;
- the Err of a fiber that was never joined.

These go to `EffectDiagnostics.UnobservedFailureHandler`, which by default writes to `Console.Error`:

```csharp
EffectDiagnostics.UnobservedFailureHandler = failure => logger.LogError("Unobserved failure: {Failure}", failure.Value);
```

- The handler receives a `Die` or a `Failed`.
- `null` restores the default.
- Exceptions thrown by the handler are ignored, and reporting never changes an outcome.
- The handler is global, so tests that replace it cannot run in parallel.

Suppressed Errs are not reported; they are dropped with the Err that handled them.

A defect already transferred from a scope step to a Task before Gen's body snapshot follows normal Task observation rules instead of this handler. Await the adapter's result; see [ValueTask adapters and failure ownership](Gen-blocks#valuetask-adapters-and-failure-ownership).

## Per-execution diagnostics

Pass `EffectRunOptions` to scope diagnostic reporting to one execution and its children, including recovery and cleanup:

```csharp
var options = new EffectRunOptions { UnobservedFailureHandler = reporter };
var exit = await effect.RunExitAsync(environment, cancellationToken, options);
```

The token and options arguments are both required on this overload, preserving existing calls that use `default`. A null option handler uses the global handler as it is at report time. Options never change the global setting or use AsyncLocal. The handler can be called concurrently and must be thread-safe; its exceptions do not change the effect result. [Hosting](Hosting) manages such connections and their disposal for registered workers.

## Inspect an ExitCase

When a release or diagnostic callback receives an `ExitCase`, query the case you need:

```csharp
if (failure.TryGetValue(out Die? die))
    Console.Error.WriteLine(die.Exception.Message);
```

A successful query returns a non-null case. A mismatched, default or null case returns false and a null output. These accessors do not change failure precedence or add an outcome to the execution.
