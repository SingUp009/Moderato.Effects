---
title: HTTP
---

# HTTP

Install `Moderato.Effects.Http`. It brings Core, its generator and analyzers as transitive dependencies. See [Installation](/installation/) for package setup.

Use ordinary ASP.NET Core routes. Inject `EffectRequest`, run the effect, and turn its typed result into a response.

## Map an endpoint

This handler is taken from the [runnable Orders sample](https://github.com/SingUp009/Moderato.Effects/tree/main/samples/Moderato.Effects.Http.Samples). `Orders<AppEnv>.Create` is application code returning `Effect<Order, CreateOrderError, AppEnv>`; its error union contains `ValidationError` and `DuplicateOrder`.

```csharp
using Moderato.Effects.Http;
using Moderato.Results;
using HttpResults = Microsoft.AspNetCore.Http.HttpResults;

static async Task<HttpResults.Results<
    HttpResults.Created<Order>,
    HttpResults.BadRequest<ValidationError>,
    HttpResults.Conflict<DuplicateOrder>>>
CreateOrder(CreateOrderRequest command, AppEnv env, EffectRequest request)
{
    var result = await request.RunAsync(Orders<AppEnv>.Create(command), env);
    return result switch
    {
        Ok<Order>(var order) => TypedResults.Created($"/orders/{order.Id}", order),
        Err<CreateOrderError>(var error) => error switch
        {
            ValidationError e => TypedResults.BadRequest(e),
            DuplicateOrder e => TypedResults.Conflict(e),
        },
    };
}
```

In the Web application's composition root, register the sample's environment and store:

```csharp
builder.Services.AddEffectsHttp();
builder.Services.AddScoped<AppEnv>();
builder.Services.AddSingleton<OrderStore>();
builder.Services.AddOpenApi();

// After builder.Build():
app.UseExceptionHandler(errors => errors.Run(context =>
    TypedResults.Problem(statusCode: 500, title: "The request failed.")
        .ExecuteAsync(context)));
app.MapOpenApi();
app.MapPost("/orders", CreateOrder).ProducesProblem(500);
```

`AddOpenApi` requires `Microsoft.AspNetCore.OpenApi`. The sample supplies the complete project, model, store and environment. Run it from the repository root:

```sh
dotnet run --project samples/Moderato.Effects.Http.Samples --urls http://localhost:5000
```

Then, in another terminal:

```sh
curl -i http://localhost:5000/orders \
  -H 'Content-Type: application/json' \
  -d '{"sku":"coffee","quantity":1}'
curl http://localhost:5000/openapi/v1.json
```

The first create returns 201; repeating the SKU returns 409. Invalid input returns 400.

## OpenAPI and exception handling

`Results<Created<Order>, BadRequest<ValidationError>, Conflict<DuplicateOrder>>` is ASP.NET Core's typed response union. ASP.NET Core reads this endpoint metadata to describe the response types in OpenAPI. The effect's error union is mapped explicitly at the endpoint; the integration does not infer HTTP status codes from business errors.

`.ProducesProblem(500)` adds documentation for a possible response. It does not install an exception handler or produce a response. `UseExceptionHandler` above writes the generic 500 Problem response when an exception reaches it. If the response has already started, the exception handler cannot replace it with a new 500 response. Keep internal exception details out of that response.

## Cancellation and defects

`EffectRequest.RunAsync` uses the current request's `RequestAborted` token and awaits the effect, its managed children and cleanup before returning.

| Effect result | HTTP boundary |
|---|---|
| Ok / Err | Returns `Result<T, E>` for the endpoint to map |
| Die | Throws `EffectHttpException`, retaining the `Die` in `Exit` |
| Interrupted, request token canceled when observed | Throws `OperationCanceledException` with the request token |
| Interrupted, request token not canceled when observed | Throws `EffectHttpException`, retaining `Interrupted` in `Exit` |

A Die containing an `OperationCanceledException` is still a defect and stays wrapped. You can inspect `EffectHttpException.Exit` in a shared exception handler. The wrapper does not choose a status code. Cancellation does not promise an HTTP response to a disconnected client, and cleanup cannot force non-cooperative work to stop.

## Authorization

Use ASP.NET Core authentication and authorization as usual:

```csharp
app.UseAuthentication();
app.UseAuthorization();
app.MapPost("/orders", CreateOrder).RequireAuthorization("orders.write");
```

Register your authentication scheme and the `orders.write` policy before building the app. The runnable sample includes an authorized route and OpenAPI security metadata. Its demonstration credentials are sample code, not an authentication scheme for deployment.

## Lifetimes and diagnostics

`EffectRequest` is scoped to the request. Keep the environment and its dependencies alive through execution and cleanup; do not start detached work using request-owned services. A DbContext shared by parallel effects still needs EF's concurrency rules.

`AddEffectsHttp()` connects unobserved failures to standard logging. Configure ILogger providers in application code. HTTP diagnostics disconnect on request/Host disposal; late reports fall back to the process-wide handler or Console.Error. This does not replace your middleware's reporting of the primary request failure.

Streaming integration is deferred. See [Caveats](/caveats/) for validation limits.
