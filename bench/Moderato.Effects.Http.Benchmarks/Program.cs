using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Moderato.Effects;
using Moderato.Effects.Http;
using Moderato.Results;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.ClearProviders();
var provider = new CountingProvider();
builder.Logging.AddProvider(provider);
builder.Services.AddEffectsHttp();
builder.Services.AddScoped<BenchEnvironment>();
builder.WebHost.UseUrls("http://127.0.0.1:0");
await using var app = builder.Build();
var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Moderato.Effects.Http");
var write = LoggerMessage.Define<string?>(LogLevel.Warning, new EventId(1002, "UnjoinedError"), "Unjoined effect error of type {ErrorType}");
var options = new EffectRunOptions
{
    UnobservedFailureHandler = failure =>
    {
        if (failure.Value is Failed failed && logger.IsEnabled(LogLevel.Warning))
            write(logger, failed.Error?.GetType().Name, null);
    },
};
var environment = new BenchEnvironment();
var effects = new Dictionary<(bool Async, bool Diagnostic), Effect<int, Never, BenchEnvironment>>();
foreach (var asynchronous in new[] { false, true })
{
    foreach (var diagnostic in new[] { false, true })
    {
        Effect<int, Never, BenchEnvironment> effect = diagnostic ? UnjoinedError() : Effect<BenchEnvironment>.Succeed(42);
        if (asynchronous)
        {
            var next = effect;
            effect = Effect<BenchEnvironment>.Async<Unit>(async _ => { await Task.Yield(); return Unit.Value; }).AndThen(_ => next);
        }
        effects[(asynchronous, diagnostic)] = effect;
    }
}

app.MapGet("/direct/{asynchronous:bool}/{diagnostic:bool}", async (bool asynchronous, bool diagnostic, HttpContext context, BenchEnvironment env) =>
{
    var exit = await effects[(asynchronous, diagnostic)].RunExitAsync(env, context.RequestAborted, options);
    return TypedResults.Ok(Value(exit));
});
app.MapGet("/runner/{asynchronous:bool}/{diagnostic:bool}", async (bool asynchronous, bool diagnostic, EffectRequest request, BenchEnvironment env) =>
{
    var result = await request.RunAsync(effects[(asynchronous, diagnostic)], env);
    return TypedResults.Ok(result is Ok<int>(var value) ? value : throw new InvalidOperationException("Unexpected result."));
});
Console.WriteLine($"Runtime={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}; StopwatchFrequency={Stopwatch.Frequency}; rounds=5; in-process client; process allocations include framework/client/background work");
Console.WriteLine("section,async,diagnostic,logging,round,arm,count,ns/op,B/op");
var accessor = app.Services.GetRequiredService<IHttpContextAccessor>();
accessor.HttpContext = new DefaultHttpContext();
using (var scope = app.Services.CreateScope())
{
    var request = scope.ServiceProvider.GetRequiredService<EffectRequest>();
    foreach (var enabled in new[] { false, true })
    {
        foreach (var pair in effects)
        {
            provider.Enabled = enabled;
            Func<ValueTask<int>> direct = async () => Value(await pair.Value.RunExitAsync(environment, CancellationToken.None, options));
            Func<ValueTask<int>> adapter = async () =>
            {
                var result = await request.RunAsync(pair.Value, environment);
                return result is Ok<int>(var value) ? value : throw new InvalidOperationException("Unexpected result.");
            };
            await WarmFor(direct);
            await WarmFor(adapter);
            for (var round = 0; round < 5; round++)
            {
                foreach (var arm in round % 2 == 0 ? new[] { "direct", "adapter" } : new[] { "adapter", "direct" })
                    await Measure("reuse", pair.Key.Async, pair.Key.Diagnostic, enabled, round, arm, arm == "direct" ? direct : adapter, pair.Key.Async || pair.Key.Diagnostic ? 10000 : 200000);
            }
        }
    }
}
accessor.HttpContext = null;
Func<ValueTask<int>> CreateScope(bool resolve) => () =>
{
    using var scope = app.Services.CreateScope();
    if (resolve) _ = scope.ServiceProvider.GetRequiredService<EffectRequest>();
    return ValueTask.FromResult(42);
};
var scopeOnly = CreateScope(false);
var scopeRunner = CreateScope(true);
await WarmFor(scopeOnly);
await WarmFor(scopeRunner);
for (var round = 0; round < 5; round++)
{
    foreach (var arm in round % 2 == 0 ? new[] { "scope", "scope+runner" } : new[] { "scope+runner", "scope" })
        await Measure("scope-create-dispose", false, false, false, round, arm, arm == "scope" ? scopeOnly : scopeRunner, 10000);
}
await app.StartAsync();
var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
using var client = new HttpClient { BaseAddress = new Uri(address) };
foreach (var enabled in new[] { false, true })
{
    foreach (var pair in effects)
    {
        provider.Enabled = enabled;
        Func<ValueTask<int>> Http(string arm) => async () =>
        {
            var response = await client.GetStringAsync($"/{(arm == "direct" ? "direct" : "runner")}/{pair.Key.Async}/{pair.Key.Diagnostic}");
            if (response != "42") throw new InvalidOperationException($"Unexpected HTTP response: {response}");
            return 42;
        };
        var direct = Http("direct");
        var adapter = Http("adapter");
        await Warm(direct, 40000);
        await Warm(adapter, 40000);
        await WarmFor(direct);
        await WarmFor(adapter);
        for (var round = 0; round < 5; round++)
        {
            foreach (var arm in round % 2 == 0 ? new[] { "direct", "adapter" } : new[] { "adapter", "direct" })
                await Measure("http", pair.Key.Async, pair.Key.Diagnostic, enabled, round, arm, arm == "direct" ? direct : adapter, 1000);
        }
    }
}

