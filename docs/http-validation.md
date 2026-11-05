# HTTP preview validation

Issue #400 implements `Moderato.Effects.Http` on the Core baseline `526c2ca`. This record distinguishes product checks from the earlier prototype. No package has been published by this work.

## Toolchain

Use `/tmp/moderato-dotnet-rc3/dotnet`, SDK `11.0.100-rc.3.26522.148`, matching ASP.NET Core runtime/OpenAPI RC3. System RC2 is not the acceptance toolchain. Revalidation on .NET 11 GA is outstanding. Long soak is deferred to the user's always-on PC.

## Acceptance evidence

Initial product verification on RC3:

- Public-API xunit: 36 passed, 0 failed, 0 skipped.
- Real HTTP/OpenAPI sample: 45 PASS lines (including its final completion line), exit 0. Verified both ordinary responses and response-started/client-disconnection behavior.

Solution Release build: 0 warnings and 0 errors. All 2,423 solution tests passed; existing Core and Hosting samples completed. Package, NativeAOT, performance and independent clean-clone checks remain in progress. Source presence and a successful compile do not imply all acceptance conditions have passed.

- `tests/Moderato.Effects.Http.Tests`: public registration and runner tests, sync/async exits, captured token replacement, settled results, children/cleanup, ordered suppressed context, logging and late disconnection.
- `samples/Moderato.Effects.Http.Samples --verify`: real Kestrel requests, public/protected routes, explicit error mappings, OpenAPI schemas/security/statuses, cancellation defects, response-started failure and client disconnection.
- `tests/Moderato.Effects.Http.PackageTests`: HTTP-only dependency, generated environment/error union, typed responses, negative analyzer/exhaustiveness/response-type checks (the CS8509 exhaustiveness warning fails the build because this repository enables TreatWarningsAsErrors; CS0029 response incompatibility is always an error) and JIT/NativeAOT execution.
- `tests/verify-packages.py`: all three packages' metadata, payload, symbols/SourceLink, HTTP's Core dependency and ASP.NET shared framework reference.

## Performance protocol

`bench/Moderato.Effects.Http.Benchmarks` compares the same immutable effect and integer response through direct `RunExitAsync` and the request runner. Both diagnostic paths use standard logging and the same warning message shape. Cases cover synchronous/asynchronous effects, normal/unjoined-error paths, and enabled/disabled logging. At least one second of warmup per arm precedes five alternating paired rounds. Shared delegate invocation uses an unoptimized non-inlined helper to avoid specializing the measurement loop for one arm; product code retains its normal JIT optimization. This controls only that invocation boundary; it does not control all JIT decisions, GC activity or code placement. A separate scope-only versus scope-plus-runner comparison measures request creation/disposal. Real HTTP uses stateless endpoints, bounded response data and no growing store.

The counting provider measures logging dispatch without provider I/O. `GC.GetTotalAllocatedBytes` covers the entire process, including asynchronous execution, the in-process HTTP client and background activity. HTTP totals are not server-only allocation costs. Ranges and paired differences must be reported; a noisy sign change does not establish an optimization. This finite comparison is not a throughput, saturation or long-term safety test. Default diagnostics performance acceptance remains open until the measured cost is reviewed.

## Documentation delivery

`docs/wiki-patches/HTTP.md` is an English wiki draft, not a published wiki page. The release owner must apply it to the separate wiki repository and add a navigation link with the release's other guide changes.
