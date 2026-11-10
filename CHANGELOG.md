# Changelog

All notable changes to this project are documented here.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed

- Report scope misuse in typed catches and exception filters that accept `ScopeFailure`. ([#444](https://github.com/SingUp009/Moderato.Effects/issues/444))
- Preserve the last `Retry` or `Repeat` result when the schedule is exhausted, without calling its predicate again. ([#445](https://github.com/SingUp009/Moderato.Effects/issues/445))
- Keep a constant effect's `WithName` name and location, or its factory name, in recordings after the implicit `Never` conversions and `WidenEnv`, and record `Timeout` and `TimeoutResult` under their own names instead of `Create`. ([#457](https://github.com/SingUp009/Moderato.Effects/issues/457))

## [0.1.0-preview.6] - 2026-11-09

### Added

- Emit standard .NET activities and completion/duration metrics for Effect runs, scopes, fibers, acquisition, resource lifetimes and release attempts, independently of debug tracking. Core has no OpenTelemetry SDK or exporter dependency. ([#439](https://github.com/SingUp009/Moderato.Effects/pull/439))
- Add bounded execution recording with `EffectTracker.Record()`, named operations through `WithName`, immutable snapshots, resource ownership, monotonic timing, body/final outcomes and trace/span correlation IDs. Recording and standard telemetry share observation points but remain independently enabled. ([#439](https://github.com/SingUp009/Moderato.Effects/pull/439))
- Add a read-only local Blazor viewer and real HTTP samples with OTLP export. Document the two views using screenshots of the same request. The viewer observes its own process and is intended for access from the same PC. ([#439](https://github.com/SingUp009/Moderato.Effects/pull/439), [#440](https://github.com/SingUp009/Moderato.Effects/pull/440))

### Changed

- Keep fixed timeout settings in the construction-time closure, preserving the existing finite timeout-nesting verification depth after adding observation metadata. Arbitrarily deep control-wrapper nesting remains unsupported. ([#439](https://github.com/SingUp009/Moderato.Effects/pull/439))

### Validation and known limitations

- This release updates `Moderato.Effects` only. Hosting, HTTP and EF retain their independently versioned preview.5 packages.
- Validated with the .NET 11 RC3 SDK/runtime pinned in `global.json`; .NET 11 GA revalidation remains outstanding.
- Core, Hosting and HTTP have JIT and Linux x64 Native AOT package-consumer coverage. EF has JIT and SQLite coverage; EF Native AOT, PostgreSQL and real network failures are not validated.
- Validation includes 2,555 solution tests, process-isolated deep-composition checks, short sustained-execution checks and 120 real HTTP requests with matching digests. No new long-duration soak is included; it remains deferred to an always-on PC.
- Recording retains bounded metadata and can drop or evict records. Trace sampling and exporter queues independently affect telemetry delivery. Neither release completion nor an observed interruption proves whether an external operation completed.
- Observation adds time and allocation costs when enabled. Runtime allocation metrics are process-wide, not per Effect; nested durations overlap and are not additive CPU time. Short-run measurements do not establish performance under sustained production load.

## [0.1.0-preview.5] - 2026-11-06

### Added

- Classify EF optimistic concurrency conflicts as `OptimisticConcurrencyConflict`, preserving the original exception and existing save-mapping policy. No automatic retry or conflict resolution is added. ([#424](https://github.com/SingUp009/Moderato.Effects/pull/424))
- Add an EF Core integration with owned transactions, scope-aware saves and standard `CommitNotConfirmed` errors. Include a ConsoleAppFramework SQLite CRUD sample and package-consumer checks. Initial database validation is limited to SQLite; PostgreSQL, EF Native AOT and real network failures are not validated. ([#420](https://github.com/SingUp009/Moderato.Effects/pull/420))
- Add `ExitCase.HasValue` and typed `TryGetValue` access to `Succeeded`, `Failed`, `Die` and `Interrupted`. A successful query provides a non-null case; mismatched, default and null cases return false with a null output. Direct case patterns remain available. ([#414](https://github.com/SingUp009/Moderato.Effects/pull/414))

### Changed

- Organize source, tests, benchmarks and samples by feature, and shorten public API XML documentation without changing library behavior or API signatures. ([#422](https://github.com/SingUp009/Moderato.Effects/pull/422))
- Preserve each package's own version during validation and release only the package selected by its tag. Core uses `v<Version>`; extensions use `<PackageId>/v<Version>`. Extension publication checks that its required Core version is already available on NuGet. ([#426](https://github.com/SingUp009/Moderato.Effects/pull/426))

### Validation and known limitations

- Validated with the .NET 11 RC3 SDK/runtime pinned in `global.json`; .NET 11 GA revalidation remains outstanding.
- Core, Hosting and HTTP have JIT and Native AOT package-consumer coverage. EF has JIT package-consumer and SQLite integration coverage; EF Native AOT, PostgreSQL and real network failures are not validated. Retrying execution strategies and contexts with an existing transaction are explicitly rejected.
- No new final-source long-duration validation is included. The earlier soak targeted commit `611ce78`; it is not evidence for this release. Current-source soak remains deferred to an always-on PC.
- Open follow-ups include [#377](https://github.com/SingUp009/Moderato.Effects/issues/377) for the earlier late-run Gen slowdown, [#401](https://github.com/SingUp009/Moderato.Effects/issues/401) for disabled Tracker completion overhead, [#403](https://github.com/SingUp009/Moderato.Effects/issues/403) for further HTTP validation, and [#409](https://github.com/SingUp009/Moderato.Effects/issues/409) for broader database integration requirements. Interrupted outcomes and unconfirmed commits do not establish whether an external write completed; no automatic database retry is added.

## [0.1.0-preview.4] - 2026-11-05

### Added

- Add the separate `Moderato.Effects.Http` preview package. `AddEffectsHttp()` registers a scoped `EffectRequest` that awaits execution and cleanup with the current request token, returns typed results, and preserves defects or unexpected interruptions in `EffectHttpException.Exit`. ([#402](https://github.com/SingUp009/Moderato.Effects/pull/402))
- Connect HTTP unobserved failure diagnostics to standard logging, with request and Host disconnection and late-report fallback. Routing, response mapping, authentication and exception handling remain standard ASP.NET Core application code. ([#402](https://github.com/SingUp009/Moderato.Effects/pull/402))
- Add an executable HTTP/OpenAPI sample and HTTP-only package consumer checks, including transitive generators, analyzer errors and Native AOT. Streaming is deferred; .NET 11 GA revalidation remains outstanding. ([#402](https://github.com/SingUp009/Moderato.Effects/pull/402))
- Add an opt-in Effect Tracker for active scopes, fibers and acquired resources. Snapshots contain metadata only; stack capture is separately opt-in. Disabling clears the registry, and enabling tracks future starts without retaining environments or resource values. ([#398](https://github.com/SingUp009/Moderato.Effects/pull/398))

### Changed

- Store the first registered release directly and create a list only when another is added. In the measured warmed synchronous cases, this saves 88 B per run for Gen with one deferred release and Resource.Use with one acquired resource, without changing release order or failure aggregation. This does not imply zero allocation for the whole library. ([#389](https://github.com/SingUp009/Moderato.Effects/pull/389))

### Validation and known limitations

- Validated on the .NET 11 RC3 SDK/runtime selected by `global.json`; .NET 11 GA revalidation remains outstanding.
- The previous long-duration validation targeted commit `611ce78`. This release has unit, integration and package-consumer checks, but no long-duration validation of its final source. Current-main soak is deferred to the user's always-on PC; earlier evidence must not be treated as evidence for this version.
- Follow-up work remains open: [#377](https://github.com/SingUp009/Moderato.Effects/issues/377) for the earlier unexplained late-run Gen slowdown, [#401](https://github.com/SingUp009/Moderato.Effects/issues/401) for disabled Tracker completion overhead, and [#403](https://github.com/SingUp009/Moderato.Effects/issues/403) for HTTP process-isolated measurements, concurrent/long-duration coverage, weak-retention verification and GA revalidation. HTTP timing observations and remaining drift are documented in [the performance report](https://github.com/SingUp009/Moderato.Effects/blob/v0.1.0-preview.4/docs/http-performance.md).

## [0.1.0-preview.3] - 2026-11-04

### Breaking changes

- Recompiled `TapErr(error => BuildEffect(error))` calls now run the returned effect. Use an explicit `Action<E>` when only the callback's synchronous side effects are intended.

- A zero `Timeout` or `TimeoutResult` now expires without starting its body, including constant effects. An already canceled caller still gets `Interrupted`. Use a positive or infinite timeout when the body must run.

- Reject overlapping union case types with MEFF008 instead of generating conversions whose patterns cannot distinguish the alternatives. This includes interface pairs that could share an implementation. Wrap alternatives in distinct records.

- Bound union generation and Catch analysis to 4,096 case-path segments and 256 nodes per type expression. Exceeding either limit reports MEFF007 and emits no code for that generator target, including for finite shapes. Simplify the shape or implement the required interfaces manually.

### Added

- Include structured error type names in Hosting diagnostics for unjoined and suppressed typed failures without formatting error values.

- Package Hosting separately with its own XML docs, symbols, SourceLink and guide; verify a Hosting-only package consumer under JIT and Native AOT.

- Add Host-scoped effect services with typed error handlers, asynchronous DI scopes and injectable diagnostics. Hosting uses the public Core API and preserves defect and cleanup context.

- Add effect-returning `TapErr` observers, keeping the original error as context when observation fails.

- Add explicit `EffectRunOptions` overloads for per-execution diagnostics, including children and cleanup, without changing the global handler or existing run signatures.

### Changed

- Keep pending task storage only in execution frames that can await their own task, reducing deep Bind, Provide and Resource execution allocations without changing the public API or failure/context rules.

- Use .NET 11 RC3 to replace the vulnerable RC2 Native AOT toolchain. Verify uniquely versioned Core and Hosting artifacts in non-tag CI builds.

- Share bounded failure-context operations across outcomes, scopes, concurrency, cancellation, fibers and timeouts, and use one continuation implementation for effect operators.

- Execute core operator composition iteratively, preserving execution-context boundaries and concurrent reuse. Arbitrarily deep nesting of control wrappers such as Gen, Retry and Timeout remains unsupported.

- Document the domain and observations under which effect composition laws hold, including the distinction between explicit environments and ambient AsyncLocal state.

- Clarify that standard AsTask and Preserve adapters consume a scope step when their source GetResult runs. A defect transferred before the body snapshot belongs to the returned Task; callers must await it. The default MEFF310 severity remains Warning.

- Clarify the different interruption contracts of AllErrors and AllSettled, including child interruption without caller cancellation.

### Fixed

- Distinguish same-named types from different assemblies and report MEFF009 when generated code cannot reference a required type by its global name, instead of emitting invalid references or false duplicate-path warnings. This includes the reference to Moderato.Effects itself.

- Preserve reachable conversions through recursive unions whose equivalent constructed types use different object/dynamic or tuple-name spellings.

- Respect C# type identity when checking Catch sources, avoiding MEFF308 for equivalent tuple names and object/dynamic spellings, including manual rest interfaces.

- Discover C# union member-provider factories, including inherited and equivalent case types, while preserving nullable cases and bounded analysis.

- Create intermediate error unions with implicit assignments so object and interface cases are not unboxed or downcast during Widen or nested RestOf conversion.

- Treat an interrupted release as `Die(InvalidOperationException)`, run every remaining release, and aggregate failures using the existing priority rules.

- Treat types implementing only IUnion as opaque cases; only actual C# unions are explored for transitive cases.

- Project timeout results after inner execution and cleanup finish, restoring finite nested timeouts that previously overflowed the stack.

- Detect reference cycles when unwrapping hand-written error unions, preserving the unreadable error as context and allowing releases to run.

- Keep separate parent and child failures when they throw the same exception instance, while counting a joined child's failure only once.

- Preserve step defects read by adapters after the Gen body snapshot without resurrecting failures the body already handled.

- Report MEFF310 when an async Gen body returns a scope step as its result value instead of awaiting it.

- Recognize immediately consumed scope steps through checked and unchecked expressions, null-forgiving expressions, identity casts and Task upcasts without accepting unrelated user awaiters.

- Exclude once-only loop setup from resource-retention warnings while retaining warnings for regions that can repeat.

- Report scope-owned resources retained by deconstructing foreach and await foreach loops.

- Keep MEFF310 for user ConfigureAwait extensions that do not prove a scope step was awaited.

- Exclude unevaluated nameof operands from clock-read, nested scope-use and catch scope-use diagnostics.

- Avoid MEFF306 for user extensions, uninvoked wait methods and Result references inside nameof while retaining actual wait diagnostics.

- Restrict scope-operation warnings to invoked library methods, excluding unrelated extensions and uninvoked method groups.

- Avoid MEFF308 and MEFF309 for unrelated Catch extension methods that bind successfully.

- Keep suppressed defects and omitted counts once when combining release, child and pending scope failures.

- Preserve the original RunAsync defect when Exception.Data rejects metadata, reporting the attachment failure and its context through diagnostics.

- Keep pending scope steps unobserved after a premature synchronous read so Gen waits and releases acquired resources.

- Widen nullable RestOf cases from their declared type to avoid ambiguous numeric conversions.

- Preserve late scope errors through Task adapters and unreadable union values when draining unawaited steps.

- Avoid MEFF310 for immediately awaited or returned standard ValueTask AsTask and Preserve adapters.

- Emit object type patterns for dynamic error cases in Error and RestOf generators.

- Emit dynamic environment services as object and report MEFF103 for equivalent dynamic/object service declarations.

- Keep AllErrors interrupted when a child is interrupted without caller cancellation, retaining completed errors as context.

- Accept environment classes whose sole primary constructor parameter has the class's own type.

- Preserve Interrupted when a Gen block rethrows an earlier interruption after catching later interrupted steps.

## [0.1.0-preview.2] - 2026-11-02

### Fixed

- Report MEFF301 when an unfiltered catch mixes rethrow with replacement throws or rethrows a nested catch exception.

- Recognize immediately awaited conditional and switch branches in MEFF310 without allowing stored or discarded scope steps.

- Report MEFF006 on file-local generator targets and references, including union conversion paths, RestOf sources/cases and exposed services, before emitting inaccessible types.

- Keep suppressed defects on the returned Die without also reporting them when a recovery handler throws or returns an uninitialized effect.

- Emit valid type patterns for tuple error cases in Error and RestOf generators while preserving tuple element names in signatures.

- Compare union cases by canonical type identity while retaining tuple element names in generated signatures and diagnostics.

- Preserve body failures and cancellation callback defects when timeout timer disposal throws.

- Keep independent group errors when caller cancellation arrives during cleanup, and classify canceled validation errors by observation rather than completion.

- Report duplicate environment services that differ only in reference nullability or tuple element names.

- Preserve the body error cancellation classification when its unreadable union value becomes a new defect before release.

- Preserve an earlier scoped defect when it is rethrown after handling another defect.

- Preserve observed failure order and cancellation classification through nested groups, error transformations, Gen, and resource cleanup.

- Keep grouped failure context in child completion order, including the selected child, before applying the retention limit.

- Publish timeout failure order before its cancellation reason, even when timer disposal does not wait for the callback.

- Return a faulted ValueTask from RunAsync when execution throws synchronously, including invalid Never conversions without a cancelable token.

- Return a Die from RunExitAsync when an execution exception escapes, including a pending invalid Never conversion without a cancelable token.

### Changed

- Preserve the earliest independent member error origin in AllErrors aggregates, or the earliest consequential origin when no independent error exists.

- Preserve inline constant results when widening an effect's environment type.

- Build retained cancellation-callback context without a temporary prefix array.

- Reuse internal release-loop async state when cleanup suspends.

- Skip creating discarded defect cases after grouped failure context reaches its limit.

- Reuse private async state for scheduled Retry and Repeat waits.

- Initialize fresh outcomes with empty failure context directly.

- Avoid capturing dispatch information for exceptions successfully handled by Try and TryAsync.

- Map asynchronous try outcomes in the body continuation without a second async wrapper.

- Pass Effect composition state directly to continuations without separate callback closures.
- Convert asynchronous Results directly into outcomes without an intermediate outcome continuation.

- Remove Effect LINQ selector adapters and widen second-step outcomes in the projection continuation.

- Project Resource LINQ results directly without constructing mapped resources on each run.

- Bound independent timeout-defect context before building its immutable array.

- Copy only retained canceled-body context after a timeout, counting discarded entries as omitted.

- Resolve timeout clocks without boxing value-type environments.

- Build retained retry failure context directly without copying discarded entries or intermediate arrays.
- Reuse private recurrence await state across repeated waits while retaining execution-context boundaries.

- Avoid a per-acquisition bound delegate when registering resource releases.
- Skip discarded retry context arrays when the policy failure already fills the context limit.

- Bound collected group failure context while counting discarded entries as omitted.

- Collect group failure context by completion rank instead of sorting intermediate sequences.
- Assert the completion-rank bound before collecting group failure context in Debug builds.

- Aggregate child and release defects directly from their owned builders without intermediate array copies.

- Traverse child and release defects without allocating LINQ enumeration state or a captured predicate.

- Unwrap single callback failures without building flattened aggregate exceptions.

- Copy only the retained prefix when combined failure context exceeds its limit.
- Register fiber cleanup without allocating a separate closure to retain the fiber.

- Reuse saturated failure context in continuations while counting later failures as omitted.
- Pass resource acquisition continuation state explicitly instead of capturing it on every run.
- Transfer the registered release list to cleanup without copying it to an array.

- Skip unobserved-step tracking lists when a Gen block has no steps left to settle.
- Create step-completion notifications only when a Gen block must wait for an unobserved step.

- Use one async wrapper for Sleep so suspended waits avoid an intermediate result state.
- Avoid parallel-worker capture state in sequential groups and skip internal execution state for empty groups.

- Fill group result arrays directly and avoid per-run wrapper closures in group and race execution.

- Snapshot input effect arrays and lists without an intermediate validation iterator.

- Skip failure aggregation buffers when a group has no stored secondary failures, retaining omitted counts.
- Allocate release action lists only when needed and read remaining release defects without copying their array.

- Store constant effects inline so `Succeed`, `Fail`, `FromResult`, and their `Never` conversions avoid construction allocations; effect structs are larger, including deferred effects.
- Independently constructed constant effects can now compare equal through inherited `Equals`, because their stored results replace distinct captured delegate targets.
- Create release failure buffers only when a release has failures to preserve.

- Filter timeout failure context directly without allocating a LINQ sequence.

- Bound child-defect context while preserving body-error projection and duplicate suppression.

- Reuse single-child defect context when the body adds no separate failure.

- Bound context collected from failed releases and count entries beyond the retained prefix.

- Bound carried context from successful releases before merging it with the final failure.

- Copy only retained error context when a mapper replaces an error with a defect.

- Reuse bounded context merging when settling group failures and race winners.

- Reuse private parallel-child await state between waits without changing execution boundaries.

- Synchronize release ownership on its private owner without a separate lock allocation.

- Reuse bounded context merging when a Retry or Repeat policy fails.

- Reuse existing context when a successful body has a single failed release.

- Transfer completed context buffers to immutable results when their capacity is fully used.

- Store cancellation bookkeeping on the owned token source to avoid a separate wrapper allocation.

- Reuse the immutable `Interrupted` case when returning interrupted exits, avoiding per-result allocation after initialization.

## [0.1.0-preview.1] - 2026-10-27

### Added

- `Effect<T, E, R>`: a lazy, re-runnable description of work with its value, expected error and environment in the type. `Never` for effects that cannot fail or cannot succeed.
- Factories on `Effect<R>`: `Succeed`, `Fail`, `FromResult`, `Sync`, `Async`, `FromResultAsync`, `Try`, `TryAsync`, `Access`, `AccessAsync`, `Env`.
- `RunAsync` returning `Result<T, E>`, and `RunExitAsync` returning `Exit<T, E>` (`Ok`, `Err`, `Die`, `Interrupted`). `EmptyEnvironment` and `DefaultEnvironment`.
- Operators: `Map`, `MapErr`, `AndThen`, `AndThrough`, `OrElse`, `Tap`, `TapErr`, `Match`, `UnwrapOr`, `UnwrapOrElse`, `Attempt`, `Widen`, `CatchCase`, `Catch`, `Provide`, `WidenEnv`, `OnThreadPool`, and LINQ query syntax.
- Gen blocks: `Scope<E, R>.Gen` and `Effect.Gen`, with `Run`, `Get`, `Fail`, `Defer`, `Acquire` and `Fork` on the scope.
- `Resource<T, E, R>` with `Resource.Make`, `Use`, `Map`, `AndThen` and LINQ query syntax.
- `Effect.Sleep`, `Timeout`, `TimeoutResult` and `TimeoutError`.
- `Concurrency`, `Effect.All` (2 to 8 effects or a sequence), `AllSettled`, `AllErrors`, `Race`, `RaceFirst`, and `Fiber<T, E, R>` with `Join`.
- `Schedule` and `Schedule<TNeed>` (`Spaced`, `Exponential`, `Immediately`, `UpTo`, `During`, `WithMaxDelay`, `Jittered`), `Retry` and `Repeat` with a schedule or with an effect as the policy.
- Failure model: suppressed failures in `Die.Suppressed`, and `EffectDiagnostics.UnobservedFailureHandler` for failures no outcome can carry.
- Source generators: `[Error]` (`IUnionCase`, including the cases of cases), `[Environment]` and `[Expose<T>]` (`IHas<T>`), `[RestOf<E>]` (`IUnionRest`, for `Catch`).
- Source generator diagnostics: MEFF001 to MEFF005 for `[Error]` and `[RestOf<E>]` targets, MEFF101 to MEFF104 for `[Environment]`, and MEFF201 to MEFF203 for `[RestOf<E>]` unions.
- Analyzers: MEFF301 to MEFF306 and MEFF310 for misuse of Gen blocks, MEFF307 for the system clock inside effects, and MEFF308 and MEFF309 for `Catch` calls that do not compile.
- Zero allocation: running a prebuilt effect that completes synchronously allocates nothing. This covers the factories, operators and LINQ query syntax, `Retry` and `Repeat` with built-in schedules that do not wait, and value-type environments, errors and clocks. Gen blocks still allocate their scope.

[Unreleased]: https://github.com/SingUp009/Moderato.Effects/compare/v0.1.0-preview.6...HEAD
[0.1.0-preview.6]: https://github.com/SingUp009/Moderato.Effects/compare/v0.1.0-preview.5...v0.1.0-preview.6
[0.1.0-preview.5]: https://github.com/SingUp009/Moderato.Effects/compare/v0.1.0-preview.4...v0.1.0-preview.5
[0.1.0-preview.4]: https://github.com/SingUp009/Moderato.Effects/compare/v0.1.0-preview.3...v0.1.0-preview.4
[0.1.0-preview.3]: https://github.com/SingUp009/Moderato.Effects/compare/v0.1.0-preview.2...v0.1.0-preview.3
[0.1.0-preview.2]: https://github.com/SingUp009/Moderato.Effects/compare/v0.1.0-preview.1...v0.1.0-preview.2
[0.1.0-preview.1]: https://github.com/SingUp009/Moderato.Effects/releases/tag/v0.1.0-preview.1
