# Moderato.Effects

An effect system for C# on .NET 11 or later with C# 15 unions, modeled on [Effect](https://effect.website).

`Effect<T, E, R>` is a lazy description of work. It succeeds with `T`, fails with an expected error `E`, and needs an environment `R`. The described work runs only when you execute the effect, and every run starts over, so time limits, retries and concurrency compose like ordinary operators.

See [Getting started](Getting-started) for a first program, or [Installation](Installation) for packages and source builds.

## Start here

- [Getting started](Getting-started): install and run a complete first program.
- [From Results to Effects](From-Results-to-Effects): keep typed errors and describe rerunnable work.
- [Installation](Installation): choose packages or build from source.
- [Adopting in an application](Adopting-in-an-application): choose execution boundaries and service lifetimes.

## Write effects

- [Gen blocks](Gen-blocks): use async code, branches and loops.
- [Operators](Operators): transform, chain and recover.
- [Error unions](Error-unions): name expected failures and handle individual cases.
- [Environments](Environments): supply services through typed dependencies.

## Control execution

- [Resources](Resources): acquire and release safely.
- [Time](Time): sleep and apply timeouts.
- [Concurrency](Concurrency): run groups and fibers.
- [Retry and repeat](Retry-and-repeat): apply explicit schedules.
- [Failures](Failures): inspect defects, interruption and failure context.

## Integrate and diagnose

- [Hosting](Hosting): run background services.
- [HTTP](Http): use ASP.NET Core routes, typed responses and OpenAPI.
- [Entity Framework Core](Entity-Framework-Core): SQLite CRUD and owned transactions.
- [Effect Tracker](Effect-Tracker): inspect active lifetimes.
- [Analyzers](Analyzers): catch incorrect use at build time.
- [Caveats](Caveats): known limits and validation coverage.

## At a glance

| Type | Meaning |
|---|---|
| `Effect<T, E, R>` | Work that needs `R` and ends with `T` or `E` |
| `Scope<E, R>` | The handle a Gen block runs effects with |
| `Resource<T, E, R>` | A value that is released after use |
| `Exit<T, E>` | How a run ended: `Ok`, `Err`, `Die` or `Interrupted` |
| `Never` | A type with no values: an `Effect<T, Never, R>` cannot return an expected error; it can still die or be interrupted |
| `IHas<T>` | An environment that provides `T` |

`Result<T, E>`, `Ok<T>`, `Err<E>` and `Unit` come from [Moderato.Results](https://github.com/SingUp009/Moderato.Results).
