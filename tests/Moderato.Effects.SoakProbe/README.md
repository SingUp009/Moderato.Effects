# Sustained execution probe (#377)

Standalone executable, outside the test solution. No change to runtime behavior.
One operation is in flight; the same effect description stays alive across runs.
Each run gets a fresh explicit environment. Acquired leases are registered for
release before the first asynchronous suspension. Every result value/error and
the acquisition/release balance are checked immediately. Duplicate release fails.

Conditions:

- `gen`: asynchronous Gen / Defer; typed error exactly every thousand operations.
- `resource`: asynchronous Resource.Use with exact result and release checks.
- `provide`: Gen wrapped in 1,024 Provide transformations.
- `bind`: Gen followed by 1,024 AndThen operations.
- `cancel`: cancel after acquisition and asynchronous suspension; every run must
  interrupt and asynchronously release its lease with a noncancelable cleanup token.
- `die`: a fresh exception per operation must emerge as the original primary
  defect with no supplemental failures, after releasing its lease.
- `fiber`: fork an asynchronously failing child, wait for its release without
  joining it, then verify exactly one unobserved typed error per operation through
  the per-execution diagnostics connection. No global handler is modified.
- `host`: two independent Hosts, each with one continuously running service.
  Each iteration acquires an async DI scope, resolves a disposable scoped marker,
  runs an unjoined failing Fiber, and releases the scope. Distinct errors verify
  Host diagnostics isolation. Stop joins all services before asynchronous Host
  disposal. Final scope/Fiber/diagnostic counts must balance; sampled iteration
  scopes must collect. Cancellation may interrupt the final iteration, so complete
  iteration counts and actual child error counts are separate.

Build and check the harness first:

```bash
dotnet build tests/Moderato.Effects.SoakProbe -c Release
bash tests/Moderato.Effects.SoakProbe/verify.sh dotnet tests/Moderato.Effects.SoakProbe/bin/Release/net11.0/Moderato.Effects.SoakProbe.dll
```

Run from an isolated worktree using the SDK required by its global.json:

```bash
bash tests/Moderato.Effects.SoakProbe/run.sh gen 30 /tmp/soak-gen-after
```

The output directory must not exist. It receives build output, the commit/status,
SDK details, source and binary SHA256 hashes, runtime/GC metadata, CSV windows and
the actual exit code. No output pipeline masks application failure. A process
watchdog terminates the run after its duration plus five minutes. Exit 124/137 is
a failure, never a completed soak.

Core observation windows run on the standard thread pool and have a deadline of
the requested window duration plus 30 seconds (31 seconds for warmup). This bounds
a synchronous stall before the first await as well as an asynchronous stall. The
deadline does not cancel the measured effect or its cleanup: exceeding it fails
the process rather than claiming a successful drain. The process watchdog remains
a fallback. Per-window scheduling/deadline overhead is included in the CSV window
measurements; it does not add a library caller cancellation unit per operation.
Use identical harness versions for rate and allocation comparisons.

For a before/after comparison, copy the identical probe directory to a separate
worktree pinned at the comparison commit. Record both manifests, and run them
sequentially. Keep machine load and GC configuration comparable. Defaults are
Workstation, nonconcurrent GC; the executable prints the actual configuration.
Exercise the Host condition under both Workstation and Server GC, keeping their
results separate. The short verification script also requires a Server GC pass
and checks the reported mode (the machine must support Server GC):

```bash
DOTNET_gcServer=1 bash tests/Moderato.Effects.SoakProbe/run.sh host 30 /tmp/soak-host-server
```

Build/initialization and one second of warmup precede the timed run. Counts include
warmup; per-window operation and allocation deltas exclude it.

Each CSV window records elapsed wall time, process-wide allocated bytes and natural
GC counts during work, then forces collection and checks a fixed ring of weak
references to completed environments and leases. The async window must have ended
before collection. The collection check first yields out of the producer's inline
completion stack, then retries for at most five seconds. The self-check exercises
single-operation windows whose final environment and lease are both sampled; a
separate retained-reference control must still fail through that same collection
check. No extra executions replace the sampled objects. Failure to
collect samples fails the run. Forced collections are excluded from the natural
GC columns. Sampling, formatting and runtime activity can contribute to metrics;
this is not a microbenchmark. Window timing includes validation, not the later
forced collections; total elapsed time includes observation pauses.
Both `us_per_op` and the working-set series describe this workload with periodic
forced full GC, not the steady state of a service using only natural collections.
Do not use these rates as production throughput estimates. Weak-reference survival
is an assertion after each completed window, not a constant-valued CSV column.

Post-GC managed heap, GC committed bytes, process private bytes and working set are
different measurements. None isolates the library's retained bytes. Do not divide
live heap by cumulative operations to infer allocations or leak rate. A staircase
alone does not distinguish caches from retention bugs. Interpret trends together
with release counts and sampled weak references; sampling does not prove that all
objects were collected. Thread and handle counts describe this process only.
On Linux, `private` (`Process.PrivateMemorySize64`) is backed by `VmData` in
`/proc/<pid>/status`: a virtual data-segment size, not private resident RAM (see
the [.NET implementation notes](https://source.dot.net/System.Diagnostics.Process/System/Diagnostics/Process.cs.html)).
Read it separately from the process working-set observation in `working_set`.

`host` observes live services in its windows, including in-flight scopes. Its weak
references and exact counters are checked after all services stop and Hosts are
disposed. Collection checks first leave the producer completion stack, whose
locals can otherwise remain genuine GC roots while an inline continuation waits.
The self-check verifies this boundary and that a deliberately retained sample is
still detected; live objects are not classified as leaks. Each Host has its own 30-second
stop-and-join budget. Once running, SIGINT/SIGTERM follow Generic Host's
`ApplicationStopping` notification through the same stop, join, disposal and final
invariant checks. Exit 130 and `STOPPED` distinguish this verified early shutdown
from a completed timed run.

For the Host condition, startup precedes the measured windows; counts include
startup, with no separate warmup. It reports its own CSV schema. Forced GC pauses
the workload, so this condition does not establish uninterrupted production
throughput.

On POSIX, Core-only conditions also handle SIGINT/SIGTERM: they stop between
public runs, after the current run and its cleanup finish. They keep each scenario's
existing execution token, so an OS stop does not add per-operation caller
cancellation state or change outcome classification. The final `STOPPED` counts
and exit 130 identify an incomplete timed run. Core-only Windows signal handling
is not covered.

The short verification script invokes the C# `verify-signals` mode. It sends
both signals to all eight conditions in isolated running processes after their
first observation window, checking cleanup before exit 130. Its output controls
also reject incorrect exit codes, counts and completion markers. A stuck operation is still a watchdog failure, and SIGKILL cannot
await cleanup. Startup-signal handling is not established by these checks.

These finite, sequential conditions do not establish stability under arbitrary
business load. `host` covers two concurrent roots and bounded children; it does not
cover arbitrary fan-out, Host restart loops, or stopping past the shutdown budget.
The existing deep-composition
and Hosting regression tests remain required independently of this probe.
