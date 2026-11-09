---
title: OpenTelemetry
---

# OpenTelemetry

> This integration is unreleased. The published preview.5 package does not emit these activities or metrics.

Collect Effect execution telemetry using .NET's standard `ActivitySource` and `Meter`. Core does not depend on the OpenTelemetry SDK. Applications select their SDK, exporter and monitoring backend.

## Register collection

Add `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Exporter.OpenTelemetryProtocol` and, for HTTP applications, `OpenTelemetry.Instrumentation.AspNetCore` to your application. Configure the providers at startup:

```csharp
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("Orders"))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddSource("Moderato.Effects")
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddMeter("Moderato.Effects", "System.Runtime")
        .AddOtlpExporter());
```

Set `OTEL_EXPORTER_OTLP_ENDPOINT` and `OTEL_EXPORTER_OTLP_PROTOCOL` for your collector. Use your application's existing TLS and authentication configuration. The runnable sample includes package references, namespace imports, logging export and local viewer instructions:

[OpenTelemetry sample](https://github.com/SingUp009/Moderato.Effects/tree/main/samples/Moderato.Effects.OpenTelemetry.Samples)

Run Effects normally. No Tracker switches or recording session are required.

## Boundaries and outcomes

| Boundary | Observed interval |
| --- | --- |
| `run` | A public RunAsync or RunExitAsync execution, through cleanup |
| `scope` | Gen or Resource.Use, through child completion and cleanup |
| `fiber` | A forked child, from launch through its completion |
| `acquire` | A resource acquisition attempt |
| `resource` | Successful registration through completion of the release attempt |
| `release` | One registered release or deferred callback |

`effect.outcome` is `ok`, `err`, `die` or `interrupted`. Only `die` sets the span status to Error. Typed errors and interruption retain their classification without asserting an infrastructure failure. Scope spans also contain `effect.body.outcome`, recorded before cleanup; cleanup can change the final result.

The resource's outcome includes its release outcome. Acquisition has a separate span. Completion of a release attempt, especially a failing one, does not prove physical resource closure.

The execution context carries owning boundaries across cancellation changes and concurrent work. Execution, Scope, Fiber, acquisition and release spans are ambient while their work runs; the caller's ambient activity is restored afterwards. A resource's retention interval does not become the ambient span for its whole lifetime.

## Metrics

The `Moderato.Effects` meter exports:

- `moderato.effects.completed`: completed boundaries, in `{boundary}` units.
- `moderato.effects.duration`: boundary duration, in seconds.

Both use only `effect.kind` and `effect.outcome` as dimensions. They do not include identifiers, names, error types, stack traces or caller paths. The `System.Runtime` meter supplies process-wide allocation, GC, heap and CPU measurements; these cannot be attributed directly to an individual asynchronous Effect.

## Interpretation

Tracing can be sampled, and exporters can drop queued data. Standard span exporters report completed intervals, so a resource that has not finished may not appear yet. Use the [Effect Tracker](effect-tracker) for a local view of active lifetimes.

Durations include waits, scheduling and instrumentation. Nested or parallel intervals overlap: their sum is not request latency or CPU time. Metrics count completed observations, not physical resource objects or bytes retained.

Core emits no environment, resource value, typed error payload, exception message, stack or source path. Additional HTTP instrumentation and logging have their own data policies. Observer failures do not replace the Effect's outcome; successful delivery is not guaranteed.

## Inspect the same request locally

Start `EffectTracker.Record()` to collect detailed execution metadata alongside standard telemetry. The two outputs share lifecycle observations and monotonic duration measurements. Snapshot entries expose `TraceId` and `SpanId` for correlation; operation and retry entries refer to their nearest traced parent rather than generating extra spans. See [execution recording](effect-tracker#record-execution-details).

The recording's bounded history and the exporter's sampling/queue limits remain independent. The active-lifetime debug registry also remains available without either output.
