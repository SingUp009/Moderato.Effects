using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moderato.Effects;
using Moderato.Effects.Hosting;
using Moderato.Results;

internal static class HostProbe
{
    internal static async Task<int> Run(TimeSpan duration, TimeSpan window)
    {
        var states = new[] { new State("host-a"), new State("host-b") };
        var stopped = await RunHosts(states, duration, window);
        foreach (var state in states)
        {
            if (state.ScopesCreated != state.ScopesDisposed || state.DiagnosticsDisposed != 1 ||
                state.Completed == 0 || state.FibersStarted != state.FibersEnded || state.Diagnostics != state.FibersFailed || state.InvalidDiagnostics != 0)
            {
                throw new InvalidOperationException($"Host ownership mismatch: {state}");
            }
            var collected = await WaitForCollection(state);
            if (!collected) throw new InvalidOperationException("Stopped Host retained a sampled iteration scope.");
            Console.WriteLine($"PASS {state}");
        }
        if (stopped) Console.WriteLine("STOPPED Host lifetime request; timed run incomplete");
        return stopped ? 130 : 0;
    }

    private static async Task<bool> WaitForCollection(State state)
    {
        // Leave the producer's inline completion stack before waiting for its locals to collect.
        await Task.Yield();
        return SpinWait.SpinUntil(() =>
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            lock (state.References) return state.References.All(static reference => reference is null || !reference.IsAlive);
        }, TimeSpan.FromSeconds(5));
    }

    internal static async Task CheckCollectionBoundary()
    {
        // Given
        var state = new State("completion-control");
        var completed = new TaskCompletionSource();
        var producerThreadId = new StrongBox<int>();
        var collection = CollectAfterCompletion(completed.Task, state, producerThreadId);
        // When
        await Task.Run(() => CompleteFromProducer(completed, state, producerThreadId));
        var collected = await collection;
        // Then
        if (!collected) throw new InvalidOperationException("Collection ran on the producer completion stack.");

        // Given
        var retained = new object();
        state.References[0] = new WeakReference(retained);
        // When
        collected = await WaitForCollection(state);
        GC.KeepAlive(retained);
        // Then
        if (collected) throw new InvalidOperationException("A retained Host sample was reported as collected.");
        Console.WriteLine("PASS Host collection controls: inline completion unwinds; retained samples remain detected");
    }

    private static async Task<bool> CollectAfterCompletion(Task completed, State state, StrongBox<int> producerThreadId)
    {
        await completed.ConfigureAwait(false);
        if (Volatile.Read(ref producerThreadId.Value) != System.Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("The completion control did not run inline.");
        return await WaitForCollection(state);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CompleteFromProducer(TaskCompletionSource completed, State state, StrongBox<int> producerThreadId)
    {
        var sample = new object();
        state.References[0] = new WeakReference(sample);
        Volatile.Write(ref producerThreadId.Value, System.Environment.CurrentManagedThreadId);
        completed.SetResult();
        GC.KeepAlive(sample);
        Volatile.Write(ref producerThreadId.Value, 0);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<bool> RunHosts(State[] states, TimeSpan duration, TimeSpan window)
    {
        var hosts = states.Select(Build).ToArray();
        try
        {
            foreach (var host in hosts) await host.StartAsync();
            var tasks = hosts.SelectMany(host => host.Services.GetServices<IHostedService>())
                .Cast<BackgroundService>().Select(service => service.ExecuteTask!).ToArray();
            var firstCompleted = Task.WhenAny(tasks);
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(hosts.Select(host =>
                host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping).ToArray());
            var shutdown = Task.Delay(Timeout.InfiniteTimeSpan, stopping.Token);
            var stopped = false;
            using var process = Process.GetCurrentProcess();
            var clock = Stopwatch.StartNew();
            long previous = 0;
            var lastCounts = new long[states.Length];
            var lastProgress = new TimeSpan[states.Length];
            Console.WriteLine("elapsed_s,operations,operations_window,allocated_window,gen0,gen1,gen2,post_gc_heap,gc_committed,private,working_set,threads,handles,scope_live,diagnostics");
            while (clock.Elapsed < duration)
            {
                var remaining = duration - clock.Elapsed;
                if (remaining <= TimeSpan.Zero) break;
                var allocated = GC.GetTotalAllocatedBytes(true);
                var collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
                var delay = Task.Delay(remaining < window ? remaining : window);
                var completed = await Task.WhenAny(firstCompleted, shutdown, delay);
                if (completed == firstCompleted)
                {
                    await await firstCompleted;
                    throw new InvalidOperationException("Hosted services ended before stop was requested.");
                }
                if (completed == shutdown)
                {
                    stopped = true;
                    break;
                }
                var allocationDelta = GC.GetTotalAllocatedBytes(true) - allocated;
                var counts = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - collections[i]).ToArray();
                var operations = states.Sum(state => Volatile.Read(ref state.Completed));
                for (var i = 0; i < states.Length; i++)
                {
                    var count = Volatile.Read(ref states[i].Completed);
                    if (count > lastCounts[i]) lastProgress[i] = clock.Elapsed;
                    if (clock.Elapsed - lastProgress[i] > TimeSpan.FromSeconds(30) || Volatile.Read(ref states[i].InvalidDiagnostics) != 0)
                        throw new InvalidOperationException($"Hosted execution stalled or received invalid diagnostics: {states[i].Name}");
                    lastCounts[i] = count;
                }
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                process.Refresh();
                Console.WriteLine(FormattableString.Invariant($"{clock.Elapsed.TotalSeconds:F3},{operations},{operations - previous},{allocationDelta},{counts[0]},{counts[1]},{counts[2]},{GC.GetTotalMemory(false)},{GC.GetGCMemoryInfo().TotalCommittedBytes},{process.PrivateMemorySize64},{process.WorkingSet64},{process.Threads.Count},{process.HandleCount},{states.Sum(state => Volatile.Read(ref state.ScopesCreated) - Volatile.Read(ref state.ScopesDisposed))},{states.Sum(state => Volatile.Read(ref state.Diagnostics))}"));
                previous = operations;
            }
            foreach (var host in hosts)
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await host.StopAsync(stop.Token);
                await Task.WhenAll(host.Services.GetServices<IHostedService>().Cast<BackgroundService>()
                    .Select(service => service.ExecuteTask!)).WaitAsync(stop.Token);
            }
            Console.WriteLine($"Host run actualSeconds={clock.Elapsed.TotalSeconds:F3}");
            return stopped;
        }
        finally
        {
            foreach (var host in hosts) await ((IAsyncDisposable)host).DisposeAsync();
        }
    }

    private static IHost Build(State state)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(state);
        builder.Services.AddScoped<Marker>();
        builder.Services.AddEffectsHosting(options => options.Diagnostics = _ => new Diagnostics(state));
        var resource = Resource.Make(Effect<HostEnvironment>.Access(environment => environment.Services.CreateAsyncScope()),
            static (scope, _) => Effect<HostEnvironment>.Async<Unit>(async _ =>
            {
                await scope.DisposeAsync();
                return Unit.Value;
            }));
        var iteration = resource.Use(scope => Scope<Never, HostEnvironment>.Gen(async owner =>
        {
            var marker = scope.ServiceProvider.GetRequiredService<Marker>();
            if (marker.Disposed) throw new InvalidOperationException("Iteration scope was already disposed.");
            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await owner.Fork(Scope<string, HostEnvironment>.Gen<int>(async child =>
            {
                Interlocked.Increment(ref state.FibersStarted);
                child.Defer(_ => Effect<HostEnvironment>.Sync(() =>
                {
                    Interlocked.Increment(ref state.FibersEnded);
                    ended.SetResult();
                    return Unit.Value;
                }));
                await Task.Yield();
                Interlocked.Increment(ref state.FibersFailed);
                throw child.Fail(state.Name);
            }));
            await ended.Task.WaitAsync(owner.CancellationToken);
            if (marker.Disposed) throw new InvalidOperationException("Iteration scope ended before its work.");
            return Unit.Value;
        }));
        Effect<Unit, string, HostEnvironment> root = Scope<string, HostEnvironment>.Gen(async scope =>
        {
            while (!scope.CancellationToken.IsCancellationRequested)
            {
                // Finish each acquired scope before observing the next stop boundary.
                await scope.Run(iteration);
                Interlocked.Increment(ref state.Completed);
            }
            return Unit.Value;
        });
        builder.Services.AddEffectHostedService(root,
            services => new HostEnvironment(services, services.GetRequiredService<Marker>()),
            static (error, _, _) => throw new InvalidOperationException($"Unexpected root error: {error}"));
        return builder.Build();
    }

    private sealed record HostEnvironment(IServiceProvider Services, Marker RootMarker);

    private sealed class Marker : IDisposable
    {
        private readonly State state;
        internal bool Disposed { get; private set; }

        public Marker(State state)
        {
            this.state = state;
            var number = Interlocked.Increment(ref state.ScopesCreated);
            if (number % 2500 == 2)
                lock (state.References) state.References[(number / 2500) % state.References.Length] = new WeakReference(this);
        }

        public void Dispose()
        {
            if (Disposed) throw new InvalidOperationException("Scope marker disposed twice.");
            Disposed = true;
            Interlocked.Increment(ref state.ScopesDisposed);
        }
    }

    private sealed class Diagnostics(State state) : IEffectDiagnostics, IDisposable
    {
        private int disposed;
        public void Report(ExitCase failure)
        {
            if (Volatile.Read(ref disposed) != 0 || failure.Value is not Failed failed || !Equals(failed.Error, state.Name))
                Interlocked.Increment(ref state.InvalidDiagnostics);
            Interlocked.Increment(ref state.Diagnostics);
        }
        public void Dispose()
        {
            Interlocked.Exchange(ref disposed, 1);
            Interlocked.Increment(ref state.DiagnosticsDisposed);
        }
    }

    private sealed class State(string name)
    {
        internal string Name { get; } = name;
        internal WeakReference?[] References { get; } = new WeakReference?[16];
        internal long Completed;
        internal long FibersStarted;
        internal long FibersEnded;
        internal long FibersFailed;
        internal long ScopesCreated;
        internal long ScopesDisposed;
        internal long Diagnostics;
        internal long InvalidDiagnostics;
        internal int DiagnosticsDisposed;
        public override string ToString() => $"{Name} completed={Completed} fibers={FibersStarted}/{FibersEnded} scopes={ScopesCreated}/{ScopesDisposed} diagnostics={Diagnostics}/{FibersFailed} invalid={InvalidDiagnostics} diagnosticDisposals={DiagnosticsDisposed}";
    }
}
