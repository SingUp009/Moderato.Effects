# OpenTelemetry

A small HTTP application that hashes the actual request body. Effect execution, Scope, Fiber, acquisition, managed resource and release boundaries emit standard .NET activities and metrics. The OpenTelemetry SDK and OTLP exporters belong to this application; Core has no OpenTelemetry package dependency. Debug tracking and stack capture stay disabled.

## Run

Use the SDK selected by `global.json`. Start an OTLP-compatible viewer, for example the [standalone Aspire dashboard](https://aspire.dev/dashboard/standalone/):

```bash
npx -y @microsoft/aspire-cli@13.6.4 dashboard run
```

Open the login URL printed by the dashboard. Then run this application in another terminal:

```bash
OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317 \
OTEL_EXPORTER_OTLP_PROTOCOL=grpc \
DOTNET_CLI_TELEMETRY_OPTOUT=1 \
dotnet run --project samples/Moderato.Effects.OpenTelemetry.Samples -- --urls http://localhost:5218
```

The CLI opt-out keeps SDK command telemetry out of this viewer; the application still exports its own telemetry. Select the `Moderato.Effects.OpenTelemetry.Samples` service in the dashboard.

Send real requests:

```bash
printf 'hello' | curl --fail-with-body -H 'Content-Type: application/octet-stream' \
  --data-binary @- http://localhost:5218/sha256
```

The response contains the SHA-256 digest. Bodies are streamed and limited to 1 MiB. There are no scenario buttons, artificial delays or injected failures. The application borrows the request stream and owns/disposes its hashing algorithm through `Resource`.

In the viewer, select this service and inspect the HTTP trace and its `Effect run`, `Effect scope`, `Effect acquire`, `Effect resource` and `Effect release` spans. The example does not fork a Fiber; applications that do also produce `Effect fiber` spans.

Metrics include `moderato.effects.completed` and `moderato.effects.duration` (seconds), grouped only by `effect.kind` and `effect.outcome`. `System.Runtime` supplies process-wide allocations, GC, heap and CPU metrics. These are not allocations attributed to individual Effects.

## Connect an existing application

Copy the OpenTelemetry registration from `Program.cs`, select your service name, and set the standard `OTEL_EXPORTER_OTLP_*` environment variables for your collector. The same instrumentation works with `RunAsync`, `RunExitAsync`, Hosting and HTTP integration. No `EffectTracker.Record()` call is needed.

`effect.outcome` distinguishes `ok`, `err`, `die` and `interrupted`. Only `die` sets span status Error: a typed error or interruption does not establish an infrastructure failure. Scope spans also carry `effect.body.outcome`; their final outcome includes child completion and cleanup. Resource spans run from successful registration until the release attempt finishes, including a failed release. Acquisition has its own span. Completed callback observation does not prove physical resource closure.

The activity parent identifies the recorded owning boundary; the application's ambient activity is restored after each boundary. Execution, Scope, Fiber, acquisition and release spans are ambient while that work runs, so ordinary HTTP/client instrumentation can correlate with them. Resource ownership does not replace the ambient activity throughout the body. Nested and parallel durations overlap and cannot be added together as request latency or CPU time.

Trace sampling and exporter queues can omit spans. Standard exporters send completed spans; a never-ending resource may not appear in the trace view. A local Tracker snapshot remains useful for inspecting live work. The counters count completed boundaries, not live resource objects or retained bytes.

Core emits no environment, resource value, error payload, exception message, stack or caller file path. ASP.NET Core and application logging have their own data policies. For deployment, use your existing authenticated collector, TLS and access controls; the local dashboard setup is a viewing example, not a public monitoring service.
