# Moderato.Effects

[Effect](https://effect.website) for C#

```csharp
// async / await
async Task<Profile> GetUserProfileAsync(int id)
{
    // Which errors can this throw? Which services does it touch?
    // Can it be canceled? Is it safe to retry?
    // The signature cannot tell you.
}
```

```csharp
using Moderato.Effects;

static Effect<Profile, AppError, R> GetUserProfile<R>(int id) where R : IHas<IUserRepository>, IHas<TimeProvider> =>
    Scope<AppError, R>.Gen(async scope =>
    {
        var user = await scope.Run(FindUser<R>(id));      // an Err stops the block here
        await scope.Run(CheckAccess<R>(user));
        var now = scope.Env.Resolve<TimeProvider>().GetUtcNow();
        return new Profile(user.Name, now);
    });

var result = await GetUserProfile<AppEnv>(id)
    .Timeout(TimeSpan.FromSeconds(5))
    .Retry(Schedule.Exponential(TimeSpan.FromMilliseconds(100)).UpTo(3), when: e => e is TimeoutError)
    .RunAsync(env, cancellationToken);
```

`Effect<Profile, AppError, R>` is a description of work: it succeeds with a `Profile`, fails with an `AppError`, and needs an environment `R` that provides the listed services. Nothing runs until `RunAsync`, and every run starts over, which is what makes `Timeout`, `Retry` and `Effect.All` composable.

```csharp
var message = result switch
{
    Ok<Profile>(var profile)         => profile.Name,
    Err<AppError>(NotFound n)        => $"no user {n.Id}",
    Err<AppError>(Forbidden f)       => $"forbidden: {f.Reason}",
    Err<AppError>(TimeoutError t)    => $"timed out after {t.After}",
};
```

Results are [Moderato.Results](https://github.com/SingUp009/Moderato.Results) `Result<T, E>` values, so everything that works with a `Result` works here.

**Warning:** .NET 11 is in preview, and so is this library.

## Requirements

- .NET 11 SDK (`11.0.100-rc.2` or later), `net11.0`
- C# 15

The library is Native AOT compatible (`IsAotCompatible`). The package includes source generators and analyzers.

## Installation

```
dotnet add package Moderato.Effects --prerelease
```

## Documentation

The API and how to use it are in the [wiki](https://github.com/SingUp009/Moderato.Effects/wiki):

- [Getting started](https://github.com/SingUp009/Moderato.Effects/wiki/Getting-started)
- [Gen blocks](https://github.com/SingUp009/Moderato.Effects/wiki/Gen-blocks)
- [Operators](https://github.com/SingUp009/Moderato.Effects/wiki/Operators)
- [Error unions](https://github.com/SingUp009/Moderato.Effects/wiki/Error-unions)
- [Environments](https://github.com/SingUp009/Moderato.Effects/wiki/Environments)
- [Resources](https://github.com/SingUp009/Moderato.Effects/wiki/Resources)
- [Time](https://github.com/SingUp009/Moderato.Effects/wiki/Time)
- [Concurrency](https://github.com/SingUp009/Moderato.Effects/wiki/Concurrency)
- [Retry and repeat](https://github.com/SingUp009/Moderato.Effects/wiki/Retry-and-repeat)
- [Failures](https://github.com/SingUp009/Moderato.Effects/wiki/Failures)
- [Analyzers and diagnostics](https://github.com/SingUp009/Moderato.Effects/wiki/Analyzers)
- [Caveats](https://github.com/SingUp009/Moderato.Effects/wiki/Caveats)

## Building from source

```
dotnet build
dotnet test -c Release
dotnet run --project samples/Moderato.Effects.Samples
dotnet run -c Release --project bench/Moderato.Effects.Benchmarks -- --filter '*' --inProcess
```

See the [performance measurements](docs/performance/README.md) for execution time, allocations, measurement conditions and reproduction commands. BenchmarkDotNet 0.15.8 requires `--inProcess` with the current .NET 11 preview.

The [zero-allocation contract](docs/performance/zero-allocation/README.md) defines the supported execution paths and their regression tests, including LINQ and custom Retry/Repeat policies.

See [allocation coverage by feature](docs/performance/coverage.md) for the current guarantees, built-in scheduling support and remaining API constraints.

## Changelog

See [CHANGELOG.md](https://github.com/SingUp009/Moderato.Effects/blob/main/CHANGELOG.md). Breaking changes are listed first for every release.

## License

MIT. See [LICENSE](https://github.com/SingUp009/Moderato.Effects/blob/main/LICENSE).
