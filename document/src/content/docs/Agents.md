---
title: Moderato.Effects for coding agents
description: Find version-appropriate APIs, write a first effect, and verify typed errors, cancellation, and resource lifetimes in generated C# code.
---

# Moderato.Effects for coding agents

Moderato.Effects is an effect system for C#. It requires .NET 11 or later and C# 15 union support. In an existing application, check `global.json`, the target framework, and the resolved NuGet versions before choosing APIs. Check `Directory.Packages.props` if the application manages package versions centrally. Core includes the source generators and analyzers.

Start at [`/llms.txt`](/llms.txt) to find individual Markdown guides. For example, fetch [`/getting-started.md`](/getting-started.md) for the quickstart. Use the [API reference](https://github.com/SingUp009/Moderato.Effects/wiki), release changelog, and the installed package to check signatures for the version you are targeting.

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

`RunAsync` returns a Moderato.Results `Result<T, E>` and throws for defects or interruption. `RunExitAsync` returns those outcomes as `Die` and `Interrupted` alongside `Ok<T>` and `Err<E>`. Pass the caller's cancellation token and supply the environment when required. The token observed inside an effect may be linked to the caller's token. See [Failures](/failures/) and [Environments](/environments/).

## Verify the implementation

Run the application's `dotnet build` and `dotnet test`. Exercise success, each expected error, and any defect or cancellation the boundary must handle. Use [MEFF diagnostics](/analyzers/) to fix the source of a generator error or scope warning. For example, MEFF310 means a scope step must be awaited immediately or returned directly from a non-async body. The [runnable tutorial](/tutorial/00-what-is-an-effect/) covers the same APIs in a small application.

| When writing code that… | Check |
| --- | --- |
| Starts async work | Defer work by passing a factory and pass its token to the awaited operation. Use [Gen blocks](/gen-blocks/) for composition and keep the scope inside its body. |
| Combines different error types | Model expected failures with explicit cases and check the supported widening conversions. Give `Fail` its intended error type. See [Error unions](/error-unions/) and [Caveats](/caveats/). |
| Acquires or borrows a resource | Use `Resource.Use` or `scope.Acquire` for owned resources. Keep caller-owned services under their caller's ownership. Verify release after success, failure, and interruption. See [Resources](/resources/). |
| Starts parallel work or a fiber | Observe results within the scope's lifetime. Use independent or thread-safe services, including separate EF Core contexts for parallel operations. See [Concurrency](/concurrency/). |
| Adds timeouts or retries | Pass cancellation through and verify cleanup before the call returns. A timeout requests cancellation and waits for work and releases; retry only safe-to-repeat work. See [Time](/time/) and [Retry and repeat](/retry-and-repeat/). |
| Integrates at an application boundary | Follow the [Hosting](/hosting/), [HTTP](/http/), or [EF Core](/entity-framework-core/) guide for service lifetimes, cancellation and error handling. |

For help with a reproducible example, search or open a [GitHub issue](https://github.com/SingUp009/Moderato.Effects/issues). Include the SDK and package versions, diagnostics, and expected behavior.
