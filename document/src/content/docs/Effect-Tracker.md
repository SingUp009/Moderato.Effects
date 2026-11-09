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

Core provides the data API. A built-in viewer and OpenTelemetry integration are not included; visualization is follow-up work in [#399](https://github.com/SingUp009/Moderato.Effects/issues/399).
