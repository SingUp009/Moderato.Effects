using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Moderato.Effects;
using Moderato.Results;

if (args is ["verify-signals", .. var command]) return await SignalVerification.Run(command);

if (args is ["self-check"])
{
    await Probe.SelfCheck();
    await HostProbe.CheckCollectionBoundary();
    SignalVerification.CheckOutputControls();
    return 0;
}
if (args.Length != 3 || args[0] is not ("gen" or "resource" or "provide" or "bind" or "cancel" or "fiber" or "die" or "host") ||
    !double.TryParse(args[1], CultureInfo.InvariantCulture, out var minutes) || !double.IsFinite(minutes) || minutes <= 0 || minutes > 1440 ||
    !double.TryParse(args[2], CultureInfo.InvariantCulture, out var seconds) || !double.IsFinite(seconds) || seconds <= 0 || seconds > 3600)
{
    Console.Error.WriteLine("Usage: SoakProbe gen|resource|provide|bind|cancel|fiber|die|host <minutes: (0,1440]> <window seconds: (0,3600]>; or self-check; or verify-signals <probe executable command ...>");
    return 2;
}

Console.WriteLine($"runtime={RuntimeInformation.FrameworkDescription} os={RuntimeInformation.OSDescription} arch={RuntimeInformation.ProcessArchitecture} cpu={System.Environment.ProcessorCount}");
Console.WriteLine($"gcServer={System.Runtime.GCSettings.IsServerGC} gcLatency={System.Runtime.GCSettings.LatencyMode} scenario={args[0]} minutes={minutes:R} windowSeconds={seconds:R}");
if (args[0] == "host") return await HostProbe.Run(TimeSpan.FromMinutes(minutes), TimeSpan.FromSeconds(seconds));
var stopRequested = 0;
void RequestStop(PosixSignalContext signal)
{
    signal.Cancel = true;
    Interlocked.Exchange(ref stopRequested, 1);
}
Func<bool> shouldStop = () => Volatile.Read(ref stopRequested) != 0;
using var interrupt = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGINT, RequestStop);
using var terminate = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, RequestStop);
var probe = new Probe(args[0]);
using var process = Process.GetCurrentProcess();
Console.WriteLine("WARMUP 1 second (counts remain cumulative)");
await probe.Window(TimeSpan.FromSeconds(1), shouldStop);
await probe.CheckCollectedAsync();
var clock = Stopwatch.StartNew();
var duration = TimeSpan.FromMinutes(minutes);
Console.WriteLine("elapsed_s,ops,ops_window,us_per_op,allocated_window,gen0,gen1,gen2,post_gc_heap,gc_committed,private,working_set,threads,handles,created,released,expected_err,interrupted,defects,diagnostics");
while (!shouldStop() && clock.Elapsed < duration)
{
    var remaining = duration - clock.Elapsed;
    if (remaining <= TimeSpan.Zero) break;
    var allocated = GC.GetTotalAllocatedBytes(precise: true);
    var collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
    var started = Stopwatch.GetTimestamp();
    var previous = probe.Completed;
    await probe.Window(remaining < TimeSpan.FromSeconds(seconds) ? remaining : TimeSpan.FromSeconds(seconds), shouldStop);
    if (probe.Completed == previous) break;
    var elapsed = Stopwatch.GetElapsedTime(started);
    var allocatedWindow = GC.GetTotalAllocatedBytes(precise: true) - allocated;
    var gcCounts = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - collections[i]).ToArray();
    await probe.CheckCollectedAsync();
    process.Refresh();
    Console.WriteLine(FormattableString.Invariant($"{clock.Elapsed.TotalSeconds:F3},{probe.Completed},{probe.Completed - previous},{elapsed.TotalMicroseconds / (probe.Completed - previous):F3},{allocatedWindow},{gcCounts[0]},{gcCounts[1]},{gcCounts[2]},{GC.GetTotalMemory(false)},{GC.GetGCMemoryInfo().TotalCommittedBytes},{process.PrivateMemorySize64},{process.WorkingSet64},{process.Threads.Count},{process.HandleCount},{probe.Created},{probe.Released},{probe.Errors},{probe.Interruptions},{probe.Defects},{probe.Diagnostics}"));
}
GC.KeepAlive(probe);
var stopped = shouldStop();
Console.WriteLine($"{(stopped ? "STOPPED" : "PASS")} actualSeconds={clock.Elapsed.TotalSeconds:F3} operations={probe.Completed} acquired={probe.Created} released={probe.Released} errors={probe.Errors}");
return stopped ? 130 : 0;