await app.StopAsync();
Console.WriteLine($"Observed diagnostic log calls={provider.Calls}");

static int Value(Exit<int, Never> exit) => exit is Ok<int>(var value) ? value : throw new InvalidOperationException("Unexpected exit.");

static async Task WarmFor(Func<ValueTask<int>> action)
{
    var started = Stopwatch.GetTimestamp();
    do { await Warm(action, 1000); }
    while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(1));
}

static async Task Warm(Func<ValueTask<int>> action, int count)
{
    for (var i = 0; i < count; i++)
        if (await Invoke(action) != 42) throw new InvalidOperationException("Invalid benchmark result.");
}

// A shared optimized delegate call can specialize one arm through dynamic PGO.
[MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
static ValueTask<int> Invoke(Func<ValueTask<int>> action) => action();

static async Task Measure(string section, bool asynchronous, bool diagnostic, bool logging, int round, string arm, Func<ValueTask<int>> action, int count)
{
    var allocated = GC.GetTotalAllocatedBytes(true);
    var started = Stopwatch.GetTimestamp();
    await Warm(action, count);
    var elapsed = Stopwatch.GetElapsedTime(started).TotalNanoseconds / count;
    var bytes = (GC.GetTotalAllocatedBytes(true) - allocated) / (double)count;
    Console.WriteLine(FormattableString.Invariant($"{section},{asynchronous},{diagnostic},{logging},{round},{arm},{count},{elapsed:F2},{bytes:F2}"));
}

static Effect<int, Never, BenchEnvironment> UnjoinedError() => Scope<Never, BenchEnvironment>.Gen(async scope =>
{
    var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    await scope.Fork(Effect<BenchEnvironment>.Fail("diagnostic").TapErr(_ => ended.TrySetResult()));
    await ended.Task;
    return 42;
});

internal sealed class CountingProvider : ILoggerProvider
{
    internal bool Enabled { get; set; }
    private int calls;
    internal int Calls => Volatile.Read(ref calls);
    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
    public void Dispose() { }

    private sealed class Logger(CountingProvider owner, string category) : ILogger
    {
        public bool IsEnabled(LogLevel logLevel) => owner.Enabled && category == "Moderato.Effects.Http";
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Interlocked.Increment(ref owner.calls);
    }
}

public sealed class BenchEnvironment;
