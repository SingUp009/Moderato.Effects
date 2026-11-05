# HTTP preview validation

Issue #400 implements `Moderato.Effects.Http` starting from Core `526c2ca` and integrating the merged Tracker baseline `b85a9a5`. This record distinguishes product checks from the earlier prototype. No package has been published by this work.

## Toolchain

Use `/tmp/moderato-dotnet-rc3/dotnet`, SDK `11.0.100-rc.3.26522.148`, matching ASP.NET Core runtime/OpenAPI RC3. System RC2 is not the acceptance toolchain. Revalidation on .NET 11 GA is outstanding. Long soak is deferred to the user's always-on PC.

## Acceptance evidence

Product verification on RC3 after integrating `b85a9a5`:

- All 2,439 solution tests passed: Core 1,612, generators/analyzers 762, Hosting 27, HTTP 38. HTTP registration preserves both existing Tracker settings.
- Real HTTP/OpenAPI sample: 45 PASS lines (including its final completion line), exit 0, also rechecked after integration. Existing Core and Hosting samples completed.
- Release solution and HTTP benchmark builds: 0 warnings and 0 errors. Solution whitespace/style and HTTP consumer style checks passed.
- Core, Hosting and HTTP packed as `0.1.0-local.http.ab1f73c.1`; metadata, payload, XML, symbols and SourceLink commit `ab1f73c2e711ce7e8458722d70a1c6ff29cc6993` passed verification. All three package consumers passed JIT and NativeAOT execution. Package source is unchanged by later test/document/benchmark updates.
- HTTP-only negative builds reported MEFF301, CS8509 and CS0029 separately. A control with `TreatWarningsAsErrors=false` built successfully with warning CS8509.

The [performance report](http-performance.md) and [170-sample CSV](measurements/http-rc3.csv) record the final comparison, including residual drift. Performance acceptance and independent clean-clone verification remain subject to review.

- `tests/Moderato.Effects.Http.Tests`: public registration and runner tests, sync/async exits, captured token replacement, settled results, children/cleanup, ordered suppressed context, logging and late disconnection.
- `samples/Moderato.Effects.Http.Samples --verify`: real Kestrel requests, public/protected routes, explicit error mappings, OpenAPI schemas/security/statuses, cancellation defects, response-started failure and client disconnection.
- `tests/Moderato.Effects.Http.PackageTests`: HTTP-only dependency, generated environment/error union, typed responses, negative analyzer/exhaustiveness/response-type checks (the CS8509 exhaustiveness warning fails the build because this repository enables TreatWarningsAsErrors; CS0029 response incompatibility is always an error) and JIT/NativeAOT execution.
- `tests/verify-packages.py`: all three packages' metadata, payload, symbols/SourceLink, HTTP's Core dependency and ASP.NET shared framework reference.

## Performance protocol

`bench/Moderato.Effects.Http.Benchmarks` compares the same immutable effect and integer response through direct `RunExitAsync` and the request runner. Both diagnostic paths use standard logging and the same warning message shape. Cases cover synchronous/asynchronous effects, normal/unjoined-error paths, and enabled/disabled logging. At least one second of warmup per arm precedes five alternating paired rounds. Shared delegate invocation uses an unoptimized non-inlined helper to avoid specializing the measurement loop for one arm; product code retains its normal JIT optimization. This controls only that invocation boundary; it does not control all JIT decisions, GC activity or code placement. A separate scope-only versus scope-plus-runner comparison measures request creation/disposal with the same timed warmup and alternating order. Real HTTP uses stateless endpoints, bounded response data and no growing store. Both HTTP arms resolve the same scoped environment type; equal-length route names avoid a request-path size difference. Reuse rounds run 200,000 normal synchronous operations or 10,000 asynchronous/diagnostic operations; HTTP warmup runs at least 40,000 requests per arm followed by at least one second of additional warmup; measured HTTP rounds run 1,000 requests. Counts are included in the CSV.

The counting provider measures logging dispatch without provider I/O. `GC.GetTotalAllocatedBytes` covers the entire process, including asynchronous execution, the in-process HTTP client and background activity. HTTP totals are not server-only allocation costs. Ranges and paired differences must be reported; a noisy sign change does not establish an optimization. This finite comparison is not a throughput, saturation or long-term safety test. Default diagnostics performance acceptance remains open until the measured cost is reviewed.

The first run used only timed HTTP warmup and a 5,000-operation scope warmup. It showed a first HTTP sequence of about 108 → 65 microseconds and runner construction falling from about 792 → 559 ns. Those timing values are preliminary evidence of insufficient warmup or a changing process state, not accepted performance results. The revised protocol above is used for the final comparison.

## Documentation delivery

`docs/wiki-patches/HTTP.md` is an English wiki draft, not a published wiki page. The release owner must apply it to the separate wiki repository and add a navigation link with the release's other guide changes.
