---
title: Getting started
description: Write and run your first Moderato.Effects program in C#. Handle typed results and compose asynchronous work with timeouts and retries.
---

# Getting started

## Requirements

- .NET 11 SDK or later, targeting `net11.0` or later. For a source build, use the SDK pinned in `global.json`.
- C# 15 union support, provided by the .NET 11 SDK.

The package includes its source generators and analyzers. Core supports Native AOT; validate your application and its dependencies separately.

```bash
dotnet new console --framework net11.0
dotnet add package Moderato.Effects
```

Pin the SDK and package version in your application. See [Installation](/installation/) for the other integration packages and source builds.

## First step

Replace `Program.cs` with this code, then run `dotnet run`:

```csharp
using Moderato.Effects;
using Moderato.Results;

foreach (var id in new[] { 1, 0 })
{
    Result<string, AppError> result = await Greet(id).RunAsync(CancellationToken.None);
    Console.WriteLine(result switch
    {
        Ok<string>(var text)       => text,
        Err<AppError>(NotFound n)  => $"no user {n.Id}",
        Err<AppError>(Forbidden f) => $"forbidden: {f.Reason}",
    });
}

static Effect<string, AppError, EmptyEnvironment> Greet(int id) =>
    Scope<AppError, EmptyEnvironment>.Gen(async scope =>
    {
        if (id <= 0) throw scope.Fail(new NotFound(id));
        await Task.Yield();
        return $"hello {id}";
    });

#region Error
public record NotFound(int Id);
public record Forbidden(string Reason);

[Error]
public partial union AppError(NotFound, Forbidden);
#endregion
```

The output is `hello 1`, followed by `no user 0`. Both results are handled explicitly.

- `Effect<string, AppError, EmptyEnvironment>` succeeds with a `string`, fails with an `AppError` and needs no services.
- `[Error]` lets a case such as `NotFound` stand in for the whole `AppError` union (see [Error unions](/error-unions/)).
- `Scope<E, R>.Gen` writes the effect as `async` code (see [Gen blocks](/gen-blocks/)).

Calling `Greet(1)` does nothing yet. It only describes the work.

## Running it

```csharp
Result<string, AppError> result = await Greet(1).RunAsync();

var message = result switch
{
    Ok<string>(var text)        => text,
    Err<AppError>(NotFound n)   => $"no user {n.Id}",
    Err<AppError>(Forbidden f)  => $"forbidden: {f.Reason}",
};
```

| Method | Returns | Bugs and cancellation |
|---|---|---|
| `RunAsync(env, ct)` | `ValueTask<Result<T, E>>` | thrown as exceptions |
| `RunExitAsync(env, ct)` | `ValueTask<Exit<T, E>>` | returned as `Die` and `Interrupted` |

An effect that needs `EmptyEnvironment` or `DefaultEnvironment` can be run without passing an environment. Any other effect takes its environment as the first argument: `effect.RunAsync(env, cancellationToken)`.

Expected errors come back as `Err`; they do not need `try`/`catch`. `RunAsync` can still throw defects and `OperationCanceledException`. Handle those at your application's boundary, such as middleware or a worker loop, or use `RunExitAsync` to inspect them as values. See [Failures](/failures/).

## Effects compose

Because an effect is a description of work, you can add a time limit, retries or concurrency without changing the underlying operation.

The following example assumes a `GetUserProfile` method, an `AppEnv` implementing `IHas<TimeProvider>`, and an error union containing `TimeoutError`:

```csharp
var result = await GetUserProfile<AppEnv>(id)
    .Timeout(TimeSpan.FromSeconds(5))
    .Retry(Schedule.Exponential(TimeSpan.FromMilliseconds(100)).UpTo(3), when: e => e is TimeoutError)
    .RunAsync(env, cancellationToken);
```

Each retry runs the whole effect again from the start.

## Creating effects

Factories live on the static class `Effect<R>`, so the environment type is given once:

| From | API | Type |
|---|---|---|
| a value | `Effect<R>.Succeed(value)` | `Effect<T, Never, R>` |
| an error | `Effect<R>.Fail<AppError>(error)` | `Effect<Never, AppError, R>` |
| a `Result<T, E>` | `Effect<R>.FromResult(result)` | `Effect<T, E, R>` |
| synchronous code | `Effect<R>.Sync(() => ...)` | `Effect<T, Never, R>` |
| asynchronous code | `Effect<R>.Async(ct => ...)` | `Effect<T, Never, R>` |
| code returning `ValueTask<Result<T, E>>` | `Effect<R>.FromResultAsync(ct => ...)` | `Effect<T, E, R>` |
| code that throws expected exceptions | `Effect<R>.Try(...)` / `TryAsync(...)` | `Effect<T, E, R>` |
| the environment | `Effect<R>.Access(env => ...)` / `AccessAsync((env, ct) => ...)` / `Effect<R>.Env` | `Effect<T, Never, R>` |
| waiting | `Effect.Sleep<R>(duration)` | `Effect<Unit, Never, R>` |

- An API that needs a constraint on `R` is written `Effect.X<R>(...)`; all others are `Effect<R>.X(...)`.
- `Never` converts implicitly: an `Effect<T, Never, R>` can be used as an `Effect<T, E, R>` for any `E`, and an `Effect<Never, E, R>` as an `Effect<T, E, R>` for any `T`.
- Always write `Fail`'s type argument: `Effect<R>.Fail<AppError>(new NotFound(id))`. Without it the effect is `Effect<Never, NotFound, R>`, which needs two conversions to become `Effect<T, AppError, R>`, and C# applies only one.
- `Succeed` evaluates its argument once, when it is called. Use `Sync` for code that must run on each execution, such as reading the time.
- Exceptions thrown inside `Sync`, `Async` and `Access` are bugs (`Die`). To turn an exception into an expected error, use `Try`:

```csharp
Effect<R>.Try(() => JsonSerializer.Deserialize<Dto>(json)!, (JsonException ex) => new ParseError(ex.Message));
```

Only `JsonException` becomes the error; anything else is still a defect.

## Next

- [Gen blocks](/gen-blocks/) are the main way to write effects.
- [Environments](/environments/) show how an effect lists the services it needs.
