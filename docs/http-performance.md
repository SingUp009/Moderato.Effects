# HTTP comparison on RC3

Measured 2026-11-05 on AMD Ryzen 7 255, .NET/ASP.NET Core `11.0.0-rc.3.26522.148`, Release, Server GC requested by the runtime configuration. Source commit: `5d1a3a8`. Tracker remained disabled. Other agents paused builds and measurements. The run exited 0; the counting provider observed 2,045,001 diagnostic calls including warmup.

The [CSV](measurements/http-rc3.csv) contains all 170 measured samples. Five alternating paired rounds per case; timings below include benchmark invocation/validation overhead. HTTP uses an in-process client and stateless identical integer responses. Allocation totals cover the entire process, including client, framework, asynchronous scheduling and background activity. They are not server-only costs. The provider counts calls without I/O; these results do not predict a file/network provider.

Difference means adapter minus direct **within the same round**, then the median of five differences. The difference of the two displayed arm medians is a different statistic. Brackets show observed minimum and maximum paired differences, not confidence intervals.

## Reused runner

| Async | Diagnostic | Logging | Direct median ns | Adapter median ns | Paired delta ns [min, max] | Process delta B/op [min, max] |
|---|---|---|---:|---:|---:|---:|
| no | no | no | 43.11 | 91.89 | 48.78 [40.99, 58.44] | 0.00 [0.00, 0.00] |
| no | no | yes | 41.40 | 80.33 | 38.98 [38.40, 39.46] | 0.00 [0.00, 0.00] |
| no | yes | no | 1946.96 | 2189.90 | 247.03 [160.53, 371.12] | 168.61 [163.56, 180.12] |
| no | yes | yes | 2204.06 | 2364.35 | 160.29 [58.38, 352.38] | 169.97 [165.16, 174.55] |
| yes | no | no | 993.91 | 1214.89 | 222.78 [85.84, 294.12] | 178.91 [165.50, 183.47] |
| yes | no | yes | 1040.80 | 1228.25 | 197.42 [70.17, 314.23] | 177.52 [165.51, 196.91] |
| yes | yes | no | 2895.24 | 3272.70 | 318.20 [201.63, 463.60] | 176.49 [165.58, 181.47] |
| yes | yes | yes | 2900.76 | 3043.49 | 184.85 [46.82, 270.53] | 177.00 [167.85, 183.68] |

## Real HTTP

| Async | Diagnostic | Logging | Direct median ns | Adapter median ns | Paired delta ns [min, max] | Process delta B/op [min, max] |
|---|---|---|---:|---:|---:|---:|
| no | no | no | 27776.20 | 27965.60 | 207.80 [-2294.30, 1561.10] | 240.91 [239.83, 241.67] |
| no | no | yes | 27817.30 | 28391.50 | 661.00 [-1089.60, 1061.70] | 240.26 [238.82, 241.72] |
| no | yes | no | 32233.20 | 32909.60 | 1129.90 [-376.80, 2795.60] | 399.32 [190.69, 526.60] |
| no | yes | yes | 33408.60 | 33589.00 | -99.70 [-2381.00, 5570.40] | 268.56 [194.73, 474.47] |
| yes | no | no | 29633.50 | 29468.90 | -347.80 [-996.90, 1254.70] | 174.41 [107.94, 275.50] |
| yes | no | yes | 28648.90 | 29797.20 | 815.90 [118.00, 2641.30] | 262.74 [242.18, 476.32] |
| yes | yes | no | 35122.40 | 37545.60 | 1686.20 [831.50, 4236.20] | 424.57 [375.95, 536.16] |
| yes | yes | yes | 37964.40 | 38644.60 | 1378.40 [-2198.00, 7171.20] | 433.25 [410.48, 471.07] |

## Scope creation and disposal

Scope-only median: 50.63 ns / 128 B. Scope plus runner: 194.96 ns / 552 B. Paired time delta: 148.47 ns [140.88, 179.77]; allocation delta: 424 B. This includes the first scoped service resolution, DI cache/disposal management and the runner's diagnostic connection. It is not the size of the EffectRequest object. Both real HTTP arms already resolve a scoped environment, so that common work is present on both sides.

## Interpretation and limits

Normal synchronous reuse consistently adds about 39–49 ns at the paired medians with no measured additional allocation. The adapter checks lifetime, captures the request token, classifies the Exit and maintains its diagnostic connection. Async/diagnostic cases add an await bridge and reporting/lifetime work; their process allocation differences are measured totals, not a fixed object-size claim.

The first revised HTTP case is around 27–30 microseconds instead of the preliminary run's 65–108 microseconds. That supports rejecting the preliminary timing values; it does not prove universal convergence. The final async + diagnostic + enabled-logging case still drifts: direct rises from 37.49 to 45.33 microseconds and adapter from 38.87 to 52.50 microseconds. Thermal, scheduler, GC or other process changes are possible explanations, not measured causes. Longer warmup alone did not make every condition stationary.

HTTP paired median differences range from -0.348 to +1.686 microseconds. Six cases change sign across rounds; two are positive in every round. There is real adapter work, as the reuse/creation comparisons show. These five pairs do not establish equal speed, a universal regression bound, fastest defaults, zero allocation for HTTP, server capacity or long-term safety. The observed normal synchronous HTTP allocation difference is about 240 B/request; async/diagnostic differences are noisier. The old prototype's approximately 780 B process difference is not reused as an adapter constant.

Performance acceptance for this preview is subject to review with these costs and limits; no optional diagnostic bypass was added. GA revalidation, other hardware/providers, saturation and long soak remain outside this run.
