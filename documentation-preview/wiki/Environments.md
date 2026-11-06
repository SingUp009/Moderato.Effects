# Environments

`R` lists the services an effect needs, as constraints:

```csharp
static Effect<User, NotFound, R> FindUser<R>(int id) where R : IHas<IUserRepository> =>
    Scope<NotFound, R>.Gen(async scope =>
    {
        var user = await scope.Env.Resolve<IUserRepository>().FindAsync(id, scope.CancellationToken);
        return user ?? throw scope.Fail(new NotFound(id));
    });
```

- `IHas<T>` provides one service; `env.Resolve<T>()` gets it.
- An effect that calls `FindUser<R>` needs at least `IHas<IUserRepository>` too, and the compiler checks it.
- All requirements are met at the entry point, `RunAsync(env)`. A requirement cannot be removed halfway.

## Declaring an environment

```csharp
[Environment]
public sealed partial record AppEnv(IUserRepository Users, ILogger Logger, TimeProvider Clock);
```

`[Environment]` implements `IHas<T>` for each primary constructor parameter. It works on records and classes.

- The type must be `partial` and have a primary constructor.
- Two parameters of the same type are an error. Wrap one in its own type, or use `[Expose<T>]`.
- `[Expose<T>]` chooses the type a parameter is provided as:

```csharp
[Environment]
public sealed partial record TestEnv([Expose<TimeProvider>] FakeTimeProvider Clock);   // IHas<TimeProvider>
```

`IHas<T>` is invariant, so a `FakeTimeProvider` parameter would otherwise only satisfy `IHas<FakeTimeProvider>`. The test keeps the concrete type, so it can still call `Advance`. If the parameter cannot be converted to `T`, the generator reports MEFF104.

The exposed service type must be unambiguously available by its global name. A parameter available only through `extern alias` can still expose a globally accessible interface through `[Expose<T>]`; the generated code names `T`, not the parameter type.

## Keeping constraints short

When a low-level effect gains a dependency, every caller's `where` clause would change. Two patterns avoid that:

```csharp
// 1. Group the requirements in one interface
public interface IUserDeps : IHas<IUserRepository>, IHas<ILogger>;

[Environment]
public sealed partial record AppEnv(IUserRepository Users, ILogger Logger, TimeProvider Clock) : IUserDeps;

// 2. Group related effects in a generic class, so <R> is written once
public static class Users<R> where R : IUserDeps
{
    public static Effect<User, AppError, R> Find(int id) => ...;
}
```

`AppEnv` satisfies `IUserDeps` through its generated `IHas<>` implementations; if one is missing, it does not compile.

## Built-in environments

| Environment | Provides | Use for |
|---|---|---|
| `EmptyEnvironment.Value` | nothing | effects with no dependencies |
| `DefaultEnvironment.Value` | `TimeProvider.System`, `Random.Shared` | small apps that only need the clock and jitter |

Effects that need one of these can be run without an environment: `effect.RunAsync(cancellationToken)`.

Only the clock (`TimeProvider`) and the random number generator (`Random`) are standard, because the library's own operators use them. Logging, configuration and everything else go into your environment like any other service.

## Changing the environment

- `effect.Provide<R2>(r2 => r)` runs an effect that needs `R` inside one that has `R2`, building `R` from `R2`.
- `effect.WidenEnv<R2>()` uses an effect with any `R2` that implements `R`.

## Dependency injection

Register the environment as an ordinary service. There is no DI-specific API:

```csharp
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<AppDbContext>();
builder.Services.AddScoped<AppEnv>();

app.MapGet("/users/{id}", async (int id, AppEnv env, CancellationToken ct) =>
    await GetUserProfile<AppEnv>(id).RunAsync(env, ct) switch { ... });
```

- Register the environment with the lifetime of its shortest-lived service: an environment holding a scoped `DbContext` must be scoped too. `ValidateScopes` catches a mistake at startup.
- Keyed services (`[FromKeyedServices]`) work on the constructor parameters.
- A `DbContext` is not thread-safe. Effects run with `Concurrency.Parallel` should get an `IDbContextFactory<T>` instead.
- Work that must outlive a request belongs in a hosted service, not in a fiber: a fiber ends with the effect that started it.
- There is no environment that wraps `IServiceProvider`, on purpose: the requirements would vanish from the type and fail only at run time.

| Built with | Suits | Lifetimes managed by |
|---|---|---|
| DI | ASP.NET Core, Generic Host | the DI scope |
| `Resource` and `Provide` | console apps, tests | the effect (`Use`) |
| `new` | tests | the test |
| `EmptyEnvironment` / `DefaultEnvironment` | effects with no or only standard dependencies | nothing to manage |

## Thread safety

Sequential execution keeps children within one group from using a service concurrently. It does not serialize separate runs: two callers running effects with the same environment can still use its services at the same time. Match service lifetimes to the run, or use thread-safe services or factories when the environment is shared.

The library's own use of the environment's `Random` is safe even if the instance is not thread-safe (see [Retry and repeat](Retry-and-repeat)).