internal sealed class Probe
{
    private readonly Effect<int, string, RunEnvironment> effect;
    private readonly EffectRunOptions options;
    private readonly string scenario;
    private readonly WeakReference?[] references = new WeakReference?[32];
    private int referenceAt;
    internal long Completed { get; private set; }
    internal long Created;
    internal long Released;
    internal long Errors { get; private set; }
    internal long Interruptions { get; private set; }
    internal long Defects { get; private set; }
    internal long Diagnostics;
    private long unexpectedDiagnostics;

    internal Probe(string scenario)
    {
        this.scenario = scenario;
        options = new EffectRunOptions
        {
            UnobservedFailureHandler = failure =>
            {
                if (failure.Value is Failed { Error: "child" }) Interlocked.Increment(ref Diagnostics);
                else Interlocked.Increment(ref unexpectedDiagnostics);
            }
        };
        effect = scenario == "resource"
            ? Resource.Make(Effect<RunEnvironment>.Access(environment => new Lease(environment.Probe)),
                static (lease, _) => Effect<RunEnvironment>.Sync(() => { lease.Dispose(); return Unit.Value; }))
                .Use(_ => Effect<RunEnvironment>.AccessAsync<int>(static async (environment, token) =>
                {
                    await Task.Yield();
                    token.ThrowIfCancellationRequested();
                    return environment.Id;
                }))
            : Scope<string, RunEnvironment>.Gen<int>(static async scope =>
            {
                var lease = new Lease(scope.Env.Probe);
                if (scope.Env.Probe.scenario == "cancel")
                {
                    scope.Defer(_ => Effect<RunEnvironment>.Async<Unit>(async token =>
                    {
                        if (token.CanBeCanceled) throw new InvalidOperationException("Cleanup received a cancelable token.");
                        await Task.Yield();
                        lease.Dispose();
                        return Unit.Value;
                    }));
                }
                else
                {
                    scope.Defer(_ => Effect<RunEnvironment>.Sync(() => { lease.Dispose(); return Unit.Value; }));
                }
                await Task.Yield();
                if (scope.Env.Probe.scenario == "cancel") scope.Env.Cancellation!.Cancel();
                scope.CancellationToken.ThrowIfCancellationRequested();
                if (scope.Env.Defect is { } defect) throw defect;
                if (scope.Env.Probe.scenario == "fiber")
                {
                    var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    await scope.Fork(Scope<string, RunEnvironment>.Gen<int>(async child =>
                    {
                        child.Defer(_ => Effect<RunEnvironment>.Sync(() => { ended.SetResult(); return Unit.Value; }));
                        await Task.Yield();
                        throw child.Fail("child");
                    }));
                    await ended.Task;
                    return scope.Env.Id;
                }
                if (scope.Env.Id == 999) throw scope.Fail("expected");
                return scope.Env.Id;
            });
        if (scenario == "provide")
            for (var i = 0; i < 1024; i++) effect = effect.Provide<RunEnvironment>(static environment => environment);
        if (scenario == "bind")
            for (var i = 0; i < 1024; i++) effect = effect.AndThen(static value => Effect<RunEnvironment>.Succeed(value));
        ResourceScenario = scenario == "resource";
    }

    private bool ResourceScenario { get; }

    internal Task Window(TimeSpan duration, Func<bool>? shouldStop = null)
        => BoundedWindow(() => RunWindow(duration, shouldStop), duration + TimeSpan.FromSeconds(30));

    private static Task BoundedWindow(Func<Task> work, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(work);
        return Task.Run(work).WaitAsync(timeout);
    }

    // Finish the async state machine before collecting samples; live locals are not leaks.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task RunWindow(TimeSpan duration, Func<bool>? shouldStop)
    {
        // Drain the current run rather than adding cancelable-caller overhead to each measured operation.
        if (shouldStop?.Invoke() == true) return;
        var clock = Stopwatch.StartNew();
        do
        {
            var id = (int)(Completed % 1000);
            using var cancellation = scenario == "cancel" ? new CancellationTokenSource() : null;
            var environment = new RunEnvironment(this, id, cancellation, scenario == "die" ? new IOException("expected") : null);
            if (Completed % 2500 == 0) Track(environment);
            var exit = await effect.RunExitAsync(environment, cancellation?.Token ?? CancellationToken.None, options);
            if (scenario == "cancel")
            {
                if (exit.Value is not Interrupted) throw new InvalidOperationException($"Expected interruption: {exit}.");
                Interruptions++;
            }
            else if (scenario == "die")
            {
                if (exit.Value is not Die die || !ReferenceEquals(die.Exception, environment.Defect) || die.Suppressed.Count != 0 || die.OmittedSuppressed != 0)
                    throw new InvalidOperationException($"Expected original defect without supplemental failures: {exit}.");
                Defects++;
            }
            else
            {
                Validate(exit, id, !ResourceScenario && scenario != "fiber" && id == 999);
            }
            if (exit.Value is Err<string>) Errors++;
            Completed++;
            if (Created != Released) throw new InvalidOperationException($"Cleanup mismatch: {Created} acquired, {Released} released.");
            if (Diagnostics != (scenario == "fiber" ? Completed : 0) || unexpectedDiagnostics != 0)
                throw new InvalidOperationException("Unexpected diagnostic count or content.");
        } while (clock.Elapsed < duration && shouldStop?.Invoke() != true);
    }

