# Adopting in an application

Start with one boundary where expected failures already have names: a repository call, a validation pipeline or a background job. Keep the application's existing hosting and dependency injection. Effects describe the work; the host still owns requests, shutdown and service lifetimes.

Keep the included analyzers enabled. In particular, MEFF310 catches scope steps that are not immediately awaited. To reject these mistakes in CI, set its severity to error in `.editorconfig`:

```ini
dotnet_diagnostic.MEFF310.severity = error
```

## Define expected failures

Use `E` for failures the caller can make a decision about, such as invalid input or a missing record. Convert only the exceptions you intend to recover from with `Try` or `TryAsync`. An unexpected exception remains a defect, so it reaches your application's error reporting.

Run the effect at the application boundary. `RunAsync` returns expected errors as `Result` and throws defects or cancellation; integrate it with the host's existing exception handling. `RunExitAsync` returns the four outcomes as values when the boundary needs to inspect them directly. An `Err` does not undo side effects.

> [!warning]
> Effects do not guarantee idempotency. Running an effect again, including through `Retry` or `Repeat`, can repeat side effects. A failed or interrupted run may already have changed external state. Use idempotency keys or another application-level deduplication mechanism when repeated execution must be safe.

## Align lifetimes and cancellation

An environment holding a scoped service must have that scope's lifetime. Sequential execution applies within one group; sharing an environment across runs can still access its services concurrently. Use the factory pattern for services such as database contexts when running work in parallel. See [Environments](Environments).

Pass the request or worker's cancellation token at the run boundary, and pass the effect's token to asynchronous dependencies. Timeouts request cancellation and wait for cleanup; they do not provide a wall-clock return deadline. Cleanup runs with an uncanceled token. Choose cleanup operations that eventually finish, and monitor their duration. See [Resources](Resources) and [Time](Time).

Retry only operations that are safe to repeat. A timed-out write may have committed before cancellation arrived. Use an idempotency key or restrict the retried part to safe work. See [Retry and repeat](Retry-and-repeat#idempotency).

## Keep failures observable

Handle every expected error at the boundary and log defects with their suppressed failure context. For registered workers or HTTP requests, use the lifetime-aware connections in [Hosting](Hosting) or [HTTP](Http). For a standalone application, install `EffectDiagnostics.UnobservedFailureHandler` once during startup if it uses a structured logger. The handler is global; replacing it per request would affect unrelated runs.

Await scope steps and their `AsTask` or `Preserve` results. Adapting a step is not the same as awaiting it: an adapter can transfer its exception to a Task before Gen captures unread results. That Task then follows the standard .NET observation rules, including a GC-dependent fallback for unobserved exceptions. See [ValueTask adapters and failure ownership](Gen-blocks#valuetask-adapters-and-failure-ownership).

## Test the behavior your application needs

Use explicit environments and a fake `TimeProvider` for time-dependent code. Check success, each expected error, defects, cancellation and release order. Add a case where cancellation arrives after an external operation has already changed state. Test parallel use with the service lifetimes you actually deploy.

For Native AOT applications, publish and execute your application with Native AOT in CI. The library's package checks cover its own consumer scenarios, not every dependency or reflection path in your application.
