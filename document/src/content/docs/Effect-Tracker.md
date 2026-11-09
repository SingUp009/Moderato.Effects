---
title: Effect Tracker
---

# Effect Tracker

Use it to inspect active scopes, fibers and acquired resources while debugging. No additional package is needed.

## Inspect active work

Enable tracking before starting the work you want to inspect:

```csharp
using Moderato.Effects;

EffectTracker.EnableTracking = true;

foreach (var item in EffectTracker.GetSnapshot())
    Console.WriteLine($"{item.Id}: {item.Kind}, started {item.StartedAt:O}");

EffectTracker.EnableTracking = false;
```

An empty snapshot means there are no entries at the time of that query. This short example starts no work of its own; call `GetSnapshot()` while your application is running effects.

| Kind | Tracked lifetime |
|---|---|
| Scope | A Gen block or Resource.Use, including cleanup |
| Fiber | A forked effect until completion |
| Resource | A successful Resource.Make acquisition until its release completes |

This is a view of active lifetimes, not a history of every operator or outcome. An entry staying active can mean legitimate waiting or cleanup; it is not by itself evidence of a leak.

## Capture creation stacks

```csharp
EffectTracker.EnableStackTrace = true;
EffectTracker.EnableTracking = true;
```

Stack capture applies only to new entries and is disabled by default. Each snapshot entry has `Id`, `Kind`, `TypeName`, `StartedAt` and an optional `StackTrace`. `TypeName` describes the internal lifetime owner, not an application operation name.

Stack capture and snapshot allocation have a cost. Enable them deliberately during diagnosis. Tracking is disabled by default; this does not promise that the presence of tracking support has zero execution overhead. Disabled completion overhead is tracked in [#401](https://github.com/SingUp009/Moderato.Effects/issues/401).

## Build a display

`GetSnapshot()` returns immutable metadata ordered by process-local ID. It does not return an environment, result, resource value or handle to the running operation. A display can poll this API without retaining those objects.

Starts and completions may race with a snapshot. Enabling tracks future starts, and disabling clears the registry; re-enabling does not reconstruct existing work. The switches are process-wide, so coordinate them between tests and debugging tools.

Core provides the data API. For standard tracing and process-wide runtime metrics, see [OpenTelemetry](opentelemetry). Its instrumentation is unreleased and is independent of these Tracker switches.

## Record execution details

The following API is unreleased. It is not available in the published preview.5 package.

Start a bounded recording before running the work:

```csharp
using var recording = EffectTracker.Record();
await effect.WithName("Orders.Create").RunExitAsync(environment, cancellationToken);
var snapshot = recording.GetSnapshot();
```

Recording captures roots started during that session, including detailed operations and retry attempts. It is separate from the active-lifetime registry above: neither `EnableTracking` nor stack capture is required. Only one recording can be active per process. Disposing stops new root capture; already captured executions can finish in their original recording.

`GetSnapshot()` copies one coherent frame of metadata. Each entry has a monotonic `StartOffset`, optional `EndOffset` and `Elapsed`. Active durations extend to the capture instant; completed durations remain fixed. Scope `BodyState` is recorded before cleanup and `State` is the final result. A resource's state includes its release outcome; acquisition has its own entry. Resource and release completion share one observation instant.

The default capacity is 2,048 records. Completed entries can be evicted, and new observations can be dropped when all retained entries are active. `Dropped`, `Evicted` and `IsComplete` describe capture loss, not execution failure. Missing parents and ownership references are reported as unavailable.

## Correlate with OpenTelemetry

Execution recording and standard telemetry use the same internal observation points. Enable [OpenTelemetry](opentelemetry) normally, then start a recording when detailed local inspection is needed. No second instrumentation wrapper is required.

An entry's `TraceId` and `SpanId` let you look up the surrounding request in your monitoring tool:

```csharp
foreach (var entry in recording.GetSnapshot().Entries)
    Console.WriteLine($"{entry.TraceId}/{entry.SpanId}: {entry.Name} {entry.State}");
```

Execution, Scope, Fiber, acquisition, resource and release entries identify their own span when one was created. Detailed operations and retry attempts identify their nearest traced parent; they do not create a span each. With no tracing context, the IDs are null. Sampling can suppress export even when correlation IDs are present.

Recording capacity does not limit standard telemetry, and trace sampling does not disable local recording. Both features remain optional. Recording adds storage, locking and metadata costs; enabling both does not make detailed capture free.
