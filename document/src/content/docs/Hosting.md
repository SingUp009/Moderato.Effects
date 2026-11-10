---
title: Hosting
description: Run an effect as a .NET BackgroundService, supply its typed environment from dependency injection, and connect its diagnostics to the host.
---

# Hosting

Install `Moderato.Effects.Hosting`, which brings Core as a transitive dependency. See [Installation](/installation/) for package setup.

`Moderato.Effects.Hosting` runs an `Effect<Unit, E, R>` as a .NET `BackgroundService`. The Host owns the diagnostic connection; each service execution owns an asynchronous DI scope for its typed environment.

## Register a worker

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moderato.Effects;
using Moderato.Effects.Hosting;
using Moderato.Results;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddScoped<WorkerEnvironment>();

builder.Services.AddEffectsHosting(options =>
{
    options.Diagnostics = services => new LoggerDiagnostics(
        services.GetRequiredService<ILoggerFactory>());
});

Effect<Unit, string, WorkerEnvironment> worker =
    Effect<WorkerEnvironment>.AccessAsync<Unit>(async (environment, token) =>
    {
        await environment.RunAsync(token);
        return Unit.Value;
    });

builder.Services.AddEffectHostedService(worker,
    scopedServices => scopedServices.GetRequiredService<WorkerEnvironment>(),
    static (error, environment, token) => environment.ReportAsync(error, token));

await builder.Build().RunAsync();
```

`WorkerEnvironment.RunAsync(CancellationToken)` represents your long-running business operation; `ReportAsync(string, CancellationToken)` returns a `ValueTask`. The environment is ordinary application code. See the [finite, runnable sample](https://github.com/SingUp009/Moderato.Effects/tree/main/samples/Moderato.Effects.Hosting.Samples) for a complete program.

Registration builds a description. Starting the Host runs each registration once, including separate registrations with the same E and R types. A completed worker is not restarted, and its successful completion does not stop the Host. Put deliberate repetition or retry in the effect itself.

An environment factory can construct a value-type environment or resolve services from the execution scope. Keep `IServiceProvider` in registration code, rather than exposing it as a service locator inside business effects. A long-running worker holds its scope until completion; create dependencies per operation when a single long-lived scope would be inappropriate. Scoped services such as DbContext are not automatically safe to share between parallel children.

## Handle results

| Result | Host boundary |
|---|---|
| Ok | Complete the service normally |
| Err | Await the required typed onError handler before disposing the environment |
| Die | Throw an `EffectHostedServiceException` with the original `Die` in `Exit` and its exception in `InnerException` |
| Interrupted, stop token canceled when received | Complete normally |
| Interrupted, stop token not canceled when received | Throw `EffectHostedServiceException` retaining `Interrupted` in `Exit` |

A successful onError consumes the Err. If it throws synchronously or fails after awaiting, the handler failure becomes Die and the original Err remains Sequential context. An OCE is Interrupted only when the effect's execution token is canceled; other exceptions remain defects. Interrupted's public representation does not expose supplemental errors.

Scope disposal is part of the same Core `Resource` boundary. If both the body and disposal fail, the Core failure rules choose the primary defect and preserve the other with its Relation. Disposing the environment does not overwrite the body defect. After a successful error handler, an unrelated disposal failure does not revive the handled Err.

Die containing OCE is wrapped in a non-OCE exception, including during shutdown. It therefore remains an abnormal service completion. The Host's `BackgroundServiceExceptionBehavior` decides whether to stop the application or ignore a completed service's fault; Hosting does not change that setting or restart the service.

## Choose diagnostics

`AddEffectHostedService` supplies `LoggerDiagnostics` by default. `AddEffectsHosting` lets you choose another `IEffectDiagnostics` implementation by factory. The factory uses root services, runs once per Host when first resolved, and its result is owned and disposed by that Host's DI container. The last diagnostic configuration wins. Do not resolve scoped services in that factory, return a borrowed shared instance, or dispose a borrowed ILoggerFactory in your implementation. An implementation may receive calls from concurrent work and must be thread-safe.

Use your normal logging providers. With ZLogger installed, configure `builder.Logging.AddZLoggerConsole()` in application code; no ZLogger-specific Hosting adapter is needed. Standard ILogger providers also work.

The diagnostic connection applies to the roots started through these registrations and their children. It does not automatically cover arbitrary effects run in HTTP requests. Hosting never replaces the process-wide `EffectDiagnostics.UnobservedFailureHandler`.

Default log events use category `Moderato.Effects.Hosting`:

| Event | Level | Fields |
|---|---|---|
| 1001 UnobservedDefect | Error | FailureKind, SuppressedCount, OmittedSuppressed; original exception |
| 1002 UnjoinedError | Warning | FailureKind, ErrorType |
| 1003 SuppressedFailure | Error | SuppressedIndex (zero-based), Relation, FailureKind, ErrorType; exception when present |

Retained context is emitted in order; omitted context has a count, not invented records. `ErrorType` is the short runtime name of `Failed.Error` (for example, `Rejected`), not a unique type identifier. It is null for a null error or a non-Failed context entry. Arbitrary typed error values are not automatically formatted or exposed, and union values are not projected again. Disabled logs do not fetch type names or enumerate context. Diagnostic failures do not replace the business outcome; the Host itself owns logging the primary service fault.

No-op and disabled-provider checks measure emitter allocation only. Providers, formatting, output and delivery can have their own costs; zero allocation or durable delivery is not promised for the complete logging pipeline.

## Stop and dispose

Pass shutdown through the Host. Effects stop their managed children and run registered cleanup with an uncancelable token; they cannot force non-cooperative work to end. Configure `HostOptions.ShutdownTimeout` and arrange for cleanup to finish within the Host's overall budget. Hosting neither extends that budget nor holds Host disposal open indefinitely.

If the budget expires, `StopAsync` may return while cleanup is still running. Its execution scope remains owned by that execution, but Host disposal can already destroy root-owned dependencies. Keeping the child scope alive does not protect those dependencies.

Host disposal disconnects diagnostics before disposing the implementation. Late diagnostic callbacks release their reference to the Host-owned implementation and use the current global handler, or Console.Error when none is set. This late fallback is process-wide and cannot guarantee output after process exit. Use asynchronous Host disposal when your diagnostic implementation is only IAsyncDisposable.
