# HTTP integration

Unreleased preview API. Validated SDK/runtime versions and remaining acceptance work are recorded in [HTTP validation](https://github.com/SingUp009/Moderato.Effects/blob/main/docs/http-validation.md). Revalidation on .NET 11 GA is still required.

Install `Moderato.Effects.Http`, register `builder.Services.AddEffectsHttp()`, and register your environment and its request-owned services as scoped services. Inject `EffectRequest` alongside your environment in a standard `MapGet`, `MapPost` or `MapGroup` handler.

```csharp
static async Task<Microsoft.AspNetCore.Http.HttpResults.Results<
    Microsoft.AspNetCore.Http.HttpResults.Created<Order>,
    Microsoft.AspNetCore.Http.HttpResults.BadRequest<ValidationError>,
    Microsoft.AspNetCore.Http.HttpResults.Conflict<DuplicateOrder>>>
CreateOrder(CreateOrderRequest command, AppEnv env, EffectRequest request)
{
    var result = await request.RunAsync(Orders<AppEnv>.Create(command), env);
    return result switch
    {
        Moderato.Results.Ok<Order>(var order) => TypedResults.Created($"/orders/{order.Id}", order),
        Moderato.Results.Err<CreateOrderError>(var error) => error switch
        {
            ValidationError invalid => TypedResults.BadRequest(invalid),
            DuplicateOrder duplicate => TypedResults.Conflict(duplicate),
        },
    };
}
```

The complete [HTTP sample](https://github.com/SingUp009/Moderato.Effects/tree/main/samples/Moderato.Effects.Http.Samples) defines the types, services, authentication and OpenAPI transformers. Run it with `--verify` to compare generated metadata against real HTTP responses. Its trusted header authentication is for local demonstration only; use a real authentication scheme in production.

`RunAsync` reads the current `HttpContext.RequestAborted` at execution entry, including middleware replacements after DI resolution. It awaits the effect, its structured children and cleanup. `Ok` and `Err` reach your explicit result mapping. A later cancellation during cleanup does not replace a settled result.

Defects throw `EffectHttpException`, even when the original exception is `OperationCanceledException` or `IOException` during request cancellation. `InnerException` preserves the original exception; `Exit.Value` is the original `Die`, including ordered suppressed failures, their relations and the omitted count. An `Interrupted` result checks the captured request token once after completion. If canceled, it throws cancellation; otherwise it throws `EffectHttpException` with an `Interrupted` exit and no inner exception. This observation cannot identify the physical cause of an interruption.

Configure standard `UseExceptionHandler` yourself. `ProducesProblem(500)` declares metadata and does not install middleware. Avoid exposing exception messages or arbitrary error values in response bodies. The adapter does not duplicate the root defect log that the standard handler owns. Once a response has started, that middleware cannot replace it with a Problem Details response. Client disconnection does not guarantee delivery of any replacement response.

Unobserved child failures use the standard `ILogger` category `Moderato.Effects.Http`. Unjoined typed errors log at Warning with event 1002 and an error type name; defects log at Error with event 1001 and retained/omitted counts. Suppressed entries use event 1003 with index, relation and error type or exception. Error values are not formatted. Configure levels and providers through ordinary logging. Diagnostic failures do not change execution results. Request disposal disconnects the request; Host disposal disconnects the singleton logger connection. Late reports fall back to Core's global handler or `Console.Error` without retaining the environment, request body or `HttpContext` for diagnostics. A logger callback in progress can delay disposal; the adapter adds no shutdown deadline.

Use standard `RequireAuthorization`. Authorization refusals and application-selected statuses may need explicit metadata: the sample adds 401 and security requirements on a protected group and declares its JSON 403 with `Produces<AccessDenied>(403)`. Check the actual authentication scheme's challenge/forbid behavior. `Produces` declarations are not compiler checked against runtime responses. Union exhaustiveness produces a compiler warning (CS8509); enable `TreatWarningsAsErrors` or elevate that diagnostic if missing a business case must fail your build. Returning a response outside the declared typed result is a compiler error (CS0029). `Results<T1, ..., T6>` accepts up to six response types, which can represent more than six business error cases. For additional response types, use `IResult` and explicit `Produces` metadata.

The first preview supports ordinary values. It does not manage stream, SSE or asynchronous sequence lifetimes during body transmission. There is no custom router, automatic status selection, retry policy or automatic Tracker enablement. Generators and analyzers arrive transitively through the Core dependency; the package itself depends on Core and the ASP.NET Core shared framework, without Hosting or OpenAPI package dependencies.
