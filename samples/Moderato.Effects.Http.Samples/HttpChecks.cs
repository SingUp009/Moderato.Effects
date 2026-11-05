using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Moderato.Results;

namespace Moderato.Effects.Http.Samples;

internal static class HttpChecks
{
    internal static void MapEndpoints(WebApplication app)
    {
        app.MapGet("/http-sample/die", async (AppEnv env, EffectRequest request) =>
            await request.RunAsync(Effect<AppEnv>.Sync<int>(() => throw new OperationCanceledException("unexpected OCE")), env))
            .ProducesProblem(500).ExcludeFromDescription();
        app.MapGet("/http-sample/cancel-die/{kind}", async (string kind, HttpContext context, AppEnv env) =>
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            context.RequestAborted = cancellation.Token;
            var request = context.RequestServices.GetRequiredService<EffectRequest>();
            var resource = Resource.Make(Effect<AppEnv>.Succeed(Unit.Value), (_, _) => Effect<AppEnv>.Sync<Unit>(() =>
            {
                cancellation.Cancel();
                if (kind == "io") throw new IOException("unexpected IO failure");
                throw new OperationCanceledException(cancellation.Token);
            }));
            return await request.RunAsync(resource.Use(_ => Effect<AppEnv>.Succeed(1)), env);
        }).ProducesProblem(500).ExcludeFromDescription();
        app.MapGet("/http-sample/interrupted", async (AppEnv env, EffectRequest request) =>
        {
            Fiber<Unit, Never, AppEnv>? child = null;
            await Scope<Never, AppEnv>.Gen(async scope =>
            {
                child = await scope.Fork(Effect<AppEnv>.Async<Unit>(async token =>
                {
                    await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                    return Unit.Value;
                }));
                return Unit.Value;
            }).RunAsync(env);
            return await request.RunAsync(child!.Join(), env);
        }).ProducesProblem(500).ExcludeFromDescription();
        app.MapGet("/http-sample/started", async (HttpContext context, AppEnv env, EffectRequest request) =>
        {
            await context.Response.WriteAsync("started");
            await context.Response.Body.FlushAsync();
            await request.RunAsync(Effect<AppEnv>.Sync<int>(() => throw new IOException("after response start")), env);
        }).ExcludeFromDescription();
        app.MapGet("/http-sample/disconnect", async (AppEnv env, EffectRequest request, ObservedFailures observed) =>
        {
            var resource = Resource.Make(Effect<AppEnv>.Succeed(Unit.Value), (_, _) => Effect<AppEnv>.Sync(() =>
            {
                observed.DisconnectCleaned.TrySetResult();
                return Unit.Value;
            }));
            return await request.RunAsync(resource.Use(_ => Effect<AppEnv>.Async<int>(async token =>
            {
                observed.DisconnectEntered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return 1;
            })), env);
        }).ExcludeFromDescription();
    }

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
        Check((await created.Content.ReadFromJsonAsync<Order>()) is { Sku: "sample", Quantity: 2 }, "201 body matches Order");
        Check((await duplicate.Content.ReadFromJsonAsync<DuplicateOrder>()) is { Sku: "sample" }, "409 body matches DuplicateOrder");
        Check((await invalid.Content.ReadFromJsonAsync<ValidationError>()) is { Message.Length: > 0 }, "400 body matches ValidationError");

        // Given
        var observed = app.Services.GetRequiredService<ObservedFailures>();
        // When
        using var defect = await client.GetAsync("/http-sample/die");
        using var interrupted = await client.GetAsync("/http-sample/interrupted");
        using var canceledDefect = await client.GetAsync("/http-sample/cancel-die/oce");
        using var canceledIo = await client.GetAsync("/http-sample/cancel-die/io");
        // Then
        Check(defect.StatusCode == HttpStatusCode.InternalServerError, "Die reaches exception handler");
        Check(interrupted.StatusCode == HttpStatusCode.InternalServerError, "independent interruption reaches exception handler");
        Check(observed.Errors.Any(error => error is EffectHttpException { InnerException: OperationCanceledException }), "Die(OCE) preserved");
        Check(observed.Errors.Any(error => error is EffectHttpException boundary
            && boundary.Exit.Value is Interrupted && boundary.InnerException is null), "Interrupted classification preserved");
        Check(canceledDefect.StatusCode == HttpStatusCode.InternalServerError, "Die(OCE) reaches handler with request token cancelled");
        Check(canceledIo.StatusCode == HttpStatusCode.InternalServerError, "Die(IOException) reaches handler with request token cancelled");
        Check(!(await defect.Content.ReadAsStringAsync()).Contains("unexpected OCE", StringComparison.Ordinal), "no exception detail in response");

        // Given
        var output = Path.Combine(Environment.CurrentDirectory, "artifacts", "http-sample");
        Directory.CreateDirectory(output);
        // When
        var document = await client.GetStringAsync("/openapi/v1.json");
        var stable = JsonNode.Parse(document)!;
        stable["servers"] = JsonNode.Parse("[{\"url\":\"/\"}]");
        await File.WriteAllTextAsync(Path.Combine(output, "openapi.json"), stable.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        // Then
        using var json = JsonDocument.Parse(document);
        var operation = json.RootElement.GetProperty("paths").GetProperty("/orders").GetProperty("post");
        var responses = operation.GetProperty("responses");
        foreach (var (code, schema) in new[] { ("201", "Order"), ("400", "ValidationError"), ("409", "DuplicateOrder") })
        {
            var body = responses.GetProperty(code).GetProperty("content").GetProperty("application/json");
            Check(body.GetProperty("schema").GetProperty("$ref").GetString() == $"#/components/schemas/{schema}", $"OpenAPI {code} → {schema}");
        }
        Check(responses.TryGetProperty("500", out var problem), "explicit 500 metadata");
        Check(problem.GetProperty("content").GetProperty("application/problem+json").GetProperty("schema").GetProperty("$ref").GetString() == "#/components/schemas/ProblemDetails", "500 metadata matches ProblemDetails schema");
        Check(defect.Content.Headers.ContentType?.MediaType == "application/problem+json", "500 response matches declared problem media type");
        Check((await defect.Content.ReadFromJsonAsync<ProblemDetails>())?.Status == 500, "500 body matches ProblemDetails");
        Check(!operation.TryGetProperty("parameters", out var parameters) || parameters.GetArrayLength() == 0, "services are not HTTP parameters");
        await CheckAuthorization(client, json.RootElement);

        // Given
        var handledBefore = observed.Errors.Count;
        // When
        var transportFailed = false;
        try { await client.GetStringAsync("/http-sample/started"); }
        catch (HttpRequestException) { transportFailed = true; }
        var startedFailure = await observed.AfterResponseStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Then
        Check(transportFailed, "started response cannot be replaced with a complete 500 body");
        Check(startedFailure.InnerException is IOException && startedFailure.Exit.Value is Die, "started response still preserves defect classification");
        Check(observed.Errors.Count == handledBefore, "standard exception handler does not replace a started response");

        // Given
        using var disconnect = new CancellationTokenSource();
        // When
        var disconnected = client.GetAsync("/http-sample/disconnect", disconnect.Token);
        await observed.DisconnectEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        disconnect.Cancel();
        try { using var response = await disconnected; }
        catch (OperationCanceledException) { }
        await observed.DisconnectCleaned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Then
        Check(observed.DisconnectCleaned.Task.IsCompletedSuccessfully, "real client disconnection finishes effect cleanup");
        Console.WriteLine("PASS: HTTP and OpenAPI checks complete");
    }

    private static async Task CheckAuthorization(HttpClient client, JsonElement document)
    {
        // Given
        using var aliceRequest = new HttpRequestMessage(HttpMethod.Get, "/authorized-orders/1");
        aliceRequest.Headers.Add(SampleAuthentication.HEADER_NAME, "alice");
        using var bobRequest = new HttpRequestMessage(HttpMethod.Get, "/authorized-orders/1");
        bobRequest.Headers.Add(SampleAuthentication.HEADER_NAME, "bob");
        using var missingRequest = new HttpRequestMessage(HttpMethod.Get, "/authorized-orders/99");
        missingRequest.Headers.Add(SampleAuthentication.HEADER_NAME, "alice");
        using var invalidRequest = new HttpRequestMessage(HttpMethod.Get, "/authorized-orders/1");
        invalidRequest.Headers.Add(SampleAuthentication.HEADER_NAME, "unknown");
        // When
        using var anonymous = await client.GetAsync("/authorized-orders/1");
        using var alice = await client.SendAsync(aliceRequest);
        using var bob = await client.SendAsync(bobRequest);
        using var missing = await client.SendAsync(missingRequest);
        using var invalid = await client.SendAsync(invalidRequest);
        // Then
        Check(anonymous.StatusCode == HttpStatusCode.Unauthorized, "anonymous request is challenged with 401");
        Check(invalid.StatusCode == HttpStatusCode.Unauthorized, "invalid sample identity is challenged with 401");
        Check(alice.StatusCode == HttpStatusCode.OK, "authorized owner receives 200");
        Check((await alice.Content.ReadFromJsonAsync<Order>())?.Sku == "protected-sample", "authorized response contains the order");
        Check(bob.StatusCode == HttpStatusCode.Forbidden, "business access denial returns 403");
        Check(bob.Content.Headers.ContentType?.MediaType == "application/json", "business denial uses declared JSON media type");
        Check((await bob.Content.ReadFromJsonAsync<AccessDenied>())?.Reason == "Access denied.", "business denial matches its body schema");
        Check(missing.StatusCode == HttpStatusCode.NotFound, "missing order returns 404");

        var operation = document.GetProperty("paths").GetProperty("/authorized-orders/{id}").GetProperty("get");
        var responses = operation.GetProperty("responses");
        foreach (var code in new[] { "200", "401", "403", "404", "500" })
            Check(responses.TryGetProperty(code, out _), $"authorized OpenAPI includes {code}");
        foreach (var (code, name) in new[] { ("200", "Order"), ("403", "AccessDenied"), ("404", "OrderNotFound") })
        {
            var schema = responses.GetProperty(code).GetProperty("content").GetProperty("application/json").GetProperty("schema");
            Check(schema.GetProperty("$ref").GetString() == $"#/components/schemas/{name}", $"authorized OpenAPI {code} body matches {name}");
        }
        var scheme = document.GetProperty("components").GetProperty("securitySchemes").GetProperty(SampleAuthentication.SCHEME_NAME);
        Check(scheme.GetProperty("type").GetString() == "apiKey" && scheme.GetProperty("name").GetString() == SampleAuthentication.HEADER_NAME,
            "OpenAPI describes the sample authentication header");
        Check(operation.GetProperty("security")[0].TryGetProperty(SampleAuthentication.SCHEME_NAME, out _), "only protected operation declares authentication");
        Check(!document.GetProperty("paths").GetProperty("/orders").GetProperty("post").TryGetProperty("security", out _),
            "public order creation is not documented as protected");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"FAIL: {name}");
        Console.WriteLine($"PASS: {name}");
    }
}
