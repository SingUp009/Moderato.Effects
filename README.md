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

## Zero allocation

Running an effect that is already built should cost nothing on the managed heap. An effect is built once and run many times, so the library keeps its per-run work free of closures, context objects and boxing, and regression tests hold each covered operator to 0 bytes per run.

A run allocates nothing when:

- the effect, its callbacks and the environment were built beforehand and have run once;
- it runs with no `CancellationToken` (or `CancellationToken.None`);
- every step completes synchronously, and callbacks and the effects they return allocate nothing themselves;
- it ends with Ok or Err, with no exception, interruption or suppressed failure.

| Covered | Not yet covered |
|---|---|
| `Sync`, `Access`, `Try`, and `Async` / `AccessAsync` / `TryAsync` / `FromResultAsync` that complete at once | building effects |
| `Map`, `MapErr`, `Tap`, `TapErr`, `Match`, `UnwrapOr`, `UnwrapOrElse`, `Attempt`, `Provide` | a cancelable token, `Timeout` |
| `AndThen`, `AndThrough`, `OrElse` returning a reused effect, and `CatchCase` with a handler that cannot fail | Gen blocks, resources, fibers, `Race`, `OnThreadPool` |
| LINQ query syntax over effects | `All`, `AllSettled`, `AllErrors` |
| `Retry` / `Repeat` with a policy, or with a schedule whose waits are zero and not jittered | jitter, waits longer than zero, work that completes asynchronously |
| `Effect.Sleep(TimeSpan.Zero)`, also with a value-type environment | exceptions and suppressed failures |

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

BenchmarkDotNet 0.15.8 needs `--inProcess` on the .NET 11 preview.

## Changelog

See [CHANGELOG.md](https://github.com/SingUp009/Moderato.Effects/blob/main/CHANGELOG.md). Breaking changes are listed first for every release.

## License

MIT. See [LICENSE](https://github.com/SingUp009/Moderato.Effects/blob/main/LICENSE).
