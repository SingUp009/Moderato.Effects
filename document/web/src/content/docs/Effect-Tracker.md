---
title: Effect Tracker
description: Inspect active scopes, fibers, and resources with Effect Tracker. Record execution details and correlate them with OpenTelemetry traces.
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

Stack capture and snapshot allocation have a cost. Enable them deliberately during diagnosis. Tracking is disabled by default; this does not promise that the presence of tracking support has zero execution overhead.

## Build a display

`GetSnapshot()` returns immutable metadata ordered by process-local ID. It does not return an environment, result, resource value or handle to the running operation. A display can poll this API without retaining those objects.

Starts and completions may race with a snapshot. Enabling tracks future starts, and disabling clears the registry; re-enabling does not reconstruct existing work. The switches are process-wide, so coordinate them between tests and debugging tools.

Core provides the data API. For standard tracing and process-wide runtime metrics, see [OpenTelemetry](/opentelemetry/). Its instrumentation is independent of these Tracker switches.

## Record execution details

Start a bounded recording before running the work:

```csharp
using var recording = EffectTracker.Record();
await effect.WithName("Orders.Create").RunExitAsync(environment, cancellationToken);
var snapshot = recording.GetSnapshot();
```

Each operation is recorded under the member that built it (`Map`, `AndThen`, `Timeout`, `Select`, `CatchCase`, ...) unless `WithName` names it. An implicit conversion from a `Never`-typed effect is recorded as `Widen`. Nothing is hidden: an operator the library builds from others, such as a tuple `All`, shows each of its steps (`Map`, `All`, and one `Map` per child).

Recording captures roots started during that session, including detailed operations and retry attempts. It is separate from the active-lifetime registry above: neither `EnableTracking` nor stack capture is required. Only one recording can be active per process. Disposing stops new root capture; already captured executions can finish in their original recording.

`GetSnapshot()` copies one coherent frame of metadata. Each entry has a monotonic `StartOffset`, optional `EndOffset` and `Elapsed`. Active durations extend to the capture instant; completed durations remain fixed. Scope `BodyState` is recorded before cleanup and `State` is the final result. A resource's state includes its release outcome; acquisition has its own entry. Resource and release completion share one observation instant.

The default capacity is 2,048 records. Completed entries can be evicted, and new observations can be dropped when all retained entries are active. `Dropped`, `Evicted` and `IsComplete` describe capture loss, not execution failure. Missing parents and ownership references are reported as unavailable.

## Use the local viewer

The [Tracking sample](https://github.com/SingUp009/Moderato.Effects/tree/main/samples/Moderato.Effects.Tracking.Samples) provides a read-only Blazor screen for execution recordings. Build it from source with the SDK selected by `global.json`.

[![The local viewer showing an active SHA256 resource, its ownership, elapsed time and trace correlation.](/effect-tracker/demo.png)](/effect-tracker/demo.png)

This capture uses a slowly uploaded request so the resource remains active while it is inspected. Select the image to view it at full size.

```sh
dotnet watch --project samples/Moderato.Effects.Tracking.Samples
```

Open `http://127.0.0.1:5217`, then send application work from another terminal:

```sh
curl --data-binary 'hello' http://127.0.0.1:5217/sha256
```

The endpoint computes a real SHA-256 digest. The viewer shows the execution tree, resource ownership, time before release, release duration and final outcome. Select a record for its timing, source location and trace correlation. Filter by operation, trace ID, record ID or root outcome; choose `Running` to follow work still in progress.

The screen refreshes every 500 ms. **Pause updates** freezes only that browser's view; capture and application execution continue. **Refresh** updates the displayed snapshot once. There are no controls to start, cancel or release application work. Tabs share the same recording.

The sample explicitly binds IPv4 loopback and checks Host and Origin headers. It has no authentication: use it on the same PC, without a public reverse proxy or tunnel. Change the port with `--Tracking:Port 5219`. It observes effects in its own process; it does not attach to another application or read a remote collector's data.

The Host owns the recording. Its window closes when the Host stops, while executions already captured can still finish in that recording. Restarting the process starts a fresh history. The screen retains up to 2,048 records and labels incomplete capture; it does not retain application objects or provide a persistent audit log.

Durations include waiting and cleanup, and nested intervals overlap. A completed release attempt does not prove physical resource closure. Runtime allocation metrics describe the process, not an individual Effect.

## Correlate with OpenTelemetry

Execution recording and standard telemetry use the same internal observation points. Enable [OpenTelemetry](/opentelemetry/) normally, then start a recording when detailed local inspection is needed. No second instrumentation wrapper is required.

An entry's `TraceId` and `SpanId` let you look up the surrounding request in your monitoring tool:

```csharp
foreach (var entry in recording.GetSnapshot().Entries)
    Console.WriteLine($"{entry.TraceId}/{entry.SpanId}: {entry.Name} {entry.State}");
```

Execution, Scope, Fiber, acquisition, resource and release entries identify their own span when one was created. Detailed operations and retry attempts identify their nearest traced parent; they do not create a span each. With no tracing context, the IDs are null. Sampling can suppress export even when correlation IDs are present.

Recording capacity does not limit standard telemetry, and trace sampling does not disable local recording. Both features remain optional. Recording adds storage, locking and metadata costs; enabling both does not make detailed capture free.

The local viewer's **Open trace** link opens Aspire's trace detail page using these IDs. `Tracking:TraceViewerUrl` defaults to `http://localhost:18888`; sign into that dashboard separately. The link uses Aspire's URL format. Other monitoring tools can consume the same OTLP data without changing Core.
