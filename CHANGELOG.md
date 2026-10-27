# Changelog

All notable changes to this project are documented here.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed

- Skip failure aggregation buffers when a group has no stored secondary failures, retaining omitted counts.
- Allocate release action lists only when needed and read remaining release defects without copying their array.

- Store constant effects inline so `Succeed`, `Fail`, `FromResult`, and their `Never` conversions avoid construction allocations; effect structs are larger, including deferred effects.
- Independently constructed constant effects can now compare equal through inherited `Equals`, because their stored results replace distinct captured delegate targets.
- Create release failure buffers only when a release has failures to preserve.

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

[Unreleased]: https://github.com/SingUp009/Moderato.Effects/compare/v0.1.0-preview.1...HEAD
[0.1.0-preview.1]: https://github.com/SingUp009/Moderato.Effects/releases/tag/v0.1.0-preview.1
