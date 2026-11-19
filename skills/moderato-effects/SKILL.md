---
name: moderato-effects
description: Write, extend, or debug C# applications that use Moderato.Effects. Use when the project references Moderato.Effects or its Hosting, HTTP, or EF Core integration, or when the user asks to use this library.
---

# Moderato.Effects

1. Check `global.json`, the target framework, and the resolved package versions. Moderato.Effects requires .NET 11 or later and C# 15 unions. Check `Directory.Packages.props` in projects with central package management. Keep the application's existing SDK, dependencies, and boundary conventions unless the task requires a change.
2. Start with the [documentation index](https://moderato-effects.009singup.workers.dev/llms.txt) or the [runnable first effect](https://github.com/SingUp009/Moderato.Effects/blob/main/tutorial/Moderato.Effects.Tutorial/Chapters/02_FirstEffect.cs). Confirm signatures against the installed package or its release tag when the current docs describe a newer version. Use public APIs and inspect the surrounding application before adding a new environment, error union, or execution boundary.
3. Build and run the relevant tests. Check expected errors, defects, and interruption separately. Use the [MEFF diagnostic reference](https://moderato-effects.009singup.workers.dev/analyzers.md) to fix generator or scope warnings at their source.

## Rules that affect correctness

- `Effect<T, E, R>` describes work executed at the application boundary. `T` is success, `E` is expected failure, and `R` provides dependencies. Pass a delegate to defer I/O until each run. Await each `scope.Run`, `scope.Acquire`, or `scope.Fork` when called inside `Scope<E, R>.Gen`.
- Keep expected failures in `E`. `RunAsync` returns `Result<T, E>` and throws defects and cancellation. Use `RunExitAsync` when the boundary needs `Die` and `Interrupted` as values. Preserve the caller's cancellation through async operations.
- Acquire owned resources within a scope and let their release finish before returning. Keep fibers in their owner scope. Use independent or thread-safe services for parallel effects.
- A timeout requests cooperative cancellation and waits for cleanup. Retry only operations that are safe to run again.

## Read for the current task

- **Typed errors and composition.** [Error unions](https://moderato-effects.009singup.workers.dev/error-unions.md), [Gen blocks](https://moderato-effects.009singup.workers.dev/gen-blocks.md), and [failures](https://moderato-effects.009singup.workers.dev/failures.md) explain `[Error]`, supported widening, and short-circuiting. Specify the error union explicitly when calling `Effect<R>.Fail<E>`.
- **Dependencies.** [Environments](https://moderato-effects.009singup.workers.dev/environments.md) covers `IHas<T>`, `scope.Env.Resolve<T>()`, and caller-supplied dependencies.
- **Lifetimes.** Read [resources](https://moderato-effects.009singup.workers.dev/resources.md), [concurrency](https://moderato-effects.009singup.workers.dev/concurrency.md), [time](https://moderato-effects.009singup.workers.dev/time.md), and [retry and repeat](https://moderato-effects.009singup.workers.dev/retry-and-repeat.md) before changing cleanup, fiber ownership, or scheduling.
- **Integrations.** Follow [Hosting](https://moderato-effects.009singup.workers.dev/hosting.md), [HTTP](https://moderato-effects.009singup.workers.dev/http.md), or [EF Core](https://moderato-effects.009singup.workers.dev/entity-framework-core.md) for the matching application boundary. For parallel EF Core work, give each operation its own `DbContext`.
