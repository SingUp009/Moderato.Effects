# Moderato.Effects

Effect System for C#

```csharp
// async / await
async Task<Profile> GetUserProfileAsync(int id)
{
    // Which expected failures should the caller handle? Which services does it touch?
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

`Effect<Profile, AppError, R>` is a description of work: it succeeds with a `Profile`, has expected errors of type `AppError`, and needs an environment `R` that provides the listed services. The described work runs through `RunAsync` or `RunExitAsync`, and every run starts over, which is what makes `Timeout`, `Retry` and `Effect.All` composable.

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

- .NET 11 SDK (`11.0.100-rc.3` or later), `net11.0`
- C# 15

Core is Native AOT compatible (`IsAotCompatible`). The package includes source generators and analyzers.

## Installation

```
dotnet add package Moderato.Effects --prerelease
```

## Packages

| Package | Provides |
|---|---|
| `Moderato.Effects` | Core effects and the opt-in Effect Tracker |
| `Moderato.Effects.Hosting` | .NET background service integration |
| `Moderato.Effects.Http` | ASP.NET Core request execution |
| `Moderato.Effects.EntityFrameworkCore` | EF Core transactions and scope-aware saves |

The integration packages depend on Core; it does not need to be installed separately.

## Documentation

- [Documentation](https://moderato-effects.009singup.workers.dev) — guides and tutorials.
- [API Reference](https://github.com/SingUp009/Moderato.Effects/wiki) — reference for the public API.

## Building from source

```
dotnet build
dotnet test -c Release
dotnet run --project samples/Moderato.Effects.Samples
dotnet run -c Release --project bench/Moderato.Effects.Benchmarks -- --filter '*' --inProcess
```

BenchmarkDotNet 0.15.8 needs `--inProcess` on the .NET 11 preview.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for bug reports, feature requests and pull requests.

## Changelog

See [CHANGELOG.md](https://github.com/SingUp009/Moderato.Effects/blob/main/CHANGELOG.md). Breaking changes are listed first for every release.

## License

MIT. See [LICENSE](https://github.com/SingUp009/Moderato.Effects/blob/main/LICENSE).
