using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Moderato.Results;

namespace Moderato.Effects.Http.Prototype;

internal static class PrototypeChecks
{
    internal static async Task Run(WebApplication app, string address)
    {
        using var client = new HttpClient { BaseAddress = new Uri(address) };

        // Given
        var command = new CreateOrderRequest("sample", 2);
        // When
        using var created = await client.PostAsJsonAsync("/orders", command);
        using var duplicate = await client.PostAsJsonAsync("/orders", command);
        using var invalid = await client.PostAsJsonAsync("/orders", command with { Quantity = 0 });
        // Then
        Check(created.StatusCode == HttpStatusCode.Created, "201 created");
        Check(duplicate.StatusCode == HttpStatusCode.Conflict, "409 duplicate");
        Check(invalid.StatusCode == HttpStatusCode.BadRequest, "400 invalid");

        // Given
        var observed = app.Services.GetRequiredService<ObservedFailures>();
        // When
        using var defect = await client.GetAsync("/prototype/die");
        using var interrupted = await client.GetAsync("/prototype/interrupted");
        using var canceledDefect = await client.GetAsync("/prototype/cancel-die/oce");
        using var canceledIo = await client.GetAsync("/prototype/cancel-die/io");
        // Then
        Check(defect.StatusCode == HttpStatusCode.InternalServerError, "Die reaches exception handler");
        Check(interrupted.StatusCode == HttpStatusCode.InternalServerError, "independent interruption reaches exception handler");
        Check(observed.Errors.Any(error => error is EffectHttpException { InnerException: OperationCanceledException }), "Die(OCE) preserved");
        Check(observed.Errors.Any(error => error is EffectHttpInterruptedException), "Interrupted classification preserved");
        Check(canceledDefect.StatusCode == HttpStatusCode.InternalServerError, "Die(OCE) reaches handler with request token cancelled");
        Check(canceledIo.StatusCode == HttpStatusCode.InternalServerError, "Die(IOException) reaches handler with request token cancelled");
        Check(!(await defect.Content.ReadAsStringAsync()).Contains("unexpected OCE", StringComparison.Ordinal), "no exception detail in response");

        // Given
        var output = Path.Combine(Environment.CurrentDirectory, "artifacts", "http-prototype");
        Directory.CreateDirectory(output);
        // When
        var document = await client.GetStringAsync("/openapi/v1.json");
        await File.WriteAllTextAsync(Path.Combine(output, "openapi.json"), document);
        // Then
        using var json = JsonDocument.Parse(document);
        var operation = json.RootElement.GetProperty("paths").GetProperty("/orders").GetProperty("post");
        var responses = operation.GetProperty("responses");
        foreach (var (code, schema) in new[] { ("201", "Order"), ("400", "ValidationError"), ("409", "DuplicateOrder") })
        {
            var body = responses.GetProperty(code).GetProperty("content").GetProperty("application/json");
            Check(body.GetProperty("schema").GetProperty("$ref").GetString() == $"#/components/schemas/{schema}", $"OpenAPI {code} → {schema}");
        }
        Check(responses.TryGetProperty("500", out _), "explicit 500 metadata");
        Check(!operation.TryGetProperty("parameters", out var parameters) || parameters.GetArrayLength() == 0, "services are not HTTP parameters");

        await CheckCancellationAndCleanup(app);
        await CheckActiveCancellation(app);
        await CheckFailureMetadata(app);
        await CheckLateDiagnostics(app);
        await CheckUnobservedError(app);
        MeasureEntry(app, output);
        Console.WriteLine($"PASS — prototype checks; OpenAPI saved to {Path.Combine(output, "openapi.json")}");
    }