    internal void Track(object value) => references[referenceAt++ % references.Length] = new WeakReference(value);

    internal async Task CheckCollectedAsync()
    {
        // Leave the producer's inline completion stack before waiting for its locals to collect.
        await Task.Yield();
        var collected = SpinWait.SpinUntil(CollectSamples, TimeSpan.FromSeconds(5));
        if (!collected) throw new InvalidOperationException("Completed environment or lease sample remains alive.");
    }

    private bool CollectSamples()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return references.All(static reference => reference is null || !reference.IsAlive);
    }

    internal static void Validate(Exit<int, string> exit, int id, bool error)
    {
        if (!error && exit.Value is Ok<int> ok && ok.Value == id) return;
        if (error && exit.Value is Err<string> err && err.Error == "expected") return;
        throw new InvalidOperationException($"Unexpected outcome for id {id}: {exit}.");
    }

    internal static async Task SelfCheck()
    {
        // Given
        Exit<int, string> ok = new Ok<int>(7);
        Exit<int, string> error = new Err<string>("expected");
        // When
        Validate(ok, 7, false);
        Validate(error, 999, true);
        // Then
        Reject(() => Validate(ok, 8, false));
        Reject(() => Validate(ok, 7, true));
        Reject(() => Validate(error, 999, false));
        Reject(() => Validate(new Err<string>("wrong"), 999, true));
        Reject(() => Validate(new Die(new InvalidOperationException("defect"), []), 7, false));
        Reject(() => Validate(new Interrupted(), 7, false));
        var probe = new Probe("gen");
        var lease = new Lease(probe);
        lease.Dispose();
        Reject(lease.Dispose);
        if (probe.Created != 1 || probe.Released != 1) throw new InvalidOperationException("Lease counter self-check failed.");
        // When
        var retainedDetected = false;
        try { await probe.CheckCollectedAsync(); }
        catch (InvalidOperationException) { retainedDetected = true; }
        GC.KeepAlive(lease);
        // Then
        if (!retainedDetected) throw new InvalidOperationException("A retained sample was reported as collected.");
        Console.WriteLine("PASS outcome and cleanup self-check (including seven negative controls)");

        // Given
        for (var i = 0; i < 8; i++)
        {
            var completed = new Probe("gen");
            // When
            await completed.Window(TimeSpan.Zero);
            await completed.CheckCollectedAsync();
            // Then
            if (completed.Completed != 1 || completed.Created != completed.Released)
                throw new InvalidOperationException("The completed-window collection control did not run and release once.");
        }
        Console.WriteLine("PASS weak-reference controls: completed windows collect; retained samples do not");

        // Given
        using var resume = new ManualResetEventSlim();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task StalledWindow()
        {
            resume.Wait();
            ended.SetResult();
            return Task.CompletedTask;
        }
        // When
        var bounded = BoundedWindow(StalledWindow, TimeSpan.FromMilliseconds(100));
        var timedOut = false;
        try { await bounded; }
        catch (TimeoutException) { timedOut = true; }
        finally { resume.Set(); }
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Then
        if (!timedOut) throw new InvalidOperationException("A stalled window did not fail at its deadline.");
        Console.WriteLine("PASS window deadline: synchronous stall detected and control worker released");
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Invalid outcome was accepted.");
    }
}

internal sealed record RunEnvironment(Probe Probe, int Id, CancellationTokenSource? Cancellation, Exception? Defect);

internal sealed class Lease : IDisposable
{
    private readonly Probe probe;
    private bool disposed;

    internal Lease(Probe probe)
    {
        this.probe = probe;
        probe.Created++;
        if (probe.Created % 2500 == 1) probe.Track(this);
    }

    public void Dispose()
    {
        if (disposed) throw new InvalidOperationException("Lease disposed twice.");
        disposed = true;
        probe.Released++;
    }
}
