# Caveats

## Versions

Target .NET 11 or later with C# 15 union support. Pin the SDK and packages used by your application. Breaking changes are listed in the [changelog](https://github.com/SingUp009/Moderato.Effects/blob/main/CHANGELOG.md).

- The .NET 11 RC2 compiler can wrongly report CS8655 on a `switch` over an `Exit` when a call in a loop passes `default` for a `CancellationToken`. Pass the token explicitly.

## Type inference

- Write `Fail`'s type argument: `Effect<R>.Fail<AppError>(new NotFound(id))`.
- When the branches of a conditional return different effect types, write the result type: `AndThen<int>(...)`.
- `Effect.All` over effects with `Never` and with `AppError` errors cannot infer `E`; specify its type arguments or type its input collection (see [Type inference](Concurrency#type-inference)). Typing only the result variable does not help.
- Steps with different error types do not combine in LINQ; use `Widen<F>()` or a Gen block.

## Timeouts do not stop work

`Timeout` requests cancellation and waits for the work and its releases to finish. Work that ignores its `CancellationToken` keeps running, and the caller waits for it. See [Time](Time#timeouts-are-cooperative).

## Idempotency

Running an effect again runs its side effects again. `Retry`, `Repeat` and a second `RunAsync` all repeat them, and nothing caches a result. A timed-out attempt may already have finished its work. Retry only what is safe to repeat; see [Retry and repeat](Retry-and-repeat#idempotency).

## Cancellation

- If the caller's token is canceled, any `OperationCanceledException` counts as `Interrupted`, even one from another token or from no token. Telling them apart would need the library to track every linked token, for little gain.
- An API that throws `ObjectDisposedException` or `IOException` when canceled produces a `Die`, so shutdown can log errors.
- The token an effect sees is derived from the caller's, not the same instance.
- A step or policy that never yields can keep a thread busy until it checks its token.

## Gen blocks

- Variables captured from outside the lambda are shared by every run, including retries.
- `AsyncLocal` and other ambient state can depend on async boundaries. See [reassociating effects](Operators#reassociating-effects) before regrouping callbacks that read or write it.
- Catching `ScopeFailure` and continuing cannot turn the block's Err into Ok, but plain code after the `catch` still runs. MEFF301 reports such a `catch`.
- A step that is not awaited and ignores cancellation delays the end of the block until it finishes. This is intentional, so that nothing outlives the block.

## Resources

- Nothing stops a resource's value from escaping `Use`.
- Releases run with an uncanceled token and are awaited, so a slow release delays the result.

## Unions

- A nullable case holding `null` and an uninitialized `default` union are the same value (MEFF005).
- Generic attributed targets and targets declared inside another type are not supported. Closed generic union cases inside a non-generic target are supported within the analysis budget (MEFF007).
- `Catch` removes one direct case at a time.
- Automatic conversions reject overlapping case types (MEFF008), including interface pairs that could share an implementation. Wrap the alternatives in distinct records; see [Error unions](Error-unions#overlapping-case-types).

## Environments and threads

- Services in the environment are shared by everything that runs with it. With `Concurrency.Parallel`, use thread-safe services or factories, such as `IDbContextFactory<T>` instead of a `DbContext`.
- `EffectDiagnostics.UnobservedFailureHandler` is global.

## Not provided

- Running an effect synchronously, since blocking on async work causes deadlocks.
- Streams; use `IAsyncEnumerable`, `Channel` or R3.
- Forking work that outlives its scope; use a hosted service.
- An environment that wraps `IServiceProvider`.

## Validation coverage

The current validation uses the repository's .NET 11 RC3 SDK/runtime. GA revalidation and long-duration checks of the latest source remain outstanding; an earlier soak does not establish coverage of later changes.

Core, Hosting and HTTP have Native AOT consumer checks. That does not establish Native AOT compatibility for EF or your application's dependencies. EF integration validation initially covers SQLite, not PostgreSQL or real network failures.

HTTP has request, package-consumer and OpenAPI checks. Process-isolated concurrent/long-duration performance and weak-retention work remain tracked in [#403](https://github.com/SingUp009/Moderato.Effects/issues/403). Tracker disabled completion overhead is tracked in [#401](https://github.com/SingUp009/Moderato.Effects/issues/401). Treat benchmark observations as measurements of their stated scenarios, not a guarantee for every application.
