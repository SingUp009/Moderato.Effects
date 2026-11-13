---
title: Analyzers and diagnostics
description: Understand the diagnostics emitted by the Moderato.Effects source generators and analyzers, and configure their severity in .editorconfig.
---

# Analyzers and diagnostics
The package ships its source generators and analyzers; there is nothing extra to install. Change a severity in `.editorconfig`:

```ini
dotnet_diagnostic.MEFF307.severity = warning
```

## Source generators

| ID | Severity | Title | Fix |
|---|---|---|---|
| MEFF001 | Error | Needs a partial union | Declare the `[Error]` / `[RestOf<E>]` target as a `partial union` |
| MEFF002 | Error | Generic types are not supported | Use a non-generic union |
| MEFF003 | Warning | Case reachable in more than one way | Only the direct case is generated; remove one of the paths if you need the nested one |
| MEFF004 | Error | Nested types are not supported | Move the union or environment to namespace level |
| MEFF005 | Warning | Union case admits null | Wrap the nullable value in a record (see [Error unions](/error-unions/#nullable-cases)) |
| MEFF006 | Error | File-local types are not supported | Move the target or referenced type out of `file` scope |
| MEFF007 | Error | Union case graph is too complex | Simplify the union graph or implement its interfaces manually; the failing target emits no partial code |
| MEFF008 | Error | Union cases may overlap | Wrap overlapping payloads in distinct record types; no code is generated for the failing target |
| MEFF009 | Error | Type cannot be referenced by its global name | Make the type unambiguously available through its global name, wrap it in a globally accessible type, or implement the interfaces manually |
| MEFF101 | Error | Needs a partial type | Make the `[Environment]` type `partial` |
| MEFF102 | Error | Needs a primary constructor | Give the `[Environment]` type a primary constructor |
| MEFF103 | Error | Service provided twice | Wrap one parameter in its own type, or use `[Expose<T>]` |
| MEFF104 | Error | Parameter cannot be provided as that type | Use a `T` in `[Expose<T>]` that the parameter converts to |
| MEFF201 | Error | Not a union | The `E` in `[RestOf<E>]` must be a union |
| MEFF202 | Error | Must leave out exactly one case | The rest union holds every case of `E` but one |
| MEFF203 | Error | Rest holds cases the union does not | Remove the cases `E` does not have |

When MEFF103 or MEFF104 is reported, nothing is generated for that environment.

MEFF103 also covers a parameter whose service the type already provides through a hand-written explicit `IHas<T>.Service` implementation. Remove that implementation, or expose the parameter as another type.

## Gen blocks

| ID | Severity | Reports | Fix |
|---|---|---|---|
| MEFF301 | Warning | A `catch` around scope steps that swallows `ScopeFailure` | Add `when (ex is not ScopeFailure)`, or rethrow with `throw;` |
| MEFF302 | Warning | `scope.Run` / `Get` / `Acquire` / `Fork` in `finally` or in a `catch` that handles `ScopeFailure` | Use `scope.Defer` or a resource for cleanup |
| MEFF303 | Warning | The scope used in a nested function: a lambda, a local function or a nested Gen block | Use the scope only in its own block; run the nested block with `scope.Run` |
| MEFF304 | Warning | The scope stored in a field, property, array element or variable of the enclosing member, or returned | Keep the scope inside its block |
| MEFF305 | Warning | `scope.Acquire` / `Defer` / `Fork` in a loop, which holds every resource until the block ends | Use `resource.Use` or a nested Gen block per iteration |
| MEFF306 | Warning | `.Result`, `.Wait()` or `GetAwaiter().GetResult()`, which block the thread | `await` instead |
| MEFF310 | Warning | `scope.Run` / `Acquire` / `Fork` not awaited at once, including storing it to await later | `await` it, or `return` it |

A method or local function that takes a `Scope<E, R>` is analyzed like a Gen block lambda, whether it is passed to `Gen` as a method group or called as a helper. Its own `catch` clauses, cleanup code, loops and waits are checked; a non-async helper may return its step for the caller to await.

Returning a step directly from a non-async Gen body lets Gen await it. An async body must await the step: returning it as the body's result value does not consume it. Awaiting a step whose value itself is a `Task` or `ValueTask` remains valid.

MEFF302, MEFF305 and MEFF310 inspect calls bound to the library's scope methods. A method-group reference or a call bound to a user extension does not trigger them; the analyzer does not follow a call into a helper, but it analyzes each helper that takes the scope on its own. The scope capture and escape checks still apply.

MEFF310 follows the standard `ValueTask<T>.AsTask`, `Preserve` and `ConfigureAwait` adapters, and `Task<T>` / `Task` `ConfigureAwait`. A call bound to a custom extension with one of these names does not establish that the step was awaited, even if the extension's result is awaited.

Parentheses, `checked` / `unchecked`, null-forgiving `!`, identity casts and standard upcasts from `Task<T>` to `Task` also preserve immediate consumption. Other conversions or casts do not establish it: they can select a user awaiter that ignores the original step.

MEFF306 checks direct `Result` access and calls to the supported wait methods. Taking a method-group reference, mentioning a member in `nameof`, or calling a user extension does not trigger it. The analyzer does not follow later calls through delegates or helper implementations.

A `nameof` operand does not count as scope use for MEFF301 or MEFF303: it neither runs a scope operation nor captures the scope.

MEFF301 and MEFF302 recognize `catch (ScopeFailure)` and filters that accept it based on the caught exception's type, such as `when (ex is not OperationCanceledException)`. Filters that call application code may require manual review.

MEFF304 treats a local or parameter declared outside the Gen block as storage that outlives it, because the block captures it. Assigning the scope to a local declared inside the block is not reported.

MEFF305 includes tuple-deconstructing `foreach` and `await foreach` loops. Use a nested Gen block per iteration when its resources should be released before the next iteration.

MEFF305 checks registrations that can repeat. A `for` statement's initialization and a `foreach` or `await foreach` collection expression run once, so they do not trigger it on their own. An enclosing loop can still repeat that setup; conditions, incrementors and loop bodies remain checked.

Not reported, because they are safe:

- `scope.Run` in a `catch` for a specific type, such as `catch (FormatException)`: that `catch` never catches `ScopeFailure`.
- Passing the scope to a helper method that uses it in the same flow. The helper's own body is checked by the same rules.

## Time

| ID | Severity | Reports | Fix |
|---|---|---|---|
| MEFF307 | Info | The system clock inside a Gen block, an `Effect<R>` factory's lambda, `Succeed`'s argument or a callback passed to the library | Use `scope.Env.Resolve<TimeProvider>()` or `Effect.Sleep<R>` |

A callback passed to the library is a lambda given to any method of the `Moderato.Effects` packages: operators such as `Map`, `AndThen` and `Tap`, query syntax, `Resource.Make` and `Use`, `Retry` and `Repeat` policies and filters, and the integrations' callbacks. A method or local function that takes a `Scope<E, R>` counts as a Gen block. A value computed outside the lambda and captured by it is not reported, and neither is a lambda passed to other code, such as `Enumerable.Select`, unless it runs inside one of the callbacks above.

The list of clock APIs is in [Time](/time/#the-system-clock-inside-effects). Overloads that take a `TimeProvider` are not reported. A clock property mentioned only in `nameof` is not a clock read.

## Catch

`Catch<TCase, F>` needs `F` to be the matching `[RestOf<E>]` union. The compiler reports CS0315 otherwise, and these analyzers explain why:

These checks apply to the library's `Catch` method. A call bound to a user extension with the same name is not checked.

| ID | Severity | Reports |
|---|---|---|
| MEFF308 | Error | `F` is not declared `[RestOf<E>]` |
| MEFF309 | Error | `F` leaves out a different case than `TCase` |

## When to suppress

Suppress a Gen block warning only where misuse is intended, for example in a test that shows the runtime stays safe:

```csharp
#pragma warning disable MEFF310
_ = scope.Run(effect);
#pragma warning restore MEFF310
```