    private static async Task CheckFailureMetadata(WebApplication app)
    {
        // Given
        var accessor = app.Services.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext();
        await using var services = app.Services.CreateAsyncScope();
        var request = services.ServiceProvider.GetRequiredService<EffectRequest>();
        var env = services.ServiceProvider.GetRequiredService<AppEnv>();
        var body = new InvalidOperationException("body");
        var cleanup = new IOException("cleanup");
        var resource = Resource.Make(Effect<AppEnv>.Succeed(Unit.Value), (_, _) =>
            Effect<AppEnv>.Sync<Unit>(() => throw cleanup));
        try
        {
            // When
            await request.RunAsync(resource.Use(_ => Effect<AppEnv>.Sync<int>(() => throw body)), env);
            throw new InvalidOperationException("Expected defect.");
        }
        catch (EffectHttpException error)
        {
            // Then
            Check(ReferenceEquals(error.InnerException, body), "body failure remains primary");
            Check(error.Failure.Suppressed is [{ Case.Value: Die additional, Relation: Relation.Sequential }]
                && ReferenceEquals(additional.Exception, cleanup), "suppressed cleanup and relation retained");
            Check(error.Failure.OmittedSuppressed == 0, "omitted count retained");
        }
        finally { accessor.HttpContext = null; }
    }

    private static async Task CheckUnobservedError(WebApplication app)
    {
        // Given
        var accessor = app.Services.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext();
        await using var services = app.Services.CreateAsyncScope();
        var request = services.ServiceProvider.GetRequiredService<EffectRequest>();
        var env = services.ServiceProvider.GetRequiredService<AppEnv>();
        var childEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var effect = Scope<Never, AppEnv>.Gen(async scope =>
        {
            await scope.Fork(Effect<AppEnv>.Fail(new PrivateError()).TapErr(_ => childEnded.TrySetResult()));
            await childEnded.Task.ConfigureAwait(false);
            return 3;
        });
        // When
        var result = await request.RunAsync(effect, env);
        // Then
        Check(result is Ok<int>(3), "unobserved child error preserves successful root");
        Check(PrivateError.FormatCalls == 0, "diagnostic does not format private error value");
        accessor.HttpContext = null;
    }

