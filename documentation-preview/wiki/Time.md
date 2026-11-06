# Time

Effects that wait need a clock, and they get it from the environment: `where R : IHas<TimeProvider>`. A dependency on time shows in the signature, and tests can swap the clock.

## Sleep

```csharp
await scope.Run(Effect.Sleep<R>(TimeSpan.FromSeconds(1)));
```

- Waits on the environment's `TimeProvider`. Canceling the run cuts the wait short.
- The duration is from zero up to about 49.7 days (the timer's limit). `Timeout.InfiniteTimeSpan` waits until canceled. Anything else throws `ArgumentOutOfRangeException` when the effect is created, not when it runs.

## Timeout

When the time runs out, the effect is canceled and the result is `Err(TimeoutError)`. The library defines `public sealed record TimeoutError(TimeSpan After)`.

| Overload | When | Result type |
|---|---|---|
| `Timeout(after)` | `E` is a union with `TimeoutError` as a case | `Effect<T, E, R>` |
| `Timeout(after)` | `E` is `Never` or `TimeoutError` | `Effect<T, TimeoutError, R>` |
| `Timeout(after, onTimeout: t => error)` | you map it to your own error | `Effect<T, E, R>` |
| `TimeoutResult(after)` | you want the timeout as a value | `Effect<Result<T, TimeoutError>, E, R>` |

```csharp
[Error]
public partial union AppError(NotFound, Forbidden, TimeoutError);

Effect<Profile, AppError, R> limited = GetUserProfile<R>(id).Timeout(TimeSpan.FromSeconds(5));
```

The limit follows the same range rules as `Sleep`.

`TimeSpan.Zero` means the budget has already expired. It returns the timeout result without starting the effect, acquiring its resources or creating a timer, even for a constant `Succeed`. `TimeoutResult` returns `Ok(Err(TimeoutError))`; `onTimeout` runs once per execution. If the caller is already canceled, interruption wins and the mapper is not called. `Sleep(TimeSpan.Zero)` still succeeds immediately, and `Timeout.InfiniteTimeSpan` disables the deadline.

This skips execution, not construction: any ordinary C# expression evaluated before you built the description has already run. A completed `Task` also differs from an effect description: its work has already finished, whereas a lazy effect has not been started by this run.

### Timeouts are cooperative

On timeout, the effect is canceled, and the result waits until the effect and its releases have finished. Work that ignores its token is not cut short.

| Property | Guaranteed |
|---|---|
| Cancellation is requested when the time is up | yes |
| The result waits for the work and its releases to finish | yes, if they finish |
| The caller gets control back within the limit | no |
| Work that ignores cancellation is stopped | no |

The library never returns early and leaves work running in the background. Releases run with an uncanceled token, so a slow release also delays the result. This trades a strict deadline for `finally` blocks that always run.

### Where it goes matters

```csharp
effect.Timeout(TimeSpan.FromSeconds(2)).Retry(schedule);    // 2 seconds per attempt
effect.Retry(schedule).Timeout(TimeSpan.FromSeconds(30));   // 30 seconds for everything
```

### Timeouts and the caller's cancellation

If the caller cancels while the timeout fires, the caller wins: the result is `Interrupted`, not `Err(TimeoutError)`. A Gen block canceled by a timeout sees `Interrupted` in its release actions; the outer `Timeout` turns it into `Err(TimeoutError)`.

## The system clock inside effects

Reading the system clock inside an effect bypasses the environment, and tests cannot control it. The analyzer MEFF307 reports these inside Gen blocks and inside the arguments of `Effect<R>` factories:

`DateTime.Now`, `DateTime.UtcNow`, `DateTime.Today`, `DateTimeOffset.Now`, `DateTimeOffset.UtcNow`, `TimeProvider.System`, `Task.Delay`, `Thread.Sleep`, `Stopwatch.GetTimestamp`, `Stopwatch.StartNew`, `Environment.TickCount`, `Environment.TickCount64`

Use `scope.Env.Resolve<TimeProvider>().GetUtcNow()` and `Effect.Sleep<R>` instead. Overloads that take a `TimeProvider`, such as `Task.Delay(delay, timeProvider)`, are not reported. MEFF307 is a suggestion (Info) by default; raise it in `.editorconfig` if your tests rely on a fake clock:

```ini
dotnet_diagnostic.MEFF307.severity = warning
```

Also remember that `Effect<R>.Succeed(DateTime.UtcNow)` reads the clock once, when the effect is created. Use `Sync` or `Access` for a value that must be read on every run.

## Testing with a fake clock

```csharp
[Environment]
public sealed partial record TestEnv([Expose<TimeProvider>] FakeTimeProvider Clock);

var clock = new FakeTimeProvider();
var run = Effect.Sleep<TestEnv>(TimeSpan.FromMinutes(5)).RunAsync(new TestEnv(clock)).AsTask();
clock.Advance(TimeSpan.FromMinutes(5));
await run;
```

`FakeTimeProvider` is in the `Microsoft.Extensions.TimeProvider.Testing` package.
