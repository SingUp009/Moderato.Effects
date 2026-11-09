# Local execution viewer

A read-only Blazor management screen for the effects running in this process. The example application computes SHA-256 for an HTTP request body, while the viewer shows its execution tree, resource lifetime and trace correlation. UI interactions never start, cancel or release application work.

## Run

Use the SDK selected by `global.json`:

```bash
dotnet watch --project samples/Moderato.Effects.Tracking.Samples
```

Open http://127.0.0.1:5217. Send work from another terminal:

```bash
curl --data-binary 'hello' http://127.0.0.1:5217/sha256
```

The response is the SHA-256 digest. Refresh the page to see the completed execution, then expand its tree and select a record. New work appears automatically every 500 ms. **Pause updates** freezes this browser's view; capture continues. Tabs share the same recording. The recording window closes when the Host stops; work already captured can still finish in that window.

The app explicitly binds IPv4 loopback, allows only `localhost` and `127.0.0.1` Host headers, and rejects cross-origin browser requests. `--urls` does not widen that binding. Change the port with `--Tracking:Port 5219`. This example has no authentication and is intended for use on the same PC; do not publish it through a proxy or tunnel.

## OpenTelemetry

The recording and standard OpenTelemetry instrumentation observe the same effects. No snapshot-to-span conversion is used. To export to a local OTLP collector:

```bash
DOTNET_CLI_TELEMETRY_OPTOUT=1 \
OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317 \
OTEL_EXPORTER_OTLP_PROTOCOL=grpc \
dotnet run --project samples/Moderato.Effects.Tracking.Samples
```

The service name is `Moderato.Effects.Tracking.Samples`. Traces include ASP.NET Core requests and effect boundaries; metrics include effect outcomes and .NET runtime measurements. Exported telemetry does not contain the request body.

**Open trace** opens Aspire's trace details using the selected record's trace and span IDs. `Tracking:TraceViewerUrl` defaults to `http://localhost:18888`; log into that dashboard separately. Other backends can consume the same OTLP data, but this link uses Aspire's URL format. Export and sampling determine whether a recorded trace is available in the backend.

Without a collector, the local viewer still works. Set `OTEL_SDK_DISABLED=true` to disable telemetry export.

## Read the view

| Display | Meaning |
| --- | --- |
| `acquire` | The acquisition attempt |
| `resource` | The managed interval from registration through release completion |
| `release` | The release attempt |
| Body / final | The Scope's body result and its result after cleanup |
| Duration | Elapsed time; active rows use snapshot capture time |
| Before release / release | The two portions of a resource's managed interval |
| Owner / acquired by | The owning Scope and registering operation |
| Retained records | Active and completed records sharing a 2,048-record capacity |
| Dropped / evicted | Missing observations and completed records removed at capacity |

Incomplete capture is labelled **Partial**. Missing references are explicit. Nested and parallel intervals overlap; their durations are not additive CPU or request time. Callback completion does not prove that a physical resource closed successfully. The viewer stores copied metadata, not application resources or exception objects.

## Source

- `Program.cs` configures the local host, telemetry and real `/sha256` endpoint.
- `Execution/Hashing.cs` borrows the request stream and owns the hash algorithm.
- `Execution/TrackingSession.cs` owns the process recording for the Host lifetime.
- `Components/Pages/Home.razor` displays and filters snapshots.
- `Components/Execution/` renders the tree and selected record.
- `tests/Moderato.Effects.Tracking.SampleTests` verifies the sample outside its user-facing code.

Capture is enabled explicitly for this diagnostic sample. Library users choose whether to enable recording; this screen does not observe other processes. Runtime allocation metrics are process-level, not per-effect allocation counts.
