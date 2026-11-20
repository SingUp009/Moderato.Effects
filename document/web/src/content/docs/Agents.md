---
title: Agent Guide
slug: agent-guide
description: Choose Moderato.Effects APIs and implement typed errors, effect scopes, cancellation, and resource cleanup in C#.
---

Use this guide when writing or reviewing application code with Moderato.Effects.

In an existing application, check `global.json`, the target framework, and the resolved NuGet versions before choosing APIs. Check `Directory.Packages.props` if the application manages versions centrally. Use the installed package, release changelog, and [API reference](https://github.com/SingUp009/Moderato.Effects/wiki) to check signatures.

## Write and run a first effect

```sh
dotnet new console --framework net11.0
dotnet add package Moderato.Effects --prerelease
```

Replace `Program.cs` with the following and run `dotnet run`. It prints `hello 1` and `no user 0`.

```csharp
using Moderato.Effects;
using Moderato.Results;

foreach (var id in new[] { 1, 0 })
{
    Result<string, LookupError> result = await Greet(id).RunAsync();
    Console.WriteLine(result switch
    {
        Ok<string>(var text) => text,
        Err<LookupError>(MissingUser missing) => $"no user {missing.Id}",
        Err<LookupError>(InvalidId invalid) => $"invalid id {invalid.Id}",
        _ => throw new InvalidOperationException("Invalid lookup result."),
    });
}

static Effect<string, LookupError, EmptyEnvironment> Greet(int id) =>
    Scope<LookupError, EmptyEnvironment>.Gen(scope =>
    {
        if (id < 0) throw scope.Fail(new InvalidId(id));
        if (id == 0) throw scope.Fail(new MissingUser(id));
        return ValueTask.FromResult($"hello {id}");
    });

public record MissingUser(int Id);
public record InvalidId(int Id);

[Error]
public partial union LookupError(MissingUser, InvalidId);
```

`Effect<T, E, R>` describes deferred work. `T` is the success value, `E` is an expected error, and `R` supplies the environment dependencies. Calling `Greet` builds the description. Every execution runs the body again. `Scope<E, R>.Gen` gives the body a scope for composing effects and managing their lifetime. An expected failure from `scope.Run` ends the block and reaches the caller as `Err<E>`.

Compose child effects with `scope.Run` or pass them directly to `Effect.All`. Call `RunAsync` or `RunExitAsync` at the application boundary. A resource release receives its own `ExitCase` from the library. Match cases with patterns such as `exit is Succeeded` or `exit is Failed(var error)`.

## Manage a resource with asynchronous cleanup

**For parallel work that stops on the first failure, use `Effect.All(..., Concurrency.Parallel)`.** It cancels the remaining children and waits for them and their releases. If an `Err` cancels a sibling and that sibling's release throws, the group returns `Die` with the original error in `Die.Suppressed`. When one child owns a resource, pass `resource.Use(...)` to `All` as that child so release receives the child's exit.

In `Resource.Make`, pass an acquisition effect that calls the factory for the value you will release. For a synchronous factory, use `Effect<R>.Sync(open)`. Once the factory returns a value, release is registered even if cancellation arrived during the call. Put work on the value in `Use`. When acquisition has error type `Never`, set `E` in `Resource.Make<TResource, E, R>` to the error type of the effect returned by the `Use` callback. Use `Effect<R>.FromResultAsync<T, E>` to forward the run token and preserve a callback's typed result. Wrap asynchronous cleanup in `Effect<R>.Async` and return `Moderato.Results.Unit.Value` after awaiting it. The release receives the read's exit case and an uncanceled token, including when a parallel sibling cancels the read.

```csharp
using Moderato.Effects;
using Moderato.Results;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

public sealed record StoreError(string Message);

public static class StoreEffects
{
    public static Effect<(string, bool), StoreError, EmptyEnvironment> ReadSettingAndStatus(
        Func<Stream> open,
        Func<Stream, CancellationToken, ValueTask<Result<string, StoreError>>> read,
        Func<Stream, ExitCase, CancellationToken, ValueTask> close,
        Effect<bool, StoreError, EmptyEnvironment> status)
    {
        // Open during acquisition so release is registered even if cancellation prevents Use from starting.
        var handle = Resource.Make<Stream, StoreError, EmptyEnvironment>(
            Effect<EmptyEnvironment>.Sync(open),
            (stream, exit) => Effect<EmptyEnvironment>.Async(async ct =>
            {
                await close(stream, exit, ct);
                return Unit.Value;
            }));

        var readSetting = handle.Use(stream =>
            Effect<EmptyEnvironment>.FromResultAsync<string, StoreError>(ct => read(stream, ct)));

        return Effect.All(readSetting, status, Concurrency.Parallel);
    }
}
```

When several resources need the same body's `ExitCase`, acquire them in one `Gen` block. Nested calls to `Use` create separate scopes, so an inner release defect can change the exit case seen by the outer release.

This excerpt assumes a resource named `first`, an `openSecond` factory that takes the first stream, and a `work` effect factory that takes both streams. Both resources and the effect use the same error type.

```csharp
var operation = Scope<StoreError, EmptyEnvironment>.Gen(async scope =>
{
    var firstStream = await scope.Acquire(first);
    var secondStream = await scope.Acquire(openSecond(firstStream));
    return await scope.Run(work(firstStream, secondStream));
});
```

If the body succeeds and the second release throws, the first release still receives `Succeeded` and the effect ends as `Die`. Leave borrowed values with their caller. See the [release rules](/resources/#release-rules) for cleanup order and cancellation.

## Verify the implementation

`RunAsync` returns a `ValueTask<Result<T, E>>`. Awaiting it yields the result or throws for a defect or interruption. `RunExitAsync` returns a `ValueTask<Exit<T, E>>`. Awaiting it yields `Ok<T>`, `Err<E>`, `Die`, or `Interrupted`. Pass the caller's cancellation token and supply the environment when required. The token observed inside an effect may be linked to the caller's token. See [Failures](/failures/) and [Environments](/environments/).

Run the application's `dotnet build` and `dotnet test`. Exercise success, each expected error, and any defect or cancellation the boundary must handle. Use [MEFF diagnostics](/analyzers/) to fix the source of a generator error or scope warning. For example, MEFF310 means a scope step must be awaited immediately or returned directly from a non-async body. The [runnable tutorial](/tutorial/00-what-is-an-effect/) covers the same APIs in a small application.

| When writing code that… | Check |
| --- | --- |
| Starts async work | Defer work by passing a factory and pass its token to the awaited operation. Use [Gen blocks](/gen-blocks/) for composition and keep the scope inside its body. |
| Combines different error types | Model expected failures with explicit cases and check the supported widening conversions. Give `Fail` its intended error type. See [Error unions](/error-unions/) and [Caveats](/caveats/). |
| Uses an injected service | Declare `where R : IHas<TService>` and resolve the service inside the `Gen` block with `scope.Env.Resolve<TService>()`. For a service returning `ValueTask<Result<T, E>>`, use `scope.Get(await service.Call(scope.CancellationToken))` when its error type is `E`. See [Environments](/environments/). |
| Acquires or borrows a resource | Check the [ownership and cleanup rules](#manage-a-resource-with-asynchronous-cleanup). Test cleanup after success, expected failure, defect, and interruption. With multiple resources, test the release order and what each release receives when another release throws. |
| Starts parallel work or a fiber | Use `Effect.All(..., Concurrency.Parallel)` for sibling cancellation on failure. When a release needs a child's exit case, pass that child's `resource.Use(...)` effect to `All`. Observe results within the scope's lifetime and use independent or thread-safe services. See [Concurrency](/concurrency/#all). |
| Adds timeouts or retries | Pass cancellation through and verify cleanup before the call returns. A timeout requests cancellation and waits for work and releases; retry only safe-to-repeat work. See [Time](/time/) and [Retry and repeat](/retry-and-repeat/). |
| Integrates at an application boundary | Follow the [Hosting](/hosting/), [HTTP](/http/), or [EF Core](/entity-framework-core/) guide for service lifetimes, cancellation and error handling. |

For help with a reproducible example, search or open a [GitHub issue](https://github.com/SingUp009/Moderato.Effects/issues). Include the SDK and package versions, diagnostics, and expected behavior.