    private static void MeasureEntry(WebApplication app, string output)
    {
        const int ITERATIONS = 100_000;
        var accessor = app.Services.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext();
        using var services = app.Services.CreateScope();
        var request = services.ServiceProvider.GetRequiredService<EffectRequest>();
        var env = services.ServiceProvider.GetRequiredService<AppEnv>();
        var effect = Effect<AppEnv>.Succeed(1);
        var rows = new List<object>();
        for (var round = 0; round < 4; round++)
        {
            if (round % 2 == 0) { Measure("direct", Direct); Measure("request", ThroughRequest); }
            else { Measure("request", ThroughRequest); Measure("direct", Direct); }
        }
        Measure("scope-and-direct", ScopedDirect);
        Measure("scope-and-request", ScopedRequest);
        File.WriteAllText(Path.Combine(output, "entry-measurement.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        accessor.HttpContext = null;

        void Direct() => _ = effect.RunExitAsync(env).GetAwaiter().GetResult();
        void ThroughRequest() => _ = request.RunAsync(effect, env).GetAwaiter().GetResult();
        void ScopedDirect()
        {
            using var scope = app.Services.CreateScope();
            var environment = scope.ServiceProvider.GetRequiredService<AppEnv>();
            _ = effect.RunExitAsync(environment).GetAwaiter().GetResult();
        }
        void ScopedRequest()
        {
            using var scope = app.Services.CreateScope();
            var environment = scope.ServiceProvider.GetRequiredService<AppEnv>();
            var entry = scope.ServiceProvider.GetRequiredService<EffectRequest>();
            _ = entry.RunAsync(effect, environment).GetAwaiter().GetResult();
        }
        void Measure(string path, Action action)
        {
            for (var index = 0; index < 10_000; index++) action();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            for (var index = 0; index < ITERATIONS; index++) action();
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(start);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            rows.Add(new { path, iterations = ITERATIONS, nanosecondsPerCall = elapsed.TotalNanoseconds / ITERATIONS, bytesPerCall = (double)allocated / ITERATIONS });
        }
    }

    private sealed record PrivateError
    {
        internal static int FormatCalls;
        public override string ToString()
        {
            Interlocked.Increment(ref FormatCalls);
            throw new InvalidOperationException("Error values must not be formatted.");
        }
    }

    private static async Task CheckActiveCancellation(WebApplication app)
    {
        // Given
        using var cancellation = new CancellationTokenSource();
        var accessor = app.Services.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext { RequestAborted = cancellation.Token };
        await using var services = app.Services.CreateAsyncScope();
        var request = services.ServiceProvider.GetRequiredService<EffectRequest>();
        var env = services.ServiceProvider.GetRequiredService<AppEnv>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = false;
        var resource = Resource.Make(Effect<AppEnv>.Succeed(Unit.Value), (_, _) => Effect<AppEnv>.Sync(() =>
        {
            released = true;
            return Unit.Value;
        }));
        var effect = resource.Use(_ => Effect<AppEnv>.Async<int>(async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            return 1;
        }));
        try
        {
            // When
            var pending = request.RunAsync(effect, env).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            throw new InvalidOperationException("Expected cancellation.");
        }
        catch (OperationCanceledException)
        {
            // Then
            Check(released, "active cancellation waits for resource cleanup");
        }
        finally { accessor.HttpContext = null; }
    }

    private static async Task CheckCancellationAndCleanup(WebApplication app)
    {
        // Given
        using var cancellation = new CancellationTokenSource();
        var context = new DefaultHttpContext { RequestAborted = cancellation.Token };
        var accessor = app.Services.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = context;
        await using var services = app.Services.CreateAsyncScope();
        var request = services.ServiceProvider.GetRequiredService<EffectRequest>();
        var env = services.ServiceProvider.GetRequiredService<AppEnv>();
        var releaseStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = new OperationCanceledException("cleanup failed");
        var releaseCount = 0;
        var resource = Resource.Make(Effect<AppEnv>.Succeed(Unit.Value), (_, _) =>
            Effect<AppEnv>.Async<Unit>(async token =>
            {
                Check(!token.CanBeCanceled, "cleanup token is not cancellable");
                releaseStarted.SetResult();
                await resume.Task.ConfigureAwait(false);
                Interlocked.Increment(ref releaseCount);
                throw original;
            }));
        // When
        var pending = request.RunAsync(resource.Use(_ => Effect<AppEnv>.Succeed(7)), env).AsTask();
        await releaseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        Check(!pending.IsCompleted, "request awaits cleanup after cancellation");
        resume.SetResult();
        try
        {
            await pending;
            throw new InvalidOperationException("Expected defect.");
        }
        catch (EffectHttpException error)
        {
            // Then
            Check(ReferenceEquals(original, error.InnerException), "cleanup Die(OCE) wins over later cancellation");
            Check(releaseCount == 1, "cleanup runs exactly once");
        }

        // Given
        // When
        try
        {
            await request.RunAsync(Effect<AppEnv>.Succeed(1), env);
            throw new InvalidOperationException("Expected cancellation.");
        }
        catch (OperationCanceledException error)
        {
            // Then
            Check(error.CancellationToken == cancellation.Token, "cancelled request propagates cancellation");
        }
        accessor.HttpContext = null;
    }

    private static async Task CheckLateDiagnostics(WebApplication app)
    {
        // Given
        var previous = EffectDiagnostics.UnobservedFailureHandler;
        var failures = new System.Collections.Concurrent.ConcurrentQueue<ExitCase>();
        EffectDiagnostics.UnobservedFailureHandler = failures.Enqueue;
        var accessor = app.Services.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext();
        var services = app.Services.CreateAsyncScope();
        var request = services.ServiceProvider.GetRequiredService<EffectRequest>();
        var env = services.ServiceProvider.GetRequiredService<AppEnv>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resource = Resource.Make(Effect<AppEnv>.Succeed(Unit.Value), (_, _) =>
            Scope<Never, AppEnv>.Gen(async scope =>
            {
                started.SetResult();
                await resume.Task.ConfigureAwait(false);
                var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await scope.Fork(Effect<AppEnv>.Fail("late").TapErr(_ => ended.TrySetResult()));
                await ended.Task.ConfigureAwait(false);
                return Unit.Value;
            }));
        try
        {
            // When
            var pending = request.RunAsync(resource.Use(_ => Effect<AppEnv>.Succeed(1)), env).AsTask();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await services.DisposeAsync();
            resume.SetResult();
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            // Then
            Check(result is Ok<int>(1), "late diagnostics do not change the result");
            Check(failures.Count == 1 && failures.Single().Value is Failed, "disposed request falls back to core diagnostics");
        }
        finally
        {
            resume.TrySetResult();
            EffectDiagnostics.UnobservedFailureHandler = previous;
            accessor.HttpContext = null;
            await services.DisposeAsync();
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"FAIL: {name}");
        Console.WriteLine($"PASS: {name}");
    }
}
