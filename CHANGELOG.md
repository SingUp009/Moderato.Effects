# Changelog

All notable changes to this project are documented here.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed

- Recognize immediately awaited conditional and switch branches in MEFF310 without allowing stored or discarded scope steps.

- Report MEFF006 on file-local Error, Environment and RestOf targets instead of emitting unrelated partial types.

- Keep suppressed defects on the returned Die without also reporting them when a recovery handler throws or returns an uninitialized effect.

- Emit valid type patterns for tuple error cases in Error and RestOf generators while preserving tuple element names in signatures.

- Preserve body failures and cancellation callback defects when timeout timer disposal throws.

- Keep independent group errors when caller cancellation arrives during cleanup, and classify canceled validation errors by observation rather than completion.

- Report duplicate environment services that differ only in reference nullability or tuple element names.

- Preserve observed failure order and cancellation classification through nested groups, error transformations, Gen, and resource cleanup.

- Keep grouped failure context in child completion order, including the selected child, before applying the retention limit.

- Publish timeout failure order before its cancellation reason, even when timer disposal does not wait for the callback.

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

### Tests

- Pin scoped composition laws for binding, mapping, queries, recovery, environment provision, validation, resource release, retry and repeat.

- Verify recurrence execution-context isolation and pending exception boundaries without caller isolation.
- Verify asynchronous Result failure classification without caller isolation.
- Cover successful LINQ projection context through a later defect, including retained limits and pending steps.

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

[Unreleased]: https://github.com/SingUp009/Moderato.Effects/compare/v0.1.0-preview.1...HEAD
[0.1.0-preview.1]: https://github.com/SingUp009/Moderato.Effects/releases/tag/v0.1.0-preview.1
